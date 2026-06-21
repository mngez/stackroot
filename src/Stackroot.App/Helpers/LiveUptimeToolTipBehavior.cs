using System.Windows;
using System.Windows.Controls;

namespace Stackroot.App.Helpers;

public static class LiveUptimeToolTipBehavior
{
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
            element.AddHandler(ToolTipService.ToolTipOpeningEvent, new ToolTipEventHandler(OnToolTipOpening));
            element.AddHandler(ToolTipService.ToolTipClosingEvent, new ToolTipEventHandler(OnToolTipClosing));
            return;
        }

        element.RemoveHandler(ToolTipService.ToolTipOpeningEvent, new ToolTipEventHandler(OnToolTipOpening));
        element.RemoveHandler(ToolTipService.ToolTipClosingEvent, new ToolTipEventHandler(OnToolTipClosing));
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
