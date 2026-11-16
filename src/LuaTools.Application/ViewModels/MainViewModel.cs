// LuaTools Nova — shell + onboarding ViewModels (ported from the MIT original's MainViewModel /
// OnboardingViewModel; MessageBox calls go through IDialogService).
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using LuaTools.Core.Services;

namespace LuaTools.Application.ViewModels;

public partial class MainViewModel : ObservableObject
{
    private readonly AuthServiceProxy _auth;
    private readonly IProcessControl _steam;
    private readonly IDialogService _dialogs;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(IsRealUser), nameof(FooterStatus))]
    private bool _isGuest = true;

    public bool IsRealUser => !IsGuest;

    public string VersionLabel => "v1.0.0";
    public string FooterStatus => $"{VersionLabel} · {(IsGuest ? "Browsing as Guest" : "Logged In")}";

    [ObservableProperty] private bool _isSigningIn;
    [ObservableProperty] private string? _signInError;

    public OnboardingViewModel Onboarding { get; }

    public MainViewModel(AuthServiceProxy auth, IProcessControl steam, OnboardingViewModel onboarding,
        IDialogService dialogs)
    {
        _auth = auth;
        _steam = steam;
        Onboarding = onboarding;
        _dialogs = dialogs;
        _auth.AuthStateChanged += () => IsGuest = _auth.IsGuest;
    }

    public async Task InitializeAsync()
    {
        await _auth.InitializeAsync();
        IsGuest = _auth.IsGuest;
    }

    [RelayCommand]
    private async Task SignInAsync()
    {
        if (IsSigningIn) return;
        IsSigningIn = true;
        SignInError = null;
        try { await _auth.SignInAsync(); }
        catch (Exception ex) { SignInError = ex.Message; }
        finally { IsSigningIn = false; }
    }

    [RelayCommand]
    private void RestartSteam()
    {
        if (!_dialogs.Confirm("Restart Steam", "Restart Steam now? This closes Steam and any running games, then relaunches it."))
            return;

        if (!_steam.RestartSteam())
            _dialogs.Info("Restart Steam", "Couldn't find or launch Steam. Set its location in Settings.");
    }
}

/// <summary>Wrapper so ViewModels can be exercised in tests without the HTTP auth flow.</summary>
public sealed class AuthServiceProxy(LuaTools.Infrastructure.Auth.AuthService auth)
{
    public bool IsGuest => auth.IsGuest;
    public bool IsSignedIn => auth.IsSignedIn;
    public string? DisplayName => auth.DisplayName;
    public string? Email => auth.Email;
    public string? AvatarUrl => auth.AvatarUrl;
    public bool IsBotProvisioned => auth.IsBotProvisioned;

    public event Action? AuthStateChanged
    {
        add => auth.AuthStateChanged += value;
        remove => auth.AuthStateChanged -= value;
    }

    public Task<bool> InitializeAsync() => auth.InitializeAsync();
    public Task SignInAsync(CancellationToken ct = default) => auth.SignInAsync(ct);
    public Task SignInWithCodeAsync(string code, CancellationToken ct = default) => auth.SignInWithCodeAsync(code, ct);
    public void SignOut() => auth.SignOut();
}

public partial class OnboardingViewModel : ObservableObject
{
    private readonly AuthServiceProxy _auth;
    private readonly LuaTools.Core.Persistence.CacheService _cache;
    private readonly LuaTools.Core.Persistence.SettingsService _settings;
    private readonly UnlockerServiceProxy _unlocker;
    private readonly IToastService _toast;

    public OnboardingViewModel(AuthServiceProxy auth, LuaTools.Core.Persistence.CacheService cache,
        LuaTools.Core.Persistence.SettingsService settings, UnlockerServiceProxy unlocker, IToastService toast)
    {
        _auth = auth;
        _cache = cache;
        _settings = settings;
        _unlocker = unlocker;
        _toast = toast;
        _auth.AuthStateChanged += () => IsSignedIn = _auth.IsSignedIn;
        IsSignedIn = _auth.IsSignedIn;
    }

