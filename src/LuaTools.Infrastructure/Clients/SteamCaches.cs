// Derived from madoiscool/LuaTools (MIT) — SteamAppInfoCache / SteamAppListCache / SteamDepotInfo /
// CoverCache / HardwareAppIdService.
using System.Collections.Concurrent;
using System.Net;
using System.Text.Json;
using System.Text.Json.Serialization;
using System.Text.RegularExpressions;
using LuaTools.Core.Downloads;
using LuaTools.Core.Models;
using LuaTools.Core.Persistence;
using LuaTools.Infrastructure.Network;

namespace LuaTools.Infrastructure.Clients;

public sealed record SteamAppInfo(string Name, string? HeaderImage);

public sealed record AppFilterData(
    string? Type, IReadOnlyList<string> Genres, bool Windows, bool Mac, bool Linux,
    int? ReleaseYear, DateTime? ReleaseDate, string? ReleaseDateText, bool IsFree,
    int? Metacritic, long? Reviews, bool IsAdult);

public sealed record AppOverview(
    string? ShortDescription, IReadOnlyList<string> Developers, IReadOnlyList<string> Publishers, IReadOnlyList<string> Genres)
{
    public string DeveloperLabel => string.Join(", ", Developers);
    public string PublisherLabel => string.Join(", ", Publishers);
    public bool HasDescription => !string.IsNullOrWhiteSpace(ShortDescription);
    public bool HasDeveloper => Developers.Count > 0;
    public bool HasDistinctPublisher =>
        Publishers.Count > 0 && !string.Equals(PublisherLabel, DeveloperLabel, StringComparison.OrdinalIgnoreCase);
    public bool HasGenres => Genres.Count > 0;
    public bool HasAnything => HasDescription || HasDeveloper || HasGenres;
}

public sealed class SteamAppInfoCache
{
    private readonly string _detailsDir;
    private readonly HttpClient _http = AppHttp.Create(TimeSpan.FromSeconds(15));
    private readonly ConcurrentDictionary<long, SteamAppInfo?> _cache = new();
    private readonly ConcurrentDictionary<long, AppFilterData> _filterCache = new();
    private readonly ConcurrentDictionary<long, AppOverview> _overviewCache = new();
    private readonly ConcurrentDictionary<long, byte> _unresolvable = new();
    private readonly CacheServiceProxy _cacheStore;
    private readonly SemaphoreSlim _rateGate = new(1, 1);
    private readonly Queue<DateTime> _requestTimes = new();
    private static readonly TimeSpan Window = TimeSpan.FromSeconds(200);
    private const int MaxPerWindow = 190;
    private DateTime _lastPersist = DateTime.MinValue;
    private int _interactiveWaiting;

    public SteamAppInfoCache(CacheServiceProxy cache, string dataDir)
    {
        _cacheStore = cache;
        _detailsDir = Path.Combine(dataDir, "details");

        var now = DateTime.UtcNow;
        foreach (long ms in cache.GetSteamApiRequestTimes())
        {
            var t = DateTimeOffset.FromUnixTimeMilliseconds(ms).UtcDateTime;
            if (now - t < Window) _requestTimes.Enqueue(t);
        }
    }

    public static string GuessHeaderImageUrl(long appid) =>
        $"https://cdn.cloudflare.steamstatic.com/steam/apps/{appid}/header.jpg";

    public SteamAppInfo? GetCached(long appid)
    {
        if (_cache.TryGetValue(appid, out var info)) return info;
        var fromDisk = ReadInfoFromDetails(appid);
        _cache[appid] = fromDisk;
        return fromDisk;
    }

    private SteamAppInfo? ReadInfoFromDetails(long appid)
    {
        try
        {
            if (!File.Exists(DetailsPath(appid))) return null;
            using var doc = JsonDocument.Parse(File.ReadAllText(DetailsPath(appid)));
            var d = doc.RootElement;
            if (d.ValueKind != JsonValueKind.Object) return null;
            string? name = d.TryGetProperty("name", out var n) ? n.GetString() : null;
            if (string.IsNullOrWhiteSpace(name)) return null;
            string? image = d.TryGetProperty("header_image", out var img) ? img.GetString() : null;
            return new SteamAppInfo(name, image);
        }
        catch { return null; }
    }

