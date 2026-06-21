using Stackroot.Core.Abstractions;
using Stackroot.Core.Observability;
using Stackroot.Core.Services;
using Stackroot.Core.Supervisor;
using Stackroot.Core.Windows;
using Stackroot.Engine.Runtime;

namespace Stackroot.App.Services;

/// <summary>
/// Header RAM/CPU on a slow timer (not tied to runtime refresh).
/// Full Performance snapshot only when the Performance page requests it.
/// </summary>
public sealed class RuntimeMetricsService : IDisposable
{
    private static readonly TimeSpan HeaderRamRefreshPeriod = TimeSpan.FromSeconds(30);

    private readonly ServiceManager _serviceManager;
    private readonly GlobalProcessManager _globalProcessManager;
    private readonly ProcessSupervisor _processSupervisor;
    private readonly PerformanceSampler _performanceSampler;
    private readonly RuntimeStateService _runtimeState;
    private readonly object _sync = new();
    private int _refreshInFlight;
    private int _headerRefreshInFlight;
    private bool _measureCpu;
    private bool _headerCpuEnabled;
    private TimeSpan _headerCpuRefreshPeriod = TimeSpan.FromSeconds(ShellMetricsDefaults.CpuRefreshSeconds);
    private bool _stopped;
    private bool _disposed;
    private double? _lastTotalCpuPercent;
    private readonly ProcessCpuDeltaTracker _headerCpuTracker = new();
    private System.Threading.Timer? _headerTimer;
    private System.Threading.Timer? _headerRamTimer;
    private string _lastNotifiedSummaryText = string.Empty;
    private int _headerUiNotifyScheduled;

    public RuntimeMetricsService(
        ServiceManager serviceManager,
        GlobalProcessManager globalProcessManager,
        ProcessSupervisor processSupervisor,
        PerformanceSampler performanceSampler,
        RuntimeStateService runtimeState)
    {
        _serviceManager = serviceManager;
        _globalProcessManager = globalProcessManager;
        _processSupervisor = processSupervisor;
        _performanceSampler = performanceSampler;
        _runtimeState = runtimeState;
        _runtimeState.StateUpdated += OnRuntimeStateUpdated;
    }

    public event EventHandler? SnapshotUpdated;

    public PerformanceSnapshot? LatestSnapshot { get; private set; }

    public double TotalMemoryMb { get; private set; }

    public double? TotalCpuPercent { get; private set; }

    public string SummaryText { get; private set; } = "-";

    public void StopPolling()
    {
        _stopped = true;
        StopHeaderTimers();
    }

    public void StartBackgroundPolling()
    {
        _stopped = false;
        if (_headerCpuEnabled)
        {
            StartHeaderCpuTimer(forceRestart: false);
            StartHeaderRamTimer();
        }
        else if (_measureCpu)
        {
            _ = RefreshFullSnapshotAsync(measureCpu: true);
        }
    }

    public void SetDetailedPolling(bool enabled)
    {
        _measureCpu = enabled;
        if (enabled)
        {
            StopHeaderTimers();
            _ = RefreshFullSnapshotAsync(measureCpu: true);
        }
        else if (_headerCpuEnabled)
        {
            StartHeaderCpuTimer(forceRestart: false);
            StartHeaderRamTimer();
        }
    }

    /// <summary>
    /// Header CPU via delta sampling on a slow timer — never on UI thread, never on runtime poll cadence.
    /// </summary>
    public void ConfigureHeaderMetrics(bool enabled, int cpuRefreshSeconds)
    {
        var period = TimeSpan.FromSeconds(ShellMetricsDefaults.ClampCpuRefreshSeconds(cpuRefreshSeconds));
        var periodChanged = _headerCpuRefreshPeriod != period;
        _headerCpuRefreshPeriod = period;

        if (!enabled)
        {
            if (_headerCpuEnabled)
            {
                _headerCpuEnabled = false;
                StopHeaderTimers();
            }

            return;
        }

        var wasEnabled = _headerCpuEnabled;
        _headerCpuEnabled = true;

        if (_measureCpu)
        {
            return;
        }

        lock (_sync)
        {
            SummaryText = FormatHeaderSummary(TotalMemoryMb, _lastTotalCpuPercent);
        }

        StartHeaderCpuTimer(forceRestart: periodChanged && wasEnabled);
        StartHeaderRamTimer();

        if (!wasEnabled)
        {
            _ = Task.Factory.StartNew(
                PrimeHeaderCpuBaseline,
                CancellationToken.None,
                TaskCreationOptions.LongRunning,
                TaskScheduler.Default);
            _ = RefreshHeaderRamAsync();
        }
    }

