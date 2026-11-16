// Derived from madoiscool/LuaTools (MIT) — SettingsService.cs / CacheService.cs.
using System.Text.Json;

namespace LuaTools.Core.Persistence;

public sealed class AppSettings
{
    public string? SteamPathOverride { get; set; }
    public string? SelectedMode { get; set; }
    public bool? AutoUpdateApps { get; set; }
    public bool? DonateKeys { get; set; }
    public int? ManagePageSize { get; set; }
    public int? FixesPageSize { get; set; }
    public int? BuildsPageSize { get; set; }
    public string? Language { get; set; }
    public string? HubcapApiKey { get; set; }
    public bool? StartWithSystem { get; set; }
    public bool? MinimizeToTray { get; set; }
    public bool? FastFetch { get; set; }
    public string? DnsMode { get; set; }
}

/// <summary>settings.json with atomic write + .bak + .corrupt preservation, exactly like the original.</summary>
public sealed class SettingsService
{
    private static readonly JsonSerializerOptions JsonOpts = new() { WriteIndented = true };

    private readonly string _dir;
    private readonly string _filePath;
    private AppSettings _settings = new();

    public SettingsService(string dir)
    {
        _dir = dir;
        _filePath = Path.Combine(dir, "settings.json");
        Load();
    }

    public string? SteamPathOverride
    {
        get => _settings.SteamPathOverride;
        set { _settings.SteamPathOverride = NullIfBlank(value); Save(); }
    }

    public string? SelectedMode
    {
        get => _settings.SelectedMode;
        set { _settings.SelectedMode = NullIfBlank(value); Save(); }
    }

    public bool AutoUpdateApps
    {
        get => _settings.AutoUpdateApps ?? true;
        set { _settings.AutoUpdateApps = value; Save(); }
    }

    public bool DonateKeys
    {
        get => _settings.DonateKeys ?? true;
        set { _settings.DonateKeys = value; Save(); }
    }

    public int ManagePageSize { get => _settings.ManagePageSize ?? 24; set { _settings.ManagePageSize = value; Save(); } }
    public int FixesPageSize { get => _settings.FixesPageSize ?? 24; set { _settings.FixesPageSize = value; Save(); } }
    public int BuildsPageSize { get => _settings.BuildsPageSize ?? 10; set { _settings.BuildsPageSize = value; Save(); } }

    public string? Language
    {
        get => _settings.Language;
        set { _settings.Language = NullIfBlank(value); Save(); }
    }

    public string? HubcapApiKey
    {
        get => _settings.HubcapApiKey;
        set { _settings.HubcapApiKey = NullIfBlank(value); Save(); }
    }

    public bool StartWithSystem
    {
        get => _settings.StartWithSystem ?? false;
        set { _settings.StartWithSystem = value; Save(); }
    }

    public bool MinimizeToTray
    {
        get => _settings.MinimizeToTray ?? false;
        set { _settings.MinimizeToTray = value; Save(); }
    }

    public bool FastFetch
    {
        get => _settings.FastFetch ?? false;
        set { _settings.FastFetch = value; Save(); }
    }

    public string DnsMode
    {
        get => _settings.DnsMode is "Always" or "Never" ? _settings.DnsMode : "Auto";
        set { _settings.DnsMode = value; Save(); }
    }

    private static string? NullIfBlank(string? v) => string.IsNullOrWhiteSpace(v) ? null : v;

    private string TmpPath => _filePath + ".tmp";
    private string BakPath => _filePath + ".bak";

    private void Load()
    {
        if (TryLoad(_filePath)) return;
        PreserveCorrupt(_filePath);
        if (TryLoad(BakPath)) return;
        _settings = new AppSettings();
    }

    private bool TryLoad(string path)
    {
        try
        {
            if (!File.Exists(path)) return false;
            if (JsonSerializer.Deserialize<AppSettings>(File.ReadAllText(path), JsonOpts) is { } loaded)
            {
                _settings = loaded;
                return true;
            }
        }
        catch { /* missing / truncated / invalid JSON → caller falls through */ }
        return false;
    }

    private static void PreserveCorrupt(string path)
    {
        try
        {
            if (File.Exists(path) && new FileInfo(path).Length > 0)
                File.Move(path, path + ".corrupt", overwrite: true);
        }
        catch { /* best effort */ }
    }

    private void Save()
    {
        try
        {
            Directory.CreateDirectory(_dir);
            string json = JsonSerializer.Serialize(_settings, JsonOpts);
            File.WriteAllText(TmpPath, json);
            try { if (File.Exists(_filePath)) File.Copy(_filePath, BakPath, overwrite: true); } catch { /* best effort */ }
            File.Move(TmpPath, _filePath, overwrite: true);
        }
        catch
        {
            // A failed persistence must never crash the caller that flipped a setting.
        }
    }
}

