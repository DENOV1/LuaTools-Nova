// Derived from madoiscool/LuaTools (MIT) — DepotDownloaderService.cs + AppliedFixIndexService.cs.
// Linux: the tool is SteamRE's official DepotDownloader (linux-x64 build), run via its apphost or
// `dotnet DepotDownloader.dll`. Same flag protocol as the original (-depotkeys/-manifestfile/-validate).
using System.Diagnostics;
using System.Globalization;
using System.IO.Compression;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;
using LuaTools.Core.AppInfo;
using LuaTools.Core.Downloads;
using LuaTools.Core.Lua;
using LuaTools.Core.Models;
using LuaTools.Core.Persistence;
using LuaTools.Core.Services;
using LuaTools.Infrastructure.Auth;
using LuaTools.Infrastructure.Clients;
using LuaTools.Infrastructure.Network;

namespace LuaTools.Infrastructure.Downloads;

public sealed partial class DepotDownloaderService(
    GithubProxy gh,
    ISteamLocator steam,
    IAccessTokenSource auth,
    CacheService cache)
{
    public bool CanFetchManifests => true;

    private static readonly string ToolDir = ToolPaths.DataDir("tools", "depotdownloader");

    private static string DllPath => Path.Combine(ToolDir, "DepotDownloader.dll");
    private static string AppHostPath => Path.Combine(ToolDir, "DepotDownloader");

    private static readonly JsonSerializerOptions JsonOpts = new() { PropertyNameCaseInsensitive = true };

    private static readonly TimeSpan SilenceTimeout = TimeSpan.FromMinutes(10);
    private const int MaxChunkDownloads = 32;
    private static readonly TimeSpan ToolCheckInterval = TimeSpan.FromHours(6);

    private readonly SemaphoreSlim _toolGate = new(1, 1);
    private readonly SemaphoreSlim _runGate = new(1, 1);

    private const string PreAllocatingPrefix = "Pre-allocating ";

    private static DepotPhase? PhaseOf(string line) => line switch
    {
        _ when line.StartsWith(PreAllocatingPrefix, StringComparison.Ordinal) => DepotPhase.PreAllocating,
        _ when line.StartsWith("Validating ", StringComparison.Ordinal) => DepotPhase.Validating,
        _ when line.StartsWith("Downloading depot ", StringComparison.Ordinal)
               && line.EndsWith(" manifest", StringComparison.Ordinal) => DepotPhase.Manifest,
        _ when line.StartsWith("Downloading depot ", StringComparison.Ordinal) => DepotPhase.Downloading,
        _ => null,
    };

    [GeneratedRegex(@"^\s*([0-9]+(?:\.[0-9]+)?)%\s+(.+)$")]
    private static partial Regex ProgressRegex();

    private static bool CheckedRecently(long lastMs) =>
        lastMs > 0 && DateTimeOffset.UtcNow.ToUnixTimeMilliseconds() - lastMs < (long)ToolCheckInterval.TotalMilliseconds;

    public async Task<string?> EnsureToolAsync(IProgress<DownloadProgress>? progress, CancellationToken ct = default)
    {
        if (ToolExists() && CheckedRecently(cache.DepotDownloaderCheckedAtMs)) return ToolPath();

        await _toolGate.WaitAsync(ct);
        bool have = false;
        try
        {
            have = ToolExists();
            if (have && CheckedRecently(cache.DepotDownloaderCheckedAtMs)) return ToolPath();

            void RecordAttempt() =>
                cache.DepotDownloaderCheckedAtMs = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds();

            string url = $"https://api.github.com/repos/{AppConfig.DepotDownloaderRepo}/releases/latest";
            using var res = await gh.SendAsync(url, ct);
            if (res is null || !res.IsSuccessStatusCode)
            {
                if (have) RecordAttempt();
                return have ? ToolPath() : null;
            }

            var release = JsonSerializer.Deserialize<GithubRelease>(await res.Content.ReadAsStringAsync(ct), JsonOpts);
            var asset = release?.Assets.FirstOrDefault(a =>
                a.Name.Contains("linux-x64", StringComparison.OrdinalIgnoreCase) &&
                a.Name.EndsWith(".zip", StringComparison.OrdinalIgnoreCase))
                ?? release?.Assets.FirstOrDefault(a =>
                a.Name.EndsWith(".zip", StringComparison.OrdinalIgnoreCase) &&
                a.Name.Contains("linux", StringComparison.OrdinalIgnoreCase));
            if (asset is null)
            {
                if (have) RecordAttempt();
                return have ? ToolPath() : null;
            }

            if (have && !string.IsNullOrEmpty(release!.TagName)
                     && string.Equals(release.TagName, cache.DepotDownloaderVersion, StringComparison.Ordinal))
            {
                cache.DepotDownloaderCheckedAtMs = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds();
                return ToolPath();
            }

            Directory.CreateDirectory(ToolDir);
            string zipPath = Path.Combine(ToolDir, "depotdownloader.zip");
            var sized = progress is null ? null : new ProgressRelay<double?>(f =>
                progress.Report(new DownloadProgress(
                    (long)((f ?? 0) * asset.Size), asset.Size > 0 ? asset.Size : null)));
            await gh.DownloadAsync(asset.DownloadUrl, zipPath, sized, ct);

            if (!AssetHash.Matches(zipPath, asset.Digest))
            {
                try { File.Delete(zipPath); } catch { }
                if (have) RecordAttempt();
                return have ? ToolPath() : null;
            }

            // Extract the WHOLE zip: the tool needs SteamKit2.dll and friends beside it.
            ZipFile.ExtractToDirectory(zipPath, ToolDir, overwriteFiles: true);
            try { File.Delete(zipPath); } catch { }

            if (!ToolExists())
            {
                if (have) RecordAttempt();
                return have ? ToolPath() : null;
            }

            try
            {
                if (File.Exists(AppHostPath))
                    File.SetUnixFileMode(AppHostPath, UnixFileMode.UserExecute | UnixFileMode.GroupExecute | UnixFileMode.OtherExecute);
            }
            catch { }

            cache.DepotDownloaderVersion = release!.TagName;
            cache.DepotDownloaderCheckedAtMs = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds();
            return ToolPath();
        }
        catch (OperationCanceledException) { throw; }
        catch
        {
            if (have) cache.DepotDownloaderCheckedAtMs = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds();
            return have ? ToolPath() : null;
        }
        finally { _toolGate.Release(); }
    }

    private static bool ToolExists() => File.Exists(DllPath) || File.Exists(AppHostPath);
    private static string ToolPath() => File.Exists(AppHostPath) ? AppHostPath : DllPath;

    public IReadOnlyDictionary<long, string> ResolveKeys(long appId)
    {
        var keys = new Dictionary<long, string>();

        try
        {
            if (steam.EffectivePath is { } root)
            {
                string lua = Path.Combine(root, "config", "stplug-in", $"{appId}.lua");
                if (File.Exists(lua) && LuaFileParser.Parse(lua, appId) is { } parsed)
                {
                    foreach (var e in parsed.Entries.Concat(parsed.DisabledEntries))
                        if (e.Key is { Length: > 0 }) keys[e.Id] = e.Key;
                }
            }
        }
        catch { /* best effort */ }

        try
        {
            if (steam.EffectivePath is { } root)
            {
                string vdf = Path.Combine(root, "config", "config.vdf");
                if (File.Exists(vdf))
                {
                    foreach (var (depot, key) in DonateKeysService.ExtractKeys(File.ReadAllText(vdf)))
                        if (long.TryParse(depot, out long id) && !keys.ContainsKey(id)) keys[id] = key;
                }
            }
        }
        catch { /* best effort */ }

        return keys;
    }

    public string? ResolveManifestPath(long depotId, string manifestId)
    {
        if (CachedManifestPath(depotId, manifestId) is not { } path) return null;
        return ManifestFile.Matches(path, depotId, manifestId) ? path : null;
    }

    private string? CachedManifestPath(long depotId, string manifestId)
    {
        string name = $"{depotId}_{manifestId}.manifest";
        if (steam.EffectivePath is not { } root) return null;
        string path = Path.Combine(root, "depotcache", name);
        return path;
    }

    public bool DiscardCachedManifest(long depotId, string manifestId)
    {
        if (CachedManifestPath(depotId, manifestId) is not { } path) return false;
        try
        {
            if (!File.Exists(path)) return false;
            File.Delete(path);
            return true;
        }
        catch { return false; }
    }

    public static long? FreeSpaceFor(string path)
    {
        try
        {
            string? root = Path.GetPathRoot(Path.GetFullPath(path));
            return root is null ? null : new DriveInfo(root).AvailableFreeSpace;
        }
        catch { return null; }
    }

    public static string DriveOf(string path)
    {
        try { return Path.GetPathRoot(Path.GetFullPath(path)) ?? ""; }
        catch { return ""; }
    }

    public static string WriteKeysFile(IReadOnlyDictionary<long, string> keys)
    {
        Directory.CreateDirectory(HttpFileDownloader.StagingFolder);
        string path = Path.Combine(HttpFileDownloader.StagingFolder, $"depotkeys_{Guid.NewGuid():N}.txt");

        var sb = new StringBuilder();
        foreach (var (id, key) in keys) sb.Append(id).Append(';').Append(key).Append('\n');
        File.WriteAllText(path, sb.ToString(), new UTF8Encoding(false));
        return path;
    }

    public async Task<DepotRunResult> RunAsync(
        long appId, DepotSelection sel, string keysFile, string outDir, bool validate,
        IProgress<double>? depotFraction, CancellationToken ct, IProgress<DepotPhase>? phase = null,
        IProgress<string>? createdFile = null)
    {
        string? exe = await EnsureToolAsync(null, ct);
        if (exe is null) return new DepotRunResult(false, "tool");

        await _runGate.WaitAsync(ct);
        try
        {
            Directory.CreateDirectory(outDir);

            var psi = new ProcessStartInfo
            {
                UseShellExecute = false,
                CreateNoWindow = true,
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                WorkingDirectory = ToolDir,
            };

            if (exe.EndsWith(".dll", StringComparison.OrdinalIgnoreCase))
            {
                psi.FileName = "dotnet";
                psi.ArgumentList.Add(exe);
            }
            else
            {
                psi.FileName = exe;
            }

            ArgumentNullException.ThrowIfNull(sel.ManifestId);
            ArgumentNullException.ThrowIfNull(sel.ManifestPath);

            foreach (string a in new[]
            {
                "-app", appId.ToString(),
                "-depot", sel.DepotId.ToString(),
                "-manifest", sel.ManifestId!,
                "-depotkeys", keysFile,
                "-manifestfile", sel.ManifestPath,
                "-dir", outDir,
                "-max-downloads", MaxChunkDownloads.ToString(),
            }) psi.ArgumentList.Add(a);

            if (validate) psi.ArgumentList.Add("-validate");

            // Strip Steam-runtime env vars so the child links against system libs.
            foreach (var name in new[] { "LD_LIBRARY_PATH", "LD_PRELOAD", "LD_AUDIT",
                     "STEAM_RUNTIME_LIBRARY_PATH", "STEAM_ZENITY" })
                psi.Environment.Remove(name);

            using var proc = Process.Start(psi);
            if (proc is null) return new DepotRunResult(false, "spawn");

            long lastOutput = DateTime.UtcNow.Ticks;
            string? lastError = null;
            string? lastLine = null;
            DepotPhase? lastPhase = null;

            proc.OutputDataReceived += (_, e) =>
            {
                if (e.Data is null) return;
                Interlocked.Exchange(ref lastOutput, DateTime.UtcNow.Ticks);

                var m = ProgressRegex().Match(e.Data);
                if (m.Success && double.TryParse(m.Groups[1].Value, NumberStyles.Float,
                        CultureInfo.InvariantCulture, out double pct))
                {
                    if (lastPhase != DepotPhase.Downloading)
                    {
                        lastPhase = DepotPhase.Downloading;
                        phase?.Report(DepotPhase.Downloading);
                    }
                    depotFraction?.Report(Math.Clamp(pct / 100d, 0d, 1d));
                    return;
                }

                string trimmed = e.Data.Trim();

                if (createdFile is not null
                    && trimmed.StartsWith(PreAllocatingPrefix, StringComparison.Ordinal))
                {
                    string path = trimmed[PreAllocatingPrefix.Length..].Trim();
                    if (path.Length > 0) createdFile.Report(path);
                }

                if (PhaseOf(trimmed) is { } p && p != lastPhase)
                {
                    lastPhase = p;
                    phase?.Report(p);
                }

                string line = e.Data.Trim();
                if (line.Length > 0 && !line.StartsWith("at ", StringComparison.Ordinal)) lastLine = line;
            };
            proc.ErrorDataReceived += (_, e) =>
            {
                if (e.Data is null) return;
                Interlocked.Exchange(ref lastOutput, DateTime.UtcNow.Ticks);
                lastError = e.Data;
            };
            proc.BeginOutputReadLine();
            proc.BeginErrorReadLine();

            using var reg = ct.Register(() => TryKill(proc));
            bool timedOut = false;

            while (!proc.WaitForExit(2000))
            {
                if (DateTime.UtcNow.Ticks - Interlocked.Read(ref lastOutput) <= SilenceTimeout.Ticks) continue;
                timedOut = true;
                TryKill(proc);
                break;
            }

            proc.WaitForExit();
            ct.ThrowIfCancellationRequested();

            if (timedOut) return new DepotRunResult(false, "timeout");
            if (proc.ExitCode != 0)
            {
                string? why = lastError ?? lastLine;
                if (why is { Length: > 300 }) why = why[..300];
                return new DepotRunResult(false, why ?? $"exit {proc.ExitCode}");
            }

            depotFraction?.Report(1d);
            return new DepotRunResult(true, null);
        }
        finally { _runGate.Release(); }
    }

    private const string DownloaderMarkerDir = ".DepotDownloader";

    public static bool HasDownloadedContent(string? dir) =>
        !string.IsNullOrWhiteSpace(dir)
        && Directory.Exists(Path.Combine(dir, DownloaderMarkerDir));

    public static bool TryDeleteCreatedFiles(string? dir, IReadOnlyCollection<string> createdFiles)
    {
        if (string.IsNullOrWhiteSpace(dir)) return false;

        string root;
        try { root = Path.TrimEndingDirectorySeparator(Path.GetFullPath(dir)); }
        catch { return false; }

        bool allGone = true;
        var touched = new HashSet<string>(StringComparer.Ordinal);

        foreach (string f in createdFiles)
        {
            try
            {
                string full = Path.GetFullPath(f, root);
                if (!full.StartsWith(root + Path.DirectorySeparatorChar, StringComparison.Ordinal))
                    continue;
                if (File.Exists(full)) File.Delete(full);
                if (Path.GetDirectoryName(full) is { Length: > 0 } parent) touched.Add(parent);
            }
            catch { allGone = false; }
        }

        try
        {
            string marker = Path.Combine(root, DownloaderMarkerDir);
            if (Directory.Exists(marker)) Directory.Delete(marker, recursive: true);
        }
        catch { allGone = false; }

        PruneEmptyDirectories(root, touched);
        return allGone;
    }

    private static void PruneEmptyDirectories(string root, HashSet<string> touched)
    {
        foreach (string start in touched.OrderByDescending(d => d.Length))
        {
            string? dir = start;
            while (dir is not null
                   && dir.Length > root.Length
                   && dir.StartsWith(root, StringComparison.Ordinal))
            {
                try
                {
                    if (!Directory.Exists(dir)) { dir = Path.GetDirectoryName(dir); continue; }
                    if (Directory.EnumerateFileSystemEntries(dir).Any()) break;
                    Directory.Delete(dir);
                }
                catch { break; }
                dir = Path.GetDirectoryName(dir);
            }
        }
    }

    private static void TryKill(Process proc)
    {
        try { if (!proc.HasExited) proc.Kill(entireProcessTree: true); } catch { /* already gone */ }
    }
}

