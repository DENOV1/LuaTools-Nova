// Derived from madoiscool/LuaTools (MIT) — AppHttp.cs, DohResolver.cs, GithubProxy.cs, AppConfig.cs.
using System.Collections.Concurrent;
using System.IO;
using System.Net;
using System.Net.Http;
using System.Net.Sockets;
using System.Text.Json;

namespace LuaTools.Infrastructure.Network;

/// <summary>Compiled-in client configuration (public values, same as the original).</summary>
public static class AppConfig
{
    public const string SupabaseUrl = "https://db.lua.tools";
    public const string SupabaseAnonKey =
        "eyJhbGciOiJIUzI1NiIsInR5cCI6IkpXVCJ9.eyJpYXQiOjE3NzYwMzkzNzYsImV4cCI6MTg5MzQ1NjAwMCwicm9sZSI6ImFub24iLCJpc3MiOiJzdXBhYmFzZSJ9.f_-K38u3odjltP-g_67FVmG32Vg-_-k-lNBvIaVUVBM";
    public const string ApiBaseUrl = "https://lua.tools";
    public const string BotAccountEmailDomain = "@bot.lua.tools";
    public const string HubcapBaseUrl = "https://hubcapmanifest.com";
    public const int OAuthCallbackPort = 53789;
    public const string OAuthCallbackUrl = "http://localhost:53789/callback";
    public const int DailyDownloadLimit = 25;
    public const string SteamStoreSearchUrl = "https://store.steampowered.com/api/storesearch/";
    public const string SteamFeaturedUrl = "https://store.steampowered.com/api/featuredcategories";
    public const string HardwareAppIdListUrl =
        "https://raw.githubusercontent.com/jsnli/steamappidlist/master/data/hardware_appid.json";
    public const string SteamlessRepo = "atom0s/Steamless";
    public const string SteamAutoCrackRepo = "SteamAutoCracks/Steam-auto-crack";
    public const string DepotDownloaderRepo = "SteamRE/DepotDownloader";
    public const string ManifestBackendUrl = "http://167.235.229.108";
    public const string ManifestBackendUserAgent = "secretgoonpoon";
    public const string DonateKeysUserAgent = "discord(dot)gg/luatools";
    public const string UmamiHost = "https://analytics.lua.tools";
    public const string UmamiWebsiteId = "820d782c-a434-424f-9f90-dee83dc6032e";
    public const string UmamiHostname = "desktop.lua.tools";

    public static readonly string[] GithubReleasesRepos =
    [
        "https://github.com/DENOV1/LuaTools-Nova",
    ];

    public static string GithubReleasesRepo => GithubReleasesRepos[0];

    public const string PluginReleasesOwner = "madoiscool";
    public const string PluginReleasesRepo = "LTSP";

    public static readonly string[] GithubApiMirrors =
    [
        "https://lua.tools/api/gh/",
    ];

    public static readonly string[] GithubDownloadMirrors =
    [
        "https://ghproxy.net/",
        "https://ghfast.top/",
        "https://gh.ddlc.top/",
    ];
}

/// <summary>
/// The one HttpClient factory for the whole app: a single SocketsHttpHandler whose connect
/// callback honors the DNS setting (Auto/Always/Never) via DoH.
/// </summary>
public static class AppHttp
{
    public static Func<string>? ModeProvider { get; set; }
    private static readonly DohResolver Resolver = new();
    private static volatile bool _systemDnsSuspect;

    private static readonly SocketsHttpHandler Handler = new()
    {
        PooledConnectionLifetime = TimeSpan.FromMinutes(2),
        ConnectCallback = ConnectAsync,
    };

    public static HttpClient Create(TimeSpan timeout, Uri? baseAddress = null) =>
        new(Handler, disposeHandler: false) { Timeout = timeout, BaseAddress = baseAddress };

    public static HttpMessageHandler SharedHandler => Handler;

    private static string Mode
    {
        get
        {
            try { return ModeProvider?.Invoke() ?? "Auto"; }
            catch { return "Auto"; }
        }
    }

