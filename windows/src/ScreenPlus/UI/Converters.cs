using System.Globalization;
using System.Windows;
using System.Windows.Data;

namespace ScreenPlus.UI;

public sealed class BoolToVisibilityConverter : IValueConverter
{
    public bool Invert { get; set; }

    public object Convert(object? value, Type targetType, object? parameter, CultureInfo culture) =>
        (value is true) != Invert ? Visibility.Visible : Visibility.Collapsed;

    public object ConvertBack(object? value, Type targetType, object? parameter, CultureInfo culture) =>
        (value is Visibility.Visible) != Invert;
}

public sealed class NullToVisibilityConverter : IValueConverter
{
    public object Convert(object? value, Type targetType, object? parameter, CultureInfo culture) =>
        value is null or "" ? Visibility.Collapsed : Visibility.Visible;

    public object ConvertBack(object? value, Type targetType, object? parameter, CultureInfo culture) =>
        throw new NotSupportedException();
}

/// <summary>Multiplies a number, e.g. 0.5 → 50 for percentages.</summary>
public sealed class ScaleConverter : IValueConverter
{
    public double Factor { get; set; } = 1;

    public object Convert(object? value, Type targetType, object? parameter, CultureInfo culture) =>
        value is double d ? d * Factor : 0.0;

    public object ConvertBack(object? value, Type targetType, object? parameter, CultureInfo culture) =>
        value is double d ? d / Factor : 0.0;
}
