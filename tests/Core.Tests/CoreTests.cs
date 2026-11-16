// LuaTools Nova — Core.Tests: lua codec/parser/editor, manifest codec, persistence, link parser,
// mode migration, DoH parser, byte formatting, library roots.
using LuaTools.Core.Lua;
using LuaTools.Core.Services;
using LuaTools.Infrastructure.Network;
using Xunit;

public class LuaFileParserTests
{
    private const string SampleLua = """
        -- Created by LuaTools Nova
        -- Elden Ring
        addappid(1245620, 1, "AABBCCDD00112233AABBCCDD00112233AABBCCDD00112233AABBCCDD00112233")
        addappid(2233090, 1, "DDEEFF0099887766DDEEFF0099887766DDEEFF0099887766DDEEFF0099887766")
        setManifestid(1245620, "8151245856372456417", 24523452345)
        setManifestid(2233090, "2852155177389857723")
        addappid(3351420)
        -- addappid(4499020, 1, "DEADDEADDEADDEADDEADDEADDEADDEADDEADDEADDEADDEADDEADDEADDEADDEAD")
        """;

    [Fact]
    public void Parse_ExtractsEntriesKeysAndPins()
    {
        var lua = LuaFileParser.ParseText(SampleLua, 1245620);

        Assert.Equal(1245620, lua.BaseAppId);
        Assert.Equal(3, lua.Entries.Count); // the commented addappid is NOT an active entry

        var baseEntry = lua.Entries[0];
        Assert.Equal(1245620, baseEntry.Id);
        Assert.Equal("AABBCCDD00112233AABBCCDD00112233AABBCCDD00112233AABBCCDD00112233", baseEntry.Key);
        Assert.Equal("8151245856372456417", baseEntry.ManifestId);
        Assert.Equal(24523452345L, baseEntry.SizeOnDisk);

        Assert.Equal(2, lua.DepotCount);
        Assert.Equal(1, lua.DlcCount); // the bare addappid(3351420)
        Assert.True(lua.HasActivePins);
        Assert.Single(lua.DisabledEntries);
        Assert.Equal(4499020, lua.DisabledEntries[0].Id);
    }

    [Fact]
    public void Parse_CommentedSetManifestId_GoesToCommentedPins()
    {
        var commented = """
            addappid(730, 1, "AABBCCDD00112233AABBCCDD00112233AABBCCDD00112233AABBCCDD00112233")
            -- setManifestid(730, "1111111111111111111")
            """;
        var lua = LuaFileParser.ParseText(commented, 730);
        Assert.False(lua.ActivePins.ContainsKey(730));
        Assert.Equal("1111111111111111111", lua.CommentedPins[730]);
        Assert.False(lua.HasActivePins);
    }

    [Fact]
    public void Parse_MergesDuplicateAddAppIdKeys()
    {
        var text = """
            addappid(730)
            addappid(730, 1, "AABBCCDD00112233AABBCCDD00112233AABBCCDD00112233AABBCCDD00112233")
            """;
        var lua = LuaFileParser.ParseText(text, 730);
        Assert.Single(lua.Entries);
        Assert.NotNull(lua.Entries[0].Key); // the keyed form wins
    }

    [Fact]
    public void Diff_ReportsAddedAndRemoved()
    {
        var old = LuaFileParser.ParseText("addappid(100)\naddappid(200)", 100);
        var neu = LuaFileParser.ParseText("addappid(200)\naddappid(300)", 100);
        var diff = LuaFileParser.Diff(old, neu);
        Assert.Contains(diff.Added, e => e.Id == 300);
        Assert.Contains(diff.Removed, e => e.Id == 100);
        Assert.True(diff.HasChanges);
    }

    [Fact]
    public void Parse_IgnoresCommentsAndNonStatementLines()
    {
        var text = """
            -- addappid(999999) -- fully commented line is not an entry
            local x = "addappid(888888)" -- not a bare addappid statement (line-anchored grammar)
            """;
        var lua = LuaFileParser.ParseText(text, 1);
        Assert.DoesNotContain(lua.Entries, e => e.Id == 999999);
        Assert.DoesNotContain(lua.Entries, e => e.Id == 888888);
    }
}

public class LuaEditorTests
{
    private const string Lua = """
        addappid(730, 1, "AABBCCDD00112233AABBCCDD00112233AABBCCDD00112233AABBCCDD00112233")
          setManifestid(730, "1111111111111111111", 2000)
        """;

    [Fact]
    public void SetDepotLocked_CommentsPinOut()
    {
        var edited = LuaEditor.SetDepotLocked(Lua, 730, locked: false);
        Assert.Contains("--setManifestid(730", edited, StringComparison.Ordinal);
        Assert.True(edited.Contains("setManifestid(730, \"1111", StringComparison.Ordinal));
        // The key line must stay untouched by the lock toggle
        Assert.True(edited.Contains("addappid(730, 1, \"AABB", StringComparison.Ordinal));

        // and back in, indentation preserved
        var restored = LuaEditor.SetDepotLocked(edited, 730, locked: true);
        Assert.True(restored.Contains("\n  setManifestid(730", StringComparison.Ordinal));
    }

