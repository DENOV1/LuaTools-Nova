// LuaTools Nova — Linux process control. Clean-shutdown ladder: `steam -shutdown` first (a clean
// exit writes the bootstrap Shutdown line and skips the next boot's re-verification), then SIGTERM,
// then SIGKILL; only `steam`/`steamwebhelper` gate a relaunch; never start over a live client.
// Gamescope/SteamOS Game Mode restarts go through systemd user units when present.
using System.Diagnostics;
using LuaTools.Core.Services;

namespace LuaTools.Linux;

public sealed class ProcessControl(ISteamLocator locator) : IProcessControl
{
    private static readonly string[] RuntimeEnvVars =
    {
        "LD_LIBRARY_PATH", "LD_PRELOAD", "LD_AUDIT",
        "STEAM_RUNTIME_LIBRARY_PATH", "STEAM_ZENITY",
    };

    public bool IsSteamRunning() => SteamAlive();

    private static bool SteamAlive() => ProcessAlive("steam") || ProcessAlive("steamwebhelper");

    private static bool ProcessAlive(string name)
    {
        try
        {
            using var p = Process.Start(new ProcessStartInfo("pgrep", $"-x {name}")
            {
                UseShellExecute = false,
                RedirectStandardOutput = true,
                CreateNoWindow = true,
            });
            string output = p?.StandardOutput.ReadToEnd() ?? "";
            p?.WaitForExit(2000);
            return output.Trim().Length > 0;
        }
        catch { return false; }
    }

    public void StopSteam()
    {
        if (!SteamAlive()) return;

        // 1. Clean shutdown via the client itself (bounded — the Debian wrapper can stall in the runtime).
        string? launcher = ResolveLauncher();
        if (launcher is not null)
        {
            try
            {
                using var p = Process.Start(new ProcessStartInfo("timeout", $"-k 5 20 {Quote(launcher)} -shutdown")
                { UseShellExecute = false, CreateNoWindow = true });
                p?.WaitForExit(25000);
            }
            catch { /* fall through to signals */ }
        }

        // 2. Wait up to ~12s for a clean exit.
        if (AwaitExit(120)) return;

        // 3. SIGTERM, wait ~3s.
        Signal("TERM");
        if (AwaitExit(30)) return;

        // 4. SIGKILL, wait ~2s.
        Signal("KILL");
        AwaitExit(20);
    }

    public bool StartSteam()
    {
        string? launcher = ResolveLauncher();
        if (launcher is null) return false;

        try
        {
            // Detached so Nova's exit can't kill Steam; environment stripped of the Steam runtime
            // vars when we inherited them (inside a Steam session they break spawned binaries).
            var psi = new ProcessStartInfo("/bin/sh",
                $"-c {Quote($"unset {string.Join(' ', RuntimeEnvVars)} 2>/dev/null; setsid nohup {Quote(launcher)} </dev/null >/dev/null 2>&1 &")}")
            {
                UseShellExecute = false,
                CreateNoWindow = true,
            };
            Process.Start(psi)?.Dispose();
            return true;
        }
        catch { return false; }
    }

    public bool RestartSteam()
    {
        // Game Mode (gamescope) fast path: Steam is supervised by a systemd user unit there;
        // restarting the unit preserves injection. Detected generically, not hardcoded to "-plus".
        if (TryRestartViaSystemdUnit()) return true;

        StopSteam();
        return StartSteam();
    }

    private static bool TryRestartViaSystemdUnit()
    {
        try
        {
            if (!Directory.Exists("/run/systemd/system")) return false;

            // SteamOS: steam-launcher.service is Steam's own user unit (the gamescope compositor
            // unit refuses manual restarts — restarting Steam alone is the only thing that works).
            if (SystemdUnitActive("steam-launcher.service"))
            {
                return SystemdRestart("steam-launcher.service");
            }

            // Bazzite/ChimeraOS Game Mode: a gamescope-session* unit supervises Steam.
            string? unit = FirstActiveGamescopeUnit();
            if (unit is not null) return SystemdRestart(unit);

            return false;
        }
        catch { return false; }
    }

    private static bool SystemdUnitActive(string unit)
    {
        try
        {
            using var p = Process.Start(new ProcessStartInfo("systemctl",
                $"--user is-active --quiet {unit}")
            { UseShellExecute = false, CreateNoWindow = true });
            p?.WaitForExit(2000);
            return p?.ExitCode == 0;
        }
        catch { return false; }
    }

    private static string? FirstActiveGamescopeUnit()
    {
        try
        {
            using var p = Process.Start(new ProcessStartInfo("systemctl",
                "--user list-units --type=service --state=active --plain --no-legend 'gamescope-session*'")
            {
                UseShellExecute = false,
                RedirectStandardOutput = true,
                CreateNoWindow = true,
            });
            string output = p?.StandardOutput.ReadToEnd() ?? "";
            p?.WaitForExit(3000);
            var line = output.Split('\n', StringSplitOptions.RemoveEmptyEntries).FirstOrDefault();
            return line?.Split(' ', StringSplitOptions.RemoveEmptyEntries).FirstOrDefault();
        }
        catch { return null; }
    }

    private static bool SystemdRestart(string unit)
    {
        try
        {
            var psi = new ProcessStartInfo("/bin/sh",
                $"-c \"setsid nohup systemctl --user restart {Quote(unit)} </dev/null >/dev/null 2>&1 &\"")
            { UseShellExecute = false, CreateNoWindow = true };
            Process.Start(psi)?.Dispose();
            return true;
        }
        catch { return false; }
    }

    /// <summary>Launcher resolution for shutdown/relaunch. Skips anything that looks like a wrapper shim
    /// so `-shutdown` reaches the real client.</summary>
    private static string? ResolveLauncher()
    {
        foreach (string candidate in new[]
                 {
                     "/usr/bin/steam", "/usr/games/steam", "/usr/local/bin/steam", "/bin/steam",
                 })
        {
            if (IsSafeNativeLauncher(candidate)) return candidate;
        }

        string path = Environment.GetEnvironmentVariable("PATH") ?? "";
        foreach (string dir in path.Split(':', StringSplitOptions.RemoveEmptyEntries))
        {
            string candidate = Path.Combine(dir, "steam");
            if (IsSafeNativeLauncher(candidate)) return candidate;
        }
        return null;
    }

    private static bool IsSafeNativeLauncher(string path)
    {
        try
        {
            if (!File.Exists(path)) return false;
            var mode = File.GetUnixFileMode(path);
            return (mode & UnixFileMode.UserExecute) != 0;
        }
        catch { return false; }
    }

    private static void Signal(string sig)
    {
        foreach (string name in new[] { "steam", "steamwebhelper" })
        {
            try
            {
                using var p = Process.Start(new ProcessStartInfo("pkill", $"-{sig} -x {name}")
                { UseShellExecute = false, CreateNoWindow = true });
                p?.WaitForExit(3000);
            }
            catch { /* best effort */ }
        }
    }

    /// <summary>Poll until the client is gone. $1 slices × 100ms.</summary>
    private static bool AwaitExit(int slices)
    {
        for (int i = 0; i < slices; i++)
        {
            if (!SteamAlive()) return true;
            Thread.Sleep(100);
        }
        return !SteamAlive();
    }

    private static string Quote(string value) =>
        "'" + value.Replace("'", "'\\''") + "'";
}
