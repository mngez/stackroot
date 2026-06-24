using Stackroot.App.Helpers;
using Stackroot.App.Scheduling;
using Stackroot.Core.Abstractions;
using Stackroot.Core.AdminTools;
using Stackroot.Core.Catalog;
using Stackroot.Core.Observability;
using Stackroot.Core.Services;

namespace Stackroot.App.Services;

public sealed class SessionActivityCoordinator : IDisposable
{
    private readonly SessionActivityService _activity;
    private readonly ServiceManager _serviceManager;
    private readonly MailpitManager _mailpitManager;
    private readonly PackageInstaller _packageInstaller;
    private readonly DeferredStartupCoordinator _deferredStartup;
    private readonly TaskSchedulerService _taskScheduler;
    private readonly StackrootStartupReadyGate _startupReadyGate;
    private readonly IDiagnosticsReporter _diagnostics;
    private readonly object _sync = new();
    private readonly HashSet<string> _awaitingServiceStarts = new(StringComparer.OrdinalIgnoreCase);
    private readonly HashSet<string> _reportedPhpListeners = new(StringComparer.OrdinalIgnoreCase);
    private readonly Dictionary<string, ServiceStatus?> _lastServiceStatus = new(StringComparer.OrdinalIgnoreCase);
    private readonly Dictionary<string, Guid> _serviceProgressIds = new(StringComparer.OrdinalIgnoreCase);
    private readonly Dictionary<string, Guid> _installProgressIds = new(StringComparer.OrdinalIgnoreCase);
    private readonly Dictionary<string, int> _suppressDepth = new(StringComparer.OrdinalIgnoreCase);

    private Guid? _startupProgressId;
    private bool? _mailpitRunning;
    private bool _baselineReady;
    private bool _startupProgressCompleted;
    private bool _coreStartupFinished;
    private int _pendingDeferredPhases;
    private DateTimeOffset _lastStartupWaitLog = DateTimeOffset.MinValue;

    public SessionActivityCoordinator(
        SessionActivityService activity,
        ServiceManager serviceManager,
        MailpitManager mailpitManager,
        PackageInstaller packageInstaller,
        DeferredStartupCoordinator deferredStartup,
        TaskSchedulerService taskScheduler,
        StackrootStartupReadyGate startupReadyGate,
        IDiagnosticsReporter diagnostics)
    {
        _activity = activity;
        _serviceManager = serviceManager;
        _mailpitManager = mailpitManager;
        _packageInstaller = packageInstaller;
        _deferredStartup = deferredStartup;
        _taskScheduler = taskScheduler;
        _startupReadyGate = startupReadyGate;
        _diagnostics = diagnostics;

        _serviceManager.LiveStatusChanged += OnServiceLiveStatusChanged;
        _serviceManager.PhpListenersChanged += OnPhpListenersChanged;
        _mailpitManager.StatusChanged += (_, _) => _ = RefreshMailpitAsync();
        _packageInstaller.ProgressChanged += OnInstallProgressChanged;
        _deferredStartup.Completed += OnDeferredStartupCompleted;
    }

    public async Task BeginSessionAsync(CancellationToken cancellationToken = default)
    {
        _activity.Log("Stackroot started.", SessionActivityTone.Info);
        await InitializeBaselineAsync(cancellationToken).ConfigureAwait(false);
        _startupProgressId = _activity.Begin("Starting services…");
    }

    public void RegisterDeferredStartupPhases(int count)
    {
        if (count <= 0)
        {
            return;
        }

        Interlocked.Add(ref _pendingDeferredPhases, count);
    }

    public void NotifyDeferredPhaseComplete()
    {
        if (Interlocked.Decrement(ref _pendingDeferredPhases) <= 0)
        {
            Interlocked.Exchange(ref _pendingDeferredPhases, 0);
        }

        _ = ReconcileStartupStateAsync();
    }

    public async Task NotifyCoreStartupFinishedAsync(CancellationToken cancellationToken = default)
    {
        _coreStartupFinished = true;
        await ReconcileStartupStateAsync(cancellationToken).ConfigureAwait(false);
    }

    public void CompleteSession(string? message = null)
    {
        if (_startupProgressCompleted || _startupProgressId is not Guid id)
        {
            return;
        }

        _startupProgressCompleted = true;
        _activity.Complete(
            id,
            string.IsNullOrWhiteSpace(message) ? "Stackroot ready." : message,
            SessionActivityTone.Success);
        _startupProgressId = null;

        if (!_taskScheduler.IsStarted)
        {
            _taskScheduler.Start();
        }

        _startupReadyGate.SignalReady();
    }

