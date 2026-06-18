using Stackroot.App.Helpers;
using Stackroot.Core.Abstractions;
using Stackroot.Core.Observability;

namespace Stackroot.App.Services;

/// <summary>
/// Writes user-facing session activity and mirrors the same events to the development report.
/// </summary>
public sealed class SessionActivityReporter
{
    private readonly SessionActivityService _activity;
    private readonly IDiagnosticsReporter _diagnostics;

    public SessionActivityReporter(SessionActivityService activity, IDiagnosticsReporter diagnostics)
    {
        _activity = activity;
        _diagnostics = diagnostics;
    }

    public void LogInfo(string area, string message)
    {
        _diagnostics.LogActivity(area, message);
        _activity.Log(message, SessionActivityTone.Info);
    }

    public void LogSuccess(string area, string message)
    {
        _diagnostics.LogActivity(area, message);
        _activity.Log(message, SessionActivityTone.Success);
    }

    public void LogError(string area, string message, Exception? exception = null)
    {
        if (exception is not null)
        {
            _diagnostics.LogException(area, exception);
        }
        else
        {
            _diagnostics.LogUserError(area, message);
        }

        _activity.Log(message, SessionActivityTone.Error);
    }

    public Guid Begin(string area, string message)
    {
        _diagnostics.LogActivity(area, message);
        return _activity.Begin(message);
    }

    public void Complete(Guid id, string area, string message, SessionActivityTone tone = SessionActivityTone.Success)
    {
        _diagnostics.LogActivity(area, message);
        _activity.Complete(id, message, tone);
    }

    public void Fail(Guid id, string area, string message, Exception? exception = null)
    {
        if (exception is not null)
        {
            _diagnostics.LogException(area, exception);
        }
        else
        {
            _diagnostics.LogUserError(area, message);
        }

        _activity.Fail(id, message);
    }

    public async Task RunAsync(
        string area,
        string progressMessage,
        Func<Task> work,
        string successMessage,
        SessionActivityTone successTone = SessionActivityTone.Success)
    {
        var id = Begin(area, progressMessage);
        try
        {
            await work().ConfigureAwait(true);
            Complete(id, area, successMessage, successTone);
        }
        catch (Exception ex)
        {
            Fail(id, area, ex.Message, ex);
            throw;
        }
    }

    public async Task<UiActionResult<T>> RunBackgroundAsync<T>(
        string area,
        string action,
        Func<Task<T>> work,
        Action<bool> setBusy,
        string successMessage,
        Action<string>? setStatus = null,
        Action<Exception>? onError = null,
        string? busyMessage = null,
        string? failureMessage = null,
        Func<T, string>? formatSuccess = null)
    {
        var progressId = Begin(area, busyMessage ?? action);
        try
        {
            var result = await UiBackgroundAction.RunAsync(
                _diagnostics,
                area,
                action,
                work,
                setBusy,
                setStatus,
                onError,
                busyMessage).ConfigureAwait(true);

            if (result.Succeeded)
            {
                var message = formatSuccess is not null && result.Value is not null
                    ? formatSuccess(result.Value)
                    : successMessage;
                Complete(progressId, area, message);
            }
            else
            {
                Fail(progressId, area, failureMessage ?? $"{action} failed.");
            }

            return result;
        }
        catch (Exception ex)
        {
            Fail(progressId, area, ex.Message, ex);
            throw;
        }
    }

    public async Task<bool> RunBackgroundAsync(
        string area,
        string action,
        Func<Task> work,
        Action<bool> setBusy,
        string successMessage,
        Action<string>? setStatus = null,
        Action<Exception>? onError = null,
        string? busyMessage = null,
        string? failureMessage = null)
    {
        var result = await RunBackgroundAsync<object?>(
            area,
            action,
            async () =>
            {
                await work().ConfigureAwait(false);
                return null;
            },
            setBusy,
            successMessage,
            setStatus,
            onError,
            busyMessage,
            failureMessage).ConfigureAwait(true);
        return result.Succeeded;
    }
}
