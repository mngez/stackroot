using System.Diagnostics;
using System.IO;
using System.Text;
using Stackroot.Core.Abstractions;
using Stackroot.Core.Windows;

namespace Stackroot.Core.Supervisor;

public sealed class ProcessSupervisor : IDisposable
{
    private const int MaxLogLength = 128_000;
    private readonly object _sync = new();
    private readonly Dictionary<string, ManagedProcess> _running = new(StringComparer.OrdinalIgnoreCase);
    private readonly Dictionary<string, LogBuffer> _logs = new(StringComparer.OrdinalIgnoreCase);
    private readonly IProcessJobManager _jobManager;
    private Func<ProcessScope, ProcessRunTarget?>? _resolveTarget;
    private bool _disposed;

    public ProcessSupervisor(IProcessJobManager jobManager)
    {
        _jobManager = jobManager;
    }

    public void SetTargetResolver(Func<ProcessScope, ProcessRunTarget?>? resolver) =>
        _resolveTarget = resolver;

    public ManagedProcessSnapshot Start(ProcessRunTarget target)
    {
        ThrowIfDisposed();
        Stop(target.Scope);
        return StartInternal(target, restartAttempt: 0);
    }

    public ManagedProcessSnapshot Restart(ProcessRunTarget target)
    {
        ThrowIfDisposed();
        Stop(target.Scope);
        ProcessPortTools.InvalidatePortCache();
        ReleaseOrphanListeners(target);
        Thread.Sleep(300);
        return StartInternal(target, restartAttempt: 0);
    }

    public ManagedProcessSnapshot? GetStatus(ProcessScope scope)
    {
        ThrowIfDisposed();
        var key = scope.ToScopeKey();
        lock (_sync)
        {
            return _running.TryGetValue(key, out var managed) ? managed.ToSnapshot() : null;
        }
    }

    public IReadOnlyList<ManagedProcessSnapshot> ListStatuses()
    {
        ThrowIfDisposed();
        lock (_sync)
        {
            return _running.Values.Select(v => v.ToSnapshot()).ToList();
        }
    }

    public ProcessLog GetLog(ProcessScope scope, string? commandLine = null)
    {
        ThrowIfDisposed();
        var key = scope.ToScopeKey();
        string content;
        bool running;
        int? pid;
        string? cmdLine;
        lock (_sync)
        {
            _logs.TryGetValue(key, out var log);
            _running.TryGetValue(key, out var m);
            content = log?.Content.ToString() ?? string.Empty;
            cmdLine = m?.CommandLine ?? log?.CommandLine ?? commandLine;
            // Snapshot the Process reference so we can check HasExited / Id
            // OUTSIDE the lock — both can hang on zombie processes.
            var process = m?.Process;
            if (process is not null)
            {
                running = IsProcessRunning(process);
                pid = TryGetProcessId(process);
            }
            else
            {
                running = false;
                pid = null;
            }
        }

        return new ProcessLog
        {
            Content = content,
            Running = running,
            Pid = pid,
            CommandLine = cmdLine
        };
    }

    public void Stop(ProcessScope scope, bool ignoreDisposed = false)
    {
        if (!ignoreDisposed)
        {
            ThrowIfDisposed();
        }
        var key = scope.ToScopeKey();
        ManagedProcess? managed;

        lock (_sync)
        {
            if (!_running.TryGetValue(key, out managed))
            {
                return;
            }

            managed.Stopping = true;
            CancelRestart(managed);
        }

        try
        {
            ForceStopManagedProcess(managed);
        }
        finally
        {
            lock (_sync)
            {
                _running.Remove(key);
            }

            managed.Process.Dispose();
        }
    }

    public void StopAll()
    {
        ThrowIfDisposed();
        List<ManagedProcess> snapshot;
        lock (_sync)
        {
            snapshot = _running.Values.ToList();
            foreach (var m in snapshot) { m.Stopping = true; CancelRestart(m); }
            _running.Clear();
        }

        // Kill every managed process by PID. ProcessKiller.TryKill handles
        // HasExited / Kill / WaitForExit safely without touching the original
        // Process objects, whose .Dispose() can hang on zombie processes.
        foreach (var managed in snapshot)
        {
            try
            {
                if (managed.RootPid is int rootPid) ProcessKiller.TryKill(rootPid);
            }
            catch { }
        }
    }

