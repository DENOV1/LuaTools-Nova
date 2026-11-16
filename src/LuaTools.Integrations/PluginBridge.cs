// LuaTools Nova — Integrations layer.
// 1. PluginInstallerService: installs the store-plugin FRONTEND (madoiscool/LTSP releases,
//    same upstream the Windows app uses) into Nova's XDG plugin dir; manages status/updates.
//    The Windows winmm.dll loader does not exist on Linux — the Linux path for store-page
//    injection is Steam's own CEF remote-debugging bridge (CefInjectorService), honestly surfaced.
// 2. CefInjectorService: connects to Steam's CDP (127.0.0.1:8080), injects the polyfill +
//    luatools.js into store pages, and drains the pending RPC queue — same wire protocol as
//    the original app (pure .NET, platform-neutral).
using System.Diagnostics;
using System.IO.Compression;
using System.Net;
using System.Net.Sockets;
using System.Net.WebSockets;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;
using LuaTools.Core.AppInfo;
using LuaTools.Core.Downloads;
using LuaTools.Core.Models;
using LuaTools.Core.Services;
using LuaTools.Infrastructure.Downloads;
using LuaTools.Infrastructure.Network;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;

namespace LuaTools.Integrations;

public sealed record PluginStatus(
    bool FrontendInstalled,
    bool BridgeReady,
    bool BridgeMatches,
    string? InstalledTag,
    string? LatestTag,
    bool UpdateAvailable,
    bool Offline,
    bool CdpUnavailable);

public sealed class PluginInstallerService
{
    private readonly GithubProxy _gh;
    private static readonly JsonSerializerOptions JsonOpts = new() { PropertyNameCaseInsensitive = true };
    private const string PluginZipAsset = "plugin.zip";
    private static string FrontendDir => ToolPaths.DataDir("plugin");
    private static string LuatoolsJsPath => Path.Combine(FrontendDir, "public", "luatools.js");
    private static string ManifestPath => Path.Combine(FrontendDir, "installed.json");
    private GithubRelease? _cachedLatest;

    public PluginInstallerService(GithubProxy gh)
    {
        _gh = gh;
    }

    private sealed class Manifest
    {
        public string? Tag { get; set; }
        public string? JsSha { get; set; }
    }

    private static Manifest? ReadManifest()
    {
        try
        {
            return File.Exists(ManifestPath)
                ? JsonSerializer.Deserialize<Manifest>(File.ReadAllText(ManifestPath), JsonOpts)
                : null;
        }
        catch { return null; }
    }

    private static void WriteManifest(Manifest m)
    {
        Directory.CreateDirectory(FrontendDir);
        File.WriteAllText(ManifestPath, JsonSerializer.Serialize(m));
    }

    public async Task<GithubRelease?> FetchLatestAsync(bool force, CancellationToken ct = default)
    {
        if (!force && _cachedLatest is not null) return _cachedLatest;
        string url = $"https://api.github.com/repos/{AppConfig.PluginReleasesOwner}/{AppConfig.PluginReleasesRepo}/releases/latest";
        try
        {
            using var res = await _gh.SendAsync(url, ct);
            if (res is null || !res.IsSuccessStatusCode) return null;
            var rel = JsonSerializer.Deserialize<GithubRelease>(await res.Content.ReadAsStringAsync(ct), JsonOpts);
            if (rel is not null) _cachedLatest = rel;
            return rel;
        }
        catch { return null; }
    }

    public bool IsInstalledLocally() => File.Exists(LuatoolsJsPath);

    public bool IsCdpReachable()
    {
        try
        {
            using var client = new TcpClient();
            var task = client.ConnectAsync(IPAddress.Loopback, 8080);
            return task.Wait(500) && client.Connected;
        }
        catch { return false; }
    }

    public async Task<PluginStatus> GetStatusAsync(bool force = false, CancellationToken ct = default)
    {
        bool frontend = File.Exists(LuatoolsJsPath);
        bool bridge = IsCdpReachable();
        var manifest = ReadManifest();
        var latest = await FetchLatestAsync(force, ct);

        if (latest is null)
            return new PluginStatus(frontend, bridge, false, manifest?.Tag, null, false, Offline: true, !bridge);

        bool jsMatches = manifest?.JsSha is not null
            && frontend
            && AssetHash.OfFile(LuatoolsJsPath).Equals(manifest.JsSha, StringComparison.OrdinalIgnoreCase);
        bool updateAvailable = frontend && manifest?.Tag != latest.TagName;

        return new PluginStatus(frontend, bridge, jsMatches, manifest?.Tag, latest.TagName,
            updateAvailable, Offline: false, CdpUnavailable: !bridge);
    }

