// LuaTools Nova — Linux shell/desktop integrations (xdg-open, reveal, clipboard, protocol
// handler, autostart, Downloads folder). Everything is user-level; Nova never needs root.
using System.Diagnostics;
using LuaTools.Core.Services;

namespace LuaTools.Linux;

public sealed class SystemIntegrations : ISystemIntegrations
{
    public void OpenUrl(string url)
    {
        try
        {
            SpawnClean($"xdg-open {SingleQuote(url)}");
        }
        catch { /* no shell association */ }
    }

    public bool RevealPath(string? path)
    {
        if (string.IsNullOrWhiteSpace(path)) return false;
        try
        {
            if (File.Exists(path))
            {
                // DBus file-manager "show item" when supported, else open the parent.
                if (TryDBusReveal(path)) return true;
                SpawnClean($"xdg-open {SingleQuote(Path.GetDirectoryName(path) ?? path)}");
                return true;
            }
            if (Directory.Exists(path))
            {
                SpawnClean($"xdg-open {SingleQuote(path)}");
                return true;
            }
            return false;
        }
        catch { return false; }
    }

    private static bool TryDBusReveal(string path)
    {
        try
        {
            var psi = new ProcessStartInfo("gdbus", $"call --session --dest org.freedesktop.FileManager1 " +
                $"--object-path /org/freedesktop/FileManager1 --method org.freedesktop.FileManager1.ShowItems " +
                $"[\"file://{Uri.EscapeDataString(path)}\"] \"\"")
            { UseShellExecute = false, CreateNoWindow = true, RedirectStandardError = true };
            using var p = Process.Start(psi);
            p?.WaitForExit(3000);
            return p?.ExitCode == 0;
        }
        catch { return false; }
    }

    public bool CopyToClipboard(string? text)
    {
        if (string.IsNullOrEmpty(text)) return false;
        try
        {
            return UiBridge.CopyToClipboard?.Invoke(text) ?? false;
        }
        catch { return false; }
    }

    public void RegisterProtocol()
    {
        try
        {
            string appsDir = Path.Combine(
                Environment.GetEnvironmentVariable("XDG_DATA_HOME")
                ?? Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile),
                    ".local", "share"),
                "applications");
            Directory.CreateDirectory(appsDir);

            string exe = Environment.ProcessPath ?? "luatools-nova";
            string desktop = $"""
                [Desktop Entry]
                Type=Application
                Name=LuaTools Nova
                Comment=Steam manifest/lua management for Linux
                Exec={exe} %u
                Icon=luatools-nova
                Terminal=false
                Categories=Game;Utility;
                MimeType=x-scheme-handler/luatools;x-scheme-handler/luatoolsnova;
                NoDisplay=false
                """;
            string path = Path.Combine(appsDir, "luatools-nova.desktop");
            File.WriteAllText(path, desktop);
            try
            {
                File.SetUnixFileMode(path, UnixFileMode.UserRead | UnixFileMode.UserWrite |
                    UnixFileMode.GroupRead | UnixFileMode.OtherRead);
            }
            catch { }

            try
            {
                using var p = Process.Start(new ProcessStartInfo("update-desktop-database", Quote(appsDir))
                { UseShellExecute = false, CreateNoWindow = true });
                p?.WaitForExit(3000);
            }
            catch { /* not installed → harmless */ }
        }
        catch { /* best effort */ }
    }

    public void SetAutostart(bool enabled)
    {
        try
        {
            string configHome = Environment.GetEnvironmentVariable("XDG_CONFIG_HOME")
                ?? Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), ".config");
            string autostartDir = Path.Combine(configHome, "autostart");
            string path = Path.Combine(autostartDir, "luatools-nova.desktop");

            if (!enabled)
            {
                if (File.Exists(path)) File.Delete(path);
                return;
            }

            Directory.CreateDirectory(autostartDir);
            string exe = Environment.ProcessPath ?? "luatools-nova";
            File.WriteAllText(path, $"""
                [Desktop Entry]
                Type=Application
                Name=LuaTools Nova
                Exec={exe} --minimized
                Terminal=false
                X-GNOME-Autostart-enabled=true
                """);
        }
        catch { /* best effort */ }
    }

    public string DownloadsFolder
    {
        get
        {
            try
            {
                using var p = Process.Start(new ProcessStartInfo("xdg-user-dir", "DOWNLOAD")
                { UseShellExecute = false, RedirectStandardOutput = true, CreateNoWindow = true });
                string output = p?.StandardOutput.ReadToEnd().Trim() ?? "";
                p?.WaitForExit(1500);
                if (!string.IsNullOrWhiteSpace(output) && Directory.Exists(output)) return output;
            }
            catch { /* fall through */ }

            string profile = Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);
            string guess = Path.Combine(profile, "Downloads");
            return Directory.Exists(guess) ? guess : profile;
        }
    }

    public string? PickFolder(string title, string? initial)
    {
        return UiBridge.PickFolder?.Invoke(title, initial);
    }

    public IReadOnlyList<string> PickFiles(string title, params string[] patterns)
    {
        return UiBridge.PickFiles?.Invoke(title, patterns) ?? Array.Empty<string>();
    }

    public void MinimizeToTrayChanged(bool toTray) => UiBridge.MinimizeToTrayChanged?.Invoke(toTray);
    public void ExitApplication() => UiBridge.ExitApplication?.Invoke();

    /// <summary>Wired once at UI startup so platform calls reach the views.</summary>
    public static class UiBridge
    {
        public static Func<string, string?, string?>? PickFolder { get; set; }
        public static Func<string, string[], IReadOnlyList<string>>? PickFiles { get; set; }
        public static Action<bool>? MinimizeToTrayChanged { get; set; }
        public static Action? ExitApplication { get; set; }
        public static Func<string, bool>? CopyToClipboard { get; set; }
    }

    internal static void SpawnClean(string command)
    {
        // Strip Steam runtime env vars: inside a Steam session, LD_LIBRARY_PATH points at the
        // 32-bit runtime and crashes spawned GUI binaries (verified Linux hazard).
        var psi = new ProcessStartInfo("/bin/sh",
            $"-c \"unset LD_LIBRARY_PATH LD_PRELOAD LD_AUDIT STEAM_RUNTIME_LIBRARY_PATH STEAM_ZENITY; setsid {command} >/dev/null 2>&1 &\"")
        {
            UseShellExecute = false,
            CreateNoWindow = true,
        };
        Process.Start(psi)?.Dispose();
    }

    private static string SingleQuote(string value) =>
        "'" + value.Replace("'", "'\\''") + "'";

    internal static string Quote(string value) => SingleQuote(value);
}