    internal static JsonElement? FindAppDetailsEntry(JsonElement root, long appid)
    {
        if (root.ValueKind != JsonValueKind.Object) return null;

        if (root.TryGetProperty(appid.ToString(), out var direct) &&
            (!TryGetDataObject(direct, out var directData) || DataNamesApp(directData, appid)))
            return direct;

        foreach (var prop in root.EnumerateObject())
            if (TryGetDataObject(prop.Value, out var data) && DataNamesApp(data, appid))
                return prop.Value;

        return null;
    }

    private static bool TryGetDataObject(JsonElement entry, out JsonElement data)
    {
        data = default;
        return entry.ValueKind == JsonValueKind.Object &&
               entry.TryGetProperty("data", out data) && data.ValueKind == JsonValueKind.Object;
    }

    private static bool DataNamesApp(JsonElement data, long appid) =>
        data.TryGetProperty("steam_appid", out var sa) && sa.ValueKind == JsonValueKind.Number &&
        sa.TryGetInt64(out long got) && got == appid;

    public async Task<SteamAppInfo?> ResolveAsync(long appid, CancellationToken ct = default)
    {
        if (_cache.TryGetValue(appid, out var cached)) return cached;

        var url = $"https://store.steampowered.com/api/appdetails?appids={appid}&cc=us&l=english";
        const int maxAttempts = 3;
        for (int attempt = 0; attempt < maxAttempts; attempt++)
        {
            await ThrottleAsync(ct);
            try
            {
                using var res = await _http.GetAsync(url, ct);
                if (res.StatusCode is HttpStatusCode.TooManyRequests or HttpStatusCode.Forbidden)
                {
                    if (attempt < maxAttempts - 1) { await Task.Delay(TimeSpan.FromSeconds(4 * (attempt + 1)), ct); continue; }
                    return null;
                }
                if (!res.IsSuccessStatusCode) return null;

                using var doc = JsonDocument.Parse(await res.Content.ReadAsStringAsync(ct));
                if (FindAppDetailsEntry(doc.RootElement, appid) is not { } entry)
                {
                    _unresolvable.TryAdd(appid, 0);
                    return null;
                }

                if (!entry.TryGetProperty("success", out var okEl) || okEl.ValueKind != JsonValueKind.True ||
                    !entry.TryGetProperty("data", out var data) || data.ValueKind != JsonValueKind.Object)
                {
                    _ = SaveFullDetailsAsync(appid, "{}");
                    return null;
                }

                string? name = data.TryGetProperty("name", out var nameEl) ? nameEl.GetString() : null;
                if (string.IsNullOrWhiteSpace(name)) return null;

                string? image = data.TryGetProperty("header_image", out var img) ? img.GetString() : null;
                var info = new SteamAppInfo(name, image);
                _cache[appid] = info;
                _ = SaveFullDetailsAsync(appid, data.GetRawText());
                return info;
            }
            catch (OperationCanceledException) { return null; }
            catch { return null; }
        }
        return null;
    }

    public bool HasFullDetails(long appid) => File.Exists(DetailsPath(appid));

    public string? GetFullDetails(long appid)
    {
        try { return HasFullDetails(appid) ? File.ReadAllText(DetailsPath(appid)) : null; }
        catch { return null; }
    }

