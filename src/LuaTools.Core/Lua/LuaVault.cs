// Derived from madoiscool/LuaTools (MIT) — LuaVault.cs. Content-addressed variant store.
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;
using LuaTools.Core.Services;

namespace LuaTools.Core.Lua;

public static class LuaVariantKind
{
    public const string Default = "default";
    public const string Build = "build";
    public const string Preset = "preset";
}

public sealed record LuaVariant(
    string Hash, string Kind, string? BuildId, string? Label, DateTime CapturedAt, string? Source,
    IReadOnlyDictionary<string, string> ManifestIds, int DepotCount, int DlcCount)
{
    [JsonIgnore] public bool IsPinned => ManifestIds.Count > 0;

    [JsonIgnore]
    public string DisplayLabel =>
        !string.IsNullOrWhiteSpace(Label) ? Label!
        : BuildId is not null ? $"Build {BuildId}"
        : Kind == LuaVariantKind.Default ? "Default"
        : $"Saved · {CapturedAt:MMM d}";
}

internal sealed class VaultIndex
{
    public long AppId { get; set; }
    public List<LuaVariant> Variants { get; set; } = new();
    public string? EditBaseHash { get; set; }
}

/// <summary>
/// Keeps every lua the app has ever installed for a game, so builds/presets can be switched even
/// though Steam only reads ONE file (config/stplug-in/&lt;appid&gt;.lua). Files are content-addressed.
/// </summary>
public sealed class LuaVault
{
    private static readonly JsonSerializerOptions JsonOpts = new()
    {
        WriteIndented = true,
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
    };

    private readonly string _root;
    private readonly Func<string?> _stPlugInDir;
    private readonly object _gate = new();

    public event Action<long>? VaultChanged;

    public LuaVault(Func<string?> stPlugInDir, string root)
    {
        _stPlugInDir = stPlugInDir;
        _root = root;
    }

    private string AppDir(long appId) => Path.Combine(_root, appId.ToString());
    private string IndexPath(long appId) => Path.Combine(AppDir(appId), "index.json");
    private string VariantPath(long appId, string hash) => Path.Combine(AppDir(appId), hash + ".lua");

    public string? LivePath(long appId) =>
        _stPlugInDir() is { } dir ? Path.Combine(dir, $"{appId}.lua") : null;

    public IReadOnlyList<LuaVariant> GetVariants(long appId)
    {
        lock (_gate) return LoadIndex(appId).Variants.OrderByDescending(v => v.CapturedAt).ToList();
    }

    public string? GetActiveHash(long appId)
    {
        string? path = LivePath(appId);
        return path is not null && File.Exists(path) ? HashFile(path) : null;
    }

    public bool HasVariants(long appId)
    {
        lock (_gate) return LoadIndex(appId).Variants.Count > 0;
    }

    public LuaVariant? GetActiveVariant(long appId)
    {
        string? hash = GetActiveHash(appId);
        return hash is null ? null : GetVariants(appId).FirstOrDefault(v => v.Hash == hash);
    }

    public IEnumerable<(long AppId, string BuildId, string Path)> EnumerateLooseBuildLuas()
    {
        string? dir = _stPlugInDir();
        if (dir is null || !Directory.Exists(dir)) yield break;

        foreach (string path in Directory.EnumerateFiles(dir, "*_*.lua"))
        {
            long? appId = LuaInstaller.AppIdFromFileName(path);
            string? buildId = LuaInstaller.BuildIdFromFileName(path);
            if (appId is { } id && buildId is not null) yield return (id, buildId, path);
        }
    }

    public void AdoptLooseBuildLuas(long appId)
    {
        foreach (var (id, buildId, path) in EnumerateLooseBuildLuas())
            if (id == appId)
                Capture(appId, path, LuaVariantKind.Build, buildId);
    }

    public LuaVariant? ApplyBuildIfNothingLive(long appId)
    {
        if (GetActiveHash(appId) is not null) return null;
        var build = GetVariants(appId).FirstOrDefault(v => v.BuildId is not null);
        if (build is null) return null;
        return Apply(appId, build) ? build : null;
    }

    public IReadOnlyList<long> AppsWithVariants()
    {
        try
        {
            if (!Directory.Exists(_root)) return Array.Empty<long>();
            return Directory.EnumerateDirectories(_root)
                .Select(d => long.TryParse(Path.GetFileName(d), out long id) ? id : 0)
                .Where(id => id > 0 && HasVariants(id))
                .ToList();
        }
        catch { return Array.Empty<long>(); }
    }

    public string ReadText(long appId, string hash)
    {
        try
        {
            string path = VariantPath(appId, hash);
            return File.Exists(path) ? File.ReadAllText(path) : "";
        }
        catch { return ""; }
    }

    public string ReadLiveText(long appId)
    {
        try
        {
            string? path = LivePath(appId);
            return path is not null && File.Exists(path) ? File.ReadAllText(path) : "";
        }
        catch { return ""; }
    }

