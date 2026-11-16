// LuaTools Nova — Add page (DownloadViewModel) + Downloads page VMs (ported from the MIT original).
using System.Collections.ObjectModel;
using System.IO;
using System.IO.Compression;
using System.Text.RegularExpressions;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using LuaTools.Core.Downloads;
using LuaTools.Core.Lua;
using LuaTools.Core.Models;
using LuaTools.Core.Services;
using LuaTools.Infrastructure.Clients;
using LuaTools.Infrastructure.Downloads;
using LuaTools.Infrastructure.Network;

namespace LuaTools.Application.ViewModels;

public partial class SourceRowViewModel : ObservableObject
{
    private readonly DownloadViewModel _parent;

    public string Name { get; }
    public string DisplayName { get; }
    public string Status { get; }
    public string? DiscordUrl { get; }
    public bool NeedsKey { get; }

    public bool IsAvailable => Status == "available";
    public string StatusLabel => Status.ToUpperInvariant();
    public bool ShowStatus => Status != "unknown";

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(CanDownload))]
    private bool _isLocked;

    [ObservableProperty] private string? _statsText;
    [ObservableProperty] private bool _isSupporter;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(CanDownload))]
    private DownloadItem? _queueItem;

    public bool CanDownload => IsAvailable && !IsLocked && QueueItem?.IsActive != true;

    public SourceRowViewModel(DownloadViewModel parent, string name, string status)
    {
        _parent = parent;
        Name = name;
        Status = status;
        var meta = SourceMeta.Get(name);
        DisplayName = meta.DisplayName ?? name;
        DiscordUrl = meta.DiscordUrl;
        NeedsKey = meta.RequiresUserKey;
    }

    [RelayCommand]
    private async Task DownloadAsync() => await _parent.DownloadFromSourceAsync(this);

    [RelayCommand]
    private void OpenDiscord()
    {
        if (DiscordUrl is not null)
            _parent._svc.Integrations.OpenUrl(DiscordUrl);
    }
}

public record DiffRow(string Title, string Meta, bool IsDlc, bool IsShared, string SteamDbUrl);
public record FeaturedItem(long AppId, string Name, string? Image);

public partial class DownloadViewModel : ObservableObject
{
    internal readonly NovaServices _svc;
    private CancellationTokenSource? _searchCts;
    private CancellationTokenSource? _detailsCts;

    private IReadOnlyDictionary<long, ContentDepot> _depotsById = new Dictionary<long, ContentDepot>();

    public Func<Task>? RequestSignIn { get; set; }
    public Action<long>? NavigateToGame { get; set; }

    public ObservableCollection<SteamSearchResult> SearchResults { get; } = new();
    public ObservableCollection<SourceRowViewModel> Sources { get; } = new();
    public ObservableCollection<DlcDepot> DlcDepots { get; } = new();
    public ObservableCollection<FeaturedItem> TopSellers { get; } = new();
    public ObservableCollection<FeaturedItem> NewReleases { get; } = new();

    public bool HasTopSellers => TopSellers.Count > 0;
    public bool HasNewReleases => NewReleases.Count > 0;

