namespace Stackroot.App.Helpers;

/// <summary>
/// Metadata shown in the log viewer UI — never written to the on-disk log file.
/// </summary>
public sealed record SiteLogChrome(
    string? CommandLine = null,
    Func<(int ExitCode, long DurationMs)?>? GetCompletion = null);
