// LuaTools Nova — composition root. Wires every service; runnable headless (tests) or from the UI.
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
using Microsoft.Extensions.DependencyInjection;

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

public sealed class NovaServices
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
    public LuaToolsApiClient Api { get; }
    public HubcapService Hubcap { get; }
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

    public NovaServices(
        ISteamLocator? steamLocator = null,
        IProcessControl? processControl = null,
        ISystemIntegrations? integrations = null,
        Action<Action>? dispatcher = null,
        Action<long>? onRecentAdd = null)
    {
        Settings = new SettingsService(NovaPaths.ConfigDir);
        Cache = new CacheService(NovaPaths.DataDir);
        Steam = steamLocator ?? new SteamPathLocator(Settings.SteamPathOverride);
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

        var services = new ServiceCollection();
        services.AddSingleton(this);
        services.AddSingleton(Settings);
        services.AddSingleton(Cache);
        services.AddSingleton(Steam);
        services.AddSingleton(ProcessControl);
        services.AddSingleton(Integrations);
        services.AddSingleton(Vault);
        services.AddSingleton(Installer);
        services.AddSingleton(Library);
        services.AddSingleton(Auth);
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

        Provider = services.BuildServiceProvider();
    }

    public void Start() => Queue.Start();
    public async Task StopAsync() => await Queue.StopAsync();

    public T Get<T>() where T : notnull => Provider.GetRequiredService<T>();
}
