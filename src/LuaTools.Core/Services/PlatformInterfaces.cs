// LuaTools Nova — platform abstraction. Business code depends on these interfaces only;
// LuaTools.Linux provides the implementations (see ANALYSIS/LINUX_IMPLEMENTATION_MAP.md).

namespace LuaTools.Core.Services;

/// <summary>Steam install discovery + canonical paths.</summary>
public interface ISteamLocator
{
    string? AutoDetectedPath { get; }
    string? EffectivePath { get; }
    bool IsOverridden { get; }
    bool IsValid { get; }
    string ClientExecutableFor(string steamPath);
    string? SteamLanguage { get; }
    string DetectionSource { get; }
}

/// <summary>Steam process control (clean shutdown ladder, relaunch).</summary>
public interface IProcessControl
{
    bool IsSteamRunning();
    void StopSteam();
    bool StartSteam();
    bool RestartSteam();
}

/// <summary>Desktop/system integrations (URLs, reveal, clipboard, protocol, autostart, downloads dir).</summary>
public interface ISystemIntegrations
{
    void OpenUrl(string url);
    bool RevealPath(string? path);
    bool CopyToClipboard(string? text);
    void RegisterProtocol();
    void SetAutostart(bool enabled);
    string DownloadsFolder { get; }
    string? PickFolder(string title, string? initial);
    IReadOnlyList<string> PickFiles(string title, params string[] patterns);
    void MinimizeToTrayChanged(bool toTray);
    void ExitApplication();
}

/// <summary>UI-level services the application layer needs (implemented by the Avalonia host).</summary>
public interface IDialogService
{
    bool Confirm(string title, string body);
    bool ConfirmWithCancel(string title, string body);
    void Info(string title, string body);
}

public interface IToastService
{
    void Show(string title, string message, bool error = false);
    void ShowAction(string title, string message, string actionLabel, Action onAction, bool error = false);
}

/// <summary>Cross-page navigation host.</summary>
public interface INavigationHost
{
    void NavigateTo(string pageKey);
}
