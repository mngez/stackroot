using System.IO;
using System.Text.Json;
using Stackroot.Core.Abstractions;
using Stackroot.Core.Abstractions.DataDocuments;
using Stackroot.Core.IO;

namespace Stackroot.App.Scheduling;

public sealed class TaskSchedulerService : IDisposable
{
    private readonly string _storePath;
    private List<ScheduledTaskModel> _tasks = [];
    private readonly HashSet<string> _runningTaskIds = new(StringComparer.OrdinalIgnoreCase);
    private readonly System.Timers.Timer _timer;
    private readonly object _lock = new();
    private volatile bool _isStarted;

    public event EventHandler? TaskExecuted;
    public event EventHandler? StatusChanged;

    public bool IsStarted => _isStarted;

    public TaskSchedulerService(StackrootPaths paths)
    {
        ArgumentNullException.ThrowIfNull(paths);
        _storePath = StackrootPathResolver.ScheduledTasksPath(paths.DataRoot);

        Load();

        _timer = new System.Timers.Timer(30_000);
        _timer.Elapsed += OnTick;
        _timer.AutoReset = true;
    }

    public void Start()
    {
        _timer.Start();
        _isStarted = true;
        RaiseStatusChanged();
    }

    public IReadOnlyList<ScheduledTaskModel> List()
    {
        lock (_lock) return _tasks.ToList();
    }

    public void Add(ScheduledTaskModel task)
    {
        lock (_lock) _tasks.Add(task);
        Save();
    }

    public void Update(ScheduledTaskModel task)
    {
        lock (_lock)
        {
            var idx = _tasks.FindIndex(t => t.Id == task.Id);
            if (idx >= 0) _tasks[idx] = task;
        }
        Save();
    }

    public void Delete(string id)
    {
        lock (_lock) _tasks.RemoveAll(t => t.Id == id);
        Save();
    }

    public Task RunNowAsync(string id)
    {
        return Task.Run(() => ExecuteTask(id));
    }

    private void OnTick(object? sender, System.Timers.ElapsedEventArgs e)
    {
        if (ApplicationShutdownState.ShutdownRequested || ApplicationShutdownState.IsShuttingDown)
        {
            return;
        }

        List<ScheduledTaskModel> snapshot;
        lock (_lock) snapshot = _tasks.Where(t => t.IsEnabled).ToList();

        var now = DateTime.Now;
        foreach (var task in snapshot)
        {
            var next = CronParser.GetNextRun(task.CronExpression, now.AddMinutes(-1));
            if (next.HasValue && next.Value <= now)
            {
                _ = Task.Run(() => ExecuteTask(task.Id));
            }
        }
    }

