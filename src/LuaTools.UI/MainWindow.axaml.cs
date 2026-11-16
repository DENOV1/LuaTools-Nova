using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Media;
using Avalonia.Media.Imaging;
using Avalonia.Styling;
using FluentAvalonia.UI.Controls;

using LuaTools.Application;
using LuaTools.Application.ViewModels;
using LuaTools.Core.Services;
using LuaTools.UI.Services;
using System;
using System.Collections.Generic;
using System.Threading.Tasks;

namespace LuaTools.UI;

public partial class MainWindow : Window
{
    private readonly MainViewModel _vm;
    private readonly NavigationHost _navigation;
    private readonly IDialogService _dialogs;
    private readonly IToastService _toasts;
    private readonly NovaServices _svc;
    private bool _reallyExiting;

    private readonly Dictionary<string, Control> _pages;
    private readonly Dictionary<string, Action> _pageLoads;


    public MainWindow(MainViewModel vm, NavigationHost navigation, IDialogService dialogs,
        IToastService toasts, NovaServices svc)
    {
        _vm = vm;
        _navigation = navigation;
        _dialogs = dialogs;
        _toasts = toasts;
        _svc = svc;

        InitializeComponent();

        TryLoadIcon();

        _pages = new Dictionary<string, Control>
        {
            ["home"] = PageHome,
            ["add"] = PageAdd,
            ["manage"] = PageManage,
            ["builds"] = PageBuilds,
            ["mode"] = PageMode,
            ["fixes"] = PageFixes,
            ["plugin"] = PagePlugin,
            ["downloads"] = PageDownloads,
            ["settings"] = PageSettings,
        };

        var toast = (ToastServiceImpl)_toasts;
        toast.Attach(this);

        DataContext = vm;

        // Wire the page DataContexts (views are named controls, not DI-resolved).
        PageHome.DataContext = svc.Get<HomeViewModel>();
        PageAdd.DataContext = svc.Get<DownloadViewModel>();
        PageManage.DataContext = svc.Get<ManageViewModel>();
        PageBuilds.DataContext = svc.Get<BuildsViewModel>();
        PageMode.DataContext = svc.Get<ModeViewModel>();
        PageFixes.DataContext = svc.Get<FixesViewModel>();
        PagePlugin.DataContext = svc.Get<PluginViewModel>();
        PageDownloads.DataContext = svc.Get<DownloadsViewModel>();
        PageSettings.DataContext = svc.Get<SettingsViewModel>();
        OnboardingOverlay.DataContext = vm.Onboarding;

        _pageLoads = new Dictionary<string, Action>
        {
            ["home"] = () => _ = svc.Get<HomeViewModel>().LoadAsync(),
            ["add"] = () => _ = svc.Get<DownloadViewModel>().LoadFeaturedAsync(),
            ["manage"] = () => _ = svc.Get<ManageViewModel>().LoadAsync(),
            ["builds"] = () => _ = svc.Get<BuildsViewModel>().LoadAsync(),
            ["mode"] = () => _ = svc.Get<ModeViewModel>().LoadAsync(),
            ["fixes"] = () => _ = svc.Get<FixesViewModel>().LoadAsync(),
            ["plugin"] = () => _ = svc.Get<PluginViewModel>().LoadAsync(),
            ["settings"] = () => svc.Get<SettingsViewModel>().OnViewLoaded(),
            ["downloads"] = () => { },
        };

        // Onboarding overlay visibility follows the VM (blurs the nav like the original).
        vm.Onboarding.PropertyChanged += (_, e) =>
        {
            if (e.PropertyName == nameof(OnboardingViewModel.IsOpen))
                OnboardingOverlay.IsVisible = vm.Onboarding.IsOpen;
        };
        OnboardingOverlay.IsVisible = vm.Onboarding.IsOpen;

        Closing += (_, e) =>
        {
            if (_reallyExiting) return;
            if (_svc.Settings.MinimizeToTray)
            {
                e.Cancel = true;
                Hide();
            }
        };

        Opened += (_, _) =>
        {
            NavigateTo("home");
        };
    }

    private void TryLoadIcon()
    {
        try
        {
            string exeDir = AppContext.BaseDirectory;
            string iconPath = System.IO.Path.Combine(exeDir, "luatools-nova.png");
            if (System.IO.File.Exists(iconPath))
                Icon = new WindowIcon(new Bitmap(iconPath));
        }
        catch { /* icon is cosmetic */ }
    }

