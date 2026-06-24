using System.Diagnostics;
using Stackroot.Core.Abstractions;
using Stackroot.Core.AdminTools;
using Stackroot.Core.Services;
using Stackroot.Core.Services.Php;
using Stackroot.Core.Settings;
using Stackroot.Core.Supervisor;
using Stackroot.Core.Windows;
using Stackroot.Engine.Runtime;

namespace Stackroot.App.Services;

/// <summary>
/// Single source of truth for managed runtime status (services, PHP listeners, processes).
/// Uses Stackroot-managed state — no port probing for listeners.
/// </summary>
public sealed class RuntimeStateService : IDisposable
{
    private readonly ServiceManager _serviceManager;
    private readonly SettingsStore _settingsStore;
    private readonly PhpConfigWriter _phpConfigWriter;
    private readonly GlobalProcessManager _processManager;
    private readonly MailpitManager _mailpitManager;
    private readonly TestDnsCoordinator _testDnsCoordinator;
    private readonly IDiagnosticsReporter _diagnostics;
    private readonly Timer _pollTimer;
    private readonly object _sync = new();
    private readonly object _debounceGate = new();
    private int _refreshInFlight;
    private int _detailedPollCount;
    private int _powerSavingCount;
    private int _coalescedRefreshPending;
    private int _pollTick;
    private string? _lastPresentationFingerprint;
    private CancellationTokenSource? _debounceCts;
    private bool _disposed;

    public RuntimeStateService(
        ServiceManager serviceManager,
        SettingsStore settingsStore,
        PhpConfigWriter phpConfigWriter,
        GlobalProcessManager processManager,
        MailpitManager mailpitManager,
        TestDnsCoordinator testDnsCoordinator,
        IDiagnosticsReporter diagnostics)
    {
        _serviceManager = serviceManager;
        _settingsStore = settingsStore;
        _phpConfigWriter = phpConfigWriter;
        _processManager = processManager;
        _mailpitManager = mailpitManager;
        _testDnsCoordinator = testDnsCoordinator;
        _diagnostics = diagnostics;
        _pollTimer = new Timer(OnPollTimerTick, null, Timeout.InfiniteTimeSpan, Timeout.InfiniteTimeSpan);

        _serviceManager.LiveStatusChanged += OnServiceLiveStatusChanged;
        _serviceManager.PhpListenersChanged += OnUnderlyingStateChanged;
        _processManager.Changed += OnUnderlyingStateChanged;
        _mailpitManager.StatusChanged += OnUnderlyingStateChanged;
        _testDnsCoordinator.StatusChanged += OnUnderlyingStateChanged;
    }

    public event EventHandler? StateUpdated;

    public RuntimeStateSnapshot? LatestSnapshot { get; private set; }

    internal object SnapshotSync => _sync;

    public DateTimeOffset? LastSuccessfulRefreshAt { get; private set; }

    public void StopPolling()
    {
        _pollTimer.Change(Timeout.InfiniteTimeSpan, Timeout.InfiniteTimeSpan);
        lock (_debounceGate)
        {
            _debounceCts?.Cancel();
            _debounceCts?.Dispose();
            _debounceCts = null;
        }
    }

    public void StartBackgroundPolling()
    {
        ApplyPollingInterval(detailed: false);
        _ = Task.Run(async () =>
        {
            try
            {
                await RequestRefresh("StartBackgroundPolling").ConfigureAwait(false);
            }
            catch
            {
                // Best-effort; polling timer will retry.
            }
        });
    }

    public void SetDetailedPolling(bool enabled)
    {
        if (enabled)
        {
            if (Interlocked.Increment(ref _detailedPollCount) == 1)
            {
                ApplyPollingInterval(detailed: true);
            }

            return;
        }

        if (Interlocked.Decrement(ref _detailedPollCount) <= 0)
        {
            Interlocked.Exchange(ref _detailedPollCount, 0);
            ReapplyPollingInterval();
        }
    }

