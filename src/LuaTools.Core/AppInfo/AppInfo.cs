// Derived from madoiscool/LuaTools (MIT) — Services/AppInfo/BinaryVdf.cs + AppInfoFile.cs + LaunchOption.cs.
using System.Security.Cryptography;
using System.Text;

namespace LuaTools.Core.AppInfo;

public enum VdfType : byte
{
    Table = 0x00, String = 0x01, Int32 = 0x02, Float = 0x03, Pointer = 0x04,
    WString = 0x05, Color = 0x06, UInt64 = 0x07, End = 0x08, Int64 = 0x0A, EndAlt = 0x0B,
}

public sealed class VdfProperty(string name, VdfType type, object value)
{
    public string Name { get; set; } = name;
    public VdfType Type { get; } = type;
    public object Value { get; set; } = value;

    public VdfTable? AsTable() => Value as VdfTable;
    public string AsText() => Value is byte[] b ? Encoding.UTF8.GetString(b) : Value?.ToString() ?? "";

    public static VdfProperty Text(string name, string value) => new(name, VdfType.String, Encoding.UTF8.GetBytes(value));
    public static VdfProperty Table(string name, VdfTable value) => new(name, VdfType.Table, value);
}

/// <summary>An ORDERED list of properties — the format permits the same key twice inside one object.</summary>
public sealed class VdfTable
{
    public List<VdfProperty> Items { get; } = new();

    public VdfProperty? Find(string name) =>
        Items.FirstOrDefault(p => string.Equals(p.Name, name, StringComparison.OrdinalIgnoreCase));

    public VdfTable? GetTable(string name) => Find(name)?.AsTable();
    public string? GetText(string name) => Find(name)?.AsText();

    public VdfTable EnsureTable(string name)
    {
        if (GetTable(name) is { } existing) return existing;
        var table = new VdfTable();
        Items.Add(VdfProperty.Table(name, table));
        return table;
    }

    public void SetText(string name, string? value)
    {
        if (string.IsNullOrEmpty(value)) { Remove(name); return; }
        if (Find(name) is { } existing) existing.Value = Encoding.UTF8.GetBytes(value);
        else Items.Add(VdfProperty.Text(name, value));
    }

    public void Remove(string name)
    {
        for (int i = Items.Count - 1; i >= 0; i--)
            if (string.Equals(Items[i].Name, name, StringComparison.OrdinalIgnoreCase))
                Items.RemoveAt(i);
    }

    public VdfTable Clone()
    {
        var copy = new VdfTable();
        foreach (var p in Items)
        {
            object value = p.Value switch
            {
                VdfTable t => t.Clone(),
                byte[] b => b.ToArray(),
                var v => v,
            };
            copy.Items.Add(new VdfProperty(p.Name, p.Type, value));
        }
        return copy;
    }
}

public static class BinaryVdf
{
    public static VdfTable Read(Stream s, IReadOnlyList<string> strings, bool indexedKeys)
    {
        var table = new VdfTable();
        while (true)
        {
            int marker = s.ReadByte();
            if (marker < 0 || marker == (int)VdfType.End || marker == (int)VdfType.EndAlt) return table;

            var type = (VdfType)marker;
            string name = indexedKeys ? strings[(int)ReadUInt32(s)] : ReadCString(s);

            object value = type switch
            {
                VdfType.Table => Read(s, strings, indexedKeys),
                VdfType.String => ReadCStringBytes(s),
                VdfType.Int32 or VdfType.Pointer or VdfType.Color => ReadInt32(s),
                VdfType.UInt64 => ReadUInt64(s),
                VdfType.Int64 => ReadInt64(s),
                VdfType.Float => BitConverter.ToSingle(ReadExactly(s, 4)),
                _ => throw new InvalidDataException($"Unsupported VDF value type 0x{marker:X2}"),
            };
            table.Items.Add(new VdfProperty(name, type, value));
        }
    }