    public bool ShowFeatured =>
        (TopSellers.Count > 0 || NewReleases.Count > 0)
        && string.IsNullOrWhiteSpace(SearchText)
        && !HasDetails && !HasSources && !HasInstallResult;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(ShowFeatured))]
    private string _searchText = "";
    [ObservableProperty] private bool _isSearching;
    [ObservableProperty] private bool _isResultsOpen;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(HasDetails), nameof(ShowFeatured), nameof(DetailsHeaderUrl))]
    [NotifyCanExecuteChangedFor(nameof(FetchCommand))]
    private GameDetails? _details;

    /// <summary>Header art for the details card (original binds Details.HeaderImage the same way).</summary>
    public string? DetailsHeaderUrl => Details?.HeaderImage;

    // ── Store price (v1.0.3 feature: Steam store pricing in the details card) ──
    [ObservableProperty] private string? _priceFinal;
    [ObservableProperty] private string? _priceInitial;
    [NotifyPropertyChangedFor(nameof(HasDiscount), nameof(PriceLine))]
    [ObservableProperty] private bool _priceUnavailable;
    public bool HasDiscount => PriceInitial is not null;
    public string? PriceLine => PriceUnavailable ? "Price unavailable"
        : PriceFinal is null ? null
        : HasDiscount ? $"{PriceInitial} → {PriceFinal}"
        : PriceFinal;

    // (_details already carries NotifyPropertyChangedFor/NotifyCanExecuteChangedFor — the command
    //  re-queries and HasDetails/ShowFeatured/DetailsHeaderUrl recompute on every change.)
    partial void OnDetailsChanged(GameDetails? value)
    {
        PriceFinal = PriceInitial = null;
        PriceUnavailable = false;
        if (value is null) return;
        _ = LoadPriceAsync(value.AppId);
    }

    private async Task LoadPriceAsync(long appId)
    {
        try
        {
            var price = await _svc.Store.GetPriceAsync(appId);
            // Ignore stale results if the selection changed while loading.
            if (Details is not { } d || d.AppId != appId) return;
            if (price is null) { PriceUnavailable = true; return; }
            PriceFinal = price.IsFree ? "Free" : price.FinalFormatted;
            PriceInitial = price.InitialFormatted;
            PriceUnavailable = false;
        }
        catch { PriceUnavailable = true; } // honest: never fabricate a price
    }

    public bool HasDetails => Details is not null;
    public string GenresText => Details is null ? "" : string.Join(", ", Details.Genres);

    [ObservableProperty]
    [NotifyCanExecuteChangedFor(nameof(FetchCommand))]
    private bool _isChecking;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(HasSources), nameof(ShowFeatured))]
    private bool _sourcesLoaded;

    public bool HasSources => SourcesLoaded && Sources.Count > 0;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(HasDlcInfo), nameof(DlcComplete), nameof(CanGenerateDlc))]
    private DlcInfo? _dlcInfo;

    public bool HasDlcInfo => DlcInfo is not null;
    public bool DlcComplete => DlcInfo?.MissingCount == 0;
    public bool CanGenerateDlc => DlcInfo is not null && (DlcInfo.HaveCount > 0 || DlcInfo.MissingCount == 0);
    public bool IsGenerating => DlcQueueItem?.IsActive == true;
    [ObservableProperty] private string? _error;

    [ObservableProperty] private string? _installStatus;
    [ObservableProperty] private bool _installFailed;
    public bool HasInstallResult => InstallStatus is not null;

    private long? _installedAppId;
    private bool _silentInstall;
    private DownloadItem? _lastEnqueued;
    private readonly SemaphoreSlim _confirmGate = new(1, 1);
    private TaskCompletionSource<bool>? _pendingConfirm;
    private bool _suppressSearch;
    private string? _fastFetchSource;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(HasDiffAdded))]
    private List<DiffRow> _diffAdded = [];
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(HasDiffRemoved))]
    private List<DiffRow> _diffRemoved = [];
    [ObservableProperty] private bool _isConfirmingOverwrite;
    [ObservableProperty] private string _confirmTitle = "";
    [ObservableProperty] private string? _confirmNoChanges;

    public bool HasDiffAdded => DiffAdded.Count > 0;
    public bool HasDiffRemoved => DiffRemoved.Count > 0;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(IsGenerating))]
    private DownloadItem? _dlcQueueItem;

    [ObservableProperty] private bool _fastFetch;
    partial void OnFastFetchChanged(bool value) => _svc.Settings.FastFetch = value;

    public void SyncFastFetch() => FastFetch = _svc.Settings.FastFetch;

    public DropInstallViewModel Drop { get; }

    public DownloadViewModel(NovaServices svc)
    {
        _svc = svc;
        Drop = new DropInstallViewModel(svc);
        _fastFetch = svc.Settings.FastFetch;
    }

    public async Task ProtocolInstall(long appId, Action<string, bool>? onComplete = null)
    {
        _silentInstall = onComplete is not null;
        _lastEnqueued = null;
        // v1.0.3 bug 2: this used to set FastFetch = true, whose setter PERSISTS to settings.json —
        // one protocol launch permanently switched the user's Fetch flow to silent auto-download
        // (the app then "proceeds directly" instead of showing the source/confirm interface).
        // Flip it only for the duration of the protocol flow, then restore the user's choice.
        bool savedFastFetch = FastFetch;
        FastFetch = true;
        _suppressSearch = true;
        SearchText = appId.ToString();
        _suppressSearch = false;
        ResetResults();
        IsResultsOpen = false;

        try
        {
            Details = await _svc.Api.GetDetailsAsync(appId.ToString());
        }
        catch
        {
            onComplete?.Invoke("Something went wrong. Check your connection and try again.", true);
            _silentInstall = false;
            return;
        }

        if (HasDetails)
            await FetchCommand.ExecuteAsync(null);

        if (onComplete is not null)
        {
            var item = _lastEnqueued;
            if (item is not null) await item.Completion;

            if (InstallStatus is not null)
                onComplete(InstallStatus, InstallFailed);
            else
                onComplete(Error ?? "Something went wrong. Check your connection and try again.", true);
            _silentInstall = false;
        }

        FastFetch = savedFastFetch; // restore the user's persisted choice
    }

    public async Task StartPluginAddAsync(long appId)
    {
        _silentInstall = true;
        _suppressSearch = true;
        SearchText = appId.ToString();
        _suppressSearch = false;
        ResetResults();
        IsResultsOpen = false;
        InstallStatus = null;
        Error = null;
        try { Details = await _svc.Api.GetDetailsAsync(appId.ToString()); }
        catch { Error = "Something went wrong. Check your connection and try again."; return; }
        if (HasDetails) await FetchCommand.ExecuteAsync(null);
    }

    public Task DownloadSourceByNameAsync(string name)
    {
        _silentInstall = true;
        var row = Sources.FirstOrDefault(s =>
            string.Equals(s.Name, name, StringComparison.OrdinalIgnoreCase)
            || string.Equals(s.DisplayName, name, StringComparison.OrdinalIgnoreCase));
        return row is null ? Task.CompletedTask : DownloadFromSourceAsync(row);
    }

    public void SeedSearch(long appId)
    {
        _suppressSearch = true;
        SearchText = appId.ToString();
        _suppressSearch = false;
        ResetResults();
        Details = null;
        IsResultsOpen = false;
        _ = FetchDetailsDebouncedAsync(appId.ToString());
    }

    private async Task<bool> PromptSignInIfGuestAsync(string message)
    {
        if (_svc.Auth.IsSignedIn) return false;
        Error = message;
        _svc.Get<IToastService>().Show("Sign in required", message);
        if (RequestSignIn is not null) await RequestSignIn();
        return true;
    }

    partial void OnSearchTextChanged(string value)
    {
        if (_suppressSearch) return;
        ResetResults();
        Details = null;
        if (string.IsNullOrWhiteSpace(value)) InstallStatus = null;

        string? appid = ExtractAppId(value);
        if (appid is not null)
        {
            IsResultsOpen = false;
            _ = FetchDetailsDebouncedAsync(appid);
            return;
        }
        _ = SearchDebouncedAsync(value);
    }

    private async Task SearchDebouncedAsync(string query)
    {
        _searchCts?.Cancel();
        var cts = _searchCts = new CancellationTokenSource();
        try
        {
            await Task.Delay(350, cts.Token);
            if (string.IsNullOrWhiteSpace(query) || query.Length > 100)
            {
                SearchResults.Clear();
                IsResultsOpen = false;
                return;
            }

            IsSearching = true;
            var results = await _svc.Api.SearchAsync(query.Trim(), cts.Token);
            if (cts.Token.IsCancellationRequested) return;

            SearchResults.Clear();
            foreach (var r in results)
                if (!_svc.Hardware.IsBlacklisted(r.AppId)) SearchResults.Add(r);
            IsResultsOpen = SearchResults.Count > 0;
        }
        catch (OperationCanceledException) { }
        catch (ApiException ex) { Error = ex.Message; }
        catch { /* network hiccup during typing */ }
        finally { if (_searchCts == cts) IsSearching = false; }
    }

    private async Task FetchDetailsDebouncedAsync(string appid)
    {
        _detailsCts?.Cancel();
        var cts = _detailsCts = new CancellationTokenSource();
        try
        {
            await Task.Delay(400, cts.Token);
            Details = await _svc.Api.GetDetailsAsync(appid, cts.Token);
        }
        catch (OperationCanceledException) { }
        catch { Details = null; }
    }

    [RelayCommand]
    private async Task SelectResultAsync(SteamSearchResult result)
    {
        _suppressSearch = true;
        SearchText = result.Name;
        _suppressSearch = false;
        IsResultsOpen = false;
        ResetResults();
        _detailsCts?.Cancel();
        var cts = _detailsCts = new CancellationTokenSource();
        try { Details = await _svc.Api.GetDetailsAsync(result.AppId.ToString(), cts.Token); }
        catch (OperationCanceledException) { }
        catch { Details = null; }
    }

    [RelayCommand]
    private async Task SelectFeaturedAsync(FeaturedItem item)
    {
        if (item is null) return;
        _suppressSearch = true;
        SearchText = item.Name;
        _suppressSearch = false;
        IsResultsOpen = false;
        ResetResults();
        _detailsCts?.Cancel();
        var cts = _detailsCts = new CancellationTokenSource();
        try { Details = await _svc.Api.GetDetailsAsync(item.AppId.ToString(), cts.Token); }
        catch (OperationCanceledException) { }
        catch { Details = null; }
    }

    public async Task LoadFeaturedAsync()
    {
        if (TopSellers.Count > 0 || NewReleases.Count > 0) return;
        await _svc.Hardware.EnsureFreshAsync();
        var (top, fresh) = await _svc.Api.GetFeaturedAsync();
        foreach (var i in top)
            if (!_svc.Hardware.IsBlacklisted(i.Id)) TopSellers.Add(new FeaturedItem(i.Id, i.Name, i.LargeCapsuleImage));
        foreach (var i in fresh)
            if (!_svc.Hardware.IsBlacklisted(i.Id)) NewReleases.Add(new FeaturedItem(i.Id, i.Name, i.LargeCapsuleImage));
        OnPropertyChanged(nameof(HasTopSellers));
        OnPropertyChanged(nameof(HasNewReleases));
        OnPropertyChanged(nameof(ShowFeatured));
    }

    [RelayCommand] private void CloseResults() => IsResultsOpen = false;

    private bool CanFetch() => HasDetails && !IsChecking;

    [RelayCommand(CanExecute = nameof(CanFetch))]
    private async Task FetchAsync()
    {
        if (Details is null) return;
        if (Details.IsDlc && await PromptSignInIfGuestAsync("Sign in with Discord to view DLC unlocks.")) return;
        ResetResults();
        IsChecking = true;
        try
        {
            if (Details.IsDlc)
            {
                if (Details.BaseAppId is null)
                {
                    Error = "Could not resolve the base game for this DLC";
                    return;
                }
                DlcInfo = await _svc.Api.GetDlcInfoAsync(Details.AppId.ToString(), Details.BaseAppId);
                DlcDepots.Clear();
                foreach (var d in DlcInfo?.Depots.OrderByDescending(d => d.Included) ?? Enumerable.Empty<DlcDepot>())
                    DlcDepots.Add(d);
            }
            else
            {
                Dictionary<string, string> statuses;
                bool backendFailed = false;
                try
                {
                    statuses = await _svc.Api.CheckSourcesAsync(Details.AppId.ToString());
                }
                catch (ApiException ex)
                {
                    // v1.0.3: distinct message for backend/network failure (was "No available sources").
                    Error = ex.Message;
                    NovaLog.Warn("fetch", $"source check failed for {Details.AppId}: {ex.Message}");
                    backendFailed = true;
                    statuses = new Dictionary<string, string>();
                }
                await AddHubcapSourceAsync(statuses, Details.AppId.ToString());

                foreach (var (name, status) in statuses.OrderByDescending(kv => SourceMeta.Get(kv.Key).RequiresUserKey ? 1 : 0))
                    Sources.Add(new SourceRowViewModel(this, name, status));

                await ApplyHubcapStateAsync();

                if (FastFetch)
                {
                    var best = Sources.FirstOrDefault(s => s.CanDownload);
                    if (best is null)
                    {
                        bool signedIn = !_svc.Auth.IsGuest;
                        bool hasKey = !string.IsNullOrEmpty(_svc.Settings.HubcapApiKey);
                        var gated = Sources.Any(s => s.IsAvailable);
                        var outcome = SourceDiagnostics.Classify(
                            statuses, backendFailed, anyRowCanDownload: false,
                            anyRowGated: gated && (signedIn ? !hasKey : true));
                        Error = outcome.Message;
                        return;
                    }
                    _fastFetchSource = best.DisplayName;
                    await DownloadFromSourceAsync(best);
                }
                else
                {
                    SourcesLoaded = true;
                    await RefreshStandardUsageAsync();
                }
            }
        }
        catch (ApiException ex) { Error = ex.Message; }
        catch { Error = "Something went wrong. Check your connection and try again."; }
        finally { IsChecking = false; }
    }

    private const string HubcapSourceName = "Sadie (Morrenus)";

    private async Task AddHubcapSourceAsync(Dictionary<string, string> statuses, string appid)
    {
        string? key = _svc.Settings.HubcapApiKey;
        if (string.IsNullOrEmpty(key))
        {
            statuses[HubcapSourceName] = "unknown";
            return;
        }
        var status = await _svc.Hubcap.CheckStatusAsync(key, appid);
        statuses[HubcapSourceName] = status?.ManifestFileExists == true ? "available" : "unavailable";
    }

    private async Task ApplyHubcapStateAsync()
    {
        var keyRows = Sources.Where(s => s.NeedsKey).ToList();
        if (keyRows.Count == 0) return;

        string? key = _svc.Settings.HubcapApiKey;
        if (string.IsNullOrEmpty(key))
        {
            foreach (var row in keyRows) row.IsLocked = true;
            return;
        }

        var stats = await _svc.Hubcap.GetStatsAsync(key);
        foreach (var row in keyRows)
        {
            row.IsLocked = stats?.CanMakeRequests != true;
            if (stats is not null)
                row.StatsText = $"{stats.DailyUsage}/{stats.DailyLimit}";
        }
    }

    public async Task RefreshStandardUsageAsync()
    {
        var standardRows = Sources.Where(s => !s.NeedsKey).ToList();
        if (standardRows.Count == 0) return;
        if (_svc.Auth.IsGuest) return;

        var usageTask = _svc.Api.GetStandardUsageAsync();
        var supporterTask = _svc.Api.GetSupporterStatusAsync();
        await Task.WhenAll(usageTask, supporterTask);

        var usage = usageTask.Result;
        bool isSupporter = supporterTask.Result?.IsSupporter == true;

        foreach (var row in standardRows)
        {
            row.IsSupporter = isSupporter;
            if (usage is null) continue;
            row.StatsText = isSupporter ? $"{usage.Used} / Unlimited" : $"{usage.Used}/{usage.Limit}";
        }
    }

    public async Task<DownloadItem?> DownloadFromSourceAsync(SourceRowViewModel source)
    {
        if (Details is null) return null;

        bool hubcapWithKey = source.NeedsKey && !string.IsNullOrEmpty(_svc.Settings.HubcapApiKey);
        if (!hubcapWithKey && await PromptSignInIfGuestAsync("Sign in with Discord to download.")) return null;

        Error = null;
        InstallStatus = null;

        long appId = Details.AppId;
        string gameName = Details.Name;
        bool needsKey = source.NeedsKey;

        var job = _svc.Jobs.CreateManifestJob(
            appId, gameName, source.Name, needsKey,
            confirm: _silentInstall ? null : (file, _, ct) => ConfirmOverwriteAsync(file, appId, gameName, ct),
            onFinished: (item, result) => OnManifestFinished(item, result, needsKey),
            onReveal: () => NavigateToGame?.Invoke(appId));

        var queued = _svc.Queue.Enqueue(job);
        source.QueueItem = queued;
        _lastEnqueued = queued;
        return queued;
    }

    [RelayCommand]
    private async Task RestartSteamAsync()
    {
        if (!await _svc.Get<IDialogService>().ConfirmAsync("Restart Steam",
                "Restart Steam now? This closes Steam and any running games, then relaunches it."))
            return;
        bool ok = await Task.Run(() => _svc.ProcessControl.RestartSteam());
        if (!ok) await _svc.Get<IDialogService>().InfoAsync("Restart Steam", "Couldn't find or launch Steam. Set its location in Settings.");
        else _svc.Get<IToastService>().Show("Steam", "Steam restarted. Library changes are now active.");
    }

    [RelayCommand]
    private async Task GenerateDlcAsync()
    {
        if (Details?.BaseAppId is null) return;
        if (await PromptSignInIfGuestAsync("Sign in with Discord to download.")) return;

        Error = null;
        InstallStatus = null;

        long appId = Details.AppId;
        var job = _svc.Jobs.CreateDlcJob(
            appId, Details.BaseAppId, Details.Name,
            onFinished: (item, result) => OnManifestFinished(item, result, false),
            onReveal: () => NavigateToGame?.Invoke(appId));

        DlcQueueItem = _lastEnqueued = _svc.Queue.Enqueue(job);
    }

    private void OnManifestFinished(DownloadItem item, JobResult? result, bool needsKey)
    {
        _installedAppId = item.AppId;

        if (result is null)
        {
            if (item.Status == DownloadStatus.Failed) Error = item.Message;
            else { InstallStatus = item.Message; InstallFailed = false; }
        }
        else
        {
            InstallFailed = !result.Ok;
            InstallStatus = result.Message;
            if (result.Ok && _fastFetchSource is not null)
            {
                InstallStatus += $" · via {_fastFetchSource}";
                _fastFetchSource = null;
            }
            // v1.0.3 bug 1: the lua file IS installed, but Steam only shows library entries from
            // config/stplug-in after a restart (the original says the same). Say it explicitly
            // and point at the exact Steam root so a wrong-root situation is diagnosable.
            if (result.Ok)
            {
                string? root = _svc.Steam.EffectivePath;
                InstallStatus += $" · Restart Steam to see it in your library"
                    + (root is null ? "" : $" (Steam root: {root})");
            }
        }

        _ = needsKey ? ApplyHubcapStateAsync() : RefreshStandardUsageAsync();
    }

    private async Task<bool> ConfirmOverwriteAsync(
        DownloadedFile file, long appId, string gameName, CancellationToken ct)
    {
        string? existing = _svc.Installer.ReadInstalledLua(appId);
        if (existing is null) return true;

        await _confirmGate.WaitAsync(ct);
        try
        {
            var oldLua = LuaFileParser.Parse(existing, appId);
            var newLua = ExtractLuaFromZip(file.FilePath, appId);
            if (newLua is null) return true;

            var diff = LuaFileParser.Diff(oldLua, newLua);
            var tcs = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);

            BuildDiffUi(diff, appId, gameName, tcs);

            await using var reg = ct.Register(() => tcs.TrySetResult(false));
            bool answer = await tcs.Task;

            if (ReferenceEquals(_pendingConfirm, tcs))
            {
                _pendingConfirm = null;
                IsConfirmingOverwrite = false;
            }
            return answer;
        }
        finally { _confirmGate.Release(); }
    }

    private void BuildDiffUi(LuaDiff diff, long appId, string gameName, TaskCompletionSource<bool> tcs)
    {
        _ = Task.Run(async () =>
        {
            await _svc.AppList.EnsureLoadedAsync();
            _depotsById = await BuildDepotLookupAsync(appId);
            DiffAdded = diff.Added.Select(ToDiffRow).ToList();
            DiffRemoved = diff.Removed.Select(ToDiffRow).ToList();
            ConfirmTitle = $"Replace \"{gameName}\"?";
            ConfirmNoChanges = diff.HasChanges ? null : "No depot/DLC changes. Same content.";
            _pendingConfirm = tcs;
            IsConfirmingOverwrite = true;
        });
    }

    [RelayCommand]
    private void ConfirmOverwrite()
    {
        IsConfirmingOverwrite = false;
        _pendingConfirm?.TrySetResult(true);
    }

    [RelayCommand]
    private void CancelOverwrite()
    {
        IsConfirmingOverwrite = false;
        _pendingConfirm?.TrySetResult(false);
    }

    private async Task<IReadOnlyDictionary<long, ContentDepot>> BuildDepotLookupAsync(long appId)
    {
        var map = new Dictionary<long, ContentDepot>();
        var info = await _svc.DepotInfo.GetAsync(appId);
        if (info is null) return map;
        foreach (var d in info.Depots)
        {
            map[d.Id] = d;
            if (d.DlcAppId is { } dlc) map[dlc] = d;
        }
        return map;
    }

    private string? DiffDisplayName(LuaEntry e)
    {
        var d = _depotsById.GetValueOrDefault(e.Id);
        if (d?.DlcAppId is { } dlc)
        {
            string? steamName = _svc.AppList.GetName(dlc) ?? _svc.AppInfo.GetCached(dlc)?.Name;
            if (steamName is not null) return steamName;
        }
        return _svc.AppList.GetName(e.Id) ?? _svc.AppInfo.GetCached(e.Id)?.Name ?? e.Comment;
    }

    private DiffRow ToDiffRow(LuaEntry e)
    {
        var d = _depotsById.GetValueOrDefault(e.Id);
        bool isDlc = d?.IsDlc ?? false;
        bool isShared = d?.IsShared ?? false;

        string title = DiffDisplayName(e)
            ?? (isDlc ? $"DLC {d!.DlcAppId}"
                : isShared ? "Shared depot"
                : e.HasKey ? "Depot"
                : $"DLC {e.Id}");

        var meta = new List<string> { e.Id.ToString() };
        if (d is { Size: > 0 }) meta.Add(FormatSize(d.Size));
        if (!string.IsNullOrWhiteSpace(d?.Os)) meta.Add(PrettyOs(d!.Os!));
        if (!string.IsNullOrWhiteSpace(d?.Language)) meta.Add(d!.Language!);

        string url = d?.DlcAppId is { } dlcId
            ? $"https://steamdb.info/app/{dlcId}/"
            : $"https://steamdb.info/depot/{e.Id}/";

        return new DiffRow(title, string.Join("  ·  ", meta), isDlc, isShared, url);
    }

    [RelayCommand]
    private void OpenSteamDb(DiffRow row) => _svc.Integrations.OpenUrl(row.SteamDbUrl);

    private static string FormatSize(long bytes)
    {
        if (bytes <= 0) return "";
        double gb = bytes / 1024d / 1024d / 1024d;
        if (gb >= 1) return $"{gb:0.##} GB";
        return $"{bytes / 1024d / 1024d:0.#} MB";
    }

    private static string PrettyOs(string os) => os switch
    {
        "windows" => "Windows",
        "macos" or "macosx" => "macOS",
        "linux" => "Linux",
        _ => os
    };

    private static LuaContents? ExtractLuaFromZip(string zipPath, long appId)
    {
        try
        {
            if (!ManifestJobFactory.IsZip(zipPath)) return LuaFileParser.Parse(zipPath, appId);

            using var archive = ZipFile.OpenRead(zipPath);
            var luaEntry = archive.Entries.FirstOrDefault(e =>
                e.Name.EndsWith(".lua", StringComparison.OrdinalIgnoreCase));
            if (luaEntry is null) return null;

            string tmp = Path.Combine(Path.GetTempPath(), $"luatools_diff_{appId}_{Guid.NewGuid():N}.lua");
            luaEntry.ExtractToFile(tmp, overwrite: true);
            try { return LuaFileParser.Parse(tmp, appId); }
            finally { try { File.Delete(tmp); } catch { } }
        }
        catch { return null; }
    }

    [RelayCommand]
    private void RevealInstalled()
    {
        if ((_installedAppId ?? Details?.AppId) is { } appId) NavigateToGame?.Invoke(appId);
    }

    private void ResetResults()
    {
        Sources.Clear();
        SourcesLoaded = false;
        DlcInfo = null;
        DlcDepots.Clear();
        Error = null;
        _fastFetchSource = null;
    }

    private static string? ExtractAppId(string input)
    {
        string trimmed = input.Trim();
        if (Regex.IsMatch(trimmed, @"^\d{5,}$")) return trimmed;
        var m = Regex.Match(trimmed, @"/app/(\d+)", RegexOptions.IgnoreCase);
        return m.Success ? m.Groups[1].Value : null;
    }
}

