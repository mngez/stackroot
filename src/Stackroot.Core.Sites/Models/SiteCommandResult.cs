namespace Stackroot.Core.Sites.Models;

public sealed record SiteCommandResult
{
    public int ExitCode { get; init; }
    public string Stdout { get; init; } = string.Empty;
    public string Stderr { get; init; } = string.Empty;
    public long DurationMs { get; init; }
    public string CommandLine { get; init; } = string.Empty;
    public string LogPath { get; init; } = string.Empty;
}
