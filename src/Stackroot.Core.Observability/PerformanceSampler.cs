using Stackroot.Core.Windows;

namespace Stackroot.Core.Observability;

public sealed class PerformanceSampler
{
    private sealed record PendingPerformanceItem(
        string Id,
        string Label,
        PerformanceItemKind Kind,
        int? Pid,
        string? Status,
        string? Endpoint,
        IReadOnlyList<int> MemoryPids);

    public PerformanceSnapshot SamplePerformance(
        IReadOnlyList<ServicePerformanceTarget> services,
        IReadOnlyList<ProcessPerformanceTarget> processes,
        IReadOnlyList<PhpListenerPerformanceTarget> phpListeners,
        int? appPid = null,
        bool measureCpu = true)
        => SamplePerformanceAsync(services, processes, phpListeners, appPid, measureCpu)
            .GetAwaiter()
            .GetResult();

    public async Task<PerformanceSnapshot> SamplePerformanceAsync(
        IReadOnlyList<ServicePerformanceTarget> services,
        IReadOnlyList<ProcessPerformanceTarget> processes,
        IReadOnlyList<PhpListenerPerformanceTarget> phpListeners,
        int? appPid = null,
        bool measureCpu = true,
        CancellationToken cancellationToken = default)
    {
        services ??= [];
        processes ??= [];
        phpListeners ??= [];

        var pending = new List<PendingPerformanceItem>();

        if (appPid is > 0)
        {
            pending.Add(new PendingPerformanceItem(
                "stackroot",
                "Stackroot",
                PerformanceItemKind.App,
                appPid,
                "running",
                null,
                [appPid.Value]));
        }

        foreach (var service in services)
        {
            var memoryPids = ResolveServiceMemoryPids(service.MemoryPids, service.Pid);
            if (memoryPids.Count == 0)
            {
                continue;
            }

            pending.Add(new PendingPerformanceItem(
                service.Id,
                service.Name,
                PerformanceItemKind.Service,
                service.Pid ?? memoryPids[0],
                service.Status,
                null,
                memoryPids));
        }

        foreach (var listener in phpListeners)
        {
            var memoryPids = listener.MemoryPids.Count > 0
                ? listener.MemoryPids
                : listener.Pid is > 0
                    ? new[] { listener.Pid.Value }
                    : Array.Empty<int>();

            if (memoryPids.Count == 0)
            {
                continue;
            }

            pending.Add(new PendingPerformanceItem(
                listener.Id,
                listener.Name,
                PerformanceItemKind.PhpListener,
                listener.Pid ?? memoryPids[0],
                listener.Status,
                listener.Endpoint,
                memoryPids));
        }

        foreach (var process in processes)
        {
            var memoryPids = process.MemoryPids.Count > 0
                ? process.MemoryPids
                : process.Pid is > 0
                    ? ProcessMemoryTools.CollectManagedProcessMemoryPids(process.Pid.Value)
                    : Array.Empty<int>();

            pending.Add(new PendingPerformanceItem(
                process.Id,
                string.IsNullOrWhiteSpace(process.SiteName)
                    ? process.Name
                    : $"{process.Name} - {process.SiteName}",
                PerformanceItemKind.Process,
                process.Pid,
                process.Status,
                null,
                memoryPids));
        }

        var cpuSamples = measureCpu
            ? await ProcessCpuTools.SampleCpuUsagePercentAsync(
                pending.SelectMany(item => item.MemoryPids),
                cancellationToken: cancellationToken).ConfigureAwait(false)
            : new Dictionary<int, double>();

        var items = pending.Select(item => new ProcessPerformance
        {
            Id = item.Id,
            Label = item.Label,
            Kind = item.Kind,
            Pid = item.Pid,
            Status = item.Status,
            Endpoint = item.Endpoint,
            MemoryMb = item.MemoryPids.Count > 0
                ? ProcessMemoryTools.SumTaskManagerMemoryMb(item.MemoryPids)
                : null,
            CpuPercent = measureCpu && item.MemoryPids.Count > 0
                ? ProcessCpuTools.SumCpuUsagePercent(item.MemoryPids, cpuSamples)
                : null
        }).ToList();

        return new PerformanceSnapshot
        {
            SampledAt = DateTimeOffset.UtcNow,
            Items = items
        };
    }

    private static IReadOnlyList<int> ResolveServiceMemoryPids(IReadOnlyList<int> memoryPids, int? pid)
    {
        if (memoryPids.Count > 0)
        {
            return memoryPids;
        }

        return pid is > 0 ? ProcessMemoryTools.CollectProcessTree(pid.Value) : Array.Empty<int>();
    }
}