    public Task RefreshAsync(bool? measureCpu = null) => RefreshFullSnapshotAsync(measureCpu ?? _measureCpu);

    private void OnRuntimeStateUpdated(object? sender, EventArgs e)
    {
        if (_stopped
            || ApplicationShutdownState.ShutdownRequested
            || ApplicationShutdownState.IsShuttingDown)
        {
            return;
        }

        // Header metrics are timer-driven — do not piggyback on runtime refresh (avoids scroll jank).
        if (_measureCpu)
        {
            _ = RefreshFullSnapshotAsync(measureCpu: true);
        }
    }

    private void StartHeaderCpuTimer(bool forceRestart)
    {
        if (_stopped || !_headerCpuEnabled || _measureCpu)
        {
            return;
        }

        if (_headerTimer is not null && !forceRestart)
        {
            return;
        }

        _headerTimer?.Dispose();
        _headerTimer = null;

        var initialDelay = TimeSpan.FromSeconds(
            Math.Min(6, Math.Max(1, _headerCpuRefreshPeriod.TotalSeconds / 2)));
        _headerTimer = new System.Threading.Timer(
            static state => ((RuntimeMetricsService)state!).HeaderCpuTimerTick(),
            this,
            initialDelay,
            _headerCpuRefreshPeriod);
    }

    private void StartHeaderRamTimer()
    {
        if (_stopped || !_headerCpuEnabled || _measureCpu)
        {
            return;
        }

        _headerRamTimer ??= new System.Threading.Timer(
            static state => ((RuntimeMetricsService)state!).HeaderRamTimerTick(),
            this,
            TimeSpan.FromSeconds(2),
            HeaderRamRefreshPeriod);
    }

    private void StopHeaderTimers()
    {
        _headerTimer?.Dispose();
        _headerTimer = null;
        _headerRamTimer?.Dispose();
        _headerRamTimer = null;
    }

    private void HeaderCpuTimerTick()
    {
        if (_stopped || !_headerCpuEnabled || _measureCpu)
        {
            return;
        }

        _ = RefreshHeaderCpuAsync();
    }

    private void HeaderRamTimerTick()
    {
        if (_stopped || !_headerCpuEnabled || _measureCpu)
        {
            return;
        }

        _ = RefreshHeaderRamAsync();
    }

    private async Task RefreshHeaderCpuAsync()
    {
        if (Interlocked.CompareExchange(ref _headerRefreshInFlight, 1, 0) != 0)
        {
            return;
        }

        try
        {
            var cpuSampled = await Task.Factory.StartNew(
                SampleHeaderCpuOnly,
                CancellationToken.None,
                TaskCreationOptions.LongRunning,
                TaskScheduler.Default).ConfigureAwait(false);

            string summaryText;
            lock (_sync)
            {
                if (cpuSampled)
                {
                    TotalCpuPercent = _lastTotalCpuPercent;
                }

                summaryText = FormatHeaderSummary(TotalMemoryMb, TotalCpuPercent);
                if (string.Equals(SummaryText, summaryText, StringComparison.Ordinal))
                {
                    return;
                }

                SummaryText = summaryText;
            }

            NotifySnapshotUpdated(summaryText);
        }
        finally
        {
            Interlocked.Exchange(ref _headerRefreshInFlight, 0);
        }
    }

    private async Task RefreshHeaderRamAsync()
    {
        var memoryMb = await Task.Factory.StartNew(
            SampleHeaderRamOnly,
            CancellationToken.None,
            TaskCreationOptions.LongRunning,
            TaskScheduler.Default).ConfigureAwait(false);

        string summaryText;
        lock (_sync)
        {
            TotalMemoryMb = memoryMb;
            summaryText = FormatHeaderSummary(TotalMemoryMb, TotalCpuPercent);
            if (string.Equals(SummaryText, summaryText, StringComparison.Ordinal))
            {
                return;
            }

            SummaryText = summaryText;
        }

        NotifySnapshotUpdated(summaryText);
    }