    public LuaVariant? Capture(long appId, string luaPath, string kind, string? buildId = null, string? source = null)
    {
        try
        {
            if (!File.Exists(luaPath)) return null;
            string hash = HashFile(luaPath);

            LuaVariant result;
            bool changed;

            lock (_gate)
            {
                var index = LoadIndex(appId);
                var existing = index.Variants.FirstOrDefault(v => v.Hash == hash);

                if (existing is not null)
                {
                    changed = existing.BuildId is null && buildId is not null;
                    if (!changed) return existing;

                    result = existing with
                    {
                        BuildId = buildId,
                        Kind = LuaVariantKind.Build,
                        Source = existing.Source ?? source,
                    };
                    index.Variants[index.Variants.IndexOf(existing)] = result;
                }
                else
                {
                    Directory.CreateDirectory(AppDir(appId));
                    File.Copy(luaPath, VariantPath(appId, hash), overwrite: true);
                    result = Describe(appId, luaPath, hash, kind, buildId, source);
                    index.Variants.Add(result);
                    changed = true;
                }

                SaveIndex(appId, index);
            }

            if (changed) VaultChanged?.Invoke(appId);
            return result;
        }
        catch { return null; }
    }

    public void SyncDefaultFromLive(long appId)
    {
        if (SyncDefaultFromLiveCore(appId)) VaultChanged?.Invoke(appId);
    }

    private bool SyncDefaultFromLiveCore(long appId)
    {
        try
        {
            string? live = LivePath(appId);
            if (live is null || !File.Exists(live)) return false;
            string liveHash = HashFile(live);

            lock (_gate)
            {
                var index = LoadIndex(appId);
                bool changed = CollapseDefaults(index, liveHash);

                bool liveIsStored = index.Variants.Any(v => v.Hash == liveHash);
                bool editPending = index.EditBaseHash is not null;

                if (!liveIsStored && !editPending)
                {
                    foreach (var old in index.Variants
                                 .Where(v => v.Kind == LuaVariantKind.Default && v.Hash != liveHash)
                                 .ToList())
                    {
                        index.Variants.Remove(old);
                        DeleteVariantFile(appId, old.Hash);
                    }

                    Directory.CreateDirectory(AppDir(appId));
                    File.Copy(live, VariantPath(appId, liveHash), overwrite: true);
                    index.Variants.Add(Describe(appId, live, liveHash, LuaVariantKind.Default, null, null));
                    changed = true;
                }

                if (changed) SaveIndex(appId, index);
                return changed;
            }
        }
        catch { return false; }
    }

    private static bool CollapseDefaults(VaultIndex index, string? liveHash)
    {
        var defaults = index.Variants.Where(v => v.Kind == LuaVariantKind.Default).ToList();
        if (defaults.Count <= 1) return false;

        var keep = defaults.FirstOrDefault(v => v.Hash == liveHash)
                   ?? defaults.OrderByDescending(v => v.CapturedAt).First();

        foreach (var v in defaults.Where(v => v != keep && v.Hash != liveHash).ToList())
            index.Variants.Remove(v);
        return true;
    }

    private void DeleteVariantFile(long appId, string hash)
    {
        try { File.Delete(VariantPath(appId, hash)); } catch { /* the index is what matters */ }
    }

    public bool Apply(long appId, LuaVariant variant)
    {
        string? live = LivePath(appId);
        string source = VariantPath(appId, variant.Hash);
        if (live is null || !File.Exists(source)) return false;

        try
        {
            SyncDefaultFromLiveCore(appId);
            Directory.CreateDirectory(Path.GetDirectoryName(live)!);
            File.Copy(source, live, overwrite: true);
            StampNow(live);
            SetEditBase(appId, null);
            VaultChanged?.Invoke(appId);
            return true;
        }
        catch { return false; }
    }

    public bool WriteLive(long appId, string text)
    {
        string? live = LivePath(appId);
        if (live is null) return false;
        try
        {
            Directory.CreateDirectory(Path.GetDirectoryName(live)!);
            File.WriteAllText(live, text);
            StampNow(live);
            VaultChanged?.Invoke(appId);
            return true;
        }
        catch { return false; }
    }

    public LuaVariant? SaveText(long appId, string text, string? label)
    {
        string tmp = Path.Combine(Path.GetTempPath(), $"luavault_{Guid.NewGuid():N}.lua");
        try
        {
            File.WriteAllText(tmp, text);
            var variant = Capture(appId, tmp, LuaVariantKind.Preset);
            if (variant is null) return null;

            SetEditBase(appId, null);
            if (!string.IsNullOrWhiteSpace(label))
            {
                Rename(appId, variant.Hash, label);
                return variant with { Label = label };
            }
            return variant;
        }
        catch { return null; }
        finally { try { File.Delete(tmp); } catch { /* best effort */ } }
    }

    public string? GetEditBase(long appId)
    {
        lock (_gate) return LoadIndex(appId).EditBaseHash;
    }

