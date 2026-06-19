namespace Stackroot.App.Services;

/// <summary>
/// Tracks long-running UI background work so shutdown can wait before stopping services.
/// </summary>
public static class BackgroundOperationTracker
{
    private static int _activeOperations;
    private static int _shutdownRequested;

    public static int ActiveOperations => Volatile.Read(ref _activeOperations);

    public static bool IsShutdownRequested => Volatile.Read(ref _shutdownRequested) != 0;

    public static BackgroundOperationLease Enter(string operationName)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(operationName);

        if (IsShutdownRequested)
        {
            throw new InvalidOperationException("Stackroot is closing — the operation was cancelled.");
        }

        Interlocked.Increment(ref _activeOperations);
        return new BackgroundOperationLease(operationName);
    }

    public static void RequestShutdown()
    {
        Interlocked.Exchange(ref _shutdownRequested, 1);
    }

    public static async Task WaitForCompletionAsync(
        TimeSpan timeout,
        CancellationToken cancellationToken = default,
        Action<int>? onStillActive = null)
    {
        var deadline = DateTimeOffset.UtcNow + timeout;
        while (Volatile.Read(ref _activeOperations) > 0 && DateTimeOffset.UtcNow < deadline)
        {
            cancellationToken.ThrowIfCancellationRequested();
            onStillActive?.Invoke(ActiveOperations);
            await Task.Delay(250, cancellationToken).ConfigureAwait(false);
        }
    }

    public sealed class BackgroundOperationLease : IDisposable
    {
        private int _disposed;

        internal BackgroundOperationLease(string operationName)
        {
            OperationName = operationName;
        }

        public string OperationName { get; }

        public void Dispose()
        {
            if (Interlocked.Exchange(ref _disposed, 1) != 0)
            {
                return;
            }

            Interlocked.Decrement(ref _activeOperations);
        }
    }
}
