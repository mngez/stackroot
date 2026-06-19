using Stackroot.Core.Abstractions;

namespace Stackroot.Core.Supervisor;

public sealed class GlobalProcessManager
{
    private readonly ProcessSupervisor _supervisor;
    private readonly GlobalProcessStore _store;
    private readonly IGlobalProcessArgvResolver? _argvResolver;

    public GlobalProcessManager(
        ProcessSupervisor supervisor,
        GlobalProcessStore store,
        IGlobalProcessArgvResolver? argvResolver = null)
    {
        _supervisor = supervisor;
        _store = store;
        _argvResolver = argvResolver;
        _supervisor.SetTargetResolver(ResolveRunTarget);
    }

    public event EventHandler? Changed;

    public IReadOnlyList<ProcessInfo> List(string? siteId = null)
    {
        var processes = _store.List();
        var filtered = string.IsNullOrWhiteSpace(siteId)
            ? processes
            : processes.Where(process => string.Equals(process.SiteId, siteId, StringComparison.OrdinalIgnoreCase)).ToList();

        return filtered.Select(process =>
            {
                try { return ToProcessInfo(process, null); }
                catch { return null; }
            })
            .Where(static snapshot => snapshot is not null)
            .Select(static snapshot => snapshot!)
            .ToList();
    }

    public GlobalProcess Add(GlobalProcess process)
    {
        if (!string.IsNullOrWhiteSpace(process.Id))
        {
            var existing = _store.GetById(process.Id);
            if (existing is not null)
            {
                throw new InvalidOperationException($"Process already exists: {process.Id}");
            }
        }

        var created = _store.Upsert(process);
        RaiseChanged();
        return created;
    }

    public GlobalProcess Update(string id, GlobalProcess process)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(id);
        var existing = _store.GetById(id) ?? throw new KeyNotFoundException($"Process not found: {id}");
        var merged = process with
        {
            Id = existing.Id,
            SiteId = process.SiteId ?? existing.SiteId,
            Description = process.Description ?? existing.Description,
            FromPreset = process.FromPreset ?? existing.FromPreset,
            NodeVersion = process.NodeVersion ?? existing.NodeVersion,
        };