/// <summary>Shared roots under the XDG data dir.</summary>
public static class ToolPaths
{
    public static string DataDir(params string[] segments)
    {
        string dataHome =
            Environment.GetEnvironmentVariable("XDG_DATA_HOME");
        if (string.IsNullOrWhiteSpace(dataHome))
            dataHome = Path.Combine(
                Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), ".local", "share");
        string dir = Path.Combine(dataHome, "LuaToolsNova");
        foreach (string s in segments) dir = Path.Combine(dir, s);
        return dir;
    }

    public static string AppDir(params string[] segments) => DataDir("app", segments.Length > 0 ? Path.Combine(segments) : "");
}

/// <summary>Hint index over the per-game .luatools-fix records (self-healing, never authoritative).</summary>
public sealed class AppliedFixIndexService(SteamLibraryService library)
{
    private static readonly string FilePath = ToolPaths.DataDir("applied-fixes.json");
    private static readonly JsonSerializerOptions JsonOpts =
        new() { PropertyNameCaseInsensitive = true, WriteIndented = true };
    private readonly SemaphoreSlim _gate = new(1, 1);

    public async Task<IReadOnlyList<AppliedFixIndexEntry>> ListAsync(CancellationToken ct = default)
    {
        await _gate.WaitAsync(ct);
        try
        {
            var index = Load();
            var live = new List<AppliedFixIndexEntry>(index.Entries.Count);
            bool pruned = false;

            foreach (var e in index.Entries)
            {
                if (ct.IsCancellationRequested) break;
                if (RecordExists(e)) live.Add(e);
                else pruned = true;
            }

            if (pruned) Save(new AppliedFixIndex { Entries = live });

            return live
                .OrderByDescending(e => e.AppliedAt, StringComparer.Ordinal)
                .ToList();
        }
        finally { _gate.Release(); }
    }

