using Stackroot.Core.Abstractions;
using Stackroot.Core.Windows;

namespace Stackroot.Core.Services;

/// <summary>
/// Process-first rules for managed service status.
/// TCP port probes are for start/wait paths only — not periodic live status.
/// </summary>
public static class ManagedServiceStatusPolicy
{
    public const string PortConflictMessageSuffix = "is already in use by another application";

    public const string PortConflictRetryHint = "Will retry when the port is free";

    private const string PortConflictRetryHintSeparator = " · ";

    public const string KeepAliveRestartHint = "Restarting…";

    public const string KeepAliveStartingHint = "Starting…";

    public const string KeepAliveAutoRestartPrefix = "Auto-restart";

    public static bool IsStopFailedMessage(string? message)
        => !string.IsNullOrWhiteSpace(message)
           && message.Contains("Failed to stop", StringComparison.OrdinalIgnoreCase);

    public static bool IsStackrootServing(int ownedPidCount) => ownedPidCount > 0;

    public static bool IsPortConflictMessage(string? message)
        => !string.IsNullOrWhiteSpace(message)
           && StripPortConflictDecorations(message).Contains(
               PortConflictMessageSuffix,
               StringComparison.OrdinalIgnoreCase);

    public static string StripPortConflictDecorations(string? message)
    {
        if (string.IsNullOrWhiteSpace(message))
        {
            return string.Empty;
        }

        var result = message.Trim();
        var retrySuffix = PortConflictRetryHintSeparator + PortConflictRetryHint;
        while (result.Contains(retrySuffix, StringComparison.OrdinalIgnoreCase))
        {
            var index = result.IndexOf(retrySuffix, StringComparison.OrdinalIgnoreCase);
            if (index < 0)
            {
                break;
            }

            result = result[..index].TrimEnd();
        }

        return result;
    }

    public static string FormatPortConflictKeepAliveMessage(string baseMessage)
        => $"{StripPortConflictDecorations(baseMessage)}{PortConflictRetryHintSeparator}{PortConflictRetryHint}";

    public static string StripKeepAliveDecorations(string? message)
    {
        var result = StripPortConflictDecorations(message);
        if (string.IsNullOrWhiteSpace(result))
        {
            return string.Empty;
        }

        while (true)
        {
            var next = TryStripKeepAliveSuffix(result);
            if (next is null)
            {
                break;
            }

            result = next;
        }

        return result.Trim();
    }

    public static string FormatKeepAliveRecoveryMessage(string? baseMessage, int failureCount)
    {
        var stripped = StripKeepAliveDecorations(baseMessage);
        var suffix = failureCount > 0
            ? $"{KeepAliveAutoRestartPrefix}: attempt {failureCount}"
            : KeepAliveRestartHint;
        return string.IsNullOrWhiteSpace(stripped)
            ? suffix
            : $"{stripped}{PortConflictRetryHintSeparator}{suffix}";
    }

    public static bool IsKeepAliveRecoverySuffix(string? message)
    {
        if (string.IsNullOrWhiteSpace(message))
        {
            return false;
        }

        var trimmed = message.Trim();
        return trimmed.StartsWith(KeepAliveAutoRestartPrefix, StringComparison.OrdinalIgnoreCase)
               || string.Equals(trimmed, KeepAliveRestartHint, StringComparison.Ordinal)
               || string.Equals(trimmed, KeepAliveStartingHint, StringComparison.Ordinal)
               || string.Equals(trimmed, "Restarting...", StringComparison.Ordinal);
    }

    private static string? TryStripKeepAliveSuffix(string message)
    {
        foreach (var suffix in new[]
                 {
                     PortConflictRetryHintSeparator + KeepAliveRestartHint,
                     PortConflictRetryHintSeparator + "Restarting...",
                     PortConflictRetryHintSeparator + KeepAliveStartingHint
                 })
        {
            if (message.EndsWith(suffix, StringComparison.OrdinalIgnoreCase))
            {
                return message[..^suffix.Length].TrimEnd();
            }
        }

        var separator = PortConflictRetryHintSeparator;
        var prefixIndex = message.LastIndexOf(separator + KeepAliveAutoRestartPrefix, StringComparison.OrdinalIgnoreCase);
        if (prefixIndex >= 0)
        {
            return message[..prefixIndex].TrimEnd();
        }

        return null;
    }

    public static bool ShouldClearTrackedService(ServiceInfo? cached, int ownedPidCount)
    {
        if (ownedPidCount > 0)
        {
            return false;
        }

        return !HasLiveTrackedProcess(cached);
    }

    /// <summary>
    /// True when netstat shows a listener on the port but none belong to Stackroot.
    /// Prefer <see cref="StackrootManagedProcessResolver.HasForeignListener"/> when service context is available.
    /// </summary>
    public static bool HasForeignListenerOnPort(int port, IReadOnlyList<int> ownedPids)
    {
        if (port <= 0)
        {
            return false;
        }

        var listeners = ServiceProcessTools.FindPidsListeningOnPort(port);
        if (listeners.Count == 0)
        {
            return false;
        }

        if (ownedPids.Count == 0)
        {
            return true;
        }

        var owned = ownedPids.ToHashSet();
        return listeners.Any(pid => !owned.Contains(pid));
    }

    public static bool ShouldSkipSupervisionRestartWhenPortBusy(int port, IReadOnlyList<int> ownedPids)
        => HasForeignListenerOnPort(port, ownedPids);

    public static string FormatPortConflictMessage(int port)
        => $"Port {port} {PortConflictMessageSuffix}";

    /// <summary>
    /// Process-first start gate: netstat (TCP + UDP) instead of TCP connect alone.
    /// </summary>
    public static bool CanStartOnPort(int port, IReadOnlyList<int> ownedPids, out bool alreadyServing)
    {
        alreadyServing = false;
        if (port <= 0)
        {
            alreadyServing = IsStackrootServing(ownedPids.Count);
            return true;
        }

        if (HasForeignListenerOnPort(port, ownedPids))
        {
            return false;
        }

        alreadyServing = IsStackrootServing(ownedPids.Count);
        return true;
    }

    private static bool HasLiveTrackedProcess(ServiceInfo? cached)
        => cached?.Pid is int pid && ServiceProcessTools.IsProcessAlive(pid);
}
