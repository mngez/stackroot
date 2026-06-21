using System.Runtime.InteropServices;

namespace Stackroot.Core.Windows;

/// <summary>
/// Non-blocking CPU % from successive samples (same idea as Task Manager / PDH deltas).
/// No sleep — callers invoke on their existing refresh cadence (~10s runtime poll).
/// </summary>
public sealed class ProcessCpuDeltaTracker
{
    private const uint ProcessQueryLimitedInformation = 0x1000;

    private readonly Dictionary<int, (TimeSpan ProcessorTime, long WallTicks)> _last = new();
    private readonly object _sync = new();

    public void Prime(IEnumerable<int> pids)
    {
        var now = Environment.TickCount64;
        var samples = ReadProcessorTimes(pids);
        lock (_sync)
        {
            foreach (var (pid, processorTime) in samples)
            {
                _last[pid] = (processorTime, now);
            }
        }
    }

    public void Reset()
    {
        lock (_sync)
        {
            _last.Clear();
        }
    }

    /// <summary>
    /// Returns summed CPU % for managed PIDs when a prior sample exists and wall time >= <paramref name="minWallMs"/>.
    /// </summary>
    public bool TryGetTotalCpuPercent(
        IEnumerable<int> pids,
        out double totalPercent,
        int minWallMs = 500)
    {
        totalPercent = 0;
        var now = Environment.TickCount64;
        var processorCount = Environment.ProcessorCount;
        var distinct = DistinctPids(pids).ToList();
        var current = ReadProcessorTimes(distinct);
        var any = false;

        lock (_sync)
        {
            var active = new HashSet<int>(distinct);
            foreach (var (pid, processorTime) in current)
            {
                if (_last.TryGetValue(pid, out var previous))
                {
                    var wallMs = now - previous.WallTicks;
                    if (wallMs >= minWallMs)
                    {
                        var usedMs = (processorTime - previous.ProcessorTime).TotalMilliseconds;
                        if (usedMs >= 0)
                        {
                            var percent = usedMs / (wallMs * processorCount) * 100d;
                            totalPercent += Math.Clamp(percent, 0, 100d * processorCount);
                            any = true;
                        }
                    }
                }

                _last[pid] = (processorTime, now);
            }

            foreach (var stale in _last.Keys.Where(pid => !active.Contains(pid)).ToList())
            {
                _last.Remove(stale);
            }
        }

        if (any)
        {
            totalPercent = Math.Round(totalPercent, 1);
        }

        return any;
    }

    private static IEnumerable<int> DistinctPids(IEnumerable<int> pids)
        => pids.Where(static pid => pid > 0).Distinct();

    private static List<(int Pid, TimeSpan ProcessorTime)> ReadProcessorTimes(IEnumerable<int> pids)
    {
        var results = new List<(int, TimeSpan)>();
        foreach (var pid in DistinctPids(pids))
        {
            if (TryGetTotalProcessorTime(pid, out var processorTime))
            {
                results.Add((pid, processorTime));
            }
        }

        return results;
    }

    private static bool TryGetTotalProcessorTime(int pid, out TimeSpan totalProcessorTime)
    {
        totalProcessorTime = default;

        var handle = OpenProcess(ProcessQueryLimitedInformation, false, pid);
        if (handle == IntPtr.Zero)
        {
            return false;
        }

        try
        {
            if (!GetProcessTimes(
                    handle,
                    out _,
                    out _,
                    out var kernelTime,
                    out var userTime))
            {
                return false;
            }

            totalProcessorTime = FileTimeToTimeSpan(kernelTime) + FileTimeToTimeSpan(userTime);
            return true;
        }
        finally
        {
            CloseHandle(handle);
        }
    }

    private static TimeSpan FileTimeToTimeSpan(long fileTime)
        => fileTime > 0 ? new TimeSpan(fileTime) : TimeSpan.Zero;

    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern IntPtr OpenProcess(uint desiredAccess, bool inheritHandle, int processId);

    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern bool CloseHandle(IntPtr handle);

    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern bool GetProcessTimes(
        IntPtr processHandle,
        out long creationTime,
        out long exitTime,
        out long kernelTime,
        out long userTime);
}
