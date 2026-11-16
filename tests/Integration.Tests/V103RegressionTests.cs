// LuaTools Nova v1.0.3 — regression tests for the second Mint 22.3 test round.
//
// Bug 1 (game not in Steam): install actually worked; Steam only shows stplug-in library entries
//   after a restart, and v1.0.2 said nothing. Now the status says "Restart Steam…" + offers the
//   button. These tests pin the install/read precondition that drives the Replace dialog.
// Bug 2 (Fetch proceeds directly): ProtocolInstall permanently persisted FastFetch=true.
// Bug 3 ("No available sources" for everything): backend failures were silent-empty.
// Bug 4 (Manage delete freeze): IDialogService blocked the UI thread on its own dialog.
// Bug 5/6 (Restart Steam / duplicate Settings): command existed unbound; Home Steam row was
//   mis-bound to OpenSettings. Now bound — tests pin the wiring.
using LuaTools.Application;
using LuaTools.Application.ViewModels;
using LuaTools.Core.Persistence;
using LuaTools.Core.Lua;
using LuaTools.Core.Services;
using LuaTools.Integration.Tests;
using Xunit;

namespace LuaTools.Integration.Tests;

public class V103RegressionTests
{
    // ── Bug 1: install → existing-entry detection (Replace dialog precondition) ──

    [Fact]
    public void InstallLua_ThenReadInstalled_FindsExisting_AtSameRoot()
    {
        // The second-Fetch Replace dialog is CORRECT behavior: an installed lua must be found at
        // <EffectivePath>/config/stplug-in/<appid>.lua. The v1.0.2 failure was the missing
        // restart affordance, not this detection.
        var root = Path.Combine(Path.GetTempPath(), "nova-v103-" + Guid.NewGuid().ToString("N"));
        try
        {
            Directory.CreateDirectory(root);
            var locator = new FakeLocator(root);
            var settings = new SettingsService(Path.Combine(root, "settings"));
            var installer = new LuaInstaller(locator, settings,
                new LuaVault(() => Path.Combine(root, "config", "stplug-in"), Path.Combine(root, "vault")), null);

            string lua = Path.Combine(root, "1245620_23850278.lua");
            File.WriteAllText(lua, "setManifestid(\"23850278\")\n");

            var result = installer.InstallLua(lua, 1245620);
            Assert.True(result.LuaInstalled);

            Assert.NotNull(installer.ReadInstalledLua(1245620));            // Replace dialog precondition
            Assert.True(File.Exists(Path.Combine(root, "config", "stplug-in", "1245620.lua")));
        }
        finally { try { Directory.Delete(root, true); } catch { } }
    }

    // ── Bug 2: protocol install must not permanently flip FastFetch ──

    [Fact]
    public async Task ProtocolInstall_Restores_FastFetch_Setting()
    {
        using var nova = HeadlessHarness.Build();
        var vm = nova.Get<DownloadViewModel>();
        var settings = nova.Get<SettingsService>();
        settings.FastFetch = false;
        vm.SyncFastFetch();
        Assert.False(vm.FastFetch);

        // Details lookup for a bogus appid fails fast (client returns null) — either way the
        // protocol flow must restore the user's persisted choice.
        await vm.ProtocolInstall(-1, null);

        Assert.False(vm.FastFetch);
        Assert.False(settings.FastFetch); // settings.json untouched
    }

    [Fact]
    public void FastFetch_Setting_Persists_And_Syncs()
    {
        using var nova = HeadlessHarness.Build();
        var vm = nova.Get<DownloadViewModel>();
        nova.Get<SettingsService>().FastFetch = true;
        vm.SyncFastFetch();
        Assert.True(vm.FastFetch);
    }

    // ── Bug 3: honest source-failure classification ──

    [Fact]
    public void SourceDiagnostics_Distinguishes_BackendFailure_From_NoSources()
    {
        var down = SourceDiagnostics.Classify(new Dictionary<string, string>(), backendFailed: true, anyRowCanDownload: false, anyRowGated: false);
        Assert.Contains("backend could not be reached", down.Message);
        Assert.True(down.HadFailure);

        var none = SourceDiagnostics.Classify(new Dictionary<string, string>(), backendFailed: false, anyRowCanDownload: false, anyRowGated: false);
        Assert.Contains("listed no sources", none.Message);

        var gated = SourceDiagnostics.Classify(new Dictionary<string, string> { ["Luie"] = "available" },
            backendFailed: false, anyRowCanDownload: false, anyRowGated: true);
        Assert.Contains("require signing in", gated.Message);

        var allDown = SourceDiagnostics.Classify(new Dictionary<string, string> { ["Ryuu"] = "error 403" },
            backendFailed: false, anyRowCanDownload: false, anyRowGated: false);
        Assert.Contains("reported errors", allDown.Message);
    }