    private async Task RefreshFullSnapshotAsync(bool measureCpu)
    {
        if (Interlocked.CompareExchange(ref _refreshInFlight, 1, 0) != 0)
        {
            return;
        }

        try
        {
            var snapshot = await Task.Factory.StartNew(
                () => SampleFullSnapshot(measureCpu),
                CancellationToken.None,
                TaskCreationOptions.LongRunning,
                TaskScheduler.Default).ConfigureAwait(false);

            lock (_sync)
            {
                LatestSnapshot = snapshot;
                TotalMemoryMb = Math.Round(
                    snapshot.Items.Sum(item => item.MemoryMb ?? 0),
                    1);

                if (measureCpu)
                {
                    _lastTotalCpuPercent = Math.Round(
                        snapshot.Items.Sum(item => item.CpuPercent ?? 0),
                        1);
                }

                TotalCpuPercent = _lastTotalCpuPercent;
                SummaryText = FormatHeaderSummary(TotalMemoryMb, TotalCpuPercent);
            }

            NotifySnapshotUpdated(SummaryText);
        }
        finally
        {
            Interlocked.Exchange(ref _refreshInFlight, 0);
        }
    }

    private void PrimeHeaderCpuBaseline()
    {
        _headerCpuTracker.Prime(CollectHeaderCpuPidsFromRuntime());
    }

    private bool SampleHeaderCpuOnly()
    {
        var pids = CollectHeaderCpuPidsFromRuntime();
        if (!_headerCpuTracker.TryGetTotalCpuPercent(pids, out var cpuPercent))
        {
            return false;
        }

        lock (_sync)
        {
            _lastTotalCpuPercent = cpuPercent;
        }

        return true;
    }

    private double SampleHeaderRamOnly()
        => Math.Round(ProcessMemoryTools.SumTaskManagerMemoryMb(CollectHeaderCpuPidsFromRuntime()) ?? 0, 1);

    private static string FormatHeaderSummary(double memoryMb, double? cpuPercent)
    {
        var ram = memoryMb > 0 ? $"{memoryMb:F0}" : "-";
        var cpu = cpuPercent is double value ? $"{value:F1}%" : "-%";
        return $"RAM {ram} MB · CPU {cpu}";
    }

    private PerformanceSnapshot SampleFullSnapshot(bool measureCpu)
        => _performanceSampler.SamplePerformance(
            BuildServiceTargets(),
            BuildProcessTargets(),
            BuildPhpListenerTargets(),
            Environment.ProcessId,
            measureCpu);

    private void NotifySnapshotUpdated(string summaryText)
    {
        if (string.Equals(_lastNotifiedSummaryText, summaryText, StringComparison.Ordinal))
        {
            return;
        }

        if (Interlocked.CompareExchange(ref _headerUiNotifyScheduled, 1, 0) != 0)
        {
            return;
        }

        var handler = SnapshotUpdated;
        if (handler is null)
        {
            Interlocked.Exchange(ref _headerUiNotifyScheduled, 0);
            return;
        }

        var dispatcher = System.Windows.Application.Current?.Dispatcher;
        if (dispatcher is null || dispatcher.HasShutdownStarted)
        {
            Interlocked.Exchange(ref _headerUiNotifyScheduled, 0);
            return;
        }

        dispatcher.BeginInvoke(
            () =>
            {
                Interlocked.Exchange(ref _headerUiNotifyScheduled, 0);
                string latest;
                lock (_sync)
                {
                    latest = SummaryText;
                }

                if (string.Equals(_lastNotifiedSummaryText, latest, StringComparison.Ordinal))
                {
                    return;
                }

                _lastNotifiedSummaryText = latest;
                try
                {
                    handler(this, EventArgs.Empty);
                }
                catch
                {
                    // Best-effort UI notification.
                }
            },
            System.Windows.Threading.DispatcherPriority.ContextIdle);
    }

