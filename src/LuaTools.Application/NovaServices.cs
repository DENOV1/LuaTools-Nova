// LuaTools Nova — composition root. Wires every service; runnable headless (tests) or from the UI.
//
// v1.0.1 architectural fix: this is now the SINGLE composition root. Previously the DI container
// registered only domain services while App.axaml.cs constructed proxies/VMs/UI services ad-hoc —
// yet ViewModels fetched those via svc.Get<T>(), producing the reported startup crash:
//   "No service for type 'LuaTools.Application.ViewModels.UnlockerServiceProxy' has been registered"
// Everything is registered here (or through the `configure` callback for UI-layer services),
// and the container is built with ValidateOnBuild so a missing dependency fails at BUILD time
// with a precise message instead of at first resolution on the UI thread.
using LuaTools.Core.AppInfo;
using LuaTools.Core.Downloads;
using LuaTools.Core.Lua;
using LuaTools.Core.Persistence;
using LuaTools.Core.Services;
using LuaTools.Infrastructure.Auth;
using LuaTools.Infrastructure.Clients;
using LuaTools.Infrastructure.Downloads;
using LuaTools.Infrastructure.Network;
using LuaTools.Linux;
using LuaTools.Application.ViewModels;
using LuaTools.Integrations;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;

namespace LuaTools.Application;

/// <summary>Filesystem roots for Nova's own state (XDG).</summary>
public static class NovaPaths
{
    public static string ConfigDir { get; } = Dir(
        Environment.GetEnvironmentVariable("XDG_CONFIG_HOME"),
        ".config");

    public static string DataDir { get; } = Dir(
        Environment.GetEnvironmentVariable("XDG_DATA_HOME"),
        ".local", "share");

    private static string Dir(string? xdg, params string[] fallback)
    {
        string root;
        if (!string.IsNullOrWhiteSpace(xdg)) root = xdg;
        else root = Path.Combine(fallback.Prepend(
            Environment.GetFolderPath(Environment.SpecialFolder.UserProfile)).ToArray());
        return Path.Combine(root, "LuaToolsNova");
    }
}

public sealed class NovaServices : IDisposable
{
    public IServiceProvider Provider { get; }

    public SettingsService Settings { get; }
    public CacheService Cache { get; }
    public ISteamLocator Steam { get; private set; }
    public IProcessControl ProcessControl { get; }
    public ISystemIntegrations Integrations { get; }
    public LuaVault Vault { get; }
    public LuaInstaller Installer { get; }
    public SteamLibraryService Library { get; }
    public AuthService Auth { get; }
    public AuthServiceProxy AuthProxy { get; }
    public UnlockerServiceProxy Unlocker { get; }
    public LuaToolsApiClient Api { get; }
    public HubcapService Hubcap { get; }
    public SteamStoreService Store { get; }
    public DownloadQueue Queue { get; }
    public ManifestJobFactory Jobs { get; }
    public SteamAppInfoCache AppInfo { get; }
    public SteamAppListCache AppList { get; }
    public SteamDepotInfo DepotInfo { get; }
    public CoverCache Covers { get; }
    public HardwareAppIdService Hardware { get; }
    public GithubProxy Github { get; }
    public DepotDownloaderService DepotTool { get; }
    public AppliedFixIndexService FixIndex { get; }
    public AnalyticsService Analytics { get; }
    public DonateKeysService Donate { get; }
    public PluginInstallerService PluginInstaller { get; }
    public CefInjectorService CefInjector { get; }
    public ILoggerFactory LoggerFactory { get; }

