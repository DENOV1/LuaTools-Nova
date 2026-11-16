// LuaTools Nova — the local HTTP backend on 127.0.0.1:6767 serving the store-plugin's RPC
// contract (route-for-route port of the MIT original's HttpServerService). The endpoints drive
// headless add flows through the same DownloadQueue the app UI uses.
using System.IO;
using System.Net;
using System.Text;
using System.Text.Json;
using LuaTools.Core.Downloads;
using LuaTools.Core.Persistence;
using LuaTools.Core.Services;
using LuaTools.Infrastructure.Clients;
using LuaTools.Infrastructure.Downloads;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;

namespace LuaTools.Integrations;

public sealed class HttpServerService : IHostedService
{
    private readonly LuaInstaller _installer;
    private readonly CacheService _cache;
    private readonly IProcessControl _steam;
    private readonly ISystemIntegrations _integrations;
    private readonly INavigationHost _navigation;
    private readonly DownloadQueue _queue;
    private readonly ManifestJobFactory _jobs;
    private readonly LuaToolsApiClient _api;
    private readonly SteamAppListCache _appList;
    private readonly Func<IApplicationAddFacade> _addFacade;
    private readonly ILogger<HttpServerService> _log;
    private HttpListener? _listener;
    private CancellationTokenSource? _appCts;
    private readonly Dictionary<long, DownloadItem> _downloads = new();

    public HttpServerService(LuaInstaller installer, CacheService cache, IProcessControl steam,
        ISystemIntegrations integrations, INavigationHost navigation, DownloadQueue queue,
        ManifestJobFactory jobs, LuaToolsApiClient api, SteamAppListCache appList,
        Func<IApplicationAddFacade> addFacade, ILogger<HttpServerService> logger)
    {
        _installer = installer;
        _cache = cache;
        _steam = steam;
        _integrations = integrations;
        _navigation = navigation;
        _queue = queue;
        _jobs = jobs;
        _api = api;
        _appList = appList;
        _addFacade = addFacade;
        _log = logger;
    }

    public Task StartAsync(CancellationToken cancellationToken)
    {
        _appCts = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        _listener = new HttpListener();
        _listener.Prefixes.Add("http://127.0.0.1:6767/");
        try
        {
            _listener.Start();
        }
        catch (Exception ex)
        {
            _log.LogWarning("Failed to start HTTP server on :6767: {Message}", ex.Message);
            return Task.CompletedTask;
        }

        _log.LogInformation("HTTP server listening on http://127.0.0.1:6767");
        _ = Task.Run(() => ListenLoop(_appCts.Token), _appCts.Token);
        return Task.CompletedTask;
    }

    public Task StopAsync(CancellationToken cancellationToken)
    {
        _appCts?.Cancel();
        try { _listener?.Stop(); } catch { }
        return Task.CompletedTask;
    }

    private async Task ListenLoop(CancellationToken ct)
    {
        while (!ct.IsCancellationRequested && _listener?.IsListening == true)
        {
            try
            {
                var ctx = await _listener.GetContextAsync().WaitAsync(ct);
                _ = Task.Run(() => HandleRequest(ctx), ct);
            }
            catch (OperationCanceledException) { break; }
            catch (Exception) { break; }
        }
    }

    private async Task HandleRequest(HttpListenerContext ctx)
    {
        var req = ctx.Request;
        var resp = ctx.Response;

        resp.AddHeader("Access-Control-Allow-Origin", "*");
        resp.AddHeader("Access-Control-Allow-Methods", "GET, POST, OPTIONS");
        resp.AddHeader("Access-Control-Allow-Headers", "Content-Type");
        resp.ContentType = "application/json; charset=utf-8";

        try
        {
            string? path = req.Url?.AbsolutePath.TrimEnd('/');
            (int status, string body) = await RouteAsync(path, req);
            resp.StatusCode = status;
            var bytes = Encoding.UTF8.GetBytes(body);
            await resp.OutputStream.WriteAsync(bytes);
        }
        catch (Exception ex)
        {
            resp.StatusCode = 500;
            var body = Encoding.UTF8.GetBytes(Json(JsonErr(ex.Message)));
            await resp.OutputStream.WriteAsync(body);
        }
        finally
        {
            resp.Close();
        }
    }

