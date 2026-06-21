namespace Stackroot.App.Services;

/// <summary>
/// Auto-start waits for this so service live-events reach Dashboard rows that already exist.
/// </summary>
public sealed class DashboardShellReadyGate
{
    private volatile bool _isReady;
    private readonly TaskCompletionSource _ready = new(TaskCreationOptions.RunContinuationsAsynchronously);

    public bool IsReady => _isReady;

    public void SignalReady()
    {
        if (_isReady)
        {
            return;
        }

        _isReady = true;
        _ready.TrySetResult();
    }

    public Task WaitAsync(CancellationToken cancellationToken = default)
    {
        if (_isReady)
        {
            return Task.CompletedTask;
        }

        return _ready.Task.WaitAsync(cancellationToken);
    }
}
