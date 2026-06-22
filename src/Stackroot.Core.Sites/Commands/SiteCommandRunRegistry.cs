using System.Collections.Concurrent;
using System.Diagnostics;

namespace Stackroot.Core.Sites.Commands;

/// <summary>
/// Tracks in-flight site shell commands so the log viewer can cancel them by log path.
/// </summary>
public sealed class SiteCommandRunRegistry
{
    private readonly ConcurrentDictionary<string, RunEntry> _active = new(StringComparer.OrdinalIgnoreCase);

    public CancellationToken Register(string logPath, Process process)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(logPath);
        ArgumentNullException.ThrowIfNull(process);

        var cts = new CancellationTokenSource();
        _active[logPath] = new RunEntry(process, cts);
        return cts.Token;
    }

    public bool IsRunning(string logPath) =>
        !string.IsNullOrWhiteSpace(logPath) && _active.ContainsKey(logPath);

    public bool TryCancel(string logPath)
    {
        if (string.IsNullOrWhiteSpace(logPath) || !_active.TryGetValue(logPath, out var entry))
        {
            return false;
        }

        entry.Cancellation.Cancel();
        TryKillProcessTree(entry.Process);
        try
        {
            if (!entry.Process.HasExited)
            {
                entry.Process.WaitForExit(2000);
            }
        }
        catch
        {
            // Best effort.
        }

        return true;
    }

    public void Complete(string logPath)
    {
        if (string.IsNullOrWhiteSpace(logPath))
        {
            return;
        }

        if (_active.TryRemove(logPath, out var entry))
        {
            entry.Cancellation.Dispose();
        }
    }

    private static void TryKillProcessTree(Process process)
    {
        try
        {
            if (!process.HasExited)
            {
                process.Kill(entireProcessTree: true);
            }
        }
        catch
        {
            // Best effort.
        }
    }

    private sealed class RunEntry(Process process, CancellationTokenSource cancellation)
    {
        public Process Process { get; } = process;

        public CancellationTokenSource Cancellation { get; } = cancellation;
    }
}
