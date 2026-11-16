// LuaTools Nova — Linux.Tests: Steam path discovery, Proton compat detection, Wine overrides,
// clean-restart semantics knowledge encoded as testable pure functions.
using LuaTools.Linux;
using Xunit;

public class SteamPathLocatorTests : IDisposable
{
    private readonly string _home = Path.Combine(Path.GetTempPath(), "nova-linux-" + Guid.NewGuid().ToString("N"));

    public SteamPathLocatorTests()
    {
        Directory.CreateDirectory(_home);
    }

    public void Dispose() { try { Directory.Delete(_home, recursive: true); } catch { } }

    private void WithHome(Action run)
    {
        var old = Environment.GetEnvironmentVariable("HOME");
        Environment.SetEnvironmentVariable("HOME", _home);
        try { run(); }
        finally { Environment.SetEnvironmentVariable("HOME", old); }
    }

    [Fact]
    public void BootstrapSymlink_IsFollowedAndValidated()
    {
        var real = Path.Combine(_home, ".local", "share", "Steam");
        Directory.CreateDirectory(real);
        File.WriteAllText(Path.Combine(real, "steam.sh"), "#!/bin/bash\n");

        Directory.CreateDirectory(Path.Combine(_home, ".steam"));
        File.CreateSymbolicLink(Path.Combine(_home, ".steam", "steam"), real);

        WithHome(() =>
        {
            var locator = new SteamPathLocator(null);
            Assert.Equal(real, locator.AutoDetectedPath);
            Assert.Equal("~/.steam/steam", locator.DetectionSource);
            Assert.True(locator.IsValid);
            Assert.Equal(Path.Combine(real, "steam.sh"), locator.ClientExecutableFor(real));
        });
    }

    [Fact]
    public void DebianInstallation_Fallback_Detected()
    {
        var real = Path.Combine(_home, ".steam", "debian-installation");
        Directory.CreateDirectory(real);
        File.WriteAllText(Path.Combine(real, "steam.sh"), "");

        WithHome(() =>
        {
            var locator = new SteamPathLocator(null);
            Assert.Equal(real, locator.AutoDetectedPath);
            Assert.Equal("~/.steam/debian-installation", locator.DetectionSource);
        });
    }

    [Fact]
    public void FlatpakSteam_IsDetectedAndFlagged()
    {
        var real = Path.Combine(_home, ".var", "app", "com.valvesoftware.Steam", "data", "Steam");
        Directory.CreateDirectory(real);
        File.WriteAllText(Path.Combine(real, "steam.sh"), "");

        WithHome(() =>
        {
            var locator = new SteamPathLocator(null);
            Assert.Equal(real, locator.AutoDetectedPath);
            Assert.Contains("flatpak", locator.DetectionSource, StringComparison.Ordinal);
        });
    }

    [Fact]
    public void SteamLanguage_ComesFromRegistryVdf()
    {
        Directory.CreateDirectory(Path.Combine(_home, ".steam"));
        File.WriteAllText(Path.Combine(_home, ".steam", "registry.vdf"),
            "\"Registry\" { \"HKCU\" { \"Software\" { \"Valve\" { \"Steam\" { \"Language\"\t\t\"brazilian\" } } } } }");

        WithHome(() =>
        {
            var locator = new SteamPathLocator(null);
            Assert.Equal("brazilian", locator.SteamLanguage);
        });
    }

    [Fact]
    public void Override_TakesPrecedence()
    {
        var real = Path.Combine(_home, "custom-steam");
        Directory.CreateDirectory(real);
        File.WriteAllText(Path.Combine(real, "steam.sh"), "");

        WithHome(() =>
        {
            var locator = new SteamPathLocator(real);
            Assert.True(locator.IsOverridden);
            Assert.Equal(real, locator.EffectivePath);
            Assert.True(locator.IsValid);

            locator.SetOverride(null);
            Assert.False(locator.IsOverridden);
            Assert.Null(locator.AutoDetectedPath); // nothing else exists under the fake home
        });
    }
}

public class ProtonCompatTests
{
    private const string ConfigVdf = """
        "InstallConfigStore"
        {
            "Software"
            {
                "Valve"
                {
                    "Steam"
                    {
                        "CompatToolMapping"
                        {
                            "1245620"
                            {
                                "name"      "proton_experimental"
                                "config"    ""
                                "priority"  "250"
                            }
                            "570"
                            {
                                "name"      "steamlinuxruntime_sniper"
                                "config"    ""
                                "priority"  "250"
                            }
                            "440"
                            {
                                "name"      ""
                                "config"    ""
                                "priority"  "250"
                            }
                        }
                    }
                }
            }
        }
        """;

    [Fact]
    public void ToolForApp_ResolvesForcedTool()
        => Assert.Equal("proton_experimental", ProtonCompat.ToolForApp(ConfigVdf, 1245620));

