using System.Diagnostics;
using System.IO;
using System.Timers;

namespace Stackroot.App.Scheduling;

public sealed class TaskSchedulerService : IDisposable
{
    private readonly string _storePath;
    private List<ScheduledTaskModel> _tasks = [];
    private readonly System.Timers.Timer _timer;
    private readonly object _lock = new();

    public event EventHandler? TaskExecuted;

    public TaskSchedulerService()
    {
        var appData = Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData);
        _storePath = Path.Combine(appData, "Stackroot", "scheduled-tasks.json");

        Load(); // Load immediately, don't wait for Start()

        _timer = new System.Timers.Timer(30_000);
        _timer.Elapsed += OnTick;
        _timer.AutoReset = true;
    }

    public void Start()
    {
        _timer.Start();
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
        ScheduledTaskModel? task;
        lock (_lock) task = _tasks.FirstOrDefault(t => t.Id == id);
        if (task is null) return Task.CompletedTask;
        return Task.Run(() => ExecuteTask(task));
    }

    private void OnTick(object? sender, ElapsedEventArgs e)
    {
        List<ScheduledTaskModel> snapshot;
        lock (_lock) snapshot = _tasks.Where(t => t.IsEnabled).ToList();

        var now = DateTime.Now;
        foreach (var task in snapshot)
        {
            var next = CronParser.GetNextRun(task.CronExpression, now.AddMinutes(-1));
            if (next.HasValue && next.Value <= now)
            {
                ExecuteTask(task);
            }
        }
    }

    private void ExecuteTask(ScheduledTaskModel task)
    {
        var logPath = task.CaptureLog
            ? Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData),
                "Stackroot", "logs", "scheduled", $"task-{task.Id}.log")
            : null;

        try
        {
            var psi = new ProcessStartInfo
            {
                FileName = "cmd.exe",
                Arguments = $"/c \"{task.Command}\"",
                WorkingDirectory = string.IsNullOrWhiteSpace(task.WorkingDirectory)
                    ? Environment.GetFolderPath(Environment.SpecialFolder.UserProfile)
                    : task.WorkingDirectory,
                UseShellExecute = false,
                CreateNoWindow = true,
                RedirectStandardOutput = true,
                RedirectStandardError = true
            };

            var process = Process.Start(psi);
            if (process is null)
            {
                task.LastError = "Failed to start process.";
                return;
            }

            var stdout = process.StandardOutput.ReadToEnd();
            var stderr = process.StandardError.ReadToEnd();
            process.WaitForExit(60_000);

            // Write log file if capture is enabled
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

            task.LastRunAt = DateTime.Now.ToString("O");
            task.LastLogPath = logPath;
            task.LastError = process.ExitCode != 0 ? $"Exit code: {process.ExitCode}" : null;
        }
        catch (Exception ex)
        {
            task.LastRunAt = DateTime.Now.ToString("O");
            task.LastError = ex.Message;
        }

        Save();
        try { TaskExecuted?.Invoke(this, EventArgs.Empty); } catch { }
    }

    private void Load()
    {
        try
        {
            if (File.Exists(_storePath))
            {
                var json = File.ReadAllText(_storePath);
                lock (_lock) _tasks = System.Text.Json.JsonSerializer.Deserialize<List<ScheduledTaskModel>>(json) ?? [];
            }
        }
        catch { lock (_lock) _tasks = []; }
    }

    private void Save()
    {
        try
        {
            var dir = Path.GetDirectoryName(_storePath);
            if (dir is not null) Directory.CreateDirectory(dir);
            string json;
            lock (_lock) json = System.Text.Json.JsonSerializer.Serialize(_tasks);
            File.WriteAllText(_storePath, json);
        }
        catch { }
    }

    public void Dispose()
    {
        _timer.Stop();
        _timer.Dispose();
    }
}