    public void Dispose()
    {
        if (_disposed)
        {
            return;
        }

        List<ProcessScope> scopes;
        lock (_sync)
        {
            scopes = _running.Values.Select(v => v.Scope).ToList();
        }

        foreach (var scope in scopes)
        {
            Stop(scope, ignoreDisposed: true);
        }

        _disposed = true;
        GC.SuppressFinalize(this);
    }

    private ManagedProcessSnapshot StartInternal(ProcessRunTarget target, int restartAttempt)
    {
        var key = target.Scope.ToScopeKey();
        ReleaseOrphanListeners(target);

        var commandLine = $"{target.Executable} {string.Join(' ', target.Arguments)}".Trim();
        var startInfo = ProcessStreamEncoding.Create(target.Executable, target.WorkingDirectory);

        foreach (var argument in target.Arguments)
        {
            startInfo.ArgumentList.Add(argument);
        }

        if (target.EnvironmentVariables is not null)
        {
            foreach (var pair in target.EnvironmentVariables)
            {
                startInfo.Environment[pair.Key] = pair.Value;
            }
        }

        SiteProcessEnvironment.StripCiEnvironment(startInfo.Environment);

        var process = new Process
        {
            StartInfo = startInfo,
            EnableRaisingEvents = true
        };

        var managed = new ManagedProcess(target, process, ProcessStatus.Running, null, restartAttempt, commandLine);
        if (restartAttempt == 0)
        {
            InitializeLog(key, target, commandLine);
        }
        else
        {
            AppendRestartSectionIfConfigChanged(key, target, commandLine, restartAttempt);
        }
        WireOutputHandlers(process, key);
        WireExitHandler(process, key);

        if (!process.Start())
        {
            throw new InvalidOperationException($"Failed to start process: {target.Executable}");
        }

        managed.RootPid = process.Id;
        process.BeginOutputReadLine();
        process.BeginErrorReadLine();
        _jobManager.AssignProcess(process.Id);

        ManagedProcessSnapshot? duplicateSnapshot = null;

        lock (_sync)
        {
            if (_running.TryGetValue(key, out var previous) && previous != managed)
            {
                if (previous.Stopping)
                {
                    CancelRestart(previous);
                }
                else if (IsProcessRunning(previous.Process))
                {
                    duplicateSnapshot = previous.ToSnapshot();
                    _running[key] = previous;
                }
                else
                {
                    CancelRestart(previous);
                    try
                    {
                        previous.Process.Dispose();
                    }
                    catch
                    {
                        // Best-effort cleanup of the exited process handle.
                    }
                }
            }

            if (duplicateSnapshot is null)
            {
                _running[key] = managed;
            }
        }

        // Kill and dispose OUTSIDE the lock — ForceStopManagedProcess calls
        // Process.Kill + WaitForExit and Process.Dispose() can hang on zombie
        // processes. Neither must ever be called while holding _sync.
        if (duplicateSnapshot is not null)
        {
            ForceStopManagedProcess(managed);
            process.Dispose();
            return duplicateSnapshot;
        }

        return managed.ToSnapshot();
    }

    private void WireOutputHandlers(Process process, string key)
    {
        process.OutputDataReceived += (_, args) =>
        {
            if (args.Data is not null)
            {
                AppendLog(key, args.Data);
            }
        };

        process.ErrorDataReceived += (_, args) =>
        {
            if (args.Data is not null)
            {
                AppendLog(key, $"[err] {args.Data}");
            }
        };
    }

