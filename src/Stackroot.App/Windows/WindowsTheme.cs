using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Interop;

namespace Stackroot.App.Windows;

internal static class WindowsTheme
{
    private const int DwmwaUseImmersiveDarkMode = 20;
    private const int DwmwaUseImmersiveDarkModeBefore20H1 = 19;

    private static bool _globalHookRegistered;
    private static readonly HashSet<Window> HookedWindows = [];

    public static readonly DependencyProperty EnableDarkTitleBarProperty =
        DependencyProperty.RegisterAttached(
            "EnableDarkTitleBar",
            typeof(bool),
            typeof(WindowsTheme),
            new PropertyMetadata(false, OnEnableDarkTitleBarChanged));

    [DllImport("dwmapi.dll", PreserveSig = true)]
    private static extern int DwmSetWindowAttribute(IntPtr hwnd, int attr, ref int attrValue, int attrSize);

    public static void SetEnableDarkTitleBar(DependencyObject element, bool value) =>
        element.SetValue(EnableDarkTitleBarProperty, value);

    public static bool GetEnableDarkTitleBar(DependencyObject element) =>
        (bool)element.GetValue(EnableDarkTitleBarProperty);

    public static void EnableForAllWindows()
    {
        if (_globalHookRegistered)
        {
            return;
        }

        _globalHookRegistered = true;
        EventManager.RegisterClassHandler(
            typeof(Window),
            FrameworkElement.LoadedEvent,
            new RoutedEventHandler(OnAnyWindowLoaded),
            handledEventsToo: true);
    }

    public static void HookDarkTitleBar(Window window)
    {
        if (!HookedWindows.Add(window))
        {
            return;
        }

        window.Closed += (_, _) => HookedWindows.Remove(window);
        window.SourceInitialized += (_, _) => ApplyDarkTitleBar(window);
        window.Loaded += (_, _) => ApplyDarkTitleBar(window);
        window.ContentRendered += (_, _) => ApplyDarkTitleBar(window);
    }

    public static void ApplyDarkTitleBar(Window window)
    {
        var helper = new WindowInteropHelper(window);
        var handle = helper.Handle;
        if (handle == IntPtr.Zero)
        {
            if (!window.IsInitialized)
            {
                return;
            }

            handle = helper.EnsureHandle();
        }

        if (handle == IntPtr.Zero)
        {
            return;
        }

        var enabled = 1;
        _ = DwmSetWindowAttribute(handle, DwmwaUseImmersiveDarkModeBefore20H1, ref enabled, sizeof(int));
        _ = DwmSetWindowAttribute(handle, DwmwaUseImmersiveDarkMode, ref enabled, sizeof(int));
    }

    private static void OnEnableDarkTitleBarChanged(DependencyObject d, DependencyPropertyChangedEventArgs e)
    {
        if (d is Window window && e.NewValue is true)
        {
            HookDarkTitleBar(window);
        }
    }

    private static void OnAnyWindowLoaded(object sender, RoutedEventArgs e)
    {
        if (sender is not Window window)
        {
            return;
        }

        HookDarkTitleBar(window);
        ApplyDarkTitleBar(window);
    }
}
