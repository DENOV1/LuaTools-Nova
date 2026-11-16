// LuaTools Nova — Manage (library) ViewModel (ported from the MIT original's ManageViewModel).
using System.Collections.ObjectModel;
using System.IO;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using LuaTools.Core.Services;
using LuaTools.Infrastructure.Clients;

namespace LuaTools.Application.ViewModels;

public partial class ManageViewModel : PagedListViewModel<LuaTileViewModel>
{
    private readonly NovaServices _svc;
    private List<LuaTileViewModel> _all = [];
    private CancellationTokenSource? _prefetchCts;

    public Action<long>? NavigateToAdd { get; set; }
    public Action<long>? NavigateToBuilds { get; set; }
    public Action<long, string>? OpenLaunchOptions { get; set; }

    [ObservableProperty] private string _searchText = "";
    [ObservableProperty] private bool _isFilterPanelOpen;

    public const string AnyOption = "Any";

    public ObservableCollection<string> TypeOptions { get; } = new(new[] { AnyOption });
    public ObservableCollection<string> GenreOptions { get; } = new(new[] { AnyOption });
    public ObservableCollection<string> YearOptions { get; } = new(new[] { AnyOption });
    public ObservableCollection<string> PriceOptions { get; } = new(new[] { AnyOption, "Free", "Paid" });
    public ObservableCollection<string> ContentOptions { get; } = new(new[] { AnyOption, "Hide adult", "Adult only" });
    public ObservableCollection<string> SortOptions { get; } = new(new[]
    {
        "Recently added", "Name (A–Z)", "Release date (newest)", "Metacritic", "Most reviewed",
    });

    [ObservableProperty][NotifyPropertyChangedFor(nameof(HasActiveFilters))] private string _selectedType = AnyOption;
    [ObservableProperty][NotifyPropertyChangedFor(nameof(HasActiveFilters))] private string _selectedGenre = AnyOption;
    [ObservableProperty][NotifyPropertyChangedFor(nameof(HasActiveFilters))] private string _selectedYear = AnyOption;
    [ObservableProperty][NotifyPropertyChangedFor(nameof(HasActiveFilters))] private string _selectedPrice = AnyOption;
    [ObservableProperty][NotifyPropertyChangedFor(nameof(HasActiveFilters))] private string _selectedContent = AnyOption;
    [ObservableProperty] private string _selectedSort = "Recently added";

    protected override void SavePageSizeSetting(int size) => _svc.Settings.ManagePageSize = size;