    public AppFilterData? GetFilterData(long appid)
    {
        if (_filterCache.TryGetValue(appid, out var memo)) return memo;
        string? raw = GetFullDetails(appid);
        if (raw is null) return null;

        try
        {
            using var doc = JsonDocument.Parse(raw);
            var d = doc.RootElement;

            string? type = d.TryGetProperty("type", out var t) ? t.GetString() : null;

            var genres = new List<string>();
            if (d.TryGetProperty("genres", out var g) && g.ValueKind == JsonValueKind.Array)
                foreach (var item in g.EnumerateArray())
                    if (item.TryGetProperty("description", out var desc) && desc.GetString() is { } s)
                        genres.Add(s);

            bool win = false, mac = false, lin = false;
            if (d.TryGetProperty("platforms", out var p) && p.ValueKind == JsonValueKind.Object)
            {
                win = p.TryGetProperty("windows", out var w) && w.ValueKind == JsonValueKind.True;
                mac = p.TryGetProperty("mac", out var m) && m.ValueKind == JsonValueKind.True;
                lin = p.TryGetProperty("linux", out var l) && l.ValueKind == JsonValueKind.True;
            }

            int? year = null;
            DateTime? releaseDate = null;
            string? releaseDateText = null;
            if (d.TryGetProperty("release_date", out var rd) && rd.TryGetProperty("date", out var dateEl) &&
                dateEl.GetString() is { } ds)
            {
                releaseDateText = string.IsNullOrWhiteSpace(ds) ? null : ds.Trim();
                if (DateTime.TryParse(ds, System.Globalization.CultureInfo.InvariantCulture,
                        System.Globalization.DateTimeStyles.None, out var parsed))
                {
                    releaseDate = parsed;
                    year = parsed.Year;
                }
                else
                {
                    var m = Regex.Match(ds, @"\b(19|20)\d{2}\b");
                    if (m.Success && int.TryParse(m.Value, out int y))
                    {
                        year = y;
                        releaseDate = new DateTime(y, 1, 1);
                    }
                }
            }

            bool isFree = d.TryGetProperty("is_free", out var f) && f.ValueKind == JsonValueKind.True;

            int? meta = null;
            if (d.TryGetProperty("metacritic", out var mc) && mc.TryGetProperty("score", out var sc) &&
                sc.TryGetInt32(out int score)) meta = score;

            long? reviews = null;
            if (d.TryGetProperty("recommendations", out var rec) && rec.TryGetProperty("total", out var tot) &&
                tot.TryGetInt64(out long rv)) reviews = rv;

            bool isAdult = false;
            if (d.TryGetProperty("content_descriptors", out var cd) &&
                cd.TryGetProperty("ids", out var ids) && ids.ValueKind == JsonValueKind.Array)
                isAdult = ids.EnumerateArray().Any(e => e.TryGetInt32(out int id) && (id is 3 or 4));

            var result = new AppFilterData(type, genres, win, mac, lin, year, releaseDate, releaseDateText, isFree, meta, reviews, isAdult);
            _filterCache[appid] = result;
            return result;
        }
        catch { return null; }
    }

    public AppOverview? GetOverview(long appid)
    {
        if (_overviewCache.TryGetValue(appid, out var memo)) return memo;

        string? raw = GetFullDetails(appid);
        if (string.IsNullOrWhiteSpace(raw) || raw == "{}") return null;

        try
        {
            using var doc = JsonDocument.Parse(raw);
            var d = doc.RootElement;

            string? blurb = d.TryGetProperty("short_description", out var sd) ? sd.GetString() : null;
            if (!string.IsNullOrWhiteSpace(blurb)) blurb = WebUtility.HtmlDecode(blurb).Trim();

            var overview = new AppOverview(
                string.IsNullOrWhiteSpace(blurb) ? null : blurb,
                ReadStringArray(d, "developers"),
                ReadStringArray(d, "publishers"),
                ReadDescriptions(d, "genres"));

            _overviewCache[appid] = overview;
            return overview;
        }
        catch { return null; }
    }

    private static List<string> ReadStringArray(JsonElement d, string property)
    {
        var list = new List<string>();
        if (d.TryGetProperty(property, out var arr) && arr.ValueKind == JsonValueKind.Array)
            foreach (var item in arr.EnumerateArray())
                if (item.GetString() is { } s && !string.IsNullOrWhiteSpace(s))
                    list.Add(WebUtility.HtmlDecode(s).Trim());
        return list;
    }

    private static List<string> ReadDescriptions(JsonElement d, string property)
    {
        var list = new List<string>();
        if (d.TryGetProperty(property, out var arr) && arr.ValueKind == JsonValueKind.Array)
            foreach (var item in arr.EnumerateArray())
                if (item.TryGetProperty("description", out var desc) && desc.GetString() is { } s)
                    list.Add(WebUtility.HtmlDecode(s).Trim());
        return list;
    }

    public async Task<GameDetails?> ResolveGameDetailsAsync(long appid, CancellationToken ct = default)
    {
        await EnsureFullDetailsAsync(appid, ct);
        return GetGameDetails(appid);
    }