        var updated = _store.Upsert(merged);
        RaiseChanged();
        return updated;
    }

    public bool Remove(string id)
    {
        var removed = _store.Remove(id);
        if (removed is null)
        {
            return false;
        }

        _supervisor.Stop(CreateScope(removed.Id));
        RaiseChanged();
        return true;
    }

    public ProcessInfo Start(string id)
    {
        var process = _store.GetById(id) ?? throw new KeyNotFoundException($"Process not found: {id}");
        var target = ToRunTarget(process);
        var snapshot = _supervisor.Start(target);
        RaiseChanged();
        return ToProcessInfo(process, snapshot);
    }

    public ProcessInfo Stop(string id)
    {
        var process = _store.GetById(id) ?? throw new KeyNotFoundException($"Process not found: {id}");
        _supervisor.Stop(CreateScope(process.Id));
        RaiseChanged();
        return ToProcessInfo(process);
    }

    public ProcessInfo Restart(string id)
    {
        var process = _store.GetById(id) ?? throw new KeyNotFoundException($"Process not found: {id}");
        Stop(id);
        Thread.Sleep(300);
        return Start(id);
    }

    public IReadOnlyList<ProcessInfo> StartAll(string? siteId = null)
    {
        var results = new List<ProcessInfo>();
        foreach (var process in _store.List().Where(p => p.Enabled))
        {
            if (!string.IsNullOrWhiteSpace(siteId) && !string.Equals(process.SiteId, siteId, StringComparison.OrdinalIgnoreCase))
            {
                continue;
            }

            try
            {
                var snapshot = _supervisor.Start(ToRunTarget(process));
                results.Add(ToProcessInfo(process, snapshot));
            }
            catch (Exception ex)
            {
                results.Add(ToProcessInfo(process, new ManagedProcessSnapshot(
                    CreateScope(process.Id),
                    process.Name,
                    ProcessStatus.Error,
                    null,
                    string.Join(' ', process.Argv),
                    ex.Message,
                    true)));
            }
        }

        RaiseChanged();
        return results;
    }

    public IReadOnlyList<ProcessInfo> AutoStart()
    {
        var results = new List<ProcessInfo>();
        foreach (var process in _store.List().Where(static p => p.Enabled && p.AutoStart))
        {
            try
            {
                var snapshot = _supervisor.Start(ToRunTarget(process));
                results.Add(ToProcessInfo(process, snapshot));
            }
            catch (Exception ex)
            {
                results.Add(ToProcessInfo(process, new ManagedProcessSnapshot(
                    CreateScope(process.Id),
                    process.Name,
                    ProcessStatus.Error,
                    null,
                    string.Join(' ', process.Argv ?? []),
                    ex.Message,
                    true)));
            }
        }

        RaiseChanged();
        return results;
    }

    public async Task<IReadOnlyList<ProcessInfo>> AutoStartAsync(
        TimeSpan? delayBetweenStarts = null,
        CancellationToken cancellationToken = default)
    {
        var results = new List<ProcessInfo>();
        var delay = delayBetweenStarts ?? TimeSpan.FromMilliseconds(250);
        foreach (var process in _store.List().Where(static p => p.Enabled && p.AutoStart))
        {
            cancellationToken.ThrowIfCancellationRequested();
            try
            {
                var snapshot = _supervisor.Start(ToRunTarget(process));
                results.Add(ToProcessInfo(process, snapshot));
            }
            catch (Exception ex)
            {
                results.Add(ToProcessInfo(process, new ManagedProcessSnapshot(
                    CreateScope(process.Id),
                    process.Name,
                    ProcessStatus.Error,
                    null,
                    string.Join(' ', process.Argv ?? []),
                    ex.Message,
                    true)));
            }

            await Task.Delay(delay, cancellationToken).ConfigureAwait(false);
        }

        RaiseChanged();
        return results;
    }

    public IReadOnlyList<ProcessInfo> StopAll(string? siteId = null)
    {
        _supervisor.StopAll();
        var results = new List<ProcessInfo>();
        foreach (var process in _store.List())
        {
            if (!string.IsNullOrWhiteSpace(siteId) && !string.Equals(process.SiteId, siteId, StringComparison.OrdinalIgnoreCase))
                continue;
            try { results.Add(ToProcessInfo(process)); } catch { }
        }
        RaiseChanged();
        return results;
    }

    public ProcessInfo SetEnabled(string id, bool enabled)
    {
        var process = _store.GetById(id) ?? throw new KeyNotFoundException($"Process not found: {id}");
        if (!enabled)
        {
            _supervisor.Stop(CreateScope(process.Id));
        }

        var updated = _store.Upsert(process with { Enabled = enabled });
        RaiseChanged();
        return ToProcessInfo(updated);
    }

    public ProcessLog GetLog(string id)
    {
        var process = _store.GetById(id) ?? throw new KeyNotFoundException($"Process not found: {id}");
        return _supervisor.GetLog(CreateScope(process.Id), string.Join(' ', process.Argv));
    }

    private ProcessInfo ToProcessInfo(GlobalProcess process, ManagedProcessSnapshot? snapshot = null)
    {
        snapshot ??= _supervisor.GetStatus(CreateScope(process.Id));
        var workDir = _argvResolver?.ResolveWorkDir(process) ?? ProcessWorkDir.Resolve(process);
        var resolvedCwd = ProcessWorkingDirectory.Resolve(workDir, process.Cwd);
        var argv = ResolveArgv(process);
        var available = ProcessAvailability.IsAvailable(process, argv, resolvedCwd);
        var status = snapshot?.Status ?? ProcessStatus.Stopped;
        var pid = snapshot?.Pid;

        if (status is ProcessStatus.Stopped
            && ProcessExternalProbe.TryDetectRunning(process, argv, resolvedCwd, out var externalPid))
        {
            status = ProcessStatus.Running;
            pid = externalPid;
        }

        return new ProcessInfo
        {
            Id = process.Id,
            Name = process.Name,
            Description = process.Description,
            Runtime = process.Runtime,
            RuntimeLabel = FormatRuntimeLabel(process),
            Argv = argv.ToList(),
            Cwd = process.Cwd ?? ".",
            WorkDir = !string.IsNullOrWhiteSpace(process.WorkDir) ? process.WorkDir : workDir,
            ResolvedCwd = resolvedCwd,
            Enabled = process.Enabled,
            AutoStart = process.AutoStart,
            Featured = process.Featured,
            Available = available,
            Status = status,
            CommandLine = _argvResolver?.FormatDisplayCommandLine(process, argv)
                ?? snapshot?.CommandLine
                ?? string.Join(' ', argv),
            PhpVersionId = process.PhpVersionId,
            NodeVersion = process.NodeVersion,
            RestartDelaySeconds = process.RestartDelaySeconds,
            Pid = pid,
            Message = snapshot?.Message,
            FromPreset = process.FromPreset,
            HasLog = true,
            Supervised = true,
            Scope = ProcessScopeType.Global,
            SiteId = process.SiteId
        };
    }

    private ProcessRunTarget ToRunTarget(GlobalProcess process) =>
        BuildRunTarget(process, ResolveArgv(process));

    private ProcessRunTarget? ResolveRunTarget(ProcessScope scope)
    {
        if (scope.Type != ProcessScopeType.Global || string.IsNullOrWhiteSpace(scope.ProcessId))
        {
            return null;
        }

        var process = _store.GetById(scope.ProcessId);
        return process is null ? null : ToRunTarget(process);
    }

    private IReadOnlyList<string> ResolveArgv(GlobalProcess process) =>
        _argvResolver?.Resolve(process) ?? process.Argv ?? [];

    private void RaiseChanged() => Changed?.Invoke(this, EventArgs.Empty);

    private ProcessRunTarget BuildRunTarget(GlobalProcess process, IReadOnlyList<string> argv)
    {
        if (argv.Count == 0 || string.IsNullOrWhiteSpace(argv[0]))
        {
            throw new InvalidOperationException($"Global process {process.Id} has no executable.");
        }

        var executable = argv[0];
        var arguments = argv.Skip(1).ToList();
        var workDir = _argvResolver?.ResolveWorkDir(process) ?? ProcessWorkDir.Resolve(process);
        var cwd = ProcessWorkingDirectory.Resolve(workDir, process.Cwd);
        var environment = _argvResolver?.BuildEnvironment(process);

        return new ProcessRunTarget(
            CreateScope(process.Id),
            process.Name,
            executable,
            arguments,
            cwd,
            environment,
            Supervised: true,
            RestartDelaySeconds: process.RestartDelaySeconds);
    }

    private static string FormatRuntimeLabel(GlobalProcess process)
    {
        if (!string.IsNullOrWhiteSpace(process.PhpVersionId))
        {
            return $"PHP {process.PhpVersionId}";
        }

        return process.Runtime switch
        {
            SiteCommandRuntime.Php => "PHP",
            SiteCommandRuntime.Composer => "Composer",
            SiteCommandRuntime.Npm => "npm",
            SiteCommandRuntime.Node => "Node",
            SiteCommandRuntime.Python => "Python",
            _ => process.Runtime.ToString()
        };
    }

    private static ProcessScope CreateScope(string processId) =>
        new()
        {
            Type = ProcessScopeType.Global,
            ProcessId = processId
        };
}
