using System.Collections.Concurrent;
using System.Diagnostics;
using Stackroot.Core.Abstractions;
using Stackroot.Core.Catalog;
using Stackroot.Core.Databases;
using Stackroot.Core.Nginx;
using Stackroot.Core.Settings;
using Stackroot.Core.Services.Lifecycle;
using Stackroot.Core.Sites.Persistence;
using Stackroot.Core.Windows;

namespace Stackroot.Core.Services;

public sealed class ServiceManager : IDisposable
{
    private readonly StackrootPaths _paths;
    private readonly InstallRegistryStore _registry;
    private readonly SettingsStore _settingsStore;
    private readonly IProcessJobManager _jobManager;
    private readonly IDiagnosticsReporter _diagnostics;
    private readonly PackageCatalogStore _catalog;
    private readonly NginxWebStackCoordinator? _webStackCoordinator;
    private readonly ConcurrentDictionary<ServiceId, ServiceInfo> _services = new();
    private readonly ConcurrentDictionary<ServiceId, string> _lastErrors = new();
    private readonly HashSet<ServiceId> _starting = [];
    private readonly object _startingSync = new();
    private readonly ConcurrentDictionary<string, DateTimeOffset> _phpRecoveryAttemptedAt = new(StringComparer.OrdinalIgnoreCase);
    private int _phpRecoveryInFlight;
    private static readonly TimeSpan PhpRecoveryRetryCooldown = TimeSpan.FromSeconds(30);
    private readonly ConcurrentDictionary<ServiceId, DateTime> _supervisionCooldowns = new();
    private readonly ConcurrentDictionary<ServiceId, int> _supervisionFailures = new();
    private readonly ConcurrentDictionary<ServiceId, bool> _supervisionEligible = new();
    /// <summary>User explicitly stopped — keep-alive must not restart until manual Start.</summary>
    private readonly ConcurrentDictionary<ServiceId, byte> _userStoppedServices = new();
    private readonly ConcurrentDictionary<ServiceId, byte> _supervisionRestartInFlight = new();
    private readonly ConcurrentDictionary<ServiceId, DateTime> _supervisionRecoveredAt = new();
    private readonly ConcurrentDictionary<ServiceId, DateTimeOffset> _unexpectedStopHandledAt = new();
    private readonly object _unexpectedStopSync = new();
    private const int MaxSupervisionFailures = 10;
    private const int MaxConcurrentSupervisionRestarts = 2;

    private readonly SemaphoreSlim _supervisionRestartSlots = new(MaxConcurrentSupervisionRestarts, MaxConcurrentSupervisionRestarts);
    private readonly object _supervisionRestartCtsSync = new();
    private CancellationTokenSource? _supervisionRestartCts;
    private static readonly TimeSpan SupervisionRecoveryGrace = TimeSpan.FromSeconds(15);
    private static readonly TimeSpan UnexpectedStopDedupeWindow = TimeSpan.FromSeconds(5);
    private System.Timers.Timer? _supervisionTimer;
    private bool _supervisionStarted;
    private int _supervisionTickInFlight;
    private bool _disposed;

    public event EventHandler<ServiceInfo>? LiveStatusChanged;

    /// <summary>
    /// Fired when keep-alive restart attempts reach a milestone (every 10 failures).
    /// Supervision continues retrying — this is for user notification only.
    /// </summary>
    public event EventHandler<ServiceSupervisionAlertEventArgs>? SupervisionAlert;

    public event EventHandler? PhpListenersChanged;

    private static readonly HashSet<ServiceId> Implemented = new()
    {
        ServiceId.Nginx,
        ServiceId.Redis,
        ServiceId.Memcached,
        ServiceId.Imagemagick,
        ServiceId.Mysql,
        ServiceId.Mariadb,
        ServiceId.Postgresql,
        ServiceId.Mongodb,
        ServiceId.Mailpit
    };

    public ServiceManager(
        StackrootPaths paths,
        InstallRegistryStore? registry = null,
        SettingsStore? settingsStore = null,
        IProcessJobManager? jobManager = null,
        IDiagnosticsReporter? diagnostics = null,
        PackageCatalogStore? catalog = null,
        NginxWebStackCoordinator? webStackCoordinator = null)
    {
        _paths = paths;
        _registry = registry ?? new InstallRegistryStore(paths.DataRoot);
        _settingsStore = settingsStore ?? new SettingsStore(paths.DataRoot);
        _jobManager = jobManager ?? new ProcessJobManager();
        _diagnostics = diagnostics ?? NoOpDiagnosticsReporter.Instance;
        _catalog = catalog ?? new PackageCatalogStore(_paths.ResourcesRoot);
        _webStackCoordinator = webStackCoordinator;
    }

    private PackageCatalogStore Catalog => _catalog;

    public Task<IReadOnlyList<ServiceInfo>> listLive(CancellationToken cancellationToken = default) => ListLiveAsync(cancellationToken);

    public Task<ServiceInfo> start(string id, CancellationToken cancellationToken = default) => StartAsync(id, cancellationToken);

    public Task<ServiceInfo> stop(string id, CancellationToken cancellationToken = default) => StopAsync(id, cancellationToken);

    public Task stopAllForceQuick(CancellationToken cancellationToken = default) => StopAllForceQuickAsync(cancellationToken);

    public Task autoStartEnabledServices(CancellationToken cancellationToken = default) => AutoStartEnabledServicesAsync(cancellationToken);

    public Task<bool> IsPortOpenAsync(string host, int port, int timeoutMs = 800)
    {
        return PortProbe.IsPortOpenAsync(host, port, timeoutMs);
    }

    public Task<ServiceInfo> StartNginxAsync(CancellationToken cancellationToken = default)
    {
        return StartAsync("nginx", cancellationToken);
    }

    public Task<ServiceInfo> StartRedisAsync(CancellationToken cancellationToken = default)
    {
        return StartAsync("redis", cancellationToken);
    }

    public Task<ServiceInfo> StopAsync(string id, int port, CancellationToken cancellationToken = default)
    {
        return StopInternalAsync(id, cancellationToken, port > 0 ? port : null);
    }

    public Task<ServiceInfo> StopAsync(string id, CancellationToken cancellationToken = default)
    {
        return StopInternalAsync(id, cancellationToken, portOverride: null);
    }

    public async Task<ServiceInfo> RestartAsync(string id, CancellationToken cancellationToken = default)
    {
        _diagnostics.LogActivity("ServiceManager", $"Restarting service '{id}'");

        if (!TryParseServiceId(id, out var serviceId))
        {
            return Fail(id, id, "Service not found");
        }

        var settings = _settingsStore.Load();
        var definition = SettingsDefaults.ServiceDefinitions.First(d => d.Id == serviceId);
        var serviceSettings = GetServiceSettings(
            settings,
            serviceId,
            definition.DefaultPort,
            definition.DefaultSslPort,
            definition.PackageId);

        await StopAsync(id, cancellationToken).ConfigureAwait(false);

        var effectivePort = serviceSettings.Port;
        if (definition.Runtime != ServiceRuntime.Library && effectivePort > 0)
        {
            await PortProbe.WaitForPortClosedAsync(
                serviceSettings.Host,
                effectivePort,
                cancellationToken: cancellationToken).ConfigureAwait(false);
        }

        if (serviceId == ServiceId.Nginx)
        {
            await EnsureRequiredPhpFastCgiAsync(cancellationToken).ConfigureAwait(false);
        }

        return await StartAsync(id, cancellationToken, ServiceStartMode.WaitUntilReady).ConfigureAwait(false);
    }

    /// <summary>
    /// Live service rows from Stackroot-owned processes (netstat + install path). No TCP probes.
    /// </summary>
    public Task<IReadOnlyList<ServiceInfo>> ListLiveAsync(CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        var settings = _settingsStore.Load();
        var rows = new List<ServiceInfo>();

        foreach (var definition in SettingsDefaults.ServiceDefinitions)
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (definition.Id == ServiceId.TestDns)
            {
                continue;
            }

            rows.Add(BuildLiveInfo(definition.Id, settings));
        }

