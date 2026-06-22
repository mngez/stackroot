namespace Stackroot.App.Helpers;

/// <summary>
/// Mutable log viewer state for site commands — supports run-again and live path updates.
/// </summary>
public sealed class SiteLogSession
{
    private string _logPath;
    private int _activeRuns;

    public SiteLogSession(string logPath) => _logPath = logPath;

    public string LogPath
    {
        get => _logPath;
        set
        {
            if (string.Equals(_logPath, value, StringComparison.Ordinal))
            {
                return;
            }

            _logPath = value;
            LogPathChanged?.Invoke(this, EventArgs.Empty);
        }
    }

    public event EventHandler? LogPathChanged;

    public event EventHandler? Updated;

    public string? CommandLine { get; set; }

    public Func<Task>? CancelAsync { get; set; }

    public Func<bool>? IsRunning { get; set; }

    public Func<(int ExitCode, long DurationMs)?>? GetCompletion { get; set; }

    public Func<Task>? RunAgainAsync { get; set; }

    public bool IsMarkedRunning => Volatile.Read(ref _activeRuns) > 0;

    public void MarkRunning() => Interlocked.Increment(ref _activeRuns);

    public void MarkFinished() => Interlocked.Decrement(ref _activeRuns);

    public void NotifyUpdated() => Updated?.Invoke(this, EventArgs.Empty);
}
