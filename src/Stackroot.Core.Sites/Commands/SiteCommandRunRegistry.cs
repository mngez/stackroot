using System.Collections.Concurrent;
using System.Diagnostics;

namespace Stackroot.Core.Sites.Commands;

public enum SiteCommandKind
{
    Custom,
    QuickAction
}

public sealed record ActiveSiteCommand(
    string LogPath,
    string SiteId,
    SiteCommandKind Kind,
    string CommandKey,
    string CommandLine,
    DateTimeOffset StartedAtUtc);

public sealed record SiteCommandCompletedEventArgs(
    string LogPath,
    string SiteId,
    SiteCommandKind Kind,
    string CommandKey,
    int ExitCode);

/// <summary>
/// Tracks in-flight site shell commands so the log viewer can cancel them by log path,
/// and so a site page reopened after navigating away can rediscover what is still running.
/// </summary>
public sealed class SiteCommandRunRegistry
{
    private readonly ConcurrentDictionary<string, RunEntry> _active = new(StringComparer.OrdinalIgnoreCase);

    public event EventHandler<SiteCommandCompletedEventArgs>? CommandCompleted;

    public CancellationToken Register(
        string logPath,
        Process process,
        string? siteId = null,
        SiteCommandKind kind = SiteCommandKind.Custom,
        string? commandKey = null,
        string? commandLine = null)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(logPath);
        ArgumentNullException.ThrowIfNull(process);

        var cts = new CancellationTokenSource();
        _active[logPath] = new RunEntry(process, cts, siteId, kind, commandKey, commandLine, DateTimeOffset.UtcNow);
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

    public void Complete(string logPath, int exitCode = 0)
    {
        if (string.IsNullOrWhiteSpace(logPath))
        {
            return;
        }

        if (!_active.TryRemove(logPath, out var entry))
        {
            return;
        }

        entry.Cancellation.Dispose();

        if (!string.IsNullOrWhiteSpace(entry.SiteId))
        {
            CommandCompleted?.Invoke(this, new SiteCommandCompletedEventArgs(
                logPath, entry.SiteId!, entry.Kind, entry.CommandKey ?? string.Empty, exitCode));
        }
    }

    /// <summary>Returns commands currently running for a site, keyed by custom-command id or quick-action id.</summary>
    public IReadOnlyList<ActiveSiteCommand> GetActiveForSite(string siteId)
    {
        if (string.IsNullOrWhiteSpace(siteId))
        {
            return [];
        }

        return _active
            .Where(kv => string.Equals(kv.Value.SiteId, siteId, StringComparison.OrdinalIgnoreCase))
            .Select(kv => new ActiveSiteCommand(
                kv.Key,
                kv.Value.SiteId!,
                kv.Value.Kind,
                kv.Value.CommandKey ?? string.Empty,
                kv.Value.CommandLine ?? string.Empty,
                kv.Value.StartedAtUtc))
            .OrderBy(c => c.StartedAtUtc)
            .ToList();
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

    private sealed class RunEntry(
        Process process,
        CancellationTokenSource cancellation,
        string? siteId,
        SiteCommandKind kind,
        string? commandKey,
        string? commandLine,
        DateTimeOffset startedAtUtc)
    {
        public Process Process { get; } = process;

        public CancellationTokenSource Cancellation { get; } = cancellation;

        public string? SiteId { get; } = siteId;

        public SiteCommandKind Kind { get; } = kind;

        public string? CommandKey { get; } = commandKey;

        public string? CommandLine { get; } = commandLine;

        public DateTimeOffset StartedAtUtc { get; } = startedAtUtc;
    }
}
