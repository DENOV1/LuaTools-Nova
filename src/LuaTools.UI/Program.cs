using Avalonia;
using Avalonia.Controls;
using Avalonia.Media;
using System;

namespace LuaTools.UI;

internal static class Program
{
    [STAThread]
    public static void Main(string[] args)
    {
        string? protocolUrl = null;
        bool startMinimized = false;
        foreach (var arg in args)
        {
            if (arg.StartsWith("luatools://", StringComparison.OrdinalIgnoreCase) ||
                arg.StartsWith("luatoolsnova://", StringComparison.OrdinalIgnoreCase))
                protocolUrl = arg;
            else if (arg.Equals("--minimized", StringComparison.OrdinalIgnoreCase))
                startMinimized = true;
        }

        try
        {
            BuildAvaloniaApp().StartWithClassicDesktopLifetime(args);
        }
        catch (Exception ex) when (protocolUrl is not null)
        {
            // A protocol launch that fails before the window exists must not leave a zombie.
            Console.Error.WriteLine($"protocol launch failed: {ex.Message}");
        }
    }

    public static AppBuilder BuildAvaloniaApp()
        => AppBuilder.Configure<App>()
            .UsePlatformDetect()
            .WithInterFont()
            .LogToTrace();
}
