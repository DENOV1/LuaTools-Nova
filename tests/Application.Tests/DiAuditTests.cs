// LuaTools Nova — DI audit + startup-crash regression tests.
//
// Regression target (v1.0.0, Linux Mint 22.3 first-run):
//   System.InvalidOperationException: No service for type
//   'LuaTools.Application.ViewModels.UnlockerServiceProxy' has been registered.
//     at NovaServices.Get<T>() — NovaServices.cs:139
//     at ModeViewModel constructor — PageViewModels.cs:565
//     at App.OnFrameworkInitializationCompleted() — App.axaml.cs:53
// Root cause: split composition root — NovaServices registered only domain services while
// proxies/VMs/UI services were constructed ad-hoc in App yet fetched via svc.Get<T>().
// Fix: NovaServices is the single composition root; everything is registered there; the container
// is built with ValidateOnBuild/ValidateScopes; UI services arrive through the `configure` callback.
using System.Runtime.CompilerServices;
using LuaTools.Application;
using LuaTools.Application.ViewModels;
using LuaTools.Core.Downloads;
using LuaTools.Core.Services;
using LuaTools.Integrations;
using Microsoft.Extensions.DependencyInjection;
using Xunit;

namespace LuaTools.Application.Tests;

public static class TestEnvInit
{
    [ModuleInitializer]
    internal static void Init()
    {
        // Isolate NovaPaths/ToolPaths per test run (they read XDG dirs at first touch).
        var root = Path.Combine(Path.GetTempPath(), "nova-di-tests-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(root);
        Environment.SetEnvironmentVariable("XDG_CONFIG_HOME", Path.Combine(root, "config"));
        Environment.SetEnvironmentVariable("XDG_DATA_HOME", Path.Combine(root, "data"));
    }
}

/// <summary>UI-boundary fakes — exactly what the real App registers via the configure callback.</summary>
public sealed class FakeDialogService : IDialogService
{
    public bool ConfirmResult = true;
    public List<(string Title, string Body)> Shown = new();
    public bool Confirm(string title, string body) { Shown.Add((title, body)); return ConfirmResult; }
    public bool ConfirmWithCancel(string title, string body) => Confirm(title, body);
    public void Info(string title, string body) { Shown.Add((title, body)); }
}

public sealed class FakeToastService : IToastService
{
    public List<(string Title, string Message, bool Error)> Toasts = new();
    public void Show(string title, string message, bool error = false) => Toasts.Add((title, message, error));
    public void ShowAction(string title, string message, string actionLabel, Action onAction, bool error = false) => Toasts.Add((title, message, error));
}

public sealed class FakeNavigationHost : INavigationHost
{
    public List<string> Pages = new();
    public void NavigateTo(string pageKey) => Pages.Add(pageKey);
}

public static class DiTestHarness
{
    /// <summary>Builds the production container with OS-boundary fakes + UI-boundary fakes —
    /// identical registration shape to App.OnFrameworkInitializationCompleted.</summary>
    public static NovaServices Build(Action<IServiceCollection>? extra = null)
    {
        var locator = new FakeSteamLocator();
        return new NovaServices(
            steamLocator: locator,
            processControl: new FakeProcessControl(),
            integrations: new FakeSystemIntegrations(),
            dispatcher: action => action(), // inline dispatcher for deterministic tests
            configure: services =>
            {
                services.AddSingleton<INavigationHost>(new FakeNavigationHost());
                services.AddSingleton<IDialogService>(new FakeDialogService());
                services.AddSingleton<IToastService>(new FakeToastService());
                extra?.Invoke(services);
            });
    }
}

public sealed class FakeSteamLocator : ISteamLocator
{
    public string? AutoDetectedPath => null;
    public string? EffectivePath => null;
    public bool IsOverridden => false;
    public bool IsValid => false;
    public string ClientExecutableFor(string steamPath) => Path.Combine(steamPath, "steam.sh");
    public string? SteamLanguage => null;
    public string DetectionSource => "test";
}

public sealed class FakeProcessControl : IProcessControl
{
    public bool IsSteamRunning() => false;
    public void StopSteam() { }
    public bool StartSteam() => true;
    public bool RestartSteam() => true;
}

public sealed class FakeSystemIntegrations : ISystemIntegrations
{
    public void OpenUrl(string url) { }
    public bool RevealPath(string? path) => true;
    public bool CopyToClipboard(string? text) => true;
    public void RegisterProtocol() { }
    public void SetAutostart(bool enabled) { }
    public string DownloadsFolder => "/tmp";
    public string? PickFolder(string title, string? initial) => null;
    public IReadOnlyList<string> PickFiles(string title, params string[] patterns) => Array.Empty<string>();
    public void MinimizeToTrayChanged(bool toTray) { }
    public void ExitApplication() { }
}

public class DiAuditTests
{
    // ── REGRESSION: the Linux Mint 22.3 startup crash ────────────────

