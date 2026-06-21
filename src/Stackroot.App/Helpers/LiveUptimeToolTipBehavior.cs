using System.Windows;
using System.Windows.Controls;

namespace Stackroot.App.Helpers;

public static class LiveUptimeToolTipBehavior
{
    private static readonly ToolTipEventHandler OpeningHandler = OnToolTipOpening;
    private static readonly ToolTipEventHandler ClosingHandler = OnToolTipClosing;

    public static readonly DependencyProperty IsEnabledProperty =
        DependencyProperty.RegisterAttached(
            "IsEnabled",
            typeof(bool),
            typeof(LiveUptimeToolTipBehavior),
            new PropertyMetadata(false, OnIsEnabledChanged));

    public static bool GetIsEnabled(DependencyObject element) =>
        (bool)element.GetValue(IsEnabledProperty);

    public static void SetIsEnabled(DependencyObject element, bool value) =>
        element.SetValue(IsEnabledProperty, value);

    private static void OnIsEnabledChanged(DependencyObject d, DependencyPropertyChangedEventArgs e)
    {
        if (d is not FrameworkElement element)
        {
            return;
        }

        if (e.NewValue is true)
        {
            element.AddHandler(ToolTipService.ToolTipOpeningEvent, OpeningHandler);
            element.AddHandler(ToolTipService.ToolTipClosingEvent, ClosingHandler);
            return;
        }

        element.RemoveHandler(ToolTipService.ToolTipOpeningEvent, OpeningHandler);
        element.RemoveHandler(ToolTipService.ToolTipClosingEvent, ClosingHandler);
    }

    private static void OnToolTipOpening(object sender, ToolTipEventArgs e)
    {
        if (sender is not FrameworkElement element || !GetIsEnabled(element))
        {
            return;
        }

        if (element.DataContext is IUptimeTooltipTarget target)
        {
            UptimeToolTipTicker.Register(target);
        }
    }

    private static void OnToolTipClosing(object sender, ToolTipEventArgs e)
    {
        if (sender is not FrameworkElement element || !GetIsEnabled(element))
        {
            return;
        }

        if (element.DataContext is IUptimeTooltipTarget target)
        {
            UptimeToolTipTicker.Unregister(target);
        }
    }
}
