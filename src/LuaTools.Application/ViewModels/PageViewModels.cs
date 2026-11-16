// LuaTools Nova — Fixes / Plugin / Mode / Settings ViewModels (ported from the MIT original;
// platform actions routed through NovaServices interfaces; Linux limitations surfaced honestly).
using System.Collections.ObjectModel;
using System.IO;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using LuaTools.Core.Downloads;
using LuaTools.Core.Models;
using LuaTools.Core.Services;
using LuaTools.Infrastructure.Clients;
using LuaTools.Infrastructure.Network;
using LuaTools.Linux;

namespace LuaTools.Application.ViewModels;

public partial class FixGameCardVm : ObservableObject
{
    public string AppId { get; }
    public string Name { get; }
    public string? HeaderImage { get; }
    public int FixCount { get; }
    public IReadOnlyList<string> TagIds { get; }
    public string FixCountLabel => $"{FixCount} fixes";

    [ObservableProperty] private string? _cover;
    private int _resolving;

    public bool Matches(string q) =>
        Name.Contains(q, StringComparison.OrdinalIgnoreCase) || AppId.Contains(q);

    public async Task EnsureCoverAsync(CoverCache covers)
    {
        if (Cover is not null || string.IsNullOrWhiteSpace(HeaderImage)) return;
        if (!long.TryParse(AppId, out long appid)) return;
        if (Interlocked.Exchange(ref _resolving, 1) == 1) return;
        try
        {
            string? local = covers.GetLocalPath(appid) ?? await covers.EnsureAsync(appid, HeaderImage!);
            if (local is not null) Cover = local;
        }
        finally { Interlocked.Exchange(ref _resolving, 0); }
    }

    public FixGameCardVm(DenuvoGameListing g)
    {
        AppId = g.AppId;
        Name = g.Name;
        HeaderImage = g.HeaderImage;
        FixCount = g.FixCount;
        TagIds = g.Tags.Select(t => t.Id).ToList();
    }
}

public partial class TagPillVm : ObservableObject
{
    public string Id { get; }
    public string Name { get; }
    [ObservableProperty] private bool _isSelected;

    public TagPillVm(DenuvoTag t)
    {
        Id = t.Id;
        Name = t.Name;
    }
}

public partial class FixItemVm : ObservableObject
{
    public string Id { get; }
    public string Title { get; }
    public string? Description { get; }
    public IReadOnlyList<DenuvoTag> Tags { get; }
    public bool HasManifest { get; }
    public bool HasFix { get; }
    public string? ManifestFilename { get; }
    public string? FixFilename { get; }
    public string DateLabel { get; }

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(CanDownloadManifest))]
    private DownloadItem? _manifestItem;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(CanDownloadFix), nameof(FixHint))]
    private DownloadItem? _fixItem;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(CanDownloadFix), nameof(FixHint))]
    private bool _gameInstalled;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(HasApplied), nameof(CanDownloadFix), nameof(FixHint))]
    private bool _isApplied;

    public bool HasApplied => IsApplied;
    public bool CanDownloadManifest => HasManifest && ManifestItem?.IsActive != true;
    public bool CanDownloadFix => HasFix && GameInstalled && !IsApplied && FixItem?.IsActive != true;

    public string? FixHint =>
        IsApplied ? "This fix is already applied. Revert it if you want to install another."
        : GameInstalled ? null
        : "Install the game first to apply a fix.";

    public FixItemVm(DenuvoFix f)
    {
        Id = f.Id;
        Title = f.Title;
        Description = f.Description;
        Tags = f.Tags;
        HasManifest = f.HasManifest;
        HasFix = f.HasFix;
        ManifestFilename = f.ManifestFilename;
        FixFilename = f.FixFilename;
        DateLabel = DateTimeOffset.TryParse(f.CreatedAt, out var d) ? d.UtcDateTime.ToString("d MMM yyyy") : "";
    }
}

public partial class FixesViewModel : PagedListViewModel<FixGameCardVm>
{
    private readonly NovaServices _svc;
    private List<FixGameCardVm> _allGames = [];

    public ObservableCollection<TagPillVm> Tags { get; } = new();
    protected override void SavePageSizeSetting(int size) => _svc.Settings.FixesPageSize = size;

