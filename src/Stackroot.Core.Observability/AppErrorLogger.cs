using System.Text;
using Stackroot.Core.Abstractions;

namespace Stackroot.Core.Observability;

public sealed class AppErrorLogger
{
    private const string LogFileName = "app-error.log";
    private readonly object _gate = new();
    private readonly string _logPath;

    public AppErrorLogger(StackrootPaths paths)
        : this(paths.LogsRoot)
    {
    }

    public AppErrorLogger(string logsRoot)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(logsRoot);
        Directory.CreateDirectory(logsRoot);
        _logPath = Path.Combine(logsRoot, LogFileName);
    }

    public string LogPath => _logPath;

    public static AppErrorLogger ForDefaultPaths()
    {
        var appData = Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData);
        return new AppErrorLogger(Path.Combine(appData, "Stackroot", "logs"));
    }

    public void LogError(string message, string? context = null)
    {
        Write("ERROR", message, context, exception: null);
    }

    public void LogWarning(string message, string? context = null)
    {
        Write("WARN", message, context, exception: null);
    }

    public void Log(Exception exception, string? context = null)
    {
        ArgumentNullException.ThrowIfNull(exception);
        Write("ERROR", exception.Message, context, exception);
    }

    private void Write(string level, string message, string? context, Exception? exception)
    {
        try
        {
            var builder = new StringBuilder();
            builder.Append('[').Append(DateTimeOffset.Now.ToString("u")).Append("] ");
            builder.Append(level);
            if (!string.IsNullOrWhiteSpace(context))
            {
                builder.Append(" [").Append(context).Append(']');
            }

            builder.AppendLine();
            builder.AppendLine(message);

            if (exception is not null)
            {
                builder.AppendLine(exception.ToString());
            }

            builder.AppendLine("---");

            lock (_gate)
            {
                File.AppendAllText(_logPath, builder.ToString(), Encoding.UTF8);
            }
        }
        catch
        {
            // Best-effort logging only.
        }
    }
}
