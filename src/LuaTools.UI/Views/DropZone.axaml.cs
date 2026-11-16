using Avalonia.Controls;
using Avalonia.Platform.Storage;
using Avalonia.Input;
using LuaTools.Application.ViewModels;
using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Threading.Tasks;

namespace LuaTools.UI.Views;

public partial class DropZone : UserControl
{
    public DropZone()
    {
        InitializeComponent();

        AddHandler(DragDrop.DragEnterEvent, OnDragEnter);
        AddHandler(DragDrop.DragLeaveEvent, OnDragLeave);
        AddHandler(DragDrop.DropEvent, OnDrop);
    }

    private DropInstallViewModel? Vm => DataContext as DropInstallViewModel;

    private void OnDragEnter(object? sender, DragEventArgs e)
    {
        if (Vm is null) return;
        Vm.IsDragOver = true;
        DropBox.Background = new Avalonia.Media.SolidColorBrush(
            Avalonia.Media.Color.Parse("#207c3aed"));
        DropBox.BorderBrush = new Avalonia.Media.SolidColorBrush(
            Avalonia.Media.Color.Parse("#a78bfa"));
    }

    private void OnDragLeave(object? sender, DragEventArgs e)
    {
        if (Vm is null) return;
        Vm.IsDragOver = false;
        DropBox.Background = new Avalonia.Media.SolidColorBrush(
            Avalonia.Media.Color.Parse("#0AFFFFFF"));
        DropBox.BorderBrush = new Avalonia.Media.SolidColorBrush(
            Avalonia.Media.Color.Parse("#28FFFFFF"));
    }

    private async void OnDrop(object? sender, DragEventArgs e)
    {
        OnDragLeave(sender, e);
        if (Vm is null) return;

        // A dropped LINK (SteamDB / store URL) installs that appid.
        var text = e.Data?.GetText();
        if (!string.IsNullOrWhiteSpace(text) && await Vm.TryHandleLinkAsync(text)) return;

        var files = e.Data?.GetFiles();
        if (files is null) return;

        var paths = files
            .Select(f => f.TryGetLocalPath())
            .Where(p => !string.IsNullOrEmpty(p))
            .Cast<string>()
            .ToList();

        if (paths.Count > 0)
            await Vm.HandleDropAsync(paths);
    }

    private async void OnBrowseClick(object? sender, Avalonia.Interactivity.RoutedEventArgs e)
    {
        if (Vm is null) return;
        var window = App.Main;
        if (window is null) return;

        var files = await window.PickFilesAsync("Choose lua, manifest or zip files", new[] { "*.lua", "*.manifest", "*.zip" });
        if (files.Count > 0)
            await Vm.HandleDropAsync(files);
    }
}