    protected override void OnPageSliced(IReadOnlyList<FixGameCardVm> slice)
    {
        foreach (var g in slice) _ = g.EnsureCoverAsync(_svc.Covers);
    }

    [ObservableProperty] private string _searchText = "";
    partial void OnSearchTextChanged(string value) => ApplyFilter();

    [ObservableProperty] private string? _selectedTagId;
    private HashSet<long> _installedAppIds = [];

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(CanFilter))]
    private bool _loaded;
    public bool CanFilter => Loaded && _allGames.Count > 0;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(MyGamesHint))]
    private bool _myGamesOnly;

    public string MyGamesHint
    {
        get
        {
            if (_installedAppIds.Count == 0) return "No luas added yet — add a game (or install one) to see it here.";
            int withFixes = _allGames.Count(g =>
                long.TryParse(g.AppId, out long id) && _installedAppIds.Contains(id));
            return $"{withFixes} of your games have fixes. Scroll the grid to browse the rest.";
        }
    }

    partial void OnMyGamesOnlyChanged(bool value)
    {
        if (value)
        {
            SelectedTagId = null;
            foreach (var pill in Tags) pill.IsSelected = false;
        }
        ApplyFilter();
    }

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(IsDetailOpen))]
    private FixGameCardVm? _selectedGame;
    public bool IsDetailOpen => SelectedGame is not null;
    public ObservableCollection<FixItemVm> Fixes { get; } = new();
    [ObservableProperty] private bool _isLoadingFixes;

    private List<FixItemVm> _allFixes = [];
    public ObservableCollection<TagPillVm> FixTags { get; } = new();
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(HasFixTags))]
    private string? _selectedFixTagId;
    public bool HasFixTags => FixTags.Count > 0;

    public Func<Task>? RequestSignIn { get; set; }

    [ObservableProperty] private bool _isConfirmingRevert;
    [ObservableProperty] private string _confirmRevertTitle = "";
    [ObservableProperty] private string _confirmRevertBody = "";
    private (FixItemVm Fix, FixGameCardVm Game)? _pendingRevert;

    public FixesViewModel(NovaServices svc)
    {
        _svc = svc;
        InitPageSize(svc.Settings.FixesPageSize);
    }

    public async Task LoadAsync(bool force = false)
    {
        if (!force && _allGames.Count > 0) return;
        IsLoading = true;
        try
        {
            var data = await _svc.Api.GetDenuvoListingsAsync();
            if (data is null)
            {
                EmptyMessage = "Couldn't load fixes. Check your connection.";
                return;
            }

            _allGames = data.Games.Select(g => new FixGameCardVm(g)).ToList();
            Tags.Clear();
            foreach (var t in data.Tags) Tags.Add(new TagPillVm(t));

            _installedAppIds = _svc.Steam.EffectivePath is { } root && Directory.Exists(Path.Combine(root, "config", "stplug-in"))
                ? await Task.Run(() => LuaInstaller.EnumerateInstalled(
                    Path.Combine(root, "config", "stplug-in")).Select(i => i.AppId).ToHashSet())
                : new HashSet<long>();
            OnPropertyChanged(nameof(MyGamesHint));

            ApplyFilter();
            if (_allGames.Count == 0) EmptyMessage = "No fixes available yet.";
        }
        catch
        {
            EmptyMessage = "Couldn't load fixes. Check your connection.";
        }
        finally
        {
            IsLoading = false;
            Loaded = true;
        }
    }

    [RelayCommand]
    private Task Refresh() => RefreshWithCooldownAsync(async () =>
    {
        if (SearchText.Length > 0) SearchText = "";
        if (SelectedTagId is not null) SelectTag(SelectedTagId);
        if (MyGamesOnly) MyGamesOnly = false;
        await LoadAsync(force: true);
        _svc.Get<IToastService>().Show("Refreshed", $"{_allGames.Count} games with fixes.");
    });

    [RelayCommand]
    private void SelectTag(string? tagId)
    {
        SelectedTagId = SelectedTagId == tagId ? null : tagId;
        if (MyGamesOnly) MyGamesOnly = false;
        foreach (var pill in Tags) pill.IsSelected = pill.Id == SelectedTagId;
        ApplyFilter();
    }

    private void ApplyFilter()
    {
        string q = SearchText.Trim();
        IEnumerable<FixGameCardVm> shown = _allGames;
        if (SelectedTagId is { } tag) shown = shown.Where(g => g.TagIds.Contains(tag));
        if (MyGamesOnly) shown = shown.Where(g => long.TryParse(g.AppId, out long id) && _installedAppIds.Contains(id));
        if (q.Length > 0) shown = shown.Where(g => g.Matches(q));
        SetFiltered(shown);
    }

    public async Task OpenForAppIdAsync(long appId)
    {
        if (_allGames.Count == 0)
            await LoadAsync();

        var game = _allGames.FirstOrDefault(g => g.AppId == appId.ToString());
        if (game is null) return;

        SearchText = "";
        SelectedTagId = null;
        ApplyFilter();
        await OpenGame(game);
    }

    [RelayCommand]
    private void CopyAppId(FixGameCardVm game)
    {
        if (!_svc.Integrations.CopyToClipboard(game.AppId))
            _svc.Get<IToastService>().Show("Copy App ID", "Couldn't copy — another app is holding the clipboard.", error: true);
    }

    [RelayCommand]
    private void ShowInFolder(FixGameCardVm game)
    {
        string? dir = long.TryParse(game.AppId, out long appId) ? _svc.Library.GetInstallDir(appId) : null;
        if (_svc.Integrations.RevealPath(dir)) return;

        _svc.Get<IToastService>().Show("Game not found",
            $"Install {game.Name} in Steam first, then apply the fix.", error: true);
    }

    [RelayCommand]
    private async Task OpenGame(FixGameCardVm game)
    {
        SelectedGame = game;
        _ = game.EnsureCoverAsync(_svc.Covers);
        Fixes.Clear();
        _allFixes = [];
        FixTags.Clear();
        SelectedFixTagId = null;
        IsLoadingFixes = true;
        try
        {
            var data = await _svc.Api.GetDenuvoFixesAsync(game.AppId);
            if (data is not null)
            {
                _allFixes = data.Fixes.Select(f => new FixItemVm(f)).ToList();

                string? installDir = long.TryParse(game.AppId, out long gameAppId)
                    ? await Task.Run(() => _svc.Library.GetInstallDir(gameAppId))
                    : null;
                foreach (var f in _allFixes)
                {
                    f.GameInstalled = installDir is not null;
                    f.IsApplied = installDir is not null
                        && File.Exists(FixRecordPaths.Get(installDir, f.Id));
                }

                var distinct = _allFixes.SelectMany(f => f.Tags)
                    .GroupBy(t => t.Id).Select(g => g.First())
                    .OrderBy(t => t.Name).ToList();
                if (distinct.Count > 1)
                    foreach (var t in distinct) FixTags.Add(new TagPillVm(t));

                OnPropertyChanged(nameof(HasFixTags));
                ApplyFixFilter();
            }
        }
        catch { /* leave empty */ }
        finally { IsLoadingFixes = false; }
    }

    [RelayCommand]
    private void SelectFixTag(string? tagId)
    {
        SelectedFixTagId = SelectedFixTagId == tagId ? null : tagId;
        foreach (var pill in FixTags) pill.IsSelected = pill.Id == SelectedFixTagId;
        ApplyFixFilter();
    }

    private void ApplyFixFilter()
    {
        IEnumerable<FixItemVm> shown = _allFixes;
        if (SelectedFixTagId is { } tag) shown = shown.Where(f => f.Tags.Any(t => t.Id == tag));
        Fixes.Clear();
        foreach (var f in shown) Fixes.Add(f);
    }

    [RelayCommand] private void CloseDetail() => SelectedGame = null;

    [RelayCommand] private Task DownloadManifest(FixItemVm fix) => RunDownload(fix, "manifest");
    [RelayCommand] private Task DownloadFix(FixItemVm fix) => RunDownload(fix, "fix");

    [RelayCommand]
    private void RevertFix(FixItemVm fix)
    {
        if (SelectedGame is not { } game) return;
        _pendingRevert = (fix, game);
        ConfirmRevertTitle = $"Revert fix for {game.Name}?";
        ConfirmRevertBody = $"Revert \"{game.Name}\"? Modified files will be restored from backup and files added by the fix will be deleted.";
        IsConfirmingRevert = true;
    }

    [RelayCommand]
    private void CancelRevertConfirm()
    {
        IsConfirmingRevert = false;
        _pendingRevert = null;
    }

    [RelayCommand]
    private async Task ConfirmRevert()
    {
        IsConfirmingRevert = false;
        var pending = _pendingRevert;
        _pendingRevert = null;
        if (pending is not (var fix, var game)) return;
        if (!long.TryParse(game.AppId, out long appId)) return;

        var result = await Task.Run(() => _svc.Jobs.RevertDenuvoFix(appId, fix.Id, game.Name));
        if (result.Ok) fix.IsApplied = false;
    }

    private async Task RunDownload(FixItemVm fix, string slot)
    {
        if (_svc.Auth.IsGuest)
        {
            const string message = "Sign in with Discord to download fixes.";
            _svc.Get<IToastService>().Show("Sign in required", message);
            if (RequestSignIn is not null) await RequestSignIn();
            return;
        }
        if (SelectedGame is not { } game) return;
        if (!long.TryParse(game.AppId, out long appId)) return;

        if (slot == "fix" && _svc.Library.GetInstallDir(appId) is null)
        {
            _svc.Get<IToastService>().Show("Game not found",
                $"Install {game.Name} in Steam first, then apply the fix.", error: true);
            return;
        }

        string fallback = slot == "manifest"
            ? fix.ManifestFilename ?? $"{game.AppId}.zip"
            : fix.FixFilename ?? $"{game.AppId}_fix.zip";

        var job = _svc.Jobs.CreateDenuvoJob(fix.Id, slot, fallback, appId, game.Name, fix.Title,
            onFinished: (item, result) =>
            {
                if (result is null && item.Status == DownloadStatus.Failed)
                {
                    _svc.Get<IToastService>().Show("Download failed",
                        item.Message ?? "Something went wrong. Try again.", error: true);
                    return;
                }
                if (slot == "fix" && result?.Ok == true) fix.IsApplied = true;
            });

        var item = _svc.Queue.Enqueue(job);
        if (slot == "manifest") fix.ManifestItem = item;
        else fix.FixItem = item;
    }
}

