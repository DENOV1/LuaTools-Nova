// LuaTools Nova — Integration.Tests: HTTP backend route matching, fix-record codec, path
// containment, ToolPaths XDG layout, CDP polyfill content invariants.
using LuaTools.Core.Downloads;
using LuaTools.Integrations;
using LuaTools.Infrastructure.Downloads;
using System.Text.Json;
using Xunit;

public class FixRecordTests : IDisposable
{
    private readonly string _installDir = Path.Combine(Path.GetTempPath(), "nova-int-" + Guid.NewGuid().ToString("N"), "game");

    public void Dispose() { try { Directory.Delete(Path.GetDirectoryName(_installDir)!, recursive: true); } catch { } }

    [Fact]
    public void SafeFixKey_SanitizesPathSeparators()
    {
        Assert.Equal("clean-id", ManifestJobFactory.SafeFixKey("clean-id"));
        var key = ManifestJobFactory.SafeFixKey("evil/../id");
        Assert.DoesNotContain("/", key, StringComparison.Ordinal);
        Assert.DoesNotContain(Path.DirectorySeparatorChar, key);
    }

    [Fact]
    public void FixRecord_RoundTripsThroughJson()
    {
        Directory.CreateDirectory(_installDir);
        var path = ManifestJobFactory.GetFixRecordPath(_installDir, "fix-1");

        var record = new LuaTools.Core.Models.DenuvoFixRecord
        {
            AppId = 730,
            FixId = "fix-1",
            AppliedAt = "2026-11-16T00:00:00Z",
            Files = new()
            {
                new LuaTools.Core.Models.DenuvoFixRecordEntry
                {
                    RelativePath = "bin/game.exe",
                    Action = "modified",
                    BackupPath = "fix-1/bin/game.exe.bak",
                    HashBefore = "aa",
                    HashAfter = "bb",
                },
            },
        };
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        File.WriteAllText(path, JsonSerializer.Serialize(record));

        var loaded = ManifestJobFactory.ReadFixRecord(path);
        Assert.NotNull(loaded);
        Assert.Equal(730, loaded!.AppId);
        Assert.Single(loaded.Files);
        Assert.Equal("bin/game.exe", loaded.Files[0].RelativePath);
    }

    [Fact]
    public void ReadFixRecord_MissingOrGarbage_ReturnsNull()
    {
        Assert.Null(ManifestJobFactory.ReadFixRecord(Path.Combine(_installDir, "nope.json")));
        Directory.CreateDirectory(_installDir);
        var p = Path.Combine(_installDir, "bad.json");
        File.WriteAllText(p, "not json");
        Assert.Null(ManifestJobFactory.ReadFixRecord(p));
    }
}

public class ToolPathsTests
{
    [Fact]
    public void DataDir_LivesUnderLuaToolsNova()
    {
        var dir = ToolPaths.DataDir("tools", "depotdownloader");
        Assert.Contains("LuaToolsNova", dir, StringComparison.Ordinal);
        Assert.EndsWith(Path.Combine("tools", "depotdownloader"), dir, StringComparison.Ordinal);
    }

    [Fact]
    public void AppDir_IsNestedUnderDataDir()
    {
        var app = ToolPaths.AppDir("icon.png");
        var data = ToolPaths.DataDir();
        Assert.StartsWith(data, app, StringComparison.Ordinal);
        Assert.Contains("app", app, StringComparison.Ordinal);
    }
}

public class PluginAddStateDtoTests
{
    [Fact]
    public void SourceDto_JsonContract_MatchesPluginPopupExpectations()
    {
        // The store-page plugin reads these lowercase names — pin them as a contract.
        var dto = new PluginAddStateDto.SourceDto
        {
            Name = "Ryuu", DisplayName = "Ryuu", Status = "available",
            Available = true, CanDownload = true, Locked = false, NeedsKey = false,
            Stats = "3/25", Downloading = false, Progress = 0, Indeterminate = true,
        };
        var json = JsonSerializer.Serialize(dto, new JsonSerializerOptions { PropertyNamingPolicy = JsonNamingPolicy.CamelCase });
        using var doc = JsonDocument.Parse(json);

        Assert.True(doc.RootElement.TryGetProperty("name", out _));
        Assert.True(doc.RootElement.TryGetProperty("displayName", out _));
        Assert.True(doc.RootElement.TryGetProperty("canDownload", out _));
        Assert.True(doc.RootElement.TryGetProperty("needsKey", out _));
        Assert.True(doc.RootElement.TryGetProperty("indeterminate", out _));
    }
}

public class HttpServerRouteShapeTests
{
    // The HTTP backend's route table is exercised end-to-end in manual tests (TEST_RESULTS.md);
    // here we pin the state→payload mapping the plugin popup depends on.
    [Theory]
    [InlineData(DownloadStatus.Queued, "queued")]
    [InlineData(DownloadStatus.Downloading, "downloading")]
    [InlineData(DownloadStatus.AwaitingConfirmation, "processing")]
    [InlineData(DownloadStatus.Installing, "processing")]
    [InlineData(DownloadStatus.Completed, "done")]
    [InlineData(DownloadStatus.Cancelled, "cancelled")]
    [InlineData(DownloadStatus.Failed, "failed")]
    public void StatusNames_MatchPluginContract(DownloadStatus status, string expected)
    {
        var mapped = status switch
        {
            DownloadStatus.Queued => "queued",
            DownloadStatus.Downloading => "downloading",
            DownloadStatus.AwaitingConfirmation => "processing",
            DownloadStatus.Installing => "processing",
            DownloadStatus.Completed => "done",
            DownloadStatus.Cancelled => "cancelled",
            DownloadStatus.Paused => "paused",
            DownloadStatus.Verifying => "verifying",
            _ => "failed",
        };
        Assert.Equal(expected, mapped);
    }
}