/// <summary>Drag-and-drop installer used on Home and Add (ported from DropInstallViewModel).</summary>
public partial class DropInstallViewModel : ObservableObject
{
    private readonly NovaServices _svc;
    public event Action? Installed;

    private IReadOnlyDictionary<long, ContentDepot> _depotsById = new Dictionary<long, ContentDepot>();

    [ObservableProperty] private bool _isDragOver;
    [ObservableProperty] private string? _resultText;
    [ObservableProperty] private bool _resultFailed;
    public bool HasResult => ResultText is not null;

    [ObservableProperty] private bool _isConfirming;
    [ObservableProperty] private string _confirmTitle = "";
    [ObservableProperty] private string? _confirmNoChanges;
    [ObservableProperty] private List<DiffRow> _added = [];
    [ObservableProperty] private List<DiffRow> _removed = [];
    public bool HasAdded => Added.Count > 0;
    public bool HasRemoved => Removed.Count > 0;

    public Func<long, Task>? InstallByAppId { get; set; }

    private Queue<string>? _queue;
    private (string luaPath, long appId)? _pendingLua;

    private struct InstallTally
    {
        public int Luas, Manifests, Failed;
        public List<string> Errors = new(), Skipped = new();
        public InstallTally() { }
    }
    private InstallTally _tally;