    public GameDetails? GetGameDetails(long appid)
    {
        string? raw = GetFullDetails(appid);
        if (string.IsNullOrWhiteSpace(raw) || raw == "{}") return null;
        try
        {
            using var doc = JsonDocument.Parse(raw);
            var d = doc.RootElement;
            string? name = d.TryGetProperty("name", out var n) ? n.GetString() : null;
            if (string.IsNullOrWhiteSpace(name)) return null;

            var genres = new List<string>();
            if (d.TryGetProperty("genres", out var g) && g.ValueKind == JsonValueKind.Array)
                foreach (var it in g.EnumerateArray())
                    if (it.TryGetProperty("description", out var de) && de.GetString() is { } s) genres.Add(s);

            string? baseApp = null;
            if (d.TryGetProperty("fullgame", out var fg) && fg.ValueKind == JsonValueKind.Object
                && fg.TryGetProperty("appid", out var fa))
                baseApp = fa.ValueKind == JsonValueKind.String ? fa.GetString() : fa.GetRawText();

            return new GameDetails
            {
                Name = name,
                AppId = d.TryGetProperty("steam_appid", out var sa) && sa.TryGetInt64(out var said) ? said : appid,
                Type = d.TryGetProperty("type", out var t) ? t.GetString() ?? "" : "",
                BaseAppId = baseApp,
                Genres = genres,
                HeaderImage = d.TryGetProperty("header_image", out var hi) ? hi.GetString() : null,
                ReleaseDate = d.TryGetProperty("release_date", out var rd)
                    && rd.TryGetProperty("date", out var dt) ? dt.GetString() : null,
            };
        }
        catch { return null; }
    }

    public async Task<bool> EnsureFullDetailsAsync(long appid, CancellationToken ct = default, bool background = false)
    {
        if (HasFullDetails(appid)) return true;

        var url = $"https://store.steampowered.com/api/appdetails?appids={appid}&cc=us&l=english";
        const int maxAttempts = 3;
        for (int attempt = 0; attempt < maxAttempts; attempt++)
        {
            await ThrottleAsync(ct, background);
            try
            {
                using var res = await _http.GetAsync(url, ct);
                if (res.StatusCode is HttpStatusCode.TooManyRequests or HttpStatusCode.Forbidden)
                {
                    if (attempt < maxAttempts - 1) { await Task.Delay(TimeSpan.FromSeconds(4 * (attempt + 1)), ct); continue; }
                    return false;
                }
                if (!res.IsSuccessStatusCode) return false;

                using var doc = JsonDocument.Parse(await res.Content.ReadAsStringAsync(ct));
                if (FindAppDetailsEntry(doc.RootElement, appid) is not { } entry)
                {
                    _unresolvable.TryAdd(appid, 0);
                    return false;
                }

                if (!entry.TryGetProperty("success", out var okEl) || okEl.ValueKind != JsonValueKind.True ||
                    !entry.TryGetProperty("data", out var data) || data.ValueKind != JsonValueKind.Object)
                {
                    await SaveFullDetailsAsync(appid, "{}");
                    return true;
                }

                await SaveFullDetailsAsync(appid, data.GetRawText());

                if (!_cache.ContainsKey(appid) && data.TryGetProperty("name", out var n) && n.GetString() is { } nm && nm.Length > 0)
                {
                    string? image = data.TryGetProperty("header_image", out var img) ? img.GetString() : null;
                    _cache[appid] = new SteamAppInfo(nm, image);
                }
                return true;
            }
            catch (OperationCanceledException) { return false; }
            catch { return false; }
        }
        return false;
    }

    public async Task BackfillFullDetailsAsync(IEnumerable<long> appids, Action? onProgress = null, CancellationToken ct = default)
    {
        foreach (long appid in appids)
        {
            if (ct.IsCancellationRequested) return;
            if (HasFullDetails(appid)) continue;
            await EnsureFullDetailsAsync(appid, ct, background: true);
            onProgress?.Invoke();
            try { await Task.Delay(TimeSpan.FromMilliseconds(750), ct); } catch (OperationCanceledException) { return; }
        }
    }

    private string DetailsPath(long appid) => Path.Combine(_detailsDir, $"{appid}.json");

    private async Task SaveFullDetailsAsync(long appid, string rawJson)
    {
        try
        {
            Directory.CreateDirectory(_detailsDir);
            await File.WriteAllTextAsync(DetailsPath(appid), rawJson);
        }
        catch { /* best effort */ }
    }

