// Derived from madoiscool/LuaTools (MIT) — LuaToolsApiClient.cs, HubcapService.cs, HttpFileDownloader.cs.
using System.Net;
using System.Net.Http.Headers;
using System.Text.Json;
using System.Text.RegularExpressions;
using LuaTools.Core.Downloads;
using LuaTools.Core.Models;
using LuaTools.Core.Services;
using LuaTools.Infrastructure.Network;

namespace LuaTools.Infrastructure.Clients;

public sealed class ApiException(string message, HttpStatusCode? status = null) : Exception(message)
{
    public HttpStatusCode? Status { get; } = status;
}

/// <summary>The shared response→file copy loop + staging folder.</summary>
public static class HttpFileDownloader
{
    public static readonly string StagingFolder =
        Path.Combine(Path.GetTempPath(), "LuaToolsNova", "downloads");

    public static async Task<DownloadedFile> SaveResponseAsync(
        HttpResponseMessage res, string fallbackName,
        IProgress<DownloadProgress>? progress, CancellationToken ct)
    {
        string fileName = res.Content.Headers.ContentDisposition?.FileName?.Trim('"') ?? fallbackName;
        foreach (char c in Path.GetInvalidFileNameChars()) fileName = fileName.Replace(c, '_');

        Directory.CreateDirectory(StagingFolder);
        string filePath = Path.Combine(StagingFolder, fileName);

        long? total = res.Content.Headers.ContentLength;
        await using var src = await res.Content.ReadAsStreamAsync(ct);
        await using var dst = File.Create(filePath);

        var buffer = new byte[81920];
        long written = 0;
        int read;
        while ((read = await src.ReadAsync(buffer, ct)) > 0)
        {
            await dst.WriteAsync(buffer.AsMemory(0, read), ct);
            written += read;
            progress?.Report(new DownloadProgress(written, total));
        }

        progress?.Report(new DownloadProgress(written, total ?? written));

        return new DownloadedFile(filePath, fileName);
    }

    public static void SweepStale()
    {
        try
        {
            if (!Directory.Exists(StagingFolder)) return;
            var cutoff = DateTime.UtcNow.AddDays(-1);
            foreach (string path in Directory.EnumerateFiles(StagingFolder))
            {
                try
                {
                    if (File.GetLastWriteTimeUtc(path) < cutoff) File.Delete(path);
                }
                catch { /* in use or gone */ }
            }
        }
        catch { /* best effort */ }
    }
}

/// <summary>Typed client for the lua.tools web API + direct Steam endpoints.</summary>
public sealed class LuaToolsApiClient
{
    private readonly IAccessTokenSource _auth;
    private readonly SteamAppInfoCache _appInfo;
    private readonly CoverCache _covers;
    private readonly HttpClient _http = AppHttp.Create(TimeSpan.FromMinutes(5), new Uri(AppConfig.ApiBaseUrl));
    private static readonly JsonSerializerOptions JsonOpts = new() { PropertyNameCaseInsensitive = true };

    public LuaToolsApiClient(IAccessTokenSource auth, SteamAppInfoCache appInfo, CoverCache covers)
    {
        _auth = auth;
        _appInfo = appInfo;
        _covers = covers;
    }

    public async Task<List<SteamSearchResult>> SearchAsync(string query, CancellationToken ct = default)
    {
        var url = $"{AppConfig.SteamStoreSearchUrl}?term={Uri.EscapeDataString(query)}&l=english&cc=US";
        var res = await _http.GetAsync(url, ct);
        if (!res.IsSuccessStatusCode) return new List<SteamSearchResult>();

        var data = await ReadJsonAsync<SteamStoreSearchResponse>(res, ct);
        return (data?.Items ?? new List<SteamStoreItem>())
            .Take(8)
            .Select(i => new SteamSearchResult
            {
                AppId = i.Id,
                Name = i.Name,
                Icon = i.TinyImage ?? $"https://cdn.cloudflare.steamstatic.com/steam/apps/{i.Id}/capsule_sm_120.jpg",
            })
            .ToList();
    }

