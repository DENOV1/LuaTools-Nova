// LuaTools Nova — Home page ViewModel (ported from the MIT original's HomeViewModel).
using System.Collections.ObjectModel;
using System.IO;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using LuaTools.Core.Services;
using LuaTools.Infrastructure.Clients;

namespace LuaTools.Application.ViewModels;

/// <summary>One installed lua file (a game added via config/stplug-in/&lt;appid&gt;.lua).</summary>
public partial class LuaTileViewModel : ObservableObject
{
    public long AppId { get; }
    public string FilePath { get; }
    public DateTime AddedAt { get; }

    public string AddedLabel =>
        "Added " + AddedAt.ToString(@"MMM d, \'yy", System.Globalization.CultureInfo.InvariantCulture);

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(AddedReleaseLabel))]
    private string _releaseLabel = "";

    public string AddedReleaseLabel =>
        string.IsNullOrEmpty(ReleaseLabel) ? AddedLabel : $"{AddedLabel}  •  {ReleaseLabel}";

    private int _resolving;
    private bool _nameIsPlaceholder;

    [ObservableProperty] private string _name;
    [ObservableProperty] private string? _coverPath;
    [ObservableProperty] private bool _isSelected;
    [ObservableProperty] private bool _detailsLoaded;
    [ObservableProperty] private string? _variantBadge;

    public Action? SelectionChanged { get; set; }
    partial void OnIsSelectedChanged(bool value) => SelectionChanged?.Invoke();

    public LuaTileViewModel(long appId, string filePath, DateTime addedAt, string name, bool nameIsPlaceholder)
    {
        AppId = appId;
        FilePath = filePath;
        AddedAt = addedAt;
        _name = name;
        _nameIsPlaceholder = nameIsPlaceholder;
    }

    public void UpdateReleaseLabel(SteamAppInfoCache appInfo)
    {
        var data = appInfo.GetFilterData(AppId);
        string? text = data?.ReleaseDateText;
        if (string.IsNullOrWhiteSpace(text)) { ReleaseLabel = ""; return; }

        text = System.Text.RegularExpressions.Regex.Replace(text, @"\b(19|20)(\d{2})\b", "'$2");
        ReleaseLabel = $"Released {text}";
    }

    public async Task EnsureResolvedAsync(SteamAppInfoCache appInfo, CoverCache covers)
    {
        if (string.IsNullOrEmpty(ReleaseLabel)) UpdateReleaseLabel(appInfo);

        if (CoverPath is not null) return;
        if (Interlocked.Exchange(ref _resolving, 1) == 1) return;
        try
        {
            string? local = await ResolveCoverFileAsync(AppId, appInfo, covers,
                name => { if (_nameIsPlaceholder) Name = name; });

            if (_nameIsPlaceholder)
            {
                var info = appInfo.GetCached(AppId) ?? await appInfo.ResolveAsync(AppId);
                if (!string.IsNullOrWhiteSpace(info?.Name)) Name = info!.Name;
            }

            if (local is not null) CoverPath = local;
        }
        finally { Interlocked.Exchange(ref _resolving, 0); }
    }

    public static async Task<string?> ResolveCoverFileAsync(
        long appId, SteamAppInfoCache appInfo, CoverCache covers, Action<string>? onName = null)
    {
        string? local = covers.GetLocalPath(appId);
        if (local is not null) return local;
        if (covers.IsKnownMissing(appId)) return null;

        local = await covers.EnsureAsync(appId, SteamAppInfoCache.GuessHeaderImageUrl(appId));
        if (local is not null) return local;

        var info = appInfo.GetCached(appId) ?? await appInfo.ResolveAsync(appId);
        if (onName is not null && !string.IsNullOrWhiteSpace(info?.Name)) onName(info!.Name);
        if (!string.IsNullOrWhiteSpace(info?.HeaderImage))
            local = await covers.EnsureAsync(appId, info!.HeaderImage!);

        if (local is null && info is not null) covers.MarkMissing(appId);
        return local;
    }

    public bool Matches(string q) =>
        Name.Contains(q, StringComparison.OrdinalIgnoreCase) || AppId.ToString().Contains(q);
}

