namespace Stackroot.App.Services;

public sealed class DeferredStartupCoordinator
{
    private readonly CancellationTokenSource _shutdown = new();

    public event Action? Completed;

    public CancellationToken CancellationToken => _shutdown.Token;

    public bool IsCancellationRequested => _shutdown.IsCancellationRequested;

    public void Cancel()
    {
        if (!_shutdown.IsCancellationRequested)
        {
            _shutdown.Cancel();
        }
    }

    public void RaiseCompleted() => Completed?.Invoke();
}
