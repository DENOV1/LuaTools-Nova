// LuaTools Nova — Application.Tests: download queue semantics, item metrics, vault/paged list
// behavior through the real ViewModels.
using LuaTools.Core.Downloads;
using LuaTools.Core.Persistence;
using LuaTools.Core.Lua;
using LuaTools.Application.ViewModels;
using Xunit;

public class DownloadQueueTests : IDisposable
{
    private readonly string _dir = Path.Combine(Path.GetTempPath(), "nova-app-" + Guid.NewGuid().ToString("N"));

    public void Dispose() { try { Directory.Delete(_dir, recursive: true); } catch { } }

    private DownloadQueue MakeQueue(out CacheService cache)
    {
        cache = new CacheService(_dir);
        // Deterministic dispatcher: run inline (the UI would marshal to the Avalonia thread).
        return new DownloadQueue(cache, action => action());
    }

    private static DownloadJob MakeJob(long appId = 730, string dedupe = "manifest:730") =>
        new(DownloadKind.Manifest, dedupe, appId, "Test Game", "Source", null,
            (_, _, ct) => Task.FromResult(new DownloadedFile("/tmp/nope.zip", "nope.zip")),
            (_, _, _) => Task.FromResult(new JobResult(true, "ok")),
            OutputPath: "/tmp/nope");

    [Fact]
    public void Enqueue_DeduplicatesByJobKey()
    {
        var q = MakeQueue(out _);
        var a = q.Enqueue(MakeJob());
        var b = q.Enqueue(MakeJob());
        Assert.Same(a, b);
        Assert.Single(q.Items);
    }

    [Fact]
    public void Cancel_QueuedItem_SetsCancelledAndRecordsHistory()
    {
        var q = MakeQueue(out var cache);
        var item = q.Enqueue(MakeJob());
        q.Cancel(item);

        Assert.Equal(DownloadStatus.Cancelled, item.Status);
        Assert.Single(q.History);
        Assert.Single(cache.GetDownloadHistory());
    }

    [Fact]
    public void PauseResume_OnlyAppliesToDepotJobs()
    {
        var q = MakeQueue(out _);
        var manifestItem = q.Enqueue(MakeJob());
        Assert.False(manifestItem.CanPause);

        var depotJob = new DownloadJob(DownloadKind.Depot, "depot:730", 730, "G", "Depot files", null,
            (_, _, ct) => Task.FromResult(new DownloadedFile("/tmp/x", "x")),
            (_, _, _) => Task.FromResult(new JobResult(true, null)));
        var depotItem = q.Enqueue(depotJob);
        depotItem.Status = DownloadStatus.Downloading;
        Assert.True(depotItem.CanPause);

        q.Pause(depotItem);
        Assert.Equal(DownloadStatus.Paused, depotItem.Status);
        Assert.True(depotItem.CanResume);

        q.Resume(depotItem);
        Assert.Equal(DownloadStatus.Queued, depotItem.Status);
        Assert.True(depotItem.NeedsValidate); // resume mandates re-validation
    }

    [Fact]
    public void Retry_CarriesDepotProgressIntoFreshItem()
    {
        var q = MakeQueue(out _);
        var depotJob = new DownloadJob(DownloadKind.Depot, "depot:440", 440, "G", "Depot files", null,
            (_, _, ct) => Task.FromResult(new DownloadedFile("/tmp/x", "x")),
            (_, _, _) => Task.FromResult(new JobResult(true, null)));
        var item = q.Enqueue(depotJob);
        item.CompletedDepots.Add(228980);
        item.Status = DownloadStatus.Failed;

        var fresh = q.Retry(item);
        Assert.Contains(228980, fresh.CompletedDepots);
        Assert.True(fresh.NeedsValidate);
    }

    [Fact]
    public void ClearHistory_Persists()
    {
        var q = MakeQueue(out var cache);
        var item = q.Enqueue(MakeJob());
        q.Cancel(item);
        q.ClearHistory();

        var fresh = MakeQueue(out _);
        Assert.Empty(fresh.History);
        Assert.Empty(cache.GetDownloadHistory());
    }
}

public class DownloadItemMetricTests
{
    private static DownloadItem MakeItem(DownloadKind kind = DownloadKind.Manifest) =>
        new(new DownloadJob(kind, "k:1", 1, "T", "S", null,
            (_, _, _) => Task.FromResult(new DownloadedFile("/tmp/x", "x")),
            (_, _, _) => Task.FromResult(new JobResult(true, null))));

    private static DateTime T(double seconds) => new DateTime(2026, 1, 1, 0, 0, 0, DateTimeKind.Utc).AddSeconds(seconds);

    [Fact]
    public void RateWindow_ComputesSpeedOverSlidingWindow()
    {
        var item = MakeItem();
        item.UtcNow = () => T(0);
        item.ApplySample(0, 1000);

        item.UtcNow = () => T(2); // 2s later, within the 3s window, above min-span
        item.ApplySample(1000, 1000);

        Assert.Equal(500.0, item.BytesPerSecond); // 1000 B / 2s
        Assert.Equal(1000, item.BytesRead);
    }

    [Fact]
    public void RateWindow_MinSpanFloorsBurstNoise()
    {
        var item = MakeItem();
        item.UtcNow = () => T(0);
        item.ApplySample(0, 1000);

        item.UtcNow = () => T(0.05); // 50ms later: below the 0.5s min-span
        item.ApplySample(900, 1000);
        Assert.Equal(0, item.BytesPerSecond); // no rate computed yet
    }