    public void SetPowerSavingMode(bool enabled)
    {
        if (enabled)
        {
            if (Interlocked.Increment(ref _powerSavingCount) == 1)
            {
                ReapplyPollingInterval();
            }

            return;
        }

        if (Interlocked.Decrement(ref _powerSavingCount) <= 0)
        {
            Interlocked.Exchange(ref _powerSavingCount, 0);
            ReapplyPollingInterval();
        }
    }

    private void ReapplyPollingInterval()
    {
        if (Volatile.Read(ref _powerSavingCount) > 0)
        {
            _pollTimer.Change(TimeSpan.FromSeconds(10), TimeSpan.FromSeconds(60));
            return;
        }

        var detailed = Volatile.Read(ref _detailedPollCount) > 0;
        var initialDelay = detailed ? TimeSpan.FromSeconds(1) : TimeSpan.FromSeconds(5);
        var period = detailed ? TimeSpan.FromSeconds(5) : TimeSpan.FromSeconds(30);
        _pollTimer.Change(initialDelay, period);
    }

    private void ApplyPollingInterval(bool detailed)
    {
        if (Volatile.Read(ref _powerSavingCount) > 0)
        {
            ReapplyPollingInterval();
            return;
        }

        var initialDelay = detailed ? TimeSpan.FromSeconds(1) : TimeSpan.FromSeconds(5);
        var period = detailed ? TimeSpan.FromSeconds(5) : TimeSpan.FromSeconds(30);
        _pollTimer.Change(initialDelay, period);
    }

    public Task RefreshAsync() => RequestRefresh("RefreshAsync");

    public bool IsManagedPhpListenerRunning(string versionId)
    {
        lock (_sync)
        {
            return LatestSnapshot?.PhpListeners.FirstOrDefault(listener =>
                string.Equals(listener.VersionId, versionId, StringComparison.OrdinalIgnoreCase)) is
                { IsRunning: true };
        }
    }

    public ServiceInfo? TryGetService(string serviceId)
    {
        lock (_sync)
        {
            return LatestSnapshot?.Services.FirstOrDefault(service =>
                string.Equals(service.Id, serviceId, StringComparison.OrdinalIgnoreCase));
        }
    }

    public RuntimeProcessState? TryGetProcess(string processId)
    {
        lock (_sync)
        {
            return LatestSnapshot?.Processes.FirstOrDefault(process =>
                string.Equals(process.Id, processId, StringComparison.OrdinalIgnoreCase));
        }
    }

    private void OnServiceLiveStatusChanged(object? sender, ServiceInfo e)
    {
        if (ApplicationShutdownState.IsClosing)
        {
            return;
        }

        if (e.Status == ServiceStatus.Starting)
        {
            return;
        }

        if (e.Status is ServiceStatus.Stopped or ServiceStatus.Running or ServiceStatus.Error)
        {
            if (TryPatchServiceInSnapshot(e))
            {
                StateUpdated?.Invoke(this, EventArgs.Empty);
            }

            _ = RequestRefresh($"LiveStatusChanged:{e.Id}");
            return;
        }

        ScheduleDebouncedRefresh($"LiveStatusChanged:{e.Id}");
    }

    private bool TryPatchServiceInSnapshot(ServiceInfo updated)
    {
        lock (_sync)
        {
            if (LatestSnapshot is null)
            {
                return false;
            }

            var services = LatestSnapshot.Services.ToList();
            var index = services.FindIndex(service =>
                string.Equals(service.Id, updated.Id, StringComparison.OrdinalIgnoreCase));
            if (index < 0)
            {
                return false;
            }

            var previous = services[index];
            if (previous.Status == updated.Status
                && previous.PortOpen == updated.PortOpen
                && previous.Pid == updated.Pid
                && string.Equals(previous.Message, updated.Message, StringComparison.Ordinal))
            {
                return false;
            }

            services[index] = updated;
            var patched = new RuntimeStateSnapshot
            {
                RefreshedAt = DateTimeOffset.UtcNow,
                Services = services,
                PhpListeners = LatestSnapshot.PhpListeners,
                Processes = LatestSnapshot.Processes,
                Mailpit = LatestSnapshot.Mailpit,
                TestDns = LatestSnapshot.TestDns
            };
            var fingerprint = RuntimeStateSnapshotFingerprint.Compute(patched);
            var presentationChanged = !string.Equals(
                fingerprint,
                _lastPresentationFingerprint,
                StringComparison.Ordinal);
            LatestSnapshot = patched;
            if (presentationChanged)
            {
                _lastPresentationFingerprint = fingerprint;
            }

            return presentationChanged;
        }
    }