    // ── Bug 4: delete workflow — async, guarded, correct results ──

    [Fact]
    public async void Manage_DeleteSelected_Deletes_Files_And_Refreshes()
    {
        string root = Path.Combine(Path.GetTempPath(), "nova-del-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(Path.Combine(root, "config", "stplug-in"));
        using var nova = HeadlessHarness.Build(null, root);
        var dialogs = (HeadlessDialogService)nova.Get<IDialogService>();
        var manage = nova.Get<ManageViewModel>();
        var installer = nova.Get<LuaInstaller>();

        foreach (long appid in new[] { 111111, 222222 })
        {
            string lua = Path.Combine(root, $"{appid}.lua");
            await File.WriteAllTextAsync(lua, "setManifestid(\"0\")\n");
            Assert.True(installer.InstallLua(lua, appid).LuaInstalled);
        }
        await manage.LoadAsync();
        var targets = manage.Items.Where(t => t.AppId is 111111 or 222222).ToList();
        Assert.Equal(2, targets.Count);
        foreach (var t in targets) t.IsSelected = true;

        // Decline the confirmation → nothing is deleted, nothing breaks.
        dialogs.ConfirmResult = false;
        await manage.DeleteSelectedCommand.ExecuteAsync(null);
        Assert.True(File.Exists(Path.Combine(root, "config", "stplug-in", "111111.lua")));

        // Confirm → both files deleted, list refreshed, guard reset.
        dialogs.ConfirmResult = true;
        await manage.DeleteSelectedCommand.ExecuteAsync(null);
        Assert.False(File.Exists(Path.Combine(root, "config", "stplug-in", "111111.lua")));
        Assert.False(File.Exists(Path.Combine(root, "config", "stplug-in", "222222.lua")));
        Assert.False(manage.IsDeleting);
    }

    [Fact]
    public async void Manage_DeleteSelected_Reentry_Guarded()
    {
        using var nova = HeadlessHarness.Build();
        var dialogs = (HeadlessDialogService)nova.Get<IDialogService>();
        dialogs.ConfirmResult = false; // user closes the confirm → command ends immediately
        var manage = nova.Get<ManageViewModel>();

        // IsDeleting must never stay stuck after the confirm path.
        await manage.DeleteSelectedCommand.ExecuteAsync(null);
        await manage.DeleteSelectedCommand.ExecuteAsync(null);
        Assert.False(manage.IsDeleting);
    }

    // ── Bug 5/6: Restart Steam is reachable from the UI layer ──

    [Fact]
    public async void Home_RestartSteam_Wired_To_ProcessControl()
    {
        using var nova = HeadlessHarness.Build(new CountingProcessControl());
        var home = nova.Get<HomeViewModel>();
        Assert.False(home.IsSteamRunning); // headless harness has no Steam

        var pc = (CountingProcessControl)nova.Get<IProcessControl>();
        ((HeadlessDialogService)nova.Get<IDialogService>()).ConfirmResult = false;
        await home.RestartSteamCommand.ExecuteAsync(null); // declined → no restart
        Assert.Equal(0, pc.Restarts);

        ((HeadlessDialogService)nova.Get<IDialogService>()).ConfirmResult = true;
        await home.RestartSteamCommand.ExecuteAsync(null); // confirmed → ProcessControl used
        Assert.Equal(1, pc.Restarts);
        Assert.False(home.IsSteamRunning);
    }
}

/// <summary>ProcessControl fake that counts restarts (injected through NovaServices).</summary>
public sealed class CountingProcessControl : IProcessControl
{
    public int Restarts;
    public bool IsSteamRunning() => false;
    public void StopSteam() { }
    public bool RestartSteam() { Restarts++; return true; }
    public bool StartSteam() { return true; }
}

/// <summary>Steam locator over an arbitrary root (mirrors the Linux.Tests fake).</summary>
public sealed class FakeLocator(string root) : LuaTools.Core.Services.ISteamLocator
{
    public string? AutoDetectedPath => root;
    public string? EffectivePath => root;
    public bool IsOverridden => false;
    public bool IsValid => true;
    public string ClientExecutableFor(string steamPath) => Path.Combine(steamPath, "steam.sh");
    public string? SteamLanguage => null;
    public string DetectionSource => "test";
}
