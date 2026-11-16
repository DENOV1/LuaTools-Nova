// LuaTools Nova — Avalonia implementations of the UI-facing service interfaces
// (dialogs, toasts, navigation marshalling).
using Avalonia.Controls;
using Avalonia.Controls.Notifications;
using Avalonia.Media;
using FluentAvalonia.UI.Controls;
using LuaTools.Core.Services;
using System;
using System.Threading.Tasks;

namespace LuaTools.UI.Services;

public sealed class DialogServiceImpl : IDialogService
{
    public bool Confirm(string title, string body)
    {
        var window = App.Main;
        if (window is null) return false;
        return window.ConfirmAsync(title, body).GetAwaiter().GetResult();
    }

    public bool ConfirmWithCancel(string title, string body) => Confirm(title, body);

    public void Info(string title, string body)
    {
        var window = App.Main;
        if (window is null) return;
        window.InfoAsync(title, body).GetAwaiter().GetResult();
    }
}

public sealed class ToastServiceImpl : IToastService
{
    private WindowNotificationManager? _manager;

    public void Attach(Window window)
    {
        _manager = new WindowNotificationManager(window)
        {
            Position = NotificationPosition.BottomRight,
            MaxItems = 4,
            Margin = new Avalonia.Thickness(0, 0, 24, 18),
        };
    }

    public void Show(string title, string message, bool error = false)
    {
        Avalonia.Threading.Dispatcher.UIThread.Post(() =>
        {
            try
            {
                _manager?.Show(new Notification(title, message,
                    error ? NotificationType.Error : NotificationType.Information,
                    TimeSpan.FromSeconds(3)));
            }
            catch { /* a toast must never crash the caller */ }
        });
    }

    public void ShowAction(string title, string message, string actionLabel, Action onAction, bool error = false)
    {
        Avalonia.Threading.Dispatcher.UIThread.Post(() =>
        {
            try
            {
                _manager?.Show(new Notification(title, message,
                    error ? NotificationType.Error : NotificationType.Information,
                    TimeSpan.FromSeconds(10),
                    onClick: onAction));
            }
            catch { /* best effort */ }
        });
    }
}
