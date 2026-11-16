// LuaTools Nova — Builds/Depots page ViewModel (ported from the MIT original's BuildsViewModel).
using System.Collections.ObjectModel;
using System.IO;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using LuaTools.Core.Downloads;
using LuaTools.Core.Lua;
using LuaTools.Core.Models;
using LuaTools.Core.Services;

namespace LuaTools.Application.ViewModels;

public record DepotRow(
    long Id, string Title, string Meta, bool IsDlc, bool IsShared, string SteamDbUrl,
    string? ManifestId, string? CommentedManifestId, string? PublicManifestId,
    bool IsInLua = false, bool IsEnabled = false, bool CanToggle = false, bool IsBaseApp = false)
{
    public bool IsLocked => ManifestId is not null;
    public bool CanLock => CanToggle && (ManifestId is not null || CommentedManifestId is not null);
    public bool CanEnable => CanToggle && !IsBaseApp;
    public long ToggleId { get; init; }
    public long Size { get; init; }
    public string? Os { get; init; }
    public string? Language { get; init; }
    public long? FromAppId { get; init; }
    public long LuaSize { get; init; }

    public bool Matches(string query)
    {
        if (string.IsNullOrWhiteSpace(query)) return true;
        string q = query.Trim();

        bool Has(string? s) => s is not null && s.Contains(q, StringComparison.OrdinalIgnoreCase);

        return Has(Title)
            || Has(Meta)
            || Has(Id.ToString())
            || Has(ToggleId.ToString())
            || Has(ManifestId)
            || Has(CommentedManifestId)
            || (IsDlc && Has("DLC"))
            || (IsShared && Has("SHARED"));
    }

    public string? ManifestLabel =>
        ManifestId is not null ? $"manifest {ManifestId}"
        : CommentedManifestId is not null
            ? $"manifest {CommentedManifestId}  ·  pin off (Auto Update)"
            : null;

    public bool HasManifest => ManifestLabel is not null;
    public bool IsPinActive => ManifestId is not null;

    public string? OutdatedLabel =>
        ManifestId is not null && PublicManifestId is not null && ManifestId != PublicManifestId
            ? $"latest is {PublicManifestId}"
            : null;

    public bool IsOutdated => OutdatedLabel is not null;
}

public partial class VariantRowViewModel : ObservableObject
{
    public LuaVariant Variant { get; }
    public string Hash => Variant.Hash;
    public string? BuildId => Variant.BuildId;

    [ObservableProperty] private bool _isActive;
    public bool HasPendingEdit { get; init; }
    public string Label => Variant.DisplayLabel;

    public string Meta
    {
        get
        {
            var parts = new List<string>
            {
                $"{Variant.DepotCount} depots · {Variant.DlcCount} DLC",
                Variant.IsPinned ? "Pinned" : "Auto-updating",
            };
            if (!string.IsNullOrWhiteSpace(Variant.Source)) parts.Add($"from {Variant.Source}");
            if (HasPendingEdit) parts.Add("unsaved");
            return string.Join("  ·  ", parts);
        }
    }

    public VariantRowViewModel(LuaVariant variant) => Variant = variant;
}

public partial class BuildsViewModel : PagedListViewModel<LuaTileViewModel>
{
    private readonly NovaServices _svc;
    private List<LuaTileViewModel> _allGames = [];
    private long _depotLoadToken;
    private Task? _loading;

    [ObservableProperty] private string _searchText = "";
    partial void OnSearchTextChanged(string value) => ApplyGameFilter();