    private void WireExitHandler(Process process, string key)
    {
        process.Exited += async (_, _) =>
        {
            ManagedProcess? current;
            lock (_sync)
            {
                _running.TryGetValue(key, out current);
            }

            if (current is null)
            {
                process.Dispose();
                return;
            }

            AppendLog(key, $"[exit] code {process.ExitCode}");
            if (current.Stopping)
            {
                lock (_sync)
                {
                    _running.Remove(key);
                }

                process.Dispose();
                return;
            }

            if (!current.Target.Supervised)
            {
                current.Status = process.ExitCode == 0 ? ProcessStatus.Stopped : ProcessStatus.Error;
                current.Message = process.ExitCode == 0 ? null : $"Exited with code {process.ExitCode}";
                lock (_sync)
                {
                    _running.Remove(key);
                }

                process.Dispose();
                return;
            }

            var nextAttempt = current.RestartAttempt + 1;
            var delayMs = ComputeRestartDelayMs(current.Target, current.RestartAttempt);
            var restartCancellation = new CancellationTokenSource();
            bool stopping;
            lock (_sync)
            {
                stopping = current.Stopping;
                if (!stopping)
                {
                    current.RestartCancellation = restartCancellation;
                    current.Status = ProcessStatus.Restarting;
                    current.Message = $"Restarting in {delayMs / 1000}s...";
                }
            }

            // Dispose outside the lock — Process.Dispose() can hang on zombie
            // processes and must never be called while holding _sync.
            if (stopping)
            {
                restartCancellation.Dispose();
                process.Dispose();
                return;
            }

            AppendLog(key, $"[supervisor] restarting in {delayMs}ms");
            process.Dispose();

            try
            {
                await Task.Delay(delayMs, restartCancellation.Token).ConfigureAwait(false);
            }
            catch (OperationCanceledException)
            {
                restartCancellation.Dispose();
                return;
            }

            lock (_sync)
            {
                if (!_running.TryGetValue(key, out var latest) || latest.Stopping || latest.RestartCancellation != restartCancellation)
                {
                    restartCancellation.Dispose();
                    return;
                }

                latest.RestartCancellation = null;
            }

            restartCancellation.Dispose();

            try
            {
                var target = _resolveTarget?.Invoke(current.Target.Scope) ?? current.Target;
                StartInternal(target, nextAttempt);
            }
            catch (Exception ex)
            {
                AppendLog(key, $"[supervisor] restart failed: {ex.Message}");
                lock (_sync)
                {
                    if (_running.TryGetValue(key, out var latest))
                    {
                        latest.Status = ProcessStatus.Error;
                        latest.Message = ex.Message;
                    }
                }
            }
        };
    }

    private static void ForceStopManagedProcess(ManagedProcess managed)
    {
        if (managed.RootPid is int rootPid)
        {
            ProcessKiller.TryKill(rootPid);
        }

        try
        {
            if (IsProcessRunning(managed.Process))
            {
                managed.Process.Kill(entireProcessTree: true);
                managed.Process.WaitForExit(5000);
            }
        }
        catch
        {
            // Best-effort stop.
        }

        ReleaseOrphanListeners(managed.Target);
    }

    private static void ReleaseOrphanListeners(ProcessRunTarget target)
    {
        var port = ProcessPortTools.TryResolveListenPort(target.Executable, target.Arguments);
        if (port is not > 0)
        {
            return;
        }

        ProcessPortTools.InvalidatePortCache(port);
        ProcessPortTools.KillMatchingListenersOnPort(port.Value, target.Executable);
    }

    private static void CancelRestart(ManagedProcess managed)
    {
        var restartCancellation = managed.RestartCancellation;
        if (restartCancellation is null)
        {
            return;
        }

        managed.RestartCancellation = null;
        restartCancellation.Cancel();
        restartCancellation.Dispose();
    }

    private void AppendRestartSectionIfConfigChanged(string key, ProcessRunTarget target, string commandLine, int restartAttempt)
    {
        var fingerprint = BuildConfigFingerprint(target, commandLine);
        lock (_sync)
        {
            if (!_logs.TryGetValue(key, out var log))
            {
                log = new LogBuffer(commandLine);
                WriteLogPreamble(log, target, commandLine);
                log.ConfigFingerprint = fingerprint;
                _logs[key] = log;
                return;
            }

            if (string.Equals(log.ConfigFingerprint, fingerprint, StringComparison.Ordinal))
            {
                return;
            }

            log.Content.AppendLine();
            log.Content.AppendLine($"[supervisor] --- restart #{restartAttempt} (config updated) at {DateTimeOffset.Now:u} ---");
            WriteLogPreamble(log, target, commandLine);
            log.ConfigFingerprint = fingerprint;
        }
    }

