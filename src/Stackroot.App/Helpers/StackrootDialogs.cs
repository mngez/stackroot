using System.Windows;
using System.Windows.Threading;
using Stackroot.App.ViewModels;
using Stackroot.App.Views;

namespace Stackroot.App.Helpers;

public static class StackrootDialogs
{
    public static void ShowError(Window? owner, string title, string message, string? details = null)
    {
        ShowOnUiThread(owner, () =>
            MessageDialog.Show(owner, title, message, StackrootDialogKind.Error, details: details));
    }

    public static void ShowWarning(Window? owner, string title, string message, string? details = null)
    {
        ShowOnUiThread(owner, () =>
            MessageDialog.Show(owner, title, message, StackrootDialogKind.Warning, details: details));
    }

    public static void ShowInfo(Window? owner, string title, string message, string? details = null)
    {
        ShowOnUiThread(owner, () =>
            MessageDialog.Show(owner, title, message, StackrootDialogKind.Info, details: details));
    }

    public static bool Confirm(Window? owner, string title, string message, string confirmText = "Confirm", bool isDanger = false)
    {
        return ShowOnUiThread(owner, () => ConfirmDialog.Show(owner, title, message, confirmText, isDanger));
    }

    public static bool ConfirmWarning(Window? owner, string title, string message, string yesText = "Yes")
    {
        return ShowOnUiThread(owner, () =>
            MessageDialog.Show(
                owner,
                title,
                message,
                StackrootDialogKind.Warning,
                StackrootDialogButtons.YesNo,
                yesText: yesText) == StackrootDialogResult.Yes);
    }

    public static StackrootDialogResult AskCloseBehavior(Window? owner)
    {
        return ShowOnUiThread(owner, () =>
            MessageDialog.Show(
                owner,
                "Close Stackroot",
                "Quit stops running services. Choose Keep in tray to minimize to the notification area.",
                StackrootDialogKind.Question,
                StackrootDialogButtons.YesNoCancel,
                yesText: "Quit",
                noText: "Keep in tray",
                cancelText: "Cancel"));
    }

    private static void ShowOnUiThread(Window? owner, Action show)
    {
        var dispatcher = ResolveDispatcher(owner);
        if (dispatcher is null)
        {
            return;
        }

        if (dispatcher.CheckAccess())
        {
            show();
            return;
        }

        dispatcher.Invoke(show);
    }

    private static T? ShowOnUiThread<T>(Window? owner, Func<T> show)
    {
        var dispatcher = ResolveDispatcher(owner);
        if (dispatcher is null)
        {
            return default;
        }

        return dispatcher.CheckAccess() ? show() : dispatcher.Invoke(show);
    }

    private static Dispatcher? ResolveDispatcher(Window? owner)
    {
        if (owner?.Dispatcher is { HasShutdownStarted: false, HasShutdownFinished: false } ownerDispatcher)
        {
            return ownerDispatcher;
        }

        var app = Application.Current;
        if (app?.Dispatcher is { HasShutdownStarted: false, HasShutdownFinished: false } appDispatcher)
        {
            return appDispatcher;
        }

        return null;
    }
}
