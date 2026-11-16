using Avalonia;
using AvApp = Avalonia.Application;
using Avalonia.Controls.ApplicationLifetimes;
using Avalonia.Markup.Xaml;
using LuaTools.Application;
using LuaTools.Application.ViewModels;
using LuaTools.Core.Services;
using LuaTools.Integrations;
using LuaTools.Linux;
using LuaTools.UI.Services;
using Microsoft.Extensions.DependencyInjection;
using System;
using System.Threading.Tasks;

namespace LuaTools.UI;

public partial class App : AvApp
{
    public static NovaServices Services { get; private set; } = null!;
    public static MainWindow? Main { get; private set; }

    public override void Initialize()
    {
        AvaloniaXamlLoader.Load(this);
    }

    public override void OnFrameworkInitializationCompleted()
    {
        // UI-layer service implementations are created here and registered into the SINGLE
        // composition root (NovaServices) before the container is built — so every ViewModel
        // resolves them via constructor/Get<> without a second, ad-hoc composition root.
        var dialogs = new DialogServiceImpl();
        var toasts = new ToastServiceImpl();
        var navigation = new NavigationHost();

        Services = new NovaServices(
            dispatcher: action => Avalonia.Threading.Dispatcher.UIThread.Post(action),
            onRecentAdd: appId => CacheRecent(appId),
            configure: services =>
            {
                services.AddSingleton<INavigationHost>(navigation);
                services.AddSingleton<IDialogService>(dialogs);
                services.AddSingleton<IToastService>(toasts);
            });

        // Everything resolves from the container (ValidateOnBuild already guaranteed the graph).
        var mainVm = Services.Get<MainViewModel>();
        var onboarding = mainVm.Onboarding;
        var homeVm = Services.Get<HomeViewModel>();
        var downloadVm = Services.Get<DownloadViewModel>();
        var downloadsVm = Services.Get<DownloadsViewModel>();
        var manageVm = Services.Get<ManageViewModel>();
        var buildsVm = Services.Get<BuildsViewModel>();
        var fixesVm = Services.Get<FixesViewModel>();
        var pluginVm = Services.Get<PluginViewModel>();
        var modeVm = Services.Get<ModeViewModel>();
        var settingsVm = Services.Get<SettingsViewModel>();
        var dropVm = homeVm.Drop;
        var pluginInstaller = Services.Get<PluginInstallerService>();
        var cefInjector = Services.Get<CefInjectorService>();
        var httpServer = Services.Get<HttpServerService>();

        // Cross-page navigation delegates (same graph as the original's App.OnStartup)
        homeVm.NavigateToGame = appId => { navigation.NavigateTo("manage"); _ = manageVm.OpenDetailForAppIdAsync(appId); };
        homeVm.NavigateToPlugin = () => navigation.NavigateTo("plugin");
        homeVm.NavigateToManage = () => navigation.NavigateTo("manage");
        homeVm.NavigateToSettings = () => navigation.NavigateTo("settings");
        homeVm.NavigateToMode = () => navigation.NavigateTo("mode");
        homeVm.InstallPluginAsync = async () => await pluginInstaller.InstallAsync(null);
        onboarding.InstallPluginAsync = async () => await pluginInstaller.InstallAsync(null);
        onboarding.RefreshHome = () => homeVm.LoadAsync();

        downloadVm.RequestSignIn = () => RunSignInAsync(mainVm);
        downloadVm.NavigateToGame = appId => { navigation.NavigateTo("manage"); _ = manageVm.OpenDetailForAppIdAsync(appId); };
        fixesVm.RequestSignIn = () => RunSignInAsync(mainVm);
        settingsVm.RequestSignIn = () => RunSignInAsync(mainVm);
        settingsVm.RequestRestartPrompt = () =>
            _ = dialogs.InfoAsync("Language changed", "Restart LuaTools Nova to apply the new language everywhere.");
        manageVm.NavigateToAdd = _ => navigation.NavigateTo("add");
        manageVm.NavigateToBuilds = appId => { navigation.NavigateTo("builds"); _ = buildsVm.SelectAppAsync(appId); };
        manageVm.OpenLaunchOptions = (appId, name) => _ = dialogs.InfoAsync("Launch options",
            $"Launch options editing for {name} (App {appId}) edits Steam's appinfo.vdf and requires closing Steam. This editor opens from the game's context menu.");
        buildsVm.NavigateToManage = appId => { navigation.NavigateTo("manage"); _ = manageVm.OpenDetailForAppIdAsync(appId); };
        downloadsVm.RevealItem = item => navigation.NavigateTo("downloads");

        dropVm.InstallByAppId = async appId => await downloadVm.ProtocolInstall(appId, null);
        onboarding.SetApplyRecommendedCommand.Execute(true);

        if (ApplicationLifetime is IClassicDesktopStyleApplicationLifetime desktop)
        {
            Main = new MainWindow(mainVm, navigation, dialogs, toasts, Services)
            {
                DataContext = mainVm,
            };

            navigation.Attach(Main);
            desktop.MainWindow = Main;

            desktop.ShutdownRequested += async (_, e) =>
            {
                e.Cancel = !Main.ConfirmExit();
                if (!e.Cancel) await Services.StopAsync();
            };

            _ = StartBackgroundAsync(mainVm, homeVm, downloadVm, settingsVm, fixesVm, manageVm,
                buildsVm, pluginVm, modeVm, httpServer, cefInjector);
        }

        base.OnFrameworkInitializationCompleted();
    }

