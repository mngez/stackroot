using Stackroot.Core.Services;
using Xunit;

namespace Stackroot.Core.Tests;

public sealed class ManagedServiceStatusPolicyTests
{
    [Theory]
    [InlineData(0, false)]
    [InlineData(1, true)]
    public void IsStackrootServing_reflects_owned_pid_count(int ownedPidCount, bool expected)
    {
        Assert.Equal(expected, ManagedServiceStatusPolicy.IsStackrootServing(ownedPidCount));
    }

    [Theory]
    [InlineData("Port 80 is already in use by another application", true)]
    [InlineData("Process started but port is not listening", false)]
    [InlineData(null, false)]
    public void IsPortConflictMessage_detects_start_time_conflict(string? message, bool expected)
    {
        Assert.Equal(expected, ManagedServiceStatusPolicy.IsPortConflictMessage(message));
    }

    [Fact]
    public void StripPortConflictDecorations_removes_repeated_retry_hints()
    {
        const string baseMessage = "Port 6379 is already in use by another application";
        var decorated = $"{baseMessage} · Will retry when the port is free · Will retry when the port is free";

        Assert.Equal(baseMessage, ManagedServiceStatusPolicy.StripPortConflictDecorations(decorated));
    }

    [Fact]
    public void FormatPortConflictKeepAliveMessage_is_idempotent_with_strip()
    {
        const string baseMessage = "Port 6379 is already in use by another application";
        var formatted = ManagedServiceStatusPolicy.FormatPortConflictKeepAliveMessage(baseMessage);
        var again = ManagedServiceStatusPolicy.FormatPortConflictKeepAliveMessage(formatted);

        Assert.Equal(formatted, again);
    }

    [Fact]
    public void StripKeepAliveDecorations_removes_repeated_restart_hints()
    {
        const string baseMessage = "Failed to stop - port is still listening";
        var decorated = $"{baseMessage} · Restarting… · Restarting… · Restarting…";

        Assert.Equal(baseMessage, ManagedServiceStatusPolicy.StripKeepAliveDecorations(decorated));
    }

    [Fact]
    public void FormatKeepAliveRecoveryMessage_is_idempotent_with_strip()
    {
        const string baseMessage = "Process exited unexpectedly";
        var formatted = ManagedServiceStatusPolicy.FormatKeepAliveRecoveryMessage(baseMessage, failureCount: 0);
        var again = ManagedServiceStatusPolicy.FormatKeepAliveRecoveryMessage(formatted, failureCount: 0);

        Assert.Equal(formatted, again);
    }

    [Fact]
    public void IsStopFailedMessage_detects_failed_stop_errors()
    {
        Assert.True(ManagedServiceStatusPolicy.IsStopFailedMessage("Failed to stop - port is still listening"));
        Assert.False(ManagedServiceStatusPolicy.IsStopFailedMessage("Port 6379 is already in use by another application"));
    }

    [Theory]
    [InlineData(0, 0, true, false)]
    [InlineData(0, 2, true, true)]
    public void CanStartOnPort_when_port_unspecified_ignores_listeners(
        int port,
        int ownedPidCount,
        bool expectedCanStart,
        bool expectedAlreadyServing)
    {
        var owned = Enumerable.Range(1, ownedPidCount).ToList();
        var canStart = ManagedServiceStatusPolicy.CanStartOnPort(port, owned, out var alreadyServing);
        Assert.Equal(expectedCanStart, canStart);
        Assert.Equal(expectedAlreadyServing, alreadyServing);
    }
}