public partial class PluginViewModel : ObservableObject
{
    private readonly NovaServices _svc;
    private readonly Integrations.PluginInstallerService _installer;

    [ObservableProperty] private string _installedVersion = "—";
    [ObservableProperty] private string _latestVersion = "—";
    [ObservableProperty] private string _frontendStatus = "Checking…";
    [ObservableProperty] private string _bridgeStatus = "Checking…";
    [ObservableProperty] private bool _frontendInstalled;
    [ObservableProperty] private bool _bridgeOk;
    [ObservableProperty] private bool _isInstalled;
    [ObservableProperty] private bool _updateAvailable;
    [ObservableProperty] private bool _isBusy;
    [ObservableProperty] private double _progress;
    [ObservableProperty] private bool _isProgressIndeterminate;
    [ObservableProperty] private string? _statusLine;

    public bool NotBusy => !IsBusy;
    public bool CanUninstall => IsInstalled && !IsBusy;
    public bool InstallIsPrimary => !IsInstalled || UpdateAvailable;
    public bool ShowUpToDate => IsInstalled && !UpdateAvailable;

    public string InstallButtonText => !IsInstalled
        ? "Install" : UpdateAvailable ? "Update" : "Reinstall";

    public PluginViewModel(NovaServices svc, Integrations.PluginInstallerService installer)
    {
        _svc = svc;
        _installer = installer;
    }