    public async Task<(List<SteamFeaturedItem> TopSellers, List<SteamFeaturedItem> NewReleases)> GetFeaturedAsync(
        CancellationToken ct = default)
    {
        try
        {
            var res = await _http.GetAsync($"{AppConfig.SteamFeaturedUrl}?cc=us&l=english", ct);
            if (!res.IsSuccessStatusCode) return (new List<SteamFeaturedItem>(), new List<SteamFeaturedItem>());
            var data = await ReadJsonAsync<SteamFeaturedResponse>(res, ct);

            static List<SteamFeaturedItem> Clean(SteamFeaturedCategory? c) =>
                (c?.Items ?? new List<SteamFeaturedItem>())
                    .Where(i => i.Type == 0 && i.Id > 0 && !string.IsNullOrEmpty(i.LargeCapsuleImage))
                    .DistinctBy(i => i.Id)
                    .Take(20)
                    .ToList();

            return (Clean(data?.TopSellers), Clean(data?.NewReleases));
        }
        catch { return (new List<SteamFeaturedItem>(), new List<SteamFeaturedItem>()); }
    }

    public async Task<GameDetails?> GetDetailsAsync(string appid, CancellationToken ct = default)
    {
        if (!long.TryParse(appid, out long id)) return null;
        var details = await _appInfo.ResolveGameDetailsAsync(id, ct);
        if (details is { HeaderImage: { Length: > 0 } img })
            _ = _covers.EnsureAsync(id, img, CancellationToken.None);
        return details;
    }

    public async Task<Dictionary<string, string>> CheckSourcesAsync(string appid, CancellationToken ct = default)
    {
        var req = new HttpRequestMessage(HttpMethod.Get, $"{AppConfig.ManifestBackendUrl}/check_apis?appid={appid}");
        req.Headers.TryAddWithoutValidation("User-Agent", AppConfig.ManifestBackendUserAgent);
        HttpResponseMessage res;
        try
        {
            res = await _http.SendAsync(req, ct);
        }
        catch (Exception ex)
        {
            // v1.0.3: a network failure here used to be swallowed into "no sources". Distinguish it.
            NovaLog.Error("sources", $"check_apis network failure for {appid}: {ex.Message}");
            throw new ApiException($"Source backend unreachable ({ex.GetType().Name}). The manifest backend may be down or blocked.");
        }
        if (!res.IsSuccessStatusCode)
        {
            NovaLog.Error("sources", $"check_apis HTTP {(int)res.StatusCode} for {appid}");
            throw new ApiException($"Source backend returned HTTP {(int)res.StatusCode}. It may be temporarily down.");
        }
        NovaLog.Info("sources", $"check_apis OK for {appid}");
        return await ReadJsonAsync<Dictionary<string, string>>(res, ct) ?? new Dictionary<string, string>();
    }

    public async Task<StandardUsage?> GetStandardUsageAsync(CancellationToken ct = default)
    {
        try
        {
            var todayUtc = DateTime.UtcNow.Date.ToString("yyyy-MM-dd'T'HH:mm:ss'Z'");
            string url = $"{AppConfig.SupabaseUrl}/rest/v1/user_downloads" +
                         $"?select=appid&downloaded_at=gte.{Uri.EscapeDataString(todayUtc)}";

            var req = new HttpRequestMessage(HttpMethod.Head, url);
            req.Headers.TryAddWithoutValidation("apikey", AppConfig.SupabaseAnonKey);
            req.Headers.Authorization = new AuthenticationHeaderValue("Bearer", await _auth.GetValidAccessTokenAsync());
            req.Headers.TryAddWithoutValidation("Prefer", "count=exact");

            using var res = await _http.SendAsync(req, ct);
            if (!res.IsSuccessStatusCode) return null;

            string? range = res.Content.Headers.TryGetValues("Content-Range", out var v) ? v.FirstOrDefault() : null;
            int used = 0;
            if (range is not null && range.Split('/') is [_, var countStr] && int.TryParse(countStr, out int c))
                used = c;
            return new StandardUsage(used, AppConfig.DailyDownloadLimit);
        }
        catch { return null; }
    }

    public async Task<SupporterStatus?> GetSupporterStatusAsync(CancellationToken ct = default)
    {
        try
        {
            var res = await SendAsync(HttpMethod.Get, "/api/me/supporter-status", ct);
            return await ReadJsonAsync<SupporterStatus>(res, ct);
        }
        catch { return null; }
    }