    [ObservableProperty] private bool _isOpen;
    public Func<Task>? RefreshHome { get; set; }

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(IsGuest))]
    private bool _isSignedIn;
    public bool IsGuest => !IsSignedIn;

    [ObservableProperty] private bool _isSigningIn;
    [ObservableProperty] private bool _applyRecommended = true;
    [ObservableProperty] private bool _installPlugin = true;
    [ObservableProperty] private bool _isBusy;
    public bool NotBusy => !IsBusy;
    [ObservableProperty] private string? _statusLine;

    [RelayCommand] private void SetApplyRecommended(bool value) => ApplyRecommended = value;
    [RelayCommand] private void SetInstallPlugin(bool value) => InstallPlugin = value;

    [RelayCommand]
    private async Task SignIn()
    {
        if (IsSigningIn) return;
        IsSigningIn = true;
        StatusLine = null;
        try { await _auth.SignInAsync(); }
        catch (Exception ex) { StatusLine = ex.Message; }
        finally { IsSigningIn = false; }
    }

    [RelayCommand]
    private void Finish()
    {
        if (IsBusy) return;
        bool applyRecommended = ApplyRecommended;
        bool installPlugin = InstallPlugin;

        _cache.OnboardingComplete = true;
        IsOpen = false;

        if (applyRecommended || installPlugin)
            _ = ApplyChoicesAsync(applyRecommended, installPlugin);
    }

    private async Task ApplyChoicesAsync(bool applyRecommended, bool installPlugin)
    {
        IsBusy = true;
        _toast.Show("Welcome to the LuaTools Nova App!", "Setting things up…");
        try
        {
            if (applyRecommended)
            {
                _settings.FastFetch = true;
                // The Windows unlocker modes don't exist on Linux; Custom is the honest default.
                var result = await _unlocker.InstallAsync(Core.Models.UnlockerMode.Custom);
                if (!result.Success)
                    _toast.Show("Welcome to the LuaTools Nova App!", result.Error ?? "", error: true);
            }

            if (installPlugin && InstallPluginAsync is { } install)
            {
                var (ok, error) = await install();
                if (!ok) _toast.Show("Welcome to the LuaTools Nova App!", error ?? "", error: true);
            }
        }
        catch (Exception ex)
        {
            _toast.Show("Welcome to the LuaTools Nova App!", ex.Message, error: true);
        }
        finally
        {
            IsBusy = false;
            if (RefreshHome is not null)
                try { await RefreshHome(); } catch { /* best effort */ }
        }
    }

    /// <summary>Wired by the host (Integrations.PluginInstallerService.InstallAsync).</summary>
    public Func<Task<(bool Ok, string? Error)>>? InstallPluginAsync { get; set; }
}

/// <summary>Mode-service surface for the Mode page (Custom-only reality on Linux, honestly modeled).</summary>
public sealed class UnlockerServiceProxy
{
    public IReadOnlyList<ModeCardInfo> Modes { get; } =
    [
        new(Core.Models.UnlockerMode.Ost, "OpenSteamTools",
            "Windows unlocker (DLL injection into the Steam client). Windows-only — Steam on Linux does not load PE proxies.",
            Core.Models.ModeKind.Zip, Core.Models.ModeStatus.NotSupported, "Not supported on Linux"),
        new(Core.Models.UnlockerMode.Bst, "BetterSteamTools",
            "Fork of OpenSteamTools. Windows-only — see the note on the left.",
            Core.Models.ModeKind.Zip, Core.Models.ModeStatus.NotSupported, "Not supported on Linux"),
        new(Core.Models.UnlockerMode.Custom, "Custom Unlocker",
            "Use your own Linux-native unlocking solution (e.g. slsteam-moon style injection); Nova will not touch it.",
            Core.Models.ModeKind.Manual, Core.Models.ModeStatus.UserManaged, null),
    ];

    public Core.Models.UnlockerMode? SelectedMode
    {
        get => Enum.TryParse<Core.Models.UnlockerMode>(
            Settings?.SelectedMode, out var m) ? m : null;
        set => Settings!.SelectedMode = value?.ToString();
    }

    public LuaTools.Core.Persistence.SettingsService? Settings { get; set; }

    public Task<Core.Models.ModeInstallResult> InstallAsync(Core.Models.UnlockerMode mode)
    {
        if (Settings is null) return Task.FromResult(Core.Models.ModeInstallResult.Fail("Settings unavailable"));
        Settings.SelectedMode = mode.ToString();
        return Task.FromResult(Core.Models.ModeInstallResult.Ok());
    }
}

public sealed record ModeCardInfo(
    Core.Models.UnlockerMode Mode,
    string DisplayName,
    string Description,
    Core.Models.ModeKind Kind,
    Core.Models.ModeStatus Status,
    string? LinuxNote);
