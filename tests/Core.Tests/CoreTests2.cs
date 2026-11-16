// LuaTools Nova — Core.Tests (part 2): manifest codec, settings/cache persistence, vault,
// library roots fixture.
using LuaTools.Core.Lua;
using LuaTools.Core.Persistence;
using LuaTools.Core.Services;
using System.Buffers.Binary;
using Xunit;

public class SettingsServiceTests : IDisposable
{
    private readonly string _dir = Path.Combine(Path.GetTempPath(), "nova-tests-" + Guid.NewGuid().ToString("N"));

    public void Dispose() { try { Directory.Delete(_dir, recursive: true); } catch { } }

    [Fact]
    public void Defaults_AreNullableAware()
    {
        var s = new SettingsService(_dir);
        Assert.True(s.AutoUpdateApps);      // "never set" → default ON
        Assert.True(s.DonateKeys);
        Assert.False(s.MinimizeToTray);     // "never set" → default OFF
        Assert.Equal(24, s.ManagePageSize);
        Assert.Equal("Auto", s.DnsMode);
    }

    [Fact]
    public void RoundTrip_PersistsValues()
    {
        var s1 = new SettingsService(_dir);
        s1.HubcapApiKey = "smm_" + new string('a', 96);
        s1.ManagePageSize = 48;
        s1.MinimizeToTray = true;
        s1.SteamPathOverride = "/tmp/steam";

        var s2 = new SettingsService(_dir);
        Assert.Equal(s1.HubcapApiKey, s2.HubcapApiKey);
        Assert.Equal(48, s2.ManagePageSize);
        Assert.True(s2.MinimizeToTray);
        Assert.Equal("/tmp/steam", s2.SteamPathOverride);
        Assert.True(File.Exists(Path.Combine(_dir, "settings.json.bak")));
    }

    [Fact]
    public void CorruptSettings_FallsBackToBackup()
    {
        var s1 = new SettingsService(_dir);
        s1.ManagePageSize = 48;  // save #1: no .bak yet (nothing to back up)
        s1.ManagePageSize = 12;  // save #2: .bak now holds 48

        var s2 = new SettingsService(_dir);
        Assert.Equal(12, s2.ManagePageSize);

        // Corrupt the live file; the .bak must rescue the previous values.
        File.WriteAllText(Path.Combine(_dir, "settings.json"), "{ not json !!!");
        var s3 = new SettingsService(_dir);
        Assert.Equal(48, s3.ManagePageSize);
        Assert.True(File.Exists(Path.Combine(_dir, "settings.json.corrupt")));
    }

    [Fact]
    public void BlankValues_PersistAsNull()
    {
        var s1 = new SettingsService(_dir);
        s1.SteamPathOverride = "   ";
        var s2 = new SettingsService(_dir);
        Assert.Null(s2.SteamPathOverride);
    }
}

public class CacheServiceTests : IDisposable
{
    private readonly string _dir = Path.Combine(Path.GetTempPath(), "nova-tests-" + Guid.NewGuid().ToString("N"));

    public void Dispose() { try { Directory.Delete(_dir, recursive: true); } catch { } }

    [Fact]
    public void DownloadHistory_IsCappedAndOrderedNewestFirst()
    {
        var c = new CacheService(_dir);
        var records = Enumerable.Range(0, 150)
            .Select(i => new DownloadHistoryRecord($"id{i}", "Manifest", i, $"g{i}", "src", 100, "Completed", null,
                1_000_000 + i))
            .ToList();
        c.SaveDownloadHistory(records);

        var loaded = c.GetDownloadHistory();
        Assert.Equal(100, loaded.Count);
        Assert.Equal("id149", loaded[0].Id); // newest first
    }

    [Fact]
    public void DonatedAppIds_AreDeduplicated()
    {
        var c = new CacheService(_dir);
        c.SaveDonatedAppIds(new[] { "730", "570", "730" });
        Assert.Equal(2, c.GetDonatedAppIds().Count);
    }

