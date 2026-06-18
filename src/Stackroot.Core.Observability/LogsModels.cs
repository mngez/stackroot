namespace Stackroot.Core.Observability;

public enum LogCategory
{
    Service,
    Process
}

public sealed class LogFileEntry
{
    public string Path { get; init; } = string.Empty;
    public string RelativePath { get; init; } = string.Empty;
    public long SizeBytes { get; init; }
    public DateTimeOffset ModifiedAt { get; init; }
    public LogCategory Category { get; init; }
}

public sealed class LogInventory
{
    public long TotalBytes { get; init; }
    public IReadOnlyList<LogFileEntry> Files { get; init; } = [];
    public IReadOnlyList<LogFileEntry> ServiceFiles { get; init; } = [];
    public IReadOnlyList<LogFileEntry> ProcessFiles { get; init; } = [];
}

public sealed class LogCleanupResult
{
    public int Deleted { get; init; }
    public long FreedBytes { get; init; }
}

public sealed class CleanupLogsOptions
{
    public required string LogsRoot { get; init; }
    public int? OlderThanDays { get; init; }
    public bool DeleteAll { get; init; }
}