    public async Task<(bool ok, string? error)> InstallAsync(IProgress<double?>? progress, CancellationToken ct = default)
    {
        var latest = await FetchLatestAsync(force: true, ct);
        if (latest is null) return (false, "Couldn't reach GitHub to fetch the latest release.");

        var zipAsset = latest.Assets.FirstOrDefault(a => a.Name.Equals(PluginZipAsset, StringComparison.OrdinalIgnoreCase));
        if (zipAsset is null)
            return (false, $"Release {latest.TagName} is missing {PluginZipAsset}.");

        string tmp = Path.Combine(Path.GetTempPath(), "luatools-nova-plugin-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(tmp);
        try
        {
            string zipPath = Path.Combine(tmp, PluginZipAsset);
            await _gh.DownloadAsync(zipAsset.DownloadUrl, zipPath, progress, ct);

            if (AssetHash.ParseDigest(zipAsset.Digest) is { } zd
                && AssetHash.OfFile(zipPath) != zd)
                return (false, $"{PluginZipAsset} failed hash verification.");

            if (Directory.Exists(FrontendDir)) Directory.Delete(FrontendDir, recursive: true);
            Directory.CreateDirectory(FrontendDir);
            ZipFile.ExtractToDirectory(zipPath, FrontendDir);

            // plugin.zip may wrap everything in a single top-level folder; hoist public/ up.
            NormalizeFrontendLayout();
            if (!File.Exists(LuatoolsJsPath))
                return (false, "plugin.zip did not contain public/luatools.js.");

            // The digest we pin is of the JS entry, so the status check can detect drift.
            WriteManifest(new Manifest { Tag = latest.TagName, JsSha = AssetHash.OfFile(LuatoolsJsPath) });

            await CefInjectorServiceHolder.ReloadAsync();
            return (true, null);
        }
        catch (Exception ex) { return (false, ex.Message); }
        finally { try { Directory.Delete(tmp, recursive: true); } catch { } }
    }

    private static void NormalizeFrontendLayout()
    {
        if (Directory.Exists(Path.Combine(FrontendDir, "public"))) return;
        foreach (var sub in Directory.GetDirectories(FrontendDir))
        {
            if (!Directory.Exists(Path.Combine(sub, "public"))) continue;
            foreach (var entry in Directory.GetFileSystemEntries(sub))
            {
                string dest = Path.Combine(FrontendDir, Path.GetFileName(entry));
                if (Directory.Exists(entry)) Directory.Move(entry, dest);
                else File.Move(entry, dest, overwrite: true);
            }
            try { Directory.Delete(sub, recursive: true); } catch { }
            return;
        }
    }

    public Task<(bool ok, string? error)> UninstallAsync()
    {
        return Task.Run(() =>
        {
            try
            {
                if (Directory.Exists(FrontendDir)) Directory.Delete(FrontendDir, recursive: true);
                _ = CefInjectorServiceHolder.ReloadAsync();
                return (true, (string?)null);
            }
            catch (Exception ex) { return (false, (string?)ex.Message); }
        });
    }
}

/// <summary>Static hand-off so the installer can refresh the running injector from any thread.</summary>
public static class CefInjectorServiceHolder
{
    public static CefInjectorService? Instance { get; set; }
    public static Task ReloadAsync() => Instance?.ReloadPluginFilesAsync() ?? Task.CompletedTask;
}

/// <summary>
/// Steam CEF bridge: discovers store-page tabs over http://127.0.0.1:8080/json, injects the
/// Millennium polyfill + luatools.js (fetched by the plugin installer), and drains the page's
/// pending RPC calls to the local HTTP backend. Same protocol as the Windows app.
/// </summary>
public sealed class CefInjectorService : IHostedService
{
    private readonly ILogger<CefInjectorService> _log;
    private CancellationTokenSource? _cts;
    private string _luatoolsJs = "";
    private string _polyfillJs = "";
    private readonly HttpClient _http = AppHttp.Create(TimeSpan.FromSeconds(30));
    private readonly Dictionary<string, ClientWebSocket> _sockets = new();
    private int _cdpId;