        return Task.FromResult<IReadOnlyList<ServiceInfo>>(rows);
    }

    /// <summary>
    /// Fast runtime poll — process-alive on cached PIDs; full reconcile only when a tracked process died.
    /// </summary>
    public Task<IReadOnlyList<ServiceInfo>> ListLiveQuickAsync(CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        var settings = _settingsStore.Load();
        var rows = new List<ServiceInfo>();

        foreach (var definition in SettingsDefaults.ServiceDefinitions)
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (definition.Id == ServiceId.TestDns)
            {
                continue;
            }

            rows.Add(BuildQuickLiveInfo(definition.Id, settings));
        }

        return Task.FromResult<IReadOnlyList<ServiceInfo>>(rows);
    }

    public ServiceInfo? TryBuildLiveInfo(string id)
    {
        if (!TryParseServiceId(id, out var serviceId) || !Implemented.Contains(serviceId))
        {
            return null;
        }

        return BuildLiveInfo(serviceId);
    }

    private void NotifyLiveStatusChanged(string id, ServiceInfo? snapshot = null)
    {
        if (ApplicationShutdownState.IsClosing)
        {
            return;
        }

        var info = snapshot ?? TryBuildLiveInfo(id);
        if (info is null)
        {
            return;
        }

        LiveStatusChanged?.Invoke(this, info);
    }

    private ServiceInfo BuildLiveInfo(ServiceId serviceId, AppSettings? settings = null)
    {
        settings ??= _settingsStore.Load();
        var definition = SettingsDefaults.ServiceDefinitions.First(d => d.Id == serviceId);
        var serviceSettings = GetServiceSettings(settings, serviceId, definition.DefaultPort, definition.DefaultSslPort, definition.PackageId);
        var packageId = serviceSettings.PackageId ?? definition.PackageId;
        var installed = string.IsNullOrWhiteSpace(packageId) || _registry.GetById(packageId) is not null;
        var isLibrary = definition.Runtime == ServiceRuntime.Library;
        IReadOnlyList<int>? ownedPids = null;
        bool portOpen;
        if (isLibrary)
        {
            portOpen = installed && serviceSettings.Enabled;
        }
        else if (!serviceSettings.Enabled || !installed)
        {
            portOpen = false;
        }
        else
        {
            return BuildProcessServiceLiveInfo(serviceId, definition, serviceSettings, installed, settings);
        }

        var status = portOpen ? ServiceStatus.Running : ServiceStatus.Stopped;
        string? message = _lastErrors.GetValueOrDefault(serviceId);

        if (!serviceSettings.Enabled)
        {
            status = ServiceStatus.Stopped;
            message = "Disabled in settings";
        }
        else if (!installed && !string.IsNullOrWhiteSpace(definition.PackageId))
        {
            status = ServiceStatus.Stopped;
            message = $"Install {definition.Name} package first";
        }
        else if (IsStarting(serviceId) && !portOpen)
        {
            status = ServiceStatus.Starting;
            message = _services.GetValueOrDefault(serviceId)?.Message;
        }
        else if (!portOpen && !string.IsNullOrWhiteSpace(message))
        {
            status = ServiceStatus.Error;
        }

        if (portOpen)
        {
            _lastErrors.TryRemove(serviceId, out _);
            message = null;
        }

        var info = new ServiceInfo
        {
            Id = ToServiceKey(serviceId),
            Name = definition.Name,
            Status = status,
            Port = serviceSettings.Port,
            SslPort = serviceSettings.SslPort,
            PortOpen = portOpen,
            Installed = installed,
            Enabled = serviceSettings.Enabled,
            Pid = _services.GetValueOrDefault(serviceId)?.Pid
                ?? (ownedPids is { Count: > 0 } ? ownedPids[0] : null),
            Message = message
        };

        return info;
    }

    private bool IsWithinSupervisionRecoveryGrace(ServiceId serviceId)
        => _supervisionRecoveredAt.TryGetValue(serviceId, out var recoveredAt)
           && DateTime.UtcNow - recoveredAt < SupervisionRecoveryGrace;

    private ServiceInfo BuildProcessServiceLiveInfo(
        ServiceId serviceId,
        ServiceDefinition definition,
        ServicePortSettings serviceSettings,
        bool installed,
        AppSettings settings)
    {
        if (IsStarting(serviceId))
        {
            return BuildStarting(definition, serviceSettings);
        }

        var trackedPid = _services.GetValueOrDefault(serviceId)?.Pid;
        var previous = _services.GetValueOrDefault(serviceId);
        if (IsWithinSupervisionRecoveryGrace(serviceId) && previous is { PortOpen: true })
        {
            return BuildRunning(
                definition,
                serviceSettings,
                previous.Pid ?? trackedPid);
        }

        var wasRunning = previous is { PortOpen: true } or { Status: ServiceStatus.Running };
        var ownedPids = StackrootManagedProcessResolver.ResolveOwnedListenerPids(
            serviceId,
            definition,
            serviceSettings,
            _paths,
            _registry,
            trackedPid);

        if (!IsStarting(serviceId))
        {
            TryReattachOwnedListeners(
                serviceId,
                definition,
                serviceSettings,
                ref ownedPids,
                out var ownedPidsAliveVerified);
            if (!ownedPidsAliveVerified && ownedPids.Count > 0)
            {
                ownedPids = PruneDeadOwnedPids(ownedPids, serviceSettings.Port);
            }
        }

        if (_supervisionRestartInFlight.ContainsKey(serviceId)
            && ownedPids.Count == 0
            && !IsWithinSupervisionRecoveryGrace(serviceId))
        {
            return BuildStarting(definition, serviceSettings);
        }

        var isServing = ManagedServiceStatusPolicy.IsStackrootServing(ownedPids.Count);
        if (!isServing
            && !IsStarting(serviceId)
            && ManagedServiceStatusPolicy.ShouldClearTrackedService(previous, ownedPids.Count))
        {
            _services.TryRemove(serviceId, out _);
        }

        // Clear stale stop-failure errors when the user explicitly stopped the service.
        if (!isServing && IsUserStoppedIntent(serviceId))
        {
            _lastErrors.TryRemove(serviceId, out _);
        }

        var status = isServing ? ServiceStatus.Running : ServiceStatus.Stopped;
        string? message = _lastErrors.GetValueOrDefault(serviceId);

        if (!serviceSettings.Enabled)
        {
            status = ServiceStatus.Stopped;
            message = "Disabled in settings";
        }
        else if (!installed && !string.IsNullOrWhiteSpace(definition.PackageId))
        {
            status = ServiceStatus.Stopped;
            message = $"Install {definition.Name} package first";
        }
        else if (IsStarting(serviceId) && !isServing)
        {
            status = ServiceStatus.Starting;
            message = _services.GetValueOrDefault(serviceId)?.Message;
        }
        else if (!isServing && ManagedServiceStatusPolicy.IsPortConflictMessage(message))
        {
            status = ServiceStatus.Error;
        }
        else if (!isServing && !string.IsNullOrWhiteSpace(message))
        {
            status = ServiceStatus.Error;
        }

        if (isServing)
        {
            _lastErrors.TryRemove(serviceId, out _);
            message = null;
        }

        var info = new ServiceInfo
        {
            Id = ToServiceKey(serviceId),
            Name = definition.Name,
            Status = status,
            Port = serviceSettings.Port,
            SslPort = serviceSettings.SslPort,
            PortOpen = isServing,
            Installed = installed,
            Enabled = serviceSettings.Enabled,
            Pid = ownedPids is { Count: > 0 } ? ownedPids[0] : previous?.Pid,
            Message = message
        };

        if (isServing)
        {
            _services[serviceId] = info;
        }

        if (!IsStarting(serviceId)
            && !_supervisionRestartInFlight.ContainsKey(serviceId)
            && !IsWithinSupervisionRecoveryGrace(serviceId)
            && wasRunning
            && !isServing
            && !IsUserStoppedIntent(serviceId))
        {
            var now = DateTimeOffset.UtcNow;
            lock (_unexpectedStopSync)
            {
                if (_unexpectedStopHandledAt.TryGetValue(serviceId, out var lastHandled)
                    && now - lastHandled < UnexpectedStopDedupeWindow)
                {
                    return info;
                }

                _unexpectedStopHandledAt[serviceId] = now;
            }

            _diagnostics.LogActivity(
                "ServiceManager",
                $"Service '{info.Id}' stopped unexpectedly — notifying live listeners");
            NotifyLiveStatusChanged(info.Id, info);
            TryScheduleSupervisionRestart(serviceId, "unexpected stop", confirmedDown: true);
        }

        return info;
    }

    public Task<ServiceInfo> StartAsync(
        string id,
        CancellationToken cancellationToken = default,
        ServiceStartMode mode = ServiceStartMode.WaitUntilReady)
    {
        _diagnostics.LogActivity(
            "ServiceManager",
            mode == ServiceStartMode.Background
                ? $"Queuing background start for service '{id}'"
                : $"Starting service '{id}'");

        if (!TryParseServiceId(id, out var serviceId))
        {
            return Task.FromResult(Fail(id, id, "Service not found"));
        }

        _lastErrors.TryRemove(serviceId, out _);
        ClearUserStoppedIntent(serviceId);

        if (mode == ServiceStartMode.Background)
        {
            var definition = SettingsDefaults.ServiceDefinitions.First(d => d.Id == serviceId);
            var settings = _settingsStore.Load();
            var serviceSettings = GetServiceSettings(
                settings,
                serviceId,
                definition.DefaultPort,
                definition.DefaultSslPort,
                definition.PackageId);
            if (!TryMarkStarting(serviceId))
            {
                return Task.FromResult(BuildLiveInfo(serviceId));
            }

            NotifyLiveStatusChanged(id);
            _ = RunBackgroundStartAsync(serviceId, id, cancellationToken);
            return Task.FromResult(BuildStarting(definition, serviceSettings));
        }

        return StartAndWaitAsync(serviceId, id, cancellationToken);
    }

    private async Task<ServiceInfo> StartAndWaitAsync(ServiceId serviceId, string id, CancellationToken cancellationToken)
    {
        if (!TryMarkStarting(serviceId))
        {
            return BuildLiveInfo(serviceId);
        }

        NotifyLiveStatusChanged(id);
        ServiceInfo? result = null;
        try
        {
            result = await StartCoreAsync(serviceId, cancellationToken).ConfigureAwait(false);
            if (result.PortOpen == true || result.Status == ServiceStatus.Running)
            {
                _supervisionEligible.TryAdd(serviceId, true);
            }
            LogStartResult(id, result);
            return result;
        }
        finally
        {
            UnmarkStarting(serviceId);
            NotifyLiveStatusChanged(id, result);
        }
    }

    private async Task RunBackgroundStartAsync(ServiceId serviceId, string id, CancellationToken cancellationToken)
    {
        ServiceInfo? result = null;
        try
        {
            result = await StartCoreAsync(serviceId, cancellationToken).ConfigureAwait(false);
            LogStartResult(id, result, background: true);
        }
        catch (Exception ex)
        {
            _lastErrors[serviceId] = ex.Message;
            _diagnostics.LogException("ServiceManager", ex);
        }
        finally
        {
            UnmarkStarting(serviceId);
            NotifyLiveStatusChanged(id, result);
        }
    }

    private void LogStartResult(string id, ServiceInfo result, bool background = false)
    {
        var prefix = background ? "Background start" : "Started service";
        if (!string.IsNullOrWhiteSpace(result.Message))
        {
            _diagnostics.LogUserError("ServiceManager", $"Start '{id}' failed: {result.Message}");
        }
        else
        {
            _diagnostics.LogActivity("ServiceManager", $"{prefix} '{id}' ({result.Status})");
        }
    }

    private Task<ServiceInfo> StartCoreAsync(ServiceId serviceId, CancellationToken cancellationToken)
    {
        return serviceId switch
        {
            ServiceId.Nginx => StartNginxCoreAsync(cancellationToken),
            ServiceId.Imagemagick => Task.FromResult(StartLibraryService(serviceId)),
            _ => StartProcessServiceAsync(serviceId, cancellationToken)
        };
    }

    private async Task<ServiceInfo> StopInternalAsync(
        string id,
        CancellationToken cancellationToken,
        int? portOverride)
    {
        _diagnostics.LogActivity("ServiceManager", $"Stopping service '{id}'");

        if (!TryParseServiceId(id, out var serviceId))
        {
            return Fail(id, id, "Service not found");
        }

        MarkUserStoppedIntent(serviceId);

        ServiceInfo? notification = null;
        try
        {
            var settings = _settingsStore.Load();
            var definition = SettingsDefaults.ServiceDefinitions.First(d => d.Id == serviceId);
            var serviceSettings = GetServiceSettings(settings, serviceId, definition.DefaultPort, definition.DefaultSslPort, definition.PackageId);
            var effectivePort = portOverride ?? serviceSettings.Port;
            var stopSettings = portOverride is null
                ? serviceSettings
                : serviceSettings with { Port = effectivePort };

            _lastErrors.TryRemove(serviceId, out _);

            if (!StackrootManagedProcessResolver.IsServicePackageInstalled(definition, stopSettings, _registry))
            {
                _services.TryRemove(serviceId, out _);
                notification = await BuildStoppedAsync(definition, stopSettings).ConfigureAwait(false);
                return notification;
            }

            if (definition.Runtime == ServiceRuntime.Library)
            {
                _services.TryRemove(serviceId, out _);
                notification = await BuildStoppedAsync(definition, stopSettings).ConfigureAwait(false);
                return notification;
            }

            var trackedPid = _services.TryGetValue(serviceId, out var managed) ? managed.Pid : null;
            var ownedPids = StackrootManagedProcessResolver.ResolveOwnedListenerPids(
                serviceId,
                definition,
                stopSettings,
                _paths,
                _registry,
                trackedPid);

            if (ownedPids.Count == 0 && trackedPid is null or <= 0)
            {
                _services.TryRemove(serviceId, out _);
                notification = await BuildStoppedAsync(definition, stopSettings).ConfigureAwait(false);
                return notification;
            }

            if (serviceId == ServiceId.Nginx)
            {
                var packageId = stopSettings.PackageId ?? definition.PackageId;
                var installed = _registry.GetById(packageId!);
                if (installed is not null)
                {
                    NginxControl.StopManagedNginx(
                        _paths,
                        installed.InstallPath,
                        _jobManager,
                        effectivePort,
                        trackedPid);
                }

                StackrootManagedProcessResolver.TryKillPids(ownedPids);
                await StopAllPhpCgiAsync(cancellationToken).ConfigureAwait(false);
                _services.TryRemove(serviceId, out _);
                notification = await BuildStoppedAsync(definition, stopSettings).ConfigureAwait(false);
                return notification;
            }

            StackrootManagedProcessResolver.TryKillPids(ownedPids);
            await PortProbe.SleepAsync(200, cancellationToken).ConfigureAwait(false);

            if (effectivePort > 0)
            {
                await PortProbe.WaitForPortClosedAsync(
                    stopSettings.Host,
                    effectivePort,
                    cancellationToken: cancellationToken).ConfigureAwait(false);
            }

            _services.TryRemove(serviceId, out _);
            notification = await BuildStoppedAsync(definition, stopSettings).ConfigureAwait(false);
            _diagnostics.LogActivity("ServiceManager", $"Stopped service '{id}' ({notification.Status})");
            return notification;
        }
        finally
        {
            NotifyLiveStatusChanged(id, notification);
        }
    }

    public async Task StopAllForceQuickAsync(CancellationToken cancellationToken = default, Action<string>? onServiceStopping = null)
    {
        StopSupervision();
        await StopAllPhpCgiAsync(cancellationToken).ConfigureAwait(false);

        var settings = _settingsStore.Load();
        foreach (var serviceId in EnumerateStackrootManagedServiceIds(settings).Reverse())
        {
            cancellationToken.ThrowIfCancellationRequested();

            try
            {
                onServiceStopping?.Invoke(serviceId.ToString().ToLowerInvariant());
                await StopForShutdownAsync(serviceId, settings, cancellationToken).ConfigureAwait(false);
            }
            catch (OperationCanceledException)
            {
                throw;
            }
            catch
            {
                // Best effort on app shutdown.
            }
        }
    }

    private IEnumerable<ServiceId> EnumerateStackrootManagedServiceIds(AppSettings settings)
    {
        var ids = new HashSet<ServiceId>();

        foreach (var trackedId in _services.Keys)
        {
            if (Implemented.Contains(trackedId))
            {
                ids.Add(trackedId);
            }
        }

        foreach (var definition in SettingsDefaults.ServiceDefinitions.Where(d => Implemented.Contains(d.Id)))
        {
            var serviceSettings = GetServiceSettings(
                settings,
                definition.Id,
                definition.DefaultPort,
                definition.DefaultSslPort,
                definition.PackageId);

            if (!StackrootManagedProcessResolver.IsServicePackageInstalled(definition, serviceSettings, _registry))
            {
                continue;
            }

            var trackedPid = _services.TryGetValue(definition.Id, out var info) ? info.Pid : null;
            var ownedPids = StackrootManagedProcessResolver.ResolveOwnedListenerPids(
                definition.Id,
                definition,
                serviceSettings,
                _paths,
                _registry,
                trackedPid);

            if (ownedPids.Count > 0 || trackedPid is > 0)
            {
                ids.Add(definition.Id);
            }
        }

        return ids;
    }

    private async Task StopForShutdownAsync(
        ServiceId serviceId,
        AppSettings settings,
        CancellationToken cancellationToken)
    {
        var definition = SettingsDefaults.ServiceDefinitions.First(d => d.Id == serviceId);
        var serviceSettings = GetServiceSettings(
            settings,
            serviceId,
            definition.DefaultPort,
            definition.DefaultSslPort,
            definition.PackageId);

        _lastErrors.TryRemove(serviceId, out _);

        if (!StackrootManagedProcessResolver.IsServicePackageInstalled(definition, serviceSettings, _registry))
        {
            _services.TryRemove(serviceId, out _);
            return;
        }

        if (definition.Runtime == ServiceRuntime.Library)
        {
            _services.TryRemove(serviceId, out _);
            return;
        }

        var trackedPid = _services.TryGetValue(serviceId, out var managed) ? managed.Pid : null;
        var ownedPids = StackrootManagedProcessResolver.ResolveOwnedListenerPids(
            serviceId,
            definition,
            serviceSettings,
            _paths,
            _registry,
            trackedPid);

        if (ownedPids.Count == 0 && trackedPid is null or <= 0)
        {
            _services.TryRemove(serviceId, out _);
            return;
        }

        var stopWatch = System.Diagnostics.Stopwatch.StartNew();
        _diagnostics.LogActivity("Shutdown", $"Stopping {ToServiceKey(serviceId)} (port {serviceSettings.Port}, pids: {string.Join(", ", ownedPids)})");

        if (serviceId == ServiceId.Nginx)
        {
            var packageId = serviceSettings.PackageId ?? definition.PackageId;
            var installed = _registry.GetById(packageId!);
            if (installed is not null)
            {
                NginxControl.StopManagedNginx(
                    _paths,
                    installed.InstallPath,
                    _jobManager,
                    serviceSettings.Port,
                    trackedPid);
            }
        }

        cancellationToken.ThrowIfCancellationRequested();
        StackrootManagedProcessResolver.TryKillPids(ownedPids);
        _services.TryRemove(serviceId, out _);

        if (serviceSettings.Port > 0)
        {
            try
            {
                var portWatch = System.Diagnostics.Stopwatch.StartNew();
                await PortProbe.WaitForPortClosedAsync(
                    serviceSettings.Host,
                    serviceSettings.Port,
                    attempts: 4,
                    delayMs: 100,
                    cancellationToken: cancellationToken).ConfigureAwait(false);
                _diagnostics.LogActivity("Shutdown", $"Port {serviceSettings.Port} closed in {portWatch.ElapsedMilliseconds}ms");
            }
            catch (OperationCanceledException)
            {
                _diagnostics.LogActivity("Shutdown", $"Port wait for {ToServiceKey(serviceId)} cancelled");
                throw;
            }
            catch (Exception ex)
            {
                _diagnostics.LogActivity("Shutdown", $"Port wait for {ToServiceKey(serviceId)} failed: {ex.Message}");
            }
        }

        _diagnostics.LogActivity("Shutdown", $"Stopped {ToServiceKey(serviceId)} in {stopWatch.ElapsedMilliseconds}ms");
    }

    public async Task AutoStartEnabledServicesAsync(CancellationToken cancellationToken = default)
    {
        var autoStartWatch = Stopwatch.StartNew();
        var settings = _settingsStore.Load();
        foreach (var definition in SettingsDefaults.ServiceDefinitions)
        {
            if (!Implemented.Contains(definition.Id))
            {
                continue;
            }

            if (definition.Id == ServiceId.Mailpit)
            {
                continue;
            }

            var serviceSettings = GetServiceSettings(settings, definition.Id, definition.DefaultPort, definition.DefaultSslPort, definition.PackageId);
            if (!serviceSettings.Enabled || !serviceSettings.AutoStart)
            {
                continue;
            }

            var packageId = serviceSettings.PackageId ?? definition.PackageId;
            if (!string.IsNullOrWhiteSpace(packageId) && _registry.GetById(packageId) is null)
            {
                continue;
            }

            var alreadyServing = false;
            if (definition.Runtime != ServiceRuntime.Library
                && serviceSettings.Port > 0
                && !TryEvaluateStartPortBinding(
                    definition.Id,
                    definition,
                    serviceSettings,
                    out _,
                    out alreadyServing,
                    out var conflictMessage))
            {
                _lastErrors[definition.Id] = conflictMessage!;
                if (serviceSettings.Supervise && !IsUserStoppedIntent(definition.Id))
                {
                    _supervisionEligible.TryAdd(definition.Id, true);
                }

                NotifyLiveStatusChanged(ToServiceKey(definition.Id));
                continue;
            }

            if (definition.Runtime != ServiceRuntime.Library
                && serviceSettings.Port > 0
                && alreadyServing)
            {
                continue;
            }

            if (IsUserStoppedIntent(definition.Id))
            {
                continue;
            }

            var serviceKey = ToServiceKey(definition.Id);
            await StartAsync(serviceKey, cancellationToken, ServiceStartMode.WaitUntilReady)
                .ConfigureAwait(false);
        }

        _diagnostics.LogActivity("ServiceManager", $"Auto-start complete in {autoStartWatch.ElapsedMilliseconds}ms");
    }

    /// <summary>
    /// Seeds eligibility and starts keep-alive supervision after deferred startup
    /// (including web-stack finalize) so reload/restart during init is not treated as a crash.
    /// </summary>
    public void ActivateServiceSupervision()
    {
        var settings = _settingsStore.Load();
        SeedSupervisionEligibility(settings);
        StartSupervision();
    }

    public void SyncNginxLiveStatus(NginxControl.NginxReloadResult reloadResult)
    {
        if (!reloadResult.Ok)
        {
            return;
        }

        var settings = _settingsStore.Load();
        var definition = SettingsDefaults.ServiceDefinitions.First(d => d.Id == ServiceId.Nginx);
        var serviceSettings = GetServiceSettings(
            settings,
            ServiceId.Nginx,
            definition.DefaultPort,
            definition.DefaultSslPort,
            definition.PackageId);
        var packageId = serviceSettings.PackageId ?? definition.PackageId;
        var installed = string.IsNullOrWhiteSpace(packageId) || _registry.GetById(packageId) is not null;

        var info = new ServiceInfo
        {
            Id = ToServiceKey(ServiceId.Nginx),
            Name = definition.Name,
            Status = ServiceStatus.Running,
            Port = serviceSettings.Port,
            SslPort = serviceSettings.SslPort,
            PortOpen = true,
            Installed = installed,
            Enabled = serviceSettings.Enabled,
            Pid = reloadResult.Pid
        };

        _services[ServiceId.Nginx] = info;
        _lastErrors.TryRemove(ServiceId.Nginx, out _);
        if (serviceSettings.Enabled && serviceSettings.Supervise && info.PortOpen == true)
        {
            _supervisionEligible.TryAdd(ServiceId.Nginx, true);
        }

        NotifyLiveStatusChanged(info.Id, info);
    }

    public Task StopAllPhpCgiAsync(CancellationToken cancellationToken = default)
    {
        return PhpCgiRuntime.StopAllPhpCgiAsync(cancellationToken);
    }

    public async Task StopPhpCgiAsync(string versionId, CancellationToken cancellationToken = default)
    {
        await PhpCgiRuntime.StopPhpCgiAsync(versionId, cancellationToken).ConfigureAwait(false);
        var settings = _settingsStore.Load();
        PhpCgiRuntime.KillOwnedPhpCgiOnPort(settings, _registry, versionId);
        await PortProbe.SleepAsync(300, cancellationToken).ConfigureAwait(false);
    }

    public IReadOnlyList<string> ResolveRequiredPhpVersionIds()
    {
        var settings = _settingsStore.Load();
        var sitePhpVersionIds = new SiteStore(_paths.DataRoot, _settingsStore)
            .List()
            .Where(site => site.Enabled && !string.IsNullOrWhiteSpace(site.PhpVersionId))
            .Select(site => site.PhpVersionId!)
            .ToList();
        return PhpCgiRuntime.ResolveRequiredVersionIds(settings, _registry, sitePhpVersionIds, Catalog);
    }

    public async Task<ServiceLifecycleResult> EnsurePhpFastCgiAsync(
        IReadOnlyList<string>? versionIds = null,
        CancellationToken cancellationToken = default)
    {
        var settings = _settingsStore.Load();
        var result = await PhpCgiRuntime.EnsurePhpFastCgiAsync(
            _paths,
            _registry,
            settings,
            _jobManager,
            versionIds,
            cancellationToken,
            diagnostics: _diagnostics).ConfigureAwait(false);
        NotifyPhpListenersChangedIfReady(result);
        return result;
    }

    public async Task<ServiceLifecycleResult> RestartPhpFastCgiAsync(
        IReadOnlyList<string> versionIds,
        CancellationToken cancellationToken = default)
    {
        _diagnostics.LogActivity("PHP", $"Restarting FastCGI for {string.Join(", ", versionIds)}…");

        // Prevent the background recovery timer from attempting a
        // parallel recovery while this explicit restart is in progress.
        foreach (var versionId in versionIds)
        {
            _phpRecoveryAttemptedAt[versionId] = DateTimeOffset.UtcNow;
        }

        var settings = _settingsStore.Load();
        var restartWatch = System.Diagnostics.Stopwatch.StartNew();
        var result = await PhpCgiRuntime.EnsurePhpFastCgiAsync(
            _paths,
            _registry,
            settings,
            _jobManager,
            versionIds,
            cancellationToken,
            forceRestart: true,
            diagnostics: _diagnostics).ConfigureAwait(false);
        _diagnostics.LogActivity("PHP", $"FastCGI restart {(result.Success ? "completed" : "failed")} in {restartWatch.ElapsedMilliseconds}ms");
        NotifyPhpListenersChangedIfReady(result);
        return result;
    }

    public async Task<ServiceLifecycleResult> EnsureRequiredPhpFastCgiAsync(CancellationToken cancellationToken = default)
    {
        _diagnostics.LogActivity("PHP", "Ensuring required PHP FastCGI listeners…");
        var ensureWatch = System.Diagnostics.Stopwatch.StartNew();
        var settings = _settingsStore.Load();
        var sitePhpVersionIds = new SiteStore(_paths.DataRoot, _settingsStore)
            .List()
            .Where(site => site.Enabled && !string.IsNullOrWhiteSpace(site.PhpVersionId))
            .Select(site => site.PhpVersionId!)
            .ToList();
        var versionIds = PhpCgiRuntime.ResolveRequiredVersionIds(settings, _registry, sitePhpVersionIds, Catalog);
        var result = await PhpCgiRuntime.EnsurePhpFastCgiAsync(
            _paths,
            _registry,
            settings,
            _jobManager,
            versionIds,
            cancellationToken,
            diagnostics: _diagnostics).ConfigureAwait(false);
        _diagnostics.LogActivity("PHP", $"FastCGI listeners {(result.Success ? "ready" : "failed")} in {ensureWatch.ElapsedMilliseconds}ms");
        NotifyPhpListenersChangedIfReady(result);
        return result;
    }

    private void NotifyPhpListenersChangedIfReady(ServiceLifecycleResult result)
    {
        if (result.Success)
        {
            PhpListenersChanged?.Invoke(this, EventArgs.Empty);
        }
    }

    public Task<ServiceLifecycleResult> EnsureStackPhpCgiAsync(CancellationToken cancellationToken = default)
    {
        return EnsureRequiredPhpFastCgiAsync(cancellationToken);
    }

    public Task TryRecoverRequiredPhpAsync(CancellationToken cancellationToken = default, bool urgent = false)
        => TryRecoverRequiredPhpCoreAsync(cancellationToken, urgent);

    private async Task TryRecoverRequiredPhpCoreAsync(CancellationToken cancellationToken, bool urgent)
    {
        if (Interlocked.CompareExchange(ref _phpRecoveryInFlight, 1, 0) != 0)
        {
            return;
        }

        try
        {
            var settings = _settingsStore.Load();
            var versionIds = ResolveRequiredPhpVersionIds();
            var host = ResolvePhpFastCgiHost();
            var recovered = false;

            foreach (var versionId in versionIds)
            {
                cancellationToken.ThrowIfCancellationRequested();

                var port = ResolvePhpPlannedPort(versionId);
                if (port is null or <= 0)
                {
                    continue;
                }

                if (await IsPhpListenerRunningAsync(versionId, cancellationToken).ConfigureAwait(false))
                {
                    _phpRecoveryAttemptedAt.TryRemove(versionId, out _);
                    continue;
                }

                if (PhpCgiRuntime.TryGetManagedListenerPid(versionId, out var managedPid)
                    && ServiceProcessTools.IsProcessAlive(managedPid))
                {
                    _phpRecoveryAttemptedAt.TryRemove(versionId, out _);
                    continue;
                }

                if (!urgent
                    && _phpRecoveryAttemptedAt.TryGetValue(versionId, out var lastAttempt)
                    && DateTimeOffset.UtcNow - lastAttempt < PhpRecoveryRetryCooldown)
                {
                    continue;
                }

                _diagnostics.LogActivity("PHP", $"Recovering php-cgi on {host}:{port} for {versionId}…");

                var result = await EnsurePhpFastCgiAsync([versionId], cancellationToken).ConfigureAwait(false);
                if (result.Success)
                {
                    recovered = true;
                    _phpRecoveryAttemptedAt.TryRemove(versionId, out _);
                    _diagnostics.LogActivity("PHP", $"Recovered php-cgi for {versionId}.");
                }
                else
                {
                    _phpRecoveryAttemptedAt[versionId] = DateTimeOffset.UtcNow;
                    if (!string.IsNullOrWhiteSpace(result.Message))
                    {
                        _diagnostics.LogUserError("PHP", result.Message);
                    }
                }
            }

            if (recovered)
            {
                PhpListenersChanged?.Invoke(this, EventArgs.Empty);
            }
        }
        finally
        {
            Interlocked.Exchange(ref _phpRecoveryInFlight, 0);
        }
    }

    public string ResolvePhpFastCgiHost()
    {
        var host = _settingsStore.Load().Php.FpmHost;
        return string.IsNullOrWhiteSpace(host) ? "127.0.0.1" : host.Trim();
    }

    public int? ResolvePhpPlannedPort(string versionId) =>
        PhpCgiRuntime.ResolvePlannedPortForVersion(_settingsStore.Load(), _registry, versionId);

    public async Task<bool> IsPhpListenerRunningAsync(string versionId, CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        var port = ResolvePhpPlannedPort(versionId);
        if (port is null or <= 0)
        {
            return false;
        }

        return await PortProbe.IsPortOpenAsync(ResolvePhpFastCgiHost(), port.Value).ConfigureAwait(false);
    }

    public IReadOnlyList<ServiceInfo> ListManagedSnapshot()
    {
        return _services.Values.ToList();
    }

    /// <summary>
    /// Tracked rows for lightweight callers — process-alive only, no netstat sweep.
    /// </summary>
    public IReadOnlyList<ServiceInfo> ListCachedLiveSnapshot()
        => ListLiveQuickAsync().GetAwaiter().GetResult();

    private ServiceInfo BuildQuickLiveInfo(ServiceId serviceId, AppSettings settings)
    {
        var definition = SettingsDefaults.ServiceDefinitions.First(d => d.Id == serviceId);
        var serviceSettings = GetServiceSettings(
            settings,
            serviceId,
            definition.DefaultPort,
            definition.DefaultSslPort,
            definition.PackageId);
        var packageId = serviceSettings.PackageId ?? definition.PackageId;
        var installed = string.IsNullOrWhiteSpace(packageId) || _registry.GetById(packageId) is not null;

        if (definition.Runtime == ServiceRuntime.Library)
        {
            return BuildLiveInfo(serviceId, settings);
        }

        if (IsStarting(serviceId))
        {
            return BuildStarting(definition, serviceSettings);
        }

        if (!serviceSettings.Enabled || !installed)
        {
            return BuildCachedStoppedInfo(definition, serviceSettings);
        }

        var previous = _services.GetValueOrDefault(serviceId);
        if (previous is { PortOpen: true, Pid: int pid })
        {
            if (ServiceProcessTools.IsProcessAlive(pid))
            {
                return previous;
            }

            return BuildLiveInfo(serviceId, settings);
        }

        if (previous?.PortOpen == true)
        {
            return BuildLiveInfo(serviceId, settings);
        }

        return BuildCachedStoppedInfo(definition, serviceSettings);
    }

    private ServiceInfo BuildCachedStoppedInfo(ServiceDefinition definition, ServicePortSettings serviceSettings)
    {
        var packageId = serviceSettings.PackageId ?? definition.PackageId;
        var installed = string.IsNullOrWhiteSpace(packageId) || _registry.GetById(packageId) is not null;
        if (IsUserStoppedIntent(definition.Id))
        {
            _lastErrors.TryRemove(definition.Id, out _);
        }

        string? message = _lastErrors.GetValueOrDefault(definition.Id);
        var status = ServiceStatus.Stopped;

        if (!serviceSettings.Enabled)
        {
            message = "Disabled in settings";
        }
        else if (!installed && !string.IsNullOrWhiteSpace(definition.PackageId))
        {
            message = $"Install {definition.Name} package first";
        }
        else if (!string.IsNullOrWhiteSpace(message))
        {
            status = ServiceStatus.Error;
        }

        return new ServiceInfo
        {
            Id = ToServiceKey(definition.Id),
            Name = definition.Name,
            Status = status,
            Port = serviceSettings.Port,
            SslPort = serviceSettings.SslPort,
            PortOpen = false,
            Installed = installed,
            Enabled = serviceSettings.Enabled,
            Message = message
        };
    }

    public IReadOnlyList<int> ResolvePerformanceMemoryPids(string serviceKey)
    {
        if (!TryParseServiceId(serviceKey, out var serviceId))
        {
            return [];
        }

        var definition = SettingsDefaults.ServiceDefinitions.FirstOrDefault(row => row.Id == serviceId);
        if (definition is null)
        {
            return [];
        }

        var settings = _settingsStore.Load();
        var serviceSettings = GetServiceSettings(
            settings,
            serviceId,
            definition.DefaultPort,
            definition.DefaultSslPort,
            definition.PackageId);
        var trackedPid = _services.GetValueOrDefault(serviceId)?.Pid;
        var owned = StackrootManagedProcessResolver.ResolveOwnedListenerPids(
            serviceId,
            definition,
            serviceSettings,
            _paths,
            _registry,
            trackedPid);

        var memoryPids = new HashSet<int>();
        foreach (var pid in owned)
        {
            foreach (var treePid in ProcessMemoryTools.CollectProcessTree(pid))
            {
                memoryPids.Add(treePid);
            }
        }

        if (trackedPid is > 0)
        {
            foreach (var treePid in ProcessMemoryTools.CollectProcessTree(trackedPid.Value))
            {
                memoryPids.Add(treePid);
            }
        }

        return memoryPids.Count > 0 ? memoryPids.ToList() : [];
    }

    public IReadOnlyList<PhpListenerPerformanceInfo> ListManagedPhpListenerPerformanceTargets()
    {
        var host = ResolvePhpFastCgiHost();
        var listeners = new List<PhpListenerPerformanceInfo>();

        foreach (var (versionId, port) in PhpCgiRuntime.ActiveListeners())
        {
            if (!PhpCgiRuntime.TryGetManagedListenerPid(versionId, out var pid))
            {
                continue;
            }

            var package = _registry.GetById(versionId);
            var name = package is null || string.IsNullOrWhiteSpace(package.Version)
                ? versionId
                : $"PHP {package.Version}";

            listeners.Add(new PhpListenerPerformanceInfo
            {
                Id = versionId,
                Name = name,
                Endpoint = $"{host}:{port}",
                Status = "Running",
                Pid = pid,
                MemoryPids = [pid]
            });
        }

        return listeners;
    }

    private async Task<ServiceInfo> StartNginxCoreAsync(CancellationToken cancellationToken)
    {
        var settings = _settingsStore.Load();
        var definition = SettingsDefaults.ServiceDefinitions.First(d => d.Id == ServiceId.Nginx);
        var serviceSettings = GetServiceSettings(settings, ServiceId.Nginx, definition.DefaultPort, definition.DefaultSslPort, definition.PackageId);

        if (!serviceSettings.Enabled)
        {
            return Fail(ToServiceKey(ServiceId.Nginx), definition.Name, "Service is disabled in settings", serviceSettings.Port);
        }

        var packageId = serviceSettings.PackageId ?? definition.PackageId ?? "nginx-1.26.2";
        var installed = _registry.GetById(packageId);
        if (installed is null)
        {
            return Fail(ToServiceKey(ServiceId.Nginx), definition.Name, $"Install package {packageId} first", serviceSettings.Port);
        }

        var nginxExe = ResolveExecutable(installed.InstallPath, definition.Executable ?? "nginx.exe");
        if (nginxExe is null)
        {
            return Fail(ToServiceKey(ServiceId.Nginx), definition.Name, "nginx executable not found", serviceSettings.Port);
        }

        // If nginx is already running from a previous session, skip the
        // expensive PHP FastCGI startup — it should already be running.
        if (TryEvaluateStartPortBinding(
                ServiceId.Nginx,
                definition,
                serviceSettings,
                out var existingPids,
                out var nginxAlreadyServing,
                out var nginxConflictMessage))
        {
            if (nginxAlreadyServing && existingPids.Count > 0)
            {
                _diagnostics.LogActivity("ServiceManager", "nginx already running — skipping config + PHP");
                var nginxInfo = BuildRunning(definition, serviceSettings, existingPids[0]);
                _services[ServiceId.Nginx] = nginxInfo;
                return nginxInfo;
            }
        }
        else if (nginxConflictMessage is not null)
        {
            return Fail(
                ToServiceKey(ServiceId.Nginx),
                definition.Name,
                nginxConflictMessage,
                serviceSettings.Port);
        }

        var nginxWatch = Stopwatch.StartNew();
        ReportStartProgress(ServiceId.Nginx, "Starting web stack…", nginxWatch);
        NginxRuntime.setupNginxRuntime(_paths, installed.InstallPath);
        ReportStartProgress(ServiceId.Nginx, "Preparing HTTPS certificates and nginx config…", nginxWatch);
        if (_webStackCoordinator is not null)
        {
            if (_webStackCoordinator.WasMainConfigPreparedRecently(TimeSpan.FromMinutes(2)))
            {
                _diagnostics.LogActivity("ServiceManager", "Skipping duplicate PrepareForNginx — stack step already prepared config");
            }
            else
            {
                await _webStackCoordinator.PrepareForNginxAsync(cancellationToken).ConfigureAwait(false);
            }
        }
        else
        {
            NginxRuntime.writeNginxConfig(_paths, serviceSettings, settings.NginxHttp);
        }

        var siteStore = new SiteStore(_paths.DataRoot, _settingsStore);
        _ = siteStore.List();
        ReportStartProgress(ServiceId.Nginx, "Starting PHP FastCGI listeners…", nginxWatch);
        var php = await EnsureStackPhpCgiAsync(cancellationToken);
        if (!php.Success)
        {
            return Fail(ToServiceKey(ServiceId.Nginx), definition.Name, php.Message ?? "Failed to start php-cgi listeners", serviceSettings.Port);
        }

        ReportStartProgress(ServiceId.Nginx, "Starting nginx…", nginxWatch);
        var reloadResult = await NginxControl.ReloadOrStartWithSslRepairAsync(
            _paths,
            installed.InstallPath,
            _jobManager,
            serviceSettings.Host,
            serviceSettings.Port,
            _webStackCoordinator,
            onSslRepair: () =>
            {
                _diagnostics.LogActivity(
                    "ServiceManager",
                    "nginx config references missing HTTPS certificates — regenerating SSL material and retrying.");
                ReportStartProgress(ServiceId.Nginx, "Repairing HTTPS certificates…", nginxWatch);
            },
            cancellationToken).ConfigureAwait(false);

        if (!reloadResult.Ok)
        {
            return Fail(ToServiceKey(ServiceId.Nginx), definition.Name, reloadResult.Message ?? "Failed to start nginx", serviceSettings.Port);
        }

        var info = new ServiceInfo
        {
            Id = ToServiceKey(ServiceId.Nginx),
            Name = definition.Name,
            Status = ServiceStatus.Running,
            Port = serviceSettings.Port,
            SslPort = serviceSettings.SslPort,
            PortOpen = true,
            Installed = true,
            Enabled = true,
            Pid = reloadResult.Pid
        };

        _services[ServiceId.Nginx] = info;
        return info;
    }

    private async Task<ServiceInfo> StartProcessServiceAsync(ServiceId serviceId, CancellationToken cancellationToken)
    {
        var definition = SettingsDefaults.ServiceDefinitions.First(d => d.Id == serviceId);
        var settings = _settingsStore.Load();
        var serviceSettings = GetServiceSettings(settings, serviceId, definition.DefaultPort, definition.DefaultSslPort, definition.PackageId);

        if (!serviceSettings.Enabled)
        {
            return Fail(ToServiceKey(serviceId), definition.Name, "Service is disabled in settings", serviceSettings.Port);
        }

        if (!TryEvaluateStartPortBinding(
                serviceId,
                definition,
                serviceSettings,
                out var ownedPids,
                out var alreadyServing,
                out var conflictMessage))
        {
            return Fail(
                ToServiceKey(serviceId),
                definition.Name,
                conflictMessage ?? ManagedServiceStatusPolicy.FormatPortConflictMessage(serviceSettings.Port),
                serviceSettings.Port);
        }

        if (alreadyServing)
        {
            var alreadyRunning = BuildRunning(definition, serviceSettings, ownedPids[0]);
            _services[serviceId] = alreadyRunning;
            if (serviceId is ServiceId.Mysql or ServiceId.Mariadb)
            {
                var packageIdForSync = serviceSettings.PackageId ?? definition.PackageId;
                var dbInstalled = string.IsNullOrWhiteSpace(packageIdForSync) ? null : _registry.GetById(packageIdForSync);
                if (dbInstalled is not null)
                {
                    await TrySyncSqlCredentialsAfterStartAsync(
                        serviceId,
                        dbInstalled.InstallPath,
                        settings,
                        serviceSettings,
                        cancellationToken).ConfigureAwait(false);
                }
            }

            return alreadyRunning;
        }

        var packageId = serviceSettings.PackageId ?? definition.PackageId;
        if (string.IsNullOrWhiteSpace(packageId))
        {
            return Fail(ToServiceKey(serviceId), definition.Name, "No package configured", serviceSettings.Port);
        }

        var installed = _registry.GetById(packageId);
        if (installed is null)
        {
            return Fail(ToServiceKey(serviceId), definition.Name, $"Install package {packageId} first", serviceSettings.Port);
        }

        var executablePath = ResolveExecutable(installed.InstallPath, definition.Executable ?? string.Empty);
        if (executablePath is null)
        {
            return Fail(ToServiceKey(serviceId), definition.Name, $"Executable not found: {definition.Executable}", serviceSettings.Port);
        }

        var args = BuildArguments(serviceId, serviceSettings, _paths);
        var workingDirectory = Path.GetDirectoryName(executablePath) ?? installed.InstallPath;
        if (serviceId == ServiceId.Redis)
        {
            var confPath = RedisRuntime.WriteRedisConfig(_paths, serviceSettings);
            workingDirectory = RedisRuntime.RedisConfigDirectory(_paths);
            args = [Path.GetFileName(confPath)];
        }
        else
        {
            try
            {
                if (serviceId is ServiceId.Mysql or ServiceId.Mariadb or ServiceId.Postgresql or ServiceId.Mongodb)
                {
                    _diagnostics.LogActivity(
                        "ServiceManager",
                        $"{ToServiceKey(serviceId)}: preparing data directory and config");
                }

                args = PrepareDatabaseStart(serviceId, installed.InstallPath, executablePath, serviceSettings, ref workingDirectory);

                if (serviceId is ServiceId.Mysql or ServiceId.Mariadb or ServiceId.Postgresql or ServiceId.Mongodb)
                {
                    _diagnostics.LogActivity(
                        "ServiceManager",
                        $"{ToServiceKey(serviceId)}: config ready, spawning process");
                }
            }
            catch (Exception ex)
            {
                return Fail(ToServiceKey(serviceId), definition.Name, ex.Message, serviceSettings.Port);
            }
        }

        Process process;
        try
        {
            process = ServiceProcessTools.StartProcess(
                executablePath,
                args,
                workingDirectory,
                _jobManager);
            if (serviceId is ServiceId.Mysql or ServiceId.Mariadb or ServiceId.Postgresql or ServiceId.Mongodb)
            {
                _diagnostics.LogActivity(
                    "ServiceManager",
                    $"{ToServiceKey(serviceId)}: process spawned (pid {process.Id})");
            }
        }
        catch (Exception ex)
        {
            return Fail(ToServiceKey(serviceId), definition.Name, ex.Message, serviceSettings.Port);
        }

        var (portAttempts, portDelayMs) = serviceId is ServiceId.Mysql or ServiceId.Mariadb
            ? (120, 500)
            : serviceId is ServiceId.Postgresql or ServiceId.Mongodb
                ? (60, 500)
                : (24, 250);

        if (serviceId is ServiceId.Mysql or ServiceId.Mariadb or ServiceId.Postgresql or ServiceId.Mongodb)
        {
            _diagnostics.LogActivity(
                "ServiceManager",
                $"{ToServiceKey(serviceId)}: waiting for port {serviceSettings.Port} (up to {portAttempts * portDelayMs / 1000}s)");
        }

        var ready = await WaitForServicePortAsync(process, serviceId, definition, serviceSettings, portAttempts, portDelayMs, cancellationToken);
        if (!ready)
        {
            if (!process.HasExited)
            {
                ProcessKiller.TryKill(process.Id);
            }
            else if (serviceId is ServiceId.Mysql or ServiceId.Mariadb)
            {
                var cleanupPids = StackrootManagedProcessResolver.ResolveOwnedListenerPids(
                    serviceId,
                    definition,
                    serviceSettings,
                    _paths,
                    _registry,
                    process.Id);
                StackrootManagedProcessResolver.TryKillPids(cleanupPids);
            }

            var failureMessage = StackrootManagedProcessResolver.HasForeignListener(
                    serviceId,
                    definition,
                    serviceSettings,
                    _paths,
                    _registry)
                ? ManagedServiceStatusPolicy.FormatPortConflictMessage(serviceSettings.Port)
                : BuildPortWaitFailureMessage(serviceId, serviceSettings);
            return Fail(ToServiceKey(serviceId), definition.Name, failureMessage, serviceSettings.Port);
        }

        ProcessPortTools.InvalidatePortCache(serviceSettings.Port);
        var ownedAfterStart = StackrootManagedProcessResolver.ResolveOwnedListenerPids(
            serviceId,
            definition,
            serviceSettings,
            _paths,
            _registry,
            process.HasExited ? null : process.Id);
        if (ownedAfterStart.Count == 0)
        {
            if (!process.HasExited)
            {
                ProcessKiller.TryKill(process.Id);
            }

            return Fail(
                ToServiceKey(serviceId),
                definition.Name,
                ManagedServiceStatusPolicy.FormatPortConflictMessage(serviceSettings.Port),
                serviceSettings.Port);
        }

        var info = BuildRunning(definition, serviceSettings, ownedAfterStart[0]);
        _services[serviceId] = info;
        if (serviceId is ServiceId.Mysql or ServiceId.Mariadb)
        {
            await TrySyncSqlCredentialsAfterStartAsync(
                serviceId,
                installed.InstallPath,
                settings,
                serviceSettings,
                cancellationToken).ConfigureAwait(false);
        }

        return info;
    }

    private async Task TrySyncSqlCredentialsAfterStartAsync(
        ServiceId serviceId,
        string installPath,
        AppSettings settings,
        ServicePortSettings serviceSettings,
        CancellationToken cancellationToken)
    {
        var applied = await MariaDbProvisioner.ApplyConfiguredCredentialsAsync(
            installPath,
            serviceId,
            settings,
            cancellationToken).ConfigureAwait(false);
        if (applied)
        {
            _diagnostics.LogActivity(
                "ServiceManager",
                $"SQL credentials synced for {ToServiceKey(serviceId)} via {MariaDbCredentialSync.DescribeClientConnection(serviceSettings)}");
        }
        else
        {
            _diagnostics.LogActivity(
                "ServiceManager",
                $"SQL credential sync for {ToServiceKey(serviceId)} did not complete after start");
        }
    }

    private ServiceInfo StartLibraryService(ServiceId serviceId)
    {
        var definition = SettingsDefaults.ServiceDefinitions.First(d => d.Id == serviceId);
        var settings = _settingsStore.Load();
        var serviceSettings = GetServiceSettings(settings, serviceId, definition.DefaultPort, definition.DefaultSslPort, definition.PackageId);
        var packageId = serviceSettings.PackageId ?? definition.PackageId;
        var installed = string.IsNullOrWhiteSpace(packageId) || _registry.GetById(packageId) is not null;

        if (!serviceSettings.Enabled)
        {
            return Fail(ToServiceKey(serviceId), definition.Name, "Service is disabled in settings");
        }

        if (!installed)
        {
            return Fail(ToServiceKey(serviceId), definition.Name, $"Install package {packageId} first");
        }

        var info = new ServiceInfo
        {
            Id = ToServiceKey(serviceId),
            Name = definition.Name,
            Status = ServiceStatus.Running,
            PortOpen = true,
            Installed = true,
            Enabled = true
        };

        _services[serviceId] = info;
        return info;
    }

    private Task<ServiceInfo> BuildStoppedAsync(ServiceDefinition definition, ServicePortSettings serviceSettings)
    {
        if (serviceSettings.Port > 0)
        {
            ProcessPortTools.InvalidatePortCache(serviceSettings.Port);
            var listeners = ServiceProcessTools.FindPidsListeningOnPort(serviceSettings.Port);
            if (listeners.Count > 0 && !IsUserStoppedIntent(definition.Id))
            {
                return Task.FromResult(Fail(
                    ToServiceKey(definition.Id),
                    definition.Name,
                    "Failed to stop - port is still listening",
                    serviceSettings.Port));
            }
        }

        return Task.FromResult(new ServiceInfo
        {
            Id = ToServiceKey(definition.Id),
            Name = definition.Name,
            Status = ServiceStatus.Stopped,
            Port = serviceSettings.Port,
            SslPort = serviceSettings.SslPort,
            PortOpen = false,
            Enabled = serviceSettings.Enabled
        });
    }

    private static ServicePortSettings GetServiceSettings(AppSettings settings, ServiceId id, int defaultPort, int? defaultSslPort, string? packageId)
    {
        if (settings.Services.TryGetValue(id, out var existing))
        {
            return existing;
        }

        return new ServicePortSettings
        {
            Enabled = false,
            Host = "127.0.0.1",
            Port = defaultPort,
            SslPort = defaultSslPort,
            SslEnabled = id == ServiceId.Nginx ? true : null,
            AutoStart = false,
            PackageId = packageId
        };
    }

    private IReadOnlyList<string> PrepareDatabaseStart(
        ServiceId serviceId,
        string installPath,
        string executablePath,
        ServicePortSettings serviceSettings,
        ref string workingDirectory)
    {
        return serviceId switch
        {
            ServiceId.Mysql => StartMariaLike("mysql", installPath, executablePath, serviceSettings, ref workingDirectory),
            ServiceId.Mariadb => StartMariaLike("mariadb", installPath, executablePath, serviceSettings, ref workingDirectory),
            ServiceId.Postgresql => StartPostgreSql(installPath, executablePath, serviceSettings, ref workingDirectory),
            ServiceId.Mongodb => StartMongoDb(serviceSettings, ref workingDirectory),
            _ => BuildArguments(serviceId, serviceSettings, _paths)
        };
    }

    public async Task<bool> SyncEnabledSqlCredentialsAsync(CancellationToken cancellationToken = default)
    {
        var settings = _settingsStore.Load();
        var synced = await MariaDbProvisioner.SyncEnabledCredentialsAsync(_registry, settings, cancellationToken)
            .ConfigureAwait(false);
        if (!synced)
        {
            _diagnostics.LogActivity(
                "ServiceManager",
                "SQL credential sync failed for one or more running database engines");
        }

        return synced;
    }

    /// <summary>
    /// Waits briefly only for database engines that are actively starting in the background.
    /// Enabled-but-not-starting engines are skipped even when their port is closed.
    /// </summary>
    public async Task WaitForEnabledDatabasePortsAsync(CancellationToken cancellationToken = default)
    {
        var settings = _settingsStore.Load();
        foreach (var serviceId in new[] { ServiceId.Mysql, ServiceId.Mariadb, ServiceId.Postgresql, ServiceId.Mongodb })
        {
            if (!Implemented.Contains(serviceId))
            {
                continue;
            }

            await WaitForDatabasePortBriefAsync(serviceId, settings, cancellationToken).ConfigureAwait(false);
        }
    }

    /// <summary>
    /// Waits only while a database engine is actively starting in the background.
    /// Skips disabled engines, uninstalled packages, and enabled engines that are not auto-starting.
    /// </summary>
    private async Task WaitForDatabasePortBriefAsync(
        ServiceId serviceId,
        AppSettings settings,
        CancellationToken cancellationToken)
    {
        if (!Implemented.Contains(serviceId))
        {
            return;
        }

        var definition = SettingsDefaults.ServiceDefinitions.First(d => d.Id == serviceId);
        var serviceSettings = GetServiceSettings(
            settings,
            serviceId,
            definition.DefaultPort,
            definition.DefaultSslPort,
            definition.PackageId);
        if (!serviceSettings.Enabled)
        {
            return;
        }

        var packageId = serviceSettings.PackageId ?? definition.PackageId;
        if (!string.IsNullOrWhiteSpace(packageId) && _registry.GetById(packageId) is null)
        {
            return;
        }

        if (await PortProbe.IsPortOpenAsync(serviceSettings.Host, serviceSettings.Port).ConfigureAwait(false))
        {
            return;
        }

        if (!IsStarting(serviceId))
        {
            _diagnostics.LogActivity(
                "ServiceManager",
                $"Skipping wait for {ToServiceKey(serviceId)} — port {serviceSettings.Port} is closed and the service is not starting");
            return;
        }

        var serviceKey = ToServiceKey(serviceId);
        var startInProgress = true;
        _diagnostics.LogActivity(
            "ServiceManager",
            $"{serviceKey} background start in progress — waiting for {serviceSettings.Host}:{serviceSettings.Port}");

        const int attempts = 120;
        const int delayMs = 250;
        for (var i = 0; i < attempts; i++)
        {
            cancellationToken.ThrowIfCancellationRequested();

            if (await PortProbe.IsPortOpenAsync(serviceSettings.Host, serviceSettings.Port).ConfigureAwait(false))
            {
                return;
            }

            if (startInProgress && !IsStarting(serviceId))
            {
                if (await PortProbe.IsPortOpenAsync(serviceSettings.Host, serviceSettings.Port).ConfigureAwait(false))
                {
                    return;
                }

                _diagnostics.LogActivity(
                    "ServiceManager",
                    $"{serviceKey} background start finished but port {serviceSettings.Port} is still closed");
                return;
            }

            if (i > 0 && i % 20 == 0)
            {
                _diagnostics.LogActivity(
                    "ServiceManager",
                    $"Still waiting for {serviceKey} on port {serviceSettings.Port} (~{i * delayMs / 1000}s)");
            }

            if (i < attempts - 1)
            {
                await PortProbe.SleepAsync(delayMs, cancellationToken).ConfigureAwait(false);
            }
        }
    }

    public async Task WaitForNginxPortAsync(CancellationToken cancellationToken = default)
    {
        var settings = _settingsStore.Load();
        await WaitForServicePortIfNeededAsync(ServiceId.Nginx, settings, cancellationToken).ConfigureAwait(false);
    }

    private async Task WaitForServicePortIfNeededAsync(
        ServiceId serviceId,
        AppSettings settings,
        CancellationToken cancellationToken)
    {
        if (!Implemented.Contains(serviceId))
        {
            return;
        }

        var definition = SettingsDefaults.ServiceDefinitions.First(d => d.Id == serviceId);
        var serviceSettings = GetServiceSettings(
            settings,
            serviceId,
            definition.DefaultPort,
            definition.DefaultSslPort,
            definition.PackageId);
        if (!serviceSettings.Enabled)
        {
            return;
        }

        if (definition.Runtime == ServiceRuntime.Library)
        {
            return;
        }

        var packageId = serviceSettings.PackageId ?? definition.PackageId;
        if (!string.IsNullOrWhiteSpace(packageId) && _registry.GetById(packageId) is null)
        {
            return;
        }

        if (await PortProbe.IsPortOpenAsync(serviceSettings.Host, serviceSettings.Port).ConfigureAwait(false))
        {
            return;
        }

        if (!IsStarting(serviceId))
        {
            _diagnostics.LogActivity(
                "ServiceManager",
                $"Skipping wait for {ToServiceKey(serviceId)} — port {serviceSettings.Port} is closed and the service is not starting");
            return;
        }

        var serviceKey = ToServiceKey(serviceId);
        var startInProgress = true;
        _diagnostics.LogActivity(
            "ServiceManager",
            $"{serviceKey} is starting — waiting for {serviceSettings.Host}:{serviceSettings.Port}");

        const int attempts = 120;
        const int delayMs = 500;
        for (var i = 0; i < attempts; i++)
        {
            cancellationToken.ThrowIfCancellationRequested();

            if (await PortProbe.IsPortOpenAsync(serviceSettings.Host, serviceSettings.Port).ConfigureAwait(false))
            {
                return;
            }

            if (startInProgress && !IsStarting(serviceId))
            {
                if (await PortProbe.IsPortOpenAsync(serviceSettings.Host, serviceSettings.Port).ConfigureAwait(false))
                {
                    return;
                }

                _diagnostics.LogActivity(
                    "ServiceManager",
                    $"{serviceKey} background start finished but port {serviceSettings.Port} is still closed");
                return;
            }

            if (i > 0 && i % 10 == 0)
            {
                _diagnostics.LogActivity(
                    "ServiceManager",
                    $"Still waiting for {serviceKey} on port {serviceSettings.Port} (~{i * delayMs / 1000}s)");
            }

            if (i < attempts - 1)
            {
                await PortProbe.SleepAsync(delayMs, cancellationToken).ConfigureAwait(false);
            }
        }

        if (!await PortProbe.IsPortOpenAsync(serviceSettings.Host, serviceSettings.Port).ConfigureAwait(false))
        {
            _diagnostics.LogUserError(
                "ServiceManager",
                $"{serviceKey} port {serviceSettings.Port} did not open during startup wait");
        }
    }

    private IReadOnlyList<string> StartMariaLike(
        string engine,
        string installPath,
        string mysqldPath,
        ServicePortSettings serviceSettings,
        ref string workingDirectory)
    {
        var (configPath, dataDir) = DatabaseConfigWriter.WriteMariaDbConfig(_paths, serviceSettings, engine);
        workingDirectory = PackageBinaryResolver.ResolvePackageRoot(installPath);
        DatabaseConfigWriter.EnsureMariaDbInitialized(mysqldPath, configPath, dataDir, workingDirectory, engine);

        var args = new List<string> { $"--defaults-file={configPath}" };
        var bootstrapPath = DatabaseConfigWriter.BootstrapInitPath(_paths, engine);
        if (File.Exists(bootstrapPath))
        {
            args.Add($"--init-file={bootstrapPath.Replace('\\', '/')}");
        }

        return args;
    }

    private IReadOnlyList<string> StartPostgreSql(
        string installPath,
        string postgresPath,
        ServicePortSettings serviceSettings,
        ref string workingDirectory)
    {
        var dataDir = DatabaseConfigWriter.DatabaseDataDir(_paths, "postgresql");
        Directory.CreateDirectory(dataDir);
        var initdb = PackageBinaryResolver.ResolvePackageBinary(installPath, "bin/initdb.exe")
            ?? Path.Combine(installPath, "bin", "initdb.exe");
        if (!File.Exists(initdb))
        {
            throw new FileNotFoundException("PostgreSQL initdb.exe was not found.");
        }

        DatabaseConfigWriter.EnsurePostgreSqlInitialized(initdb, dataDir);
        DatabaseConfigWriter.WritePostgreSqlConfig(_paths, serviceSettings);
        workingDirectory = PackageBinaryResolver.ResolvePackageRoot(installPath);
        _ = postgresPath;
        return ["-D", dataDir];
    }

    private IReadOnlyList<string> StartMongoDb(ServicePortSettings serviceSettings, ref string workingDirectory)
    {
        var configPath = DatabaseConfigWriter.WriteMongoDbConfig(_paths, serviceSettings);
        workingDirectory = Path.GetDirectoryName(configPath) ?? _paths.ConfigRoot;
        return ["--config", configPath];
    }

    private static IReadOnlyList<string> BuildArguments(ServiceId serviceId, ServicePortSettings settings, StackrootPaths paths)
    {
        return serviceId switch
        {
            ServiceId.Redis => ["--bind", settings.Host, "--port", settings.Port.ToString()],
            ServiceId.Memcached => ["-p", settings.Port.ToString(), "-l", settings.Host],
            ServiceId.Postgresql => ["-D", Path.Combine(paths.DataRoot, "data", "postgresql")],
            ServiceId.Mongodb => ["--port", settings.Port.ToString(), "--bind_ip", settings.Host],
            ServiceId.Mysql or ServiceId.Mariadb => ["--port", settings.Port.ToString(), "--bind-address", settings.Host],
            _ => []
        };
    }

    private static bool TryParseServiceId(string value, out ServiceId id)
    {
        return Enum.TryParse<ServiceId>(value, ignoreCase: true, out id);
    }

    private static string ToServiceKey(ServiceId id)
    {
        return id.ToString().ToLowerInvariant();
    }

    private static IReadOnlyList<int> PruneDeadOwnedPids(IReadOnlyList<int> ownedPids, int port)
    {
        if (ownedPids.Count == 0)
        {
            return ownedPids;
        }

        var alive = ownedPids.Where(ServiceProcessTools.IsProcessAlive).ToList();
        if (alive.Count != ownedPids.Count)
        {
            ProcessPortTools.InvalidatePortCache(port);
        }

        return alive;
    }

    private bool TryEvaluateStartPortBinding(
        ServiceId serviceId,
        ServiceDefinition definition,
        ServicePortSettings serviceSettings,
        out IReadOnlyList<int> ownedPids,
        out bool alreadyServing,
        out string? conflictMessage)
    {
        conflictMessage = null;
        alreadyServing = false;
        ownedPids = [];
        if (definition.Runtime == ServiceRuntime.Library || serviceSettings.Port <= 0)
        {
            return true;
        }

        // Fast path: if nothing is listening, skip expensive netstat sweep.
        var portProbe = PortProbe.ProbePortAsync(serviceSettings.Host, serviceSettings.Port, timeoutMs: 400)
            .GetAwaiter()
            .GetResult();
        if (portProbe != PortProbeResult.Open)
        {
            return true;
        }

        // Port is open — verify Stackroot ownership before deciding.
        ProcessPortTools.InvalidatePortCache(serviceSettings.Port);
        var trackedPid = _services.GetValueOrDefault(serviceId)?.Pid;
        ownedPids = StackrootManagedProcessResolver.ResolveOwnedListenerPids(
            serviceId,
            definition,
            serviceSettings,
            _paths,
            _registry,
            trackedPid);
        if (StackrootManagedProcessResolver.HasForeignListener(
                serviceId,
                definition,
                serviceSettings,
                _paths,
                _registry))
        {
            conflictMessage = ManagedServiceStatusPolicy.FormatPortConflictMessage(serviceSettings.Port);
            return false;
        }

        alreadyServing = ManagedServiceStatusPolicy.IsStackrootServing(ownedPids.Count);
        if (alreadyServing)
        {
            return true;
        }

        // Double-check: port was open to TCP but we still don't own it.
        ProcessPortTools.InvalidatePortCache(serviceSettings.Port);
        ownedPids = StackrootManagedProcessResolver.ResolveOwnedListenerPids(
            serviceId,
            definition,
            serviceSettings,
            _paths,
            _registry,
            trackedPid);
        if (!ManagedServiceStatusPolicy.IsStackrootServing(ownedPids.Count))
        {
            conflictMessage = ManagedServiceStatusPolicy.FormatPortConflictMessage(serviceSettings.Port);
            return false;
        }

        alreadyServing = true;
        return true;
    }

    private bool TryReattachOwnedListeners(
        ServiceId serviceId,
        ServiceDefinition definition,
        ServicePortSettings serviceSettings,
        ref IReadOnlyList<int> ownedPids,
        out bool ownedPidsAliveVerified)
    {
        ownedPidsAliveVerified = false;
        if (definition.Runtime == ServiceRuntime.Library || serviceSettings.Port <= 0)
        {
            return false;
        }

        if (ownedPids.Count > 0)
        {
            var alive = ownedPids.Where(ServiceProcessTools.IsProcessAlive).ToList();
            ownedPidsAliveVerified = true;
            if (alive.Count == 0)
            {
                ownedPids = [];
                _services.TryRemove(serviceId, out _);
                ProcessPortTools.InvalidatePortCache(serviceSettings.Port);
                return false;
            }

            if (alive.Count != ownedPids.Count)
            {
                ownedPids = alive;
                ProcessPortTools.InvalidatePortCache(serviceSettings.Port);
            }

            return true;
        }

        ProcessPortTools.InvalidatePortCache(serviceSettings.Port);
        var trackedPid = _services.GetValueOrDefault(serviceId)?.Pid;
        ownedPids = StackrootManagedProcessResolver.ResolveOwnedListenerPids(
            serviceId,
            definition,
            serviceSettings,
            _paths,
            _registry,
            trackedPid);
        if (ownedPids.Count == 0)
        {
            return false;
        }

        ownedPidsAliveVerified = true;
        _lastErrors.TryRemove(serviceId, out _);
        _services[serviceId] = BuildRunning(definition, serviceSettings, ownedPids[0]);
        _diagnostics.LogActivity(
            "ServiceManager",
            $"Reattached running {ToServiceKey(serviceId)} (pid {ownedPids[0]})");
        NotifyLiveStatusChanged(ToServiceKey(serviceId));
        return true;
    }

    private ServiceInfo Fail(string id, string name, string message, int? port = null)
    {
        if (TryParseServiceId(id, out var serviceId))
        {
            _lastErrors[serviceId] = message;
            if (ManagedServiceStatusPolicy.IsPortConflictMessage(message))
            {
                var settings = _settingsStore.Load();
                var definition = SettingsDefaults.ServiceDefinitions.FirstOrDefault(d => d.Id == serviceId);
                if (definition is not null)
                {
                    var serviceSettings = GetServiceSettings(
                        settings,
                        serviceId,
                        definition.DefaultPort,
                        definition.DefaultSslPort,
                        definition.PackageId);
                    if (serviceSettings.Enabled && serviceSettings.Supervise && !IsUserStoppedIntent(serviceId))
                    {
                        _supervisionEligible.TryAdd(serviceId, true);
                    }
                }
            }
        }

        var info = new ServiceInfo
        {
            Id = id,
            Name = name,
            Status = ServiceStatus.Error,
            Port = port,
            PortOpen = false,
            Message = message
        };

        if (TryParseServiceId(id, out serviceId))
        {
            _services[serviceId] = info;
        }

        _diagnostics.LogUserError("ServiceManager", $"{name}: {message}");
        return info;
    }

    private static ServiceInfo BuildRunning(ServiceDefinition definition, ServicePortSettings settings, int? pid)
    {
        return new ServiceInfo
        {
            Id = ToServiceKey(definition.Id),
            Name = definition.Name,
            Status = ServiceStatus.Running,
            Port = settings.Port,
            SslPort = settings.SslPort,
            PortOpen = true,
            Installed = true,
            Enabled = true,
            Pid = pid
        };
    }

    private static ServiceInfo BuildStarting(ServiceDefinition definition, ServicePortSettings settings)
    {
        return new ServiceInfo
        {
            Id = ToServiceKey(definition.Id),
            Name = definition.Name,
            Status = ServiceStatus.Starting,
            Port = settings.Port,
            SslPort = settings.SslPort,
            PortOpen = false,
            Installed = true,
            Enabled = settings.Enabled
        };
    }

    private static string? ResolveExecutable(string installPath, string executable)
    {
        return PackageBinaryResolver.ResolvePackageBinary(installPath, executable);
    }

    private static int? ResolveRunningPid(Process process, ServicePortSettings serviceSettings)
    {
        if (!process.HasExited)
        {
            return process.Id;
        }

        var listenerPid = ServiceProcessTools.FindPidsListeningOnPort(serviceSettings.Port).FirstOrDefault();
        return listenerPid > 0 ? listenerPid : null;
    }

    private async Task<bool> WaitForServicePortAsync(
        Process process,
        ServiceId serviceId,
        ServiceDefinition definition,
        ServicePortSettings serviceSettings,
        int attempts,
        int delayMs,
        CancellationToken cancellationToken)
    {
        var allowsDaemonParentExit = serviceId is ServiceId.Mysql or ServiceId.Mariadb;

        for (var i = 0; i < attempts; i++)
        {
            cancellationToken.ThrowIfCancellationRequested();

            if (process.HasExited)
            {
                var daemonParentExitedCleanly = allowsDaemonParentExit && process.ExitCode == 0;
                if (!daemonParentExitedCleanly)
                {
                    _diagnostics.LogUserError(
                        "ServiceManager",
                        $"{serviceId} exited before port {serviceSettings.Port} opened (code {process.ExitCode}).");
                    return false;
                }
            }

            if (await PortProbe.IsPortOpenAsync(serviceSettings.Host, serviceSettings.Port))
            {
                ProcessPortTools.InvalidatePortCache(serviceSettings.Port);
                if (StackrootManagedProcessResolver.HasForeignListener(
                        serviceId,
                        definition,
                        serviceSettings,
                        _paths,
                        _registry))
                {
                    return false;
                }

                var trackedPid = process.HasExited ? null : (int?)process.Id;
                var owned = StackrootManagedProcessResolver.ResolveOwnedListenerPids(
                    serviceId,
                    definition,
                    serviceSettings,
                    _paths,
                    _registry,
                    trackedPid);
                if (owned.Count == 0)
                {
                    return false;
                }

                if (allowsDaemonParentExit && process.HasExited)
                {
                    _jobManager.AssignProcess(owned[0]);
                }

                return true;
            }

            if (i < attempts - 1)
            {
                await PortProbe.SleepAsync(delayMs, cancellationToken);
            }
        }

        return false;
    }

    private string BuildPortWaitFailureMessage(ServiceId serviceId, ServicePortSettings serviceSettings)
    {
        var message = "Process started but port is not listening";
        if (serviceId is not (ServiceId.Mysql or ServiceId.Mariadb))
        {
            return message;
        }

        var engine = serviceId == ServiceId.Mariadb ? "mariadb" : "mysql";
        var logPath = Path.Combine(_paths.LogsRoot, $"{engine}.log");
        if (!File.Exists(logPath))
        {
            var dataDir = DatabaseConfigWriter.DatabaseDataDir(_paths, engine);
            if (!Directory.Exists(Path.Combine(dataDir, "mysql")))
            {
                return $"{message}. Database data directory is not initialized — check {engine}.log after retry.";
            }

            return message;
        }

        var tail = string.Join(
            Environment.NewLine,
            File.ReadLines(logPath).TakeLast(4));
        return string.IsNullOrWhiteSpace(tail) ? message : $"{message}. {tail}";
    }

    private bool IsStarting(ServiceId serviceId)
    {
        lock (_startingSync)
        {
            return _starting.Contains(serviceId);
        }
    }

    private bool TryMarkStarting(ServiceId serviceId)
    {
        lock (_startingSync)
        {
            if (_starting.Add(serviceId))
            {
                return true;
            }

            return false;
        }
    }

    private void UnmarkStarting(ServiceId serviceId)
    {
        lock (_startingSync)
        {
            _starting.Remove(serviceId);
        }
    }

    private void StartSupervision()
    {
        if (_supervisionStarted) return;
        _supervisionStarted = true;
        EnsureSupervisionRestartCancellation();
        _supervisionTimer = new System.Timers.Timer(5_000);
        _supervisionTimer.Elapsed += (_, _) => SupervisionTick();
        _supervisionTimer.AutoReset = true;
        _supervisionTimer.Start();
        _diagnostics.LogActivity("ServiceManager", "Service supervision started (5s interval)");
    }

    private void StopSupervision()
    {
        CancelSupervisionRestarts();

        if (_supervisionTimer is null) return;
        _supervisionTimer.Stop();
        _supervisionTimer.Dispose();
        _supervisionTimer = null;
        _supervisionStarted = false;
    }

    private void EnsureSupervisionRestartCancellation()
    {
        lock (_supervisionRestartCtsSync)
        {
            if (_supervisionRestartCts is null || _supervisionRestartCts.IsCancellationRequested)
            {
                _supervisionRestartCts?.Dispose();
                _supervisionRestartCts = new CancellationTokenSource();
            }
        }
    }

    private CancellationToken SupervisionRestartToken
    {
        get
        {
            lock (_supervisionRestartCtsSync)
            {
                EnsureSupervisionRestartCancellation();
                return _supervisionRestartCts!.Token;
            }
        }
    }

    private void CancelSupervisionRestarts()
    {
        lock (_supervisionRestartCtsSync)
        {
            if (_supervisionRestartCts is null)
            {
                return;
            }

            try
            {
                _supervisionRestartCts.Cancel();
            }
            catch (ObjectDisposedException)
            {
            }

            _supervisionRestartCts.Dispose();
            _supervisionRestartCts = null;
        }
    }

    private void SupervisionTick()
    {
        if (ApplicationShutdownState.ShutdownRequested || ApplicationShutdownState.IsShuttingDown)
        {
            return;
        }

        if (Interlocked.CompareExchange(ref _supervisionTickInFlight, 1, 0) != 0)
        {
            return;
        }

        _ = RunSupervisionTickAsync();
    }

    private Task RunSupervisionTickAsync()
    {
        try
        {
            var settings = _settingsStore.Load();
            foreach (var definition in SettingsDefaults.ServiceDefinitions)
            {
                if (ApplicationShutdownState.ShutdownRequested || ApplicationShutdownState.IsShuttingDown)
                {
                    return Task.CompletedTask;
                }

                if (!Implemented.Contains(definition.Id)) continue;
                if (definition.Runtime == ServiceRuntime.Library) continue;

                var serviceSettings = GetServiceSettings(
                    settings, definition.Id, definition.DefaultPort,
                    definition.DefaultSslPort, definition.PackageId);

                if (!serviceSettings.Enabled) continue;

                var packageId = serviceSettings.PackageId ?? definition.PackageId;
                if (!string.IsNullOrWhiteSpace(packageId) && _registry.GetById(packageId) is null) continue;

                if (IsStarting(definition.Id)) continue;

                if (_services.GetValueOrDefault(definition.Id) is { PortOpen: true, Pid: int alivePid } trackedRunning
                    && alivePid > 0)
                {
                    if (ServiceProcessTools.IsProcessAlive(alivePid))
                    {
                        if (serviceSettings.Supervise
                            && !IsUserStoppedIntent(definition.Id))
                        {
                            _supervisionEligible.TryAdd(definition.Id, true);
                            _supervisionFailures.TryRemove(definition.Id, out _);
                        }

                        continue;
                    }

                    if (!IsUserStoppedIntent(definition.Id)
                        && !IsWithinSupervisionRecoveryGrace(definition.Id)
                        && !_supervisionRestartInFlight.ContainsKey(definition.Id))
                    {
                        _ = BuildLiveInfo(definition.Id, settings);
                    }
                }
                else if (_services.GetValueOrDefault(definition.Id) is { PortOpen: true } trackedRunningNoPid)
                {
                    if (IsUserStoppedIntent(definition.Id))
                    {
                        continue;
                    }

                    var healthPids = StackrootManagedProcessResolver.ResolveOwnedListenerPids(
                        definition.Id,
                        definition,
                        serviceSettings,
                        _paths,
                        _registry,
                        trackedRunningNoPid.Pid);
                    healthPids = PruneDeadOwnedPids(healthPids, serviceSettings.Port);
                    if (healthPids.Count == 0
                        && !IsWithinSupervisionRecoveryGrace(definition.Id)
                        && !_supervisionRestartInFlight.ContainsKey(definition.Id))
                    {
                        _ = BuildLiveInfo(definition.Id, settings);
                    }
                }

                if (!serviceSettings.Supervise || IsUserStoppedIntent(definition.Id)) continue;

                var trackedPid = _services.GetValueOrDefault(definition.Id)?.Pid;
                var ownedPids = StackrootManagedProcessResolver.ResolveOwnedListenerPids(
                    definition.Id,
                    definition,
                    serviceSettings,
                    _paths,
                    _registry,
                    trackedPid);
                if (ownedPids.Count == 0)
                {
                    TryReattachOwnedListeners(
                        definition.Id,
                        definition,
                        serviceSettings,
                        ref ownedPids,
                        out _);
                }

                if (ownedPids.Count > 0)
                {
                    ownedPids = PruneDeadOwnedPids(ownedPids, serviceSettings.Port);
                }

                if (ownedPids.Count > 0 && !IsUserStoppedIntent(definition.Id))
                {
                    _supervisionEligible.TryAdd(definition.Id, true);
                    _supervisionFailures.TryRemove(definition.Id, out _);
                    continue;
                }

                if (!_supervisionEligible.ContainsKey(definition.Id))
                {
                    continue;
                }

                if (StackrootManagedProcessResolver.HasForeignListener(
                        definition.Id,
                        definition,
                        serviceSettings,
                        _paths,
                        _registry))
                {
                    _diagnostics.LogActivity(
                        "ServiceManager",
                        $"Supervision: skipping {ToServiceKey(definition.Id)} restart — foreign listener on port {serviceSettings.Port}");
                    continue;
                }

                TryScheduleSupervisionRestart(
                    definition.Id,
                    "supervision tick");
            }
        }
        finally
        {
            Interlocked.Exchange(ref _supervisionTickInFlight, 0);
        }

        return Task.CompletedTask;
    }

    private void TryScheduleSupervisionRestart(
        ServiceId serviceId,
        string trigger,
        bool confirmedDown = false)
    {
        if (ApplicationShutdownState.ShutdownRequested || ApplicationShutdownState.IsShuttingDown)
        {
            return;
        }

        if (IsStarting(serviceId) || _supervisionRestartInFlight.ContainsKey(serviceId))
        {
            return;
        }

        if (IsUserStoppedIntent(serviceId))
        {
            return;
        }

        if (IsWithinSupervisionRecoveryGrace(serviceId))
        {
            return;
        }

        var settings = _settingsStore.Load();
        var definition = SettingsDefaults.ServiceDefinitions.FirstOrDefault(d => d.Id == serviceId);
        if (definition is null
            || definition.Runtime == ServiceRuntime.Library
            || !Implemented.Contains(serviceId))
        {
            return;
        }

        var serviceSettings = GetServiceSettings(
            settings,
            serviceId,
            definition.DefaultPort,
            definition.DefaultSslPort,
            definition.PackageId);

        if (!serviceSettings.Enabled || !serviceSettings.Supervise)
        {
            return;
        }

        if (!_supervisionEligible.ContainsKey(serviceId))
        {
            return;
        }

        var packageId = serviceSettings.PackageId ?? definition.PackageId;
        if (!string.IsNullOrWhiteSpace(packageId) && _registry.GetById(packageId) is null)
        {
            return;
        }

        if (!confirmedDown)
        {
            var trackedPid = _services.GetValueOrDefault(serviceId)?.Pid;
            var ownedPids = StackrootManagedProcessResolver.ResolveOwnedListenerPids(
                serviceId,
                definition,
                serviceSettings,
                _paths,
                _registry,
                trackedPid);
            if (StackrootManagedProcessResolver.HasForeignListener(
                    serviceId,
                    definition,
                    serviceSettings,
                    _paths,
                    _registry))
            {
                _diagnostics.LogActivity(
                    "ServiceManager",
                    $"Supervision: skipping {ToServiceKey(serviceId)} restart — foreign listener on port {serviceSettings.Port}");
                return;
            }
        }

        var now = DateTime.UtcNow;
        if (_supervisionCooldowns.TryGetValue(serviceId, out var cooldownUntil) && now < cooldownUntil)
        {
            return;
        }

        if (!_supervisionRestartInFlight.TryAdd(serviceId, 0))
        {
            return;
        }

        var failures = _supervisionFailures.AddOrUpdate(serviceId, 1, (_, count) => count + 1);
        if (failures >= MaxSupervisionFailures)
        {
            _diagnostics.LogActivity(
                "ServiceManager",
                $"Supervision: {ToServiceKey(serviceId)} restart failed ({failures}×) — keep-alive will retry after cooldown");
            if (failures % MaxSupervisionFailures == 0)
            {
                RaiseSupervisionAlert(serviceId, definition.Name, failures);
            }
        }

        var id = ToServiceKey(serviceId);
        var delaySec = Math.Min(5 * (int)Math.Pow(3, Math.Min(failures - 1, 4)), 300);
        _supervisionCooldowns[serviceId] = now.AddSeconds(delaySec);

        _diagnostics.LogActivity(
            "ServiceManager",
            $"Supervision: restarting {id} after {trigger} (failure #{failures}, next cooldown {delaySec}s)");

        if (!TryMarkStarting(serviceId))
        {
            _supervisionRestartInFlight.TryRemove(serviceId, out _);
            return;
        }

        NotifyLiveStatusChanged(id, BuildStarting(definition, serviceSettings));

        _ = Task.Run(async () =>
        {
            ServiceInfo? result = null;
            var slotAcquired = false;
            var restartToken = SupervisionRestartToken;
            try
            {
                if (ApplicationShutdownState.ShutdownRequested || ApplicationShutdownState.IsShuttingDown)
                {
                    return;
                }

                await _supervisionRestartSlots
                    .WaitAsync(restartToken)
                    .ConfigureAwait(false);
                slotAcquired = true;

                if (ApplicationShutdownState.ShutdownRequested || ApplicationShutdownState.IsShuttingDown)
                {
                    return;
                }

                result = await StartCoreAsync(serviceId, restartToken).ConfigureAwait(false);
                LogStartResult(id, result);
                if (result.PortOpen == true || result.Status == ServiceStatus.Running)
                {
                    _supervisionEligible.TryAdd(serviceId, true);
                }

                if (result.PortOpen == true)
                {
                    _diagnostics.LogActivity(
                        "ServiceManager",
                        $"Supervision: {id} recovered successfully");
                    _supervisionFailures.TryRemove(serviceId, out _);
                    _supervisionCooldowns.TryRemove(serviceId, out _);
                    _supervisionRecoveredAt[serviceId] = DateTime.UtcNow;
                    _unexpectedStopHandledAt.TryRemove(serviceId, out _);
                }
            }
            catch (OperationCanceledException)
            {
                // Queued supervision restart cancelled during shutdown.
            }
            catch (Exception ex)
            {
                _diagnostics.LogException("ServiceManager.Supervision", ex);
            }
            finally
            {
                if (slotAcquired)
                {
                    _supervisionRestartSlots.Release();
                }

                UnmarkStarting(serviceId);
                NotifyLiveStatusChanged(id, result);
                _supervisionRestartInFlight.TryRemove(serviceId, out _);
            }
        });
    }

    private void SeedSupervisionEligibility(AppSettings settings)
    {
        foreach (var definition in SettingsDefaults.ServiceDefinitions)
        {
            if (!Implemented.Contains(definition.Id) || definition.Runtime == ServiceRuntime.Library)
            {
                continue;
            }

            var serviceSettings = GetServiceSettings(
                settings,
                definition.Id,
                definition.DefaultPort,
                definition.DefaultSslPort,
                definition.PackageId);
            if (!serviceSettings.Enabled || !serviceSettings.Supervise || IsUserStoppedIntent(definition.Id))
            {
                continue;
            }

            var live = BuildLiveInfo(definition.Id, settings);
            if (live.PortOpen == true)
            {
                _supervisionEligible.TryAdd(definition.Id, true);
                continue;
            }

            if (ManagedServiceStatusPolicy.IsPortConflictMessage(_lastErrors.GetValueOrDefault(definition.Id)))
            {
                _supervisionEligible.TryAdd(definition.Id, true);
            }
        }
    }

    private void MarkUserStoppedIntent(ServiceId serviceId)
    {
        _userStoppedServices.TryAdd(serviceId, 0);
        _supervisionEligible.TryRemove(serviceId, out _);
        _supervisionFailures.TryRemove(serviceId, out _);
        _supervisionCooldowns.TryRemove(serviceId, out _);
        _supervisionRestartInFlight.TryRemove(serviceId, out _);
    }

    private void ClearUserStoppedIntent(ServiceId serviceId)
        => _userStoppedServices.TryRemove(serviceId, out _);

    private bool IsUserStoppedIntent(ServiceId serviceId)
        => _userStoppedServices.ContainsKey(serviceId);

    public int GetSupervisionFailureCount(ServiceId serviceId)
        => _supervisionFailures.GetValueOrDefault(serviceId);

    public bool IsSupervisionEligible(ServiceId serviceId)
        => _supervisionEligible.ContainsKey(serviceId);

    public bool IsSupervisionRecoveryInFlight(ServiceId serviceId)
        => _supervisionRestartInFlight.ContainsKey(serviceId) || IsStarting(serviceId);

    public bool IsUserStoppedIntent(string serviceKey)
        => TryParseServiceId(serviceKey, out var serviceId) && IsUserStoppedIntent(serviceId);

    public void MarkUserStoppedIntent(string serviceKey)
    {
        if (TryParseServiceId(serviceKey, out var serviceId))
        {
            MarkUserStoppedIntent(serviceId);
        }
    }

    private void RaiseSupervisionAlert(ServiceId serviceId, string serviceName, int failureCount)
    {
        try
        {
            SupervisionAlert?.Invoke(
                this,
                new ServiceSupervisionAlertEventArgs
                {
                    ServiceId = serviceId,
                    ServiceKey = ToServiceKey(serviceId),
                    ServiceName = serviceName,
                    FailureCount = failureCount
                });
        }
        catch
        {
            // Alert delivery is best-effort.
        }
    }

    private void ReportStartProgress(ServiceId serviceId, string message, Stopwatch? elapsed = null)
    {
        if (!IsStarting(serviceId))
        {
            return;
        }

        var definition = SettingsDefaults.ServiceDefinitions.First(d => d.Id == serviceId);
        var settings = _settingsStore.Load();
        var serviceSettings = GetServiceSettings(
            settings,
            serviceId,
            definition.DefaultPort,
            definition.DefaultSslPort,
            definition.PackageId);

        var info = new ServiceInfo
        {
            Id = ToServiceKey(serviceId),
            Name = definition.Name,
            Status = ServiceStatus.Starting,
            Port = serviceSettings.Port,
            SslPort = serviceSettings.SslPort,
            PortOpen = false,
            Installed = true,
            Enabled = serviceSettings.Enabled,
            Message = message
        };

        _services[serviceId] = info;
        NotifyLiveStatusChanged(info.Id, info);
    }

    public void PrepareForShutdown()
    {
        StopSupervision();
    }

    public void Dispose()
    {
        if (_disposed)
        {
            return;
        }

        _disposed = true;
        StopSupervision();
        _supervisionRestartSlots.Dispose();
        GC.SuppressFinalize(this);
    }
}

public sealed class ServiceSupervisionAlertEventArgs : EventArgs
{
    public ServiceId ServiceId { get; init; }

    public string ServiceKey { get; init; } = string.Empty;

    public string ServiceName { get; init; } = string.Empty;

    public int FailureCount { get; init; }
}

public sealed class PhpListenerPerformanceInfo
{
    public string Id { get; init; } = string.Empty;

    public string Name { get; init; } = string.Empty;

    public string Endpoint { get; init; } = string.Empty;

    public string Status { get; init; } = string.Empty;

    public int? Pid { get; init; }

    public IReadOnlyList<int> MemoryPids { get; init; } = [];
}