    private async Task<(int, string)> RouteAsync(string? path, HttpListenerRequest req)
    {
        if (req.HttpMethod == "OPTIONS") return (204, "");

        string body = "";
        if (req.HasEntityBody)
        {
            using var reader = new StreamReader(req.InputStream, req.ContentEncoding);
            body = await reader.ReadToEndAsync();
        }

        path ??= "";
        return path switch
        {
            var p when Match(p, "/has/{appid}", out var id) => HandleHas(long.Parse(id)),
            var p when Match(p, "/add/{appid}", out var id) => HandleAdd(long.Parse(id)),
            var p when Match(p, "/add-status/{appid}", out var id) => HandleAddStatus(long.Parse(id)),
            var p when Match(p, "/add-source/{appid}", out var id) => HandleAddSource(long.Parse(id), body),
            var p when Match(p, "/check-sources/{appid}", out var id) => await HandleCheckSources(long.Parse(id)),
            var p when Match(p, "/download/{appid}", out var id) => HandleDownload(long.Parse(id), body),
            var p when Match(p, "/download-status/{appid}", out var id) => HandleStatus(long.Parse(id)),
            var p when Match(p, "/cancel/{appid}", out var id) => HandleCancel(long.Parse(id)),
            var p when Match(p, "/remove/{appid}", out var id) => HandleRemove(long.Parse(id)),
            var p when Match(p, "/open/fix/{appid}", out var id) => HandleOpenFix(long.Parse(id)),
            "/open/settings" => HandleOpenSettings(),
            "/open-url" => HandleOpenUrl(body),
            "/restart-steam" => HandleRestartSteam(),
            "/check-updates" => (200, Json(new { success = true })),
            "/loaded-apps" when req.HttpMethod == "GET" => HandleReadLoadedApps(),
            "/loaded-apps" => HandleDismissLoadedApps(),
            "/api-list" => (200, Json(new { success = true, apis = Array.Empty<object>() })),
            "/icon" => HandleIcon(),
            _ => (404, Json(JsonErr("Not found"))),
        };
    }

    private static bool Match(string? path, string pattern, out string id)
    {
        id = "";
        if (path is null) return false;
        var parts = pattern.TrimEnd('/').Split('/');
        var pathParts = path.Split('/');
        if (parts.Length != pathParts.Length) return false;
        for (int i = 0; i < parts.Length; i++)
        {
            if (parts[i].StartsWith('{')) { id = pathParts[i]; continue; }
            if (!string.Equals(parts[i], pathParts[i], StringComparison.OrdinalIgnoreCase)) return false;
        }
        return !string.IsNullOrEmpty(id);
    }

    private (int, string) HandleHas(long appId)
    {
        var exists = _installer.ReadInstalledLua(appId) != null;
        return (200, Json(new { success = true, exists }));
    }

    private (int, string) HandleAdd(long appId)
    {
        PluginLog.Log($"/add/{appId} started");
        _ = Task.Run(async () => await _addFacade().StartPluginAddAsync(appId));
        return (200, Json(new { success = true }));
    }

    private (int, string) HandleAddSource(long appId, string body)
    {
        string source = "";
        try
        {
            using var json = JsonDocument.Parse(body);
            if (json.RootElement.ValueKind == JsonValueKind.Object && json.RootElement.TryGetProperty("source", out var s))
                source = s.GetString() ?? "";
        }
        catch { }
        if (string.IsNullOrWhiteSpace(source)) return (400, Json(JsonErr("source is required")));
        _ = Task.Run(async () => await _addFacade().DownloadSourceByNameAsync(source));
        return (200, Json(new { success = true }));
    }

    private (int, string) HandleAddStatus(long appId)
    {
        bool installed = _installer.ReadInstalledLua(appId) != null;
        var st = _addFacade().GetAddState(appId);
        if (st is null)
            return (200, Json(new { success = true, checking = false, sourcesLoaded = false,
                sources = Array.Empty<object>(), installed }));

        var camelOpts = new JsonSerializerOptions { PropertyNamingPolicy = JsonPropertyNamingPolicy.CamelCase };
        var sources = st.Sources.Select(s => (object)new
        {
            name = s.Name,
            displayName = s.DisplayName,
            status = s.Status,
            available = s.Available,
            canDownload = s.CanDownload,
            locked = s.Locked,
            needsKey = s.NeedsKey,
            stats = s.Stats,
            downloading = s.Downloading,
            progress = s.Progress,
            indeterminate = s.Indeterminate,
        }).ToList();

        return (200, JsonSerializer.Serialize(new
        {
            success = true,
            appid = st.AppId,
            checking = st.Checking,
            fastFetch = st.FastFetch,
            sourcesLoaded = st.SourcesLoaded,
            sources,
            installStatus = st.InstallStatus,
            installFailed = st.InstallFailed,
            error = st.Error,
            installed,
        }, camelOpts));
    }

    private async Task<(int, string)> HandleCheckSources(long appId)
    {
        try
        {
            var statuses = await _api.CheckSourcesAsync(appId.ToString());
            var results = statuses
                .Select(kv => (object)new { name = kv.Key, available = kv.Value == "available", url = (string?)null })
                .ToList();
            return (200, Json(new { success = true, results }));
        }
        catch (Exception ex)
        {
            return (200, Json(new { success = false, error = ex.Message, results = Array.Empty<object>() }));
        }
    }

    private (int, string) HandleDownload(long appId, string body)
    {
        string source = "";
        try
        {
            using var json = JsonDocument.Parse(body);
            source = json.RootElement.TryGetProperty("source", out var s) ? s.GetString() ?? ""
                : json.RootElement.TryGetProperty("apiName", out var a) ? a.GetString() ?? "" : "";
        }
        catch { }
        if (string.IsNullOrWhiteSpace(source)) return (400, Json(JsonErr("source is required")));

        if (_queue.FindActive($"manifest:{appId}") is not null)
            return (409, Json(JsonErr("Download already in progress for this app")));

        _downloads[appId] = _queue.Enqueue(_jobs.CreateManifestJob(appId, null, source, false));
        return (200, Json(new { success = true }));
    }