    public Guid BeginMailpitStartup() => _activity.Begin(SessionActivityMessages.Starting("Mailpit"));

    public void CompleteMailpitStartup(Guid progressId, MailpitStatus status)
    {
        var actuallyRunning = IsMailpitProcessRunning(status);
        _mailpitRunning = actuallyRunning;

        if (!status.Enabled)
        {
            _activity.Complete(progressId, "Mailpit is disabled.", SessionActivityTone.Info);
            return;
        }

        if (actuallyRunning)
        {
            _activity.Complete(progressId, SessionActivityMessages.ServiceAction("Mailpit", "started", true));
            return;
        }

        _activity.Fail(
            progressId,
            SessionActivityMessages.ServiceAction("Mailpit", "start", false, status.Message));
    }

    public Guid BeginPhpStackStartup() => _activity.Begin(SessionActivityMessages.Starting("PHP listeners"));

    public async Task CompletePhpStackStartupAsync(Guid progressId, CancellationToken cancellationToken = default)
    {
        await ReportPhpListenersAsync(cancellationToken).ConfigureAwait(false);

        var versionIds = _serviceManager.ResolveRequiredPhpVersionIds();
        if (versionIds.Count == 0)
        {
            _activity.Complete(progressId, "No PHP listeners configured.", SessionActivityTone.Info);
            return;
        }

        _activity.Complete(progressId, SessionActivityMessages.PhpListenersReady());
    }

    public void FailPhpStackStartup(Guid progressId, string? message)
    {
        _activity.Fail(progressId, string.IsNullOrWhiteSpace(message) ? "PHP listeners failed to start." : message);
    }

    public IDisposable Suppress(string serviceId)
    {
        var key = NormalizeServiceId(serviceId);
        lock (_sync)
        {
            _suppressDepth[key] = _suppressDepth.GetValueOrDefault(key) + 1;
        }

        return new SuppressionScope(this, key);
    }

    public void NotifyProcessAutoStart(IReadOnlyList<ProcessInfo> results) =>
        NotifyProcessActions(results, "start");

    public void NotifyProcessActions(IReadOnlyList<ProcessInfo> results, string verb)
    {
        foreach (var process in results)
        {
            NotifyProcessAction(process, verb);
        }
    }

    public void NotifyProcessAction(ProcessInfo process, string verb)
    {
        switch (verb.ToLowerInvariant())
        {
            case "start":
            case "restart":
                if (process.Status is ProcessStatus.Running or ProcessStatus.Restarting)
                {
                    var message = SessionActivityMessages.ProcessAction(process.Name, verb, true);
                    _diagnostics.LogActivity("Processes", message);
                    _activity.Log(message, SessionActivityTone.Success);
                }
                else if (process.Status == ProcessStatus.Error)
                {
                    var message = SessionActivityMessages.ProcessAction(process.Name, verb, false, process.Message);
                    _diagnostics.LogUserError("Processes", message);
                    _activity.Log(message, SessionActivityTone.Error);
                }

                break;

            case "stop":
                if (process.Status is ProcessStatus.Running or ProcessStatus.Restarting)
                {
                    var message = SessionActivityMessages.ProcessAction(process.Name, "stop", false, "Process is still running.");
                    _diagnostics.LogUserError("Processes", message);
                    _activity.Log(message, SessionActivityTone.Error);
                }
                else if (process.Status == ProcessStatus.Error)
                {
                    var message = SessionActivityMessages.ProcessAction(process.Name, "stop", false, process.Message);
                    _diagnostics.LogUserError("Processes", message);
                    _activity.Log(message, SessionActivityTone.Error);
                }
                else
                {
                    var message = SessionActivityMessages.ProcessAction(process.Name, "stopped", true);
                    _diagnostics.LogActivity("Processes", message);
                    _activity.Log(message, SessionActivityTone.Info);
                }

                break;
        }
    }

    public void Dispose()
    {
        _deferredStartup.Completed -= OnDeferredStartupCompleted;
        _serviceManager.LiveStatusChanged -= OnServiceLiveStatusChanged;
        _serviceManager.PhpListenersChanged -= OnPhpListenersChanged;
        _packageInstaller.ProgressChanged -= OnInstallProgressChanged;
    }