    private static async ValueTask<Stream> ConnectAsync(SocketsHttpConnectionContext ctx, CancellationToken ct)
    {
        string host = ctx.DnsEndPoint.Host;
        int port = ctx.DnsEndPoint.Port;
        string mode = Mode;

        if (mode == "Never" || DohResolver.ShouldBypass(host))
            return await ConnectBySystem(host, port, ct);

        if (mode == "Always")
        {
            var addresses = await Resolver.ResolveAsync(host, ct);
            if (addresses.Length > 0)
            {
                try { return await ConnectTo(addresses, port, ct); }
                catch (OperationCanceledException) when (ct.IsCancellationRequested) { throw; }
                catch { /* DoH addresses did not work; system may know better */ }
            }
            return await ConnectBySystem(host, port, ct);
        }

        // ── Auto ──
        if (_systemDnsSuspect)
        {
            var addresses = await Resolver.ResolveAsync(host, ct);
            if (addresses.Length > 0) return await ConnectTo(addresses, port, ct);
            return await ConnectBySystem(host, port, ct);
        }

        try
        {
            return await ConnectBySystem(host, port, ct);
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested) { throw; }
        catch (Exception systemFailure)
        {
            var addresses = await Resolver.ResolveAsync(host, ct);
            if (addresses.Length == 0) throw;

            Stream stream;
            try { stream = await ConnectTo(addresses, port, ct); }
            catch (OperationCanceledException) when (ct.IsCancellationRequested) { throw; }
            catch { throw systemFailure; }
            _systemDnsSuspect = true;
            return stream;
        }
    }

    private static async ValueTask<Stream> ConnectBySystem(string host, int port, CancellationToken ct)
    {
        var socket = new Socket(SocketType.Stream, ProtocolType.Tcp) { NoDelay = true };
        try
        {
            await socket.ConnectAsync(host, port, ct);
            return new NetworkStream(socket, ownsSocket: true);
        }
        catch { socket.Dispose(); throw; }
    }

    private static async ValueTask<Stream> ConnectTo(IPAddress[] addresses, int port, CancellationToken ct)
    {
        var socket = new Socket(SocketType.Stream, ProtocolType.Tcp) { NoDelay = true };
        try
        {
            await socket.ConnectAsync(addresses, port, ct);
            return new NetworkStream(socket, ownsSocket: true);
        }
        catch { socket.Dispose(); throw; }
    }
}

/// <summary>Resolves host names through Cloudflare's DNS-over-HTTPS endpoint (1.1.1.1, by IP literal).</summary>
public sealed class DohResolver
{
    public const string Endpoint = "https://1.1.1.1/dns-query";

    private static readonly TimeSpan MinTtl = TimeSpan.FromSeconds(30);
    private static readonly TimeSpan MaxTtl = TimeSpan.FromMinutes(5);

    private readonly HttpClient _http;
    private readonly ConcurrentDictionary<string, (IPAddress[] Addresses, DateTimeOffset Expires)> _cache =
        new(StringComparer.OrdinalIgnoreCase);

    internal Func<DateTimeOffset> UtcNow { get; init; } = () => DateTimeOffset.UtcNow;

    public DohResolver(HttpMessageHandler? handler = null)
    {
        _http = handler is null
            ? new HttpClient { Timeout = TimeSpan.FromSeconds(5) }
            : new HttpClient(handler) { Timeout = TimeSpan.FromSeconds(5) };
        _http.DefaultRequestHeaders.Accept.Add(new("application/dns-json"));
    }

    public static bool ShouldBypass(string? host)
    {
        if (string.IsNullOrWhiteSpace(host)) return true;
        if (IPAddress.TryParse(host, out _)) return true;
        if (host.Equals("localhost", StringComparison.OrdinalIgnoreCase)) return true;
        if (host.EndsWith(".local", StringComparison.OrdinalIgnoreCase)) return true;
        if (host.EndsWith(".localhost", StringComparison.OrdinalIgnoreCase)) return true;
        return !host.Contains('.');
    }

    public async Task<IPAddress[]> ResolveAsync(string host, CancellationToken ct = default)
    {
        if (ShouldBypass(host)) return Array.Empty<IPAddress>();

        if (_cache.TryGetValue(host, out var hit) && hit.Expires > UtcNow())
            return hit.Addresses;

        try
        {
            string url = $"{Endpoint}?name={Uri.EscapeDataString(host)}&type=A";
            using var resp = await _http.GetAsync(url, ct);
            if (!resp.IsSuccessStatusCode) return Array.Empty<IPAddress>();

            string body = await resp.Content.ReadAsStringAsync(ct);
            var (addresses, ttl) = Parse(body);
            if (addresses.Length == 0) return Array.Empty<IPAddress>();

            var lifetime = ttl < MinTtl ? MinTtl : ttl > MaxTtl ? MaxTtl : ttl;
            _cache[host] = (addresses, UtcNow() + lifetime);
            return addresses;
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested) { throw; }
        catch { return Array.Empty<IPAddress>(); }
    }

