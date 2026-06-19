using Stackroot.App.Services;
using Stackroot.Core.Abstractions;
using Stackroot.Core.Observability;

namespace Stackroot.App.Helpers;

public readonly record struct UiActionResult<T>(bool Succeeded, T? Value);

public static class UiBackgroundAction
{
    public static async Task<UiActionResult<T>> RunAsync<T>(
        IDiagnosticsReporter diagnostics,
        string area,
        string action,
        Func<Task<T>> work,
        Action<bool> setBusy,
        Action<string>? setStatus = null,
        Action<Exception>? onError = null,
        string? busyMessage = null)
    {
        ArgumentNullException.ThrowIfNull(work);
        ArgumentNullException.ThrowIfNull(setBusy);

        setBusy(true);
        setStatus?.Invoke(busyMessage ?? action);

        BackgroundOperationTracker.BackgroundOperationLease? backgroundOperation;
        try
        {
            backgroundOperation = BackgroundOperationTracker.Enter(action);
        }
        catch (InvalidOperationException ex)
        {
            setBusy(false);
            onError?.Invoke(ex);
            return new UiActionResult<T>(false, default);
        }

        using (backgroundOperation)
        try
        {
            var value = await diagnostics.RunUserActionAsync(
                area,
                action,
                () => Task.Run(work)).ConfigureAwait(true);
            return new UiActionResult<T>(true, value);
        }
        catch (OperationCanceledException)
        {
            return new UiActionResult<T>(false, default);
        }
        catch (Exception ex)
        {
            onError?.Invoke(ex);
            return new UiActionResult<T>(false, default);
        }
        finally
        {
            setBusy(false);
        }
    }

    public static async Task<bool> RunAsync(
        IDiagnosticsReporter diagnostics,
        string area,
        string action,
        Func<Task> work,
        Action<bool> setBusy,
        Action<string>? setStatus = null,
        Action<Exception>? onError = null,
        string? busyMessage = null)
    {
        ArgumentNullException.ThrowIfNull(work);
        var result = await RunAsync<object?>(
            diagnostics,
            area,
            action,
            async () =>
            {
                await work().ConfigureAwait(false);
                return null;
            },
            setBusy,
            setStatus,
            onError,
            busyMessage).ConfigureAwait(true);
        return result.Succeeded;
    }
}