    public DropInstallViewModel(NovaServices svc) => _svc = svc;

    public async Task<bool> TryHandleLinkAsync(string? text)
    {
        if (SteamLinkParser.AppIdFrom(text) is not { } appId || InstallByAppId is null) return false;
        await InstallByAppId(appId);
        return true;
    }

    public async Task HandleDropAsync(IEnumerable<string> paths)
    {
        var files = paths.Where(LuaInstaller.IsInstallable).ToList();
        if (files.Count == 0)
        {
            ResultFailed = true;
            ResultText = "Nothing to install. Drop .lua, .manifest, or .zip files.";
            return;
        }

        ResultText = null;
        _tally = new InstallTally();
        _queue = new Queue<string>(files);
        await ProcessQueueAsync();
    }

    private async Task ProcessQueueAsync()
    {
        while (_queue is { Count: > 0 })
        {
            string path = _queue.Dequeue();
            string ext = Path.GetExtension(path);

            if (ext.Equals(".manifest", StringComparison.OrdinalIgnoreCase))
            {
                Apply(_svc.Installer.InstallManifestFile(path));
            }
            else if (ext.Equals(".lua", StringComparison.OrdinalIgnoreCase))
            {
                long? appId = LuaInstaller.AppIdFromFileName(path);
                if (appId is null) { _tally.Skipped.Add(Path.GetFileName(path)); continue; }
                if (await ConfirmIfOverwriteAsync(path, appId.Value, isZip: false)) return;
                Apply(_svc.Installer.InstallLuaFile(path, appId.Value));
            }
            else
            {
                long? appId = LuaInstaller.AppIdForZip(path);
                if (appId is null) { _tally.Skipped.Add(Path.GetFileName(path)); continue; }
                if (await ConfirmIfOverwriteAsync(path, appId.Value, isZip: true)) return;
                Apply(_svc.Installer.InstallZip(path, appId.Value));
            }
        }
        FinishBatch();
    }

