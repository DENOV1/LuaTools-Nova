// Derived from madoiscool/LuaTools (MIT) — LuaEditor.cs.
// Toggles individual depot lines in a lua's TEXT via comment in/out.
using System.Text.RegularExpressions;

namespace LuaTools.Core.Lua;

public static class LuaEditor
{
    private const string CommentPrefix = @"^(?<indent>\s*)(?<comment>--\s*)?";

    private static Regex PinLine(long depotId) =>
        new(CommentPrefix + @"(?<body>setManifestid\s*\(\s*" + depotId + @"\s*[,)])", RegexOptions.IgnoreCase);

    private static Regex AddAppIdLine(long depotId) =>
        new(CommentPrefix + @"(?<body>addappid\s*\(\s*" + depotId + @"\s*[,)])", RegexOptions.IgnoreCase);

    /// <summary>Lock (pin) or unlock a depot by commenting its setManifestid line in/out.</summary>
    public static string SetDepotLocked(string lua, long depotId, bool locked) =>
        Rewrite(lua, PinLine(depotId), active: locked);

    /// <summary>Enable or disable a depot by commenting its addappid line in/out.</summary>
    public static string SetDepotEnabled(string lua, long depotId, bool enabled) =>
        Rewrite(lua, AddAppIdLine(depotId), active: enabled);

    private static string Rewrite(string lua, Regex line, bool active)
    {
        var lines = lua.Split('\n');
        for (int i = 0; i < lines.Length; i++)
        {
            var m = line.Match(lines[i]);
            if (!m.Success) continue;

            bool isCommented = m.Groups["comment"].Success;
            if (isCommented == !active) continue;

            lines[i] = active
                ? m.Groups["indent"].Value + lines[i][(m.Groups["comment"].Index + m.Groups["comment"].Length)..]
                : m.Groups["indent"].Value + "--" + lines[i][m.Groups["indent"].Length..];
        }
        return string.Join('\n', lines);
    }
}