    public async Task<DlcInfo?> GetDlcInfoAsync(string appid, string baseAppId, CancellationToken ct = default)
    {
        var res = await SendAsync(HttpMethod.Get, $"/api/dlc/info?appid={appid}&base={baseAppId}", ct);
        return await ReadJsonAsync<DlcInfo>(res, ct);
    }

    public Task<DownloadedFile> DownloadManifestAsync(
        string appid, string source, string? gameName,
        IProgress<DownloadProgress>? progress, CancellationToken ct = default)
    {
        string url = $"/api/manifest/download?appid={appid}&source={Uri.EscapeDataString(source)}";
        if (!string.IsNullOrEmpty(gameName)) url += $"&game_name={Uri.EscapeDataString(gameName)}";
        return DownloadFileAsync(url, $"{appid}.zip", progress, ct);
    }

    public Task<DownloadedFile> DownloadDepotManifestAsync(
        long depotId, string manifestId,
        IProgress<DownloadProgress>? progress, CancellationToken ct = default)
        => DownloadFileAsync($"/api/givemethemanifestpunk/{depotId}/{manifestId}",
                             $"{depotId}_{manifestId}.manifest", progress, ct);

    public Task<DownloadedFile> GenerateDlcAsync(
        string appid, string baseAppId, string? gameName,
        IProgress<DownloadProgress>? progress, CancellationToken ct = default)
    {
        string url = $"/api/dlc/generate?appid={appid}&base={baseAppId}";
        if (!string.IsNullOrEmpty(gameName)) url += $"&game_name={Uri.EscapeDataString(gameName)}";
        return DownloadFileAsync(url, $"{appid}.lua", progress, ct);
    }

    public async Task<DenuvoListingsResponse?> GetDenuvoListingsAsync(CancellationToken ct = default)
    {
        var res = await _http.GetAsync("/api/denuvo/listings", ct);
        if (!res.IsSuccessStatusCode) return null;
        return await ReadJsonAsync<DenuvoListingsResponse>(res, ct);
    }

    public async Task<DenuvoFixesResponse?> GetDenuvoFixesAsync(string appid, CancellationToken ct = default)
    {
        var res = await _http.GetAsync($"/api/denuvo/fixes?appid={Uri.EscapeDataString(appid)}", ct);
        if (!res.IsSuccessStatusCode) return null;
        return await ReadJsonAsync<DenuvoFixesResponse>(res, ct);
    }

    public async Task<DownloadedFile> DownloadDenuvoAsync(
        string fixId, string slot, string fallbackName,
        IProgress<DownloadProgress>? progress, CancellationToken ct = default)
    {
        var res = await SendAsync(HttpMethod.Get,
            $"/api/denuvo/download?fix={Uri.EscapeDataString(fixId)}&slot={Uri.EscapeDataString(slot)}", ct);
        var signed = await ReadJsonAsync<DenuvoDownloadResponse>(res, ct);
        if (string.IsNullOrWhiteSpace(signed?.Url))
            throw new ApiException("The download link was empty. Try again.");

        return await DownloadFromUrlAsync(signed.Url, fallbackName, progress, ct);
    }

    private async Task<HttpResponseMessage> SendAsync(
        HttpMethod method, string url, CancellationToken ct,
        HttpCompletionOption completion = HttpCompletionOption.ResponseContentRead)
    {
        var req = new HttpRequestMessage(method, url);
        req.Headers.Authorization = new AuthenticationHeaderValue("Bearer", await _auth.GetValidAccessTokenAsync());

        var res = await _http.SendAsync(req, completion, ct);
        if (res.IsSuccessStatusCode) return res;

        string message = $"Request failed ({(int)res.StatusCode})";
        try
        {
            var err = JsonSerializer.Deserialize<ApiError>(await res.Content.ReadAsStringAsync(ct), JsonOpts);
            if (!string.IsNullOrWhiteSpace(err?.Error)) message = err.Error;
        }
        catch { /* non-JSON error body */ }

        if (res.StatusCode == HttpStatusCode.Unauthorized) message = "Session expired. Please sign in again.";
        throw new ApiException(message, res.StatusCode);
    }