    private static async Task RunSignInAsync(MainViewModel mainVm)
    {
        if (!mainVm.SignInCommand.IsRunning) await mainVm.SignInCommand.ExecuteAsync(null);
    }

    private static void CacheRecent(long appId)
    {
        try
        {
            var ids = Services.Cache.GetLoadedAppIds().ToList();
            ids.Remove(appId);
            ids.Insert(0, appId);
            Services.Cache.SaveLoadedAppIds(ids.Take(50));
        }
        catch { /* cosmetic */ }
    }

    private static async Task StartBackgroundAsync(
        MainViewModel mainVm, HomeViewModel homeVm, DownloadViewModel downloadVm,
        SettingsViewModel settingsVm, FixesViewModel fixesVm, ManageViewModel manageVm,
        BuildsViewModel buildsVm, PluginViewModel pluginVm, ModeViewModel modeVm,
        HttpServerService httpServer, CefInjectorService cefInjector)
    {
        try
        {
            Services.Queue.Start();
            HttpServerService.UiBridge.Post = action => Avalonia.Threading.Dispatcher.UIThread.Post(action);
            SystemIntegrations.UiBridge.CopyToClipboard = text => Main?.CopyToClipboard(text) ?? false;
            SystemIntegrations.UiBridge.PickFolder = (title, initial) => Main?.PickFolderAsync(title, initial).GetAwaiter().GetResult();
            SystemIntegrations.UiBridge.PickFiles = (title, patterns) => Main?.PickFilesAsync(title, patterns).GetAwaiter().GetResult();
            SystemIntegrations.UiBridge.ExitApplication = () => Main?.RequestExit();
            SystemIntegrations.UiBridge.MinimizeToTrayChanged = _ => { };

            Services.Integrations.RegisterProtocol();

            var cts = new CancellationTokenSource();
            _ = httpServer.StartAsync(cts.Token);
            _ = cefInjector.StartAsync(cts.Token);

            await mainVm.InitializeAsync();

            if (!Services.Cache.OnboardingComplete)
                mainVm.Onboarding.IsOpen = true;

            // Startup jobs (mirrors the original's five fire-and-forget tasks)
            _ = Services.Analytics.TrackAppLaunchAsync();
            _ = Services.Donate.SendPendingKeysIfEnabledAsync();
            _ = Services.Hardware.EnsureFreshAsync();
            _ = Services.AppList.EnsureLoadedAsync();
            _ = downloadVm.LoadFeaturedAsync();
            _ = homeVm.LoadAsync();
            _ = Task.Run(settingsVm.OnViewLoaded);
            _ = fixesVm.LoadAsync();
            _ = manageVm.LoadAsync();
            _ = buildsVm.LoadAsync();
            _ = pluginVm.LoadAsync();
            _ = modeVm.LoadAsync();
        }
        catch { /* a failed startup job must not take the shell down */ }
    }
}

public sealed class NavigationHost : INavigationHost
{
    private MainWindow? _window;
    private string _current = "home";

    public void Attach(MainWindow window) => _window = window;
    public string Current => _current;

    public void NavigateTo(string pageKey)
    {
        _current = pageKey;
        _window?.NavigateTo(pageKey);
    }
}