    private void OnDeferredStartupCompleted() => _ = FinalizeStartupActivityAsync();

    private void OnPhpListenersChanged(object? sender, EventArgs e) => _ = OnPhpListenersChangedAsync();

    private async Task OnPhpListenersChangedAsync()
    {
        await ReportPhpListenersAsync().ConfigureAwait(false);
        await ReconcileStartupStateAsync().ConfigureAwait(false);
    }

    private async Task ReportPhpListenersAsync(CancellationToken cancellationToken = default)
    {
        if (!_baselineReady)
        {
            return;
        }

        var versionIds = _serviceManager.ResolveRequiredPhpVersionIds();
        if (versionIds.Count == 0)
        {
            return;
        }

        var host = _serviceManager.ResolvePhpFastCgiHost();
        foreach (var versionId in versionIds)
        {
            if (_reportedPhpListeners.Contains(versionId))
            {
                continue;
            }

            var port = _serviceManager.ResolvePhpPlannedPort(versionId);
            if (port is null or <= 0)
            {
                continue;
            }

            if (!await _serviceManager.IsPhpListenerRunningAsync(versionId, cancellationToken).ConfigureAwait(false))
            {
                continue;
            }

            _reportedPhpListeners.Add(versionId);
            _activity.Log(
                SessionActivityMessages.PhpListenerStarted(versionId, host, port.Value),
                SessionActivityTone.Success);
        }
    }

    private async Task ReconcileStartupStateAsync(CancellationToken cancellationToken = default)
    {
        if (!_coreStartupFinished)
        {
            return;
        }

        // If no services are being tracked for startup, there is nothing
        // to reconcile — skip the expensive port scan entirely.
        bool hasPendingWork;
        lock (_sync)
        {
            hasPendingWork = _awaitingServiceStarts.Count > 0 || _serviceProgressIds.Count > 0;
        }

        if (!hasPendingWork)
        {
            TryCompleteStartup();
            return;
        }

        var live = await Task.Run(
            () => _serviceManager.ListLiveAsync(cancellationToken).GetAwaiter().GetResult(),
            cancellationToken).ConfigureAwait(false);
        var resolvedProgress = new List<(Guid Id, string Message, bool Failed)>();

        lock (_sync)
        {
            foreach (var info in live)
            {
                _lastServiceStatus[info.Id] = info.Status;

                if (!ShouldTrackServiceActivity(info) || IsExternallyManagedService(info.Id))
                {
                    _awaitingServiceStarts.Remove(info.Id);
                    _serviceProgressIds.Remove(info.Id);
                    continue;
                }

                if (info.Status == ServiceStatus.Starting)
                {
                    if (_serviceProgressIds.ContainsKey(info.Id))
                    {
                        _awaitingServiceStarts.Add(info.Id);
                    }

                    continue;
                }

                _awaitingServiceStarts.Remove(info.Id);
                if (!_serviceProgressIds.TryGetValue(info.Id, out var progressId))
                {
                    continue;
                }

                _serviceProgressIds.Remove(info.Id);
                var name = string.IsNullOrWhiteSpace(info.Name) ? FormatServiceLabel(info.Id) : info.Name;
                resolvedProgress.Add(info.Status switch
                {
                    ServiceStatus.Running => (progressId, SessionActivityMessages.ServiceAction(name, "started", true), false),
                    ServiceStatus.Error => (progressId, SessionActivityMessages.ServiceAction(name, "start", false, info.Message), true),
                    _ => (progressId, SessionActivityMessages.ServiceAction(name, "stopped", true), false)
                });
            }
        }

        foreach (var (id, message, failed) in resolvedProgress)
        {
            if (failed)
            {
                _activity.Fail(id, message);
            }
            else
            {
                _activity.Complete(
                    id,
                    message,
                    message.Contains("stopped", StringComparison.OrdinalIgnoreCase)
                        ? SessionActivityTone.Info
                        : SessionActivityTone.Success);
            }
        }

        TryCompleteStartup();
    }

    /// <summary>
    /// Runs when deferred startup finishes — aligns activity rows with the toast
    /// ("All services started") and closes any stuck per-service progress.
    /// </summary>
    private async Task FinalizeStartupActivityAsync()
    {
        await ReconcileStartupStateAsync().ConfigureAwait(false);
        await SettleRemainingServiceProgressAsync().ConfigureAwait(false);
        TryCompleteStartup();
    }