/// <summary>Download history record (persisted shape of a finished download).</summary>
public sealed record DownloadHistoryRecord(
    string Id, string Kind, long AppId, string Title, string SubTitle, long Bytes, string Status,
    string? Message, long CompletedAtMs, string? RevealPath = null);

/// <summary>cache.json — app bookkeeping (not user settings).</summary>
public sealed class CacheData
{
    public string? InstalledPluginTag { get; set; }
    public List<long> SteamApiRequestTimes { get; set; } = new();
    public List<string> DonatedAppIds { get; set; } = new();
    public List<long> HardwareAppIds { get; set; } = new();
    public long HardwareAppIdsFetchedAtMs { get; set; }
    public List<long> LoadedAppIds { get; set; } = new();
    public bool OnboardingComplete { get; set; }
    public List<DownloadHistoryRecord> DownloadHistory { get; set; } = new();
    public string? DepotDownloaderVersion { get; set; }
    public long DepotDownloaderCheckedAtMs { get; set; }
    public string? ToolVersionsJson { get; set; }
}

public sealed class CacheService
{
    private static readonly JsonSerializerOptions JsonOpts = new() { WriteIndented = true };

    private readonly string _dir;
    private readonly string _filePath;
    private readonly object _gate = new();
    private CacheData _cache = new();

    public CacheService(string dir)
    {
        _dir = dir;
        _filePath = Path.Combine(dir, "cache.json");
        Load();
    }

    public string? InstalledPluginTag
    {
        get => _cache.InstalledPluginTag;
        set { _cache.InstalledPluginTag = value; Save(); }
    }

    public IReadOnlyList<long> GetSteamApiRequestTimes() => _cache.SteamApiRequestTimes;
    public void SaveSteamApiRequestTimes(IEnumerable<long> times) { _cache.SteamApiRequestTimes = times.ToList(); Save(); }

    public IReadOnlyCollection<string> GetDonatedAppIds() => _cache.DonatedAppIds;
    public void SaveDonatedAppIds(IEnumerable<string> appIds) { _cache.DonatedAppIds = appIds.Distinct().ToList(); Save(); }

    public IReadOnlyList<long> GetHardwareAppIds() => _cache.HardwareAppIds;
    public long GetHardwareAppIdsFetchedAt() => _cache.HardwareAppIdsFetchedAtMs;
    public void SaveHardwareAppIds(IEnumerable<long> ids, long fetchedAtMs)
    {
        _cache.HardwareAppIds = ids.Distinct().ToList();
        _cache.HardwareAppIdsFetchedAtMs = fetchedAtMs;
        Save();
    }

    public IReadOnlyList<long> GetLoadedAppIds() => _cache.LoadedAppIds;
    public void SaveLoadedAppIds(IEnumerable<long> ids) { _cache.LoadedAppIds = ids.Distinct().ToList(); Save(); }
    public void ClearLoadedAppIds() { _cache.LoadedAppIds.Clear(); Save(); }
    public void RemoveLoadedAppId(long id) { if (_cache.LoadedAppIds.Remove(id)) Save(); }

    public IReadOnlyList<DownloadHistoryRecord> GetDownloadHistory() => _cache.DownloadHistory;
    public void SaveDownloadHistory(IEnumerable<DownloadHistoryRecord> records, int cap = 100)
    {
        _cache.DownloadHistory = records.OrderByDescending(r => r.CompletedAtMs).Take(cap).ToList();
        Save();
    }

    public string? DepotDownloaderVersion
    {
        get => _cache.DepotDownloaderVersion;
        set { _cache.DepotDownloaderVersion = value; Save(); }
    }

    public long DepotDownloaderCheckedAtMs
    {
        get => _cache.DepotDownloaderCheckedAtMs;
        set { _cache.DepotDownloaderCheckedAtMs = value; Save(); }
    }

    public bool OnboardingComplete
    {
        get => _cache.OnboardingComplete;
        set { _cache.OnboardingComplete = value; Save(); }
    }

    private void Load()
    {
        try
        {
            if (File.Exists(_filePath))
                _cache = JsonSerializer.Deserialize<CacheData>(File.ReadAllText(_filePath), JsonOpts) ?? new CacheData();
        }
        catch { _cache = new CacheData(); }
    }

    private void Save()
    {
        lock (_gate)
        {
            try
            {
                Directory.CreateDirectory(_dir);
                string tmp = _filePath + ".tmp";
                File.WriteAllText(tmp, JsonSerializer.Serialize(_cache, JsonOpts));
                File.Move(tmp, _filePath, overwrite: true);
            }
            catch { /* best effort; same shape as the original */ }
        }
    }
}
