using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Documents;
using Stackroot.Core.Abstractions;

namespace Stackroot.App.Services;

public static class UiInteractionDiagnostics
{
    private static IDiagnosticsReporter? _diagnostics;
    private static bool _registered;

    public static void Register(IDiagnosticsReporter diagnostics)
    {
        _diagnostics = diagnostics;
        if (_registered || !diagnostics.IsEnabled)
        {
            return;
        }

        EventManager.RegisterClassHandler(typeof(Button), Button.ClickEvent, new RoutedEventHandler(OnButtonClick), true);
        EventManager.RegisterClassHandler(typeof(MenuItem), MenuItem.ClickEvent, new RoutedEventHandler(OnMenuItemClick), true);
        EventManager.RegisterClassHandler(typeof(Hyperlink), Hyperlink.ClickEvent, new RoutedEventHandler(OnHyperlinkClick), true);
        EventManager.RegisterClassHandler(
            typeof(Selector),
            Selector.SelectionChangedEvent,
            new SelectionChangedEventHandler(OnSelectionChanged),
            true);
        EventManager.RegisterClassHandler(typeof(Window), FrameworkElement.LoadedEvent, new RoutedEventHandler(OnWindowLoaded), true);
        EventManager.RegisterClassHandler(typeof(UserControl), FrameworkElement.LoadedEvent, new RoutedEventHandler(OnPageLoaded), true);
        _registered = true;
    }

    private static void OnButtonClick(object sender, RoutedEventArgs e)
    {
        if (_diagnostics is null || sender is not Button button || !button.IsEnabled)
        {
            return;
        }

        Log("Click", DescribeButton(button));
    }

    private static void OnMenuItemClick(object sender, RoutedEventArgs e)
    {
        if (_diagnostics is null || sender is not MenuItem item || !item.IsEnabled)
        {
            return;
        }

        Log("Menu", DescribeObject(item.Header, item.Name, "MenuItem"));
    }

    private static void OnHyperlinkClick(object sender, RoutedEventArgs e)
    {
        if (_diagnostics is null || sender is not Hyperlink link)
        {
            return;
        }

        Log("Link", DescribeObject(link.NavigateUri?.ToString(), link.Name, "Hyperlink"));
    }

    private static void OnSelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        if (_diagnostics is null || sender is not Selector selector || !ReferenceEquals(e.OriginalSource, selector))
        {
            return;
        }

        if (selector is not ComboBox && selector is not ListBox && selector is not TabControl)
        {
            return;
        }

        var selected = selector switch
        {
            ComboBox combo => combo.SelectionBoxItem?.ToString() ?? combo.SelectedValue?.ToString(),
            _ => selector.SelectedItem?.ToString() ?? selector.SelectedValue?.ToString()
        };

        if (string.IsNullOrWhiteSpace(selected))
        {
            return;
        }

        Log("Select", $"{DescribeSelector(selector)} -> {selected}");
    }

    private static void OnWindowLoaded(object sender, RoutedEventArgs e)
    {
        if (_diagnostics is null || sender is not Window window || !ReferenceEquals(e.OriginalSource, window))
        {
            return;
        }

        var title = string.IsNullOrWhiteSpace(window.Title) ? window.GetType().Name : window.Title;
        Log("Dialog", $"Opened: {title}");
    }

    private static void OnPageLoaded(object sender, RoutedEventArgs e)
    {
        if (_diagnostics is null || sender is not UserControl control || !ReferenceEquals(e.OriginalSource, control))
        {
            return;
        }

        var ns = control.GetType().Namespace;
        if (ns is null || !ns.EndsWith(".Views.Pages", StringComparison.Ordinal))
        {
            return;
        }

        Log("Page", $"Opened: {control.GetType().Name}");
    }

    private static string DescribeButton(Button button)
    {
        var parts = new List<string>();
        var content = DescribeContent(button.Content);
        if (!string.IsNullOrWhiteSpace(content))
        {
            parts.Add(content);
        }

        if (button.Command is not null)
        {
            parts.Add(button.Command.GetType().Name);
        }

        if (button.ToolTip is not null)
        {
            parts.Add(DescribeContent(button.ToolTip));
        }

        if (!string.IsNullOrWhiteSpace(button.Name))
        {
            parts.Add(button.Name);
        }

        return parts.Count == 0 ? "Button" : string.Join(" | ", parts);
    }

    private static string DescribeSelector(Selector selector) =>
        !string.IsNullOrWhiteSpace(selector.Name)
            ? selector.Name
            : selector.GetType().Name;

    private static string DescribeObject(object? primary, string? name, string fallback)
    {
        var label = DescribeContent(primary);
        if (!string.IsNullOrWhiteSpace(label))
        {
            return label;
        }

        if (!string.IsNullOrWhiteSpace(name))
        {
            return name;
        }

        return fallback;
    }

    private static string DescribeContent(object? content) =>
        content switch
        {
            null => string.Empty,
            TextBlock textBlock => textBlock.Text,
            string text => text,
            AccessText accessText => accessText.Text,
            _ => content.ToString() ?? string.Empty
        };

    private static void Log(string action, string detail)
    {
        if (_diagnostics is null || string.IsNullOrWhiteSpace(detail))
        {
            return;
        }

        _diagnostics.LogActivity("UI", $"{action}: {detail}");
    }
}
