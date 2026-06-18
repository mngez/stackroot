namespace Stackroot.Core.Scheduler;

public sealed class ScheduledTask
{
    public string Id { get; init; } = Guid.NewGuid().ToString("N")[..12];
    public string Label { get; set; } = string.Empty;
    public string Command { get; set; } = string.Empty;
    public string WorkingDirectory { get; set; } = string.Empty;
    public string CronExpression { get; set; } = "* * * * *";
    public bool CaptureLog { get; set; }
    public bool IsEnabled { get; set; } = true;
    public string? LastRunAt { get; set; }
    public string? LastLogPath { get; set; }
    public string? LastError { get; set; }
}