    private async Task ThrottleAsync(CancellationToken ct, bool background = false)
    {
        if (!background) Interlocked.Increment(ref _interactiveWaiting);
        try
        {
            await _rateGate.WaitAsync(ct);
            bool held = true;
            try
            {
                while (true)
                {
                    if (background && Volatile.Read(ref _interactiveWaiting) > 0)
                    {
                        _rateGate.Release(); held = false;
                        await Task.Delay(50, ct);
                        await _rateGate.WaitAsync(ct); held = true;
                        continue;
                    }

                    var now = DateTime.UtcNow;
                    while (_requestTimes.Count > 0 && now - _requestTimes.Peek() > Window)
                        _requestTimes.Dequeue();

                    if (_requestTimes.Count < MaxPerWindow)
                    {
                        _requestTimes.Enqueue(now);
                        PersistWindow(now);
                        return;
                    }

                    var wait = _requestTimes.Peek() + Window - now;
                    _rateGate.Release(); held = false;
                    await Task.Delay(wait > TimeSpan.Zero ? wait : TimeSpan.FromMilliseconds(100), ct);
                    await _rateGate.WaitAsync(ct); held = true;
                }
            }
            finally { if (held) _rateGate.Release(); }
        }
        finally
        {
            if (!background) Interlocked.Decrement(ref _interactiveWaiting);
        }
    }

    private void PersistWindow(DateTime now)
    {
        if (now - _lastPersist < TimeSpan.FromSeconds(5)) return;
        _lastPersist = now;
        _cacheStore.SaveSteamApiRequestTimes(_requestTimes.Select(t => new DateTimeOffset(t, TimeSpan.Zero).ToUnixTimeMilliseconds()));
    }
}

/// <summary>Adapter over LuaTools.Core.Persistence.CacheService.</summary>
public sealed class CacheServiceProxy(LuaTools.Core.Persistence.CacheService inner)
{
    public IReadOnlyList<long> GetSteamApiRequestTimes() => inner.GetSteamApiRequestTimes();
    public void SaveSteamApiRequestTimes(IEnumerable<long> times) => inner.SaveSteamApiRequestTimes(times);
    public IReadOnlyList<long> GetHardwareAppIds() => inner.GetHardwareAppIds();
    public long GetHardwareAppIdsFetchedAt() => inner.GetHardwareAppIdsFetchedAt();
    public void SaveHardwareAppIds(IEnumerable<long> ids, long at) => inner.SaveHardwareAppIds(ids, at);
    public IReadOnlyList<long> GetLoadedAppIds() => inner.GetLoadedAppIds();
    public void SaveLoadedAppIds(IEnumerable<long> ids) => inner.SaveLoadedAppIds(ids);
    public IReadOnlyList<DownloadHistoryRecord> GetDownloadHistory() => inner.GetDownloadHistory();
    public void SaveDownloadHistory(IEnumerable<DownloadHistoryRecord> records, int cap = 100) => inner.SaveDownloadHistory(records, cap);
    public string? DepotDownloaderVersion { get => inner.DepotDownloaderVersion; set => inner.DepotDownloaderVersion = value; }
    public long DepotDownloaderCheckedAtMs { get => inner.DepotDownloaderCheckedAtMs; set => inner.DepotDownloaderCheckedAtMs = value; }
}

public sealed class SteamAppListCache
{
    private const string MorrenusUrl = "https://applist.morrenus.xyz/";
    private const string SteamUrl = "https://api.steampowered.com/ISteamApps/GetAppList/v2/";
    private static readonly TimeSpan MaxAge = TimeSpan.FromDays(14);

    private readonly string _cacheFile;
    private readonly HttpClient _http = AppHttp.Create(TimeSpan.FromSeconds(60));
    private readonly ConcurrentDictionary<long, string> _names = new();
    private Task? _loadTask;

    public SteamAppListCache(string dataDir) => _cacheFile = Path.Combine(dataDir, "steam-applist.json");

    public string? GetName(long appid) => _names.TryGetValue(appid, out var n) ? n : null;

    public Task EnsureLoadedAsync() => _loadTask ??= LoadAsync();

    private async Task LoadAsync()
    {
        try
        {
            if (File.Exists(_cacheFile) && DateTime.UtcNow - File.GetLastWriteTimeUtc(_cacheFile) < MaxAge)
            {
                var map = JsonSerializer.Deserialize<Dictionary<long, string>>(await File.ReadAllTextAsync(_cacheFile));
                if (map is { Count: > 0 })
                {
                    foreach (var (k, v) in map) _names[k] = v;
                    return;
                }
            }
        }
        catch { /* fall through to download */ }

        if (await TryDownloadAsync(MorrenusUrl, flatArray: true) ||
            await TryDownloadAsync(SteamUrl, flatArray: false))
        {
            await SaveAsync();
            return;
        }

        try
        {
            if (_names.IsEmpty && File.Exists(_cacheFile))
            {
                var map = JsonSerializer.Deserialize<Dictionary<long, string>>(await File.ReadAllTextAsync(_cacheFile));
                if (map is not null) foreach (var (k, v) in map) _names[k] = v;
            }
        }
        catch { /* give up */ }
    }

