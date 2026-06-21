using Stackroot.Core.Abstractions;
using Stackroot.Core.Windows;

namespace Stackroot.Core.Services;

/// <summary>
/// Legacy TCP port-probe helpers retained for start/wait paths and unit tests.
/// Live status uses <see cref="ManagedServiceStatusPolicy"/> (process-first).
/// </summary>
public static class ServicePortReconcilePolicy
{
    /// <summary>
    /// Skip restart only when the TCP probe timed out but our tracked process is still alive.
    /// Inconclusive with no live tracked process means the service is down — restart anyway.
    /// </summary>
    public static bool ShouldSkipSupervisionRestart(PortProbeResult probe, ServiceInfo? cached = null)
    {
        if (probe != PortProbeResult.Inconclusive)
        {
            return false;
        }

        return HasLiveTrackedProcess(cached);
    }

    public static bool ShouldSkipSupervisionRestartWhenPortBusy(PortProbeResult probe)
        => probe == PortProbeResult.Open;

    public static bool ShouldClearTrackedService(PortProbeResult? probeWhenEmptyPids, ServiceInfo? cached)
    {
        if (probeWhenEmptyPids != PortProbeResult.Inconclusive)
        {
            return true;
        }

        return !HasLiveTrackedProcess(cached);
    }

    public static bool InferPortOpen(bool pidsIndicateOpen, PortProbeResult? probeWhenEmptyPids, ServiceInfo? cached)
    {
        if (pidsIndicateOpen)
        {
            return true;
        }

        if (probeWhenEmptyPids == PortProbeResult.Open)
        {
            // Port open alone does not mean Stackroot is serving (e.g. Laragon on the same port).
            return HasLiveTrackedProcess(cached);
        }

        if (probeWhenEmptyPids == PortProbeResult.Closed)
        {
            return false;
        }

        // Inconclusive: only treat as open when our tracked process is still alive.
        return HasLiveTrackedProcess(cached);
    }

    /// <summary>
    /// TCP port is listening but no Stackroot-owned listener was found.
    /// </summary>
    public static bool IsPortBlockedByOtherApplication(
        int ownedPidCount,
        PortProbeResult? probe,
        ServiceInfo? cached)
        => ownedPidCount == 0
           && probe == PortProbeResult.Open
           && !HasLiveTrackedProcess(cached);

    public static bool TryPreserveRunningWithoutPids(ServiceInfo? cached, ref IReadOnlyList<int> ownedPids)
    {
        if (!HasLiveTrackedProcess(cached))
        {
            return false;
        }

        ownedPids = [cached!.Pid!.Value];
        return true;
    }

    private static bool HasLiveTrackedProcess(ServiceInfo? cached)
        => cached?.Pid is int pid && ServiceProcessTools.IsProcessAlive(pid);
}
