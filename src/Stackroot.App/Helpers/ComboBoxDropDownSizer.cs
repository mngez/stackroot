using System.Globalization;
using System.Reflection;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Media;

namespace Stackroot.App.Helpers;

public static class ComboBoxDropDownSizer
{
    private const double ExtraHorizontalPadding = 36;

    public static readonly DependencyProperty IsEnabledProperty =
        DependencyProperty.RegisterAttached(
            "IsEnabled",
            typeof(bool),
            typeof(ComboBoxDropDownSizer),
            new PropertyMetadata(false, OnIsEnabledChanged));

    public static bool GetIsEnabled(DependencyObject obj) => (bool)obj.GetValue(IsEnabledProperty);

    public static void SetIsEnabled(DependencyObject obj, bool value) => obj.SetValue(IsEnabledProperty, value);

    private static void OnIsEnabledChanged(DependencyObject d, DependencyPropertyChangedEventArgs e)
    {
        if (d is not ComboBox combo)
        {
            return;
        }

        if ((bool)e.NewValue)
        {
            combo.DropDownOpened += OnDropDownOpened;
        }
        else
        {
            combo.DropDownOpened -= OnDropDownOpened;
        }
    }

    private static void OnDropDownOpened(object? sender, EventArgs e)
    {
        if (sender is not ComboBox combo)
        {
            return;
        }

        combo.ApplyTemplate();
        if (combo.Template.FindName("PART_Popup", combo) is not Popup popup)
        {
            return;
        }

        if (popup.Child is not FrameworkElement root)
        {
            return;
        }

        root.MinWidth = Math.Max(combo.ActualWidth, MeasureItemsWidth(combo));
    }

    private static double MeasureItemsWidth(ComboBox combo)
    {
        var typeface = new Typeface(
            combo.FontFamily,
            combo.FontStyle,
            combo.FontWeight,
            combo.FontStretch);
        var pixelsPerDip = VisualTreeHelper.GetDpi(combo).PixelsPerDip;
        var max = 0d;

        foreach (var item in combo.Items)
        {
            var text = ResolveItemText(combo, item);
            if (string.IsNullOrWhiteSpace(text))
            {
                continue;
            }

            var formatted = new FormattedText(
                text,
                CultureInfo.CurrentCulture,
                FlowDirection.LeftToRight,
                typeface,
                combo.FontSize,
                System.Windows.Media.Brushes.White,
                pixelsPerDip);
            max = Math.Max(max, formatted.WidthIncludingTrailingWhitespace);
        }

        return max + ExtraHorizontalPadding;
    }

    private static string ResolveItemText(ComboBox combo, object? item)
    {
        if (item is null)
        {
            return string.Empty;
        }

        if (string.IsNullOrWhiteSpace(combo.DisplayMemberPath))
        {
            return item.ToString() ?? string.Empty;
        }

        var property = item.GetType().GetProperty(
            combo.DisplayMemberPath,
            BindingFlags.Instance | BindingFlags.Public | BindingFlags.IgnoreCase);
        return property?.GetValue(item)?.ToString() ?? string.Empty;
    }
}