    private static async Task<T?> ReadJsonAsync<T>(HttpResponseMessage res, CancellationToken ct) =>
        JsonSerializer.Deserialize<T>(await res.Content.ReadAsStringAsync(ct), JsonOpts);

    private async Task<DownloadedFile> DownloadFileAsync(
        string url, string fallbackName, IProgress<DownloadProgress>? progress, CancellationToken ct)
    {
        var res = await SendAsync(HttpMethod.Get, url, ct, HttpCompletionOption.ResponseHeadersRead);
        return await HttpFileDownloader.SaveResponseAsync(res, fallbackName, progress, ct);
    }

    private async Task<DownloadedFile> DownloadFromUrlAsync(
        string url, string fallbackName, IProgress<DownloadProgress>? progress, CancellationToken ct)
    {
        var req = new HttpRequestMessage(HttpMethod.Get, url);
        var res = await _http.SendAsync(req, HttpCompletionOption.ResponseHeadersRead, ct);
        if (!res.IsSuccessStatusCode)
            throw new ApiException($"Download failed ({(int)res.StatusCode}).", res.StatusCode);
        return await HttpFileDownloader.SaveResponseAsync(res, fallbackName, progress, ct);
    }
}

/// <summary>Abstraction so Application can expose auth tokens without depending on the auth service shape.</summary>
public interface IAccessTokenSource
{
    Task<string> GetValidAccessTokenAsync();
}

/// <summary>Talks to Hubcap directly with the user's own API key.</summary>
public sealed partial class HubcapService
{
    private readonly HttpClient _http = AppHttp.Create(TimeSpan.FromMinutes(5), new Uri(AppConfig.HubcapBaseUrl));
    private static readonly JsonSerializerOptions JsonOpts = new() { PropertyNameCaseInsensitive = true };

    [GeneratedRegex("^smm_[0-9a-f]{96}$")]
    private static partial Regex KeyFormatRegex();

    public static bool IsValidKeyFormat(string? key) => key is not null && KeyFormatRegex().IsMatch(key);

    public async Task<HubcapStats?> GetStatsAsync(string key, CancellationToken ct = default)
    {
        try
        {
            var res = await _http.GetAsync($"/api/v1/user/stats?api_key={Uri.EscapeDataString(key)}", ct);
            if (!res.IsSuccessStatusCode) return null;
            return await ReadJsonAsync<HubcapStats>(res, ct);
        }
        catch { return null; }
    }

    public async Task<HubcapManifestStatus?> CheckStatusAsync(string key, string appid, CancellationToken ct = default)
    {
        try
        {
            var req = new HttpRequestMessage(HttpMethod.Get, $"/api/v1/status/{Uri.EscapeDataString(appid)}");
            req.Headers.Authorization = new AuthenticationHeaderValue("Bearer", key);
            var res = await _http.SendAsync(req, ct);
            if (!res.IsSuccessStatusCode) return null;
            return await ReadJsonAsync<HubcapManifestStatus>(res, ct);
        }
        catch { return null; }
    }

    public async Task<DownloadedFile> DownloadManifestAsync(
        string appid, string key, IProgress<DownloadProgress>? progress, CancellationToken ct = default)
    {
        var url = $"/api/v1/manifest/{Uri.EscapeDataString(appid)}?api_key={Uri.EscapeDataString(key)}";
        using var res = await _http.GetAsync(url, HttpCompletionOption.ResponseHeadersRead, ct);
        if (!res.IsSuccessStatusCode)
        {
            string message = res.StatusCode switch
            {
                HttpStatusCode.Unauthorized => "Your Hubcap key is invalid or expired.",
                HttpStatusCode.TooManyRequests => "Your Hubcap daily limit has been reached.",
                HttpStatusCode.NotFound => "No Hubcap manifest is available for this app.",
                _ => $"Hubcap download failed ({(int)res.StatusCode}).",
            };
            throw new ApiException(message, res.StatusCode);
        }
        return await HttpFileDownloader.SaveResponseAsync(res, $"{appid}.zip", progress, ct);
    }

    private static async Task<T?> ReadJsonAsync<T>(HttpResponseMessage res, CancellationToken ct) =>
        JsonSerializer.Deserialize<T>(await res.Content.ReadAsStringAsync(ct), JsonOpts);
}