    public bool HasActiveFilters =>
        SelectedType != AnyOption || SelectedGenre != AnyOption || SelectedYear != AnyOption ||
        SelectedPrice != AnyOption || SelectedContent != AnyOption;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(FilterPendingText))]
    private int _pendingDetailsCount;
    [ObservableProperty] private bool _hasPendingDetails;
    partial void OnPendingDetailsCountChanged(int value) => HasPendingDetails = value > 0;
    public string FilterPendingText => $"Fetching details for {PendingDetailsCount} apps";

    partial void OnSelectedTypeChanged(string value) => ApplyFilter();
    partial void OnSelectedGenreChanged(string value) => ApplyFilter();
    partial void OnSelectedYearChanged(string value) => ApplyFilter();
    partial void OnSelectedPriceChanged(string value) => ApplyFilter();
    partial void OnSelectedContentChanged(string value) => ApplyFilter();
    partial void OnSelectedSortChanged(string value) => ApplyFilter();

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(IsDetailOpen))]
    private LuaTileViewModel? _selectedTile;
    public bool IsDetailOpen => SelectedTile is not null;

    [ObservableProperty] private OverviewProxy? _overview;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(IsSelecting), nameof(SelectionLabel))]
    private int _selectedCount;
    public bool IsSelecting => SelectedCount > 0;
    public string SelectionLabel => $"{SelectedCount} selected";

    public ManageViewModel(NovaServices svc)
    {
        _svc = svc;
        InitPageSize(svc.Settings.ManagePageSize);
    }

    partial void OnSearchTextChanged(string value) => ApplyFilter();

    public async Task OpenDetailForAppIdAsync(long appId)
    {
        var tile = _all.FirstOrDefault(t => t.AppId == appId);
        if (tile is null)
        {
            await LoadAsync();
            tile = _all.FirstOrDefault(t => t.AppId == appId);
        }

        if (tile is not null) await OpenDetailAsync(tile);
        else _svc.Get<IToastService>().Show("Not found", "That game is no longer in your library.", error: true);
    }

    [RelayCommand]
    private async Task OpenDetailAsync(LuaTileViewModel tile)
    {
        SelectedTile = tile;
        Overview = ToOverview(_svc.AppInfo.GetOverview(tile.AppId));
        await tile.EnsureResolvedAsync(_svc.AppInfo, _svc.Covers);

        if (Overview is null && await _svc.AppInfo.EnsureFullDetailsAsync(tile.AppId) && SelectedTile == tile)
            Overview = ToOverview(_svc.AppInfo.GetOverview(tile.AppId));
    }

    private static OverviewProxy? ToOverview(AppOverview? o) =>
        o is null ? null : new OverviewProxy(o);

    [RelayCommand]
    private void CloseDetail()
    {
        SelectedTile = null;
        Overview = null;
    }

    [RelayCommand]
    private void ManageBuild(LuaTileViewModel tile) => NavigateToBuilds?.Invoke(tile.AppId);

    [RelayCommand]
    private void EditLaunchOptions(LuaTileViewModel tile) => OpenLaunchOptions?.Invoke(tile.AppId, tile.Name);

    [RelayCommand]
    private void OpenStorePage(LuaTileViewModel tile) =>
        _svc.Integrations.OpenUrl($"steam://store/{tile.AppId}");

    [RelayCommand]
    private void OpenInSteam(LuaTileViewModel tile) =>
        _svc.Integrations.OpenUrl($"steam://nav/games/details/{tile.AppId}");

    [RelayCommand]
    private void RevealFile(LuaTileViewModel tile) => _svc.Integrations.RevealPath(tile.FilePath);

    [RelayCommand]
    private void CopyAppId(LuaTileViewModel tile)
    {
        if (!_svc.Integrations.CopyToClipboard(tile.AppId.ToString()))
            _svc.Get<IToastService>().Show("Copy App ID", "Couldn't copy — another app is holding the clipboard.", error: true);
    }

    [RelayCommand]
    private void Update(LuaTileViewModel tile) => NavigateToAdd?.Invoke(tile.AppId);

    /// <summary>Steamless ships a .NET Framework CLI (Windows-only) — disabled on Linux with a visible reason.</summary>
    [RelayCommand]
    private void RemoveDrm(LuaTileViewModel tile) =>
        _svc.Get<IToastService>().Show("Remove Steam DRM",
            "Steamless is a Windows-only tool (.NET Framework CLI); there is no maintained Linux build, so this action is not available in LuaTools Nova.",
            error: true);

    [RelayCommand]
    private void Delete(LuaTileViewModel tile)
    {
        if (!_svc.Get<IDialogService>().Confirm("Remove lua file",
                $"Remove \"{tile.Name}\" (App ID {tile.AppId})?\n\nThis deletes its .lua file from Steam's config/stplug-in."))
            return;

        try
        {
            if (File.Exists(tile.FilePath)) File.Delete(tile.FilePath);
        }
        catch (Exception ex)
        {
            _svc.Get<IDialogService>().Info("Remove failed", $"Couldn't delete the file:\n{ex.Message}");
            return;
        }

        RemoveTile(tile);
    }

    private void RecountSelection() => SelectedCount = _all.Count(t => t.IsSelected);

    [RelayCommand]
    private void ClearSelection()
    {
        foreach (var t in _all.Where(t => t.IsSelected)) t.IsSelected = false;
        SelectedCount = 0;
    }

    [RelayCommand]
    private void DeleteSelected()
    {
        var targets = _all.Where(t => t.IsSelected).ToList();
        if (targets.Count == 0) return;

        if (!_svc.Get<IDialogService>().Confirm("Remove lua files",
                $"Remove {targets.Count} lua files?\n\nThis deletes the .lua files from Steam's config/stplug-in."))
            return;

        int failed = 0;
        foreach (var t in targets)
        {
            try
            {
                if (File.Exists(t.FilePath)) File.Delete(t.FilePath);
                t.SelectionChanged = null;
                _all.Remove(t);
                if (SelectedTile == t) CloseDetail();
            }
            catch { failed++; }
        }
        ApplyFilter();
        SelectedCount = _all.Count(t => t.IsSelected);

        if (failed > 0)
            _svc.Get<IDialogService>().Info("Remove failed", $"{failed} files couldn't be deleted.");
    }

    private void RemoveTile(LuaTileViewModel tile)
    {
        tile.SelectionChanged = null;
        _all.Remove(tile);
        if (SelectedTile == tile) CloseDetail();
        ApplyFilter();
    }

    public async Task LoadAsync()
    {
        if (IsLoading) return;
        IsLoading = true;
        try
        {
            string? dir = _svc.Installer.StPlugInDir;
            if (dir is null || !Directory.Exists(dir))
            {
                _all = [];
                ApplyFilter();
                SetEmpty(dir is null
                    ? "Steam location not found. Set it in Settings."
                    : "No lua files found yet. Add a game from the Add tab.");
                return;
            }

            await _svc.AppList.EnsureLoadedAsync();

            var tiles = await Task.Run(() =>
                LuaInstaller.EnumerateInstalled(dir)
                    .Select(f =>
                    {
                        var info = new FileInfo(f.Path);
                        string? name = _svc.AppList.GetName(f.AppId) ?? ParseLuaName(f.Path) ?? _svc.AppInfo.GetCached(f.AppId)?.Name;
                        bool placeholder = name is null;
                        var added = info.LastWriteTime > info.CreationTime ? info.LastWriteTime : info.CreationTime;
                        return new LuaTileViewModel(f.AppId, f.Path, added, name ?? $"App {f.AppId}", placeholder);
                    })
                    .OrderByDescending(t => t.AddedAt)
                    .ToList());

            foreach (var t in tiles) t.SelectionChanged = RecountSelection;
            _all = tiles;
            SelectedCount = 0;
            PopulateFilterOptions();
            ApplyFilter();
            if (_all.Count == 0) SetEmpty("No lua files found yet. Add a game from the Add tab.");

            StartCoverPrefetch(_all);
        }
        finally { IsLoading = false; }
    }

    private void StartCoverPrefetch(IReadOnlyList<LuaTileViewModel> tiles)
    {
        _prefetchCts?.Cancel();
        var cts = _prefetchCts = new CancellationTokenSource();
        var appids = tiles.Select(t => t.AppId).ToList();
        _ = Task.Run(async () =>
        {
            try
            {
                await Parallel.ForEachAsync(
                    appids,
                    new ParallelOptions { MaxDegreeOfParallelism = 8, CancellationToken = cts.Token },
                    async (appid, _) =>
                    {
                        try { await LuaTileViewModel.ResolveCoverFileAsync(appid, _svc.AppInfo, _svc.Covers); }
                        catch { /* one cover failing shouldn't stop the rest */ }
                    });

                var lastUi = DateTime.MinValue;
                await _svc.AppInfo.BackfillFullDetailsAsync(appids, onProgress: () =>
                {
                    if (cts.Token.IsCancellationRequested) return;
                    if (DateTime.UtcNow - lastUi < TimeSpan.FromSeconds(2)) return;
                    lastUi = DateTime.UtcNow;
                    PopulateFilterOptions();
                    RefreshPendingCount();
                }, cts.Token);

                if (!cts.Token.IsCancellationRequested)
                {
                    PopulateFilterOptions();
                    ApplyFilter(resetPage: false);
                }
            }
            catch (OperationCanceledException) { }
        });
    }

    [RelayCommand]
    private Task Refresh() => RefreshWithCooldownAsync(async () =>
    {
        if (!string.IsNullOrEmpty(SearchText)) SearchText = "";
        await LoadAsync();
        _svc.Get<IToastService>().Show("Library refreshed", $"{_all.Count} games on board.");
    });

    private void SetEmpty(string message) => EmptyMessage = message;

    private void RefreshPendingCount() =>
        PendingDetailsCount = _all.Count(t => _svc.AppInfo.GetFilterData(t.AppId) is null);

    private void ApplyFilter(bool resetPage = true)
    {
        string q = SearchText.Trim();
        bool filtersActive = HasActiveFilters;

        IEnumerable<LuaTileViewModel> result = _all;

        if (!string.IsNullOrEmpty(q)) result = result.Where(t => t.Matches(q));

        result = result.Where(t =>
        {
            var data = _svc.AppInfo.GetFilterData(t.AppId);
            t.DetailsLoaded = data is not null;
            t.UpdateReleaseLabel(_svc.AppInfo);
            if (!filtersActive) return true;
            if (data is null) return false;
            return MatchesFilters(data);
        });

        var list = result.ToList();
        list = SortTiles(list);

        PendingDetailsCount = _all.Count(t => _svc.AppInfo.GetFilterData(t.AppId) is null);

        EmptyMessage = _all.Count > 0
            ? "No games match your filters."
            : "No lua files found yet. Add a game from the Add tab.";

        SetFiltered(list, resetPage);
    }

    private bool MatchesFilters(AppFilterData d)
    {
        if (SelectedType != AnyOption &&
            !string.Equals(d.Type, SelectedType, StringComparison.OrdinalIgnoreCase)) return false;

        if (SelectedGenre != AnyOption &&
            !d.Genres.Any(g => string.Equals(g, SelectedGenre, StringComparison.OrdinalIgnoreCase))) return false;

        if (SelectedYear != AnyOption &&
            (d.ReleaseYear is null || d.ReleaseYear.Value.ToString() != SelectedYear)) return false;

        if (SelectedPrice != AnyOption)
        {
            if (SelectedPrice == "Free" && !d.IsFree) return false;
            if (SelectedPrice == "Paid" && d.IsFree) return false;
        }

        if (SelectedContent == "Hide adult" && d.IsAdult) return false;
        if (SelectedContent == "Adult only" && !d.IsAdult) return false;

        return true;
    }

    private List<LuaTileViewModel> SortTiles(List<LuaTileViewModel> tiles) => SelectedSort switch
    {
        "Name (A–Z)" => tiles.OrderBy(t => t.Name, StringComparer.OrdinalIgnoreCase).ToList(),
        "Release date (newest)" => tiles
            .OrderByDescending(t => _svc.AppInfo.GetFilterData(t.AppId)?.ReleaseDate ?? DateTime.MinValue).ToList(),
        "Metacritic" => tiles
            .OrderByDescending(t => _svc.AppInfo.GetFilterData(t.AppId)?.Metacritic ?? int.MinValue).ToList(),
        "Most reviewed" => tiles
            .OrderByDescending(t => _svc.AppInfo.GetFilterData(t.AppId)?.Reviews ?? long.MinValue).ToList(),
        _ => tiles.OrderByDescending(t => t.AddedAt).ToList(),
    };

    [RelayCommand]
    private void ClearFilters()
    {
        SelectedType = AnyOption;
        SelectedGenre = AnyOption;
        SelectedYear = AnyOption;
        SelectedPrice = AnyOption;
        SelectedContent = AnyOption;
        OnPropertyChanged(nameof(HasActiveFilters));
        ApplyFilter();
    }

    [RelayCommand]
    private void ToggleFilterPanel() => IsFilterPanelOpen = !IsFilterPanelOpen;

    private void PopulateFilterOptions()
    {
        var types = new SortedSet<string>(StringComparer.OrdinalIgnoreCase);
        var genres = new SortedSet<string>(StringComparer.OrdinalIgnoreCase);
        var years = new SortedSet<int>();

        foreach (var t in _all)
        {
            var d = _svc.AppInfo.GetFilterData(t.AppId);
            if (d is null) continue;
            if (!string.IsNullOrWhiteSpace(d.Type)) types.Add(d.Type!);
            foreach (var g in d.Genres) genres.Add(g);
            if (d.ReleaseYear is { } y) years.Add(y);
        }

        RebuildOptionList(TypeOptions, types);
        RebuildOptionList(GenreOptions, genres);
        RebuildOptionList(YearOptions, years.OrderByDescending(y => y).Select(y => y.ToString()));
    }

    private static void RebuildOptionList(ObservableCollection<string> target, IEnumerable<string> values)
    {
        var wanted = values.ToList();
        if (target.Count == 0 || target[0] != AnyOption) target.Insert(0, AnyOption);

        for (int i = target.Count - 1; i >= 1; i--)
            if (!wanted.Contains(target[i]))
                target.RemoveAt(i);

        foreach (var v in wanted)
            if (!target.Contains(v))
                target.Add(v);
    }

    private static string? ParseLuaName(string path)
    {
        try
        {
            using var reader = new StreamReader(path);
            string? line0 = reader.ReadLine();
            string? line1 = reader.ReadLine();
            if (line0 is not null && line1 is not null &&
                line0.Contains("Created by", StringComparison.OrdinalIgnoreCase) &&
                line1.StartsWith("--"))
            {
                string name = line1.TrimStart('-', ' ').Trim();
                return string.IsNullOrWhiteSpace(name) ? null : name;
            }
        }
        catch { /* fall back to appid */ }
        return null;
    }

    /// <summary>Bindable wrap over AppOverview (string lists don't two-bind into compiled bindings).</summary>
    public sealed class OverviewProxy(AppOverview o)
    {
        public string? ShortDescription => o.ShortDescription;
        public bool HasDescription => o.HasDescription;
        public string DeveloperLabel => o.DeveloperLabel;
        public bool HasDeveloper => o.HasDeveloper;
        public string PublisherLabel => o.PublisherLabel;
        public bool HasDistinctPublisher => o.HasDistinctPublisher;
        public IReadOnlyList<string> Genres => o.Genres;
        public bool HasGenres => o.HasGenres;
        public bool HasAnything => o.HasAnything;
    }
}