    public async Task<IReadOnlyList<AppliedFixIndexEntry>> RebuildAsync(CancellationToken ct = default)
    {
        var found = await Task.Run(() => Scan(ct), ct);
        await _gate.WaitAsync(ct);
        try { Save(new AppliedFixIndex { Entries = found }); }
        finally { _gate.Release(); }
        return found;
    }

    public void Add(long appId, string fixId, string gameName, string installDir, string appliedAt, int fileCount)
    {
        Mutate(entries =>
        {
            entries.RemoveAll(e => e.AppId == appId && e.FixId == fixId);
            entries.Add(new AppliedFixIndexEntry
            {
                AppId = appId,
                FixId = fixId,
                GameName = gameName,
                InstallDir = installDir,
                AppliedAt = appliedAt,
                FileCount = fileCount,
            });
        });
    }

    public void Remove(long appId, string fixId) =>
        Mutate(entries => entries.RemoveAll(e => e.AppId == appId && e.FixId == fixId));

    private void Mutate(Action<List<AppliedFixIndexEntry>> change)
    {
        _gate.Wait();
        try
        {
            var index = Load();
            change(index.Entries);
            Save(index);
        }
        catch { /* the index is a cache */ }
        finally { _gate.Release(); }
    }

    private static AppliedFixIndex Load()
    {
        try
        {
            if (!File.Exists(FilePath)) return new AppliedFixIndex();
            return JsonSerializer.Deserialize<AppliedFixIndex>(File.ReadAllText(FilePath), JsonOpts)
                   ?? new AppliedFixIndex();
        }
        catch { return new AppliedFixIndex(); }
    }

