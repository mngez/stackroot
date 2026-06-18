using System.Diagnostics;

namespace Stackroot.Core.Observability;

public sealed class PerformanceSampler
{
    public PerformanceSnapshot SamplePerformance(
        IReadOnlyList<ServicePerformanceTarget> services,
        IReadOnlyList<ProcessPerformanceTarget> processes,
        int? appPid = null)
    {
        services ??= [];
        processes ??= [];

        var pids = new List<int>();
        if (appPid is > 0)
        {
            pids.Add(appPid.Value);
        }

        pids.AddRange(services.Where(service => service.Pid is > 0).Select(service => service.Pid!.Value));
        pids.AddRange(processes.Where(process => process.Pid is > 0).Select(process => process.Pid!.Value));

        var memoryByPid = SampleMemoryByPid(pids);
        var items = new List<ProcessPerformance>();

        if (appPid is > 0)
        {
            items.Add(new ProcessPerformance
            {
                Id = "stackroot",
                Label = "Stackroot",
                Kind = PerformanceItemKind.App,
                Pid = appPid,
                Status = "running",
                MemoryMb = ResolveMemory(appPid, memoryByPid)
            });
        }

        foreach (var service in services)
        {
            if (service.Pid is not > 0)
            {
                continue;
            }

            items.Add(new ProcessPerformance
            {
                Id = service.Id,
                Label = service.Name,
                Kind = PerformanceItemKind.Service,
                Pid = service.Pid,
                Status = service.Status,
                MemoryMb = ResolveMemory(service.Pid, memoryByPid)
            });
        }

        foreach (var process in processes)
        {
            items.Add(new ProcessPerformance
            {
                Id = process.Id,
                Label = string.IsNullOrWhiteSpace(process.SiteName)
                    ? process.Name
                    : $"{process.Name} - {process.SiteName}",
                Kind = PerformanceItemKind.Process,
                Pid = process.Pid,
                Status = process.Status,
                MemoryMb = ResolveMemory(process.Pid, memoryByPid)
            });
        }

        return new PerformanceSnapshot
        {
            SampledAt = DateTimeOffset.UtcNow,
            Items = items
        };
    }

    private static Dictionary<int, double> SampleMemoryByPid(IEnumerable<int> pids)
    {
        var map = new Dictionary<int, double>();
        foreach (var pid in pids.Where(pid => pid > 0).Distinct())
        {
            try
            {
                using var process = Process.GetProcessById(pid);
                var memoryMb = Math.Round(process.WorkingSet64 / (1024d * 1024d), 1);
                map[pid] = memoryMb;
            }
            catch
            {
                // Process may have exited.
            }
        }

        return map;
    }

    private static double? ResolveMemory(int? pid, IReadOnlyDictionary<int, double> memoryByPid)
    {
        if (pid is not > 0)
        {
            return null;
        }

        return memoryByPid.TryGetValue(pid.Value, out var memory) ? memory : null;
    }
}