    [Fact]
    public async Task Regression_ModeViewModel_ResolvesUnlockerServiceProxy_WithoutThrowing()
    {
        using var nova = DiTestHarness.Build();
        // v1.0.0 threw InvalidOperationException here at first window construction.
        var vm = nova.Get<ModeViewModel>();
        Assert.NotNull(vm);
        Assert.NotNull(vm.Cards);
        await vm.LoadAsync();
        Assert.Equal(3, vm.Cards.Count); // OST / BST / Custom cards populate on load
        // Linux reality is encoded in the card set: OST + BST (Not Supported) + Custom (offered)
        Assert.Contains(vm.Cards, c => c.Mode == Core.Models.UnlockerMode.Custom);
        Assert.Equal(2, vm.Cards.Count(c => c.Info.Status == Core.Models.ModeStatus.NotSupported));
    }

    [Fact]
    public void Regression_AllPageViewModels_ResolveFromTheContainer()
    {
        using var nova = DiTestHarness.Build();
        Assert.NotNull(nova.Get<MainViewModel>());
        Assert.NotNull(nova.Get<OnboardingViewModel>());
        Assert.NotNull(nova.Get<HomeViewModel>());
        Assert.NotNull(nova.Get<DownloadViewModel>());
        Assert.NotNull(nova.Get<DownloadsViewModel>());
        Assert.NotNull(nova.Get<ManageViewModel>());
        Assert.NotNull(nova.Get<BuildsViewModel>());
        Assert.NotNull(nova.Get<FixesViewModel>());
        Assert.NotNull(nova.Get<PluginViewModel>());
        Assert.NotNull(nova.Get<ModeViewModel>());
        Assert.NotNull(nova.Get<SettingsViewModel>());
        Assert.NotNull(nova.Get<DropInstallViewModel>());
    }

    [Fact]
    public void Regression_HomePageDependencies_Resolve()
    {
        using var nova = DiTestHarness.Build();
        // HomeViewModel.RefreshPluginStatusAsync/RefreshMode fetch these via Get<T>:
        Assert.NotNull(nova.Get<Integrations.PluginInstallerService>());
        Assert.NotNull(nova.Get<UnlockerServiceProxy>());
        // The plugin backend resolves VMs via the facade:
        var facade = nova.Get<IApplicationAddFacade>();
        Assert.NotNull(facade.GetAddState(730));
        Assert.NotNull(facade);
    }

    [Fact]
    public void ValidateOnBuild_FailsTheBuild_WhenARequiredServiceIsMissing()
    {
        // Proves the v1.0.0 crash CLASS is now caught at container BUILD time, not on the UI thread.
        // Mimic the old broken shape: a VM whose IToastService dependency is never registered.
        // ValidateOnBuild aggregates EVERY unresolvable descriptor (this is precisely the error
        // shape the v1.0.0 startup crashed with — now surfaced at build time instead).
        var thrown = Assert.Throws<AggregateException>(() =>
        {
            _ = new NovaServices(
                steamLocator: new FakeSteamLocator(),
                processControl: new FakeProcessControl(),
                integrations: new FakeSystemIntegrations(),
                configure: sc =>
                {
                    // deliberately register a VM that depends on an unregistered service
                    sc.AddSingleton<CrashingViewModel>();
                });
        });
        Assert.True(thrown.InnerExceptions.Any(e =>
            e.Message.Contains("CrashingViewModel", StringComparison.Ordinal)),
            thrown.Message);
    }

    private sealed class CrashingViewModel
    {
        public CrashingViewModel(IToastService toast) { }
    }