    private async Task<bool> ConfirmIfOverwriteAsync(string path, long appId, bool isZip)
    {
        string? existing = _svc.Installer.ReadInstalledLua(appId);
        if (existing is null) return false;

        var oldLua = LuaFileParser.Parse(existing, appId);
        var newLua = isZip ? ExtractLuaFromZip(path, appId) : LuaFileParser.Parse(path, appId);
        if (newLua is null) return false;

        var diff = LuaFileParser.Diff(oldLua, newLua);
        await _svc.AppList.EnsureLoadedAsync();

        _depotsById = await BuildDepotLookupAsync(appId);

        Added = diff.Added.Select(ToDiffRow).ToList();
        Removed = diff.Removed.Select(ToDiffRow).ToList();
        ConfirmTitle = $"Replace \"{NameFor(appId)}\"?";
        ConfirmNoChanges = diff.HasChanges ? null : "No depot/DLC changes. Same content.";
        _pendingLua = (path, appId);
        IsConfirming = true;
        return true;
    }

    private async Task<IReadOnlyDictionary<long, ContentDepot>> BuildDepotLookupAsync(long appId)
    {
        var map = new Dictionary<long, ContentDepot>();
        var info = await _svc.DepotInfo.GetAsync(appId);
        if (info is null) return map;
        foreach (var d in info.Depots)
        {
            map[d.Id] = d;
            if (d.DlcAppId is { } dlc) map[dlc] = d;
        }
        return map;
    }

