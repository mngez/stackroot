using System.Diagnostics;

namespace Stackroot.Core.Windows;

public static class ProcessCpuTools
{
    private static readonly TimeSpan DefaultSampleWindow = TimeSpan.FromMilliseconds(500);

    public static async Task<IReadOnlyDictionary<int, double>> SampleCpuUsagePercentAsync(
        IEnumerable<int> pids,
        TimeSpan? sampleWindow = null,
        CancellationToken cancellationToken = default)
    {
        var window = sampleWindow ?? DefaultSampleWindow;
        var distinct = pids.Where(static pid => pid > 0).Distinct().ToList();
        if (distinct.Count == 0)
        {
            return new Dictionary<int, double>();
        }

        var starts = new Dictionary<int, TimeSpan>();
        foreach (var pid in distinct)
        {
            if (TryGetTotalProcessorTime(pid, out var startTime))
            {
                starts[pid] = startTime;
            }
        }

        if (starts.Count == 0)
        {
            return new Dictionary<int, double>();
        }

        await Task.Delay(window, cancellationToken).ConfigureAwait(false);

        var processorCount = Environment.ProcessorCount;
        var windowMs = window.TotalMilliseconds;
        var results = new Dictionary<int, double>();

        foreach (var (pid, start) in starts)
        {
            if (!TryGetTotalProcessorTime(pid, out var end))
            {
                continue;
            }

            var usedMs = (end - start).TotalMilliseconds;
            var percent = usedMs / (windowMs * processorCount) * 100d;
            results[pid] = Math.Round(Math.Clamp(percent, 0, 100 * processorCount), 1);
        }

        return results;
    }

    public static IReadOnlyDictionary<int, double> SampleCpuUsagePercent(
        IEnumerable<int> pids,
        TimeSpan? sampleWindow = null)
        => SampleCpuUsagePercentAsync(pids, sampleWindow).GetAwaiter().GetResult();

    public static double? SumCpuUsagePercent(
        IEnumerable<int> pids,
        IReadOnlyDictionary<int, double> samples)
    {
        var total = 0d;
        var any = false;

        foreach (var pid in pids.Where(static pid => pid > 0).Distinct())
        {
            if (!samples.TryGetValue(pid, out var percent))
            {
                continue;
            }

            total += percent;
            any = true;
        }

        return any ? Math.Round(total, 1) : null;
    }

    private static bool TryGetTotalProcessorTime(int pid, out TimeSpan totalProcessorTime)
    {
        totalProcessorTime = default;

        try
        {
            using var process = Process.GetProcessById(pid);
            process.Refresh();
            totalProcessorTime = process.TotalProcessorTime;
            return true;
        }
        catch
        {
            return false;
        }
    }
}
