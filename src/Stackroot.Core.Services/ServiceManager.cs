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

public sealed class ServiceManager
{
    private readonly StackrootPaths _paths;
    private readonly InstallRegistryStore _registry;
    private readonly SettingsStore _settingsStore;
    private readonly IProcessJobManager _jobManager;
    private readonly IDiagnosticsReporter _diagnostics;
    private readonly PackageCatalogStore _catalog;
    private readonly NginxWebStackCoordinator? _webStackCoordinator;
    private readonly Dictionary<ServiceId, ServiceInfo> _services = [];
    private readonly Dictionary<ServiceId, string> _lastErrors = [];
    private readonly HashSet<ServiceId> _starting = [];
    private readonly object _startingSync = new();
    private readonly ConcurrentDictionary<string, DateTimeOffset> _phpRecoveryAttemptedAt = new(StringComparer.OrdinalIgnoreCase);
    private readonly ConcurrentDictionary<ServiceId, DateTime> _supervisionCooldowns = new();
    private readonly ConcurrentDictionary<ServiceId, int> _supervisionFailures = new();
    private readonly ConcurrentDictionary<ServiceId, bool> _supervisionEligible = new();
    private System.Timers.Timer? _supervisionTimer;
    private bool _supervisionStarted;

    public event EventHandler<ServiceInfo>? LiveStatusChanged;

    public event EventHandler? PhpListenersChanged;

