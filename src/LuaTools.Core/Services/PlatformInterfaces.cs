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

/// <summary>UI-level services the application layer needs (implemented by the Avalonia host).
/// Dialog methods are async by design: blocking the UI thread on a dialog that itself needs the
/// UI thread deadlocks the whole app (the v1.0.2 Manage-delete freeze — root cause).</summary>
public interface IDialogService
{
    Task<bool> ConfirmAsync(string title, string body);
    Task<bool> ConfirmWithCancelAsync(string title, string body);
    Task InfoAsync(string title, string body);
}

public interface IToastService
{
    void Show(string title, string message, bool error = false);
    void ShowAction(string title, string message, string actionLabel, Action onAction, bool error = false);
}

/// <summary>Optional capability: bind the toast presenter to a window (implemented by the UI host).
/// Code must type-check against this, never hard-cast IToastService to a concrete class.</summary>
public interface IToastPresenter
{
    void Attach(object window);
}

/// <summary>Cross-page navigation host.</summary>
public interface INavigationHost
{
    void NavigateTo(string pageKey);
}
