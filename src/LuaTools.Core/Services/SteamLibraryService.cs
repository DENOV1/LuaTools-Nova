// Derived from madoiscool/LuaTools (MIT) — SteamLibraryService.cs, plus the
// config/libraryfolders.vdf union rule verified in the Moon repo.
using System.Text.RegularExpressions;

namespace LuaTools.Core.Services;

/// <summary>Resolves where a Steam game is installed by walking Valve's KeyValues files.</summary>
public sealed partial class SteamLibraryService(ISteamLocator locator)
{
    [GeneratedRegex(@"""path""\s*""([^""]+)""", RegexOptions.IgnoreCase)]
    private static partial Regex PathRegex();

    [GeneratedRegex(@"""installdir""\s*""([^""]+)""", RegexOptions.IgnoreCase)]
    private static partial Regex InstallDirRegex();

    [GeneratedRegex(@"""appid""\s*""(\d+)""", RegexOptions.IgnoreCase)]
    private static partial Regex AppIdRegex();

    [GeneratedRegex(@"""name""\s*""([^""]*)""", RegexOptions.IgnoreCase)]
    private static partial Regex NameRegex();

    public sealed record InstalledGame(long AppId, string Name, string InstallDir);

    public string? GetInstallDir(long appId)
    {
        try
        {
            string? steamRoot = locator.EffectivePath;
            if (steamRoot is null) return null;

            foreach (string library in GetLibraryRoots(steamRoot))
            {
                string acf = Path.Combine(library, "steamapps", $"appmanifest_{appId}.acf");
                if (!File.Exists(acf)) continue;

                var m = InstallDirRegex().Match(File.ReadAllText(acf));
                if (!m.Success) continue;

                string installDir = Unescape(m.Groups[1].Value);
                string full = Path.Combine(library, "steamapps", "common", installDir);
                if (Directory.Exists(full)) return full;
            }
        }
        catch { /* unreadable VDF/ACF or odd path */ }
        return null;
    }

    public IEnumerable<InstalledGame> EnumerateInstalled()
    {
        string? steamRoot = locator.EffectivePath;
        if (steamRoot is null) yield break;

        foreach (string library in GetLibraryRoots(steamRoot))
        {
            string steamapps = Path.Combine(library, "steamapps");
            string[] acfs;
            try
            {
                if (!Directory.Exists(steamapps)) continue;
                acfs = Directory.GetFiles(steamapps, "appmanifest_*.acf");
            }
            catch { continue; }

            foreach (string acf in acfs)
            {
                InstalledGame? game = null;
                try
                {
                    string text = File.ReadAllText(acf);
                    var idm = AppIdRegex().Match(text);
                    var dirm = InstallDirRegex().Match(text);
                    if (!idm.Success || !dirm.Success) continue;
                    if (!long.TryParse(idm.Groups[1].Value, out long appId)) continue;

                    string full = Path.Combine(steamapps, "common", Unescape(dirm.Groups[1].Value));
                    if (!Directory.Exists(full)) continue;

                    var nm = NameRegex().Match(text);
                    game = new InstalledGame(appId, nm.Success ? nm.Groups[1].Value : appId.ToString(), full);
                }
                catch { /* skip this one, not the whole library */ }

                if (game is not null) yield return game;
            }
        }
    }

    /// <summary>Every Steam library root. The main install first; libraryfolders.vdf is read from BOTH
    /// config/ and steamapps/ (the union: some installs keep only one, and drift between them has
    /// historically made games report "not installed").</summary>
    public static IEnumerable<string> GetLibraryRoots(string steamRoot)
    {
        yield return steamRoot;

        var seen = new HashSet<string>(StringComparer.Ordinal) { steamRoot };
        foreach (string vdf in new[]
                 {
                     Path.Combine(steamRoot, "config", "libraryfolders.vdf"),
                     Path.Combine(steamRoot, "steamapps", "libraryfolders.vdf"),
                 })
        {
            if (!File.Exists(vdf)) continue;
            string text;
            try { text = File.ReadAllText(vdf); } catch { continue; }

            foreach (Match m in PathRegex().Matches(text))
            {
                string path = Unescape(m.Groups[1].Value);
                if (string.IsNullOrWhiteSpace(path)) continue;
                if (seen.Add(path) && Directory.Exists(path)) yield return path;
            }
        }
    }

    private static string Unescape(string s) => s.Replace(@"\\", Path.DirectorySeparatorChar == '\\' ? "\\" : "/");
}