    public static void Write(Stream s, VdfTable table, Func<string, uint> intern, bool indexedKeys)
    {
        foreach (var p in table.Items)
        {
            s.WriteByte((byte)p.Type);
            if (indexedKeys) WriteUInt32(s, intern(p.Name));
            else WriteCString(s, Encoding.UTF8.GetBytes(p.Name));

            switch (p.Type)
            {
                case VdfType.Table:
                    Write(s, (VdfTable)p.Value, intern, indexedKeys);
                    break;
                case VdfType.String:
                    WriteCString(s, (byte[])p.Value);
                    break;
                case VdfType.Int32 or VdfType.Pointer or VdfType.Color:
                    WriteInt32(s, (int)p.Value);
                    break;
                case VdfType.UInt64:
                    s.Write(BitConverter.GetBytes((ulong)p.Value));
                    break;
                case VdfType.Int64:
                    s.Write(BitConverter.GetBytes((long)p.Value));
                    break;
                case VdfType.Float:
                    s.Write(BitConverter.GetBytes((float)p.Value));
                    break;
                default:
                    throw new InvalidDataException($"Can't serialize VDF type {p.Type}");
            }
        }
        s.WriteByte((byte)VdfType.End);
    }

    public static byte[] ReadExactly(Stream s, int count)
    {
        var buffer = new byte[count];
        s.ReadExactly(buffer);
        return buffer;
    }

    public static uint ReadUInt32(Stream s) => BitConverter.ToUInt32(ReadExactly(s, 4));
    public static int ReadInt32(Stream s) => BitConverter.ToInt32(ReadExactly(s, 4));
    public static ulong ReadUInt64(Stream s) => BitConverter.ToUInt64(ReadExactly(s, 8));
    public static long ReadInt64(Stream s) => BitConverter.ToInt64(ReadExactly(s, 8));

    public static void WriteUInt32(Stream s, uint v) => s.Write(BitConverter.GetBytes(v));
    public static void WriteInt32(Stream s, int v) => s.Write(BitConverter.GetBytes(v));

    public static byte[] ReadCStringBytes(Stream s)
    {
        var buffer = new MemoryStream();
        int b;
        while ((b = s.ReadByte()) > 0) buffer.WriteByte((byte)b);
        return buffer.ToArray();
    }

    public static string ReadCString(Stream s) => Encoding.UTF8.GetString(ReadCStringBytes(s));

    public static void WriteCString(Stream s, byte[] raw)
    {
        s.Write(raw);
        s.WriteByte(0);
    }
}

public class AppInfoFormatException(string message) : Exception(message);

public sealed record AppInfoEntry(int AppId, long Offset, int Size, uint ChangeNumber);

/// <summary>Reader/writer for Steam's appcache/appinfo.vdf (v27/28/29). The Linux client uses the same format.</summary>
public sealed class AppInfoFile : IDisposable
{
    public const uint Magic27 = 0x07564427;
    public const uint Magic28 = 0x07564428;
    public const uint Magic29 = 0x07564429;

    private const int Sha1TextOffset = 16;
    private const int ChangeNumberOffset = 36;
    private const int Sha1BinaryOffset = 40;

    private readonly FileStream _file;
    private readonly Dictionary<int, AppInfoEntry> _index = new();

    public string Path { get; }
    public uint MagicNumber { get; }
    public uint Universe { get; }
    public List<string> Strings { get; } = new();
    private readonly List<byte[]> _stringBytes = new();

    public bool IndexedKeys => MagicNumber >= Magic29;
    public int MetaLength => MagicNumber >= Magic28 ? 60 : 40;
    private readonly long _appsStart;

    public IReadOnlyDictionary<int, AppInfoEntry> Entries => _index;

    public AppInfoFile(string path)
    {
        Path = path;
        _file = File.Open(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite);

        MagicNumber = BinaryVdf.ReadUInt32(_file);
        Universe = BinaryVdf.ReadUInt32(_file);
        if (MagicNumber is not (Magic27 or Magic28 or Magic29))
            throw new AppInfoFormatException($"Unknown appinfo version 0x{MagicNumber:X8}.");

        if (IndexedKeys)
        {
            long tableOffset = BinaryVdf.ReadInt64(_file);
            long resume = _file.Position;
            ReadStringTable(tableOffset);
            _file.Position = resume;
        }

        _appsStart = _file.Position;
        BuildIndex();
    }

    private void ReadStringTable(long offset)
    {
        _file.Position = offset;
        uint count = BinaryVdf.ReadUInt32(_file);
        for (uint i = 0; i < count; i++)
        {
            byte[] raw = BinaryVdf.ReadCStringBytes(_file);
            _stringBytes.Add(raw);
            Strings.Add(Encoding.UTF8.GetString(raw));
        }
    }

