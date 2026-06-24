namespace Stackroot.App.Services;

/// <summary>
/// Signals when startup activity has finished and enabled services are settled.
/// Scheduled tasks wait for this before running.
/// </summary>
public sealed class StackrootStartupReadyGate
{
    private volatile bool _isReady;
    private readonly TaskCompletionSource _ready = new(TaskCreationOptions.RunContinuationsAsynchronously);

    public bool IsReady => _isReady;

    public event Action? Ready;

    public void SignalReady()
    {
        if (_isReady)
        {
            return;
        }

        _isReady = true;
        _ready.TrySetResult();
        try
        {
            Ready?.Invoke();
        }
        catch
        {
            // Subscribers are best-effort.
        }
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
