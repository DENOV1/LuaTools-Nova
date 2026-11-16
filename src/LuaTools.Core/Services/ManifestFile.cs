// Derived from madoiscool/LuaTools (MIT) — ManifestFile.cs.
// Minimal reader for Steam's depot manifest format: depot id, size, gid, encrypted-names flag.
using System.Buffers.Binary;
using System.IO.Compression;
using System.Security.Cryptography;
using System.Text;

namespace LuaTools.Core.Services;

public readonly record struct ManifestInfo(long DepotId, bool FilenamesEncrypted, long SizeOnDisk, ulong GidManifest);

public static class ManifestFile
{
    private const uint PayloadMagic = 0x71F617D0;
    private const uint MetadataMagic = 0x1F4812BE;
    private const uint EofMagic = 0x32C415AB;

    private const int FieldDepotId = 1;
    private const int FieldGidManifest = 2;
    private const int FieldFilenamesEncrypted = 4;
    private const int FieldSizeOnDisk = 5;

    public static ManifestInfo? TryRead(string? path)
    {
        if (string.IsNullOrWhiteSpace(path) || !File.Exists(path)) return null;

        try
        {
            using var fs = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite);

            Span<byte> peek = stackalloc byte[2];
            if (fs.Read(peek) == 2 && peek[0] == 'P' && peek[1] == 'K')
                return ParseMetadata(FindSection(Unwrap(File.ReadAllBytes(path)), MetadataMagic));
            fs.Position = 0;

            byte[] header = new byte[8];
            while (fs.Position + 8 <= fs.Length)
            {
                if (fs.Read(header, 0, 8) != 8) return null;
                uint magic = BinaryPrimitives.ReadUInt32LittleEndian(header);
                uint len = BinaryPrimitives.ReadUInt32LittleEndian(header.AsSpan(4));

                if (magic == EofMagic) break;
                if (len > int.MaxValue || fs.Position + len > fs.Length) return null;

                if (magic != MetadataMagic) { fs.Position += len; continue; }

                byte[] meta = new byte[len];
                return fs.ReadAtLeast(meta, (int)len, throwOnEndOfStream: false) == (int)len
                    ? ParseMetadata(meta)
                    : null;
            }
            return null;
        }
        catch { return null; }
    }

    private static ManifestInfo? ParseMetadata(byte[]? meta)
    {
        if (meta is null) return null;

        long depotId = 0, size = 0;
        ulong gid = 0;
        bool encrypted = false;

        int o = 0;
        while (o < meta.Length)
        {
            if (!ReadTag(meta, ref o, out int field, out int wire)) return null;
            if (wire == 0)
            {
                if (!ReadVarint(meta, ref o, out ulong v)) return null;
                switch (field)
                {
                    case FieldDepotId: depotId = (long)v; break;
                    case FieldGidManifest: gid = v; break;
                    case FieldFilenamesEncrypted: encrypted = v != 0; break;
                    case FieldSizeOnDisk: size = (long)v; break;
                }
            }
            else if (!SkipField(meta, ref o, wire)) return null;
        }

        return new ManifestInfo(depotId, encrypted, size, gid);
    }

    public static bool Matches(string? path, long depotId, string manifestId) =>
        TryRead(path) is { } info
        && info.DepotId == depotId
        && ulong.TryParse(manifestId, out ulong gid)
        && info.GidManifest == gid;

    public static bool KeyLooksValid(string? path, byte[] key)
    {
        if (string.IsNullOrWhiteSpace(path) || !File.Exists(path) || key.Length != 32) return true;
        if (TryRead(path) is not { FilenamesEncrypted: true }) return true;

        try
        {
            byte[] data = Unwrap(File.ReadAllBytes(path));
            if (FindSection(data, PayloadMagic) is not { } payload) return true;
            if (FirstFilename(payload) is not { } name) return true;

            byte[] cipher;
            try { cipher = Convert.FromBase64String(name); }
            catch (FormatException) { return true; }
            if (cipher.Length <= 16 || cipher.Length % 16 != 0) return true;

            return TryDecryptName(cipher, key);
        }
        catch { return true; }
    }

    private static bool TryDecryptName(byte[] cipher, byte[] key)
    {
        using var ecb = Aes.Create();
        ecb.Key = key;
        ecb.Mode = CipherMode.ECB;
        ecb.Padding = PaddingMode.None;
        byte[] iv = ecb.CreateDecryptor().TransformFinalBlock(cipher, 0, 16);

        using var cbc = Aes.Create();
        cbc.Key = key;
        cbc.IV = iv;
        cbc.Mode = CipherMode.CBC;
        cbc.Padding = PaddingMode.PKCS7;

        try
        {
            byte[] plain = cbc.CreateDecryptor().TransformFinalBlock(cipher, 16, cipher.Length - 16);
            foreach (byte b in plain)
                if (b < 0x20 && b != 0) return false;
            return true;
        }
        catch (CryptographicException) { return false; }
    }

    private static byte[] Unwrap(byte[] data)
    {
        if (data.Length < 2 || data[0] != 'P' || data[1] != 'K') return data;

        using var zip = new ZipArchive(new MemoryStream(data), ZipArchiveMode.Read);
        var entry = zip.Entries.FirstOrDefault();
        if (entry is null) return data;

        using var s = entry.Open();
        using var buf = new MemoryStream();
        s.CopyTo(buf);
        return buf.ToArray();
    }

    private static byte[]? FindSection(byte[] data, uint magic)
    {
        int o = 0;
        while (o + 8 <= data.Length)
        {
            uint m = BinaryPrimitives.ReadUInt32LittleEndian(data.AsSpan(o));
            int len = (int)BinaryPrimitives.ReadUInt32LittleEndian(data.AsSpan(o + 4));
            o += 8;

            if (m == EofMagic) break;
            if (len < 0 || o + len > data.Length) break;
            if (m == magic) return data[o..(o + len)];
            o += len;
        }
        return null;
    }

    private static string? FirstFilename(byte[] payload)
    {
        int o = 0;
        while (o < payload.Length)
        {
            if (!ReadTag(payload, ref o, out int field, out int wire)) return null;

            if (field == 1 && wire == 2)
            {
                if (!ReadVarint(payload, ref o, out ulong len)) return null;
                int end = o + (int)len;
                if (end > payload.Length) return null;

                int inner = o;
                while (inner < end)
                {
                    if (!ReadTag(payload, ref inner, out int f2, out int w2)) return null;
                    if (f2 == 1 && w2 == 2)
                    {
                        if (!ReadVarint(payload, ref inner, out ulong n)) return null;
                        if (inner + (int)n > payload.Length) return null;
                        return Encoding.UTF8.GetString(payload, inner, (int)n);
                    }
                    if (!SkipField(payload, ref inner, w2)) return null;
                }
                o = end;
            }
            else if (!SkipField(payload, ref o, wire)) return null;
        }
        return null;
    }

    private static bool ReadTag(byte[] d, ref int o, out int field, out int wire)
    {
        field = wire = 0;
        if (!ReadVarint(d, ref o, out ulong tag)) return false;
        field = (int)(tag >> 3);
        wire = (int)(tag & 0x07);
        return true;
    }

    private static bool ReadVarint(byte[] d, ref int o, out ulong value)
    {
        value = 0;
        int shift = 0;
        while (o < d.Length)
        {
            byte b = d[o++];
            value |= (ulong)(b & 0x7F) << shift;
            if ((b & 0x80) == 0) return true;
            shift += 7;
            if (shift > 63) return false;
        }
        return false;
    }

    private static bool SkipField(byte[] d, ref int o, int wire)
    {
        switch (wire)
        {
            case 0: return ReadVarint(d, ref o, out _);
            case 1: o += 8; return o <= d.Length;
            case 5: o += 4; return o <= d.Length;
            case 2:
                if (!ReadVarint(d, ref o, out ulong len)) return false;
                o += (int)len;
                return o <= d.Length;
            default: return false;
        }
    }
}
