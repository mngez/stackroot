using System.Windows;
using Stackroot.App.Services;

namespace Stackroot.App.Helpers;

public static class SettingsSaveFeedback
{
    public readonly record struct DeferredSettingsSave(
        string SavingMessage,
        string SuccessMessage,
        Func<Task>? Work = null);

    public static async Task RunAsync(
        Action<string> setStatus,
        string savingMessage,
        string successMessage,
        Func<Task> work)
    {
        await SetStatusAsync(setStatus, savingMessage).ConfigureAwait(true);
        try
        {
            await work().ConfigureAwait(true);
            await SetStatusAsync(setStatus, successMessage).ConfigureAwait(true);
        }
        catch (Exception ex)
        {
            await SetStatusAsync(setStatus, ex.Message).ConfigureAwait(true);
        }
    }

    public static Task RunDeferredAsync(
        Action<string> setStatus,
        DeferredSettingsSave deferred,
        Func<Task>? fallbackWork = null) =>
        RunAsync(
            setStatus,
            deferred.SavingMessage,
            deferred.SuccessMessage,
            deferred.Work ?? fallbackWork ?? (() => Task.CompletedTask));

    public static Task RunDeferredOnSessionActivityAsync(
        SessionActivityReporter reporter,
        DeferredSettingsSave deferred,
        Func<Task>? fallbackWork = null)
    {
        var work = deferred.Work ?? fallbackWork ?? (() => Task.CompletedTask);
        return reporter.RunAsync("Settings", deferred.SavingMessage, work, deferred.SuccessMessage);
    }

    public static Task RunDeferredOnSessionActivityAsync(
        SessionActivityService activity,
        DeferredSettingsSave deferred,
        Func<Task>? fallbackWork = null)
    {
        var work = deferred.Work ?? fallbackWork ?? (() => Task.CompletedTask);
        return activity.RunAsync(deferred.SavingMessage, work, deferred.SuccessMessage);
    }

    public static Task ShowAsync(Action<string> setStatus, string message) =>
        SetStatusAsync(setStatus, message);

    private static Task SetStatusAsync(Action<string> setStatus, string message)
    {
        var dispatcher = Application.Current?.Dispatcher;
        if (dispatcher is null || dispatcher.CheckAccess())
        {
            setStatus(message);
            return Task.CompletedTask;
        }

        return dispatcher.InvokeAsync(() => setStatus(message)).Task;
    }
}