    private const string CefDebugUrl = "http://127.0.0.1:8080/json";
    private const string BaseUrl = "http://127.0.0.1:6767";

    private static readonly JsonSerializerOptions JsonOpts = new() { PropertyNameCaseInsensitive = true };

    public CefInjectorService(ILogger<CefInjectorService> logger)
    {
        _log = logger;
        CefInjectorServiceHolder.Instance = this;
    }

    public async Task StartAsync(CancellationToken cancellationToken)
    {
        _cts = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        await ReloadPluginFilesAsync();
        _ = Task.Run(() => InjectionLoop(_cts.Token), _cts.Token);
    }

    public Task StopAsync(CancellationToken cancellationToken)
    {
        _cts?.Cancel();
        foreach (var ws in _sockets.Values)
            try { ws.Dispose(); } catch { }
        _sockets.Clear();
        return Task.CompletedTask;
    }

    public async Task ReloadPluginFilesAsync()
    {
        var ct = _cts?.Token ?? CancellationToken.None;

        string jsPath = Path.Combine(ToolPaths.DataDir("plugin"), "public", "luatools.js");
        if (File.Exists(jsPath))
        {
            _luatoolsJs = await File.ReadAllTextAsync(jsPath, ct);
        }
        else
        {
            _luatoolsJs = "";
        }
        _polyfillJs = BuildInlinePolyfill();
    }

    private const int TickMs = 150;
    private const int InjectEveryTicks = 7;

    private async Task InjectionLoop(CancellationToken ct)
    {
        List<(string Id, string Ws)> storeTabs = new();
        int tick = 0;

        while (!ct.IsCancellationRequested)
        {
            try
            {
                if (tick % InjectEveryTicks == 0)
                {
                    var tabsJson = await _http.GetStringAsync(CefDebugUrl, ct);
                    if (!string.IsNullOrWhiteSpace(tabsJson))
                    {
                        var tabs = JsonSerializer.Deserialize<List<CefTabInfo>>(tabsJson, JsonOpts) ?? new();
                        var script = _polyfillJs + "\n" + _luatoolsJs;
                        var live = new List<(string, string)>();
                        var seen = new HashSet<string>();
                        foreach (var tab in tabs)
                        {
                            if (tab.Url?.Contains("store.steampowered.com", StringComparison.OrdinalIgnoreCase) == true
                                && !string.IsNullOrEmpty(tab.WebSocketDebuggerUrl)
                                && !string.IsNullOrEmpty(tab.Id))
                            {
                                seen.Add(tab.Id!);
                                var alive = await EvaluateReturnAsync(tab.Id!, tab.WebSocketDebuggerUrl!,
                                    "String(window.__LuaToolsReady === true)", ct);
                                if (alive != "true")
                                    await EvaluateReturnAsync(tab.Id!, tab.WebSocketDebuggerUrl!, script, ct);

                                live.Add((tab.Id!, tab.WebSocketDebuggerUrl!));
                            }
                        }

                        storeTabs = live;
                        foreach (var stale in _sockets.Keys.Where(k => !seen.Contains(k)).ToList())
                            EvictSocket(stale);
                    }
                }

                foreach (var (id, ws) in storeTabs)
                    await ProcessSingleTab(id, ws, ct);

                tick++;
                await Task.Delay(TickMs, ct);
            }
            catch (OperationCanceledException) { break; }
            catch (Exception ex)
            {
                _log.LogDebug("CEF cycle: {Message}", ex.Message);
                try { await Task.Delay(1000, ct); } catch { break; }
            }
        }
    }