    private async Task SettleRemainingServiceProgressAsync()
    {
        List<(string ServiceId, Guid ProgressId)> pending;
        lock (_sync)
        {
            pending = _serviceProgressIds
                .Select(pair => (pair.Key, pair.Value))
                .ToList();
        }

        if (pending.Count == 0)
        {
            return;
        }

        var live = await Task.Run(() => _serviceManager.ListLiveAsync().GetAwaiter().GetResult())
            .ConfigureAwait(false);
        var liveById = live.ToDictionary(item => item.Id, StringComparer.OrdinalIgnoreCase);
        var resolutions = new List<(Guid Id, string Message, bool Failed)>();

        lock (_sync)
        {
            foreach (var (serviceId, progressId) in pending)
            {
                if (!_serviceProgressIds.Remove(serviceId))
                {
                    continue;
                }

                _awaitingServiceStarts.Remove(serviceId);
                var name = FormatServiceLabel(serviceId);
                if (!liveById.TryGetValue(serviceId, out var info))
                {
                    continue;
                }

                _lastServiceStatus[serviceId] = info.Status;
                if (info.Status == ServiceStatus.Running || (info.Status == ServiceStatus.Starting && info.PortOpen == true))
                {
                    resolutions.Add((progressId, SessionActivityMessages.ServiceAction(name, "started", true), false));
                }
                else if (info.Status == ServiceStatus.Error)
                {
                    resolutions.Add((progressId, SessionActivityMessages.ServiceAction(name, "start", false, info.Message), true));
                }
            }
        }

        foreach (var (id, message, failed) in resolutions)
        {
            if (failed)
            {
                _activity.Fail(id, message);
            }
            else
            {
                _activity.Complete(id, message);
            }
        }
    }

    private async Task InitializeBaselineAsync(CancellationToken cancellationToken)
    {
        // Do not block live status notifications on an expensive full port scan.
        _baselineReady = true;

        try
        {
            var live = await Task.Run(
                () => _serviceManager.ListLiveAsync(cancellationToken).GetAwaiter().GetResult(),
                cancellationToken).ConfigureAwait(false);
            lock (_sync)
            {
                foreach (var info in live)
                {
                    _lastServiceStatus[info.Id] = info.Status;
                }
            }
        }
        catch
        {
            // Baseline seed is best-effort; live events remain authoritative during startup.
        }

        var mailpit = await _mailpitManager.GetStatusAsync(cancellationToken).ConfigureAwait(false);
        _mailpitRunning = IsMailpitProcessRunning(mailpit);
    }

    private void OnServiceLiveStatusChanged(object? sender, ServiceInfo info)
    {
        if (!_baselineReady || IsSuppressed(info.Id))
        {
            lock (_sync)
            {
                _lastServiceStatus[info.Id] = info.Status;
            }

            return;
        }

        if (!ShouldTrackServiceActivity(info))
        {
            lock (_sync)
            {
                _lastServiceStatus[info.Id] = info.Status;
                _awaitingServiceStarts.Remove(info.Id);
                _serviceProgressIds.Remove(info.Id);
            }

            return;
        }

        ServiceStatus? previous;
        lock (_sync)
        {
            previous = _lastServiceStatus.GetValueOrDefault(info.Id);
            _lastServiceStatus[info.Id] = info.Status;
        }

        if (previous == info.Status)
        {
            return;
        }

        if (IsExternallyManagedService(info.Id))
        {
            TryCompleteStartup();
            return;
        }

        var name = string.IsNullOrWhiteSpace(info.Name) ? FormatServiceLabel(info.Id) : info.Name;

        switch (info.Status)
        {
            case ServiceStatus.Starting:
                // Stale progress while the service is already up (e.g. nginx reload during deferred init).
                if (previous is ServiceStatus.Running)
                {
                    break;
                }

                if (previous != ServiceStatus.Starting)
                {
                    lock (_sync)
                    {
                        _serviceProgressIds[info.Id] = _activity.Begin(SessionActivityMessages.Starting(name));
                        if (_coreStartupFinished)
                        {
                            _awaitingServiceStarts.Add(info.Id);
                        }
                    }
                }

                break;

            case ServiceStatus.Running:
                MarkServiceStartupSettled(info.Id);
                if (TryTakeServiceProgress(info.Id, out var startId))
                {
                    _activity.Complete(startId, SessionActivityMessages.ServiceAction(name, "started", true));
                }
                else if (previous is ServiceStatus.Stopped or ServiceStatus.Error or null)
                {
                    _activity.Log(SessionActivityMessages.ServiceAction(name, "started", true), SessionActivityTone.Success);
                }

                break;

            case ServiceStatus.Stopped:
                MarkServiceStartupSettled(info.Id);
                if (TryTakeServiceProgress(info.Id, out var cancelledId))
                {
                    _activity.Complete(cancelledId, SessionActivityMessages.ServiceAction(name, "stopped", true), SessionActivityTone.Info);
                }
                else if (previous is ServiceStatus.Running or ServiceStatus.Starting)
                {
                    _activity.Log(SessionActivityMessages.ServiceAction(name, "stopped", true), SessionActivityTone.Info);
                }

                break;

            case ServiceStatus.Error:
                MarkServiceStartupSettled(info.Id);
                var detail = string.IsNullOrWhiteSpace(info.Message) ? null : info.Message;
                if (TryTakeServiceProgress(info.Id, out var failedId))
                {
                    _activity.Fail(failedId, SessionActivityMessages.ServiceAction(name, "start", false, detail));
                }
                else
                {
                    _activity.Log(
                        SessionActivityMessages.ServiceAction(name, "failed", false, detail),
                        SessionActivityTone.Error);
                }

                break;
        }

        TryCompleteStartup();
    }