    public static (IPAddress[] Addresses, TimeSpan Ttl) Parse(string json)
    {
        try
        {
            using var doc = JsonDocument.Parse(json);
            var root = doc.RootElement;

            if (root.ValueKind != JsonValueKind.Object) return (Array.Empty<IPAddress>(), default);
            if (!root.TryGetProperty("Status", out var status) || status.GetInt32() != 0)
                return (Array.Empty<IPAddress>(), default);
            if (!root.TryGetProperty("Answer", out var answer) || answer.ValueKind != JsonValueKind.Array)
                return (Array.Empty<IPAddress>(), default);

            var addresses = new List<IPAddress>();
            int ttl = int.MaxValue;

            foreach (var entry in answer.EnumerateArray())
            {
                if (!entry.TryGetProperty("type", out var type)) continue;
                int t = type.GetInt32();
                if (t != 1 && t != 28) continue;
                if (!entry.TryGetProperty("data", out var data)) continue;
                if (!IPAddress.TryParse(data.GetString(), out var ip)) continue;

                addresses.Add(ip);
                if (entry.TryGetProperty("TTL", out var recordTtl) && recordTtl.TryGetInt32(out int v) && v < ttl)
                    ttl = v;
            }

            if (addresses.Count == 0) return (Array.Empty<IPAddress>(), default);
            return (addresses.ToArray(), TimeSpan.FromSeconds(ttl == int.MaxValue ? 0 : Math.Max(0, ttl)));
        }
        catch { return (Array.Empty<IPAddress>(), default); }
    }
}

/// <summary>GitHub request resilience: direct first, then capability-matched mirrors.</summary>
public sealed class GithubProxy
{
    private readonly HttpClient _http = AppHttp.Create(TimeSpan.FromMinutes(5));

    private static bool IsGithub(string url) =>
        url.StartsWith("https://github.com/", StringComparison.OrdinalIgnoreCase) ||
        url.StartsWith("https://api.github.com/", StringComparison.OrdinalIgnoreCase) ||
        url.StartsWith("https://raw.githubusercontent.com/", StringComparison.OrdinalIgnoreCase) ||
        url.StartsWith("https://objects.githubusercontent.com/", StringComparison.OrdinalIgnoreCase);

    public static IEnumerable<string> Candidates(string url)
    {
        yield return url;
        if (!IsGithub(url)) yield break;
        var mirrors = url.StartsWith("https://api.github.com/", StringComparison.OrdinalIgnoreCase)
            ? AppConfig.GithubApiMirrors
            : AppConfig.GithubDownloadMirrors;
        foreach (var mirror in mirrors)
            yield return mirror + url;
    }

    public async Task<HttpResponseMessage?> SendAsync(string url, CancellationToken ct = default)
    {
        HttpResponseMessage? lastResponse = null;
        foreach (var candidate in Candidates(url))
        {
            try
            {
                using var req = new HttpRequestMessage(HttpMethod.Get, candidate);
                req.Headers.TryAddWithoutValidation("User-Agent", "LuaToolsNova");
                req.Headers.TryAddWithoutValidation("Accept", "application/vnd.github+json");
                var res = await _http.SendAsync(req, ct);
                if (res.IsSuccessStatusCode) return res;
                lastResponse?.Dispose();
                lastResponse = res;
            }
            catch (OperationCanceledException) when (ct.IsCancellationRequested) { throw; }
            catch { /* fall through to next mirror */ }
        }
        return lastResponse;
    }

    public async Task DownloadAsync(string url, string destPath, IProgress<double?>? progress, CancellationToken ct = default)
    {
        Exception? last = null;
        foreach (var candidate in Candidates(url))
        {
            try
            {
                using var req = new HttpRequestMessage(HttpMethod.Get, candidate);
                req.Headers.TryAddWithoutValidation("User-Agent", "LuaToolsNova");
                using var res = await _http.SendAsync(req, HttpCompletionOption.ResponseHeadersRead, ct);
                res.EnsureSuccessStatusCode();

                long? total = res.Content.Headers.ContentLength;
                await using var src = await res.Content.ReadAsStreamAsync(ct);
                await using var dst = File.Create(destPath);

                var buffer = new byte[81920];
                long written = 0;
                int read;
                while ((read = await src.ReadAsync(buffer, ct)) > 0)
                {
                    await dst.WriteAsync(buffer.AsMemory(0, read), ct);
                    written += read;
                    progress?.Report(total is > 0 ? (double)written / total.Value : null);
                }
                return;
            }
            catch (OperationCanceledException) when (ct.IsCancellationRequested) { throw; }
            catch (Exception ex)
            {
                last = ex;
                try { File.Delete(destPath); } catch { /* partial file */ }
            }
        }
        throw last ?? new HttpRequestException($"Could not download {url} from GitHub or any mirror.");
    }
}