    private void ExecuteTask(string taskId)
    {
        ScheduledTaskModel? task;
        lock (_lock)
        {
            task = _tasks.FirstOrDefault(t => t.Id == taskId);
            if (task is null || !_runningTaskIds.Add(taskId))
            {
                return;
            }
        }

        var command = task.Command;
        var workingDirectory = task.WorkingDirectory;
        var logPath = task.CaptureLog
            ? Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData),
                "Stackroot", "logs", "scheduled", $"task-{task.Id}.log")
            : null;
        string? lastRunAt;
        string? lastError;

        try
        {
            var psi = new System.Diagnostics.ProcessStartInfo
            {
                FileName = "cmd.exe",
                Arguments = $"/c \"{command}\"",
                WorkingDirectory = string.IsNullOrWhiteSpace(workingDirectory)
                    ? Environment.GetFolderPath(Environment.SpecialFolder.UserProfile)
                    : workingDirectory,
                UseShellExecute = false,
                CreateNoWindow = true,
                RedirectStandardOutput = true,
                RedirectStandardError = true
            };

            var process = System.Diagnostics.Process.Start(psi);
            if (process is null)
            {
                lastRunAt = DateTime.Now.ToString("O");
                lastError = "Failed to start process.";
                UpdateTaskExecutionResult(taskId, lastRunAt, logPath, lastError);
                return;
            }

            var stdout = process.StandardOutput.ReadToEnd();
            var stderr = process.StandardError.ReadToEnd();
            process.WaitForExit(60_000);

            if (logPath is not null)
            {
                var dir = Path.GetDirectoryName(logPath);
                if (dir is not null) Directory.CreateDirectory(dir);
                var runHeader = $"\r\n=== {DateTime.Now:yyyy-MM-dd HH:mm:ss} (exit: {process.ExitCode}) ===\r\n";
                var body = string.IsNullOrEmpty(stdout) && string.IsNullOrEmpty(stderr)
                    ? "(no output)"
                    : $"{stdout}\r\n{stderr}".Trim();
                File.AppendAllText(logPath, runHeader + body + "\r\n");
            }

            lastRunAt = DateTime.Now.ToString("O");
            lastError = process.ExitCode != 0 ? $"Exit code: {process.ExitCode}" : null;
            UpdateTaskExecutionResult(taskId, lastRunAt, logPath, lastError);
        }
        catch (Exception ex)
        {
            lastRunAt = DateTime.Now.ToString("O");
            lastError = ex.Message;
            UpdateTaskExecutionResult(taskId, lastRunAt, logPath, lastError);
        }
        finally
        {
            lock (_lock)
            {
                _runningTaskIds.Remove(taskId);
            }
        }
    }

    private void RaiseStatusChanged()
    {
        try { StatusChanged?.Invoke(this, EventArgs.Empty); } catch { }
    }

    private void Load()
    {
        try
        {
            if (!File.Exists(_storePath))
            {
                return;
            }

            var json = File.ReadAllText(_storePath);
            var document = JsonSerializer.Deserialize<ScheduledTasksDocument>(json, JsonSerializerConfig.Default);
            lock (_lock)
            {
                _tasks = document?.Tasks?.Select(ToModel).ToList() ?? [];
            }
        }
        catch
        {
            // Keep the in-memory snapshot unchanged on read failures.
        }
    }

    private void Save()
    {
        try
        {
            var dir = Path.GetDirectoryName(_storePath);
            if (dir is not null) Directory.CreateDirectory(dir);

            ScheduledTasksDocument document;
            lock (_lock)
            {
                document = new ScheduledTasksDocument
                {
                    SchemaVersion = DataDocumentSchemas.ScheduledTasks,
                    Tasks = _tasks.Select(ToEntry).ToList()
                };
            }

            var json = JsonSerializer.Serialize(document, JsonSerializerConfig.Default);
            var tempPath = $"{_storePath}.tmp";
            File.WriteAllText(tempPath, json);
            if (File.Exists(_storePath))
            {
                File.Replace(tempPath, _storePath, null, ignoreMetadataErrors: true);
            }
            else
            {
                File.Move(tempPath, _storePath);
            }
        }
        catch { }
    }

    private void UpdateTaskExecutionResult(string taskId, string? lastRunAt, string? lastLogPath, string? lastError)
    {
        lock (_lock)
        {
            var task = _tasks.FirstOrDefault(t => t.Id == taskId);
            if (task is null)
            {
                return;
            }

            task.LastRunAt = lastRunAt;
            task.LastLogPath = lastLogPath;
            task.LastError = lastError;
        }

        Save();
        try { TaskExecuted?.Invoke(this, EventArgs.Empty); } catch { }
        RaiseStatusChanged();
    }

    private static ScheduledTaskModel ToModel(ScheduledTaskEntry entry) => new()
    {
        Id = entry.Id,
        Label = entry.Label,
        Command = entry.Command,
        WorkingDirectory = entry.WorkingDirectory,
        CronExpression = entry.CronExpression,
        CaptureLog = entry.CaptureLog,
        IsEnabled = entry.IsEnabled,
        LastRunAt = entry.LastRunAt,
        LastLogPath = entry.LastLogPath,
        LastError = entry.LastError,
        SiteId = entry.SiteId
    };

    private static ScheduledTaskEntry ToEntry(ScheduledTaskModel model) => new()
    {
        Id = model.Id,
        Label = model.Label,
        Command = model.Command,
        WorkingDirectory = model.WorkingDirectory,
        CronExpression = model.CronExpression,
        CaptureLog = model.CaptureLog,
        IsEnabled = model.IsEnabled,
        LastRunAt = model.LastRunAt,
        LastLogPath = model.LastLogPath,
        LastError = model.LastError,
        SiteId = model.SiteId
    };

    public void Dispose()
    {
        _isStarted = false;
        _timer.Stop();
        _timer.Dispose();
    }
}