    private (int, string) HandleStatus(long appId)
    {
        if (!_downloads.TryGetValue(appId, out var item))
            return (200, Json(new { success = true, state = (object?)null }));

        bool done = item.Status == DownloadStatus.Completed;
        long bytesRead = item.BytesRead;
        long totalBytes = item.TotalBytes is > 0 ? item.TotalBytes.Value : 100;

        var payload = new
        {
            status = item.Status switch
            {
                DownloadStatus.Queued => "queued",
                DownloadStatus.Downloading => "downloading",
                DownloadStatus.AwaitingConfirmation => "processing",
                DownloadStatus.Installing => "processing",
                DownloadStatus.Completed => "done",
                DownloadStatus.Cancelled => "cancelled",
                _ => "failed",
            },
            bytesRead,
            totalBytes,
            currentApi = item.SubTitle,
            apiErrors = (object?)null,
            error = item.Status is DownloadStatus.Failed or DownloadStatus.Cancelled ? item.Message : null,
            installedPath = (string?)null,
            success = done,
            api = done ? item.SubTitle : null,
        };
        return (200, Json(new { success = true, state = payload }));
    }

    private (int, string) HandleCancel(long appId)
    {
        var item = _queue.FindActive($"manifest:{appId}");
        if (item is null) return (200, Json(new { success = true, message = "Nothing to cancel" }));
        _queue.Cancel(item);
        return (200, Json(new { success = true }));
    }

    private (int, string) HandleRemove(long appId)
    {
        try
        {
            _cache.RemoveLoadedAppId(appId);
            var path = _installer.ReadInstalledLua(appId);
            if (path is not null)
            {
                File.Delete(path);
                var disabled = Path.Combine(Path.GetDirectoryName(path)!, $"{appId}.lua.disabled");
                if (File.Exists(disabled)) File.Delete(disabled);
                return (200, Json(new { success = true, deleted = new[] { path }, count = 1 }));
            }
            return (200, Json(new { success = true, deleted = Array.Empty<string>(), count = 0 }));
        }
        catch (Exception ex)
        {
            return (500, Json(JsonErr(ex.Message)));
        }
    }

    private (int, string) HandleOpenFix(long appId)
    {
        UiBridge.Post(() =>
        {
            _navigation.NavigateTo("fixes");
            _ = Task.Run(() => _addFacade().OpenForAppIdAsync(appId));
        });
        return (200, Json(new { success = true }));
    }

    private (int, string) HandleOpenSettings()
    {
        UiBridge.Post(() => _navigation.NavigateTo("settings"));
        return (200, Json(new { success = true }));
    }

    private (int, string) HandleRestartSteam()
    {
        var ok = _steam.RestartSteam();
        return (200, Json(new { success = ok, error = ok ? (string?)null : "Failed to restart Steam" }));
    }

    private (int, string) HandleOpenUrl(string body)
    {
        string url = "";
        try
        {
            using var json = JsonDocument.Parse(body);
            if (json.RootElement.ValueKind == JsonValueKind.Object && json.RootElement.TryGetProperty("url", out var u))
                url = u.GetString() ?? "";
        }
        catch { }

        if (!url.StartsWith("http://", StringComparison.OrdinalIgnoreCase)
            && !url.StartsWith("https://", StringComparison.OrdinalIgnoreCase))
            return (400, Json(JsonErr("Invalid URL")));

        try
        {
            _integrations.OpenUrl(url);
            return (200, Json(new { success = true }));
        }
        catch (Exception ex)
        {
            return (500, Json(JsonErr(ex.Message)));
        }
    }

    private (int, string) HandleReadLoadedApps()
    {
        var ids = _cache.GetLoadedAppIds();
        var apps = ids.Select(id => new { appid = id, name = _appList.GetName(id) }).ToList();
        return (200, Json(new { success = true, apps }));
    }

    private (int, string) HandleDismissLoadedApps()
    {
        _cache.ClearLoadedAppIds();
        return (200, Json(new { success = true }));
    }

    private (int, string) HandleIcon()
    {
        try
        {
            var iconPath = ToolPaths.AppDir("luatools-nova.png");
            if (!File.Exists(iconPath)) return (200, Json(new { success = false, dataUrl = "" }));
            var bytes = File.ReadAllBytes(iconPath);
            return (200, Json(new { success = true, dataUrl = $"data:image/png;base64,{Convert.ToBase64String(bytes)}" }));
        }
        catch
        {
            return (200, Json(new { success = false, dataUrl = "" }));
        }
    }

    private static string Json(object obj) => JsonSerializer.Serialize(obj);
    private static string JsonErr(string msg) => JsonSerializer.Serialize(new { success = false, error = msg });

    /// <summary>Wired by the UI host to marshal navigation onto the Avalonia thread.</summary>
    public static class UiBridge
    {
        public static Action<Action>? Post { get; set; }
    }
}
