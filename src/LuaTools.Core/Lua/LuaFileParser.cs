// Derived from madoiscool/LuaTools (MIT) — LuaFileParser.cs, adapted for LuaTools Nova.
// Reads the addappid()/setManifestid() grammar of stplug-in lua files.
using System.Text.RegularExpressions;

namespace LuaTools.Core.Lua;

/// <summary>One depot/app entry declared in a lua file via addappid / setManifestid.</summary>
public sealed record LuaEntry(
    long Id, string? Key, string? ManifestId, string? CommentedManifestId, string? Comment)
{
    public bool HasKey => Key is not null;
    public long? SizeOnDisk { get; init; }
}

/// <summary>Parsed contents of a stplug-in lua file.</summary>
public sealed record LuaContents(
    long BaseAppId,
    IReadOnlyList<LuaEntry> Entries,
    IReadOnlyDictionary<long, string> ActivePins,
    IReadOnlyDictionary<long, string> CommentedPins,
    IReadOnlyList<LuaEntry> DisabledEntries)
{
    public int DepotCount => Entries.Count(e => e.HasKey);
    public int DlcCount => Entries.Count(e => !e.HasKey && e.Id != BaseAppId);
    public bool HasActivePins => ActivePins.Count > 0;
}

/// <summary>Difference between an old and new lua: ids the new one adds and ids it removes.</summary>
public sealed record LuaDiff(IReadOnlyList<LuaEntry> Added, IReadOnlyList<LuaEntry> Removed)
{
    public bool HasChanges => Added.Count > 0 || Removed.Count > 0;
}

public static partial class LuaFileParser
{
    [GeneratedRegex(@"addappid\s*\(\s*(\d+)\s*(?:,\s*\d+\s*(?:,\s*""([^""]*)"")?)?\s*\)[ \t]*(?:--[ \t]*(.*?)[ \t]*)?$",
        RegexOptions.IgnoreCase)]
    private static partial Regex AddAppIdRegex();

    [GeneratedRegex(@"setManifestid\s*\(\s*(\d+)\s*,\s*""(\d+)""\s*(?:,\s*(\d+))?", RegexOptions.IgnoreCase)]
    private static partial Regex SetManifestRegex();

    [GeneratedRegex(@"\s*\(\d+\)\s*\S*\s*$")]
    private static partial Regex CommentTailRegex();

    private static string? CleanComment(string? raw)
    {
        if (string.IsNullOrWhiteSpace(raw)) return null;
        string s = raw.Trim();
        if (s.Contains("setManifestid", StringComparison.OrdinalIgnoreCase) ||
            s.Contains("addappid", StringComparison.OrdinalIgnoreCase) ||
            s.Contains("addtoken", StringComparison.OrdinalIgnoreCase))
            return null;
        s = CommentTailRegex().Replace(s, "").Trim();
        return string.IsNullOrWhiteSpace(s) ? null : s;
    }

    public static LuaContents? Parse(string filePath, long appIdFromName)
    {
        try
        {
            return ParseText(File.ReadAllText(filePath), appIdFromName);
        }
        catch
        {
            return null;
        }
    }

    public static LuaContents ParseText(string text, long appIdFromName)
    {
        var order = new List<long>();
        var keyById = new Dictionary<long, string?>();
        var commentById = new Dictionary<long, string>();
        var manifests = new Dictionary<long, string>();
        var sizes = new Dictionary<long, long>();
        var commentedManifests = new Dictionary<long, string>();
        var disabledOrder = new List<long>();
        var disabledKeyById = new Dictionary<long, string?>();

        foreach (string rawLine in text.Split('\n'))
        {
            string line = rawLine.Trim();
            bool commented = line.StartsWith("--");

            var pin = SetManifestRegex().Match(line);
            if (pin.Success && long.TryParse(pin.Groups[1].Value, out long depot))
            {
                (commented ? commentedManifests : manifests)[depot] = pin.Groups[2].Value;
                if (pin.Groups[3].Success && long.TryParse(pin.Groups[3].Value, out long sz) && sz > 0)
                    sizes.TryAdd(depot, sz);
            }

            var m = AddAppIdRegex().Match(commented ? line.TrimStart('-', ' ') : line);
            if (!m.Success || !long.TryParse(m.Groups[1].Value, out long id)) continue;

            string? key = m.Groups[2].Success && !string.IsNullOrEmpty(m.Groups[2].Value)
                ? m.Groups[2].Value : null;

            string? comment = CleanComment(m.Groups[3].Success ? m.Groups[3].Value : null);
            if (comment is not null &&
                (!commentById.TryGetValue(id, out var prev) || comment.Length > prev.Length))
                commentById[id] = comment;

            if (commented)
            {
                if (disabledKeyById.TryGetValue(id, out var hadKey)) disabledKeyById[id] = hadKey ?? key;
                else { disabledKeyById[id] = key; disabledOrder.Add(id); }
                continue;
            }
            if (keyById.TryGetValue(id, out var existing))
                keyById[id] = existing ?? key;
            else { keyById[id] = key; order.Add(id); }
        }

        var entries = order
            .Select(id => new LuaEntry(id, keyById[id],
                manifests.TryGetValue(id, out var mid) ? mid : null,
                commentedManifests.TryGetValue(id, out var cmid) ? cmid : null,
                commentById.TryGetValue(id, out var c) ? c : null)
            { SizeOnDisk = sizes.TryGetValue(id, out long sz) ? sz : null })
            .ToList();

        var disabled = disabledOrder
            .Where(id => !keyById.ContainsKey(id))
            .Select(id => new LuaEntry(id, disabledKeyById[id],
                manifests.TryGetValue(id, out var dmid) ? dmid : null,
                commentedManifests.TryGetValue(id, out var dcmid) ? dcmid : null,
                commentById.TryGetValue(id, out var dc) ? dc : null)
            { SizeOnDisk = sizes.TryGetValue(id, out long dsz) ? dsz : null })
            .ToList();

        long baseId = entries.Count > 0 ? entries[0].Id : appIdFromName;
        return new LuaContents(baseId, entries, manifests, commentedManifests, disabled);
    }

    public static LuaDiff Diff(LuaContents? oldLua, LuaContents newLua)
    {
        var oldIds = oldLua?.Entries.Select(e => e.Id).ToHashSet() ?? new HashSet<long>();
        var newIds = newLua.Entries.Select(e => e.Id).ToHashSet();
        var added = newLua.Entries.Where(e => !oldIds.Contains(e.Id)).ToList();
        var removed = oldLua?.Entries.Where(e => !newIds.Contains(e.Id)).ToList() ?? new List<LuaEntry>();
        return new LuaDiff(added, removed);
    }
}