    public async Task LoadAsync() => await RefreshAsync(force: false);

    private async Task RefreshAsync(bool force)
    {
        var st = await _installer.GetStatusAsync(force);
        IsInstalled = st.FrontendInstalled;
        InstalledVersion = st.InstalledTag ?? (IsInstalled ? "unknown" : "—");
        LatestVersion = st.Offline ? "offline" : (st.LatestTag ?? "—");
        FrontendInstalled = st.FrontendInstalled;
        FrontendStatus = st.FrontendInstalled ? "Installed" : "Not installed";
        BridgeOk = st.BridgeReady;
        BridgeStatus = st.CdpUnavailable
            ? "Not detected (Steam needs -cef-enable-remote-debugging)"
            : "CDP bridge detected on :8080";
        UpdateAvailable = st.UpdateAvailable;
        StatusLine = st.Offline ? "Couldn't reach GitHub to check for updates."
            : st.CdpUnavailable ? "Steam isn't exposing its CEF debug bridge. Launch Steam with -cef-enable-remote-debugging (Nova opens store pages through it on Linux)."
            : null;
    }

    private IProgress<double?> MakeProgress() => new Progress<double?>(p =>
    {
        if (p is null) { IsProgressIndeterminate = true; }
        else { IsProgressIndeterminate = false; Progress = p.Value * 100; }
    });

