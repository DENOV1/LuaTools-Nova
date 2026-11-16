// LuaTools Nova — Proton compatibility detection + WINEDLLOVERRIDES management.
// Behavior knowledge verified in the Moon repo (compat mapping in config.vdf; SteamLinuxRuntime
// is NOT Proton; override must precede %command% to be read as env). Implementation is original.
using System.Text.RegularExpressions;
using LuaTools.Core.Services;

namespace LuaTools.Linux;

public static class ProtonCompat
{
    private static readonly HashSet<string> NativeRuntimes = new(StringComparer.OrdinalIgnoreCase)
    {
        "steamlinuxruntime", "steamlinuxruntime_scout", "steamlinuxruntime_soldier",
        "steamlinuxruntime_sniper", "steamlinuxruntime_1.0",
    };

    /// <summary>True when a compat tool name is a Proton/Wine tool (not native / not a Linux runtime).</summary>
    public static bool IsProtonName(string? name)
    {
        if (string.IsNullOrWhiteSpace(name)) return false;
        string trimmed = name.Trim();
        if (trimmed.Length == 0) return false;
        return !NativeRuntimes.Contains(trimmed);
    }

    /// <summary>The forced compat tool for an appid from config.vdf's CompatToolMapping, or null.</summary>
    public static string? ToolForApp(string raw, long appid)
    {
        if (string.IsNullOrWhiteSpace(raw)) return null;
        string id = appid.ToString();

        var mapping = SectionBlock(raw, "CompatToolMapping");
        if (mapping is null) return null;

        var pat = new Regex("\"" + Regex.Escape(id) + "\"\\s*\\{");
        int from = 0;
        while (true)
        {
            var a = pat.Match(mapping, from);
            if (!a.Success) return null;
            int brace = a.Index + a.Length - 1;
            string? inner = BlockAfter(mapping, brace);
            if (inner is not null)
            {
                var name = Regex.Match(inner, "\"[Nn]ame\"\\s*\"([^\"]*)\"");
                return name.Success ? name.Groups[1].Value : "";
            }
            from = brace + 1;
        }
    }

    /// <summary>Conservative: unreadable config → not forced.</summary>
    public static bool IsProtonForced(ISteamLocator locator, long appid)
    {
        try
        {
            if (locator.EffectivePath is not { } root) return false;
            string path = Path.Combine(root, "config", "config.vdf");
            if (!File.Exists(path)) return false;
            string raw = File.ReadAllText(path);
            var name = ToolForApp(raw, appid);
            if (name is null) return false;
            return IsProtonName(name);
        }
        catch { return false; }
    }

    internal static string? SectionBlock(string s, string key)
    {
        int kpos = s.IndexOf("\"" + key + "\"", StringComparison.Ordinal);
        if (kpos < 0) return null;
        int open = s.IndexOf('{', kpos);
        if (open < 0) return null;
        return BlockAfter(s, open);
    }

    internal static string? BlockAfter(string s, int openPos)
    {
        int depth = 0;
        int i = openPos;
        int start = openPos + 1;
        while (i < s.Length)
        {
            char c = s[i];
            if (c == '"')
            {
                i++;
                while (i < s.Length && s[i] != '"') i++;
            }
            else if (c == '{') depth++;
            else if (c == '}')
            {
                depth--;
                if (depth == 0) return s[start..i];
            }
            i++;
        }
        return null;
    }
}

public static class WineOverrides
{
    // Wine builtins a fix's proxy DLL may collide with (conservative allowlist).
    private static readonly HashSet<string> SystemProxy = new(StringComparer.Ordinal)
    {
        "winmm", "winhttp", "version", "dxgi", "dinput8", "dinput", "dsound", "ddraw",
        "d3d8", "d3d9", "d3d10", "d3d10_1", "d3d10core", "d3d11", "d3d12", "d3d12core",
        "wininet", "wintrust", "dbghelp", "crypt32", "iphlpapi", "opengl32", "ws2_32",
        "xinput1_1", "xinput1_2", "xinput1_3", "xinput1_4", "xinput9_1_0", "xinputuap",
    };

    /// <summary>WINEDLLOVERRIDES fragment for DLL basenames colliding with a Wine builtin, or null.</summary>
    public static string? BuildOverrides(IEnumerable<string> dllNames)
    {
        var seen = new HashSet<string>(StringComparer.Ordinal);
        var order = new List<string>();
        foreach (string name in dllNames)
        {
            var stem = Regex.Match(name, "^(.+?)\\.[Dd][Ll][Ll]$").Groups[1].Value;
            if (stem.Length == 0) continue;
            string key = stem.ToLowerInvariant();
            if (!IsSystemProxy(key) || !seen.Add(key)) continue;
            order.Add(key);
        }
        if (order.Count == 0) return null;
        return "WINEDLLOVERRIDES=\"" + string.Join(";", order.Select(k => k + "=n,b")) + "\"";
    }

    private static bool IsSystemProxy(string stemLower) =>
        SystemProxy.Contains(stemLower)
        || Regex.IsMatch(stemLower, @"^d3dcompiler_\d+$")
        || Regex.IsMatch(stemLower, @"^d3dx9_\d+$")
        || Regex.IsMatch(stemLower, @"^d3dx10_\d+$")
        || Regex.IsMatch(stemLower, @"^d3dx11_\d+$");

    /// <summary>Splice the override into the user's launch options idempotently: override FIRST
    /// (leading VAR= tokens are the only ones Steam reads as env), single %command% preserved.</summary>
    public static string MergeLaunchOptions(string current, string overrides)
    {
        current ??= "";
        string rest = StripExistingOverride(current);

        int first = rest.IndexOf("%command%", StringComparison.Ordinal);
        if (first >= 0)
        {
            string head = rest[..first];
            string tail = rest[(first + 9)..].Replace("%command%", "");
            rest = Normalize(head + "%command%" + tail);
        }

        if (rest.Length == 0) return Normalize(overrides + " %command%");
        if (rest.Contains("%command%")) return Normalize(overrides + " " + rest);
        return Normalize(overrides + " " + rest + " %command%");
    }

    /// <summary>Remove any WINEDLLOVERRIDES assignment; a bare %command% clears fully.</summary>
    public static string RemoveOverrides(string current)
    {
        current ??= "";
        string rest = StripExistingOverride(current);

        int first = rest.IndexOf("%command%", StringComparison.Ordinal);
        if (first >= 0)
        {
            string head = rest[..first];
            string tail = rest[(first + 9)..].Replace("%command%", "");
            rest = Normalize(head + "%command%" + tail);
        }

        if (rest.Length == 0 || rest == "%command%") return "";
        return rest;
    }

    private static string StripExistingOverride(string s)
    {
        s = Regex.Replace(s, "WINEDLLOVERRIDES=\".-\"\\s*", "");
        s = Regex.Replace(s, "WINEDLLOVERRIDES=[^\\s]+\\s*", "");
        return Normalize(s);
    }

    private static string Normalize(string s) =>
        Regex.Replace(s.Trim(), "\\s+", " ").Trim();
}