    [Fact]
    public void LoadedAppIds_SupportRemoveAndClear()
    {
        var c = new CacheService(_dir);
        c.SaveLoadedAppIds(new[] { 730L, 570L });
        c.RemoveLoadedAppId(730);
        Assert.Single(c.GetLoadedAppIds());
        c.ClearLoadedAppIds();
        Assert.Empty(c.GetLoadedAppIds());
    }
}

public class LuaVaultTests : IDisposable
{
    private readonly string _root = Path.Combine(Path.GetTempPath(), "nova-tests-" + Guid.NewGuid().ToString("N"), "luavault");
    private readonly string _stplugIn = Path.Combine(Path.GetTempPath(), "nova-tests-" + Guid.NewGuid().ToString("N"), "stplug-in");

    private LuaVault MakeVault() => new(() => _stplugIn, _root);

    private string InstallLive(long appId, string text)
    {
        Directory.CreateDirectory(_stplugIn);
        var path = Path.Combine(_stplugIn, $"{appId}.lua");
        File.WriteAllText(path, text);
        return path;
    }

    public void Dispose()
    {
        try { Directory.Delete(Path.GetDirectoryName(_root)!, recursive: true); } catch { }
    }

    [Fact]
    public void Capture_And_Apply_RoundTrip()
    {
        var vault = MakeVault();
        var tmp = Path.Combine(Path.GetTempPath(), "nova-src.lua");
        File.WriteAllText(tmp, "addappid(730, 1, \"AABBCCDD00112233AABBCCDD00112233AABBCCDD00112233AABBCCDD00112233\")");

        var variant = vault.Capture(730, tmp, LuaVariantKind.Build, "12345", "test");
        Assert.NotNull(variant);
        Assert.Equal(LuaVariantKind.Build, variant!.Kind);
        Assert.Equal("12345", variant.BuildId);
        Assert.Equal(1, variant.DepotCount);

        InstallLive(730, "addappid(1)");
        Assert.True(vault.Apply(730, variant));
        Assert.Contains("AABBCCDD", File.ReadAllText(Path.Combine(_stplugIn, "730.lua")), StringComparison.Ordinal);
    }

    [Fact]
    public void Capture_IsContentAddressed_DedupeWorks()
    {
        var vault = MakeVault();
        var tmp = Path.Combine(Path.GetTempPath(), "nova-src2.lua");
        File.WriteAllText(tmp, "addappid(570)");

        var v1 = vault.Capture(570, tmp, LuaVariantKind.Preset);
        var v2 = vault.Capture(570, tmp, LuaVariantKind.Preset);
        Assert.NotNull(v1);
        Assert.Equal(v1!.Hash, v2!.Hash);
        Assert.Single(vault.GetVariants(570));
    }

    [Fact]
    public void SyncDefaultFromLive_CapturesTheLiveFile()
    {
        var vault = MakeVault();
        InstallLive(440, "addappid(440)");
        vault.SyncDefaultFromLive(440);

        var variants = vault.GetVariants(440);
        Assert.Single(variants);
        Assert.Equal(LuaVariantKind.Default, variants[0].Kind);
    }

    [Fact]
    public void Delete_RefusesTheActiveVariant()
    {
        var vault = MakeVault();
        var tmp = Path.Combine(Path.GetTempPath(), "nova-src3.lua");
        File.WriteAllText(tmp, "addappid(220)");
        var variant = vault.Capture(220, tmp, LuaVariantKind.Build, "999");
        InstallLive(220, "addappid(220)");
        Assert.True(vault.Apply(220, variant!));

        Assert.False(vault.Delete(220, variant!.Hash)); // it IS live now
    }

    [Fact]
    public void UpdateVariant_ReHashesAndKeepsIdentity()
    {
        var vault = MakeVault();
        var tmp = Path.Combine(Path.GetTempPath(), "nova-src4.lua");
        File.WriteAllText(tmp, "addappid(300)");
        var original = vault.Capture(300, tmp, LuaVariantKind.Preset);

        InstallLive(300, "addappid(300)");
        Assert.True(vault.Apply(300, original!));
        vault.SetEditBase(300, original!.Hash);

        var updated = vault.UpdateVariant(300, original.Hash, "addappid(300)\naddappid(301)");
        Assert.NotNull(updated);
        Assert.NotEqual(original.Hash, updated!.Hash);
        Assert.Null(vault.GetEditBase(300)); // edit consumed
        // UpdateVariant edits the STORED variant; the live file is untouched until Apply.
        Assert.Equal(2, LuaFileParser.ParseText(vault.ReadText(300, updated.Hash), 300).Entries.Count);
        Assert.Single(LuaFileParser.ParseText(File.ReadAllText(Path.Combine(_stplugIn, "300.lua")), 300).Entries);
    }
}