    [RelayCommand]
    private async Task Install()
    {
        if (IsBusy) return;
        if (!_svc.Get<IDialogService>().Confirm("Install plugin",
                "The plugin frontend will be installed; Nova's store-page bridge needs Steam relaunched with CEF debugging enabled. Continue?"))
            return;

        IsBusy = true;
        IsProgressIndeterminate = true;
        Progress = 0;
        try
        {
            var (ok, error) = await _installer.InstallAsync(MakeProgress());
            _svc.Get<IToastService>().Show("Plugin", ok
                ? "Plugin installed."
                : $"Install failed: {error}", error: !ok);
        }
        finally
        {
            IsBusy = false;
            await RefreshAsync(force: true);
        }
    }

    [RelayCommand]
    private async Task Uninstall()
    {
        if (IsBusy || !IsInstalled) return;

        IsBusy = true;
        IsProgressIndeterminate = true;
        try
        {
            var (ok, error) = await _installer.UninstallAsync();
            _svc.Get<IToastService>().Show("Plugin", ok
                ? "Plugin removed."
                : $"Uninstall failed: {error}", error: !ok);
        }
        finally
        {
            IsBusy = false;
            await RefreshAsync(force: true);
        }
    }

    [RelayCommand]
    private async Task CheckForUpdates()
    {
        if (IsBusy) return;
        IsBusy = true;
        IsProgressIndeterminate = true;
        try
        {
            await RefreshAsync(force: true);
            if (StatusLine is null)
                _svc.Get<IToastService>().Show("Plugin",
                    UpdateAvailable ? "An update is available." : "You're up to date.");
        }
        finally { IsBusy = false; }
    }
}

public partial class ModeViewModel : ObservableObject
{
    private readonly NovaServices _svc;
    private readonly UnlockerServiceProxy _unlocker;

    public ObservableCollection<ModeCardViewModel> Cards { get; } = new();

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(NotBusy))]
    private bool _isBusy;
    public bool NotBusy => !IsBusy;
    [ObservableProperty] private double _progress;
    [ObservableProperty] private bool _isProgressIndeterminate;
    [ObservableProperty] private bool _isConfirming;
    [ObservableProperty] private string _confirmTitle = "";
    private ModeCardViewModel? _pendingCard;

    public ModeViewModel(NovaServices svc)
    {
        _svc = svc;
        _unlocker = svc.Get<UnlockerServiceProxy>();
        _unlocker.Settings = svc.Settings;
    }

    public Task LoadAsync(bool forceRefresh = false)
    {
        SyncCards();

        var active = _unlocker.SelectedMode;
        foreach (var card in Cards)
        {
            card.IsActive = card.Mode == active;
            card.StatusText = card.LinuxStatusText;
            card.ButtonText = card.Mode switch
            {
                UnlockerMode.Ost or UnlockerMode.Bst => "Not available on Linux",
                UnlockerMode.Custom when card.IsActive => "Active",
                _ => "Switch to this",
            };
            card.ShowActionButton = !(card.Mode == UnlockerMode.Custom && card.IsActive)
                && card.Mode is UnlockerMode.Custom;
        }

        return Task.CompletedTask;
    }

    private void SyncCards()
    {
        foreach (var def in _unlocker.Modes)
            if (!Cards.Any(c => c.Mode == def.Mode))
                Cards.Add(new ModeCardViewModel(def));
    }

    [RelayCommand]
    private void Install(ModeCardViewModel card)
    {
        if (IsBusy) return;
        _pendingCard = card;
        ConfirmTitle = card.IsActive ? $"Reinstall {card.Title}?" : $"Switch to {card.Title}?";
        IsConfirming = true;
    }

    [RelayCommand]
    private void CancelConfirm()
    {
        IsConfirming = false;
        _pendingCard = null;
    }

    [RelayCommand]
    private async Task ConfirmInstall()
    {
        IsConfirming = false;
        var card = _pendingCard;
        _pendingCard = null;
        if (card is null) return;

        await RunInstall(card.Mode);
    }

    private async Task RunInstall(UnlockerMode mode)
    {
        if (IsBusy) return;
        IsBusy = true;
        try
        {
            var result = await _unlocker.InstallAsync(mode);
            if (result.Success)
                _svc.Get<IToastService>().Show("Mode updated", $"{mode} is now the active mode.");
            else
                _svc.Get<IToastService>().Show("Install failed", result.Error ?? "Something went wrong.", error: true);

            await LoadAsync();
        }
        finally { IsBusy = false; }
    }
}