    private async Task<bool> TryDownloadAsync(string url, bool flatArray)
    {
        try
        {
            await using var stream = await _http.GetStreamAsync(url);
            using var doc = await JsonDocument.ParseAsync(stream);
            var apps = flatArray
                ? doc.RootElement
                : doc.RootElement.GetProperty("applist").GetProperty("apps");

            foreach (var app in apps.EnumerateArray())
            {
                long id = app.GetProperty("appid").GetInt64();
                string? name = app.GetProperty("name").GetString();
                if (!string.IsNullOrWhiteSpace(name)) _names[id] = name;
            }
            return !_names.IsEmpty;
        }
        catch { return false; }
    }

    private async Task SaveAsync()
    {
        try
        {
            Directory.CreateDirectory(Path.GetDirectoryName(_cacheFile)!);
            await File.WriteAllTextAsync(_cacheFile, JsonSerializer.Serialize(_names));
        }
        catch { /* best effort */ }
    }
}

public sealed class SteamDepotInfo
{
    private static readonly TimeSpan FailureTtl = TimeSpan.FromSeconds(60);

    private readonly HttpClient _http;
    private readonly ConcurrentDictionary<long, (AppDepotInfo? Info, DateTime At)> _cache = new();
    private readonly ConcurrentDictionary<long, Task<AppDepotInfo?>> _inFlight = new();
    internal Func<DateTime> UtcNow { get; init; } = () => DateTime.UtcNow;

    public SteamDepotInfo() : this(AppHttp.SharedHandler) { }
    internal SteamDepotInfo(HttpMessageHandler handler) =>
        _http = new HttpClient(handler) { Timeout = TimeSpan.FromSeconds(15) };

    public Task<AppDepotInfo?> GetAsync(long appId, CancellationToken ct = default)
    {
        if (_cache.TryGetValue(appId, out var hit) &&
            (hit.Info is not null || UtcNow() - hit.At < FailureTtl))
            return Task.FromResult(hit.Info);

        var task = _inFlight.GetOrAdd(appId, id => FetchAsync(id, ct));
        if (task.IsCompleted) _inFlight.TryRemove(appId, out _);
        return task;
    }

    public void Invalidate(long appId)
    {
        _cache.TryRemove(appId, out _);
        _inFlight.TryRemove(appId, out _);
    }