    private void OnUnderlyingStateChanged(object? sender, EventArgs e)
    {
        if (ApplicationShutdownState.IsClosing)
        {
            return;
        }

        ScheduleDebouncedRefresh(sender?.GetType().Name ?? "UnderlyingStateChanged");
    }

    private void OnPollTimerTick(object? state)
    {
        if (ApplicationShutdownState.ShutdownRequested || ApplicationShutdownState.IsShuttingDown)
        {
            return;
        }

        RequestRefresh("PollTimer");
    }

    private void ScheduleDebouncedRefresh(string trigger)
    {
        if (ApplicationShutdownState.IsClosing)
        {
            return;
        }

        CancellationToken token;
        lock (_debounceGate)
        {
            _debounceCts?.Cancel();
            _debounceCts?.Dispose();
            _debounceCts = new CancellationTokenSource();
            token = _debounceCts.Token;
        }

        _ = Task.Run(async () =>
        {
            try
            {
                await Task.Delay(350, token).ConfigureAwait(false);
                await RefreshInternalAsync(trigger, null).ConfigureAwait(false);
            }
            catch (OperationCanceledException) when (token.IsCancellationRequested)
            {
            }
        }, token);
    }

    private Task RequestRefresh(string trigger, string? serviceId = null)
        => RefreshInternalAsync(trigger, serviceId);

    private async Task RefreshInternalAsync(string trigger, string? serviceId)
    {
        if (ApplicationShutdownState.IsClosing)
        {
            return;
        }

        if (Interlocked.CompareExchange(ref _refreshInFlight, 1, 0) != 0)
        {
            Interlocked.Exchange(ref _coalescedRefreshPending, 1);
            return;
        }

        var watch = Stopwatch.StartNew();
        try
        {
            var snapshot = await BuildSnapshotAsync(trigger).ConfigureAwait(false);
            var elapsedMs = watch.ElapsedMilliseconds;
            DiagnosticsCounters.RecordRuntimeSnapshot(elapsedMs);
            var fingerprint = RuntimeStateSnapshotFingerprint.Compute(snapshot);
            var presentationChanged = false;
            lock (_sync)
            {
                presentationChanged = !string.Equals(
                    fingerprint,
                    _lastPresentationFingerprint,
                    StringComparison.Ordinal);
                LatestSnapshot = snapshot;
                LastSuccessfulRefreshAt = snapshot.RefreshedAt;
                if (presentationChanged)
                {
                    _lastPresentationFingerprint = fingerprint;
                }
            }

            if (presentationChanged && !ApplicationShutdownState.IsClosing)
            {
                StateUpdated?.Invoke(this, EventArgs.Empty);
            }
        }
        catch (Exception ex)
        {
            DateTimeOffset? lastOk = null;
            lock (_sync)
            {
                lastOk = LastSuccessfulRefreshAt;
            }

            var ageSeconds = lastOk.HasValue
                ? (DateTimeOffset.UtcNow - lastOk.Value).TotalSeconds
                : double.NaN;
            var ageText = double.IsNaN(ageSeconds)
                ? "never"
                : $"{ageSeconds:F0}s ago";
            _diagnostics.LogActivity(
                "RuntimeStateService",
                $"Snapshot refresh failed ({trigger}); last successful {ageText}");
            _diagnostics.LogException("RuntimeStateService.Refresh", ex);
        }
        finally
        {
            ProcessPortTools.PurgeExpiredPortCacheEntries();
            Interlocked.Exchange(ref _refreshInFlight, 0);
            if (Interlocked.Exchange(ref _coalescedRefreshPending, 0) == 1)
            {
                _ = RefreshInternalAsync("Coalesced", null);
            }
        }
    }