public partial class ModeCardViewModel : ObservableObject
{
    public ModeCardInfo Info { get; }
    public UnlockerMode Mode => Info.Mode;
    public string Title => Info.DisplayName;
    public string Description => Info.Description;
    public string LinuxStatusText => Info.Status switch
    {
        ModeStatus.NotSupported => "Not supported on Linux",
        ModeStatus.UserManaged => "You're on your own",
        _ => "Not installed",
    };

    [ObservableProperty] private string _statusText = "Checking…";
    [ObservableProperty] private string _buttonText = "Install";
    [ObservableProperty] private bool _isActive;
    public bool IsRecommended => Mode == UnlockerMode.Bst;
    public bool IsExperimental => Mode == UnlockerMode.Ost;

    public bool ShowActionButton { get; set; } = true;

    public ModeCardViewModel(ModeCardInfo info) => Info = info;
}

public record LanguageOption(string Display, string? Tag);
public record DnsModeOption(string Display, string Tag);

public partial class SettingsViewModel : ObservableObject
{
    private readonly NovaServices _svc;

    [ObservableProperty] private string? _displayName;
    [ObservableProperty] private string? _email;
    [ObservableProperty] private string? _avatarUrl;
    [ObservableProperty] private bool _isGuest = true;
    public bool IsRealUser => !IsGuest;

    [ObservableProperty] private string? _loginRequiredMessage;
    [RelayCommand] private void DismissLoginRequired() => LoginRequiredMessage = null;

    [ObservableProperty] private string _steamPath = "";
    [ObservableProperty] private bool _isSteamOverridden;
    [ObservableProperty] private string _steamSource = "";
    [ObservableProperty] private string? _steamWarning;

    [ObservableProperty] private bool _autoUpdateApps;
    partial void OnAutoUpdateAppsChanged(bool value) => _svc.Settings.AutoUpdateApps = value;

    [ObservableProperty] private bool _fastFetch;
    partial void OnFastFetchChanged(bool value) => _svc.Settings.FastFetch = value;

    [ObservableProperty] private bool _donateKeys;
    partial void OnDonateKeysChanged(bool value) => _svc.Settings.DonateKeys = value;

    [ObservableProperty] private bool _startWithSystem;
    partial void OnStartWithSystemChanged(bool value)
    {
        _svc.Settings.StartWithSystem = value;
        _svc.Integrations.SetAutostart(value);
    }

    [ObservableProperty] private bool _minimizeToTray;
    partial void OnMinimizeToTrayChanged(bool value) => _svc.Settings.MinimizeToTray = value;

    public ObservableCollection<LanguageOption> LanguageOptions { get; } = new(new LanguageOption[]
    {
        new("System default", null),
        new("English", "en"),
    });

