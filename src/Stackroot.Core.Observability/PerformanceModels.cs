namespace Stackroot.Core.Observability;

public enum PerformanceItemKind
{
    Service,
    Process,
    App
}

public sealed class ProcessPerformance
{
    public string Id { get; init; } = string.Empty;
    public string Label { get; init; } = string.Empty;
    public PerformanceItemKind Kind { get; init; }
    public int? Pid { get; init; }
    public double? CpuPercent { get; init; }
    public double? MemoryMb { get; init; }
    public string? Status { get; init; }
}

public sealed class PerformanceSnapshot
{
    public DateTimeOffset SampledAt { get; init; } = DateTimeOffset.UtcNow;
    public IReadOnlyList<ProcessPerformance> Items { get; init; } = [];
}

public sealed class ServicePerformanceTarget
{
    public string Id { get; init; } = string.Empty;
    public string Name { get; init; } = string.Empty;
    public string? Status { get; init; }
    public int? Pid { get; init; }
}

public sealed class ProcessPerformanceTarget
{
    public string Id { get; init; } = string.Empty;
    public string Name { get; init; } = string.Empty;
    public string? Status { get; init; }
    public int? Pid { get; init; }
    public string? SiteName { get; init; }
}