    [Fact]
    public void Eta_DerivedFromRateAndTotal()
    {
        var item = MakeItem();
        item.UtcNow = () => T(0);
        item.ApplySample(0, 2000);
        item.UtcNow = () => T(2);
        item.ApplySample(1000, 2000); // 500 B/s, 1000 B left → 2s ETA

        Assert.NotNull(item.Eta);
        Assert.Equal(TimeSpan.FromSeconds(2), item.Eta!.Value);
    }

    [Fact]
    public void StatusFlags_MatchLifecycle()
    {
        var item = MakeItem(DownloadKind.Manifest);
        Assert.True(item.CanCancel);
        Assert.False(item.CanRemove);
        Assert.True(item.IsIndeterminate);

        item.Status = DownloadStatus.Failed;
        Assert.True(item.CanRetry);
        Assert.True(item.CanRemove);
        Assert.False(item.CanCancel);
    }
}

public class BuildsViewModelLogicTests
{
    [Fact]
    public void ResolveActiveHash_LiveMatchesStored_ReturnsLive()
    {
        var stored = new List<LuaVariant>
        {
            new("abc", LuaVariantKind.Default, null, null, DateTime.UtcNow, null,
                new Dictionary<string, string>(), 0, 0),
        };
        Assert.Equal("abc", BuildsViewModel.ResolveActiveHash("abc", stored, null));
    }

    [Fact]
    public void ResolveActiveHash_LiveIsUnstoredEdit_UsesEditBase()
    {
        var stored = new List<LuaVariant>
        {
            new("base", LuaVariantKind.Build, "123", null, DateTime.UtcNow, null,
                new Dictionary<string, string>(), 0, 0),
        };
        // Live hash "edited" doesn't match any stored variant → the pending edit rides on its base
        Assert.Equal("base", BuildsViewModel.ResolveActiveHash("edited", stored, "base"));
    }

    [Fact]
    public void ResolveActiveHash_NoEditBase_FallsBackToLive()
    {
        var stored = new List<LuaVariant>();
        Assert.Equal("live", BuildsViewModel.ResolveActiveHash("live", stored, null));
    }
}

public class DepotRowLogicTests
{
    [Fact]
    public void Matches_MatchesTitleMetaIdsAndBadges()
    {
        var row = new DepotRow(228980, "Shared depot", "228980  ·  300 MB", false, true,
            "https://steamdb.info/depot/228980/", null, null, null,
            IsInLua: true, IsEnabled: true, CanToggle: true)
        { ToggleId = 228980, Size = 300_000_000 };

        Assert.True(row.Matches("228980"));
        Assert.True(row.Matches("shared"));
        Assert.True(row.Matches("SHARED"));
        Assert.False(row.Matches("zzz-not-there"));
    }

    [Fact]
    public void PinLabels_DistinguishActiveAndCommentedPins()
    {
        var active = new DepotRow(730, "Depot", "730", false, false, "u", "111", null, "111", CanToggle: true);
        Assert.True(active.IsLocked);
        Assert.True(active.IsPinActive);
        Assert.Null(active.OutdatedLabel);

        var commented = new DepotRow(730, "Depot", "730", false, false, "u", null, "111", null, CanToggle: true);
        Assert.False(commented.IsLocked);
        Assert.Contains("pin off", commented.ManifestLabel, StringComparison.Ordinal);

        var outdated = new DepotRow(730, "Depot", "730", false, false, "u", "111", null, "222", CanToggle: true);
        Assert.NotNull(outdated.OutdatedLabel);
        Assert.Contains("222", outdated.OutdatedLabel, StringComparison.Ordinal);
    }
}

public class PageListLogicTests
{
    private sealed class TestList : PagedListViewModel<int>
    {
        public void Seed(IEnumerable<int> values) => SetFiltered(values);
        protected override void SavePageSizeSetting(int size) { }
    }

    [Fact]
    public void Paging_SlicesCorrectly_AndBuildsWindowedPager()
    {
        var list = new TestList();
        list.Seed(Enumerable.Range(1, 100)); // 24/page → 5 pages

        Assert.Equal(24, list.Items.Count);
        Assert.Equal(5, list.TotalPages);
        Assert.True(list.ShowPager);

        list.NextPageCommand.Execute(null);
        Assert.Equal(2, list.CurrentPage);
        Assert.Equal(25, list.Items[0]);

        list.GoToPageCommand.Execute(5);
        Assert.Equal(5, list.CurrentPage);
        Assert.Equal(4, list.Items.Count); // 100 items → last page has 4
        Assert.False(list.CanGoNext);
    }

    [Fact]
    public void PageSize_All_DisablesPaging()
    {
        var list = new TestList { SelectedPageSize = PagedListViewModel<int>.AllPages };
        list.Seed(Enumerable.Range(1, 100));

        Assert.Equal(100, list.Items.Count);
        Assert.False(list.ShowPager);
    }

    [Fact]
    public void SetFiltered_ResetsToFirstPage()
    {
        var list = new TestList();
        list.Seed(Enumerable.Range(1, 100));
        list.GoToPageCommand.Execute(3);
        list.Seed(Enumerable.Range(1, 10));

        Assert.Equal(1, list.CurrentPage);
        Assert.Equal(10, list.Items.Count);
    }
}
