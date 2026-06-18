using System.Threading.Channels;
using Stackroot.Core.Abstractions;
using Stackroot.Core.Observability;

namespace Stackroot.App.Services;

/// <summary>
/// Runs background work items one at a time so startup and maintenance tasks stay ordered
/// without blocking the UI thread.
/// </summary>
public sealed class BackgroundWorkQueue : IDisposable
{
    private readonly IDiagnosticsReporter _diagnostics;
    private readonly Channel<WorkItem> _channel = Channel.CreateUnbounded<WorkItem>(
        new UnboundedChannelOptions
        {
            SingleReader = true,
            SingleWriter = false
        });
    private readonly CancellationTokenSource _lifetime = new();
    private readonly Task _pumpTask;
    private bool _disposed;

    public BackgroundWorkQueue(IDiagnosticsReporter diagnostics)
    {
        _diagnostics = diagnostics;
        _pumpTask = PumpAsync(_lifetime.Token);
    }

    public void Enqueue(string area, string action, Func<CancellationToken, Task> work, Action? onComplete = null)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        ArgumentNullException.ThrowIfNull(work);
        if (!_channel.Writer.TryWrite(new WorkItem(area, action, work, onComplete)))
        {
            throw new InvalidOperationException("Failed to enqueue background work item.");
        }
    }

    public void Dispose()
    {
        if (_disposed)
        {
            return;
        }

        _disposed = true;
        _channel.Writer.TryComplete();
        _lifetime.Cancel();
        try
        {
            _pumpTask.Wait(TimeSpan.FromSeconds(5));
        }
        catch
        {
            // Best-effort shutdown.
        }

        _lifetime.Dispose();
    }

    private async Task PumpAsync(CancellationToken cancellationToken)
    {
        try
        {
            await foreach (var item in _channel.Reader.ReadAllAsync(cancellationToken).ConfigureAwait(false))
            {
                using (_diagnostics.BeginAction(item.Area, item.Action))
                {
                    try
                    {
                        await item.Work(cancellationToken).ConfigureAwait(false);
                    }
                    catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
                    {
                        throw;
                    }
                    catch (OperationCanceledException)
                    {
                        _diagnostics.LogActivity(item.Area, $"{item.Action} cancelled");
                    }
                    catch (Exception ex)
                    {
                        _diagnostics.LogException(item.Area, ex);
                    }
                }

                try
                {
                    item.OnComplete?.Invoke();
                }
                catch (Exception ex)
                {
                    _diagnostics.LogException(item.Area, ex);
                }
            }
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            // Queue shutting down.
        }
        catch (ChannelClosedException)
        {
            // Queue completed.
        }
    }

    private sealed record WorkItem(
        string Area,
        string Action,
        Func<CancellationToken, Task> Work,
        Action? OnComplete);
}