    private void BuildIndex()
    {
        _file.Position = _appsStart;
        while (true)
        {
            long offset = _file.Position;
            var header = new byte[8];
            if (_file.Read(header, 0, 8) < 8) return;

            int appId = BitConverter.ToInt32(header, 0);
            if (appId == 0) return;
            int size = BitConverter.ToInt32(header, 4);

            var meta = BinaryVdf.ReadExactly(_file, MetaLength);
            uint changeNumber = BitConverter.ToUInt32(meta, ChangeNumberOffset);

            _index[appId] = new AppInfoEntry(appId, offset, size, changeNumber);
            _file.Position = offset + 8 + size;
        }
    }

    public VdfTable? ReadApp(int appId)
    {
        if (!_index.TryGetValue(appId, out var entry)) return null;
        _file.Position = entry.Offset + 8 + MetaLength;
        return BinaryVdf.Read(_file, Strings, IndexedKeys);
    }

    public VdfTable? ReadAppBody(int appId) => ReadApp(appId)?.GetTable("appinfo");

    public void SaveAs(string destination, IReadOnlyDictionary<int, VdfTable> edits)
    {
        var strings = new List<byte[]>(_stringBytes);
        var lookup = new Dictionary<string, uint>(strings.Count, StringComparer.Ordinal);
        for (int i = 0; i < Strings.Count; i++) lookup.TryAdd(Strings[i], (uint)i);

        uint Intern(string key)
        {
            if (lookup.TryGetValue(key, out uint index)) return index;
            index = (uint)strings.Count;
            strings.Add(Encoding.UTF8.GetBytes(key));
            lookup[key] = index;
            return index;
        }

        using var output = File.Open(destination, FileMode.Create, FileAccess.Write, FileShare.None);
        BinaryVdf.WriteUInt32(output, MagicNumber);
        BinaryVdf.WriteUInt32(output, Universe);

        long tableOffsetPos = output.Position;
        if (IndexedKeys) output.Write(BitConverter.GetBytes(0L));

        foreach (var entry in _index.Values.OrderBy(e => e.Offset))
        {
            _file.Position = entry.Offset + 8;
            var meta = BinaryVdf.ReadExactly(_file, MetaLength);

            if (edits.TryGetValue(entry.AppId, out var replacement))
            {
                using var blob = new MemoryStream();
                BinaryVdf.Write(blob, replacement, Intern, IndexedKeys);
                byte[] bytes = blob.ToArray();

                if (MagicNumber >= Magic28)
                    SHA1.HashData(bytes).CopyTo(meta, Sha1BinaryOffset);

                BinaryVdf.WriteInt32(output, entry.AppId);
                BinaryVdf.WriteInt32(output, meta.Length + bytes.Length);
                output.Write(meta);
                output.Write(bytes);
            }
            else
            {
                BinaryVdf.WriteInt32(output, entry.AppId);
                BinaryVdf.WriteInt32(output, entry.Size);
                output.Write(meta);
                CopyRange(_file, output, entry.Size - MetaLength);
            }
        }

        BinaryVdf.WriteInt32(output, 0);

        if (IndexedKeys)
        {
            long tableOffset = output.Position;
            BinaryVdf.WriteInt32(output, strings.Count);
            foreach (byte[] s in strings) BinaryVdf.WriteCString(output, s);
            output.Position = tableOffsetPos;
            output.Write(BitConverter.GetBytes(tableOffset));
        }
    }

    private static void CopyRange(Stream from, Stream to, int count)
    {
        var buffer = new byte[81920];
        while (count > 0)
        {
            int read = from.Read(buffer, 0, Math.Min(buffer.Length, count));
            if (read <= 0) throw new EndOfStreamException("appinfo.vdf ended mid-app.");
            to.Write(buffer, 0, read);
            count -= read;
        }
    }

    public void Dispose() => _file.Dispose();
}

/// <summary>One entry behind Steam's Play button: config.launch.&lt;n&gt;.</summary>
public sealed class LaunchOption
{
    public string Index { get; set; } = "0";
    public string SourceIndex { get; set; } = "";
    public string Executable { get; set; } = "";
    public string Arguments { get; set; } = "";
    public string WorkingDir { get; set; } = "";
    public string Description { get; set; } = "";
    public string Type { get; set; } = "default";
    public string OsList { get; set; } = "";
    public string OsArch { get; set; } = "";
    public string BetaKey { get; set; } = "";
    public string OwnsDlc { get; set; } = "";

    public string Summary =>
        string.Join("  ·  ", new[]
        {
            Executable,
            string.IsNullOrWhiteSpace(Arguments) ? null : Arguments,
            string.IsNullOrWhiteSpace(OsList) ? null : OsList,
        }.Where(s => !string.IsNullOrWhiteSpace(s))!);