    private async Task ProcessSingleTab(string tabId, string wsUrl, CancellationToken ct)
    {
        try
        {
            var pendingJson = await EvaluateReturnAsync(tabId, wsUrl,
                "JSON.stringify(Object.values(window.Millennium._pending||{}).map(function(r){return{m:r.method,a:JSON.stringify(r.args)}}))", ct);

            if (string.IsNullOrWhiteSpace(pendingJson) || pendingJson == "[]" || pendingJson == "null") return;

            var pending = JsonSerializer.Deserialize<List<PendingRequest>>(pendingJson, JsonOpts);
            if (pending is null or { Count: 0 }) return;

            var responses = new List<object>();
            foreach (var req in pending)
            {
                try
                {
                    var result = await CallBackendMethod(req.m ?? "", req.a ?? "{}");
                    responses.Add(new { v = JsonSerializer.Deserialize<object>(result) ?? (object)result });
                }
                catch (Exception ex)
                {
                    responses.Add(new { e = ex.Message });
                }
            }

            var respArray = JsonSerializer.Serialize(responses);
            await EvaluateReturnAsync(tabId, wsUrl,
                "(function(r){var ks=Object.keys(window.Millennium._pending);for(var i=0;i<ks.length&&i<r.length;i++){var id=ks[i];window.Millennium._readyResponses[id]=r[i];delete window.Millennium._pending[id];}})(" + respArray + ");", ct);
        }
        catch { }
    }

    private async Task<string> CallBackendMethod(string method, string argsRaw)
    {
        var methodMap = new Dictionary<string, (string HttpMethod, string Path)>
        {
            ["HasLuaToolsForApp"] = ("GET", "/has/{appid}"),
            ["CheckApisForApp"] = ("POST", "/check-sources/{appid}"),
            ["StartAddViaLuaToolsFromUrl"] = ("POST", "/download/{appid}"),
            ["GetAddViaLuaToolsStatus"] = ("GET", "/download-status/{appid}"),
            ["CancelAddViaLuaTools"] = ("POST", "/cancel/{appid}"),
            ["DeleteLuaToolsForApp"] = ("POST", "/remove/{appid}"),
            ["StartLuaToolsAdd"] = ("POST", "/add/{appid}"),
            ["GetLuaToolsAddStatus"] = ("GET", "/add-status/{appid}"),
            ["PickLuaToolsAddSource"] = ("POST", "/add-source/{appid}"),
            ["OpenSettings"] = ("POST", "/open/settings"),
            ["OpenFix"] = ("POST", "/open/fix/{appid}"),
            ["RestartSteam"] = ("POST", "/restart-steam"),
            ["OpenExternalUrl"] = ("POST", "/open-url"),
            ["CheckForUpdatesNow"] = ("POST", "/check-updates"),
            ["ReadLoadedApps"] = ("GET", "/loaded-apps"),
            ["DismissLoadedApps"] = ("POST", "/loaded-apps"),
            ["GetApiList"] = ("GET", "/api-list"),
            ["GetIconDataUrl"] = ("GET", "/icon"),
            ["GetGamesDatabase"] = ("GET", "/games-database"),
            ["Logger"] = ("POST", "/log"),
        };

        if (!methodMap.TryGetValue(method, out var mapping))
            return """{"success":true}""";

        string path = mapping.Path;
        try
        {
            using var doc = JsonDocument.Parse(argsRaw ?? "{}");
            foreach (var prop in doc.RootElement.EnumerateObject())
                path = path.Replace($"{{{prop.Name}}}", Uri.EscapeDataString(prop.Value.ToString() ?? ""));
        }
        catch { }

        var url = BaseUrl + path;
        if (mapping.HttpMethod == "GET")
            return await _http.GetStringAsync(url);
        else
        {
            var content = new StringContent(argsRaw ?? "{}", Encoding.UTF8, "application/json");
            var resp = await _http.PostAsync(url, content);
            return await resp.Content.ReadAsStringAsync();
        }
    }

    private async Task<ClientWebSocket> GetSocketAsync(string tabId, string wsUrl, CancellationToken ct)
    {
        if (_sockets.TryGetValue(tabId, out var existing))
        {
            if (existing.State == WebSocketState.Open) return existing;
            EvictSocket(tabId);
        }

        var ws = new ClientWebSocket();
        await ws.ConnectAsync(new Uri(wsUrl), ct);
        _sockets[tabId] = ws;
        return ws;
    }

    private void EvictSocket(string tabId)
    {
        if (_sockets.Remove(tabId, out var ws))
            try { ws.Dispose(); } catch { }
    }

