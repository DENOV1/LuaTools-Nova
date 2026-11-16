// LuaTools Nova — UI/OS-boundary fakes + harness for headless lifecycle tests.
// Mirrors the registration shape of App.OnFrameworkInitializationCompleted.
using LuaTools.Application;
using LuaTools.Core.Services;
using Microsoft.Extensions.DependencyInjection;

namespace LuaTools.Integration.Tests;

public sealed class HeadlessSteamLocator : ISteamLocator
{
    public string? AutoDetectedPath => null;
    public string? EffectivePath => null;
    public bool IsOverridden => false;
    public bool IsValid => false;
    public string ClientExecutableFor(string steamPath) => System.IO.Path.Combine(steamPath, "steam.sh");
    public string? SteamLanguage => null;
    public string DetectionSource => "test";
}

public sealed class HeadlessProcessControl : IProcessControl
{
    public bool IsSteamRunning() => false;
    public void StopSteam() { }
    public bool StartSteam() => true;
    public bool RestartSteam() => true;
}

public sealed class HeadlessSystemIntegrations : ISystemIntegrations
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

public sealed class HeadlessDialogService : IDialogService
{
    public bool ConfirmResult = true;
    public bool Confirm(string title, string body) => ConfirmResult;
    public bool ConfirmWithCancel(string title, string body) => ConfirmResult;
    public void Info(string title, string body) { }
}

public sealed class HeadlessToastService : IToastService
{
    public void Show(string title, string message, bool error = false) { }
    public void ShowAction(string title, string message, string actionLabel, Action onAction, bool error = false) { }
}

public static class HeadlessHarness
{
    public static NovaServices Build()
    {
        return new NovaServices(
            steamLocator: new HeadlessSteamLocator(),
            processControl: new HeadlessProcessControl(),
            integrations: new HeadlessSystemIntegrations(),
            dispatcher: action => Avalonia.Threading.Dispatcher.UIThread.Post(action),
            configure: services =>
            {
                services.AddSingleton<INavigationHost>(new LuaTools.UI.NavigationHost());
                services.AddSingleton<IDialogService>(new HeadlessDialogService());
                services.AddSingleton<IToastService>(new HeadlessToastService());
            });
    }
}