public partial class HomeViewModel : ObservableObject
{
    public Action<long>? NavigateToGame { get; set; }
    public Action? NavigateToPlugin { get; set; }
    public Action? NavigateToManage { get; set; }
    public Action? NavigateToSettings { get; set; }
    public Action? NavigateToMode { get; set; }

    private readonly NovaServices _svc;

    public DropInstallViewModel Drop { get; }

    [ObservableProperty] private int _gameCount;

    [ObservableProperty] private string _pluginStatusText = "Checking…";
    [ObservableProperty] private string _pluginStatusColor = "#9ca3af";
    [ObservableProperty] private bool _showPluginInstall;
    [ObservableProperty] private bool _isInstallingPlugin;
    public bool NotInstallingPlugin => !IsInstallingPlugin;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(HasRecent))]
    private ObservableCollection<LuaTileViewModel> _recent = new();
    public bool HasRecent => Recent.Count > 0;

    [ObservableProperty] private bool _steamFound;
    [ObservableProperty] private string _steamStatus = "Checking Steam…";
    [ObservableProperty] private string _accountStatus = "Browsing as guest";
    [ObservableProperty] private string _modeStatus = "No mode selected";

    public HomeViewModel(NovaServices svc, ViewModels.DropInstallViewModel? drop = null)
    {
        _svc = svc;
        Drop = drop ?? new DropInstallViewModel(svc);
    }

    [RelayCommand] private void OpenGame(LuaTileViewModel tile) => NavigateToGame?.Invoke(tile.AppId);
    [RelayCommand] private void OpenPlugin() => NavigateToPlugin?.Invoke();
    [RelayCommand] private void OpenManage() => NavigateToManage?.Invoke();
    [RelayCommand] private void OpenSettings() => NavigateToSettings?.Invoke();

    /// <summary>v1.0.3 bug 5/6: the Home "Steam" row was mis-bound to OpenSettings (it looked like a
    /// second, emptier Settings button). It is now the Restart Steam control the original has —
    /// backed by ProcessControl's Linux-native stop/start ladder (shutdown → SIGTERM → SIGKILL,
    /// systemd-unit fast path on SteamOS/Bazzite).</summary>
    public bool IsSteamRunning => _svc.ProcessControl.IsSteamRunning();

    [RelayCommand]
    private async Task RestartSteamAsync()
    {
        if (!await _svc.Get<IDialogService>().ConfirmAsync("Restart Steam",
                "Restart Steam now? This closes Steam and any running games, then relaunches it."))
            return;
        bool ok = await Task.Run(() => _svc.ProcessControl.RestartSteam());
        OnPropertyChanged(nameof(IsSteamRunning));
        if (!ok) await _svc.Get<IDialogService>().InfoAsync("Restart Steam", "Couldn't find or launch Steam. Set its location in Settings.");
        else _svc.Get<IToastService>().Show("Steam", "Steam restarted. Library changes are now active.");
    }
    [RelayCommand] private void OpenMode() => NavigateToMode?.Invoke();

    public async Task LoadAsync()
    {
        RefreshSteam();
        RefreshAccount();
        RefreshMode();
        _ = RefreshPluginStatusAsync();
        await RefreshLibraryAsync();
    }

    public Func<Task<(bool, string?)>>? InstallPluginAsync { get; set; }

    [RelayCommand]
    private async Task InstallPlugin()
    {
        if (IsInstallingPlugin || InstallPluginAsync is null) return;
        IsInstallingPlugin = true;
        PluginStatusText = "Checking…";
        PluginStatusColor = "#9ca3af";
        try
        {
            var (ok, error) = await InstallPluginAsync();
            _svc.Get<IToastService>().Show("Plugin", ok
                ? "Plugin installed. Restart Steam to load it."
                : $"Install failed: {error}", error: !ok);
        }
        finally
        {
            IsInstallingPlugin = false;
            await RefreshPluginStatusAsync();
        }
    }

    private async Task RefreshPluginStatusAsync()
    {
        try
        {
            var st = await _svc.Get<Integrations.PluginInstallerService>().GetStatusAsync(force: false);
            bool installed = st.FrontendInstalled;
            ShowPluginInstall = !installed;
            (PluginStatusText, PluginStatusColor) =
                !installed ? ("Not installed", "#9ca3af")
                : st.UpdateAvailable ? ("Update available", "#fbbf24")
                : (st.InstalledTag is { } tag ? $"Installed · {tag}" : "Installed", "#34d399");
        }
        catch { /* leave the prior value */ }
    }

    private void RefreshMode()
    {
        var mode = _svc.Get<UnlockerServiceProxy>().SelectedMode;
        ModeStatus = mode is { } m
            ? $"Mode · {Enum.GetName(typeof(Core.Models.UnlockerMode), m)}"
            : "No mode selected";
    }

    private void RefreshSteam()
    {
        SteamFound = _svc.Steam.IsValid;
        SteamStatus = SteamFound
            ? $"Steam detected · {_svc.Steam.EffectivePath}"
            : "Steam not found. Set its location in Settings.";
    }

    private void RefreshAccount()
    {
        var auth = _svc.Auth;
        AccountStatus = auth.IsSignedIn
            ? (auth.DisplayName is { } n ? $"Signed in as {n}" : "Signed in")
            : "Browsing as guest";
    }

    public async Task RefreshLibraryAsync()
    {
        string? dir = _svc.Installer.StPlugInDir;
        if (dir is null || !Directory.Exists(dir))
        {
            GameCount = 0;
            Recent = new ObservableCollection<LuaTileViewModel>();
            return;
        }

        await _svc.AppList.EnsureLoadedAsync();

        var tiles = await Task.Run(() =>
            LuaInstaller.EnumerateInstalled(dir)
                .Select(f =>
                {
                    var info = new FileInfo(f.Path);
                    string? name = _svc.AppList.GetName(f.AppId) ?? _svc.AppInfo.GetCached(f.AppId)?.Name;
                    var added = info.LastWriteTime > info.CreationTime ? info.LastWriteTime : info.CreationTime;
                    return new LuaTileViewModel(f.AppId, f.Path, added, name ?? $"App {f.AppId}", name is null);
                })
                .OrderByDescending(t => t.AddedAt)
                .ToList());

        GameCount = tiles.Count;
        var recent = tiles.Take(4).ToList();
        Recent = new ObservableCollection<LuaTileViewModel>(recent);
        foreach (var t in recent) _ = t.EnsureResolvedAsync(_svc.AppInfo, _svc.Covers);
    }
}

