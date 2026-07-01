using System.Windows;
using Stackroot.App.Views;

namespace Stackroot.App.Services;

public enum BackgroundAlertKind { Success, Error, Warning, Info }

public sealed record BackgroundAlertAction(string Label, Action Execute);

public sealed record BackgroundAlert(
    BackgroundAlertKind Kind,
    string Title,
    string Message,
    Exception? Exception = null,
    string? Detail = null,
    IReadOnlyList<BackgroundAlertAction>? Actions = null);

/// <summary>
/// Lets background operations surface a result dialog to the user after they finish.
/// Call Raise() from any thread — it marshals to the UI automatically.
///
/// Usage (error):
///   _alertService.Raise(new BackgroundAlert(BackgroundAlertKind.Error, "Backup Failed",
///       $"Could not back up {site.Domain}.", ex));
///
/// Usage (success with path + action button):
///   _alertService.Raise(new BackgroundAlert(BackgroundAlertKind.Success, "Backup Complete",
///       $"{site.Domain} backed up successfully.",
///       Detail: resultPath,
///       Actions: [new BackgroundAlertAction("Open Folder", () => OpenFolder(resultPath))]));
/// </summary>
public sealed class BackgroundAlertService
{
    public void Raise(BackgroundAlert alert)
    {
        var dispatcher = Application.Current?.Dispatcher;
        if (dispatcher is null || dispatcher.HasShutdownStarted || dispatcher.HasShutdownFinished) return;

        if (dispatcher.CheckAccess())
            BackgroundAlertDialog.Show(Application.Current?.MainWindow, alert);
        else
            dispatcher.Invoke(() => BackgroundAlertDialog.Show(Application.Current?.MainWindow, alert));
    }
}