    private void MarkServiceStartupSettled(string serviceId)
    {
        lock (_sync)
        {
            _awaitingServiceStarts.Remove(serviceId);
        }
    }

    private void TryCompleteStartup()
    {
        if (!_coreStartupFinished || _startupProgressCompleted)
        {
            return;
        }

        if (Volatile.Read(ref _pendingDeferredPhases) > 0)
        {
            LogStartupWaitState("deferred phases");
            return;
        }

        lock (_sync)
        {
            if (_awaitingServiceStarts.Count > 0 || _serviceProgressIds.Count > 0)
            {
                LogStartupWaitState("service progress");
                return;
            }
        }

        LogStartupWaitState("complete");
        System.Windows.Application.Current?.Dispatcher.BeginInvoke(
            () => CompleteSession(),
            System.Windows.Threading.DispatcherPriority.Normal);
    }

    private void LogStartupWaitState(string trigger)
    {
        if (_startupProgressCompleted || !_coreStartupFinished)
        {
            return;
        }

        var pending = Volatile.Read(ref _pendingDeferredPhases);
        List<string> awaiting;
        List<string> progress;
        lock (_sync)
        {
            awaiting = _awaitingServiceStarts.ToList();
            progress = _serviceProgressIds.Keys.ToList();
        }

        if (string.Equals(trigger, "complete", StringComparison.Ordinal))
        {
            _diagnostics.LogActivity("StartupActivity", "Startup progress complete — closing session bar");
            return;
        }

        if (pending <= 0 && awaiting.Count == 0 && progress.Count == 0)
        {
            return;
        }

        var now = DateTimeOffset.UtcNow;
        if (now - _lastStartupWaitLog < TimeSpan.FromSeconds(5))
        {
            return;
        }

        _lastStartupWaitLog = now;
        var parts = new List<string>();
        if (pending > 0)
        {
            parts.Add($"deferred phases remaining: {pending}");
        }

        if (awaiting.Count > 0)
        {
            parts.Add($"awaiting start: {string.Join(", ", awaiting)}");
        }

        if (progress.Count > 0)
        {
            parts.Add($"open progress: {string.Join(", ", progress)}");
        }

        _diagnostics.LogActivity(
            "StartupActivity",
            $"\"Starting services…\" still open ({trigger}) — {string.Join("; ", parts)}");
    }

    private static bool ShouldTrackServiceActivity(ServiceInfo info) => info.Enabled != false;

    private static bool IsExternallyManagedService(string serviceId) =>
        string.Equals(serviceId, "mailpit", StringComparison.OrdinalIgnoreCase);

