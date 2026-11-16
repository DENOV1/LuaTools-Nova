// LuaTools Nova v1.0.3 — price retrieval + registry country + NovaLog regression tests.
using LuaTools.Infrastructure.Clients;
using LuaTools.Core.Services;
using Xunit;

namespace LuaTools.Integration.Tests;

public class StorePriceTests
{
    private const string DiscountedJson = """
        {"1245620":{"success":true,"data":{"price_overview":{"currency":"EUR","initial":3999,"final":1999,"discount_percent":50,"final_formatted":"19,99€","initial_formatted":"39,99€"}}}}
        """;
    private const string PlainJson = """
        {"470220":{"success":true,"data":{"price_overview":{"currency":"USD","initial":1999,"final":1999,"discount_percent":0,"final_formatted":"$19.99","initial_formatted":""}}}}
        """;
    private const string FreeJson = """
        {"730":{"success":true,"data":{"is_free":true}}}
        """;
    private const string NoDataJson = """
        {"999999":{"success":false}}
        """;

    [Fact]
    public void Parse_Discounted_Shows_Original_Strikethrough()
    {
        var p = SteamStoreService.ParseAppDetails(DiscountedJson, 1245620);
        Assert.NotNull(p);
        Assert.Equal("19,99€", p!.FinalFormatted);
        Assert.Equal("39,99€", p.InitialFormatted);
        Assert.Equal(50, p.DiscountPercent);
        Assert.False(p.IsFree);
    }

    [Fact]
    public void Parse_NonDiscounted_Has_No_Original()
    {
        var p = SteamStoreService.ParseAppDetails(PlainJson, 470220);
        Assert.NotNull(p);
        Assert.Equal("$19.99", p!.FinalFormatted);
        Assert.Null(p.InitialFormatted);
    }

    [Fact]
    public void Parse_Free_Game_Is_Free()
    {
        var p = SteamStoreService.ParseAppDetails(FreeJson, 730);
        Assert.NotNull(p);
        Assert.True(p!.IsFree);
        Assert.Equal("Free", p.FinalFormatted);
    }

    [Fact]
    public void Parse_Unavailable_Returns_Null_NeverFabricated()
    {
        Assert.Null(SteamStoreService.ParseAppDetails(NoDataJson, 999999));
        Assert.Null(SteamStoreService.ParseAppDetails("{}", 1));
        Assert.ThrowsAny<Exception>(() => SteamStoreService.ParseAppDetails("not-json", 1)); // caller catches
    }

    [Fact]
    public void Registry_Country_Regex_Finds_Valve_Steam_Node()
    {
        const string vdf = """
            "Registry"
            {
                "HKCU"
                {
                    "Software"
                    {
                        "Valve"
                        {
                            "Steam"
                            {
                                "language"        "german"
                                "country"         "de"
                            }
                        }
                    }
                }
            }
            """;
        Assert.True(SteamStoreService.TryReadCountry(vdf, out string cc));
        Assert.Equal("de", cc);
        Assert.False(SteamStoreService.TryReadCountry("{}", out _));
    }
}

public class NovaLogTests
{
    [Fact]
    public void Log_Writes_Lines_To_Configured_File()
    {
        string dir = Path.Combine(Path.GetTempPath(), "nova-log-" + Guid.NewGuid().ToString("N"));
        try
        {
            NovaLog.ResetForTests(dir);
            NovaLog.Info("test", "hello");
            NovaLog.Error("test", "boom");
            string text = File.ReadAllText(NovaLog.LogPath);
            Assert.Contains("[INFO] test: hello", text);
            Assert.Contains("[ERROR] test: boom", text);
        }
        finally
        {
            NovaLog.ResetForTests(null);
            try { Directory.Delete(dir, true); } catch { }
        }
    }
}
