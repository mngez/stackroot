namespace Stackroot.Core.Abstractions.DataDocuments;

public sealed class ScheduledTasksDocument
{
    public int SchemaVersion { get; set; } = DataDocumentSchemas.ScheduledTasks;

    public List<ScheduledTaskEntry> Tasks { get; set; } = [];
}

public sealed class ScheduledTaskEntry
{
    public string Id { get; set; } = string.Empty;

    public string Label { get; set; } = string.Empty;

    public string Command { get; set; } = string.Empty;

    public string WorkingDirectory { get; set; } = string.Empty;

    public string CronExpression { get; set; } = "* * * * *";

    public bool CaptureLog { get; set; }

    public bool IsEnabled { get; set; } = true;

    public string? LastRunAt { get; set; }

    public string? LastLogPath { get; set; }

    public string? LastError { get; set; }

    /// <summary>Null or empty = app-wide task (not tied to a site).</summary>
    public string? SiteId { get; set; }
}