    [Fact]
    public void SetDepotEnabled_CommentsKeyLineOut()
    {
        var edited = LuaEditor.SetDepotEnabled(Lua, 730, enabled: false);
        Assert.Contains("--addappid(730", edited, StringComparison.Ordinal);

        var restored = LuaEditor.SetDepotEnabled(edited, 730, enabled: true);
        Assert.DoesNotContain("--addappid(730", restored, StringComparison.Ordinal);
    }
}

public class SteamLinkParserTests
{
    [Theory]
    [InlineData("https://steamdb.info/app/1245620/", 1245620)]
    [InlineData("https://store.steampowered.com/app/1245620/ELDEN_RING/", 1245620)]
    [InlineData("https://store.steampowered.com/agecheck/app/730/", 730)]
    [InlineData("https://steamcommunity.com/app/570", 570)]
    [InlineData("steam://store/440", 440)]
    [InlineData("steam://rungameid/1245620", 1245620)]
    public void AppIdFrom_ValidLinks(string link, long expected)
        => Assert.Equal(expected, SteamLinkParser.AppIdFrom(link));

    [Theory]
    [InlineData("https://steamdb.info/depot/730/")]   // depot id must NOT parse
    [InlineData("https://steamdb.info/sub/1234/")]   // sub id must NOT parse
    [InlineData("12345")]                            // bare number rejected
    [InlineData("")]
    [InlineData(null)]
    public void AppIdFrom_RejectsNonAppLinks(string? link)
        => Assert.Null(SteamLinkParser.AppIdFrom(link));
}

public class ModeMigrationTests
{
    [Fact]
    public void Legacy_OpenSteamTools_MapsToBst()
    {
        var (mode, reset) = ModeMigration.Migrate("OpenSteamTools");
        Assert.Equal("Bst", mode);
        Assert.False(reset);
    }

    [Fact]
    public void Legacy_RetiredModes_ResetOnboarding()
    {
        var (mode, reset) = ModeMigration.Migrate("SteamTools");
        Assert.Null(mode);
        Assert.True(reset);
    }

    [Fact]
    public void CurrentModes_PassThrough()
    {
        var (mode, reset) = ModeMigration.Migrate("Custom");
        Assert.Equal("Custom", mode);
        Assert.False(reset);
    }

    [Fact]
    public void NullMode_IsFreshInstall()
    {
        var (mode, reset) = ModeMigration.Migrate(null);
        Assert.Null(mode);
        Assert.False(reset);
    }
}

public class ByteFormatTests
{
    [Theory]
    [InlineData(0, "0 B")]
    [InlineData(512, "512 B")]
    [InlineData(2048, "2 KB")]
    [InlineData(5 * 1024 * 1024, "5 MB")]
    [InlineData(1536 * 1024 * 1024, "1.5 GB")]
    public void Size_FormatsBoundaries(long bytes, string expected)
        => Assert.Equal(expected, LuaTools.Core.Downloads.ByteFormat.Size(bytes));

    [Fact]
    public void Rate_EmptyForZero()
        => Assert.Equal("", LuaTools.Core.Downloads.ByteFormat.Rate(0));

    [Fact]
    public void Duration_FormatsMinutesAndSeconds()
    {
        Assert.Equal("8s", LuaTools.Core.Downloads.ByteFormat.Duration(TimeSpan.FromSeconds(8)));
        Assert.Equal("1m 12s", LuaTools.Core.Downloads.ByteFormat.Duration(TimeSpan.FromSeconds(72)));
        Assert.Equal("", LuaTools.Core.Downloads.ByteFormat.Duration(TimeSpan.FromDays(2)));
    }
}

public class DohResolverParseTests
{
    [Fact]
    public void Parse_ExtractsAddressesAndTtl()
    {
        var json = """
            {"Status":0,"Answer":[{"name":"lua.tools","type":1,"TTL":120,"data":"104.21.1.2"}]}
            """;
        var (addresses, ttl) = DohResolver.Parse(json);
        Assert.Single(addresses);
        Assert.Equal("104.21.1.2", addresses[0].ToString());
        Assert.Equal(120, ttl.TotalSeconds);
    }

    [Theory]
    [InlineData("{\"Status\":3}")]          // NXDOMAIN
    [InlineData("not json")]
    [InlineData("{}")]
    public void Parse_FailsSoft(string json)
    {
        var (addresses, _) = DohResolver.Parse(json);
        Assert.Empty(addresses);
    }

    [Theory]
    [InlineData("127.0.0.1")]
    [InlineData("localhost")]
    [InlineData("myservice.local")]
    [InlineData("intranet")]
    public void ShouldBypass_LocalNames(string host)
        => Assert.True(DohResolver.ShouldBypass(host));

    [Fact]
    public void ShouldBypass_PublicNamesFalse()
        => Assert.False(DohResolver.ShouldBypass("lua.tools"));
}