    /// <param name="configure">Registered last, before Build: the UI layer adds its own
    /// implementations here (IDialogService, IToastService, INavigationHost). This callback is the
    /// sanctioned extension point — the DI audit test uses it to supply fakes.</param>
    /// <param name="validateOnBuild">Build the container with ValidateOnBuild + ValidateScopes so
    /// any missing constructor dependency fails immediately (the v1.0.0 startup crash class).</param>
    public NovaServices(
        ISteamLocator? steamLocator = null,
        IProcessControl? processControl = null,
        ISystemIntegrations? integrations = null,
        Action<Action>? dispatcher = null,
        Action<long>? onRecentAdd = null,
        Action<IServiceCollection>? configure = null,
        bool validateOnBuild = true,
        ILoggerFactory? loggerFactory = null)
    {
        NovaLog.Initialize(Path.Combine(NovaPaths.DataDir, "logs"));
        Settings = new SettingsService(NovaPaths.ConfigDir);
        Cache = new CacheService(NovaPaths.DataDir);
        Steam = steamLocator ?? new SteamPathLocator(Settings.SteamPathOverride);
        Store = new SteamStoreService(Steam);
        ProcessControl = processControl ?? new ProcessControl(Steam);
        Integrations = integrations ?? new SystemIntegrations();

        // The locator reads the override at construction; recreate it when it changes.
        if (steamLocator is null && Settings.SteamPathOverride is not null)
            Steam = new SteamPathLocator(Settings.SteamPathOverride);

        Library = new SteamLibraryService(Steam);
        Vault = new LuaVault(() => Steam.EffectivePath is { } p ? Path.Combine(p, "config", "stplug-in") : null,
            Path.Combine(NovaPaths.DataDir, "luavault"));
        Installer = new LuaInstaller(Steam, Settings, Vault, onRecentAdd);
        Auth = new AuthService(NovaPaths.DataDir);
        AuthProxy = new AuthServiceProxy(Auth);
        Unlocker = new UnlockerServiceProxy { Settings = Settings };

        var cacheProxy = new CacheServiceProxy(Cache);
        AppInfo = new SteamAppInfoCache(cacheProxy, NovaPaths.DataDir);
        AppList = new SteamAppListCache(NovaPaths.DataDir);
        Covers = new CoverCache(NovaPaths.DataDir);
        DepotInfo = new SteamDepotInfo();
        Github = new GithubProxy();
        Hardware = new HardwareAppIdService(Github, cacheProxy);
        Api = new LuaToolsApiClient(Auth, AppInfo, Covers);
        Hubcap = new HubcapService();

        dispatcher ??= (action => action());
        Queue = new DownloadQueue(Cache, dispatcher);
        DepotTool = new DepotDownloaderService(Github, Steam, Auth, Cache);
        FixIndex = new AppliedFixIndexService(Library);
        Jobs = new ManifestJobFactory(Api, Hubcap, Settings, Installer, Library, Covers, DepotTool, DepotInfo, FixIndex);

        Analytics = new AnalyticsService();
        Donate = new DonateKeysService(Settings, Steam, Cache);

        PluginInstaller = new PluginInstallerService(Github);
        LoggerFactory = loggerFactory ?? Microsoft.Extensions.Logging.LoggerFactory.Create(b =>
            b.AddConsole().SetMinimumLevel(LogLevel.Information));
        CefInjector = new CefInjectorService(LoggerFactory.CreateLogger<CefInjectorService>());

        var services = new ServiceCollection();

        // ── Domain services (singletons — the app is a single-window desktop client) ──
        services.AddSingleton(this);
        services.AddSingleton(Settings);
        services.AddSingleton(Cache);
        services.AddSingleton(Steam);
        services.AddSingleton(ProcessControl);
        services.AddSingleton(Store);
        services.AddSingleton(Integrations);
        services.AddSingleton(Vault);
        services.AddSingleton(Installer);
        services.AddSingleton(Library);
        services.AddSingleton(Auth);
        services.AddSingleton(AuthProxy);
        services.AddSingleton(Unlocker);
        services.AddSingleton(Api);
        services.AddSingleton(Hubcap);
        services.AddSingleton(Queue);
        services.AddSingleton(Jobs);
        services.AddSingleton(AppInfo);
        services.AddSingleton(AppList);
        services.AddSingleton(DepotInfo);
        services.AddSingleton(Covers);
        services.AddSingleton(Hardware);
        services.AddSingleton(Github);
        services.AddSingleton(DepotTool);
        services.AddSingleton(FixIndex);
        services.AddSingleton(Analytics);
        services.AddSingleton(Donate);

        // ── Integrations layer ──
        services.AddSingleton(PluginInstaller);
        services.AddSingleton(CefInjector);
        services.AddSingleton(LoggerFactory);
        services.AddSingleton(typeof(ILogger<>), typeof(Logger<>));
        services.AddSingleton<Integrations.IApplicationAddFacade, ApplicationAddFacadeImpl>();
        services.AddSingleton<Func<Integrations.IApplicationAddFacade>>(
            sp => () => sp.GetRequiredService<Integrations.IApplicationAddFacade>());
        // HttpServerService: resolves Installer/Cache/ProcessControl/Integrations/Queue/Jobs/Api/AppList
        // + INavigationHost (UI callback) + Func<IApplicationAddFacade> + ILogger<HttpServerService>.
        services.AddSingleton<Integrations.HttpServerService>();

        // ── ViewModels (all singletons; one instance per app lifetime, as the original resolved
        //    pages from DI) ──
        services.AddSingleton<ViewModels.MainViewModel>();
        services.AddSingleton<ViewModels.OnboardingViewModel>();
        services.AddSingleton<ViewModels.HomeViewModel>();
        services.AddSingleton<ViewModels.DownloadViewModel>();
        services.AddSingleton<ViewModels.DownloadsViewModel>();
        services.AddSingleton<ViewModels.ManageViewModel>();
        services.AddSingleton<ViewModels.BuildsViewModel>();
        services.AddSingleton<ViewModels.FixesViewModel>();
        services.AddSingleton<ViewModels.PluginViewModel>();
        services.AddSingleton<ViewModels.ModeViewModel>();
        services.AddSingleton<ViewModels.SettingsViewModel>();
        services.AddSingleton<ViewModels.DropInstallViewModel>();

        // ── UI-layer services come in through the callback (App headless/tests supply fakes) ──
        configure?.Invoke(services);

        Provider = validateOnBuild
            ? services.BuildServiceProvider(new ServiceProviderOptions
              {
                  ValidateOnBuild = true,
                  ValidateScopes = true,
              })
            : services.BuildServiceProvider();
    }

    public void Start() => Queue.Start();
    public async Task StopAsync() => await Queue.StopAsync();

    public T Get<T>() where T : notnull => Provider.GetRequiredService<T>();

    /// <summary>Dispose the container (and with it every disposable singleton + the logger factory).</summary>
    public void Dispose()
    {
        try { (Provider as IDisposable)?.Dispose(); } catch { }
        try { LoggerFactory.Dispose(); } catch { }
    }
}