public class ManifestFileTests : IDisposable
{
    private readonly string _dir = Path.Combine(Path.GetTempPath(), "nova-tests-" + Guid.NewGuid().ToString("N"));

    public void Dispose() { try { Directory.Delete(_dir, recursive: true); } catch { } }

    /// <summary>Builds a minimal valid .manifest: payload section + metadata protobuf + EOF.</summary>
    private static byte[] BuildManifest(long depotId, ulong gid, long sizeOnDisk, bool encrypted)
    {
        static byte[] Section(uint magic, byte[] body)
        {
            var buf = new byte[8 + body.Length];
            BinaryPrimitives.WriteUInt32LittleEndian(buf, magic);
            BinaryPrimitives.WriteUInt32LittleEndian(buf.AsSpan(4), (uint)body.Length);
            body.CopyTo(buf, 8);
            return buf;
        }

        static byte[] Varint(ulong v)
        {
            var tmp = new List<byte>();
            while (v >= 0x80) { tmp.Add((byte)(v & 0x7F | 0x80)); v >>= 7; }
            tmp.Add((byte)v);
            return tmp.ToArray();
        }

        static byte[] Tag(int field, int wire) => Varint((ulong)(field << 3 | wire));

        // ContentManifestMetadata: depotId(1)=varint, gid(2)=varint, encrypted(4)=varint, size(5)=varint
        var meta = new List<byte>();
        meta.AddRange(Tag(1, 0)); meta.AddRange(Varint((ulong)depotId));
        meta.AddRange(Tag(2, 0)); meta.AddRange(Varint(gid));
        meta.AddRange(Tag(4, 0)); meta.AddRange(Varint(encrypted ? 1ul : 0ul));
        meta.AddRange(Tag(5, 0)); meta.AddRange(Varint((ulong)sizeOnDisk));

        using var ms = new MemoryStream();
        ms.Write(Section(0x71F617D0, new byte[] { 1, 2, 3 }));      // payload stub
        ms.Write(Section(0x1F4812BE, meta.ToArray()));             // metadata
        ms.Write(Section(0x32C415AB, Array.Empty<byte>()));        // EOF
        return ms.ToArray();
    }

    [Fact]
    public void TryRead_ParsesSyntheticManifest()
    {
        var path = Path.Combine(_dir, "730_8151245856372456417.manifest");
        Directory.CreateDirectory(_dir);
        File.WriteAllBytes(path, BuildManifest(730, 8151245856372456417, 24523452345, encrypted: false));

        var info = ManifestFile.TryRead(path);
        Assert.NotNull(info);
        Assert.Equal(730, info!.Value.DepotId);
        Assert.Equal(8151245856372456417UL, info.Value.GidManifest);
        Assert.Equal(24523452345L, info.Value.SizeOnDisk);
        Assert.False(info.Value.FilenamesEncrypted);
    }

    [Fact]
    public void TryRead_ParsesZippedManifest()
    {
        var path = Path.Combine(_dir, "730_42.manifest.zip");
        Directory.CreateDirectory(_dir);
        using (var zip = System.IO.Compression.ZipFile.Open(path, System.IO.Compression.ZipArchiveMode.Create))
        {
            var entry = zip.CreateEntry("730_42.manifest");
            entry.Open().Write(BuildManifest(730, 42, 999, false));
        }

        var info = ManifestFile.TryRead(path);
        Assert.NotNull(info);
        Assert.Equal(730, info!.Value.DepotId);
        Assert.Equal(42UL, info.Value.GidManifest);
        Assert.Equal(999, info.Value.SizeOnDisk);
    }