    public LaunchOption Clone() => (LaunchOption)MemberwiseClone();

    public static LaunchOption FromTable(string index, VdfTable table)
    {
        var config = table.GetTable("config");
        return new LaunchOption
        {
            Index = index,
            SourceIndex = index,
            Executable = table.GetText("executable") ?? "",
            Arguments = table.GetText("arguments") ?? "",
            WorkingDir = table.GetText("workingdir") ?? "",
            Description = table.GetText("description") ?? "",
            Type = table.GetText("type") ?? "",
            OsList = config?.GetText("oslist") ?? "",
            OsArch = config?.GetText("osarch") ?? "",
            BetaKey = config?.GetText("BetaKey") ?? "",
            OwnsDlc = config?.GetText("ownsdlc") ?? "",
        };
    }

    public VdfTable ToTable(VdfTable? existing = null)
    {
        var table = existing ?? new VdfTable();
        table.SetText("executable", Executable);
        table.SetText("arguments", Arguments);
        table.SetText("workingdir", WorkingDir);
        table.SetText("description", Description);
        table.SetText("type", Type);

        bool anyConfig = !string.IsNullOrEmpty(OsList) || !string.IsNullOrEmpty(OsArch)
                      || !string.IsNullOrEmpty(BetaKey) || !string.IsNullOrEmpty(OwnsDlc);
        if (anyConfig)
        {
            var config = table.EnsureTable("config");
            config.SetText("oslist", OsList);
            config.SetText("osarch", OsArch);
            config.SetText("BetaKey", BetaKey);
            config.SetText("ownsdlc", OwnsDlc);
        }
        else if (table.GetTable("config") is { } cfg && cfg.Items.Count == 0)
        {
            table.Remove("config");
        }
        return table;
    }

    public static List<LaunchOption> ReadAll(VdfTable appBody)
    {
        var launch = appBody.GetTable("config")?.GetTable("launch");
        if (launch is null) return new List<LaunchOption>();

        return launch.Items
            .Where(p => p.Type == VdfType.Table)
            .Select(p => FromTable(p.Name, (VdfTable)p.Value))
            .ToList();
    }

    public static void WriteAll(VdfTable appBody, IReadOnlyList<LaunchOption> options)
    {
        var config = appBody.EnsureTable("config");
        var previous = config.GetTable("launch");
        var launch = new VdfTable();

        foreach (var option in options)
        {
            string lookup = string.IsNullOrEmpty(option.SourceIndex) ? option.Index : option.SourceIndex;
            var existing = previous?.GetTable(lookup)?.Clone();
            launch.Items.Add(VdfProperty.Table(option.Index, option.ToTable(existing)));
        }

        config.Remove("launch");
        config.Items.Add(VdfProperty.Table("launch", launch));
    }

    public static void Renumber(IReadOnlyList<LaunchOption> options)
    {
        for (int i = 0; i < options.Count; i++) options[i].Index = i.ToString();
    }

    public static string NextIndex(IEnumerable<LaunchOption> options)
    {
        int max = -1;
        foreach (var o in options)
            if (int.TryParse(o.Index, out int n) && n > max) max = n;
        return (max + 1).ToString();
    }
}

public static class FileHash
{
    public static string? Sha256(string? path)
    {
        if (string.IsNullOrWhiteSpace(path)) return null;
        try
        {
            using var fs = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite,
                64 * 1024, FileOptions.SequentialScan);
            return Convert.ToHexString(SHA256.HashData(fs)).ToLowerInvariant();
        }
        catch { return null; }
    }

    public static bool Matches(string? path, string? expected)
    {
        if (string.IsNullOrEmpty(expected)) return true;
        return string.Equals(Sha256(path), expected, StringComparison.OrdinalIgnoreCase);
    }
}

public static class AssetHash
{
    public static string OfFile(string path)
    {
        using var s = File.OpenRead(path);
        return Convert.ToHexString(System.Security.Cryptography.SHA256.HashData(s)).ToLowerInvariant();
    }

    public static string? ParseDigest(string? digest)
    {
        if (string.IsNullOrWhiteSpace(digest)) return null;
        int colon = digest.IndexOf(':');
        return (colon >= 0 ? digest[(colon + 1)..] : digest).Trim().ToLowerInvariant();
    }

    public static bool Matches(string path, string? assetDigest)
    {
        if (ParseDigest(assetDigest) is not { } want) return true;
        return OfFile(path).Equals(want, StringComparison.OrdinalIgnoreCase);
    }
}