    private async Task<string> EvaluateReturnAsync(string tabId, string wsUrl, string expression, CancellationToken ct)
    {
        try
        {
            var ws = await GetSocketAsync(tabId, wsUrl, ct);
            int id = ++_cdpId;
            var cmd = new { id, method = "Runtime.evaluate", @params = new { expression, returnByValue = true } };
            var json = JsonSerializer.Serialize(cmd);
            await ws.SendAsync(new ArraySegment<byte>(Encoding.UTF8.GetBytes(json)), WebSocketMessageType.Text, true, ct);

            var buffer = new byte[65536];
            using var timeoutCts = new CancellationTokenSource(TimeSpan.FromSeconds(10));
            using var linked = CancellationTokenSource.CreateLinkedTokenSource(ct, timeoutCts.Token);

            while (!linked.Token.IsCancellationRequested)
            {
                var sb = new StringBuilder();
                WebSocketReceiveResult result;
                do
                {
                    result = await ws.ReceiveAsync(new ArraySegment<byte>(buffer), linked.Token);
                    if (result.MessageType == WebSocketMessageType.Close) { EvictSocket(tabId); return ""; }
                    sb.Append(Encoding.UTF8.GetString(buffer, 0, result.Count));
                } while (!result.EndOfMessage);

                var responseText = sb.ToString();
                JsonElement root;
                try { using var doc = JsonDocument.Parse(responseText); root = doc.RootElement.Clone(); }
                catch { continue; }

                if (!(root.TryGetProperty("id", out var idEl) && idEl.TryGetInt32(out var rid) && rid == id))
                    continue;

                if (root.TryGetProperty("result", out var cdpResult) &&
                    cdpResult.TryGetProperty("result", out var evalResult) &&
                    evalResult.TryGetProperty("value", out var value) &&
                    value.ValueKind == JsonValueKind.String)
                {
                    return value.GetString() ?? responseText;
                }
                return responseText;
            }
            return "";
        }
        catch (Exception ex)
        {
            _log.LogDebug("EvaluateReturnAsync: CDP call failed: {Message}", ex.Message);
            EvictSocket(tabId);
            return "";
        }
    }

    private string BuildInlinePolyfill()
    {
        return """
        (function(){
        var real=window.Millennium;
        var pending={},ready={},reqId=0;
        function ltCall(p,m,a){
          var i='_ltr_'+(++reqId);
          pending[i]={method:m,args:a,ts:Date.now()};
          return new Promise(function(rv,rj){
            var mx=100;
            function ck(){
              var r=ready[i];
              if(r!==undefined){delete ready[i];if(r.e){rj(new Error(r.e))}else{rv(r.v)}return}
              if(--mx>0){setTimeout(ck,50)}else{delete pending[i];rv({success:true})}
            }
            ck();
          });
        }
        if(real&&typeof real.callServerMethod==='function'){
          var realCall=real.callServerMethod.bind(real);
          real.callServerMethod=function(p,m,a){return p==='luatools'?ltCall(p,m,a):realCall(p,m,a)};
          real._pending=pending;
          real._readyResponses=ready;
          window.Millennium=real;
        }else{
          window.Millennium={_pending:pending,_readyResponses:ready,callServerMethod:function(p,m,a){return ltCall(p,m,a)}};
        }
        })();
        """;
    }
}

internal class CefTabInfo
{
    [JsonPropertyName("title")] public string? Title { get; set; }
    [JsonPropertyName("url")] public string? Url { get; set; }
    [JsonPropertyName("webSocketDebuggerUrl")] public string? WebSocketDebuggerUrl { get; set; }
    [JsonPropertyName("id")] public string? Id { get; set; }
}

internal class PendingRequest
{
    public string? m { get; set; }
    public string? a { get; set; }
}

public sealed class PluginLog
{
    private static readonly object _lock = new();
    public static string FilePath => Path.Combine(ToolPaths.DataDir(), "plugin-backend.log");

    public static void Log(string msg)
    {
        try
        {
            lock (_lock)
            {
                Directory.CreateDirectory(Path.GetDirectoryName(FilePath)!);
                File.AppendAllText(FilePath, $"[{DateTime.Now:HH:mm:ss.fff}] {msg}{Environment.NewLine}");
            }
        }
        catch { /* logging must never throw */ }
    }
}