    public void NavigateTo(string pageKey)
    {
        if (!_pages.TryGetValue(pageKey, out var page)) pageKey = "home";
        if (!_pages.TryGetValue(pageKey, out page)) return;

        foreach (var (key, control) in _pages)
            control.IsVisible = key == pageKey;

        // Select the matching nav item (footer items included).
        foreach (var item in EnumerateNavItems(Nav.MenuItems))
            if ((string?)item.Tag == pageKey) { Nav.SelectedItem = item; break; }
        foreach (var item in EnumerateNavItems(Nav.FooterMenuItems))
            if ((string?)item.Tag == pageKey) { Nav.SelectedItem = item; break; }

        if (_pageLoads.TryGetValue(pageKey, out var load))
        {
            try { load(); } catch { /* a page failing to load must not kill the shell */ }
        }
    }

    private static IEnumerable<NavigationViewItem> EnumerateNavItems(
        System.Collections.IEnumerable? items)
    {
        if (items is null) yield break;
        foreach (var obj in items)
            if (obj is NavigationViewItem nvi) yield return nvi;
    }

    private void OnNavSelectionChanged(object? sender, NavigationViewSelectionChangedEventArgs args)
    {
        if (args.SelectedItem is not NavigationViewItem item) return;
        if (item.Tag is not string key) return;

        foreach (var (key2, control) in _pages)
            control.IsVisible = key2 == key;

        if (_pageLoads.TryGetValue(key, out var load))
        {
            try { load(); } catch { /* same guard as NavigateTo */ }
        }
    }

    // ── Dialog helpers (the MessageBox replacements used by the VM layer) ──

    public Task<bool> ConfirmAsync(string title, string body)
    {
        var tcs = new TaskCompletionSource<bool>();
        var dialog = new ContentDialog
        {
            Title = title,
            Content = body,
            PrimaryButtonText = "OK",
            CloseButtonText = "Cancel",
            DefaultButton = ContentDialogButton.Primary,
        };
        _ = ShowDialogAsync(dialog, tcs);
        return tcs.Task;
    }

    public Task InfoAsync(string title, string body)
    {
        var tcs = new TaskCompletionSource<bool>();
        var dialog = new ContentDialog
        {
            Title = title,
            Content = body,
            CloseButtonText = "OK",
            DefaultButton = ContentDialogButton.Close,
        };
        _ = ShowDialogAsync(dialog, tcs);
        return tcs.Task;
    }

    private async Task ShowDialogAsync(ContentDialog dialog, TaskCompletionSource<bool> tcs)
    {
        try
        {
            var result = await dialog.ShowAsync(this);
            tcs.TrySetResult(result == ContentDialogResult.Primary);
        }
        catch
        {
            tcs.TrySetResult(false);
        }
    }

    public bool CopyToClipboard(string text)
    {
        try
        {
            if (Clipboard is { } clipboard)
            {
                clipboard.SetTextAsync(text).Wait(2000);
                return true;
            }
            return false;
        }
        catch { return false; }
    }

    public async Task<string?> PickFolderAsync(string title, string? initial)
    {
        try
        {
            var storage = StorageProvider;
            var start = initial is not null && System.IO.Directory.Exists(initial)
                ? await storage.TryGetFolderFromPathAsync(new Uri(initial)) : null;
            var folders = await storage.OpenFolderPickerAsync(new Avalonia.Platform.Storage.FolderPickerOpenOptions
            {
                Title = title,
                AllowMultiple = false,
                SuggestedStartLocation = start,
            });
            return folders.Count > 0 ? folders[0].Path.LocalPath : null;
        }
        catch { return null; }
    }

    public async Task<IReadOnlyList<string>> PickFilesAsync(string title, string[] patterns)
    {
        try
        {
            var files = await StorageProvider.OpenFilePickerAsync(new Avalonia.Platform.Storage.FilePickerOpenOptions
            {
                Title = title,
                AllowMultiple = true,
                FileTypeFilter = new[]
                {
                    new Avalonia.Platform.Storage.FilePickerFileType("LuaTools files")
                    {
                        Patterns = patterns.Length > 0 ? patterns : new[] { "*.lua", "*.manifest", "*.zip" },
                    },
                },
            });
            var paths = new List<string>();
            foreach (var f in files) paths.Add(f.Path.LocalPath);
            return paths;
        }
        catch { return Array.Empty<string>(); }
    }

    public void RequestExit()
    {
        _reallyExiting = true;
        Close();
    }

    public bool ConfirmExit()
    {
        if (!_svc.Settings.MinimizeToTray) return true;
        return _reallyExiting;
    }

    protected override void OnClosed(EventArgs e)
    {
        _reallyExiting = true;
        base.OnClosed(e);
        Avalonia.Threading.Dispatcher.UIThread.InvokeAsync(async () =>
        {
            try { await _svc.StopAsync(); } catch { }
            Avalonia.Controls.ApplicationLifetimes.IClassicDesktopStyleApplicationLifetime? lifetime =
                App.Current.ApplicationLifetime as Avalonia.Controls.ApplicationLifetimes.IClassicDesktopStyleApplicationLifetime;
            lifetime?.Shutdown();
        });
    }
}