    [Fact]
    public void ToolForApp_UnsetEntry_ReturnsEmpty()
    {
        var name = ProtonCompat.ToolForApp(ConfigVdf, 440);
        Assert.NotNull(name);
        Assert.Equal("", name);
    }

    [Fact]
    public void ToolForApp_UnknownApp_ReturnsNull()
        => Assert.Null(ProtonCompat.ToolForApp(ConfigVdf, 999999));

    [Fact]
    public void IsProtonName_NativeLinuxRuntimesAreNotProton()
    {
        Assert.False(ProtonCompat.IsProtonName("steamlinuxruntime_sniper"));
        Assert.False(ProtonCompat.IsProtonName("steamlinuxruntime"));
        Assert.False(ProtonCompat.IsProtonName(""));
        Assert.False(ProtonCompat.IsProtonName(null));
    }

    [Fact]
    public void IsProtonName_ProtonToolsAreTrue()
        => Assert.True(ProtonCompat.IsProtonName("proton_experimental"));

    [Fact]
    public void IsProtonForced_NativeRuntimeGame_IsNotProtonForced()
    {
        var tmp = Path.Combine(Path.GetTempPath(), "nova-linux-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(Path.Combine(tmp, "config"));
        File.WriteAllText(Path.Combine(tmp, "config", "config.vdf"), ConfigVdf);

        try
        {
            var locator = new TestLocator(tmp);
            Assert.True(ProtonCompat.IsProtonForced(locator, 1245620));
            Assert.False(ProtonCompat.IsProtonForced(locator, 570)); // linux runtime ≠ proton
        }
        finally { try { Directory.Delete(tmp, recursive: true); } catch { } }
    }

    private sealed class TestLocator(string path) : LuaTools.Core.Services.ISteamLocator
    {
        public string? AutoDetectedPath => path;
        public string? EffectivePath => path;
        public bool IsOverridden => false;
        public bool IsValid => true;
        public string ClientExecutableFor(string steamPath) => Path.Combine(steamPath, "steam.sh");
        public string? SteamLanguage => null;
        public string DetectionSource => "test";
    }
}

public class WineOverridesTests
{
    [Fact]
    public void BuildOverrides_OnlyCollidingBuiltins()
    {
        var overrides = WineOverrides.BuildOverrides(new[] { "winmm.dll", "OnlineFix.dll", "xinput1_4.dll", "steam_api.dll" });
        Assert.NotNull(overrides);
        Assert.Contains("winmm=n,b", overrides, StringComparison.Ordinal);
        Assert.Contains("xinput1_4=n,b", overrides, StringComparison.Ordinal);
        Assert.DoesNotContain("onlinefix", overrides, StringComparison.OrdinalIgnoreCase); // not a Wine builtin
        Assert.DoesNotContain("steam_api", overrides, StringComparison.Ordinal);
        Assert.StartsWith("WINEDLLOVERRIDES=", overrides, StringComparison.Ordinal);
    }

    [Fact]
    public void BuildOverrides_NullWhenNoCollisions()
        => Assert.Null(WineOverrides.BuildOverrides(new[] { "OnlineFix64.dll" }));

    [Fact]
    public void Merge_PreservesUserOptionsAndCommand()
    {
        var merged = WineOverrides.MergeLaunchOptions(
            "gamemoderun %command% --nolauncher",
            WineOverrides.BuildOverrides(new[] { "winmm.dll" })!);

        Assert.StartsWith("WINEDLLOVERRIDES=", merged, StringComparison.Ordinal); // env var FIRST
        Assert.Contains("gamemoderun", merged, StringComparison.Ordinal);
        Assert.Contains("%command%", merged, StringComparison.Ordinal);
        Assert.Contains("--nolauncher", merged, StringComparison.Ordinal);
    }

    [Fact]
    public void Merge_IsIdempotent()
    {
        var overrides = WineOverrides.BuildOverrides(new[] { "winmm.dll" })!;
        var once = WineOverrides.MergeLaunchOptions("mangohud %command%", overrides);
        var twice = WineOverrides.MergeLaunchOptions(once, overrides);
        Assert.Equal(once, twice);
    }

    [Fact]
    public void Remove_ClearsOverrideAndKeepsUserOptions()
    {
        var overrides = WineOverrides.BuildOverrides(new[] { "winmm.dll", "winhttp.dll" })!;
        var merged = WineOverrides.MergeLaunchOptions("mangohud %command%", overrides);
        var removed = WineOverrides.RemoveOverrides(merged);

        Assert.DoesNotContain("WINEDLLOVERRIDES", removed, StringComparison.Ordinal);
        Assert.Contains("mangohud", removed, StringComparison.Ordinal);
        Assert.Equal("mangohud %command%", removed);
    }
}
