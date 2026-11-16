// Derived from madoiscool/LuaTools (MIT) — LuaInstaller.cs + SteamLinkParser.cs + ModeMigration.cs.
using System.IO.Compression;
using System.Text.RegularExpressions;
using LuaTools.Core.Lua;
using LuaTools.Core.Models;
using LuaTools.Core.Persistence;

namespace LuaTools.Core.Services;

/// <summary>Outcome of installing a downloaded artifact into Steam.</summary>
public sealed record InstallResult(bool LuaInstalled, int ManifestCount, IReadOnlyList<string> Failed, string? Error)
{
    public bool AnyFailed => Failed.Count > 0 || Error is not null;
    public static InstallResult Fail(string error) => new(false, 0, Array.Empty<string>(), error);
}

/// <summary>
/// Installs downloaded lua/manifest files into Steam: &lt;appid&gt;.lua → config/stplug-in,
/// *.manifest → depotcache (a sibling of steamapps — NOT under config).
/// </summary>
public sealed partial class LuaInstaller(
    ISteamLocator locator, SettingsService settings, LuaVault vault, Action<long>? onRecentAdd = null)
{
    private bool AutoUpdate => settings.AutoUpdateApps;

    public event Action<long>? Installed;

    [GeneratedRegex(@"^\s*\d+_(\d+)\s*$")]
    private static partial Regex BuildIdRegex();

    [GeneratedRegex(@"^(\s*)(setManifestid\s*\()", RegexOptions.IgnoreCase)]
    private static partial Regex SetManifestLineRegex();

    public static string? BuildIdFromFileName(string path)
    {
        var m = BuildIdRegex().Match(Path.GetFileNameWithoutExtension(path));
        return m.Success ? m.Groups[1].Value : null;
    }

    private static bool KeepPinsFor(string? buildId, bool forceLocked) => forceLocked || buildId is not null;

    private void CaptureInstalled(long appId, string installedPath, string? buildId, string? source)
    {
        try
        {
            if (buildId is not null)
                vault.Capture(appId, installedPath, LuaVariantKind.Build, buildId, source);
            else
                vault.SyncDefaultFromLive(appId);
        }
        catch { /* the vault is a convenience */ }
    }

    private void RecordLoaded(long appId)
    {
        try { onRecentAdd?.Invoke(appId); } catch { /* cosmetic */ }
        try { Installed?.Invoke(appId); }
        catch { /* a subscriber blowing up must never fail an install */ }
    }

    public string? StPlugInDir => locator.EffectivePath is { } p ? Path.Combine(p, "config", "stplug-in") : null;
    public string? DepotCacheDir => locator.EffectivePath is { } p ? Path.Combine(p, "depotcache") : null;

    public string? ReadInstalledLua(long appId)
    {
        string? dir = StPlugInDir;
        if (dir is null) return null;
        string path = Path.Combine(dir, $"{appId}.lua");
        return File.Exists(path) ? path : null;
    }

    private void WriteLua(string sourceLuaPath, string dest, bool forceLocked = false)
    {
        if (AutoUpdate && !forceLocked)
        {
            string lua = File.ReadAllText(sourceLuaPath);
            File.WriteAllText(dest, CommentOutManifestPins(lua));
        }
        else
        {
            File.Copy(sourceLuaPath, dest, overwrite: true);
        }
        StampNow(dest);
    }

    private static string CommentOutManifestPins(string lua)
    {
        var lines = lua.Split('\n');
        for (int i = 0; i < lines.Length; i++)
        {
            string trimmed = lines[i].TrimStart();
            if (trimmed.StartsWith("--")) continue;
            lines[i] = SetManifestLineRegex().Replace(lines[i], "$1-- $2");
        }
        return string.Join('\n', lines);
    }

    public InstallResult InstallLua(string luaPath, long appId, bool forceLocked = false, string? source = null)
    {
        string? dir = StPlugInDir;
        if (dir is null) return InstallResult.Fail("Steam location not found. Set it in Settings.");

        try
        {
            Directory.CreateDirectory(dir);
            string dest = Path.Combine(dir, $"{appId}.lua");
            string? buildId = BuildIdFromFileName(luaPath);

            WriteLua(luaPath, dest, KeepPinsFor(buildId, forceLocked));
            CaptureInstalled(appId, dest, buildId, source);
            RecordLoaded(appId);
            return new InstallResult(true, 0, Array.Empty<string>(), null);
        }
        catch (Exception ex)
        {
            return InstallResult.Fail(ex.Message);
        }
    }

    public InstallResult InstallLuaFile(string luaPath, long appId, bool forceLocked = false, string? source = null) =>
        InstallLua(luaPath, appId, forceLocked, source);

    public InstallResult InstallManifestFile(string manifestPath)
    {
        string? dir = DepotCacheDir;
        if (dir is null) return InstallResult.Fail("Steam location not found. Set it in Settings.");
        try
        {
            Directory.CreateDirectory(dir);
            string dest = Path.Combine(dir, Path.GetFileName(manifestPath));
            if (!File.Exists(dest))
            {
                File.Copy(manifestPath, dest, overwrite: false);
                StampNow(dest);
            }
            return new InstallResult(false, 1, Array.Empty<string>(), null);
        }
        catch (Exception ex)
        {
            return InstallResult.Fail(ex.Message);
        }
    }

    public InstallResult InstallZip(string zipPath, long appId, bool forceLocked = false, string? source = null)
    {
        string? plugDir = StPlugInDir;
        string? depotDir = DepotCacheDir;
        if (plugDir is null || depotDir is null)
            return InstallResult.Fail("Steam location not found. Set it in Settings.");

        ZipArchive archive;
        try { archive = ZipFile.OpenRead(zipPath); }
        catch (Exception ex) { return InstallResult.Fail($"Couldn't open the download: {ex.Message}"); }

        bool luaInstalled = false;
        int manifestCount = 0;
        var failed = new List<string>();

        using (archive)
        {
            try { Directory.CreateDirectory(plugDir); } catch { /* reported per-file below */ }
            try { Directory.CreateDirectory(depotDir); } catch { }

            foreach (var entry in archive.Entries)
            {
                if (string.IsNullOrEmpty(entry.Name)) continue;

                string name = entry.Name;
                bool isLua = name.EndsWith(".lua", StringComparison.OrdinalIgnoreCase);
                bool isManifest = name.EndsWith(".manifest", StringComparison.OrdinalIgnoreCase);
                if (!isLua && !isManifest) continue;

                string dest = isLua
                    ? Path.Combine(plugDir, $"{appId}.lua")
                    : Path.Combine(depotDir, name);

                if (isManifest && File.Exists(dest))
                {
                    manifestCount++;
                    continue;
                }

                try
                {
                    if (isLua)
                    {
                        string tmp = Path.Combine(Path.GetTempPath(), $"luatools_{Guid.NewGuid():N}.lua");
                        try
                        {
                            entry.ExtractToFile(tmp, overwrite: true);
                            string? buildId = BuildIdFromFileName(name);
                            WriteLua(tmp, dest, KeepPinsFor(buildId, forceLocked));
                            CaptureInstalled(appId, dest, buildId, source);
                            luaInstalled = true;
                            RecordLoaded(appId);
                        }
                        finally { try { File.Delete(tmp); } catch { /* best effort */ } }
                    }
                    else
                    {
                        entry.ExtractToFile(dest, overwrite: true);
                        StampNow(dest);
                        manifestCount++;
                    }
                }
                catch
                {
                    failed.Add(name);
                }
            }
        }

        return new InstallResult(luaInstalled, manifestCount, failed, null);
    }

    public static IEnumerable<(long AppId, string Path)> EnumerateInstalled(string? stPlugInDir)
    {
        if (stPlugInDir is null || !Directory.Exists(stPlugInDir)) yield break;
        foreach (string path in Directory.EnumerateFiles(stPlugInDir, "*.lua"))
            if (long.TryParse(Path.GetFileNameWithoutExtension(path), out long appId))
                yield return (appId, path);
    }

    public static bool IsInstallable(string path)
    {
        string ext = Path.GetExtension(path);
        return ext.Equals(".lua", StringComparison.OrdinalIgnoreCase)
            || ext.Equals(".manifest", StringComparison.OrdinalIgnoreCase)
            || ext.Equals(".zip", StringComparison.OrdinalIgnoreCase);
    }

    public static long? AppIdFromFileName(string path)
    {
        string stem = Path.GetFileNameWithoutExtension(path);
        var m = Regex.Match(stem, @"^\s*(\d+)");
        return m.Success && long.TryParse(m.Groups[1].Value, out long id) ? id : null;
    }

    public static long? AppIdForZip(string zipPath)
    {
        if (AppIdFromFileName(zipPath) is { } fromName) return fromName;
        try
        {
            using var archive = ZipFile.OpenRead(zipPath);
            foreach (var entry in archive.Entries)
                if (entry.Name.EndsWith(".lua", StringComparison.OrdinalIgnoreCase) &&
                    AppIdFromFileName(entry.Name) is { } id)
                    return id;
        }
        catch { /* unreadable zip */ }
        return null;
    }

    private static void StampNow(string path)
    {
        try { File.SetLastWriteTime(path, DateTime.Now); } catch { /* cosmetic */ }
    }
}

