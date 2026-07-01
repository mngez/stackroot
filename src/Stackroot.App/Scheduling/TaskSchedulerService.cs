using System.IO;
using Stackroot.App.Services;
using Stackroot.Core.Abstractions;
using Stackroot.Core.Abstractions.DataDocuments;
using Stackroot.Core.IO;
using Stackroot.Core.Sites.Commands;
using Stackroot.Core.Sites.Persistence;
using SiteModel = Stackroot.Core.Sites.Models.Site;

namespace Stackroot.App.Scheduling;

public sealed class TaskSchedulerService : IDisposable
{
    private readonly string _storePath;
    private readonly SiteCommandRunner _commandRunner;
    private readonly SiteStore _siteStore;
    private readonly StackrootStartupReadyGate _startupReadyGate;
    private List<ScheduledTaskModel> _tasks = [];
    private readonly HashSet<string> _runningTaskIds = new(StringComparer.OrdinalIgnoreCase);
    private readonly System.Timers.Timer _timer;
    private readonly object _lock = new();
    private volatile bool _isStarted;

    public event EventHandler? TaskExecuted;
    public event EventHandler? StatusChanged;

    public bool IsStarted => _isStarted;

    public TaskSchedulerService(
        StackrootPaths paths,
        SiteCommandRunner commandRunner,
        SiteStore siteStore,
        StackrootStartupReadyGate startupReadyGate)
    {
        ArgumentNullException.ThrowIfNull(paths);
        ArgumentNullException.ThrowIfNull(commandRunner);
        ArgumentNullException.ThrowIfNull(siteStore);
        ArgumentNullException.ThrowIfNull(startupReadyGate);

        _storePath = StackrootPathResolver.ScheduledTasksPath(paths.DataRoot);
        _commandRunner = commandRunner;
        _siteStore = siteStore;
        _startupReadyGate = startupReadyGate;

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

    public void DeleteBySiteId(string siteId)
    {
        lock (_lock) _tasks.RemoveAll(t => string.Equals(t.SiteId, siteId, StringComparison.OrdinalIgnoreCase));
        Save();
    }

    public Task RunNowAsync(string id)
    {
        return Task.Run(async () =>
        {
            await _startupReadyGate.WaitAsync().ConfigureAwait(false);
            ExecuteTask(id);
        });
    }

    private void OnTick(object? sender, System.Timers.ElapsedEventArgs e)
    {
        if (!_startupReadyGate.IsReady)
        {
            return;
        }

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
        var workingDirectory = ResolveWorkingDirectory(task);
        SiteModel? site = ResolveSite(task.SiteId);
        var phpVersionId = _commandRunner.ResolvePhpVersionId(site);

        string? persistedLogPath = null;
        string? tempLogPath = null;
        string? lastRunAt;
        string? lastError;

        try
        {
            var logPath = task.CaptureLog
                ? _commandRunner.CreateScheduledTaskLogFile(task.Id)
                : CreateDiscardLogFile(task.Id, out tempLogPath);

            var result = _commandRunner.RunShellCommand(command, workingDirectory, phpVersionId, logPath);

            if (task.CaptureLog)
            {
                persistedLogPath = logPath;
            }

            lastRunAt = DateTime.Now.ToString("O");
            lastError = result.ExitCode switch
            {
                0 => null,
                -1 when result.Stderr.Contains("Command cancelled.", StringComparison.Ordinal) => "Command cancelled.",
                _ => $"Exit code: {result.ExitCode}"
            };
            UpdateTaskExecutionResult(taskId, lastRunAt, persistedLogPath, lastError);
        }
        catch (Exception ex)
        {
            lastRunAt = DateTime.Now.ToString("O");
            lastError = ex.Message;
            UpdateTaskExecutionResult(taskId, lastRunAt, persistedLogPath, lastError);
        }
        finally
        {
            if (tempLogPath is not null)
            {
                TryDeleteFile(tempLogPath);
            }

            lock (_lock)
            {
                _runningTaskIds.Remove(taskId);
            }
        }
    }

    private static string ResolveWorkingDirectory(ScheduledTaskModel task)
    {
        if (!string.IsNullOrWhiteSpace(task.WorkingDirectory) && Directory.Exists(task.WorkingDirectory))
        {
            return task.WorkingDirectory;
        }

        return Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);
    }

    private SiteModel? ResolveSite(string? siteId)
    {
        if (string.IsNullOrWhiteSpace(siteId))
        {
            return null;
        }

        return _siteStore.GetById(siteId);
    }

    private static string CreateDiscardLogFile(string taskId, out string tempLogPath)
    {
        tempLogPath = Path.Combine(
            Path.GetTempPath(),
            $"stackroot-scheduled-{SanitizeTaskId(taskId)}-{Guid.NewGuid():N}.log");
        File.WriteAllBytes(tempLogPath, Array.Empty<byte>());
        return tempLogPath;
    }

    private static string SanitizeTaskId(string taskId)
    {
        var slug = new string(taskId.Where(static c => char.IsLetterOrDigit(c) || c is '-' or '_').ToArray());
        return string.IsNullOrWhiteSpace(slug) ? "task" : slug.Length <= 32 ? slug : slug[..32];
    }

    private static void TryDeleteFile(string path)
    {
        try
        {
            if (File.Exists(path))
            {
                File.Delete(path);
            }
        }
        catch
        {
            // Best effort.
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
            var document = System.Text.Json.JsonSerializer.Deserialize<ScheduledTasksDocument>(json, JsonSerializerConfig.Default);
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

            var json = System.Text.Json.JsonSerializer.Serialize(document, JsonSerializerConfig.Default);
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
