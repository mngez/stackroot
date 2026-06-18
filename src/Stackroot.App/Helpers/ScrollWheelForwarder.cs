using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Media.Media3D;

namespace Stackroot.App.Helpers;

public static class ScrollWheelForwarder
{
    private const double PixelsPerWheelNotch = 48;

    public static void OnPreviewMouseWheel(object sender, MouseWheelEventArgs e)
    {
        if (e.Handled || ShouldLeaveForInteractiveInput(e.OriginalSource as DependencyObject))
        {
            return;
        }

        var origin = e.OriginalSource as DependencyObject;
        var shell = FindOutermostScrollViewer(origin);
        if (shell is null || shell.ScrollableHeight <= 0)
        {
            return;
        }

        if (HasActiveNestedScrollViewer(origin, shell, e))
        {
            return;
        }

        ScrollShell(shell, e);
    }

    private static bool ShouldLeaveForInteractiveInput(DependencyObject? origin)
    {
        for (var current = origin; current != null; current = GetParentForTreeWalk(current))
        {
            if (current is TextBoxBase { IsReadOnly: false })
            {
                return true;
            }

            if (current is ComboBox { IsDropDownOpen: true })
            {
                return true;
            }

            if (current is ListBox { IsFocused: true })
            {
                return true;
            }
        }

        return false;
    }

    private static bool HasActiveNestedScrollViewer(
        DependencyObject? origin,
        ScrollViewer shell,
        MouseWheelEventArgs e)
    {
        for (var current = origin; current != null && !ReferenceEquals(current, shell); current = GetParentForTreeWalk(current))
        {
            if (current is not ScrollViewer viewer || !IsActiveScrollViewer(viewer))
            {
                continue;
            }

            if (e.Delta < 0 && viewer.VerticalOffset < viewer.ScrollableHeight)
            {
                return true;
            }

            if (e.Delta > 0 && viewer.VerticalOffset > 0)
            {
                return true;
            }
        }

        return false;
    }

    private static bool IsActiveScrollViewer(ScrollViewer viewer)
    {
        if (viewer.VerticalScrollBarVisibility == ScrollBarVisibility.Disabled)
        {
            return false;
        }

        if (viewer.ScrollableHeight <= 0)
        {
            return false;
        }

        if (FindAncestor<DataGrid>(viewer) is not null)
        {
            return false;
        }

        return true;
    }

    private static void ScrollShell(ScrollViewer shell, MouseWheelEventArgs e)
    {
        var wheelNotches = e.Delta / (double)Mouse.MouseWheelDeltaForOneLine;
        var scrollBy = wheelNotches * PixelsPerWheelNotch;
        var next = shell.VerticalOffset - scrollBy;
        if (next < 0)
        {
            next = 0;
        }
        else if (next > shell.ScrollableHeight)
        {
            next = shell.ScrollableHeight;
        }

        shell.ScrollToVerticalOffset(next);
        e.Handled = true;
    }

    private static ScrollViewer? FindOutermostScrollViewer(DependencyObject? start)
    {
        ScrollViewer? outermost = null;
        for (var current = start; current != null; current = GetParentForTreeWalk(current))
        {
            if (current is ScrollViewer viewer)
            {
                outermost = viewer;
            }
        }

        return outermost;
    }

    private static T? FindAncestor<T>(DependencyObject? start)
        where T : DependencyObject
    {
        for (var current = start; current != null; current = GetParentForTreeWalk(current))
        {
            if (current is T match)
            {
                return match;
            }
        }

        return null;
    }

    private static DependencyObject? GetParentForTreeWalk(DependencyObject current)
    {
        if (current is Visual or Visual3D)
        {
            return VisualTreeHelper.GetParent(current);
        }

        if (current is FrameworkContentElement { Parent: DependencyObject parent })
        {
            return parent;
        }

        return LogicalTreeHelper.GetParent(current) as DependencyObject;
    }
}