    [Fact]
    public void Matches_ChecksIdentity()
    {
        var path = Path.Combine(_dir, "730_8151245856372456417.manifest");
        Directory.CreateDirectory(_dir);
        File.WriteAllBytes(path, BuildManifest(730, 8151245856372456417, 10, false));

        Assert.True(ManifestFile.Matches(path, 730, "8151245856372456417"));
        Assert.False(ManifestFile.Matches(path, 731, "8151245856372456417"));
        Assert.False(ManifestFile.Matches(path, 730, "8151245856372456418"));
    }

    [Fact]
    public void TryRead_MissingOrGarbage_ReturnsNull()
    {
        Assert.Null(ManifestFile.TryRead(null));
        Assert.Null(ManifestFile.TryRead(Path.Combine(_dir, "does-not-exist.manifest")));

        var junk = Path.Combine(_dir, "junk.manifest");
        Directory.CreateDirectory(_dir);
        File.WriteAllBytes(junk, new byte[] { 1, 2, 3, 4, 5 });
        Assert.Null(ManifestFile.TryRead(junk));
    }
}

public class SteamLibraryServiceTests : IDisposable
{
    private readonly string _root = Path.Combine(Path.GetTempPath(), "nova-tests-" + Guid.NewGuid().ToString("N"));

    public void Dispose() { try { Directory.Delete(_root, recursive: true); } catch { } }

    [Fact]
    public void GetLibraryRoots_UnionsConfigAndSteamappsVdfs()
    {
        // Root library + one extra library present ONLY in config/libraryfolders.vdf (the drift case),
        // and another present in BOTH (must dedupe).
        var extraOnly = Path.Combine(_root, "extra");
        var dup = Path.Combine(_root, "dup");
        Directory.CreateDirectory(Path.Combine(_root, "config"));
        Directory.CreateDirectory(Path.Combine(_root, "steamapps"));
        Directory.CreateDirectory(extraOnly);
        Directory.CreateDirectory(dup);

        File.WriteAllText(Path.Combine(_root, "config", "libraryfolders.vdf"),
            $@"""LibraryFolders"" {{ ""1"" {{ ""path"" ""{_root}"" }} ""2"" {{ ""path"" ""{extraOnly.Replace('\\', '/')}"" }} }}");
        File.WriteAllText(Path.Combine(_root, "steamapps", "libraryfolders.vdf"),
            $@"""LibraryFolders"" {{ ""1"" {{ ""path"" ""{_root}"" }} ""2"" {{ ""path"" ""{dup.Replace('\\', '/')}"" }} }}");

        var roots = SteamLibraryService.GetLibraryRoots(_root).ToList();
        Assert.Equal(_root, roots[0]);
        Assert.Contains(extraOnly, roots);
        Assert.Contains(dup, roots);
        Assert.Equal(3, roots.Count); // deduped
    }

    [Fact]
    public void EnumerateInstalled_SkipsMissingInstalldirs()
    {
        Directory.CreateDirectory(Path.Combine(_root, "steamapps", "common", "RealGame"));
        File.WriteAllText(Path.Combine(_root, "steamapps", "appmanifest_100.acf"),
            "\"AppState\" { \"appid\" \"100\" \"name\" \"Real\" \"installdir\" \"RealGame\" }");
        File.WriteAllText(Path.Combine(_root, "steamapps", "appmanifest_200.acf"),
            "\"AppState\" { \"appid\" \"200\" \"name\" \"Ghost\" \"installdir\" \"NotThere\" }");

        var locator = new FakeLocator(_root);
        var lib = new SteamLibraryService(locator);
        var games = lib.EnumerateInstalled().ToList();

        Assert.Single(games);
        Assert.Equal(100, games[0].AppId);
        Assert.Equal("Real", games[0].Name);
    }

    private sealed class FakeLocator(string path) : LuaTools.Core.Services.ISteamLocator
    {
        public string? AutoDetectedPath => path;
        public string? EffectivePath => path;
        public bool IsOverridden => false;
        public bool IsValid => true;
        public string ClientExecutableFor(string steamPath) => Path.Combine(steamPath, "steam.sh");
        public string? SteamLanguage => "english";
        public string DetectionSource => "test";
    }
}