    private async Task<RuntimeStateSnapshot> BuildSnapshotAsync(string trigger, CancellationToken cancellationToken = default)
    {
        var settings = _settingsStore.Load();
        var useFullProbe = ShouldUseFullProbe(trigger);
        IReadOnlyList<ServiceInfo> services = useFullProbe
            ? await _serviceManager.ListLiveAsync(cancellationToken).ConfigureAwait(false)
            : await _serviceManager.ListLiveQuickAsync(cancellationToken).ConfigureAwait(false);
        var mailpit = await _mailpitManager.GetStatusAsync(cancellationToken).ConfigureAwait(false);
        var testDns = _testDnsCoordinator.GetCachedStatus();
        var phpListeners = BuildPhpListenerStates(settings);
        var processes = _processManager.List()
            .Select(process => new RuntimeProcessState
            {
                Id = process.Id,
                Status = process.Status,
                Available = process.Available,
                Pid = process.Pid
            })
            .ToList();

        var snapshot = new RuntimeStateSnapshot
        {
            RefreshedAt = DateTimeOffset.UtcNow,
            Services = services,
            PhpListeners = phpListeners,
            Processes = processes,
            Mailpit = new RuntimeMailpitState
            {
                Enabled = mailpit.Enabled,
                Running = mailpit.Running,
                Pid = mailpit.Pid,
                Installed = mailpit.Installed
            },
            TestDns = new RuntimeTestDnsState
            {
                Enabled = testDns.Enabled,
                Running = testDns.Running,
                NrptActive = testDns.NrptActive,
                Message = testDns.Message
            }
        };

        return snapshot;
    }

    private IReadOnlyList<RuntimePhpListenerState> BuildPhpListenerStates(AppSettings settings)
    {
        var host = string.IsNullOrWhiteSpace(settings.Php.FpmHost) ? "127.0.0.1" : settings.Php.FpmHost;
        var requiredVersionIds = _serviceManager.ResolveRequiredPhpVersionIds();
        var phpVersions = _phpConfigWriter.ListInstalledPhpVersions(settings, requiredVersionIds);
        var activeListeners = PhpCgiRuntime.ActiveListeners();
        var states = new List<RuntimePhpListenerState>();

        foreach (var version in phpVersions)
        {
            if (version.FastCgiPort is not int port || port <= 0)
            {
                continue;
            }

            int? pid = null;
            var isRunning = false;
            if (activeListeners.ContainsKey(version.Id)
                && PhpCgiRuntime.TryGetManagedListenerPid(version.Id, out var managedPid))
            {
                isRunning = true;
                pid = managedPid;
            }

            states.Add(new RuntimePhpListenerState
            {
                VersionId = version.Id,
                Endpoint = $"{host}:{port}",
                Port = port,
                IsRunning = isRunning,
                Pid = pid,
                IsRequired = version.IsRequired,
                StatusText = isRunning
                    ? "Running"
                    : version.IsRequired ? "Stopped" : "Not required"
            });
        }

        return states;
    }

    private bool ShouldUseFullProbe(string trigger)
    {
        if (trigger.StartsWith("LiveStatusChanged:", StringComparison.Ordinal))
        {
            return false;
        }

        if (string.Equals(trigger, "PollTimer", StringComparison.Ordinal))
        {
            return Interlocked.Increment(ref _pollTick) % 3 == 0;
        }

        return trigger is "StartBackgroundPolling" or "RefreshAsync";
    }

    public void Dispose()
    {
        if (_disposed)
        {
            return;
        }

        _disposed = true;
        lock (_debounceGate)
        {
            _debounceCts?.Cancel();
            _debounceCts?.Dispose();
            _debounceCts = null;
        }

        _serviceManager.LiveStatusChanged -= OnServiceLiveStatusChanged;
        _serviceManager.PhpListenersChanged -= OnUnderlyingStateChanged;
        _processManager.Changed -= OnUnderlyingStateChanged;
        _mailpitManager.StatusChanged -= OnUnderlyingStateChanged;
        _testDnsCoordinator.StatusChanged -= OnUnderlyingStateChanged;
        _pollTimer.Dispose();
    }
}