    [ObservableProperty] private LanguageOption _selectedLanguage = null!;
    partial void OnSelectedLanguageChanged(LanguageOption value)
    {
        if (value is null) return;
        _svc.Settings.Language = value.Tag;
        RequestRestartPrompt?.Invoke();
    }

    public ObservableCollection<DnsModeOption> DnsModeOptions { get; } = new(new DnsModeOption[]
    {
        new("Automatic", "Auto"),
        new("Always use Cloudflare", "Always"),
        new("Use system DNS only", "Never"),
    });

    [ObservableProperty] private DnsModeOption _selectedDnsMode = null!;
    partial void OnSelectedDnsModeChanged(DnsModeOption value)
    {
        if (value is not null) _svc.Settings.DnsMode = value.Tag;
    }

    private bool _suppressLanguagePrompt;

    [ObservableProperty] private string _hubcapKeyInput = "";
    [ObservableProperty] private string? _hubcapKeyStatus;
    [ObservableProperty] private string _hubcapKeyStatusColor = "#22c55e";
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(ShowHubcapStats), nameof(HubcapStatsPending), nameof(HubcapStatsText))]
    private bool _hubcapIsKeyConfigured;
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(CanEditHubcapKey))]
    private bool _isValidatingHubcapKey;
    public bool CanEditHubcapKey => !IsValidatingHubcapKey;
    [ObservableProperty] private bool _isRefreshingHubcapStats;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(HubcapStatsText), nameof(HubcapUsagePercent), nameof(ShowHubcapStats), nameof(HubcapStatsPending))]
    private HubcapStats? _hubcapStats;

    public double HubcapUsagePercent =>
        HubcapStats is { DailyLimit: > 0 } ? (double)HubcapStats.DailyUsage / HubcapStats.DailyLimit : 0;
    public bool ShowHubcapStats => HubcapIsKeyConfigured;
    public bool HubcapStatsPending => HubcapIsKeyConfigured && HubcapStats is null;
    public string HubcapStatsText => HubcapStats is not null
        ? $"{HubcapStats.DailyUsage}/{HubcapStats.DailyLimit} requests today"
        : "Loading…";

    public Func<Task>? RequestSignIn { get; set; }
    public Action? RequestRestartPrompt { get; set; }

    public SettingsViewModel(NovaServices svc)
    {
        _svc = svc;
        svc.Auth.AuthStateChanged += RefreshAccount;
        RefreshAccount();
        RefreshSteam();
        _autoUpdateApps = svc.Settings.AutoUpdateApps;
        _fastFetch = svc.Settings.FastFetch;
        _donateKeys = svc.Settings.DonateKeys;
        _startWithSystem = svc.Settings.StartWithSystem;
        _minimizeToTray = svc.Settings.MinimizeToTray;
        _hubcapIsKeyConfigured = !string.IsNullOrEmpty(svc.Settings.HubcapApiKey);

        _suppressLanguagePrompt = true;
        _selectedLanguage = LanguageOptions.FirstOrDefault(o => o.Tag == svc.Settings.Language) ?? LanguageOptions[0];
        _suppressLanguagePrompt = false;

        _selectedDnsMode = DnsModeOptions.FirstOrDefault(o => o.Tag == svc.Settings.DnsMode) ?? DnsModeOptions[0];
    }

    private void RefreshSteam()
    {
        string? path = _svc.Steam.EffectivePath;
        IsSteamOverridden = _svc.Steam.IsOverridden;
        SteamPath = path ?? "Steam not found";
        SteamSource = IsSteamOverridden ? "Custom location"
            : path is null ? "Not found"
            : $"Auto-detected ({_svc.Steam.DetectionSource})";
        SteamWarning = path is not null && !_svc.Steam.IsValid
            ? "Steam's data root is missing steam.sh — this looks incomplete."
            : null;
    }

    public void RefreshAccount()
    {
        IsGuest = _svc.Auth.IsGuest;
        DisplayName = _svc.Auth.DisplayName;
        Email = _svc.Auth.Email;
        AvatarUrl = _svc.Auth.AvatarUrl;
        if (!IsGuest) LoginRequiredMessage = null;
    }

    [RelayCommand] private async Task SignInAsync() { if (RequestSignIn is not null) await RequestSignIn(); }
    [RelayCommand] private void SignOut() => _svc.Auth.SignOut();

    [ObservableProperty] private string _codeInput = "";
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(CanRedeemCode))]
    private bool _isRedeemingCode;
    [ObservableProperty] private string? _codeError;
    public bool CanRedeemCode => !IsRedeemingCode;

    [RelayCommand]
    private async Task SignInWithCodeAsync()
    {
        string code = CodeInput.Trim();
        if (code.Length != 6) return;

        IsRedeemingCode = true;
        CodeError = null;
        try
        {
            await _svc.Auth.SignInWithCodeAsync(code);
            CodeInput = "";
        }
        catch (Exception ex)
        {
            CodeError = ex.Message;
        }
        finally { IsRedeemingCode = false; }
    }

    [RelayCommand]
    private void OverrideSteamFolder()
    {
        string? picked = _svc.Integrations.PickFolder("Choose your Steam folder", _svc.Steam.EffectivePath);
        if (picked is not null)
        {
            _svc.Settings.SteamPathOverride = picked;
            if (_svc.Steam is SteamPathLocator locator) locator.SetOverride(picked);
            RefreshSteam();
        }
    }

    [RelayCommand]
    private void ClearSteamOverride()
    {
        _svc.Settings.SteamPathOverride = null;
        if (_svc.Steam is SteamPathLocator locator) locator.SetOverride(null);
        RefreshSteam();
    }

    [RelayCommand]
    private void OpenSteamFolder()
    {
        string? path = _svc.Steam.EffectivePath;
        if (path is not null && Directory.Exists(path)) _svc.Integrations.RevealPath(path);
    }

    [RelayCommand]
    private void OpenWebsite() => _svc.Integrations.OpenUrl(AppConfig.ApiBaseUrl);

    [RelayCommand]
    private void OpenHubcap() => _svc.Integrations.OpenUrl(AppConfig.HubcapBaseUrl);

    public void OnViewLoaded()
    {
        if (HubcapIsKeyConfigured)
            _ = RefreshHubcapStatsAsync();
        FastFetch = _svc.Settings.FastFetch;
    }

    [RelayCommand]
    private async Task RefreshHubcapStatsAsync()
    {
        string? key = _svc.Settings.HubcapApiKey;
        if (string.IsNullOrEmpty(key)) return;

        IsRefreshingHubcapStats = true;
        try { HubcapStats = await _svc.Hubcap.GetStatsAsync(key); }
        catch { HubcapStats = null; }
        finally { IsRefreshingHubcapStats = false; }
    }

    [RelayCommand]
    private async Task ValidateAndSaveHubcapKeyAsync()
    {
        string key = HubcapKeyInput.Trim();

        if (!HubcapService.IsValidKeyFormat(key))
        {
            ShowHubcapStatus("That doesn't look like a valid key (smm_…).", isError: true);
            return;
        }

        IsValidatingHubcapKey = true;
        try
        {
            var stats = await _svc.Hubcap.GetStatsAsync(key);
            if (stats is null)
            {
                ShowHubcapStatus("Couldn't verify the key. It may be invalid, expired, or Hubcap is unreachable.", isError: true);
                return;
            }

            _svc.Settings.HubcapApiKey = key;
            HubcapIsKeyConfigured = true;
            HubcapKeyInput = "";
            HubcapKeyStatus = null;
            HubcapStats = stats;
        }
        finally { IsValidatingHubcapKey = false; }
    }

    [RelayCommand]
    private void ClearHubcapKey()
    {
        _svc.Settings.HubcapApiKey = null;
        HubcapIsKeyConfigured = false;
        HubcapKeyInput = "";
        HubcapKeyStatus = null;
        HubcapStats = null;
    }

    private void ShowHubcapStatus(string text, bool isError)
    {
        HubcapKeyStatus = text;
        HubcapKeyStatusColor = isError ? "#f87171" : "#22c55e";
    }
}

public static class FixRecordPaths
{
    public static string Get(string installDir, string fixId) =>
        Path.Combine(installDir, ".luatools-fix",
            string.Concat(fixId.Select(ch => Path.GetInvalidFileNameChars().Contains(ch) ? '_' : ch)) + ".json");
}
