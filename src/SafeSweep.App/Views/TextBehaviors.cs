using System.Globalization;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;

namespace SafeSweep.App.Views;

/// <summary>
/// Shows a TextBlock's full text as a tooltip, but only when the text is
/// actually cut off, so short values do not sprout pointless tooltips.
/// </summary>
public static class TrimmedToolTip
{
    public static readonly DependencyProperty IsEnabledProperty = DependencyProperty.RegisterAttached(
        "IsEnabled", typeof(bool), typeof(TrimmedToolTip), new PropertyMetadata(false, OnChanged));

    public static bool GetIsEnabled(DependencyObject d) => (bool)d.GetValue(IsEnabledProperty);

    public static void SetIsEnabled(DependencyObject d, bool value) => d.SetValue(IsEnabledProperty, value);

    private static void OnChanged(DependencyObject d, DependencyPropertyChangedEventArgs e)
    {
        if (d is not TextBlock text)
        {
            return;
        }

        if (e.NewValue is true)
        {
            text.ToolTip = string.Empty; // placeholder so ToolTipOpening fires
            text.ToolTipOpening += OnToolTipOpening;
        }
        else
        {
            text.ToolTipOpening -= OnToolTipOpening;
        }
    }

    private static void OnToolTipOpening(object sender, ToolTipEventArgs e)
    {
        var text = (TextBlock)sender;
        if (string.IsNullOrEmpty(text.Text) || !IsTrimmed(text))
        {
            e.Handled = true;
            return;
        }

        text.ToolTip = text.Text;
    }

    internal static bool IsTrimmed(TextBlock text)
        => Measure(text, text.Text) > text.ActualWidth + 0.5;

    internal static double Measure(TextBlock text, string value)
    {
        var typeface = new Typeface(text.FontFamily, text.FontStyle, text.FontWeight, text.FontStretch);
        var formatted = new FormattedText(
            value,
            CultureInfo.CurrentUICulture,
            text.FlowDirection,
            typeface,
            text.FontSize,
            Brushes.Black,
            VisualTreeHelper.GetDpi(text).PixelsPerDip);
        return formatted.WidthIncludingTrailingWhitespace;
    }
}

/// <summary>
/// Shortens a file path in the MIDDLE so the drive and the file name stay
/// visible ("C:\Users\...\Temp\setup.log"), with the full path as a tooltip.
/// Bind the full path to <see cref="FullPathProperty"/>.
/// </summary>
public static class PathTrimming
{
    public static readonly DependencyProperty IsEnabledProperty = DependencyProperty.RegisterAttached(
        "IsEnabled", typeof(bool), typeof(PathTrimming), new PropertyMetadata(false, OnEnabledChanged));

    public static readonly DependencyProperty FullPathProperty = DependencyProperty.RegisterAttached(
        "FullPath", typeof(string), typeof(PathTrimming), new PropertyMetadata(null, OnFullPathChanged));

    public static bool GetIsEnabled(DependencyObject d) => (bool)d.GetValue(IsEnabledProperty);

    public static void SetIsEnabled(DependencyObject d, bool value) => d.SetValue(IsEnabledProperty, value);

    public static string? GetFullPath(DependencyObject d) => (string?)d.GetValue(FullPathProperty);

    public static void SetFullPath(DependencyObject d, string? value) => d.SetValue(FullPathProperty, value);

    private static void OnEnabledChanged(DependencyObject d, DependencyPropertyChangedEventArgs e)
    {
        if (d is not TextBlock text)
        {
            return;
        }

        text.SizeChanged -= OnSizeChanged;
        if (e.NewValue is true)
        {
            text.TextTrimming = TextTrimming.None;
            text.SizeChanged += OnSizeChanged;
            Update(text);
        }
    }

    private static void OnFullPathChanged(DependencyObject d, DependencyPropertyChangedEventArgs e)
    {
        if (d is TextBlock text)
        {
            Update(text);
        }
    }

    private static void OnSizeChanged(object sender, SizeChangedEventArgs e)
    {
        if (e.WidthChanged)
        {
            Update((TextBlock)sender);
        }
    }

    private static void Update(TextBlock text)
    {
        string full = GetFullPath(text) ?? string.Empty;
        text.ToolTip = string.IsNullOrEmpty(full) ? null : full;
        double available = text.ActualWidth;
        if (!GetIsEnabled(text) || available <= 0 || TrimmedToolTip.Measure(text, full) <= available)
        {
            text.Text = full;
            return;
        }

        text.Text = Shorten(full, candidate => TrimmedToolTip.Measure(text, candidate) <= available);
    }

    /// <summary>Drops whole folders from the middle, then characters, until it fits.</summary>
    public static string Shorten(string path, Func<string, bool> fits)
    {
        const string ellipsis = "...";
        string[] parts = path.Split('\\');
        if (parts.Length >= 3)
        {
            // Keep the first segment (drive) and the last one (file name); remove folders
            // starting just after the drive until the result fits.
            for (int removeCount = 1; removeCount <= parts.Length - 2; removeCount++)
            {
                IEnumerable<string> kept = parts.Take(1).Append(ellipsis).Concat(parts.Skip(1 + removeCount));
                string candidate = string.Join('\\', kept);
                if (fits(candidate))
                {
                    return candidate;
                }
            }
        }

        // Still too long: keep the start and the end of the text itself.
        string leaf = parts[^1];
        for (int keep = leaf.Length; keep > 4; keep--)
        {
            string candidate = parts[0] + "\\" + ellipsis + leaf[^keep..];
            if (fits(candidate))
            {
                return candidate;
            }
        }

        return ellipsis + leaf[^Math.Min(leaf.Length, 4)..];
    }
}