    private async Task RefreshMailpitAsync()
    {
        if (!_baselineReady || IsSuppressed("mailpit"))
        {
            return;
        }

        var status = await _mailpitManager.GetStatusAsync().ConfigureAwait(false);
        var isRunning = IsMailpitProcessRunning(status);
        var wasRunning = _mailpitRunning;
        _mailpitRunning = isRunning;

        if (wasRunning is null || wasRunning == isRunning)
        {
            return;
        }

        if (isRunning)
        {
            _activity.Log(SessionActivityMessages.ServiceAction("Mailpit", "started", true), SessionActivityTone.Success);
        }
        else
        {
            _activity.Log(SessionActivityMessages.ServiceAction("Mailpit", "stopped", true), SessionActivityTone.Info);
        }
    }

    private static bool IsMailpitProcessRunning(MailpitStatus status) =>
        status.Running && status.Pid is > 0;

    private void OnInstallProgressChanged(object? sender, InstallProgress progress)
    {
        var label = FormatPackageLabel(progress.PackageId);

        if (progress.Phase == InstallPhase.Resolving)
        {
            lock (_sync)
            {
                if (_installProgressIds.ContainsKey(progress.PackageId))
                {
                    return;
                }

                _installProgressIds[progress.PackageId] = _activity.Begin(SessionActivityMessages.Installing(label));
            }

            return;
        }

        if (progress.Phase == InstallPhase.Done)
        {
            if (TryTakeInstallProgress(progress.PackageId, out var id))
            {
                var message = SessionActivityMessages.PackageInstalled(label);
                _diagnostics.LogActivity("Packages", message);
                _activity.Complete(id, message);
            }

            return;
        }

        if (progress.Phase != InstallPhase.Error)
        {
            return;
        }

        if (TryTakeInstallProgress(progress.PackageId, out var failedId))
        {
            var message = string.IsNullOrWhiteSpace(progress.Message)
                ? SessionActivityMessages.PackageInstallFailed(label)
                : progress.Message;
            _diagnostics.LogUserError("Packages", message);
            _activity.Fail(failedId, message);
        }
    }

    private bool TryTakeServiceProgress(string serviceId, out Guid id)
    {
        lock (_sync)
        {
            if (_serviceProgressIds.TryGetValue(serviceId, out id))
            {
                _serviceProgressIds.Remove(serviceId);
                return true;
            }
        }

        id = default;
        return false;
    }

    private bool TryTakeInstallProgress(string packageId, out Guid id)
    {
        lock (_sync)
        {
            if (_installProgressIds.TryGetValue(packageId, out id))
            {
                _installProgressIds.Remove(packageId);
                return true;
            }
        }

        id = default;
        return false;
    }

    private bool IsSuppressed(string serviceId)
    {
        lock (_sync)
        {
            return _suppressDepth.GetValueOrDefault(NormalizeServiceId(serviceId)) > 0;
        }
    }

    private void ReleaseSuppression(string serviceId)
    {
        lock (_sync)
        {
            if (!_suppressDepth.TryGetValue(serviceId, out var depth))
            {
                return;
            }

            if (depth <= 1)
            {
                _suppressDepth.Remove(serviceId);
                return;
            }

            _suppressDepth[serviceId] = depth - 1;
        }
    }

    private static string NormalizeServiceId(string serviceId) => serviceId.Trim().ToLowerInvariant();

    private static string FormatServiceLabel(string serviceKey)
    {
        if (string.IsNullOrWhiteSpace(serviceKey))
        {
            return "Service";
        }

        return char.ToUpperInvariant(serviceKey[0]) + serviceKey[1..];
    }

    private static string FormatActivityDetail(ServiceInfo info)
    {
        if (string.IsNullOrWhiteSpace(info.Message))
        {
            return string.Empty;
        }

        var message = info.Message.Length <= 60 ? info.Message : info.Message[..59] + "…";
        return $" msg=\"{message}\"";
    }

    private static string FormatPackageLabel(string packageId)
    {
        if (packageId.StartsWith("node:", StringComparison.OrdinalIgnoreCase))
        {
            return $"Node {packageId["node:".Length..]}";
        }

        return packageId;
    }

    private sealed class SuppressionScope : IDisposable
    {
        private readonly SessionActivityCoordinator _owner;
        private readonly string _serviceId;
        private bool _disposed;

        public SuppressionScope(SessionActivityCoordinator owner, string serviceId)
        {
            _owner = owner;
            _serviceId = serviceId;
        }

        public void Dispose()
        {
            if (_disposed)
            {
                return;
            }

            _disposed = true;
            _owner.ReleaseSuppression(_serviceId);
        }
    }
}