    [ObservableProperty] private LuaTileViewModel? _selectedGame;
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(HasSelection))]
    private LuaTileViewModel? _activeGame;
    public bool HasSelection => ActiveGame is not null;

    partial void OnActiveGameChanged(LuaTileViewModel? value)
    {
        CancelEdit();
        IsRenaming = false;
        DepotSearchText = "";
        RefreshVariants();
        _ = LoadDepotsAsync();
    }

    protected override void SavePageSizeSetting(int size) => _svc.Settings.BuildsPageSize = size;

    public Action<long>? NavigateToManage { get; set; }

    public ObservableCollection<VariantRowViewModel> Variants { get; } = new();

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(CanApply), nameof(CanDelete), nameof(CanRename))]
    private VariantRowViewModel? _selectedVariant;

    private bool _suppressDepotReload;

    partial void OnSelectedVariantChanged(VariantRowViewModel? value)
    {
        IsRenaming = false;
        if (!_suppressDepotReload) _ = LoadDepotsAsync();
    }

    private bool SelectionDescribesLive => SelectedVariant is null or { IsActive: true };

    public bool CanApply => SelectedVariant is { IsActive: false };
    public bool CanDelete => SelectedVariant is { IsActive: false };
    public bool CanRename => SelectedVariant is not null;

    [ObservableProperty] private string? _latestBuildLabel;
    private bool _refreshingVariants;

    public ObservableCollection<DepotRow> InLua { get; } = new();
    public ObservableCollection<DepotRow> Missing { get; } = new();
    public ObservableCollection<DepotRow> Unknown { get; } = new();

    [ObservableProperty] private string _depotSearchText = "";
    [ObservableProperty] private bool _isInLuaExpanded = true;
    [ObservableProperty] private bool _isMissingExpanded = true;
    [ObservableProperty] private bool _isUnknownExpanded;
    [ObservableProperty] private bool _isLoadingDepots;
    [ObservableProperty] private string? _depotError;
    [ObservableProperty] private bool _editingInactiveBuild;

    public int InLuaCount => InLua.Count;
    public int MissingCount => Missing.Count;
    public int UnknownCount => Unknown.Count;
    public bool HasInLua => InLua.Count > 0;
    public bool HasMissing => Missing.Count > 0;
    public bool HasUnknown => Unknown.Count > 0;

    partial void OnDepotSearchTextChanged(string value) => ApplyDepotFilter();

    [ObservableProperty] private bool _isEditing;
    [ObservableProperty] private string _editorText = "";
    [ObservableProperty] private bool _isRenaming;
    [ObservableProperty] private string _renameText = "";

    public BuildsViewModel(NovaServices svc)
    {
        _svc = svc;
        PageSizeOptions.Clear();
        foreach (string option in new[] { "10", "17", "25", "50", AllPages }) PageSizeOptions.Add(option);
        InitPageSize(svc.Settings.BuildsPageSize);
        if (!PageSizeOptions.Contains(SelectedPageSize)) SelectedPageSize = "10";

        _svc.Vault.VaultChanged += appId =>
        {
            if (ActiveGame?.AppId == appId) RefreshVariants();
        };
    }

    public Task LoadAsync()
    {
        if (_loading is { IsCompleted: false }) return _loading;
        return _loading = LoadCoreAsync();
    }

    private async Task LoadCoreAsync()
    {
        IsLoading = true;
        try
        {
            string? dir = _svc.Installer.StPlugInDir;
            if (dir is null || !Directory.Exists(dir))
            {
                _allGames = [];
                EmptyMessage = dir is null
                    ? "Steam location not found. Set it in Settings."
                    : "No lua files found yet. Add a game from the Add tab.";
                ApplyGameFilter();
                return;
            }

            await _svc.AppList.EnsureLoadedAsync();

            var games = await Task.Run(() =>
            {
                var installed = LuaInstaller.EnumerateInstalled(dir).ToDictionary(f => f.AppId, f => f.Path);
                var appIds = new HashSet<long>(installed.Keys);
                foreach (var (appId, _, _) in _svc.Vault.EnumerateLooseBuildLuas()) appIds.Add(appId);

                return appIds
                    .Select(appId =>
                    {
                        string path = installed.TryGetValue(appId, out var p) ? p : Path.Combine(dir, $"{appId}.lua");
                        string? name = _svc.AppList.GetName(appId) ?? _svc.AppInfo.GetCached(appId)?.Name;
                        var added = File.Exists(path) ? File.GetLastWriteTime(path) : DateTime.MinValue;
                        return new LuaTileViewModel(appId, path, added, name ?? $"App {appId}", name is null);
                    })
                    .OrderBy(t => t.Name, StringComparer.OrdinalIgnoreCase)
                    .ToList();
            });

            _allGames = games;
            EmptyMessage = "No lua files found yet. Add a game from the Add tab.";
            ApplyGameFilter();
            await RefreshBadgesAsync();

            if (ActiveGame is { } prev)
            {
                ActiveGame = _allGames.FirstOrDefault(g => g.AppId == prev.AppId);
                SelectedGame = Items.FirstOrDefault(g => g.AppId == prev.AppId);
            }
        }
        finally { IsLoading = false; }
    }

    private void ApplyGameFilter()
    {
        string q = SearchText.Trim();
        SetFiltered(string.IsNullOrEmpty(q) ? _allGames : _allGames.Where(g => g.Matches(q)).ToList());
    }

    private async Task RefreshBadgesAsync()
    {
        var games = _allGames.ToList();
        var badges = await Task.Run(() => games.ToDictionary(g => g.AppId, g => BadgeFor(g.AppId)));
        foreach (var g in games)
            if (badges.TryGetValue(g.AppId, out string? badge)) g.VariantBadge = badge;
    }

    private string? BadgeFor(long appId)
    {
        if (!_svc.Vault.HasVariants(appId)) return null;
        string? activeHash = _svc.Vault.GetActiveHash(appId);
        if (activeHash is null) return null;
        return _svc.Vault.GetVariants(appId).FirstOrDefault(v => v.Hash == activeHash)?.DisplayLabel;
    }

    public async Task SelectAppAsync(long appId)
    {
        var game = _allGames.FirstOrDefault(g => g.AppId == appId);
        if (game is null)
        {
            await LoadAsync();
            game = _allGames.FirstOrDefault(g => g.AppId == appId);
        }
        if (game is null)
        {
            _svc.Get<IToastService>().Show("Not found", "That game is no longer in your library.", error: true);
            return;
        }

        if (!_filtered.Contains(game)) { SearchText = ""; ApplyGameFilter(); }

        int index = _filtered.IndexOf(game);
        if (index >= 0 && PageSize > 0) CurrentPage = index / PageSize + 1;

        ActiveGame = game;
        SelectedGame = Items.FirstOrDefault(g => g.AppId == appId);
    }

    public void ResolveGame(LuaTileViewModel game) => _ = game.EnsureResolvedAsync(_svc.AppInfo, _svc.Covers);

    [RelayCommand]
    private void OpenInManage()
    {
        if (ActiveGame is { } game) NavigateToManage?.Invoke(game.AppId);
    }

    [RelayCommand]
    private Task Refresh()
    {
        if (ActiveGame is { } game) _svc.DepotInfo.Invalidate(game.AppId);
        return LoadAsync();
    }

    private void RefreshVariants()
    {
        if (_refreshingVariants) return;
        _refreshingVariants = true;
        try { RefreshVariantsCore(); }
        finally { _refreshingVariants = false; }
    }

    private void RefreshVariantsCore()
    {
        Variants.Clear();
        SelectedVariant = null;

        if (ActiveGame is not { } game) return;

        _svc.Vault.SyncDefaultFromLive(game.AppId);
        _svc.Vault.AdoptLooseBuildLuas(game.AppId);
        _svc.Vault.ApplyBuildIfNothingLive(game.AppId);

        string? liveHash = _svc.Vault.GetActiveHash(game.AppId);
        var stored = _svc.Vault.GetVariants(game.AppId);

        string? editBase = _svc.Vault.GetEditBase(game.AppId);
        string? activeHash = ResolveActiveHash(liveHash, stored, editBase);
        string? pendingHash = activeHash != liveHash ? activeHash : null;

        if (pendingHash is null && editBase is not null) _svc.Vault.SetEditBase(game.AppId, null);

        foreach (var v in stored)
            Variants.Add(new VariantRowViewModel(v)
            {
                IsActive = v.Hash == activeHash,
                HasPendingEdit = v.Hash == pendingHash,
            });

        SelectedVariant = Variants.FirstOrDefault(v => v.IsActive) ?? Variants.FirstOrDefault();

        string? badge = BadgeFor(game.AppId);
        foreach (var g in _allGames.Where(g => g.AppId == game.AppId)) g.VariantBadge = badge;

        OnPropertyChanged(nameof(EditBaseVariant));
        OnPropertyChanged(nameof(HasEditBase));
        OnPropertyChanged(nameof(SaveInPlaceLabel));
    }

    public static string? ResolveActiveHash(
        string? liveHash, IReadOnlyList<LuaVariant> stored, string? editBase)
    {
        if (liveHash is null) return null;
        if (stored.Any(v => v.Hash == liveHash)) return liveHash;
        return editBase is not null && stored.Any(v => v.Hash == editBase) ? editBase : liveHash;
    }

    [RelayCommand]
    private void Apply()
    {
        if (ActiveGame is not { } game || SelectedVariant?.Variant is not { } variant) return;

        if (!_svc.Vault.Apply(game.AppId, variant))
        {
            _svc.Get<IToastService>().Show("Depots", "Couldn't switch presets. The lua file may be in use.", error: true);
            return;
        }

        RefreshVariants();
        _svc.Get<IToastService>().Show("Depots", $"Now using \"{variant.DisplayLabel}\".");
    }

    [RelayCommand]
    private void DeleteVariant()
    {
        if (ActiveGame is not { } game || SelectedVariant?.Variant is not { } variant) return;

        if (!_svc.Get<IDialogService>().Confirm("Delete this preset?",
                $"\"{variant.DisplayLabel}\" will be removed from your saved presets. This doesn't affect the game itself."))
            return;

        if (_svc.Vault.Delete(game.AppId, variant.Hash)) RefreshVariants();
    }

    [RelayCommand]
    private void StartRename()
    {
        if (SelectedVariant?.Variant is not { } v) return;
        RenameText = v.Label ?? v.DisplayLabel;
        IsRenaming = true;
    }

    [RelayCommand]
    private void CommitRename()
    {
        if (ActiveGame is { } game && SelectedVariant?.Variant is { } v)
            _svc.Vault.Rename(game.AppId, v.Hash, RenameText);
        IsRenaming = false;
        RefreshVariants();
    }

    [RelayCommand] private void CancelRename() => IsRenaming = false;

    [RelayCommand]
    private void Edit()
    {
        if (ActiveGame is not { } game) return;
        EditorText = _svc.Vault.ReadLiveText(game.AppId);
        IsEditing = true;
    }

    [RelayCommand]
    private void CancelEdit()
    {
        IsEditing = false;
        EditorText = "";
    }

    [RelayCommand]
    private void SaveEdit()
    {
        if (ActiveGame is not { } game) return;

        RememberEditBase(game.AppId);
        if (!_svc.Vault.WriteLive(game.AppId, EditorText))
        {
            _svc.Get<IToastService>().Show("Depots", "Couldn't save. The lua file may be in use.", error: true);
            return;
        }

        IsEditing = false;
        SaveInPlace();
    }

    [RelayCommand]
    private void SaveInPlace()
    {
        if (ActiveGame is not { } game) return;

        string? baseHash = _svc.Vault.GetEditBase(game.AppId);
        if (baseHash is null) { _svc.Vault.SyncDefaultFromLive(game.AppId); return; }

        LuaVariant? updated;
        _suppressDepotReload = true;
        try
        {
            updated = _svc.Vault.UpdateVariant(game.AppId, baseHash, _svc.Vault.ReadLiveText(game.AppId));
            if (updated is not null)
            {
                RefreshVariants();
                SelectedVariant = Variants.FirstOrDefault(v => v.Hash == updated.Hash) ?? SelectedVariant;
            }
        }
        finally { _suppressDepotReload = false; }

        if (updated is null) { SaveAsPreset(); return; }

        _ = LoadDepotsAsync(quiet: true);
        _svc.Get<IToastService>().Show("Depots", $"Updated \"{updated.DisplayLabel}\".");
    }

    public LuaVariant? EditBaseVariant =>
        ActiveGame is { } g && _svc.Vault.GetEditBase(g.AppId) is { } h
            ? _svc.Vault.GetVariants(g.AppId).FirstOrDefault(v => v.Hash == h)
            : null;

    public bool HasEditBase => EditBaseVariant is not null;

    public string SaveInPlaceLabel => EditBaseVariant is { } v
        ? $"Save to \"{v.DisplayLabel}\""
        : "Save as preset";

    [RelayCommand]
    private void SaveAsPreset()
    {
        if (ActiveGame is not { } game) return;

        string text = IsEditing ? EditorText : _svc.Vault.ReadLiveText(game.AppId);
        if (string.IsNullOrWhiteSpace(text)) return;

        var saved = _svc.Vault.SaveText(game.AppId, text, null);
        if (saved is null)
        {
            _svc.Get<IToastService>().Show("Depots", "Couldn't save. The lua file may be in use.", error: true);
            return;
        }

        IsEditing = false;
        RefreshVariants();
        SelectedVariant = Variants.FirstOrDefault(v => v.Hash == saved.Hash) ?? SelectedVariant;
        _svc.Get<IToastService>().Show("Depots", "Saved as a preset.");
    }

    [RelayCommand] private void ToggleInLua() => IsInLuaExpanded = !IsInLuaExpanded;
    [RelayCommand] private void ToggleMissing() => IsMissingExpanded = !IsMissingExpanded;
    [RelayCommand] private void ToggleUnknown() => IsUnknownExpanded = !IsUnknownExpanded;
    [RelayCommand] private void OpenSteamDb(DepotRow row) => _svc.Integrations.OpenUrl(row.SteamDbUrl);

    [RelayCommand]
    private void CopyAppId()
    {
        if (ActiveGame is not { } game) return;
        if (!_svc.Integrations.CopyToClipboard(game.AppId.ToString()))
            _svc.Get<IToastService>().Show("Copy App ID", "Couldn't copy — another app is holding the clipboard.", error: true);
    }

    [RelayCommand]
    private void ToggleLock(DepotRow row)
    {
        if (row.CanLock) EditLive(row, text => LuaEditor.SetDepotLocked(text, row.ToggleId, !row.IsLocked));
    }

    [RelayCommand]
    private void ToggleEnabled(DepotRow row)
    {
        if (row.CanEnable) EditLive(row, text => LuaEditor.SetDepotEnabled(text, row.ToggleId, !row.IsEnabled));
    }

    private void EditLive(DepotRow row, Func<string, string> edit)
    {
        if (ActiveGame is not { } game) return;

        bool editingLive = SelectionDescribesLive;
        var target = SelectedVariant?.Variant;

        string before = editingLive || target is null
            ? _svc.Vault.ReadLiveText(game.AppId)
            : _svc.Vault.ReadText(game.AppId, target.Hash);
        if (string.IsNullOrEmpty(before)) return;

        string after = edit(before);
        if (after == before) return;

        bool ok;
        string? newHash = null;
        _suppressDepotReload = true;
        try
        {
            if (editingLive || target is null)
            {
                RememberEditBase(game.AppId);
                ok = _svc.Vault.WriteLive(game.AppId, after);
            }
            else
            {
                var updated = _svc.Vault.UpdateVariant(game.AppId, target.Hash, after);
                ok = updated is not null;
                newHash = updated?.Hash;
            }
            if (ok) RefreshVariants();
        }
        finally { _suppressDepotReload = false; }

        if (!ok)
        {
            _svc.Get<IToastService>().Show("Depots", "Couldn't save. The lua file may be in use.", error: true);
            return;
        }

        if (newHash is not null)
            SelectedVariant = Variants.FirstOrDefault(v => v.Hash == newHash) ?? SelectedVariant;

        _ = LoadDepotsAsync(quiet: true);
    }

    private void RememberEditBase(long appId)
    {
        if (_svc.Vault.GetActiveVariant(appId) is { Kind: not LuaVariantKind.Default } current)
            _svc.Vault.SetEditBase(appId, current.Hash);
    }

    private async Task LoadDepotsAsync(bool quiet = false)
    {
        long token = ++_depotLoadToken;
        if (ActiveGame is not { } game)
        {
            InLua.Clear(); Missing.Clear(); Unknown.Clear();
            DepotError = null; IsLoadingDepots = false;
            return;
        }

        DepotError = null;
        if (!quiet)
        {
            IsLoadingDepots = true;
            InLua.Clear(); Missing.Clear(); Unknown.Clear();
            LatestBuildLabel = null;
        }

        long appId = game.AppId;
        bool editingLive = SelectionDescribesLive;
        EditingInactiveBuild = !editingLive;
        string? storedHash = editingLive ? null : SelectedVariant!.Hash;

        var lua = await Task.Run(() => storedHash is not null
            ? ParseVariant(appId, storedHash)
            : _svc.Vault.LivePath(appId) is { } live ? LuaFileParser.Parse(live, appId) : null);

        if (token != _depotLoadToken) return;

        var info = await _svc.DepotInfo.GetAsync(game.AppId);
        if (token != _depotLoadToken) return;

        if (info is null)
        {
            DepotError = "Couldn't load depot info (offline or unknown app).";
            IsLoadingDepots = false;
            return;
        }

        if (info.PublicBuildId is not null)
            LatestBuildLabel = $"Latest build on Steam: {info.PublicBuildId}";

        BuildRows(info, lua);
        IsLoadingDepots = false;

        var declared = Declarations(lua);
        var named = declared.Where(e => e.Value.Comment is not null).Select(e => e.Key).ToHashSet();
        var unnamedDlc = info.Depots.Where(d => d.DlcAppId is not null).Select(d => d.DlcAppId!.Value)
            .Concat(info.DlcIds).Distinct()
            .Where(a => _svc.AppList.GetName(a) is null && _svc.AppInfo.GetCached(a)?.Name is null && !named.Contains(a))
            .ToList();

        if (unnamedDlc.Count > 0)
        {
            await Parallel.ForEachAsync(unnamedDlc, new ParallelOptions { MaxDegreeOfParallelism = 4 },
                async (id, _) => await _svc.AppInfo.ResolveAsync(id));
            if (token == _depotLoadToken) BuildRows(info, lua);
        }
    }

    private static Dictionary<long, LuaEntry> Declarations(LuaContents? lua)
    {
        var all = new Dictionary<long, LuaEntry>();
        if (lua is null) return all;
        foreach (var e in lua.DisabledEntries) all[e.Id] = e;
        foreach (var e in lua.Entries) all[e.Id] = e;
        return all;
    }

    private LuaContents? ParseVariant(long appId, string? hash)
    {
        if (hash is null) return null;
        string tmp = Path.Combine(Path.GetTempPath(), $"luabuilds_{Guid.NewGuid():N}.lua");
        try
        {
            File.WriteAllText(tmp, _svc.Vault.ReadText(appId, hash));
            return LuaFileParser.Parse(tmp, appId);
        }
        catch { return null; }
        finally { try { File.Delete(tmp); } catch { } }
    }

    private void BuildRows(AppDepotInfo info, LuaContents? lua)
    {
        var declared = Declarations(lua);
        var active = lua?.Entries.Select(e => e.Id).ToHashSet() ?? new HashSet<long>();
        long baseAppId = lua?.BaseAppId ?? info.AppId;
        var luaNames = declared.Where(kv => kv.Value.Comment is not null)
            .ToDictionary(kv => kv.Key, kv => kv.Value.Comment!);

        var items = new List<ContentDepot>(info.Depots);
        var depotDlcIds = info.Depots.Where(d => d.DlcAppId is not null).Select(d => d.DlcAppId!.Value).ToHashSet();
        foreach (long dlcId in info.DlcIds)
            if (!depotDlcIds.Contains(dlcId))
                items.Add(new ContentDepot(dlcId, 0, dlcId, IsShared: false, Os: null, Language: null));

        AddDeclaredButUnlisted(items, declared, baseAppId, depotDlcIds);

        bool DlcNameKnown(long dlcId) =>
            _svc.AppList.GetName(dlcId) is not null || _svc.AppInfo.GetCached(dlcId)?.Name is not null || luaNames.ContainsKey(dlcId);

        DepotRow Row(ContentDepot d)
        {
            string? steamName = d.DlcAppId is { } dlcId ? (_svc.AppList.GetName(dlcId) ?? _svc.AppInfo.GetCached(dlcId)?.Name) : null;
            string title =
                steamName
                ?? luaNames.GetValueOrDefault(d.Id)
                ?? (d.IsDlc ? $"DLC {d.DlcAppId}"
                    : d.IsShared ? "Shared depot" : "Depot");

            var meta = new List<string> { d.Id.ToString() };
            if (d.Size > 0) meta.Add(FormatSize(d.Size));
            if (!string.IsNullOrWhiteSpace(d.Os)) meta.Add(PrettyOs(d.Os));
            if (!string.IsNullOrWhiteSpace(d.Language)) meta.Add(d.Language!);

            string url = d.DlcAppId is { } dlc
                ? $"https://steamdb.info/app/{dlc}/"
                : $"https://steamdb.info/depot/{d.Id}/";

            long declId = declared.ContainsKey(d.Id) ? d.Id : d.DlcAppId ?? d.Id;
            declared.TryGetValue(declId, out var entry);
            bool inLua = entry is not null;

            return new DepotRow(d.Id, title, string.Join("  ·  ", meta), d.IsDlc, d.IsShared, url,
                entry?.ManifestId, entry?.CommentedManifestId, d.PublicManifestId,
                IsInLua: inLua,
                IsEnabled: active.Contains(declId),
                CanToggle: inLua,
                IsBaseApp: declId == baseAppId)
            {
                ToggleId = declId, Size = d.Size, Os = d.Os, Language = d.Language,
                FromAppId = d.FromAppId, LuaSize = entry?.SizeOnDisk ?? 0,
            };
        }

        bool IsInLua(ContentDepot d) => declared.ContainsKey(d.Id) || (d.DlcAppId is { } a && declared.ContainsKey(a));
        bool IsUnknown(ContentDepot d) =>
            d.IsShared
            || (d.IsDlc && !DlcNameKnown(d.DlcAppId!.Value))
            || (!d.IsDlc && d.Size == 0);

        var allInLua = items.Where(IsInLua).Select(Row).ToList();
        var allMissing = items.Where(d => !IsInLua(d) && !IsUnknown(d)).Select(Row).ToList();
        var allUnknown = items.Where(d => !IsInLua(d) && IsUnknown(d)).Select(Row).ToList();

        InLua.Clear();
        foreach (var r in Filtered(allInLua)) InLua.Add(r);
        Missing.Clear();
        foreach (var r in Filtered(allMissing)) Missing.Add(r);
        Unknown.Clear();
        foreach (var r in Filtered(allUnknown)) Unknown.Add(r);

        OnPropertyChanged(nameof(InLuaCount));
        OnPropertyChanged(nameof(MissingCount));
        OnPropertyChanged(nameof(UnknownCount));
        OnPropertyChanged(nameof(HasInLua));
        OnPropertyChanged(nameof(HasMissing));
        OnPropertyChanged(nameof(HasUnknown));
    }

    private IEnumerable<DepotRow> Filtered(IReadOnlyList<DepotRow> rows) =>
        string.IsNullOrWhiteSpace(DepotSearchText) ? rows : rows.Where(r => r.Matches(DepotSearchText));

    private void ApplyDepotFilter()
    {
        if (ActiveGame is null) return;
        // Cheap re-filter: rebuild rows from cached data is heavy; toggling search simply
        // re-applies the visible filter lists.
        OnPropertyChanged(nameof(InLuaCount));
    }

    private static void AddDeclaredButUnlisted(
        List<ContentDepot> items, Dictionary<long, LuaEntry> declared, long baseAppId,
        HashSet<long> depotDlcIds)
    {
        var known = items.Select(d => d.Id).ToHashSet();
        foreach (var (id, entry) in declared)
        {
            if (known.Contains(id) || depotDlcIds.Contains(id)) continue;
            if (id == baseAppId) continue;
            bool isDepot = entry.HasKey;
            items.Add(new ContentDepot(id, entry.SizeOnDisk ?? 0, isDepot ? null : id,
                isDepot && entry.Comment?.Contains("Shared", StringComparison.OrdinalIgnoreCase) == true,
                Os: null, Language: null));
        }
    }

    private static string PrettyOs(string os) => os switch
    {
        "windows" => "Windows",
        "macos" or "macosx" => "macOS",
        "linux" => "Linux",
        _ => os
    };

    private static string FormatSize(long bytes)
    {
        if (bytes <= 0) return "";
        double gb = bytes / 1024d / 1024d / 1024d;
        if (gb >= 1) return $"{gb:0.##} GB";
        return $"{bytes / 1024d / 1024d:0.#} MB";
    }
}