    [RelayCommand]
    private async Task ConfirmOverwrite()
    {
        IsConfirming = false;
        if (_pendingLua is { } p)
        {
            string ext = Path.GetExtension(p.luaPath);
            Apply(ext.Equals(".zip", StringComparison.OrdinalIgnoreCase)
                ? _svc.Installer.InstallZip(p.luaPath, p.appId)
                : _svc.Installer.InstallLuaFile(p.luaPath, p.appId));
        }
        _pendingLua = null;
        await ProcessQueueAsync();
    }

    [RelayCommand]
    private async Task CancelOverwrite()
    {
        IsConfirming = false;
        if (_pendingLua is { } p) _tally.Skipped.Add(Path.GetFileName(p.luaPath));
        _pendingLua = null;
        await ProcessQueueAsync();
    }

    private void Apply(InstallResult r)
    {
        if (r.Error is not null) _tally.Errors.Add(r.Error);
        _tally.Failed += r.Failed.Count;
        if (r.LuaInstalled) _tally.Luas++;
        _tally.Manifests += r.ManifestCount;
    }

    private void FinishBatch()
    {
        _queue = null;
        var t = _tally;
        bool anyProblem = t.Failed > 0 || t.Errors.Count > 0;
        ResultFailed = anyProblem;

        var parts = new List<string>();
        if (t.Luas > 0) parts.Add($"{t.Luas} lua(s)");
        if (t.Manifests > 0) parts.Add($"{t.Manifests} manifest(s)");

        if (parts.Count == 0 && !anyProblem)
            ResultText = t.Skipped.Count > 0 ? "Nothing installed (cancelled or unrecognised file names)." : null;
        else
        {
            string ok = parts.Count > 0 ? $"Installed {string.Join(" + ", parts)} to Steam." : "";
            string bad = t.Failed > 0 ? $" {t.Failed} file(s) failed. Close Steam and retry." : "";
            string err = t.Errors.Count > 0 ? $" {t.Errors[0]}" : "";
            ResultText = $"{ok}{bad}{err}".Trim();
        }

        if (t.Luas > 0 || t.Manifests > 0) Installed?.Invoke();
    }

