namespace Stackroot.Core.Abstractions;

/// <summary>
/// Process-wide shutdown flags set by the desktop host during application exit.
/// Uses volatile int backing fields so reads from background threads see updates promptly.
/// </summary>
public static class ApplicationShutdownState
{
    private static int _shutdownRequested;
    private static int _isShuttingDown;

    public static bool ShutdownRequested
    {
        get => Volatile.Read(ref _shutdownRequested) != 0;
        set => Volatile.Write(ref _shutdownRequested, value ? 1 : 0);
    }

    public static bool IsShuttingDown
    {
        get => Volatile.Read(ref _isShuttingDown) != 0;
        set => Volatile.Write(ref _isShuttingDown, value ? 1 : 0);
    }

    /// <summary>True once exit/shutdown has begun — suppress keep-alive, toasts, and background refresh.</summary>
    public static bool IsClosing => ShutdownRequested || IsShuttingDown;
}
