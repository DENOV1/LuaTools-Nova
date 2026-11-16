// LuaTools Nova — diagnostic logging to <DataDir>/logs/nova.log.
// Rationale (v1.0.3): fetch/install/restart failures were silent or mislabelled ("No available
// sources found" for what was a backend outage). A small on-disk log gives the user something
// concrete to report; nothing here is security-sensitive (paths + statuses only).
using System.Collections.Concurrent;

namespace LuaTools.Core.Services;

public static class NovaLog
{
    private static readonly ConcurrentQueue<string> _buffer = new();
    private static string? _logDir;
    private static string? _logPath;
    private static readonly object _ioLock = new();

    /// <summary>Called once at startup (NovaServices) to point the log at DataDir/logs.</summary>
    public static void Initialize(string logsDir)
    {
        lock (_ioLock) { _logDir = logsDir; _logPath = null; }
    }

    public static string LogPath
    {
        get
        {
            if (_logPath is null)
            {
                try
                {
                    string dir = _logDir ?? Path.Combine(Path.GetTempPath(), "luatools-nova-logs");
                    _logPath = Path.Combine(dir, "nova.log");
                    Directory.CreateDirectory(dir);
                }
                catch { _logPath = Path.Combine(Path.GetTempPath(), "luatools-nova.log"); }
            }
            return _logPath;
        }
    }

    /// <summary>Tests can redirect the log file.</summary>
    public static void ResetForTests(string? dir = null)
    {
        lock (_ioLock) { _logDir = dir; _logPath = dir is null ? null : Path.Combine(dir, "nova.log"); }
    }

    public static void Info(string area, string message) => Write("INFO", area, message);
    public static void Warn(string area, string message) => Write("WARN", area, message);
    public static void Error(string area, string message) => Write("ERROR", area, message);

    private static void Write(string level, string area, string message)
    {
        string line = $"{DateTime.Now:yyyy-MM-dd HH:mm:ss} [{level}] {area}: {message}";
        _buffer.Enqueue(line);
        while (_buffer.Count > 512) _buffer.TryDequeue(out _);
        try
        {
            lock (_ioLock)
            {
                string? dir = Path.GetDirectoryName(LogPath);
                if (dir is not null && !Directory.Exists(dir)) Directory.CreateDirectory(dir);
                File.AppendAllText(LogPath, line + Environment.NewLine);
            }
        }
        catch { /* logging must never break the app */ }
    }
}