    private string? DisplayName(LuaEntry e)
    {
        var d = _depotsById.GetValueOrDefault(e.Id);
        if (d?.DlcAppId is { } dlc)
        {
            string? steamName = _svc.AppList.GetName(dlc) ?? _svc.AppInfo.GetCached(dlc)?.Name;
            if (steamName is not null) return steamName;
        }
        return _svc.AppList.GetName(e.Id) ?? _svc.AppInfo.GetCached(e.Id)?.Name ?? e.Comment;
    }

    private DiffRow ToDiffRow(LuaEntry e)
    {
        var d = _depotsById.GetValueOrDefault(e.Id);
        bool isDlc = d?.IsDlc ?? false;
        bool isShared = d?.IsShared ?? false;

        string title = DisplayName(e)
            ?? (isDlc ? $"DLC {d!.DlcAppId}"
                : isShared ? "Shared depot"
                : e.HasKey ? "Depot"
                : $"DLC {e.Id}");

        var meta = new List<string> { e.Id.ToString() };
        if (d is { Size: > 0 }) meta.Add(FormatSize(d.Size));
        if (!string.IsNullOrWhiteSpace(d?.Os)) meta.Add(PrettyOs(d!.Os!));
        if (!string.IsNullOrWhiteSpace(d?.Language)) meta.Add(d!.Language!);

        string url = d?.DlcAppId is { } dlcId
            ? $"https://steamdb.info/app/{dlcId}/"
            : $"https://steamdb.info/depot/{e.Id}/";

        return new DiffRow(title, string.Join("  ·  ", meta), isDlc, isShared, url);
    }

