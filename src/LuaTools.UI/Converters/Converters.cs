using Avalonia;
using Avalonia.Data.Converters;
using Avalonia.Media;
using System;
using System.Globalization;

namespace LuaTools.UI.Converters;

/// <summary>True → error red, false → body gray (result banners).</summary>
public sealed class BoolToErrorBrushConverter : IValueConverter
{
    public static readonly BoolToErrorBrushConverter Instance = new();

    public object Convert(object? value, Type targetType, object? parameter, CultureInfo culture)
        => value is true
            ? new SolidColorBrush(Color.Parse("#f87171"))
            : new SolidColorBrush(Color.Parse("#34d399"));

    public object ConvertBack(object? value, Type targetType, object? parameter, CultureInfo culture)
        => throw new NotSupportedException();
}

/// <summary>Value ≠ null → visible (InverseBoolToVis / NullToCollapsed).</summary>
public sealed class NotNullToBoolConverter : IValueConverter
{
    public static readonly NotNullToBoolConverter Instance = new();

    public object Convert(object? value, Type targetType, object? parameter, CultureInfo culture)
        => value is not null;

    public object ConvertBack(object? value, Type targetType, object? parameter, CultureInfo culture)
        => throw new NotSupportedException();
}

public sealed class InverseBoolConverter : IValueConverter
{
    public static readonly InverseBoolConverter Instance = new();

    public object Convert(object? value, Type targetType, object? parameter, CultureInfo culture)
        => value is not true;

    public object ConvertBack(object? value, Type targetType, object? parameter, CultureInfo culture)
        => throw new NotSupportedException();
}

/// <summary>Multi-bind equality for the pager's active page (0 = ellipsis sentinel).</summary>
public sealed class EqualityMultiConverter : IMultiValueConverter
{
    public static readonly EqualityMultiConverter Instance = new();

    public object Convert(IList<object?> values, Type targetType, object? parameter, CultureInfo culture)
    {
        if (values is null || values.Count < 2 || values[0] is null || values[1] is null) return false;
        return Equals(values[0], values[1]);
    }
}

public sealed class BytesToSizeConverter : IValueConverter
{
    public static readonly BytesToSizeConverter Instance = new();

    public object Convert(object? value, Type targetType, object? parameter, CultureInfo culture)
    {
        if (value is not long b || b <= 0) return "";
        double gb = b / 1024d / 1024d / 1024d;
        if (gb >= 1) return gb.ToString("0.##") + " GB";
        double mb = b / 1024d / 1024d;
        if (mb >= 1) return mb.ToString("0.#") + " MB";
        double kb = b / 1024d;
        if (kb >= 1) return kb.ToString("0") + " KB";
        return b.ToString("0") + " B";
    }

    public object ConvertBack(object? value, Type targetType, object? parameter, CultureInfo culture)
        => throw new NotSupportedException();
}

/// <summary>String is not null/whitespace → true (visible rows for optional text).</summary>
public sealed class StringNotEmptyConverter : IValueConverter
{
    public static readonly StringNotEmptyConverter Instance = new();

    public object Convert(object? value, Type targetType, object? parameter, CultureInfo culture)
        => !string.IsNullOrWhiteSpace(value as string);

    public object ConvertBack(object? value, Type targetType, object? parameter, CultureInfo culture)
        => throw new NotSupportedException();
}

/// <summary>Value ≠ 0 → true (pager: 0 is the ellipsis sentinel).</summary>
public sealed class NotDefaultConverter : IValueConverter
{
    public static readonly NotDefaultConverter Instance = new();

    public object Convert(object? value, Type targetType, object? parameter, CultureInfo culture)
    {
        if (value is int i) return i != 0;
        if (value is long l) return l != 0;
        return value is not null;
    }

    public object ConvertBack(object? value, Type targetType, object? parameter, CultureInfo culture)
        => throw new NotSupportedException();
}