    // ── Full audit: every registered service resolves ────────────────

    [Fact]
    public void Audit_AllDomainServices_Resolve()
    {
        using var nova = DiTestHarness.Build();
        Assert.NotNull(nova.Get<NovaServices>());
        Assert.NotNull(nova.Get<Core.Persistence.SettingsService>());
        Assert.NotNull(nova.Get<Core.Persistence.CacheService>());
        Assert.NotNull(nova.Get<ISteamLocator>());
        Assert.NotNull(nova.Get<IProcessControl>());
        Assert.NotNull(nova.Get<ISystemIntegrations>());
        Assert.NotNull(nova.Get<Core.Lua.LuaVault>());
        Assert.NotNull(nova.Get<Core.Services.LuaInstaller>());
        Assert.NotNull(nova.Get<SteamLibraryService>());
        Assert.NotNull(nova.Get<Infrastructure.Auth.AuthService>());
        Assert.NotNull(nova.Get<AuthServiceProxy>());
        Assert.NotNull(nova.Get<UnlockerServiceProxy>());
        Assert.NotNull(nova.Get<Infrastructure.Clients.LuaToolsApiClient>());
        Assert.NotNull(nova.Get<Infrastructure.Clients.HubcapService>());
        Assert.NotNull(nova.Get<DownloadQueue>());
        Assert.NotNull(nova.Get<Infrastructure.Downloads.ManifestJobFactory>());
        Assert.NotNull(nova.Get<Infrastructure.Clients.SteamAppInfoCache>());
        Assert.NotNull(nova.Get<Infrastructure.Clients.SteamAppListCache>());
        Assert.NotNull(nova.Get<Infrastructure.Clients.SteamDepotInfo>());
        Assert.NotNull(nova.Get<Infrastructure.Clients.CoverCache>());
        Assert.NotNull(nova.Get<Infrastructure.Clients.HardwareAppIdService>());
        Assert.NotNull(nova.Get<Infrastructure.Network.GithubProxy>());
        Assert.NotNull(nova.Get<Infrastructure.Downloads.DepotDownloaderService>());
        Assert.NotNull(nova.Get<Infrastructure.Downloads.AppliedFixIndexService>());
        Assert.NotNull(nova.Get<Infrastructure.Auth.AnalyticsService>());
        Assert.NotNull(nova.Get<Infrastructure.Auth.DonateKeysService>());
    }

    [Fact]
    public void Audit_AllIntegrationServices_Resolve()
    {
        using var nova = DiTestHarness.Build();
        Assert.NotNull(nova.Get<PluginInstallerService>());
        Assert.NotNull(nova.Get<CefInjectorService>());
        Assert.NotNull(nova.Get<HttpServerService>());
        Assert.NotNull(nova.Get<IApplicationAddFacade>());
        Assert.NotNull(nova.Get<Func<IApplicationAddFacade>>());
        var httpServer = nova.Get<HttpServerService>();
        Assert.NotNull(httpServer);
    }

    [Fact]
    public void Audit_UiServices_AreRegisteredThroughCallback()
    {
        using var nova = DiTestHarness.Build();
        Assert.NotNull(nova.Get<INavigationHost>());
        Assert.NotNull(nova.Get<IDialogService>());
        Assert.NotNull(nova.Get<IToastService>());
    }

    [Fact]
    public void Audit_ViewModels_AreSingletons()
    {
        using var nova = DiTestHarness.Build();
        var a = nova.Get<ManageViewModel>();
        var b = nova.Get<ManageViewModel>();
        Assert.Same(a, b);

        // HomeViewModel shares the DropInstallViewModel singleton (drag-drop state coherence)
        var home = nova.Get<HomeViewModel>();
        Assert.Same(nova.Get<DropInstallViewModel>(), home.Drop);
    }

    [Fact]
    public void Audit_UnlockerProxy_IsWiredToSettings()
    {
        using var nova = DiTestHarness.Build();
        var proxy = nova.Get<UnlockerServiceProxy>();
        Assert.NotNull(proxy.Settings);
        proxy.SelectedMode = Core.Models.UnlockerMode.Custom;
        Assert.Equal("Custom", proxy.Settings!.SelectedMode);
    }
}