    private async Task<AppDepotInfo?> FetchAsync(long appId, CancellationToken ct)
    {
        try
        {
            using var res = await _http.GetAsync($"https://api.steamcmd.net/v1/info/{appId}", ct);
            if (!res.IsSuccessStatusCode) return Cache(appId, null);

            using var doc = JsonDocument.Parse(await res.Content.ReadAsStringAsync(ct));
            if (!doc.RootElement.TryGetProperty("data", out var data) ||
                !data.TryGetProperty(appId.ToString(), out var app))
                return Cache(appId, null);

            var depots = new List<ContentDepot>();
            if (app.TryGetProperty("depots", out var depotMap) && depotMap.ValueKind == JsonValueKind.Object)
            {
                foreach (var entry in depotMap.EnumerateObject())
                {
                    if (!long.TryParse(entry.Name, out long depotId)) continue;
                    if (entry.Value.ValueKind != JsonValueKind.Object) continue;
                    var v = entry.Value;

                    bool isShared = v.TryGetProperty("depotfromapp", out var fromEl);
                    long? fromAppId = isShared && long.TryParse(fromEl.GetString(), out long fa) ? fa : null;

                    long? dlcAppId = v.TryGetProperty("dlcappid", out var dlcEl) && long.TryParse(dlcEl.GetString(), out long dlc)
                        ? dlc : null;

                    string? os = null, lang = null;
                    if (v.TryGetProperty("config", out var cfg) && cfg.ValueKind == JsonValueKind.Object)
                    {
                        if (cfg.TryGetProperty("oslist", out var osEl)) os = osEl.GetString();
                        if (cfg.TryGetProperty("dlclanguage", out var lEl)) lang = lEl.GetString();
                        else if (cfg.TryGetProperty("language", out var l2)) lang = l2.GetString();
                    }

                    depots.Add(new ContentDepot(depotId, ReadPublicSize(v), dlcAppId, isShared, os, lang,
                        ReadPublicManifestId(v)) { FromAppId = fromAppId });
                }
            }

            string? publicBuildId = null;
            if (app.TryGetProperty("depots", out var dm) && dm.ValueKind == JsonValueKind.Object &&
                dm.TryGetProperty("branches", out var branches) &&
                branches.TryGetProperty("public", out var pubBranch) &&
                pubBranch.TryGetProperty("buildid", out var buildEl))
                publicBuildId = buildEl.ValueKind == JsonValueKind.String
                    ? buildEl.GetString() : buildEl.ToString();

            var dlcIds = new List<long>();
            if (app.TryGetProperty("extended", out var ext) &&
                ext.TryGetProperty("listofdlc", out var listEl) &&
                listEl.GetString() is { } csv)
            {
                foreach (var part in csv.Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
                    if (long.TryParse(part, out long id)) dlcIds.Add(id);
            }

            var launchExes = ParseLaunchExes(app);

            return Cache(appId, new AppDepotInfo(appId, depots, dlcIds, launchExes, publicBuildId));
        }
        catch (OperationCanceledException) { return null; }
        catch { return Cache(appId, null); }
        finally { _inFlight.TryRemove(appId, out _); }
    }

    private static string? ReadPublicManifestId(JsonElement depot)
    {
        if (depot.TryGetProperty("manifests", out var m) &&
            m.TryGetProperty("public", out var pub) &&
            pub.TryGetProperty("gid", out var gidEl))
            return gidEl.ValueKind == JsonValueKind.String ? gidEl.GetString() : gidEl.ToString();
        return null;
    }

    private static long ReadPublicSize(JsonElement depot)
    {
        if (depot.TryGetProperty("manifests", out var m) &&
            m.TryGetProperty("public", out var pub) &&
            pub.TryGetProperty("size", out var sizeEl) &&
            long.TryParse(sizeEl.GetString(), out long size))
            return size;
        return 0;
    }

    private static IReadOnlyList<string> ParseLaunchExes(JsonElement app)
    {
        if (!app.TryGetProperty("config", out var cfg) || cfg.ValueKind != JsonValueKind.Object ||
            !cfg.TryGetProperty("launch", out var launch) || launch.ValueKind != JsonValueKind.Object)
            return Array.Empty<string>();

        var def = new List<string>();
        var other = new List<string>();
        foreach (var entry in launch.EnumerateObject())
        {
            var e = entry.Value;
            if (e.ValueKind != JsonValueKind.Object) continue;
            if (!e.TryGetProperty("executable", out var exeEl) || exeEl.GetString() is not { Length: > 0 } exe)
                continue;

            bool isWindows = true, hasBeta = false, hasOwnsDlc = false;
            if (e.TryGetProperty("config", out var lc) && lc.ValueKind == JsonValueKind.Object)
            {
                if (lc.TryGetProperty("oslist", out var osEl) && osEl.GetString() is { } os)
                    isWindows = os.Contains("windows", StringComparison.OrdinalIgnoreCase);
                hasBeta = lc.TryGetProperty("betakey", out var bk) && !string.IsNullOrWhiteSpace(bk.GetString());
                hasOwnsDlc = lc.TryGetProperty("ownsdlc", out _);
            }
            if (!isWindows || hasBeta || hasOwnsDlc) continue;

            string norm = exe.Replace('/', '\\');
            bool isDefault = e.TryGetProperty("type", out var tEl) &&
                             string.Equals(tEl.GetString(), "default", StringComparison.OrdinalIgnoreCase);
            (isDefault ? def : other).Add(norm);
        }
        return def.Concat(other).Distinct(StringComparer.OrdinalIgnoreCase).ToList();
    }

    private AppDepotInfo? Cache(long appId, AppDepotInfo? info)
    {
        _cache[appId] = (info, UtcNow());
        return info;
    }
}

public sealed class CoverCache
{
    private const int MinValidBytes = 512;
    private const int PlaceholderLength = 9816;
    private const string PlaceholderSha256 =
        "732ec27f2af650fe079f1c83b0bb0c712a322dc175f383504176724675ad2700";

    private readonly string _coversDir;
    private readonly HttpClient _http = AppHttp.Create(TimeSpan.FromSeconds(20));
    private readonly SemaphoreSlim _ioGate = new(1, 1);
    private readonly ConcurrentDictionary<long, Task<string?>> _inFlight = new();
    private readonly ConcurrentDictionary<long, byte> _noCover = new();

