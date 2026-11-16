// LuaTools Nova v1.0.2 — regression tests for the user-reported Linux Mint 22.3 bugs.
//
// Bug 1 (Add page): covers missing + title mispositioned — root cause: Avalonia has no
//   string→IImage conversion (the original is WPF, which does); the search rows had no icon
//   column and the details card had no header image. Fixed by the UI ImageLoader pipeline +
//   original layouts. These tests cover the pure parts (image validation, URL routing) and the
//   loader attached-state machine headlessly.
// Bug 2 (Add page): Fetch button disabled after selection — root cause: _details/_isChecking
//   never notified FetchCommand, so CanExecute was never re-queried after async updates.
// Bug 3 (Fixes page): buttons disabled / silent failures — root causes: GetDenuvoFixesAsync null
//   return (API down) left an EMPTY list with no feedback (silent catch); GameInstalled=false
//   (Steam not discovered) disabled Fix buttons with no explanation.
using Avalonia.Media.Imaging;
using LuaTools.Application;
using LuaTools.Application.ViewModels;
using LuaTools.Core.Models;
using LuaTools.Integration.Tests;
using Xunit;

namespace LuaTools.Integration.Tests;

public class BugRegressionTests
{
    // ── Bug 2: Fetch button ─────────────────────────────────────────

    [Fact]
    public void FetchCommand_CanExecuteChanged_Fires_WhenDetailsArrive()
    {
        // v1.0.1: selection completed asynchronously, Details was set, but the button never
        // re-queried CanExecute → permanently disabled. The fix makes Details notify the command.
        using var nova = HeadlessHarness.Build();
        var vm = nova.Get<DownloadViewModel>();
        Assert.False(vm.FetchCommand.CanExecute(null));

        int fired = 0;
        vm.FetchCommand.CanExecuteChanged += (_, _) => fired++;

        vm.Details = new GameDetails { AppId = 1245620, Name = "ELDEN RING" };

        Assert.Equal(1, fired); // the button's CanExecute is re-queried → enables
        Assert.True(vm.FetchCommand.CanExecute(null));
    }

    [Fact]
    public void FetchCommand_Disables_WhileChecking_And_ReEnables_After()
    {
        using var nova = HeadlessHarness.Build();
        var vm = nova.Get<DownloadViewModel>();
        vm.Details = new GameDetails { AppId = 1, Name = "X" };
        Assert.True(vm.FetchCommand.CanExecute(null));

        int fired = 0;
        vm.FetchCommand.CanExecuteChanged += (_, _) => fired++;
        vm.IsChecking = true;
        Assert.False(vm.FetchCommand.CanExecute(null));

        vm.IsChecking = false;
        Assert.True(vm.FetchCommand.CanExecute(null));
        Assert.True(fired >= 2);
    }

    [Fact]
    public void DetailsHeaderUrl_TracksSelectedGame()
    {
        using var nova = HeadlessHarness.Build();
        var vm = nova.Get<DownloadViewModel>();
        Assert.Null(vm.DetailsHeaderUrl); // card shows no image without details

        vm.Details = new GameDetails
        {
            AppId = 1245620,
            Name = "ELDEN RING",
            HeaderImage = "https://shared.akamai.steamstatic.com/store_item_assets/steam/apps/1245620/header.jpg",
        };
        Assert.Equal(vm.Details.HeaderImage, vm.DetailsHeaderUrl);

        // Changing selection must change the header (image↔game association requirement).
        vm.Details = new GameDetails { AppId = 292030, Name = "The Witcher 3", HeaderImage = "https://x/292030.jpg" };
        Assert.Contains("292030", vm.DetailsHeaderUrl);
    }

    // ── Bug 3: Fixes page ───────────────────────────────────────────

    [Fact]
    public void Fixes_NullResponse_ShowsError_NotSilentEmpty()
    {
        // v1.0.1: API down → GetDenuvoFixesAsync returned null → silently empty list, no feedback.
        using var nova = HeadlessHarness.Build();
        var vm = nova.Get<FixesViewModel>();

        vm.ApplyFixesData(null);
        Assert.NotNull(vm.FixesError);
        Assert.Empty(vm.Fixes);
    }

    [Fact]
    public void Fixes_ValidResponse_Populates_And_ClearsError()
    {
        using var nova = HeadlessHarness.Build();
        var vm = nova.Get<FixesViewModel>();

        vm.ApplyFixesData(new DenuvoFixesResponse
        {
            AppId = "1245620",
            Name = "ELDEN RING",
            Fixes =
            [
                new DenuvoFix { Id = "fix1", Title = "23850278", HasManifest = true, HasFix = true },
            ],
        });

        Assert.Null(vm.FixesError);
        Assert.Single(vm.Fixes);
        Assert.True(vm.Fixes[0].CanDownloadManifest);   // Manifest button: enabled by data
        Assert.False(vm.Fixes[0].CanDownloadFix);        // Fix button: needs install dir (no Steam in tests)
        Assert.Equal("Install the game first to apply a fix.", vm.Fixes[0].FixHint);
    }

    [Fact]
    public void Fixes_SteamMissing_IsDeclaredHonestly()
    {
        // The test harness has no Steam: the page must say so instead of pretending the game
        // isn't installed. (On the user's machine this is exactly what disabled the Fix buttons.)
        using var nova = HeadlessHarness.Build();
        var vm = nova.Get<FixesViewModel>();
        Assert.False(vm.IsSteamAvailable);
        Assert.Contains("Steam was not detected", vm.SteamMissingHint);
    }

    // ── Bug 4 (audit): Mode page action buttons never updated ────────

    [Fact]
    public void ModeCard_ShowActionButton_IsObservable()
    {
        // v1.0.1: plain { get; set; } set after the card was bound → Mode buttons never enabled.
        var card = new ModeCardViewModel(new ModeCardInfo(
            Core.Models.UnlockerMode.Custom, "Custom", "desc", Core.Models.ModeKind.Zip,
            Core.Models.ModeStatus.UserManaged, null));
        int fired = 0;
        card.PropertyChanged += (_, e) => { if (e.PropertyName == nameof(card.ShowActionButton)) fired++; };
        card.ShowActionButton = false;
        Assert.Equal(1, fired);
    }
}