    private static void Save(AppliedFixIndex index)
    {
        try
        {
            Directory.CreateDirectory(Path.GetDirectoryName(FilePath)!);
            File.WriteAllText(FilePath, JsonSerializer.Serialize(index, JsonOpts));
        }
        catch { /* best effort */ }
    }

    private static bool RecordExists(AppliedFixIndexEntry e)
    {
        try
        {
            return !string.IsNullOrEmpty(e.InstallDir)
                && ManifestJobFactory.GetFixRecordPath(e.InstallDir, e.FixId) is { } p && File.Exists(p);
        }
        catch { return false; }
    }

    private List<AppliedFixIndexEntry> Scan(CancellationToken ct)
    {
        var found = new List<AppliedFixIndexEntry>();

        foreach (var game in library.EnumerateInstalled())
        {
            if (ct.IsCancellationRequested) break;

            string installDir = game.InstallDir;
            string fixDir = Path.Combine(installDir, ManifestJobFactory.FixRecordDir);
            string[] records;
            try
            {
                if (!Directory.Exists(fixDir)) continue;
                records = Directory.GetFiles(fixDir, "*.json");
            }
            catch { continue; }

            foreach (string path in records)
            {
                var record = ManifestJobFactory.ReadFixRecord(path);
                if (record is null || string.IsNullOrEmpty(record.FixId)) continue;

                found.Add(new AppliedFixIndexEntry
                {
                    AppId = record.AppId != 0 ? record.AppId : game.AppId,
                    FixId = record.FixId,
                    GameName = game.Name,
                    InstallDir = installDir,
                    AppliedAt = record.AppliedAt,
                    FileCount = record.Files.Count,
                });
            }
        }

        return found;
    }
}