    public CoverCache(string dataDir) => _coversDir = Path.Combine(dataDir, "covers");

    private static bool IsHeaderCapsulePlaceholder(byte[] b)
    {
        if (b.Length != PlaceholderLength) return false;
        var hash = Convert.ToHexString(System.Security.Cryptography.SHA256.HashData(b));
        return hash.Equals(PlaceholderSha256, StringComparison.OrdinalIgnoreCase);
    }

    private static string PathFor(string dir, long appid) => Path.Combine(dir, $"{appid}.jpg");

    private static bool IsJpeg(byte[] b) => b.Length >= 3 && b[0] == 0xFF && b[1] == 0xD8 && b[2] == 0xFF;

    public string? GetLocalPath(long appid)
    {
        string p = PathFor(_coversDir, appid);
        if (!File.Exists(p)) return null;

        try
        {
            if (new FileInfo(p).Length == PlaceholderLength &&
                IsHeaderCapsulePlaceholder(File.ReadAllBytes(p)))
            {
                File.Delete(p);
                return null;
            }
        }
        catch { /* if we can't check/delete, just use what's there */ }

        return p;
    }

    public bool IsKnownMissing(long appid) => _noCover.ContainsKey(appid);
    public void MarkMissing(long appid) => _noCover[appid] = 0;

    public Task<string?> EnsureAsync(long appid, string remoteUrl, CancellationToken ct = default)
    {
        string path = PathFor(_coversDir, appid);
        if (File.Exists(path)) return Task.FromResult<string?>(path);
        if (string.IsNullOrWhiteSpace(remoteUrl)) return Task.FromResult<string?>(null);

        return _inFlight.GetOrAdd(appid, _ => DownloadAsync(appid, path, remoteUrl, ct));
    }

    private async Task<string?> DownloadAsync(long appid, string path, string remoteUrl, CancellationToken ct)
    {
        try
        {
            byte[] bytes = await _http.GetByteArrayAsync(remoteUrl, ct);
            if (bytes.Length < MinValidBytes || !IsJpeg(bytes)) return null;
            if (IsHeaderCapsulePlaceholder(bytes)) return null;

            await _ioGate.WaitAsync(ct);
            try
            {
                Directory.CreateDirectory(_coversDir);
                await File.WriteAllBytesAsync(path, bytes, ct);
            }
            finally { _ioGate.Release(); }
            return path;
        }
        catch { return null; }
        finally { _inFlight.TryRemove(appid, out _); }
    }
}

public sealed class HardwareAppIdService
{
    private static readonly TimeSpan MaxAge = TimeSpan.FromDays(14);
    private static readonly JsonSerializerOptions JsonOpts = new() { PropertyNameCaseInsensitive = true };

    private readonly GithubProxy _gh;
    private readonly CacheServiceProxy _cache;
    private readonly HashSet<long> _ids;
    private Task? _loadTask;

    public HardwareAppIdService(GithubProxy gh, CacheServiceProxy cache)
    {
        _gh = gh;
        _cache = cache;
        _ids = new HashSet<long>(cache.GetHardwareAppIds());
    }

    public bool IsBlacklisted(long appId) => _ids.Contains(appId);

    public Task EnsureFreshAsync() => _loadTask ??= RefreshIfStaleAsync();

    private async Task RefreshIfStaleAsync()
    {
        long fetchedAt = _cache.GetHardwareAppIdsFetchedAt();
        bool fresh = fetchedAt > 0 && _ids.Count > 0
            && DateTimeOffset.UtcNow - DateTimeOffset.FromUnixTimeMilliseconds(fetchedAt) < MaxAge;
        if (fresh) return;

        try
        {
            using var res = await _gh.SendAsync(AppConfig.HardwareAppIdListUrl);
            if (res is null || !res.IsSuccessStatusCode) return;

            string body = await res.Content.ReadAsStringAsync();
            var entries = JsonSerializer.Deserialize<List<HardwareApp>>(body, JsonOpts);
            if (entries is null || entries.Count == 0) return;

            var ids = entries.Where(e => e.AppId > 0).Select(e => e.AppId).Distinct().ToList();
            _ids.Clear();
            foreach (long id in ids) _ids.Add(id);
            _cache.SaveHardwareAppIds(ids, DateTimeOffset.UtcNow.ToUnixTimeMilliseconds());
        }
        catch { /* offline → keep the existing set */ }
    }

    private sealed record HardwareApp([property: JsonPropertyName("appid")] long AppId);
}