    private IReadOnlyList<int> CollectHeaderCpuPidsFromRuntime()
    {
        RuntimeStateSnapshot? snapshot;
        lock (_runtimeState.SnapshotSync)
        {
            snapshot = _runtimeState.LatestSnapshot;
        }

        if (snapshot is not null)
        {
            return CollectHeaderCpuPids(snapshot);
        }

        return CollectHeaderCpuPidsFallback();
    }

    private static IReadOnlyList<int> CollectHeaderCpuPids(RuntimeStateSnapshot snapshot)
    {
        var pids = new HashSet<int> { Environment.ProcessId };

        foreach (var service in snapshot.Services)
        {
            if (service.Pid is > 0)
            {
                pids.Add(service.Pid.Value);
            }
        }

        foreach (var listener in snapshot.PhpListeners)
        {
            if (listener.Pid is > 0)
            {
                pids.Add(listener.Pid.Value);
            }
        }

        if (snapshot.Mailpit?.Pid is int mailpitPid && mailpitPid > 0)
        {
            pids.Add(mailpitPid);
        }

        foreach (var process in snapshot.Processes)
        {
            if (process.Pid is > 0)
            {
                pids.Add(process.Pid.Value);
            }
        }

        return pids.ToList();
    }

    private IReadOnlyList<int> CollectHeaderCpuPidsFallback()
    {
        var pids = new HashSet<int> { Environment.ProcessId };

        foreach (var service in _serviceManager.ListManagedSnapshot())
        {
            if (service.Pid is > 0)
            {
                pids.Add(service.Pid.Value);
            }
        }

        foreach (var listener in _serviceManager.ListManagedPhpListenerPerformanceTargets())
        {
            if (listener.Pid is > 0)
            {
                pids.Add(listener.Pid.Value);
            }
        }

        return pids.ToList();
    }

    private IReadOnlyList<ServicePerformanceTarget> BuildServiceTargets()
        => _serviceManager.ListManagedSnapshot()
            .Select(service => new ServicePerformanceTarget
            {
                Id = service.Id,
                Name = service.Name,
                Status = service.Status.ToString(),
                Pid = service.Pid,
                MemoryPids = _serviceManager.ResolvePerformanceMemoryPids(service.Id)
            })
            .ToList();

    private IReadOnlyList<PhpListenerPerformanceTarget> BuildPhpListenerTargets()
        => _serviceManager.ListManagedPhpListenerPerformanceTargets()
            .Select(listener => new PhpListenerPerformanceTarget
            {
                Id = listener.Id,
                Name = listener.Name,
                Endpoint = listener.Endpoint,
                Status = listener.Status,
                Pid = listener.Pid,
                MemoryPids = listener.MemoryPids
            })
            .ToList();

    private IReadOnlyList<ProcessPerformanceTarget> BuildProcessTargets()
        => BuildProcessTargetMap().Values.ToList();

    private Dictionary<string, ProcessPerformanceTarget> BuildProcessTargetMap()
    {
        var processMap = _globalProcessManager.List()
            .ToDictionary(
                process => process.Id,
                process => new ProcessPerformanceTarget
                {
                    Id = process.Id,
                    Name = process.Name,
                    Status = process.Status.ToString(),
                    Pid = process.Pid,
                    SiteName = process.SiteId,
                    MemoryPids = process.Pid is > 0
                        ? ProcessMemoryTools.CollectManagedProcessMemoryPids(process.Pid.Value)
                        : []
                },
                StringComparer.OrdinalIgnoreCase);

        foreach (var status in _processSupervisor.ListStatuses())
        {
            var memoryPids = status.Pid is > 0
                ? ProcessMemoryTools.CollectManagedProcessMemoryPids(status.Pid.Value)
                : [];
            processMap[status.Scope.ProcessId] = new ProcessPerformanceTarget
            {
                Id = status.Scope.ProcessId,
                Name = status.Label,
                Status = status.Status.ToString(),
                Pid = status.Pid,
                SiteName = status.Scope.SiteId,
                MemoryPids = memoryPids
            };
        }

        return processMap;
    }

    public void Dispose()
    {
        if (_disposed)
        {
            return;
        }

        _disposed = true;
        StopHeaderTimers();
        _runtimeState.StateUpdated -= OnRuntimeStateUpdated;
    }
}
