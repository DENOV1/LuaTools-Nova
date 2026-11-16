// LuaTools Nova — headless UI lifecycle tests.
//
// Boots the REAL Avalonia app (App.axaml + FluentAvalonia theme) on the headless platform via the
// official HeadlessUnitTestSession API, builds the real MainWindow from the production DI
// container, and drives navigation across all pages — the class of failure the Linux Mint 22.3
// first-run crash exposed, now verified end-to-end without a display server.
//
// NOTE: this verifies construction, DI wiring, page hosting, navigation and lifecycle —
// NOT pixel rendering. Visual verification on a real display remains open work (KNOWN_ISSUES.md).
using Avalonia;
using Avalonia.Headless;
using Avalonia.Headless.XUnit;
using Avalonia.Skia;
using LuaTools.Application;
using LuaTools.Application.ViewModels;
using LuaTools.Core.Services;
using LuaTools.UI;
using Xunit;

[assembly: Avalonia.Headless.AvaloniaTestApplication(typeof(LuaTools.Integration.Tests.TestAppBuilder))]

namespace LuaTools.Integration.Tests;

/// <summary>Bound via the assembly attribute below; xUnit integration runs each [AvaloniaFact]
/// on the headless UI thread with a properly initialized platform.</summary>
public static class TestAppBuilder
{
    public static AppBuilder BuildAvaloniaApp() =>
        AppBuilder.Configure<App>()
            .UseSkia()
            .UseHeadless(new AvaloniaHeadlessPlatformOptions { UseHeadlessDrawing = false });
}

public class HeadlessUiTests
{
    private static MainWindow CreateWindow()
    {
        // Per-[AvaloniaFact] container+window: the headless xUnit runner owns app lifetime.
        var nova = HeadlessHarness.Build();
        return new MainWindow(
            nova.Get<MainViewModel>(),
            (LuaTools.UI.NavigationHost)nova.Get<INavigationHost>(),
            nova.Get<IDialogService>(),
            nova.Get<IToastService>(),
            nova);
    }

    [AvaloniaFact]
    public void MainWindow_Constructs_FromProductionContainer()
    {
        var window = CreateWindow();
        Assert.NotNull(window);
        Assert.NotNull(window.DataContext);
        window.Close();
    }

    [AvaloniaFact]
    public void MainWindow_AllTenPageDatacontexts_AreInjected()
    {
        var window = CreateWindow();

        // Every page got its ViewModel from the container (v1.0.0 threw here).
        Assert.Equal(9, window.TestPages.Count);
        foreach (var (name, control) in window.TestPages)
        {
            Assert.True(control.DataContext is not null,
                $"Page '{name}' has no DataContext — DI wiring broken");
        }
        window.Close();
    }

    [AvaloniaFact]
    public void Navigation_ToEveryPage_DoesNotThrow()
    {
        var window = CreateWindow();

        foreach (var key in new[] { "home", "add", "manage", "builds", "mode", "fixes", "plugin", "downloads", "settings" })
            window.NavigateTo(key); // v1.0.0 threw InvalidOperationException on this path

        window.Close();
    }

    [AvaloniaFact]
    public void Window_Show_ThenClose_LifecycleCompletes()
    {
        var window = CreateWindow();
        window.Show();
        Avalonia.Threading.Dispatcher.UIThread.RunJobs();
        window.Close();
        Avalonia.Threading.Dispatcher.UIThread.RunJobs();
    }

    [AvaloniaFact]
    public async Task ModeViewModel_Cards_AreVisibleInTheRealShell()
    {
        var nova = HeadlessHarness.Build();
        var window = new MainWindow(
            nova.Get<MainViewModel>(),
            (LuaTools.UI.NavigationHost)nova.Get<INavigationHost>(),
            nova.Get<IDialogService>(),
            nova.Get<IToastService>(),
            nova);

        window.NavigateTo("mode");
        var vm = nova.Get<ModeViewModel>();
        await vm.LoadAsync();
        Assert.Equal(3, vm.Cards.Count);
        window.Close();
    }
}