    [RelayCommand]
    private void OpenSteamDb(DiffRow row) => _svc.Integrations.OpenUrl(row.SteamDbUrl);

    private string NameFor(long appId) =>
        _svc.AppList.GetName(appId) ?? _svc.AppInfo.GetCached(appId)?.Name ?? appId.ToString();

    private static string FormatSize(long bytes)
    {
        if (bytes <= 0) return "";
        double gb = bytes / 1024d / 1024d / 1024d;
        if (gb >= 1) return $"{gb:0.##} GB";
        return $"{bytes / 1024d / 1024d:0.#} MB";
    }

    private static string PrettyOs(string os) => os switch
    {
        "windows" => "Windows",
        "macos" or "macosx" => "macOS",
        "linux" => "Linux",
        _ => os
    };

    private static LuaContents? ExtractLuaFromZip(string zipPath, long appId)
    {
        try
        {
            using var archive = ZipFile.OpenRead(zipPath);
            var luaEntry = archive.Entries.FirstOrDefault(e =>
                e.Name.EndsWith(".lua", StringComparison.OrdinalIgnoreCase));
            if (luaEntry is null) return null;

            string tmp = Path.Combine(Path.GetTempPath(), $"luatools_drop_{appId}_{Guid.NewGuid():N}.lua");
            luaEntry.ExtractToFile(tmp, overwrite: true);
            try { return LuaFileParser.Parse(tmp, appId); }
            finally { try { File.Delete(tmp); } catch { } }
        }
        catch { return null; }
    }
}

public partial class DownloadsViewModel : ObservableObject
{
    private readonly NovaServices _svc;

    public DownloadsViewModel(NovaServices svc)
    {
        _svc = svc;
        _svc.Queue.Items.CollectionChanged += (_, _) => RaiseCounts();
        _svc.Queue.History.CollectionChanged += (_, _) => RaiseCounts();
        _svc.Queue.StateChanged += RaiseCounts;
    }

    public DownloadQueue Queue => _svc.Queue;
    public Action<DownloadItem>? RevealItem { get; set; }

    public bool HasItems => _svc.Queue.Items.Count > 0;
    public bool HasHistory => _svc.Queue.History.Count > 0;
    public bool IsEmpty => !HasItems;

    private void RaiseCounts()
    {
        OnPropertyChanged(nameof(HasItems));
        OnPropertyChanged(nameof(HasHistory));
        OnPropertyChanged(nameof(IsEmpty));
    }

    [RelayCommand(AllowConcurrentExecutions = true)]
    private async Task Cancel(DownloadItem item)
    {
        _svc.Queue.Cancel(item);
        await Task.CompletedTask;
    }

    [RelayCommand] private void Retry(DownloadItem item) => _svc.Queue.Retry(item);
    [RelayCommand] private void Pause(DownloadItem item) => _svc.Queue.Pause(item);
    [RelayCommand] private void Resume(DownloadItem item) => _svc.Queue.Resume(item);
    [RelayCommand] private void Remove(DownloadItem item) => _svc.Queue.Remove(item);
    [RelayCommand] private void MoveUp(DownloadItem item) => _svc.Queue.Move(item, -1);
    [RelayCommand] private void MoveDown(DownloadItem item) => _svc.Queue.Move(item, +1);
    [RelayCommand] private void ClearHistory() => _svc.Queue.ClearHistory();
    [RelayCommand] private void RemoveHistoryEntry(DownloadHistoryEntry entry) => _svc.Queue.RemoveHistory(entry);

    [RelayCommand] private void CopyAppId(DownloadItem item) => CopyId(item.AppId);
    [RelayCommand] private void CopyHistoryAppId(DownloadHistoryEntry entry) => CopyId(entry.Record.AppId);
    [RelayCommand] private void ShowInFolder(DownloadItem item) => Show(item.RevealPath);
    [RelayCommand] private void ShowHistoryInFolder(DownloadHistoryEntry entry) => Show(entry.Record.RevealPath);

    private void CopyId(long appId)
    {
        if (!_svc.Integrations.CopyToClipboard(appId.ToString()))
            _svc.Get<IToastService>().Show("Copy App ID", "Couldn't copy — another app is holding the clipboard.", error: true);
    }

    private void Show(string? path)
    {
        if (!_svc.Integrations.RevealPath(path))
            _svc.Get<IToastService>().Show("Show in folder",
                $"Couldn't open {path ?? ""} — it may have been moved or deleted.", error: true);
    }

    [RelayCommand]
    private void Review(DownloadItem item)
    {
        item.Job.OnReveal?.Invoke();
        RevealItem?.Invoke(item);
    }
}