/// <summary>Windowed paged list shared by Manage/Fixes/Builds (the original's PagedListViewModel).</summary>
public abstract partial class PagedListViewModel<T> : ObservableObject
{
    protected List<T> _filtered = [];
    private DateTime _lastRefresh;
    private int _filteredCount;
    private bool _suppressPageSlice;

    public Action? ScrollToTop { get; set; }

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(HasItems), nameof(ShowItems), nameof(IsEmpty))]
    private ObservableCollection<T> _items = new();

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(HasItems), nameof(ShowItems), nameof(IsEmpty))]
    private bool _isLoading;

    [ObservableProperty] private string _emptyMessage = "";

    public bool HasItems => Items.Count > 0;
    public bool ShowItems => HasItems && !IsLoading;
    public bool IsEmpty => !IsLoading && Items.Count == 0;

    public const string AllPages = "All";
    public ObservableCollection<string> PageSizeOptions { get; } = new(new[] { "12", "24", "48", AllPages });

    [ObservableProperty] private string _selectedPageSize = "24";
    public int PageSize => SelectedPageSize == AllPages ? 0 : int.Parse(SelectedPageSize);

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(CanGoPrev), nameof(CanGoNext), nameof(PageLabel))]
    private int _currentPage = 1;

    public int TotalPages => PageSize == 0 ? 1 : Math.Max(1, (int)Math.Ceiling(_filteredCount / (double)PageSize));
    public ObservableCollection<int> PageNumbers { get; } = new();
    public bool CanGoPrev => CurrentPage > 1;
    public bool CanGoNext => CurrentPage < TotalPages;
    public bool ShowPager => PageSize != 0 && TotalPages > 1;
    public string PageLabel => $"Page {CurrentPage} of {TotalPages}";

    partial void OnSelectedPageSizeChanged(string value)
    {
        SavePageSizeSetting(PageSize);
        _suppressPageSlice = true;
        CurrentPage = 1;
        OnPropertyChanged(nameof(TotalPages));
        OnPropertyChanged(nameof(ShowPager));
        _suppressPageSlice = false;
        ApplyPageSlice();
    }

    partial void OnCurrentPageChanged(int value) { if (!_suppressPageSlice) ApplyPageSlice(); }

    [RelayCommand] private void PrevPage() { if (CanGoPrev) CurrentPage--; }
    [RelayCommand] private void NextPage() { if (CanGoNext) CurrentPage++; }
    [RelayCommand]
    private void GoToPage(int page)
    {
        if (page < 1 || page > TotalPages || page == CurrentPage) return;
        CurrentPage = page;
    }

    protected void InitPageSize(int persisted)
    {
        _selectedPageSize = persisted == 0 ? AllPages : persisted.ToString();
    }

    protected virtual void SavePageSizeSetting(int size) { }
    protected virtual void OnPageSliced(IReadOnlyList<T> slice) { }

    protected void SetFiltered(IEnumerable<T> filtered, bool resetPage = true)
    {
        _suppressPageSlice = true;
        if (resetPage) CurrentPage = 1;

        _filtered = filtered as List<T> ?? filtered.ToList();
        _filteredCount = _filtered.Count;

        OnPropertyChanged(nameof(TotalPages));
        OnPropertyChanged(nameof(ShowPager));
        if (CurrentPage > TotalPages) CurrentPage = TotalPages;
        _suppressPageSlice = false;
        ApplyPageSlice();
    }

    private void ApplyPageSlice()
    {
        var slice = PageSize == 0
            ? _filtered
            : _filtered.Skip((CurrentPage - 1) * PageSize).Take(PageSize).ToList();

        Items = new ObservableCollection<T>(slice);
        RebuildPageNumbers();
        OnPropertyChanged(nameof(CanGoPrev));
        OnPropertyChanged(nameof(CanGoNext));
        OnPropertyChanged(nameof(PageLabel));
        ScrollToTop?.Invoke();
        OnPageSliced(slice);
    }

    private void RebuildPageNumbers()
    {
        PageNumbers.Clear();
        int total = TotalPages, cur = CurrentPage;
        if (PageSize == 0 || total <= 1) return;

        void Add(int n) => PageNumbers.Add(n);
        if (total <= 9)
        {
            for (int i = 1; i <= total; i++) Add(i);
            return;
        }

        Add(1);
        int start = Math.Max(2, cur - 1), end = Math.Min(total - 1, cur + 1);
        if (start > 2) Add(0);
        for (int i = start; i <= end; i++) Add(i);
        if (end < total - 1) Add(0);
        Add(total);
    }

    protected async Task RefreshWithCooldownAsync(Func<Task> reload)
    {
        if (DateTime.UtcNow - _lastRefresh < TimeSpan.FromSeconds(1) || IsLoading) return;
        _lastRefresh = DateTime.UtcNow;
        await reload();
    }
}
