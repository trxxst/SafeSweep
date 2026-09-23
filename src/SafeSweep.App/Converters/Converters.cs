using System.Globalization;
using System.Windows;
using System.Windows.Data;
using System.Windows.Media;
using SafeSweep.Core.Models;

namespace SafeSweep.App.Converters;

public sealed class BytesConverter : IValueConverter
{
    public object Convert(object value, Type targetType, object parameter, CultureInfo culture)
        => value is long bytes ? Formatting.Bytes(bytes) : value is int i ? Formatting.Bytes(i) : string.Empty;

    public object ConvertBack(object value, Type targetType, object parameter, CultureInfo culture) => Binding.DoNothing;
}

public sealed class LevelToTextConverter : IValueConverter
{
    public static string Text(CleanLevel level) => level switch
    {
        CleanLevel.Safe => "Safe Clean",
        CleanLevel.Deep => "Deep Clean",
        _ => "Manual Review",
    };

    public static string Explanation(CleanLevel level) => level switch
    {
        CleanLevel.Safe => "Regenerated automatically; never your data. Selected for you and deleted outright.",
        CleanLevel.Deep => "Caches, logs and leftovers that deserve a look. Not selected for you; quarantined so they can be restored (emptying the Recycle Bin is the one permanent exception).",
        _ => "Uncertain items. Nothing is selected for you; review every item. Quarantined or sent to the Recycle Bin, never deleted outright.",
    };

    public object Convert(object value, Type targetType, object parameter, CultureInfo culture)
    {
        CleanLevel? level = value switch
        {
            CleanLevel l => l,
            CollectionViewGroup g when g.Name is CleanLevel gl => gl,
            _ => null,
        };

        if (level is null)
        {
            return string.Empty;
        }

        return parameter as string == "explain" ? Explanation(level.Value) : Text(level.Value);
    }

    public object ConvertBack(object value, Type targetType, object parameter, CultureInfo culture) => Binding.DoNothing;
}

public sealed class RiskToTextConverter : IValueConverter
{
    public object Convert(object value, Type targetType, object parameter, CultureInfo culture) => value switch
    {
        RiskLevel.VeryLow => "Very low risk",
        RiskLevel.Low => "Low risk",
        RiskLevel.Moderate => "Moderate risk",
        RiskLevel.High => "High risk",
        _ => string.Empty,
    };

    public object ConvertBack(object value, Type targetType, object parameter, CultureInfo culture) => Binding.DoNothing;
}

/// <summary>Risk (or level) to a theme brush: green, amber or red.</summary>
public sealed class RiskToBrushConverter : IValueConverter
{
    public object Convert(object value, Type targetType, object parameter, CultureInfo culture)
    {
        string key = value switch
        {
            RiskLevel.VeryLow or RiskLevel.Low or CleanLevel.Safe => "SystemFillColorSuccessBrush",
            RiskLevel.Moderate or CleanLevel.Deep => "SystemFillColorCautionBrush",
            RiskLevel.High or CleanLevel.ManualReview => "SystemFillColorCriticalBrush",
            _ => "TextFillColorSecondaryBrush",
        };

        return Application.Current.TryFindResource(key) as Brush ?? Brushes.Gray;
    }

    public object ConvertBack(object value, Type targetType, object parameter, CultureInfo culture) => Binding.DoNothing;
}

public sealed class MethodToTextConverter : IValueConverter
{
    public object Convert(object value, Type targetType, object parameter, CultureInfo culture) => value switch
    {
        DeletionMethod.Permanent => "Deleted",
        DeletionMethod.Quarantine => "Quarantined (restorable)",
        DeletionMethod.RecycleBin => "Recycle Bin",
        _ => string.Empty,
    };

    public object ConvertBack(object value, Type targetType, object parameter, CultureInfo culture) => Binding.DoNothing;
}

public sealed class BoolToVisibilityConverter : IValueConverter
{
    public bool Invert { get; set; }

    public object Convert(object value, Type targetType, object parameter, CultureInfo culture)
    {
        bool flag = value is true;
        if (Invert)
        {
            flag = !flag;
        }

        return flag ? Visibility.Visible : Visibility.Collapsed;
    }

    public object ConvertBack(object value, Type targetType, object parameter, CultureInfo culture)
        => value is Visibility v && (v == Visibility.Visible) != Invert;
}

public sealed class NullToVisibilityConverter : IValueConverter
{
    public bool Invert { get; set; }

    public object Convert(object value, Type targetType, object parameter, CultureInfo culture)
    {
        bool hasValue = value is not null && (value is not string s || s.Length > 0);
        if (Invert)
        {
            hasValue = !hasValue;
        }

        return hasValue ? Visibility.Visible : Visibility.Collapsed;
    }

    public object ConvertBack(object value, Type targetType, object parameter, CultureInfo culture) => Binding.DoNothing;
}

public sealed class CountToVisibilityConverter : IValueConverter
{
    public bool Invert { get; set; }

    public object Convert(object value, Type targetType, object parameter, CultureInfo culture)
    {
        bool any = value switch
        {
            int i => i > 0,
            long l => l > 0,
            _ => false,
        };

        if (Invert)
        {
            any = !any;
        }

        return any ? Visibility.Visible : Visibility.Collapsed;
    }

    public object ConvertBack(object value, Type targetType, object parameter, CultureInfo culture) => Binding.DoNothing;
}

/// <summary>
/// Keeps the vertical scroll bar's column reserved while scrolling is set to
/// Auto, so content does not jump sideways when the bar appears.
/// values[0] = ComputedVerticalScrollBarVisibility, values[1] = VerticalScrollBarVisibility.
/// </summary>
public sealed class ScrollBarGutterConverter : IMultiValueConverter
{
    public object Convert(object[] values, Type targetType, object parameter, CultureInfo culture)
    {
        if (values.Length >= 1 && values[0] is Visibility computed && computed == Visibility.Visible)
        {
            return Visibility.Visible;
        }

        return values.Length >= 2 && values[1] is System.Windows.Controls.ScrollBarVisibility setting
               && setting == System.Windows.Controls.ScrollBarVisibility.Auto
            ? Visibility.Hidden
            : Visibility.Collapsed;
    }

    public object[] ConvertBack(object value, Type[] targetTypes, object parameter, CultureInfo culture)
        => throw new NotSupportedException();
}

/// <summary>true -> first length in the parameter, false -> second ("64|248").</summary>
public sealed class BoolToGridLengthConverter : IValueConverter
{
    public object Convert(object value, Type targetType, object parameter, CultureInfo culture)
    {
        string[] parts = (parameter as string ?? "0|0").Split('|');
        double length = double.Parse(value is true ? parts[0] : parts[1], CultureInfo.InvariantCulture);
        return new GridLength(length);
    }

    public object ConvertBack(object value, Type targetType, object parameter, CultureInfo culture) => Binding.DoNothing;
}

public sealed class InverseBoolConverter : IValueConverter
{
    public object Convert(object value, Type targetType, object parameter, CultureInfo culture) => value is not true;

    public object ConvertBack(object value, Type targetType, object parameter, CultureInfo culture) => value is not true;
}
