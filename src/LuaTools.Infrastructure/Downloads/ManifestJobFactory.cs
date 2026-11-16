// Derived from madoiscool/LuaTools (MIT) — ManifestJobFactory.cs (jobs) + depot runner plumbing.
// Linux: the depot tool is SteamRE's official DepotDownloader (linux-x64 build) — see DepotDownloaderService.
using System.IO.Compression;
using System.Net;
using LuaTools.Core.Downloads;
using LuaTools.Core.Models;
using LuaTools.Core.Services;
using LuaTools.Infrastructure.Clients;

namespace LuaTools.Infrastructure.Downloads;

/// <summary>ManifestJobFactory: builds the jobs for every in-scope download.</summary>
public sealed class ManifestJobFactory(
    LuaToolsApiClient api,
    HubcapService hubcap,
    LuaTools.Core.Persistence.SettingsService settings,
    LuaInstaller installer,
    SteamLibraryService library,
    CoverCache covers,
    DepotDownloaderService depotTool,
    SteamDepotInfo depotInfo,
    AppliedFixIndexService fixIndex)
{
    public DownloadJob CreateManifestJob(
        long appId, string? gameName, string sourceName, bool needsKey,
        Func<DownloadedFile, DownloadItem, CancellationToken, Task<bool>>? confirm = null,
        Action<DownloadItem, JobResult?>? onFinished = null,
        Action? onReveal = null)
    {
        string title = gameName ?? appId.ToString();
        return new DownloadJob(
            DownloadKind.Manifest,
            $"manifest:{appId}",
            appId,
            title,
            SourceMeta.Get(sourceName).DisplayName ?? sourceName,
            covers.GetLocalPath(appId),
            (_, progress, ct) => needsKey
                ? hubcap.DownloadManifestAsync(appId.ToString(), settings.HubcapApiKey ?? "", progress, ct)
                : api.DownloadManifestAsync(appId.ToString(), sourceName, gameName, progress, ct),
            (file, _, _) => Task.FromResult(InstallManifest(file, appId, title)),
            confirm, onFinished, onReveal);
    }

    public DownloadJob CreateDlcJob(
        long appId, string baseAppId, string? gameName,
        Action<DownloadItem, JobResult?>? onFinished = null,
        Action? onReveal = null)
    {
        string title = gameName ?? appId.ToString();
        return new DownloadJob(
            DownloadKind.Dlc,
            $"dlc:{appId}",
            appId,
            title,
            "DLC unlock",
            covers.GetLocalPath(appId),
            (_, progress, ct) => api.GenerateDlcAsync(appId.ToString(), baseAppId, gameName, progress, ct),
            (file, _, _) => Task.FromResult(InstallManifest(file, appId, title)),
            null, onFinished, onReveal);
    }

    public DownloadJob CreateDenuvoJob(
        string fixId, string slot, string fallbackName,
        long appId, string gameName, string fixTitle,
        Action<DownloadItem, JobResult?>? onFinished = null)
    {
        bool isManifestSlot = slot == "manifest";
        return new DownloadJob(
            isManifestSlot ? DownloadKind.DenuvoManifest : DownloadKind.DenuvoFix,
            $"denuvo:{fixId}:{slot}",
            appId,
            gameName,
            fixTitle,
            covers.GetLocalPath(appId),
            (_, progress, ct) =>
            {
                if (!isManifestSlot && library.GetInstallDir(appId) is null)
                    throw new DownloadAbortedException(
                        $"Install {gameName} in Steam first, then apply the fix.");
                return api.DownloadDenuvoAsync(fixId, slot, fallbackName, progress, ct);
            },
            (file, _, _) => Task.FromResult(isManifestSlot
                ? InstallDenuvoManifest(file, appId)
                : ApplyDenuvoFix(file, appId, fixId, gameName)),
            null, onFinished);
    }

    public DownloadJob CreateDepotJob(
        long appId, string gameName, IReadOnlyList<DepotSelection> selections, string outDir,
        Action<DownloadItem, JobResult?>? onFinished = null)
    {
        return new DownloadJob(
            DownloadKind.Depot,
            $"depot:{appId}",
            appId,
            gameName,
            "Depot files",
            covers.GetLocalPath(appId),
            (item, progress, ct) => RunDepotsAsync(item, appId, selections, outDir, progress, ct),
            (_, _, _) => Task.FromResult(new JobResult(true,
                $"Downloaded {selections.Count} depot(s) to {outDir}.", outDir)),
            null, onFinished, null, outDir);
    }

    private async Task<DownloadedFile> RunDepotsAsync(
        DownloadItem item, long appId, IReadOnlyList<DepotSelection> selections,
        string outDir, IProgress<DownloadProgress> progress, CancellationToken ct)
    {
        var keys = depotTool.ResolveKeys(appId);
        if (keys.Count == 0) throw new DownloadAbortedException("No decryption keys found for this game.");

        bool outDirExisted = Directory.Exists(outDir);

        item.Detail = "Getting the downloader";
        if (await depotTool.EnsureToolAsync(progress, ct) is null)
            throw new DownloadAbortedException("Couldn't get the depot downloader.");
        progress.Report(new DownloadProgress(0, null));

        var resolved = new List<DepotSelection>(selections.Count);
        for (int i = 0; i < selections.Count; i++)
        {
            ct.ThrowIfCancellationRequested();
            item.Detail = $"Preparing · {i + 1} of {selections.Count}";

            var sized = await ResolveSharedAsync(selections[i], ct);

            if (!item.CompletedDepots.Contains(sized.DepotId))
            {
                sized = sized with { ManifestPath = await EnsureManifestAsync(item, sized, ct) };

                if (!keys.TryGetValue(sized.DepotId, out string? hex) || !TryParseKey(hex, out byte[] key))
                    throw new DownloadAbortedException($"No decryption key in Lua for depot {sized.DepotId}.");

                if (!ManifestFile.KeyLooksValid(sized.ManifestPath, key))
                    throw new DownloadAbortedException($"The decryption key for depot {sized.DepotId} is wrong.");
            }

            if (ManifestFile.TryRead(sized.ManifestPath) is { SizeOnDisk: > 0 } info)
                sized = sized with { Size = info.SizeOnDisk };

            resolved.Add(sized);
        }

        long totalSize = resolved.Sum(s => s.Size);
        long needed = resolved.Where(s => !item.CompletedDepots.Contains(s.DepotId)).Sum(s => s.Size);
        if (needed > 0 && DepotDownloaderService.FreeSpaceFor(outDir) is { } free && free < needed)
            throw new DownloadAbortedException(
                $"Not enough disk space: needs {ByteFormat.Size(needed)}, only {ByteFormat.Size(free)} free.");

        string keysFile = DepotDownloaderService.WriteKeysFile(keys);
        try
        {
            long done = 0;
            for (int i = 0; i < resolved.Count; i++)
            {
                ct.ThrowIfCancellationRequested();
                var ready = resolved[i];

                if (item.CompletedDepots.Contains(ready.DepotId)) { done += ready.Size; continue; }

                if (ready.Size > 0 && DepotDownloaderService.FreeSpaceFor(outDir) is { } left
                    && left < ready.Size)
                    throw new DownloadAbortedException(
                        $"Not enough disk space: needs {ByteFormat.Size(ready.Size)}, only {ByteFormat.Size(left)} free.");

                item.Detail = $"Depots · {i + 1} of {resolved.Count}";

                bool validate = item.NeedsValidate || outDirExisted;
                item.NeedsValidate = false;

                long baseBytes = done;
                var relay = new ProgressRelay<double>(f =>
                    progress.Report(new DownloadProgress(baseBytes + (long)(f * ready.Size), totalSize)));

                var phases = new ProgressRelay<DepotPhase>(ph =>
                {
                    item.Detail = ph switch
                    {
                        DepotPhase.PreAllocating => $"{item.Detail} · allocating files",
                        DepotPhase.Validating => $"{item.Detail} · checking existing files",
                        DepotPhase.Manifest => $"{item.Detail} · fetching manifest",
                        _ => item.Detail,
                    };
                    item.Status = ph == DepotPhase.Validating ? DownloadStatus.Verifying : DownloadStatus.Downloading;
                });

                var created = new ProgressRelay<string>(path => item.CreatedFiles.Add(path));

                var res = await depotTool.RunAsync(appId, ready, keysFile, outDir, validate, relay, ct, phases, created);
                if (!res.Ok)
                    throw new DownloadAbortedException(
                        res.Error == "tool" ? "Couldn't get the depot downloader."
                        : $"Depot {ready.DepotId} failed: {res.Error ?? ""}");

                item.CompletedDepots.Add(ready.DepotId);
                done += ready.Size;
                progress.Report(new DownloadProgress(done, totalSize));
            }

            item.Detail = null;
            return new DownloadedFile(outDir, "game");
        }
        finally
        {
            DeleteStaged(keysFile);
        }
    }

    private static bool TryParseKey(string? hex, out byte[] key)
    {
        key = Array.Empty<byte>();
        if (hex is not { Length: 64 }) return false;
        try { key = Convert.FromHexString(hex); return true; }
        catch (FormatException) { return false; }
    }

    private async Task<DepotSelection> ResolveSharedAsync(DepotSelection sel, CancellationToken ct)
    {
        if (sel.ManifestId is not null || sel.FromAppId is not { } owner) return sel;

        var info = await depotInfo.GetAsync(owner, ct);
        if (info?.Depots.FirstOrDefault(d => d.Id == sel.DepotId) is not { PublicManifestId: not null } owned)
            throw new DownloadAbortedException("Couldn't get this depot's manifest.");

        return sel with { ManifestId = owned.PublicManifestId, Size = owned.Size };
    }

    private async Task<string> EnsureManifestAsync(DownloadItem item, DepotSelection sel, CancellationToken ct)
    {
        if (depotTool.ResolveManifestPath(sel.DepotId, sel.ManifestId!) is { } have) return have;

        depotTool.DiscardCachedManifest(sel.DepotId, sel.ManifestId!);

        DownloadedFile staged;
        try
        {
            staged = await api.DownloadDepotManifestAsync(sel.DepotId, sel.ManifestId!, null, ct);
        }
        catch (ApiException ex) when (ex.Status == HttpStatusCode.Unauthorized || ex.Status == HttpStatusCode.Forbidden)
        {
            throw new DownloadAbortedException("Sign in to download depots.");
        }

        string unzipDir = Path.Combine(Path.GetDirectoryName(staged.FilePath)!, "mf_" + Guid.NewGuid().ToString("N"));
        try
        {
            string manifestFile = staged.FilePath;
            if (IsZip(staged.FilePath))
            {
                Directory.CreateDirectory(unzipDir);
                using var archive = ZipFile.OpenRead(staged.FilePath);
                var entry = archive.Entries.FirstOrDefault(e => !string.IsNullOrEmpty(e.Name))
                    ?? throw new DownloadAbortedException("Couldn't get this depot's manifest.");

                manifestFile = Path.Combine(unzipDir, $"{sel.DepotId}_{sel.ManifestId}.manifest");
                entry.ExtractToFile(manifestFile, overwrite: true);
            }

            if (!IsSteamManifest(manifestFile))
                throw new DownloadAbortedException("Couldn't get this depot's manifest.");

            var result = installer.InstallManifestFile(manifestFile);
            if (result.AnyFailed)
                throw new DownloadAbortedException(result.Error ?? "Couldn't get this depot's manifest.");
        }
        finally
        {
            DeleteStaged(staged.FilePath);
            try { if (Directory.Exists(unzipDir)) Directory.Delete(unzipDir, recursive: true); } catch { }
        }

        return depotTool.ResolveManifestPath(sel.DepotId, sel.ManifestId!)
               ?? throw new DownloadAbortedException("Couldn't get this depot's manifest.");
    }

    private JobResult InstallManifest(DownloadedFile file, long appId, string gameName)
    {
        try
        {
            var result = IsZip(file.FilePath)
                ? installer.InstallZip(file.FilePath, appId)
                : installer.InstallLua(file.FilePath, appId);

            if (result.Error is not null) return new JobResult(false, result.Error);
            if (result.AnyFailed)
                return new JobResult(false,
                    $"Couldn't install {result.Failed.Count} file(s). Close Steam (or use Restart Steam) and try again.");

            string message = result.ManifestCount > 0
                ? $"Added {gameName}: lua + {result.ManifestCount} manifest(s)."
                : $"Added {gameName}. Steam will fetch manifests.";

            return new JobResult(true, message, installer.ReadInstalledLua(appId));
        }
        finally
        {
            DeleteStaged(file.FilePath);
        }
    }

    private JobResult InstallDenuvoManifest(DownloadedFile file, long appId)
    {
        try
        {
            bool isZip = file.FileName.EndsWith(".zip", StringComparison.OrdinalIgnoreCase);
            var result = isZip
                ? installer.InstallZip(file.FilePath, appId, forceLocked: true)
                : installer.InstallLuaFile(file.FilePath, appId, forceLocked: true);

            if (result.AnyFailed)
                return new JobResult(false, result.Error ?? "Couldn't install. Close Steam (or Restart Steam) and try again.");

            return new JobResult(true, "Fix manifest installed.", installer.ReadInstalledLua(appId));
        }
        finally
        {
            DeleteStaged(file.FilePath);
        }
    }

    private JobResult ApplyDenuvoFix(DownloadedFile file, long appId, string fixId, string gameName)
    {
        try
        {
            string? installDir = library.GetInstallDir(appId);
            if (installDir is null)
                return new JobResult(false, $"Install {gameName} in Steam first, then apply the fix.");

            string fixKey = SafeFixKey(fixId);
            string fixDir = Path.Combine(installDir, FixRecordDir);
            string recordPath = Path.Combine(fixDir, $"{fixKey}.json");

            var record = new DenuvoFixRecord
            {
                AppId = appId,
                FixId = fixId,
                AppliedAt = DateTimeOffset.UtcNow.ToString("o"),
            };

            using var archive = ZipFile.OpenRead(file.FilePath);
            var plan = new List<(string RelPath, string Dest, string? BakRel, ZipArchiveEntry Entry)>();
            int failed = 0;

            foreach (var entry in archive.Entries)
            {
                if (string.IsNullOrEmpty(entry.Name)) continue;
                string relPath = entry.FullName.Replace('\\', '/');

                if (ResolveInside(installDir, entry.FullName) is not { } dest) { failed++; continue; }

                plan.Add((relPath, dest, File.Exists(dest) ? $"{fixKey}/{relPath}.bak" : null, entry));
            }

            foreach (var (relPath, _, bakRel, _) in plan)
                record.Files.Add(new DenuvoFixRecordEntry
                {
                    RelativePath = relPath,
                    Action = bakRel is null ? "added" : "modified",
                    BackupPath = bakRel,
                });

            try
            {
                Directory.CreateDirectory(fixDir);
                File.WriteAllText(recordPath, System.Text.Json.JsonSerializer.Serialize(record,
                    new System.Text.Json.JsonSerializerOptions { WriteIndented = true }));
            }
            catch (Exception ex)
            {
                return new JobResult(false, ex.Message);
            }

            var applied = new List<DenuvoFixRecordEntry>(plan.Count);

            foreach (var (relPath, dest, bakRel, entry) in plan)
            {
                try
                {
                    DenuvoFixRecordEntry recordEntry;
                    if (bakRel is not null)
                    {
                        string bakAbs = Path.Combine(fixDir, bakRel);
                        if (!File.Exists(bakAbs))
                        {
                            Directory.CreateDirectory(Path.GetDirectoryName(bakAbs)!);
                            File.Copy(dest, bakAbs, overwrite: false);
                        }
                        recordEntry = new DenuvoFixRecordEntry
                        {
                            RelativePath = relPath,
                            Action = "modified",
                            BackupPath = bakRel,
                            HashBefore = LuaTools.Core.AppInfo.FileHash.Sha256(dest),
                        };
                    }
                    else
                    {
                        recordEntry = new DenuvoFixRecordEntry { RelativePath = relPath, Action = "added" };
                    }

                    Directory.CreateDirectory(Path.GetDirectoryName(dest)!);
                    entry.ExtractToFile(dest, overwrite: true);

                    recordEntry.HashAfter = LuaTools.Core.AppInfo.FileHash.Sha256(dest);
                    applied.Add(recordEntry);
                }
                catch { failed++; }
            }

            record.Files = applied;
            try { File.WriteAllText(recordPath, System.Text.Json.JsonSerializer.Serialize(record,
                new System.Text.Json.JsonSerializerOptions { WriteIndented = true })); }
            catch { /* the provisional record stands */ }

            fixIndex.Add(appId, fixId, gameName, installDir, record.AppliedAt, record.Files.Count);

            if (failed > 0)
                return new JobResult(false, $"{failed} file(s) couldn't be written. Close the game and try again.");

            return new JobResult(true, $"{gameName} fix extracted into the game folder.", installDir);
        }
        catch (Exception ex)
        {
            return new JobResult(false, ex.Message);
        }
        finally
        {
            DeleteStaged(file.FilePath);
        }
    }

    // ── Fix revert ──────────────────────────────────────────────────

    public const string FixRecordDir = ".luatools-fix";

    public static string SafeFixKey(string fixId) =>
        string.Concat(fixId.Select(ch => Path.GetInvalidFileNameChars().Contains(ch) ? '_' : ch));

    public static string GetFixRecordPath(string installDir, string fixId) =>
        Path.Combine(installDir, FixRecordDir, $"{SafeFixKey(fixId)}.json");

    private static string? ResolveInside(string root, string relative)
    {
        try
        {
            string rootFull = Path.TrimEndingDirectorySeparator(Path.GetFullPath(root));
            string full = Path.GetFullPath(Path.Combine(rootFull, relative));
            return full.StartsWith(rootFull + Path.DirectorySeparatorChar, StringComparison.Ordinal)
                ? full
                : null;
        }
        catch { return null; }
    }

    public static DenuvoFixRecord? ReadFixRecord(string recordPath)
    {
        try
        {
            if (!File.Exists(recordPath)) return null;
            return System.Text.Json.JsonSerializer.Deserialize<DenuvoFixRecord>(File.ReadAllText(recordPath),
                new System.Text.Json.JsonSerializerOptions { PropertyNameCaseInsensitive = true });
        }
        catch { return null; }
    }

    public JobResult RevertDenuvoFix(long appId, string fixId, string gameName)
    {
        try
        {
            string? installDir = library.GetInstallDir(appId);
            if (installDir is null)
                return new JobResult(false, $"Install {gameName} in Steam first, then apply the fix.");

            string recordPath = GetFixRecordPath(installDir, fixId);
            var record = ReadFixRecord(recordPath);
            if (record is null)
                return new JobResult(false,
                    "No record of this fix being applied. It may already have been reverted.");

            int restored = 0, deleted = 0, errors = 0, conflicts = 0;
            string? conflictFile = null;

            foreach (var entry in record.Files)
            {
                if (ResolveInside(installDir, entry.RelativePath) is not { } dest) { errors++; continue; }

                try
                {
                    if (entry.Action == "modified" && entry.BackupPath is { } bakRel)
                    {
                        if (ResolveInside(Path.Combine(installDir, FixRecordDir), bakRel) is not { } bakAbs)
                        { errors++; continue; }
                        if (!File.Exists(bakAbs)) { errors++; continue; }

                        if (File.Exists(dest)
                            && !LuaTools.Core.AppInfo.FileHash.Matches(dest, entry.HashAfter)
                            && !LuaTools.Core.AppInfo.FileHash.Matches(dest, entry.HashBefore))
                        {
                            conflicts++;
                            conflictFile ??= entry.RelativePath;
                            continue;
                        }

                        File.Copy(bakAbs, dest, overwrite: true);
                        restored++;
                    }
                    else if (entry.Action == "added")
                    {
                        if (File.Exists(dest))
                        {
                            if (!LuaTools.Core.AppInfo.FileHash.Matches(dest, entry.HashAfter))
                            {
                                conflicts++;
                                conflictFile ??= entry.RelativePath;
                                continue;
                            }
                            File.Delete(dest);
                            deleted++;
                        }
                    }
                }
                catch { errors++; }
            }

            if (conflicts > 0)
                return new JobResult(false,
                    $"{conflictFile} has changed since this fix was applied. {conflicts} file(s) affected.");

            if (errors > 0)
                return new JobResult(false, $"{errors} file(s) couldn't be restored. Close the game and try again.");

            string backupDir = Path.Combine(installDir, FixRecordDir, SafeFixKey(fixId));
            try { if (Directory.Exists(backupDir)) Directory.Delete(backupDir, recursive: true); } catch { }
            try { if (File.Exists(recordPath)) File.Delete(recordPath); } catch { }

            fixIndex.Remove(appId, fixId);

            return new JobResult(true, $"{restored} restored, {deleted} added files removed.", installDir);
        }
        catch (Exception ex)
        {
            return new JobResult(false, ex.Message);
        }
    }

    public static bool IsZip(string path)
    {
        try
        {
            using var fs = File.OpenRead(path);
            Span<byte> sig = stackalloc byte[4];
            return fs.Read(sig) == 4 && sig[0] == 0x50 && sig[1] == 0x4B && sig[2] == 0x03 && sig[3] == 0x04;
        }
        catch { return false; }
    }

    private static bool IsSteamManifest(string path)
    {
        try
        {
            using var fs = File.OpenRead(path);
            Span<byte> sig = stackalloc byte[4];
            return fs.Read(sig) == 4 &&
                   sig[0] == 0xD0 && sig[1] == 0x17 && sig[2] == 0xF6 && sig[3] == 0x71;
        }
        catch { return false; }
    }

    public static void DeleteStaged(string path)
    {
        try { if (File.Exists(path)) File.Delete(path); } catch { /* best effort */ }
    }

    public LuaToolsApiClient Api => api;
}