    public void SetEditBase(long appId, string? hash)
    {
        lock (_gate)
        {
            var index = LoadIndex(appId);
            if (index.EditBaseHash == hash) return;
            index.EditBaseHash = hash;
            SaveIndex(appId, index);
        }
    }

    public LuaVariant? UpdateVariant(long appId, string oldHash, string text)
    {
        string tmp = Path.Combine(Path.GetTempPath(), $"luavault_{Guid.NewGuid():N}.lua");
        try
        {
            File.WriteAllText(tmp, text);
            string newHash = HashFile(tmp);

            LuaVariant result;
            lock (_gate)
            {
                var index = LoadIndex(appId);
                int i = index.Variants.FindIndex(v => v.Hash == oldHash);
                if (i < 0) return null;

                var old = index.Variants[i];
                int existing = index.Variants.FindIndex(v => v.Hash == newHash);
                if (existing >= 0 && existing != i)
                {
                    index.Variants.RemoveAt(i);
                    result = index.Variants.First(v => v.Hash == newHash);
                }
                else
                {
                    Directory.CreateDirectory(AppDir(appId));
                    File.Copy(tmp, VariantPath(appId, newHash), overwrite: true);
                    var described = Describe(appId, tmp, newHash, old.Kind, old.BuildId, old.Source);
                    result = described with { Label = old.Label, CapturedAt = old.CapturedAt };
                    index.Variants[i] = result;
                }

                index.EditBaseHash = null;
                SaveIndex(appId, index);

                if (newHash != oldHash) DeleteVariantFile(appId, oldHash);
            }

            VaultChanged?.Invoke(appId);
            return result;
        }
        catch { return null; }
        finally { try { File.Delete(tmp); } catch { /* best effort */ } }
    }

    public void Rename(long appId, string hash, string? label)
    {
        lock (_gate)
        {
            var index = LoadIndex(appId);
            int i = index.Variants.FindIndex(v => v.Hash == hash);
            if (i < 0) return;
            index.Variants[i] = index.Variants[i] with
            {
                Label = string.IsNullOrWhiteSpace(label) ? null : label.Trim()
            };
            SaveIndex(appId, index);
        }
        VaultChanged?.Invoke(appId);
    }

    public bool Delete(long appId, string hash)
    {
        if (GetActiveHash(appId) == hash) return false;

        lock (_gate)
        {
            var index = LoadIndex(appId);
            if (index.Variants.RemoveAll(v => v.Hash == hash) == 0) return false;
            SaveIndex(appId, index);
        }
        DeleteVariantFile(appId, hash);
        VaultChanged?.Invoke(appId);
        return true;
    }

    private static LuaVariant Describe(
        long appId, string luaPath, string hash, string kind, string? buildId, string? source)
    {
        var lua = LuaFileParser.Parse(luaPath, appId);
        var pins = lua?.ActivePins.ToDictionary(p => p.Key.ToString(), p => p.Value)
                   ?? new Dictionary<string, string>();

        return new LuaVariant(
            Hash: hash,
            Kind: buildId is not null ? LuaVariantKind.Build : kind,
            BuildId: buildId,
            Label: null,
            CapturedAt: DateTime.UtcNow,
            Source: source,
            ManifestIds: pins,
            DepotCount: lua?.DepotCount ?? 0,
            DlcCount: lua?.DlcCount ?? 0);
    }

    public static string HashFile(string path)
    {
        using var stream = File.OpenRead(path);
        return Convert.ToHexString(SHA256.HashData(stream)).ToLowerInvariant();
    }

    public static string HashText(string text) =>
        Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(text))).ToLowerInvariant();

    private static void StampNow(string path)
    {
        try
        {
            var now = DateTime.Now;
            File.SetLastWriteTime(path, now);
        }
        catch { /* cosmetic */ }
    }

    private VaultIndex LoadIndex(long appId)
    {
        try
        {
            string path = IndexPath(appId);
            if (File.Exists(path) &&
                JsonSerializer.Deserialize<VaultIndex>(File.ReadAllText(path), JsonOpts) is { } loaded)
                return loaded;

            string bak = path + ".bak";
            if (File.Exists(bak) &&
                JsonSerializer.Deserialize<VaultIndex>(File.ReadAllText(bak), JsonOpts) is { } fromBak)
                return fromBak;
        }
        catch { /* fall through to a fresh index */ }
        return new VaultIndex { AppId = appId };
    }

    private void SaveIndex(long appId, VaultIndex index)
    {
        index.AppId = appId;
        string path = IndexPath(appId);
        string tmp = path + ".tmp";

        Directory.CreateDirectory(AppDir(appId));
        File.WriteAllText(tmp, JsonSerializer.Serialize(index, JsonOpts));
        try { if (File.Exists(path)) File.Copy(path, path + ".bak", overwrite: true); } catch { /* best effort */ }
        File.Move(tmp, path, overwrite: true);
    }
}