    private static readonly HashSet<ServiceId> Implemented = new()
    {
        ServiceId.Nginx,
        ServiceId.Redis,
        ServiceId.Memcached,
        ServiceId.Imagemagick,
        ServiceId.Gdlibs,
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

    public Task<IReadOnlyList<ServiceInfo>> ListLiveAsync(CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        var rows = new List<ServiceInfo>();

        foreach (var definition in SettingsDefaults.ServiceDefinitions)
        {
            cancellationToken.ThrowIfCancellationRequested();
            rows.Add(BuildLiveInfo(definition.Id));
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
        var info = snapshot ?? TryBuildLiveInfo(id);
        if (info is null)
        {
            return;
        }

        LiveStatusChanged?.Invoke(this, info);
    }

    private ServiceInfo BuildLiveInfo(ServiceId serviceId)
    {
        var settings = _settingsStore.Load();
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
            var trackedPid = _services.GetValueOrDefault(serviceId)?.Pid;
            ownedPids = StackrootManagedProcessResolver.ResolveOwnedListenerPids(
                serviceId,
                definition,
                serviceSettings,
                _paths,
                _registry,
                trackedPid);
            portOpen = ownedPids.Count > 0;
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
            message = null;
        }
        else if (!portOpen && !string.IsNullOrWhiteSpace(message))
        {
            status = ServiceStatus.Error;
        }

        if (portOpen)
        {
            _lastErrors.Remove(serviceId);
            message = null;
        }

        return new ServiceInfo
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

        _lastErrors.Remove(serviceId);

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
            ServiceId.Imagemagick or ServiceId.Gdlibs => Task.FromResult(StartLibraryService(serviceId)),
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

        // User manually stopped — remove from supervision eligibility
        _supervisionEligible.TryRemove(serviceId, out _);

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

            _lastErrors.Remove(serviceId);

            if (!StackrootManagedProcessResolver.IsServicePackageInstalled(definition, stopSettings, _registry))
            {
                _services.Remove(serviceId);
                notification = await BuildStoppedAsync(definition, stopSettings).ConfigureAwait(false);
                return notification;
            }

            if (definition.Runtime == ServiceRuntime.Library)
            {
                _services.Remove(serviceId);
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
                _services.Remove(serviceId);
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
                _services.Remove(serviceId);
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

            _services.Remove(serviceId);
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

        _lastErrors.Remove(serviceId);

        if (!StackrootManagedProcessResolver.IsServicePackageInstalled(definition, serviceSettings, _registry))
        {
            _services.Remove(serviceId);
            return;
        }

        if (definition.Runtime == ServiceRuntime.Library)
        {
            _services.Remove(serviceId);
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
            _services.Remove(serviceId);
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
        _services.Remove(serviceId);

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

            if (definition.Runtime != ServiceRuntime.Library && await PortProbe.IsPortOpenAsync(serviceSettings.Host, serviceSettings.Port))
            {
                continue;
            }

            await StartAsync(ToServiceKey(definition.Id), cancellationToken, ServiceStartMode.WaitUntilReady)
                .ConfigureAwait(false);
        }

        StartSupervision();
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

    public async Task TryRecoverRequiredPhpAsync(CancellationToken cancellationToken = default)
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

            if (_phpRecoveryAttemptedAt.TryGetValue(versionId, out var lastAttempt)
                && DateTimeOffset.UtcNow - lastAttempt < TimeSpan.FromSeconds(30))
            {
                continue;
            }

            _phpRecoveryAttemptedAt[versionId] = DateTimeOffset.UtcNow;
            _diagnostics.LogActivity("PHP", $"Recovering php-cgi on {host}:{port} for {versionId}…");

            var result = await EnsurePhpFastCgiAsync([versionId], cancellationToken).ConfigureAwait(false);
            if (result.Success)
            {
                recovered = true;
                _diagnostics.LogActivity("PHP", $"Recovered php-cgi for {versionId}.");
            }
            else if (!string.IsNullOrWhiteSpace(result.Message))
            {
                _diagnostics.LogUserError("PHP", result.Message);
            }
        }

        if (recovered)
        {
            PhpListenersChanged?.Invoke(this, EventArgs.Empty);
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
        if (await PortProbe.IsPortOpenAsync(serviceSettings.Host, serviceSettings.Port))
        {
            var existingPids = StackrootManagedProcessResolver.ResolveOwnedListenerPids(
                ServiceId.Nginx, definition, serviceSettings, _paths, _registry);
            if (existingPids.Count > 0)
            {
                _diagnostics.LogActivity("ServiceManager", "nginx already running — skipping config + PHP");
                var nginxInfo = BuildRunning(definition, serviceSettings, existingPids[0]);
                _services[ServiceId.Nginx] = nginxInfo;
                return nginxInfo;
            }
        }

        NginxRuntime.setupNginxRuntime(_paths, installed.InstallPath);
        ReportStartProgress(ServiceId.Nginx, "Preparing HTTPS certificates and nginx config…");
        if (_webStackCoordinator is not null)
        {
            await _webStackCoordinator.PrepareForNginxAsync(cancellationToken).ConfigureAwait(false);
        }
        else
        {
            NginxRuntime.writeNginxConfig(_paths, serviceSettings);
        }

        var siteStore = new SiteStore(_paths.DataRoot, _settingsStore);
        _ = siteStore.List();
        ReportStartProgress(ServiceId.Nginx, "Starting PHP FastCGI listeners…");
        var php = await EnsureStackPhpCgiAsync(cancellationToken);
        if (!php.Success)
        {
            return Fail(ToServiceKey(ServiceId.Nginx), definition.Name, php.Message ?? "Failed to start php-cgi listeners", serviceSettings.Port);
        }

        ReportStartProgress(ServiceId.Nginx, "Starting nginx…");
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
                ReportStartProgress(ServiceId.Nginx, "Repairing HTTPS certificates…");
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

        if (await PortProbe.IsPortOpenAsync(serviceSettings.Host, serviceSettings.Port).ConfigureAwait(false))
        {
            var ownedPids = StackrootManagedProcessResolver.ResolveOwnedListenerPids(
                serviceId,
                definition,
                serviceSettings,
                _paths,
                _registry);

            if (ownedPids.Count == 0)
            {
                return Fail(
                    ToServiceKey(serviceId),
                    definition.Name,
                    $"Port {serviceSettings.Port} is already in use by another application",
                    serviceSettings.Port);
            }

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

        var ready = await WaitForServicePortAsync(process, serviceId, serviceSettings, portAttempts, portDelayMs, cancellationToken);
        if (!ready)
        {
            if (!process.HasExited)
            {
                ProcessKiller.TryKill(process.Id);
            }
            else if (serviceId is ServiceId.Mysql or ServiceId.Mariadb)
            {
                var ownedPids = StackrootManagedProcessResolver.ResolveOwnedListenerPids(
                    serviceId,
                    definition,
                    serviceSettings,
                    _paths,
                    _registry,
                    process.Id);
                StackrootManagedProcessResolver.TryKillPids(ownedPids);
            }

            var failureMessage = BuildPortWaitFailureMessage(serviceId, serviceSettings);
            return Fail(ToServiceKey(serviceId), definition.Name, failureMessage, serviceSettings.Port);
        }

        var info = BuildRunning(definition, serviceSettings, ResolveRunningPid(process, serviceSettings));
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

    private async Task<ServiceInfo> BuildStoppedAsync(ServiceDefinition definition, ServicePortSettings serviceSettings)
    {
        var portOpen = await PortProbe.IsPortOpenAsync(serviceSettings.Host, serviceSettings.Port).ConfigureAwait(false);
        if (portOpen)
        {
            return Fail(ToServiceKey(definition.Id), definition.Name, "Failed to stop - port is still listening", serviceSettings.Port);
        }

        return new ServiceInfo
        {
            Id = ToServiceKey(definition.Id),
            Name = definition.Name,
            Status = ServiceStatus.Stopped,
            Port = serviceSettings.Port,
            SslPort = serviceSettings.SslPort,
            PortOpen = false,
            Enabled = serviceSettings.Enabled
        };
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
            ServiceId.Memcached => ["-p", settings.Port.ToString(), "-l", settings.Host, "-m", "64"],
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

    private ServiceInfo Fail(string id, string name, string message, int? port = null)
    {
        if (TryParseServiceId(id, out var serviceId))
        {
            _lastErrors[serviceId] = message;
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
                if (allowsDaemonParentExit && process.HasExited)
                {
                    var listenerPid = ServiceProcessTools.FindPidsListeningOnPort(serviceSettings.Port).FirstOrDefault();
                    if (listenerPid > 0)
                    {
                        _jobManager.AssignProcess(listenerPid);
                    }
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
            return _starting.Add(serviceId);
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
        _supervisionTimer = new System.Timers.Timer(5_000);
        _supervisionTimer.Elapsed += (_, _) => SupervisionTick();
        _supervisionTimer.AutoReset = true;
        _supervisionTimer.Start();
        _diagnostics.LogActivity("ServiceManager", "Service supervision started (5s interval)");
    }

    private void StopSupervision()
    {
        if (_supervisionTimer is null) return;
        _supervisionTimer.Stop();
        _supervisionTimer.Dispose();
        _supervisionTimer = null;
        _supervisionStarted = false;
    }

    private void SupervisionTick()
    {
        if (ApplicationShutdownState.ShutdownRequested || ApplicationShutdownState.IsShuttingDown)
        {
            return;
        }

        var settings = _settingsStore.Load();
        foreach (var definition in SettingsDefaults.ServiceDefinitions)
        {
            if (!Implemented.Contains(definition.Id)) continue;
            if (definition.Runtime == ServiceRuntime.Library) continue;

            var serviceSettings = GetServiceSettings(
                settings, definition.Id, definition.DefaultPort,
                definition.DefaultSslPort, definition.PackageId);

            if (!serviceSettings.Enabled || !serviceSettings.Supervise) continue;

            var packageId = serviceSettings.PackageId ?? definition.PackageId;
            if (!string.IsNullOrWhiteSpace(packageId) && _registry.GetById(packageId) is null) continue;

            // Don't interfere with a service that's already starting
            if (IsStarting(definition.Id)) continue;

            // Port check
            try
            {
                if (PortProbe.IsPortOpenAsync(serviceSettings.Host, serviceSettings.Port)
                    .GetAwaiter().GetResult())
                {
                    // Service is alive — mark eligible and reset failure count
                    _supervisionEligible.TryAdd(definition.Id, true);
                    _supervisionFailures.TryRemove(definition.Id, out _);
                    continue;
                }
            }
            catch
            {
                continue;
            }

            // Service is down — check cooldown
            var now = DateTime.UtcNow;
            if (_supervisionCooldowns.TryGetValue(definition.Id, out var cooldownUntil)
                && now < cooldownUntil)
            {
                continue;
            }

            // Only restart services that were actually started (auto or manually)
            if (!_supervisionEligible.ContainsKey(definition.Id)) continue;

            // Calculate backoff: 5s → 15s → 45s → 120s → 300s max
            var failures = _supervisionFailures.AddOrUpdate(definition.Id, 1, (_, c) => c + 1);
            var delaySec = Math.Min(5 * (int)Math.Pow(3, Math.Min(failures - 1, 4)), 300);
            _supervisionCooldowns[definition.Id] = now.AddSeconds(delaySec);

            var id = ToServiceKey(definition.Id);
            _diagnostics.LogActivity(
                "ServiceManager",
                $"Supervision: restarting {id} (failure #{failures}, next cooldown {delaySec}s)");

            // Restart in background — don't block the timer
            _ = Task.Run(async () =>
            {
                try
                {
                    if (ApplicationShutdownState.ShutdownRequested || ApplicationShutdownState.IsShuttingDown)
                    {
                        return;
                    }

                    var info = await StartAndWaitAsync(definition.Id, id, CancellationToken.None)
                        .ConfigureAwait(false);
                    if (info.PortOpen == true)
                    {
                        _diagnostics.LogActivity("ServiceManager",
                            $"Supervision: {id} recovered successfully");
                        _supervisionFailures.TryRemove(definition.Id, out _);
                    }
                }
                catch (Exception ex)
                {
                    _diagnostics.LogException("ServiceManager.Supervision", ex);
                }
            });
        }
    }

    private void ReportStartProgress(ServiceId serviceId, string message)
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
}