    private void InitializeLog(string key, ProcessRunTarget target, string commandLine)
    {
        lock (_sync)
        {
            var log = new LogBuffer(commandLine);
            WriteLogPreamble(log, target, commandLine);
            log.ConfigFingerprint = BuildConfigFingerprint(target, commandLine);
            _logs[key] = log;
        }
    }

    private static void WriteLogPreamble(LogBuffer log, ProcessRunTarget target, string commandLine)
    {
        log.Content.AppendLine($"[supervisor] starting {target.Label}");
        log.Content.AppendLine($"[supervisor] cwd: {target.WorkingDirectory}");
        log.Content.AppendLine($"[supervisor] command: {commandLine}");
        if (target.RestartDelaySeconds is int delaySeconds)
        {
            log.Content.AppendLine($"[supervisor] restart delay: {delaySeconds}s");
        }

        if (target.EnvironmentVariables is not null
            && target.EnvironmentVariables.TryGetValue("PHPRC", out var phpRc)
            && !string.IsNullOrWhiteSpace(phpRc))
        {
            log.Content.AppendLine($"[supervisor] PHPRC: {phpRc}");
        }
    }

    private static string BuildConfigFingerprint(ProcessRunTarget target, string commandLine)
    {
        var env = target.EnvironmentVariables is null
            ? string.Empty
            : string.Join(
                ';',
                target.EnvironmentVariables
                    .OrderBy(static pair => pair.Key, StringComparer.OrdinalIgnoreCase)
                    .Select(static pair => $"{pair.Key}={pair.Value}"));

        return $"{target.Label}|{target.WorkingDirectory}|{commandLine}|{target.RestartDelaySeconds}|{env}";
    }

    private void AppendLog(string key, string line)
    {
        lock (_sync)
        {
            if (!_logs.TryGetValue(key, out var log))
            {
                log = new LogBuffer(string.Empty);
                _logs[key] = log;
            }

            log.Content.AppendLine(line);
            if (log.Content.Length <= MaxLogLength)
            {
                return;
            }

            var trimmed = log.Content.ToString();
            log.Content.Clear();
            log.Content.Append(trimmed[^MaxLogLength..]);
        }
    }

    private static int ComputeRestartDelayMs(ProcessRunTarget target, int restartAttempt)
    {
        if (target.RestartDelaySeconds is int seconds and > 0)
        {
            return seconds * 1000;
        }

        if (restartAttempt <= 0)
        {
            return 2000;
        }

        var cappedAttempt = Math.Min(restartAttempt, 4);
        return Math.Min(2000 << cappedAttempt, 30_000);
    }

    private void ThrowIfDisposed()
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
    }

    private sealed class ManagedProcess
    {
        public ManagedProcess(
            ProcessRunTarget target,
            Process process,
            ProcessStatus status,
            string? message,
            int restartAttempt,
            string commandLine)
        {
            Target = target;
            Process = process;
            Status = status;
            Message = message;
            RestartAttempt = restartAttempt;
            CommandLine = commandLine;
        }

        public ProcessRunTarget Target { get; }
        public Process Process { get; }
        public ProcessStatus Status { get; set; }
        public string? Message { get; set; }
        public int RestartAttempt { get; set; }
        public bool Stopping { get; set; }
        public int? RootPid { get; set; }
        public CancellationTokenSource? RestartCancellation { get; set; }
        public string CommandLine { get; }
        public ProcessScope Scope => Target.Scope;

        public ManagedProcessSnapshot ToSnapshot()
        {
            return new ManagedProcessSnapshot(
                Scope,
                Target.Label,
                Status,
                RootPid ?? TryGetProcessId(Process),
                CommandLine,
                Message,
                Target.Supervised);
        }
    }

    private static bool IsProcessRunning(Process process)
    {
        try
        {
            return !process.HasExited;
        }
        catch (InvalidOperationException)
        {
            return false;
        }
    }

    private static int? TryGetProcessId(Process process)
    {
        try
        {
            return process.HasExited ? null : process.Id;
        }
        catch (InvalidOperationException)
        {
            return null;
        }
    }

    private sealed class LogBuffer
    {
        public LogBuffer(string commandLine)
        {
            CommandLine = commandLine;
            Content = new StringBuilder();
        }

        public StringBuilder Content { get; }
        public string CommandLine { get; }
        public string? ConfigFingerprint { get; set; }
    }
}