/// <summary>Pulls a Steam appid out of a link (drag-drop / paste install).</summary>
public static partial class SteamLinkParser
{
    private const long MaxAppId = 2_000_000_000;

    [GeneratedRegex(@"steamdb\.info/app/(\d+)", RegexOptions.IgnoreCase)]
    private static partial Regex SteamDbRegex();

    [GeneratedRegex(@"store\.steampowered\.com/(?:agecheck/)?app/(\d+)", RegexOptions.IgnoreCase)]
    private static partial Regex StoreRegex();

    [GeneratedRegex(@"steamcommunity\.com/app/(\d+)", RegexOptions.IgnoreCase)]
    private static partial Regex CommunityRegex();

    [GeneratedRegex(@"steam://(?:store|install|run|rungameid)/(\d+)", RegexOptions.IgnoreCase)]
    private static partial Regex SteamProtocolRegex();

    public static long? AppIdFrom(string? text)
    {
        if (string.IsNullOrWhiteSpace(text)) return null;

        foreach (var regex in new[] { SteamDbRegex(), StoreRegex(), CommunityRegex(), SteamProtocolRegex() })
        {
            var match = regex.Match(text);
            if (match.Success && long.TryParse(match.Groups[1].Value, out long appId)
                              && appId > 0 && appId < MaxAppId)
                return appId;
        }
        return null;
    }
}

public static class ModeMigration
{
    public static bool Apply(SettingsService settings)
    {
        var (newMode, resetOnboarding) = Migrate(settings.SelectedMode);
        if (newMode != settings.SelectedMode) settings.SelectedMode = newMode;
        return resetOnboarding;
    }

    public static (string? Mode, bool ResetOnboarding) Migrate(string? stored)
    {
        if (string.IsNullOrWhiteSpace(stored)) return (null, false);
        if (Enum.TryParse<UnlockerMode>(stored, out var current))
            return (current.ToString(), false);

        return stored switch
        {
            "OpenSteamTools" or "OpenSteamToolsNightly" => (UnlockerMode.Bst.ToString(), false),
            _ => (null, true),
        };
    }
}
