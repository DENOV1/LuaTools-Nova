// LuaTools Nova — Linux platform implementations.
// Steam discovery follows the verified Linux layout: ~/.steam/steam is a SYMLINK Valve's
// bootstrap creates on first launch (target varies: ~/.local/share/Steam on Fedora/Arch,
// ~/.steam/debian-installation on Debian/Ubuntu). Flatpak/Snap Steam are detected and reported
// but the native installation is required. Client language lives in ~/.steam/registry.vdf.
using System.Text.RegularExpressions;
using LuaTools.Core.Services;

namespace LuaTools.Linux;

public sealed partial class SteamPathLocator : ISteamLocator
{
    private string? _overridePath;
    private string? _detected;
    private string _source = "";

    public SteamPathLocator(string? overridePath)
    {
        _overridePath = string.IsNullOrWhiteSpace(overridePath) ? null : overridePath;
    }

    /// <summary>Called by the settings VM when the user changes the override at runtime.</summary>
    public void SetOverride(string? overridePath)
    {
        _overridePath = string.IsNullOrWhiteSpace(overridePath) ? null : overridePath;
        _detected = null;
        _source = "";
    }

    [GeneratedRegex(@"""[Ll]anguage""\s*""([^""]+)""")]
    private static partial Regex LanguageRegex();

    public string? AutoDetectedPath
    {
        get { Detect(); return _detected; }
    }

    public string DetectionSource => _source;

    public string? EffectivePath
    {
        get
        {
            if (_overridePath is not null) return Normalize(_overridePath);
            return AutoDetectedPath;
        }
    }

    public bool IsOverridden => _overridePath is not null;

    public bool IsValid => EffectivePath is { } p && File.Exists(ClientExecutableFor(p));

    public string ClientExecutableFor(string steamPath) => Path.Combine(steamPath, "steam.sh");

    public string? SteamLanguage
    {
        get
        {
            try
            {
                string home = Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);
                foreach (string candidate in new[]
                         {
                             Path.Combine(home, ".steam", "registry.vdf"),
                             Path.Combine(home, ".steam", "steam", "registry.vdf"),
                         })
                {
                    if (!File.Exists(candidate)) continue;
                    var m = LanguageRegex().Match(File.ReadAllText(candidate));
                    if (m.Success && m.Groups[1].Value.Length > 0)
                        return m.Groups[1].Value.Trim().ToLowerInvariant();
                }
            }
            catch { /* unreadable → null */ }
            return null;
        }
    }

    /// <summary>Bootstrap invariant: ~/.steam/steam must be a symlink resolving to a real Steam root
    /// (contains steam.sh). Nova never creates that path — a real directory there blocks Valve's
    /// bootstrap ("Couldn't set up Steam data").</summary>
    private void Detect()
    {
        if (_detected is not null) return;

        string home = Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);

        string link = Path.Combine(home, ".steam", "steam");
        string? root = ResolveSafe(link);
        if (root is not null && File.Exists(Path.Combine(root, "steam.sh")))
        {
            _detected = Normalize(root);
            _source = "~/.steam/steam";
            return;
        }

        foreach (var (candidate, source) in new[]
                 {
                     (Path.Combine(home, ".local", "share", "Steam"), "~/.local/share/Steam"),
                     (Path.Combine(home, ".steam", "debian-installation"), "~/.steam/debian-installation"),
                     (Path.Combine(home, ".steam", "root"), "~/.steam/root"),
                 })
        {
            if (Directory.Exists(candidate) && File.Exists(Path.Combine(candidate, "steam.sh")))
            {
                _detected = Normalize(candidate);
                _source = source;
                return;
            }
        }

        string flatpak = Path.Combine(home, ".var", "app", "com.valvesoftware.Steam", "data", "Steam");
        if (Directory.Exists(flatpak) && File.Exists(Path.Combine(flatpak, "steam.sh")))
        {
            _detected = Normalize(flatpak);
            _source = "flatpak (limited support)";
            return;
        }

        string snap = Path.Combine(home, "snap", "steam", "common", ".steam", "steam");
        if (Directory.Exists(snap) && File.Exists(Path.Combine(snap, "steam.sh")))
        {
            _detected = Normalize(snap);
            _source = "snap (limited support)";
            return;
        }

        _source = "";
    }

    private static string? ResolveSafe(string path)
    {
        try
        {
            if (!Directory.Exists(path) && !File.Exists(path)) return null;
            return Path.GetFullPath(path);
        }
        catch { return null; }
    }

    private static string Normalize(string path)
    {
        try { return Path.GetFullPath(path.Trim()); }
        catch { return path.Trim(); }
    }
}
