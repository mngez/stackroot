using System.Text;
using System.Threading.Channels;
using Stackroot.Core.Abstractions;
using Stackroot.Core.Settings;

namespace Stackroot.Core.Observability;

public sealed class DiagnosticsReportLogger : IDiagnosticsReporter, IDisposable
{
    private const string LogFileName = "development-report.log";
    private static readonly TimeSpan EnabledCacheTtl = TimeSpan.FromSeconds(10);

    private readonly object _gate = new();
    private readonly SettingsStore _settingsStore;
    private readonly string _logPath;
    private readonly Channel<LogEntry> _logQueue = Channel.CreateUnbounded<LogEntry>(
        new UnboundedChannelOptions
        {
            SingleReader = true,
            SingleWriter = false
        });
    private readonly Task _writerTask;
    private bool _sessionStarted;
    private bool? _enabledCache;
    private DateTimeOffset _enabledCacheAt;
    private string? _lastErrorSignature;
    private DateTimeOffset _lastErrorLoggedAt;
    private int _suppressedDuplicateErrors;

    private const long MaxLogBytes = 5 * 1024 * 1024;
    private static readonly TimeSpan DuplicateErrorWindow = TimeSpan.FromSeconds(30);

    private Action<string>? _countersHandler;

    public DiagnosticsReportLogger(StackrootPaths paths, SettingsStore settingsStore)
    {
        ArgumentNullException.ThrowIfNull(paths);
        ArgumentNullException.ThrowIfNull(settingsStore);

        _settingsStore = settingsStore;
        Directory.CreateDirectory(paths.LogsRoot);
        _logPath = Path.Combine(paths.LogsRoot, LogFileName);
        DevelopmentReportLogRotation.RotateSessionLog(paths.LogsRoot);
        _writerTask = Task.Run(ProcessQueueAsync);
        _countersHandler = summary => LogActivity("Counters", summary);
        DiagnosticsCounters.SummaryLogged += _countersHandler;
    }

    public string LogPath => _logPath;

    public bool IsEnabled => IsEnabledCore();

    public void Dispose()
    {
        if (_countersHandler is not null)
        {
            DiagnosticsCounters.SummaryLogged -= _countersHandler;
            _countersHandler = null;
        }

        _logQueue.Writer.TryComplete();
        try
        {
            _writerTask.Wait(TimeSpan.FromSeconds(1));
        }
        catch
        {
            // Best-effort logging only.
        }
    }

    public void LogActivity(string area, string message)
    {
        Write("ACTIVITY", area, message, exception: null);
    }

    public void LogUserError(string area, string message)
    {
        if (string.IsNullOrWhiteSpace(message))
        {
            return;
        }

        Write("USER-ERROR", area, message, exception: null);
    }

    public void LogException(string area, Exception exception)
    {
        ArgumentNullException.ThrowIfNull(exception);
        Write("ERROR", area, exception.Message, exception);
    }

    public IDisposable BeginAction(string area, string action)
        => DiagnosticsActionScope.Begin(this, area, action);

    private bool IsEnabledCore()
    {
        if (_enabledCache is not null && DateTimeOffset.UtcNow - _enabledCacheAt < EnabledCacheTtl)
        {
            return _enabledCache.Value;
        }

        try
        {
            _enabledCache = _settingsStore.Load().General.DiagnosticsLogEnabled == true;
        }
        catch
        {
            _enabledCache = false;
        }

        _enabledCacheAt = DateTimeOffset.UtcNow;
        return _enabledCache.Value;
    }

    private void Write(string level, string area, string message, Exception? exception)
    {
        var alwaysLog = level is "ERROR" or "USER-ERROR";
        if (!alwaysLog && !IsEnabledCore())
        {
            _sessionStarted = false;
            return;
        }

        if (alwaysLog && ShouldSuppressDuplicateError(area, message, exception))
        {
            return;
        }

        if (!_logQueue.Writer.TryWrite(new LogEntry(level, area, message, exception?.ToString(), DateTimeOffset.Now)))
        {
            try
            {
                WriteEntry(new LogEntry(level, area, message, exception?.ToString(), DateTimeOffset.Now));
            }
            catch
            {
                // Best-effort logging only.
            }
        }
    }

    private async Task ProcessQueueAsync()
    {
        try
        {
            await foreach (var entry in _logQueue.Reader.ReadAllAsync().ConfigureAwait(false))
            {
                try
                {
                    WriteEntry(entry);
                }
                catch
                {
                    // Best-effort logging only.
                }
            }
        }
        catch
        {
            // Best-effort logging only.
        }
    }

    private void WriteEntry(LogEntry entry)
    {
        var builder = new StringBuilder();
        if (!_sessionStarted)
        {
            _sessionStarted = true;
            builder.AppendLine($"========== Stackroot development report {DateTimeOffset.Now:u} (PID {Environment.ProcessId}) ==========");
        }

        var suppressed = Interlocked.Exchange(ref _suppressedDuplicateErrors, 0);
        if (suppressed > 0)
        {
            builder.Append('[').Append(DateTimeOffset.Now.ToString("u")).Append("] ");
            builder.Append("ACTIVITY [Diagnostics] Suppressed ");
            builder.Append(suppressed);
            builder.AppendLine(" duplicate error log entries.");
        }

        builder.Append('[').Append(entry.Timestamp.ToString("u")).Append("] ");
        builder.Append(entry.Level).Append(" [").Append(entry.Area).Append("] ");
        builder.AppendLine(entry.Message);

        if (entry.ExceptionText is not null)
        {
            builder.AppendLine(entry.ExceptionText);
        }

        TruncateIfOversized();
        File.AppendAllText(_logPath, builder.ToString(), Encoding.UTF8);
    }

    private bool ShouldSuppressDuplicateError(string area, string message, Exception? exception)
    {
        var signature = $"{area}|{exception?.GetType().FullName ?? "none"}|{message}";
        var now = DateTimeOffset.UtcNow;
        lock (_gate)
        {
            if (string.Equals(_lastErrorSignature, signature, StringComparison.Ordinal) &&
                now - _lastErrorLoggedAt < DuplicateErrorWindow)
            {
                Interlocked.Increment(ref _suppressedDuplicateErrors);
                return true;
            }

            _lastErrorSignature = signature;
            _lastErrorLoggedAt = now;
            return false;
        }
    }

    private void TruncateIfOversized()
    {
        try
        {
            if (!File.Exists(_logPath))
            {
                return;
            }

            var length = new FileInfo(_logPath).Length;
            if (length <= MaxLogBytes)
            {
                return;
            }

            var tail = File.ReadAllText(_logPath, Encoding.UTF8);
            if (tail.Length > MaxLogBytes / 2)
            {
                tail = tail[^((int)MaxLogBytes / 2)..];
            }

            File.WriteAllText(
                _logPath,
                $"[{DateTimeOffset.Now:u}] ACTIVITY [Diagnostics] Log truncated after exceeding {MaxLogBytes / (1024 * 1024)} MB.{Environment.NewLine}{tail}",
                Encoding.UTF8);
        }
        catch
        {
            // Best-effort only.
        }
    }

    private sealed record LogEntry(
        string Level,
        string Area,
        string Message,
        string? ExceptionText,
        DateTimeOffset Timestamp);
}
