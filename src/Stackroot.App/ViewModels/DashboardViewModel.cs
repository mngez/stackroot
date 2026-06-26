using System.Collections.ObjectModel;
using System.Diagnostics;
using System.IO;
using System.Windows;
using System.Windows.Threading;
using Microsoft.Extensions.DependencyInjection;
using Stackroot.App.Commands;
using Stackroot.App.Localization;
using Stackroot.App.Helpers;
using Stackroot.App.Scheduling;
using Stackroot.App.Services;
using Stackroot.Engine.Runtime;
using Stackroot.App.Views;
using Stackroot.Core.Abstractions;
using Stackroot.Core.AdminTools;
using Stackroot.Core.Catalog;
using Stackroot.Core.Services;
using Stackroot.Core.Services.Php;
using Stackroot.Core.Settings;
using Stackroot.Core.Supervisor;
using Stackroot.Core.Windows;
namespace Stackroot.App.ViewModels;

public sealed class DashboardViewModel : ViewModelBase
{
    private readonly ServiceManager _serviceManager;
    private readonly SettingsStore _settingsStore;
    private readonly InstallRegistryStore _registryStore;
    private readonly ProcessSupervisor _supervisor;
    private readonly GlobalProcessManager _processManager;
    private readonly PhpMyAdminManager _phpMyAdminManager;
    private readonly PhpRedisAdminManager _phpRedisAdminManager;
    private readonly MailpitManager _mailpitManager;
    private readonly PhpConfigWriter _phpConfigWriter;
    private readonly StackrootPaths _paths;
    private readonly IServiceProvider _services;
    private readonly InstallProgressTracker _installTracker;
    private readonly SessionActivityCoordinator _activityCoordinator;
    private readonly SessionActivityReporter _activity;
    private readonly IDiagnosticsReporter _diagnostics;
    private readonly RuntimeStateService _runtimeState;
    private readonly DashboardShellReadyGate _shellReadyGate;
    private readonly TaskSchedulerService _taskScheduler;
    public HealthDomainChipViewModel StackHealthChip { get; }
    public HealthDomainChipViewModel ProcessesHealthChip { get; }
    public HealthDomainChipViewModel SchedulerHealthChip { get; }
    public IReadOnlyList<HealthDomainChipViewModel> HealthChips { get; }
    private int _shellInitStarted;
    private bool _isRefreshing;
    private bool _isSilentRefreshing;
    private bool _dashboardInitialized;
    private int _lastReportedServiceCount = -1;
    private int _lastRunningCount = -1;
    private int _lastStoppedCount = -1;
    private int _lastQuickLinkCount = -1;
    private bool _lastAnyRunning;
    private bool _lastAnyProcessRunning;
    private int _lastRunningProcessCount = -1;
    private int _lastStoppedProcessCount = -1;
    private int _lastErrorProcessCount = -1;
    private bool _isAllBusy;
    private bool _isProcessBulkBusy;
    private string? _lastProcessStatusSnapshot;
    private string? _errorMessage;
    private int _pendingStartupResync;
    private int _pendingInstallTrackerResync;
    private int _pendingRuntimeApply;
    private int _runtimeApplyScheduled;
    private int _visibilityStaleRefreshDone;
    private int _startupPresentationUpdateScheduled;
    private DateTimeOffset? _lastAppliedSnapshotAt;
    private static readonly TimeSpan StaleSnapshotThreshold = TimeSpan.FromSeconds(45);
    private readonly HashSet<string> _handledCompletedStackPackages = new(StringComparer.OrdinalIgnoreCase);
    private bool _startupIsComplete;
    private DateTimeOffset _lastAuxiliaryWorkAt;
    private DateTimeOffset _lastUptimeDisplayRefreshAt;
    private string? _lastAppliedPresentationFingerprint;
    private EnvironmentHealthLevel _environmentHealth = EnvironmentHealthLevel.Healthy;
    private string _healthLabel = "Web stack OK";
    private string _healthSummary = string.Empty;
    private string _healthBadgeBackground = "#142019";
    private string _healthTextColor = "#8FD6B6";
    private string _healthIndicatorColor = "#4CAE8C";
    private readonly HashSet<string> _keepAliveDownNotified = new(StringComparer.OrdinalIgnoreCase);
    private readonly Dictionary<string, DateTimeOffset> _serviceLastStableRunningAt = new(StringComparer.OrdinalIgnoreCase);
    private static readonly TimeSpan ServiceSnapshotStabilization = TimeSpan.FromSeconds(15);
    private readonly HashSet<string> _supervisionAlertNotified = new(StringComparer.OrdinalIgnoreCase);

    public DashboardViewModel(
        ServiceManager serviceManager,
        SettingsStore settingsStore,
        InstallRegistryStore registryStore,
        ProcessSupervisor supervisor,
        GlobalProcessManager processManager,
        PhpMyAdminManager phpMyAdminManager,
        PhpRedisAdminManager phpRedisAdminManager,
        MailpitManager mailpitManager,
        PhpConfigWriter phpConfigWriter,
        StackrootPaths paths,
        IServiceProvider services,
        InstallProgressTracker installTracker,
        SessionActivityCoordinator activityCoordinator,
        SessionActivityReporter activity,
        IDiagnosticsReporter diagnostics,
        RuntimeStateService runtimeState,
        DashboardShellReadyGate shellReadyGate,
        TaskSchedulerService taskScheduler)
    {
        _serviceManager = serviceManager;
        _settingsStore = settingsStore;
        _registryStore = registryStore;
        _supervisor = supervisor;
        _processManager = processManager;
        _phpMyAdminManager = phpMyAdminManager;
        _phpRedisAdminManager = phpRedisAdminManager;
        _mailpitManager = mailpitManager;
        _phpConfigWriter = phpConfigWriter;
        _paths = paths;
        _services = services;
        _installTracker = installTracker;
        _activityCoordinator = activityCoordinator;
        _activity = activity;
        _diagnostics = diagnostics;
        _runtimeState = runtimeState;
        _shellReadyGate = shellReadyGate;
        _taskScheduler = taskScheduler;
        StackHealthChip = new HealthDomainChipViewModel { DomainName = "Web stack" };
        ProcessesHealthChip = new HealthDomainChipViewModel { DomainName = "Processes" };
        SchedulerHealthChip = new HealthDomainChipViewModel { DomainName = "Scheduler" };
        HealthChips = [StackHealthChip, ProcessesHealthChip, SchedulerHealthChip];
        _installTracker.Changed += (_, _) => OnInstallTrackerChanged();
        _runtimeState.StateUpdated += OnRuntimeStateUpdated;
        _processManager.Changed += OnProcessManagerChanged;
        _serviceManager.LiveStatusChanged += OnServiceLiveStatusChanged;
        _serviceManager.SupervisionAlert += OnServiceSupervisionAlert;
        _services.GetRequiredService<TestDnsCoordinator>().StatusChanged += OnTestDnsStatusChanged;
        _taskScheduler.TaskExecuted += (_, _) => UpdateEnvironmentHealth();
        _taskScheduler.StatusChanged += (_, _) => UpdateEnvironmentHealth();

        RefreshCommand = new RelayCommand(_ => _ = RefreshAsync(resyncStructure: true), _ => !IsRefreshing && !IsAllBusy && !IsProcessBulkBusy);
        StopAllCommand = new RelayCommand(_ => _ = StopAllAsync(), _ => !IsAllBusy && AnyRunning);
        StartOrRestartAllCommand = new RelayCommand(_ => _ = StartOrRestartAllAsync(), _ => !IsAllBusy && HasServices);
        OpenDataFolderCommand = new RelayCommand(_ => OpenDataFolder());
        OpenServicesCommand = new RelayCommand(_ => _services.GetRequiredService<ShellViewModel>().Navigate("services"));
        OpenProcessesCommand = new RelayCommand(_ => _services.GetRequiredService<ShellViewModel>().Navigate("processes"));
        StopAllProcessesCommand = new RelayCommand(_ => StopAllProcesses(), _ => !IsProcessBulkBusy && AnyProcessRunning);
        StartAllProcessesCommand = new RelayCommand(_ => StartAllProcesses(), _ => !IsProcessBulkBusy && HasEnabledProcesses);
        DismissErrorMessageCommand = new RelayCommand(_ => ErrorMessage = null);

        Services = new ObservableCollection<DashboardServiceRowViewModel>();
        PhpListeners = new ObservableCollection<DashboardPhpListenerViewModel>();
        EnabledProcesses = new ObservableCollection<DashboardProcessRowViewModel>();
        QuickLinks = new ObservableCollection<DashboardQuickLinkViewModel>();
    }

    public void BeginLoading()
    {
        if (_startupIsComplete)
        {
            _runtimeState.SetDetailedPolling(enabled: true);
        }

        if (Interlocked.CompareExchange(ref _shellInitStarted, 1, 0) != 0)
        {
            SyncPresentationWhenVisible();
            return;
        }

        StackHealthChip.SetPresentation(
            EnvironmentHealthLevel.Healthy,
            "Auto-starting services…",
            "Starting web stack…");
        MirrorLegacyStackHealth();
        RaisePropertyChanged(nameof(ShowHealthBadge));

        _ = InitializeDashboardShellAsync();
    }

    /// <summary>
    /// Called by App when the entire startup sequence finishes.
    /// Signals that auto-refresh may now perform PHP recovery etc.
    /// </summary>
    public void NotifyStartupCompleted()
    {
        _startupIsComplete = true;
        _runtimeState.SetDetailedPolling(enabled: true);
        _lastAppliedPresentationFingerprint = null;
        SeedExpectRunningForMonitoredServices();
        SeedExpectRunningForTestDns();
        UpdateEnvironmentHealth();
        ScanKeepAliveServiceNotifications();
    }

    public Task RefreshAfterStartupAsync()
    {
        if (IsRefreshing)
        {
            Interlocked.Exchange(ref _pendingStartupResync, 1);
            return Task.CompletedTask;
        }

        return _dashboardInitialized
            ? FinishDeferredStartupPresentationAsync()
            : RefreshAsync(resyncStructure: true);
    }

    private async Task FinishDeferredStartupPresentationAsync()
    {
        // Live service events already updated rows during startup — avoid a redundant full port scan.
        await SyncQuickLinksAsync().ConfigureAwait(false);
        await RunOnUiAsync(() =>
        {
            ApplyFromRuntimeState(_runtimeState.LatestSnapshot);
            NotifyAggregatePropertiesChanged();
        }).ConfigureAwait(false);
    }

    private async Task InitializeDashboardShellAsync()
    {
        try
        {
            var serviceItems = await Task.Run(BuildServiceShellItems).ConfigureAwait(false);
            await RunOnUiAsync(() =>
            {
                ApplyServiceShellItems(serviceItems);
                NotifyStructureChanged();
            }).ConfigureAwait(false);
            await SyncProcessesAsync().ConfigureAwait(false);
            await RunOnUiAsync(NotifyStructureChanged).ConfigureAwait(false);
        }
        finally
        {
            _shellReadyGate.SignalReady();
        }
    }

    private void OnInstallTrackerChanged()
    {
        if (!_dashboardInitialized)
        {
            return;
        }

        var dispatcher = Application.Current?.Dispatcher;
        if (dispatcher is not null && !dispatcher.CheckAccess())
        {
            dispatcher.BeginInvoke(() => _ = OnInstallTrackerChangedAsync());
            return;
        }

        _ = OnInstallTrackerChangedAsync();
    }

    private async Task OnInstallTrackerChangedAsync()
    {
        foreach (var activeItem in _installTracker.Items.Where(item => item.IsActive && IsStackPackage(item.PackageId)))
        {
            _handledCompletedStackPackages.Remove(activeItem.PackageId);
        }

        var completedStackPackages = _installTracker.Items
            .Where(item =>
                item.Phase is InstallPhase.Done or InstallPhase.Error
                && IsStackPackage(item.PackageId)
                && !_handledCompletedStackPackages.Contains(item.PackageId))
            .Select(item => item.PackageId)
            .ToList();

        if (completedStackPackages.Count == 0)
        {
            return;
        }

        if (_isSilentRefreshing || IsRefreshing)
        {
            Interlocked.Exchange(ref _pendingInstallTrackerResync, 1);
            return;
        }

        foreach (var packageId in completedStackPackages)
        {
            _handledCompletedStackPackages.Add(packageId);
        }

        await RefreshAsync(silent: true, resyncStructure: true);
    }

    private static bool IsStackPackage(string packageId)
        => packageId.StartsWith("php-", StringComparison.OrdinalIgnoreCase)
           || packageId.StartsWith("phpmyadmin-", StringComparison.OrdinalIgnoreCase)
           || packageId.StartsWith("phpredisadmin-", StringComparison.OrdinalIgnoreCase);

    public void EndLoading()
    {
        _runtimeState.SetDetailedPolling(enabled: false);
        Interlocked.Exchange(ref _visibilityStaleRefreshDone, 0);
    }

    /// <summary>
    /// Event-first recovery when Dashboard becomes visible — apply cached snapshot, then one silent
    /// refresh if data is older than <see cref="StaleSnapshotThreshold"/> (no periodic timer).
    /// </summary>
    public void SyncPresentationWhenVisible()
    {
        if (ApplicationShutdownState.IsClosing || Services.Count == 0)
        {
            return;
        }

        TryApplyLatestRuntimeState();
        MaybeRequestStaleSnapshotRefreshOnce();
    }

    /// <inheritdoc cref="SyncPresentationWhenVisible"/>
    public void SyncPresentationAfterTrayReturn() => SyncPresentationWhenVisible();

    private void TryApplyLatestRuntimeState()
    {
        if (ApplicationShutdownState.IsClosing)
        {
            return;
        }

        var snapshot = _runtimeState.LatestSnapshot;
        if (snapshot is null)
        {
            return;
        }

        void Apply()
        {
            ApplyFromRuntimeState(snapshot);
            _lastAppliedSnapshotAt = snapshot.RefreshedAt;
            Interlocked.Exchange(ref _pendingRuntimeApply, 0);
        }

        var dispatcher = Application.Current?.Dispatcher;
        if (dispatcher is null || dispatcher.CheckAccess())
        {
            Apply();
            return;
        }

        dispatcher.BeginInvoke(Apply, DispatcherPriority.ContextIdle);
    }

    private void ScheduleRuntimeApply()
    {
        if (ApplicationShutdownState.IsClosing)
        {
            return;
        }

        if (_isSilentRefreshing || IsRefreshing)
        {
            Interlocked.Exchange(ref _pendingRuntimeApply, 1);
            return;
        }

        if (Interlocked.CompareExchange(ref _runtimeApplyScheduled, 1, 0) != 0)
        {
            return;
        }

        var dispatcher = Application.Current?.Dispatcher;
        if (dispatcher is null || dispatcher.HasShutdownStarted)
        {
            Interlocked.Exchange(ref _runtimeApplyScheduled, 0);
            return;
        }

        dispatcher.BeginInvoke(
            () =>
            {
                Interlocked.Exchange(ref _runtimeApplyScheduled, 0);
                if (ApplicationShutdownState.IsClosing)
                {
                    return;
                }

                if (_isSilentRefreshing || IsRefreshing)
                {
                    Interlocked.Exchange(ref _pendingRuntimeApply, 1);
                    return;
                }

                TryApplyLatestRuntimeState();
                MaybeRefreshCachedUptimeDisplays();
                MaybeRunThrottledAuxiliaryWorkAsync();
            },
            DispatcherPriority.ContextIdle);
    }

    private void MaybeRefreshCachedUptimeDisplays()
    {
        if (!_startupIsComplete)
        {
            return;
        }

        var now = DateTimeOffset.UtcNow;
        if (now - _lastUptimeDisplayRefreshAt < TimeSpan.FromSeconds(30))
        {
            return;
        }

        _lastUptimeDisplayRefreshAt = now;
        foreach (var service in Services)
        {
            service.RefreshUptimeDisplay();
        }

        foreach (var listener in PhpListeners)
        {
            listener.RefreshUptimeDisplay();
        }

        foreach (var row in EnabledProcesses)
        {
            row.RefreshUptimeDisplay();
        }
    }

    private void MaybeRunThrottledAuxiliaryWorkAsync()
    {
        if (!_startupIsComplete
            || StackrootShutdownCoordinator.IsShuttingDown
            || ApplicationShutdownState.ShutdownRequested
            || _isSilentRefreshing
            || IsRefreshing)
        {
            return;
        }

        var now = DateTimeOffset.UtcNow;
        if (now - _lastAuxiliaryWorkAt < TimeSpan.FromSeconds(30))
        {
            return;
        }

        _ = RunThrottledAuxiliaryWorkAsync();
    }

    private void MaybeRequestStaleSnapshotRefreshOnce()
    {
        if (Volatile.Read(ref _visibilityStaleRefreshDone) == 1)
        {
            return;
        }

        var snapshot = _runtimeState.LatestSnapshot;
        var snapshotAge = snapshot is null
            ? TimeSpan.MaxValue
            : DateTimeOffset.UtcNow - snapshot.RefreshedAt;
        if (snapshotAge <= StaleSnapshotThreshold)
        {
            return;
        }

        if (Interlocked.CompareExchange(ref _visibilityStaleRefreshDone, 1, 0) != 0)
        {
            return;
        }

        _diagnostics.LogActivity("Dashboard",
            $"Stale snapshot ({snapshotAge.TotalSeconds:F0}s) — one silent refresh");
        _ = RequestStaleSnapshotRefreshAsync();
    }

    private async Task RequestStaleSnapshotRefreshAsync()
    {
        if (ApplicationShutdownState.IsClosing)
        {
            return;
        }

        if (_isSilentRefreshing || IsRefreshing)
        {
            Interlocked.Exchange(ref _pendingRuntimeApply, 1);
            return;
        }

        try
        {
            await RefreshAsync(silent: true).ConfigureAwait(false);
        }
        catch
        {
            // Stale recovery is best-effort.
        }
    }

    public ObservableCollection<DashboardServiceRowViewModel> Services { get; }
    public ObservableCollection<DashboardPhpListenerViewModel> PhpListeners { get; }
    public ObservableCollection<DashboardProcessRowViewModel> EnabledProcesses { get; }
    public ObservableCollection<DashboardQuickLinkViewModel> QuickLinks { get; }

    private enum DashboardShellServiceKind
    {
        Managed,
        Mailpit,
        TestDns
    }

    private sealed record ServiceShellItem(
        string Key,
        ServiceDefinition? Definition,
        DashboardShellServiceKind Kind = DashboardShellServiceKind.Managed);

    public RelayCommand RefreshCommand { get; }
    public RelayCommand StopAllCommand { get; }
    public RelayCommand StartOrRestartAllCommand { get; }
    public RelayCommand OpenDataFolderCommand { get; }
    public RelayCommand OpenServicesCommand { get; }
    public RelayCommand OpenProcessesCommand { get; }
    public RelayCommand StopAllProcessesCommand { get; }
    public RelayCommand StartAllProcessesCommand { get; }
    public RelayCommand DismissErrorMessageCommand { get; }

    public bool HasServices => Services.Count > 0;
    public bool ShowLoadingState => Services.Count == 0 && IsRefreshing;
    public bool ShowEmptyState => _dashboardInitialized && !HasServices && !IsRefreshing;
    public bool AnyRunning => Services.Any(s => s.IsRunning);
    public string StartOrRestartAllLabel => AnyRunning
        ? LocalizationManager.Get("Loc.Common.RestartAll", "Restart all")
        : LocalizationManager.Get("Loc.Common.StartAll", "Start all");

    public int RunningCount => Services.Count(s => s.IsRunning);
    public int StoppedCount => Services.Count(s => !s.IsRunning);

    public bool HasPhpListeners => PhpListeners.Count > 0;
    public bool HasEnabledProcesses => EnabledProcesses.Count > 0;
    public bool AnyProcessRunning => EnabledProcesses.Any(p => p.StatusText is "Running" or "Restarting");
    public int RunningProcessCount => EnabledProcesses.Count(p => p.StatusText is "Running" or "Restarting");
    public int StoppedProcessCount => EnabledProcesses.Count(p => p.StatusText == "Stopped");
    public int ErrorProcessCount => EnabledProcesses.Count(p => p.StatusText == "Error");
    public bool HasProcessErrors => ErrorProcessCount > 0;
    public bool HasQuickLinks => QuickLinks.Count > 0;

    public EnvironmentHealthLevel EnvironmentHealth
    {
        get => _environmentHealth;
        private set => SetProperty(ref _environmentHealth, value);
    }

    public string HealthBadgeText
    {
        get
        {
            if (_environmentHealth == EnvironmentHealthLevel.Healthy)
            {
                return _healthLabel;
            }

            return string.IsNullOrWhiteSpace(_healthSummary) ? _healthLabel : _healthSummary;
        }
    }

    public string HealthLabel
    {
        get => _healthLabel;
        private set => SetProperty(ref _healthLabel, value);
    }

    public string HealthSummary
    {
        get => _healthSummary;
        private set => SetProperty(ref _healthSummary, value);
    }

    public string HealthBadgeBackground
    {
        get => _healthBadgeBackground;
        private set => SetProperty(ref _healthBadgeBackground, value);
    }

    public string HealthTextColor
    {
        get => _healthTextColor;
        private set
        {
            if (SetProperty(ref _healthTextColor, value))
            {
                RaisePropertyChanged(nameof(HealthIndicatorBrush));
                RaisePropertyChanged(nameof(HealthTextBrush));
            }
        }
    }

    public System.Windows.Media.Brush HealthIndicatorBrush => CreateHealthBrush(_healthIndicatorColor);

    public System.Windows.Media.Brush HealthTextBrush => CreateHealthBrush(_healthTextColor);

    public bool ShowHealthBadge => HasServices || _shellInitStarted == 1;

    public string? ErrorMessage
    {
        get => _errorMessage;
        private set
        {
            if (SetProperty(ref _errorMessage, value))
            {
                RaisePropertyChanged(nameof(HasErrorMessage));
            }
        }
    }

    public bool HasErrorMessage => !string.IsNullOrWhiteSpace(ErrorMessage);

    public bool IsRefreshing
    {
        get => _isRefreshing;
        private set
        {
            if (SetProperty(ref _isRefreshing, value))
            {
                RefreshCommand.RaiseCanExecuteChanged();
            }
        }
    }

    public bool IsAllBusy
    {
        get => _isAllBusy;
        private set
        {
            if (SetProperty(ref _isAllBusy, value))
            {
                RefreshCommand.RaiseCanExecuteChanged();
                StopAllCommand.RaiseCanExecuteChanged();
                StartOrRestartAllCommand.RaiseCanExecuteChanged();
            }
        }
    }

    public bool IsProcessBulkBusy
    {
        get => _isProcessBulkBusy;
        private set
        {
            if (SetProperty(ref _isProcessBulkBusy, value))
            {
                RefreshCommand.RaiseCanExecuteChanged();
                StopAllProcessesCommand.RaiseCanExecuteChanged();
                StartAllProcessesCommand.RaiseCanExecuteChanged();
            }
        }
    }

    private void OnRuntimeStateUpdated(object? sender, EventArgs e)
    {
        ScheduleRuntimeApply();
    }

    private async Task RunThrottledAuxiliaryWorkAsync()
    {
        if (!_startupIsComplete
            || StackrootShutdownCoordinator.IsShuttingDown
            || ApplicationShutdownState.ShutdownRequested)
        {
            return;
        }

        if (_isSilentRefreshing || IsRefreshing)
        {
            return;
        }

        var now = DateTimeOffset.UtcNow;
        if (now - _lastAuxiliaryWorkAt < TimeSpan.FromSeconds(30))
        {
            return;
        }

        _lastAuxiliaryWorkAt = now;
        _isSilentRefreshing = true;
        try
        {
            await SyncQuickLinksAsync().ConfigureAwait(true);
        }
        finally
        {
            _isSilentRefreshing = false;
        }
    }

    private void ApplyFromRuntimeState(RuntimeStateSnapshot? snapshot)
    {
        if (snapshot is null || ApplicationShutdownState.IsClosing)
        {
            return;
        }

        var fingerprint = RuntimeStateSnapshotFingerprint.Compute(snapshot);
        if (string.Equals(fingerprint, _lastAppliedPresentationFingerprint, StringComparison.Ordinal))
        {
            return;
        }

        _lastAppliedPresentationFingerprint = fingerprint;

        ApplyServiceStatusesFromSnapshot(snapshot);
        ApplyPhpListenersFromSnapshot(snapshot);
        ApplyProcessStatusesFromSnapshot(snapshot);
        UpdateEnvironmentHealth();
        ScanKeepAliveServiceNotifications();
        NotifyAggregatePropertiesChanged();
        RaisePropertyChanged(nameof(ShowHealthBadge));
        RefreshUptimeFromSnapshot(snapshot);
    }

    private void UpdateEnvironmentHealth()
    {
        UpdateStackHealthChip();
        UpdateProcessesHealthChip();
        UpdateSchedulerHealthChip();
        ApplyKeepAliveRowMessages();
    }

    private void UpdateStackHealthChip()
    {
        var visible = ShowHealthBadge;
        if (!_startupIsComplete)
        {
            var issues = CollectStackHealthIssues();
            if (issues.Any(i => i.Kind == "error"))
            {
                StackHealthChip.SetPresentation(
                    EnvironmentHealthLevel.Critical,
                    BuildHealthSummaryText(issues),
                    "Starting web stack…",
                    visible);
            }
            else if (issues.Count > 0)
            {
                StackHealthChip.SetPresentation(
                    EnvironmentHealthLevel.Degraded,
                    BuildHealthSummaryText(issues),
                    "Starting web stack…",
                    visible);
            }
            else
            {
                StackHealthChip.SetPresentation(
                    EnvironmentHealthLevel.Healthy,
                    "Auto-starting services…",
                    "Starting web stack…",
                    visible);
            }

            MirrorLegacyStackHealth();
            return;
        }

        var healthIssues = CollectStackHealthIssues();
        if (healthIssues.Any(i => i.Kind == "error"))
        {
            StackHealthChip.SetPresentation(
                EnvironmentHealthLevel.Critical,
                BuildHealthSummaryText(healthIssues),
                "Web stack OK",
                visible);
        }
        else if (healthIssues.Count > 0)
        {
            StackHealthChip.SetPresentation(
                EnvironmentHealthLevel.Degraded,
                BuildHealthSummaryText(healthIssues),
                "Web stack OK",
                visible);
        }
        else
        {
            StackHealthChip.SetPresentation(
                EnvironmentHealthLevel.Healthy,
                "Web stack OK",
                "Web stack OK",
                visible);
        }

        MirrorLegacyStackHealth();
    }

    private void UpdateProcessesHealthChip()
    {
        if (!_startupIsComplete)
        {
            ProcessesHealthChip.SetPresentation(
                EnvironmentHealthLevel.Healthy,
                string.Empty,
                string.Empty,
                visible: false);
            return;
        }

        var issues = CollectProcessHealthIssues();
        if (issues.Any(i => i.Kind == "error"))
        {
            ProcessesHealthChip.SetPresentation(
                EnvironmentHealthLevel.Critical,
                BuildProcessSummaryText(issues),
                "Processes OK");
        }
        else if (issues.Count > 0)
        {
            ProcessesHealthChip.SetPresentation(
                EnvironmentHealthLevel.Degraded,
                BuildProcessSummaryText(issues),
                "Processes OK");
        }
        else if (EnabledProcesses.Count == 0)
        {
            ProcessesHealthChip.SetPresentation(
                EnvironmentHealthLevel.Healthy,
                "No enabled processes",
                "No processes");
        }
        else
        {
            var waiting = CountProcessesWaitingForRestart();
            var healthyText = waiting > 0
                ? $"Processes OK ({waiting} waiting)"
                : "Processes OK";
            ProcessesHealthChip.SetPresentation(
                EnvironmentHealthLevel.Healthy,
                waiting > 0 ? $"{waiting} process(es) idle between scheduled runs" : "All enabled processes running",
                healthyText);
        }
    }

    private void UpdateSchedulerHealthChip()
    {
        if (!_startupIsComplete)
        {
            SchedulerHealthChip.SetPresentation(
                EnvironmentHealthLevel.Healthy,
                string.Empty,
                string.Empty,
                visible: false);
            return;
        }

        if (!_taskScheduler.IsStarted)
        {
            SchedulerHealthChip.SetPresentation(
                EnvironmentHealthLevel.Critical,
                "Scheduler is not running",
                "Scheduler stopped");
            return;
        }

        var failedTasks = _taskScheduler.List()
            .Where(task => task.IsEnabled && !string.IsNullOrWhiteSpace(task.LastError))
            .ToList();
        if (failedTasks.Count > 0)
        {
            var summary = failedTasks.Count == 1
                ? $"{failedTasks[0].Label} failed"
                : $"{failedTasks.Count} scheduled tasks failed";
            SchedulerHealthChip.SetPresentation(
                EnvironmentHealthLevel.Degraded,
                summary,
                "Scheduler active");
            return;
        }

        var enabledCount = _taskScheduler.List().Count(task => task.IsEnabled);
        SchedulerHealthChip.SetPresentation(
            EnvironmentHealthLevel.Healthy,
            enabledCount == 0 ? "No enabled scheduled tasks" : "Scheduler running",
            "Scheduler active");
    }

    private void MirrorLegacyStackHealth()
    {
        EnvironmentHealth = StackHealthChip.Level;
        HealthSummary = StackHealthChip.Summary;
        HealthLabel = StackHealthChip.BadgeText;
        HealthBadgeBackground = StackHealthChip.BadgeBackground;
        HealthTextColor = StackHealthChip.TextColor;
        _healthIndicatorColor = StackHealthChip.Level switch
        {
            EnvironmentHealthLevel.Critical => "#E88A92",
            EnvironmentHealthLevel.Degraded => "#E9BD5B",
            _ => "#4CAE8C"
        };
        RaisePropertyChanged(nameof(HealthIndicatorBrush));
        RaisePropertyChanged(nameof(HealthBadgeText));
    }

    private sealed record HealthIssue(string Label, int RetryCount, string Kind, bool IsSupervised);

    private List<HealthIssue> CollectStackHealthIssues()
    {
        var issues = new List<HealthIssue>();
        issues.AddRange(CollectServiceHealthIssues());
        issues.AddRange(CollectRequiredPhpHealthIssues());
        return issues;
    }

    private List<HealthIssue> CollectServiceHealthIssues()
    {
        var issues = new List<HealthIssue>();

        foreach (var service in Services)
        {
            if (!ExpectsToBeUp(service))
            {
                continue;
            }

            var supervised = IsKeepAliveEnabled(service);
            var retries = supervised ? GetSupervisionFailureCount(service.ServiceKey) : 0;

            if (string.Equals(service.ServiceKey, "testdns", StringComparison.OrdinalIgnoreCase))
            {
                if (!_startupIsComplete && IsTestDnsAutoStartExpected()
                    && !(service.IsRunning && string.Equals(service.StatusText, "Running", StringComparison.Ordinal)))
                {
                    issues.Add(new HealthIssue(service.Name, 0, "starting", false));
                }
                else if (service.StatusText == "Error")
                {
                    issues.Add(new HealthIssue(service.Name, 0, "error", false));
                }
                else if (!service.IsRunning && !service.IsBusy && service.StatusText == "Stopped")
                {
                    issues.Add(new HealthIssue(service.Name, 0, "stopped", false));
                }

                continue;
            }

            if (service.StatusText == "Error")
            {
                issues.Add(new HealthIssue(service.Name, retries, "error", supervised));
                continue;
            }

            if (!service.IsRunning && !service.IsBusy && service.StatusText == "Stopped")
            {
                issues.Add(new HealthIssue(service.Name, retries, "stopped", supervised));
                continue;
            }

            if (string.Equals(service.StatusText, "Starting", StringComparison.Ordinal))
            {
                issues.Add(new HealthIssue(service.Name, retries, "starting", supervised));
            }
            else if (string.Equals(service.StatusText, "Restarting", StringComparison.Ordinal))
            {
                issues.Add(new HealthIssue(service.Name, retries, "restarting", supervised));
            }
        }

        return issues;
    }

    private IEnumerable<HealthIssue> CollectRequiredPhpHealthIssues()
    {
        foreach (var listener in PhpListeners.Where(listener => listener.IsRequired))
        {
            var label = FormatPhpListenerLabel(listener.VersionId);

            if (string.Equals(listener.StatusText, "Error", StringComparison.Ordinal))
            {
                yield return new HealthIssue(label, 0, "error", false);
                continue;
            }

            if (!listener.IsRunning
                && !listener.IsRestarting
                && string.Equals(listener.StatusText, "Stopped", StringComparison.Ordinal))
            {
                yield return new HealthIssue(label, 0, "stopped", false);
                continue;
            }

            if (listener.IsRestarting
                || listener.StatusText is "Recovering" or "Starting")
            {
                yield return new HealthIssue(label, 0, "starting", false);
            }
        }
    }

    private List<HealthIssue> CollectProcessHealthIssues()
    {
        var issues = new List<HealthIssue>();

        foreach (var process in EnabledProcesses)
        {
            if (!ShouldReportProcessHealthIssue(process))
            {
                continue;
            }

            if (string.Equals(process.StatusText, "Error", StringComparison.Ordinal))
            {
                issues.Add(new HealthIssue(process.Name, 0, "error", false));
                continue;
            }

            if (string.Equals(process.StatusText, "Stopped", StringComparison.Ordinal))
            {
                issues.Add(new HealthIssue(process.Name, 0, "stopped", false));
            }
        }

        return issues;
    }

    private static bool ShouldReportProcessHealthIssue(DashboardProcessRowViewModel process)
    {
        if (string.Equals(process.StatusText, "Error", StringComparison.Ordinal))
        {
            return true;
        }

        if (process.IsBusy
            || string.Equals(process.StatusText, "Restarting", StringComparison.Ordinal))
        {
            // Supervisor restart wait — explicit delay or runtime default (2s, then backoff).
            return false;
        }

        if (string.Equals(process.StatusText, "Stopped", StringComparison.Ordinal))
        {
            // Explicit inter-run delay (e.g. queue worker every N minutes).
            if (process.HasExplicitRestartDelay)
            {
                return false;
            }

            // Supervised on-demand workers (typical queue worker: no autostart, supervisor still restarts on exit).
            if (process.IsSupervised && !process.AutoStart)
            {
                return false;
            }

            // Always-on daemons: flag only when auto-start expects them up.
            return process.AutoStart;
        }

        return false;
    }

    private int CountProcessesWaitingForRestart()
    {
        return EnabledProcesses.Count(process =>
            string.Equals(process.StatusText, "Restarting", StringComparison.Ordinal)
            || (process.HasExplicitRestartDelay
                && string.Equals(process.StatusText, "Stopped", StringComparison.Ordinal)));
    }

    private static string FormatPhpListenerLabel(string versionId)
    {
        if (string.IsNullOrWhiteSpace(versionId))
        {
            return "PHP listener";
        }

        return versionId.StartsWith("php-", StringComparison.OrdinalIgnoreCase)
            ? "PHP " + versionId["php-".Length..]
            : "PHP " + versionId;
    }

    private static string BuildHealthSummaryText(IReadOnlyList<HealthIssue> issues)
    {
        if (issues.Count == 0)
        {
            return "Web stack OK";
        }

        return string.Join(" · ", issues.Select(FormatHealthIssue));
    }

    private static string BuildProcessSummaryText(IReadOnlyList<HealthIssue> issues)
    {
        if (issues.Count == 0)
        {
            return "Processes OK";
        }

        return string.Join(" · ", issues.Select(FormatProcessHealthIssue));
    }

    private static string FormatProcessHealthIssue(HealthIssue issue) => issue.Kind switch
    {
        "error" => $"{issue.Label} error",
        "stopped" => $"{issue.Label} stopped",
        _ => issue.Label
    };

    private static string FormatHealthIssue(HealthIssue issue) => issue.Kind switch
    {
        "error" when issue.IsSupervised && issue.RetryCount > 0
            => $"{issue.Label} error (auto-restart attempt {issue.RetryCount})",
        "error" => $"{issue.Label} error",
        "stopped" when issue.IsSupervised && issue.RetryCount > 0
            => $"{issue.Label} stopped (auto-restart attempt {issue.RetryCount})",
        "stopped" when issue.IsSupervised
            => $"{issue.Label} stopped, auto-restart pending",
        "stopped" => $"{issue.Label} stopped",
        "starting" => $"{issue.Label} starting",
        "restarting" when issue.IsSupervised && issue.RetryCount > 0
            => $"{issue.Label} restarting (auto-restart attempt {issue.RetryCount})",
        "restarting" => $"{issue.Label} restarting",
        _ => issue.Label
    };

    private bool IsKeepAliveEnabled(DashboardServiceRowViewModel service)
    {
        var settings = _settingsStore.Load();
        if (string.Equals(service.ServiceKey, "mailpit", StringComparison.OrdinalIgnoreCase))
        {
            return settings.Mailpit.Supervise
                && settings.Mailpit.Enabled
                && _registryStore.IsInstalled(settings.Mailpit.PackageId);
        }

        if (!Enum.TryParse<ServiceId>(service.ServiceKey, ignoreCase: true, out var serviceId))
        {
            return false;
        }

        return settings.Services.TryGetValue(serviceId, out var serviceSettings)
            && serviceSettings.Supervise
            && serviceSettings.Enabled;
    }

    private int GetSupervisionFailureCount(string serviceKey)
    {
        if (string.Equals(serviceKey, "mailpit", StringComparison.OrdinalIgnoreCase))
        {
            return _serviceManager.GetSupervisionFailureCount(ServiceId.Mailpit);
        }

        return Enum.TryParse<ServiceId>(serviceKey, ignoreCase: true, out var serviceId)
            ? _serviceManager.GetSupervisionFailureCount(serviceId)
            : 0;
    }

    private void ApplyKeepAliveRowMessages()
    {
        foreach (var service in Services)
        {
            ApplyKeepAliveRowMessage(service);
        }
    }

    private void ApplyKeepAliveRowMessage(DashboardServiceRowViewModel service)
    {
        if (!IsKeepAliveEnabled(service))
        {
            ClearStaleKeepAliveRowMessage(service);
            return;
        }

        if (!ExpectsToBeUp(service))
        {
            return;
        }

        var failures = GetSupervisionFailureCount(service.ServiceKey);
        if (service.IsRunning && failures == 0)
        {
            ClearStaleKeepAliveRowMessage(service);
            return;
        }

        if (service.StatusText is "Stopped" or "Error")
        {
            if (IsPortConflictRowMessage(service.Message))
            {
                var conflictBase = ManagedServiceStatusPolicy.StripKeepAliveDecorations(service.Message);
                service.Message = IsKeepAliveEnabled(service)
                    ? ManagedServiceStatusPolicy.FormatPortConflictKeepAliveMessage(conflictBase)
                    : conflictBase;
                return;
            }

            var baseMessage = ManagedServiceStatusPolicy.StripKeepAliveDecorations(service.Message);
            if (!ShouldShowKeepAliveRecoveryMessage(service, failures))
            {
                service.Message = string.IsNullOrWhiteSpace(baseMessage) ? null : baseMessage;
                return;
            }

            service.Message = ManagedServiceStatusPolicy.FormatKeepAliveRecoveryMessage(baseMessage, failures);
            return;
        }

        if (service.IsBusy || service.StatusText is "Starting" or "Restarting")
        {
            if (service.ShowStartupProgress
                && !string.IsNullOrWhiteSpace(service.Message)
                && !ManagedServiceStatusPolicy.IsKeepAliveRecoverySuffix(service.Message))
            {
                return;
            }

            service.Message = failures > 0
                ? $"Auto-restart attempt {failures}"
                : ManagedServiceStatusPolicy.KeepAliveStartingHint;
        }
    }

    private bool ShouldShowKeepAliveRecoveryMessage(DashboardServiceRowViewModel service, int failures)
    {
        if (failures > 0 || IsSupervisionRecoveryInFlight(service))
        {
            return true;
        }

        if (ManagedServiceStatusPolicy.IsStopFailedMessage(service.Message))
        {
            return false;
        }

        if (!Enum.TryParse<ServiceId>(service.ServiceKey, ignoreCase: true, out var serviceId))
        {
            return false;
        }

        return _serviceManager.IsSupervisionEligible(serviceId);
    }

    private bool IsSupervisionRecoveryInFlight(DashboardServiceRowViewModel service)
    {
        if (!Enum.TryParse<ServiceId>(service.ServiceKey, ignoreCase: true, out var serviceId))
        {
            return false;
        }

        return _serviceManager.IsSupervisionRecoveryInFlight(serviceId);
    }

    private static bool IsAutoRestartRowMessage(string message)
        => ManagedServiceStatusPolicy.IsKeepAliveRecoverySuffix(message)
           || message.StartsWith("Keep-alive", StringComparison.OrdinalIgnoreCase);

    private static bool IsPortConflictRowMessage(string? message)
        => ManagedServiceStatusPolicy.IsPortConflictMessage(message);

    private static void ClearStaleKeepAliveRowMessage(DashboardServiceRowViewModel service)
    {
        if (service.Message is not null && IsAutoRestartRowMessage(service.Message))
        {
            service.Message = null;
        }
    }

    /// <summary>
    /// Enabled services expected to stay up: started manually/auto, or keep-alive with auto-start after boot.
    /// </summary>
    private bool ExpectsToBeUp(DashboardServiceRowViewModel service)
    {
        if (string.Equals(service.ServiceKey, "testdns", StringComparison.OrdinalIgnoreCase))
        {
            return _settingsStore.Load().TestDns.Enabled
                && !_serviceManager.IsUserStoppedIntent(service.ServiceKey);
        }

        if (service.ExpectRunning)
        {
            return true;
        }

        if (_serviceManager.IsUserStoppedIntent(service.ServiceKey))
        {
            return false;
        }

        if (!service.IsSupervised || !_startupIsComplete)
        {
            return false;
        }

        return IsAutoStartEnabled(service.ServiceKey);
    }

    private bool IsAutoStartEnabled(string serviceKey)
    {
        var settings = _settingsStore.Load();
        if (string.Equals(serviceKey, "testdns", StringComparison.OrdinalIgnoreCase))
        {
            return settings.TestDns.Enabled && settings.TestDns.AutoStart;
        }

        if (string.Equals(serviceKey, "mailpit", StringComparison.OrdinalIgnoreCase))
        {
            return settings.Mailpit.Enabled && settings.Mailpit.AutoStart;
        }

        if (!Enum.TryParse<ServiceId>(serviceKey, ignoreCase: true, out var serviceId))
        {
            return false;
        }

        return settings.Services.TryGetValue(serviceId, out var serviceSettings)
            && serviceSettings.Enabled
            && serviceSettings.AutoStart;
    }

    private void SeedExpectRunningForMonitoredServices()
    {
        var settings = _settingsStore.Load();
        foreach (var service in Services)
        {
            if (string.Equals(service.ServiceKey, "testdns", StringComparison.OrdinalIgnoreCase))
            {
                continue;
            }

            if (service.IsSupervised && IsAutoStartEnabled(service.ServiceKey))
            {
                service.NoteStarted();
            }
        }
    }

    private void SeedExpectRunningForTestDns()
    {
        if (!IsTestDnsAutoStartExpected())
        {
            return;
        }

        var service = Services.FirstOrDefault(row =>
            string.Equals(row.ServiceKey, "testdns", StringComparison.OrdinalIgnoreCase));
        service?.NoteStarted();
    }

    private int CountServiceErrors()
        => CollectStackHealthIssues().Count(i => i.Kind == "error");

    private int CountStoppedNeedingAttention()
        => CollectStackHealthIssues().Count(i => i.Kind == "stopped");

    private int CountKeepAliveRecovering()
        => CollectStackHealthIssues().Count(i => i.Kind is "starting" or "restarting");

    private static System.Windows.Media.SolidColorBrush CreateHealthBrush(string hex) =>
        new((System.Windows.Media.Color)System.Windows.Media.ColorConverter.ConvertFromString(hex)!);

    private void ApplyServiceStatusesFromSnapshot(RuntimeStateSnapshot snapshot)
    {
        foreach (var service in Services)
        {
            if (string.Equals(service.ServiceKey, "testdns", StringComparison.OrdinalIgnoreCase))
            {
                if (snapshot.TestDns is { } testDns)
                {
                    ApplyTestDnsRowStatus(service, testDns);
                }

                continue;
            }

            // During startup, service rows follow live-events from ServiceManager auto-start.
            // Coalesced runtime snapshots can finish late and regress Running → Stopped.
            if (!_startupIsComplete)
            {
                continue;
            }

            if (string.Equals(service.ServiceKey, "mailpit", StringComparison.OrdinalIgnoreCase))
            {
                if (snapshot.Mailpit is { } mailpit)
                {
                    ApplyDashboardServiceStatusFromLive(service, new ServiceInfo
                    {
                        Id = "mailpit",
                        PortOpen = mailpit.Enabled && mailpit.Running,
                        Status = mailpit.Enabled && mailpit.Running ? ServiceStatus.Running : ServiceStatus.Stopped,
                        Pid = mailpit.Pid,
                        Message = mailpit.Installed ? null : "Install Mailpit package first"
                    }, "snapshot");
                }

                continue;
            }

            var liveInfo = snapshot.Services.FirstOrDefault(row =>
                string.Equals(row.Id, service.ServiceKey, StringComparison.OrdinalIgnoreCase));
            ApplyDashboardServiceStatusFromLive(service, liveInfo, "snapshot");
        }

        SeedExpectRunningFromSnapshot(snapshot);
    }

    /// <summary>
    /// Safety net: mark rows as expected-up when snapshot shows an open port for supervised/auto-start services.
    /// Does not affect keep-alive — UI <see cref="DashboardServiceRowViewModel.ExpectRunning"/> only.
    /// </summary>
    private void SeedExpectRunningFromSnapshot(RuntimeStateSnapshot snapshot)
    {
        if (!_startupIsComplete)
        {
            return;
        }

        var settings = _settingsStore.Load();
        foreach (var service in Services)
        {
            if (service.ExpectRunning)
            {
                continue;
            }

            if (_serviceManager.IsUserStoppedIntent(service.ServiceKey))
            {
                continue;
            }

            if (string.Equals(service.ServiceKey, "testdns", StringComparison.OrdinalIgnoreCase))
            {
                if (snapshot.TestDns is { Enabled: true, Running: true }
                    && settings.TestDns.AutoStart)
                {
                    service.NoteStarted();
                }

                continue;
            }

            if (string.Equals(service.ServiceKey, "mailpit", StringComparison.OrdinalIgnoreCase))
            {
                if (snapshot.Mailpit is { Enabled: true, Running: true }
                    && settings.Mailpit.Enabled
                    && (settings.Mailpit.AutoStart || settings.Mailpit.Supervise))
                {
                    service.NoteStarted();
                }

                continue;
            }

            var liveInfo = snapshot.Services.FirstOrDefault(row =>
                string.Equals(row.Id, service.ServiceKey, StringComparison.OrdinalIgnoreCase));
            if (liveInfo?.PortOpen != true)
            {
                continue;
            }

            if (ShouldMarkExpectRunningFromSettings(service.ServiceKey, settings))
            {
                service.NoteStarted();
            }
        }
    }

    private static bool ShouldMarkExpectRunningFromSettings(string serviceKey, AppSettings settings)
    {
        if (!Enum.TryParse<ServiceId>(serviceKey, ignoreCase: true, out var serviceId))
        {
            return false;
        }

        if (!settings.Services.TryGetValue(serviceId, out var serviceSettings)
            || !serviceSettings.Enabled)
        {
            return false;
        }

        return serviceSettings.AutoStart || serviceSettings.Supervise;
    }

    private void ApplyPhpListenersFromSnapshot(RuntimeStateSnapshot snapshot)
    {
        var settings = _settingsStore.Load();
        var requiredVersionIds = ResolveRequiredPhpVersionIds();
        var phpVersions = _phpConfigWriter.ListInstalledPhpVersions(settings, requiredVersionIds)
            .ToDictionary(version => version.Id, StringComparer.OrdinalIgnoreCase);
        var visibleStates = snapshot.PhpListeners
            .Where(state => phpVersions.ContainsKey(state.VersionId))
            .Where(state => state.IsRequired || state.IsRunning)
            .ToList();
        var stoppedRequired = new List<string>();

        foreach (var stale in PhpListeners.Where(row =>
                     visibleStates.All(state => !string.Equals(state.VersionId, row.VersionId, StringComparison.OrdinalIgnoreCase))).ToList())
        {
            PhpListeners.Remove(stale);
        }

        foreach (var state in visibleStates)
        {
            if (!phpVersions.TryGetValue(state.VersionId, out var version))
            {
                continue;
            }

            var existing = PhpListeners.FirstOrDefault(row =>
                string.Equals(row.VersionId, state.VersionId, StringComparison.OrdinalIgnoreCase));
            var wasRunning = existing?.IsRunning == true;
            var isRunning = state.IsRunning;
            var statusText = ResolvePhpListenerStatusText(state, existing, isRunning);

            UpsertPhpListenerRow(existing, state, version, isRunning, statusText);

            if (state.IsRequired && !isRunning && wasRunning)
            {
                stoppedRequired.Add(state.VersionId);
            }
        }

        RaisePropertyChanged(nameof(HasPhpListeners));

        if (stoppedRequired.Count > 0 && _startupIsComplete)
        {
            foreach (var versionId in stoppedRequired)
            {
                var row = PhpListeners.FirstOrDefault(r =>
                    string.Equals(r.VersionId, versionId, StringComparison.OrdinalIgnoreCase));
                if (row is not null)
                {
                    row.IsRunning = false;
                    row.StatusText = "Recovering";
                    row.StatusColor = ResolvePhpListenerStatusColor(false, "Recovering");
                }
            }

            RequestRequiredPhpRecovery();
        }
    }

    private static string ResolvePhpListenerStatusText(
        RuntimePhpListenerState state,
        DashboardPhpListenerViewModel? existing,
        bool isRunning)
    {
        if (isRunning)
        {
            return state.StatusText;
        }

        if (existing is not null
            && string.Equals(existing.StatusText, "Recovering", StringComparison.OrdinalIgnoreCase))
        {
            return "Recovering";
        }

        return state.IsRequired ? "Stopped" : state.StatusText;
    }

    private static string ResolvePhpListenerStatusColor(bool isRunning, string statusText)
        => isRunning ? "#8FD6B6"
            : statusText is "Recovering" or "Starting" ? "#E9BD5B"
            : statusText == "Error" ? "#EAAAB0"
            : "#91A0B5";

    private void UpsertPhpListenerRow(
        DashboardPhpListenerViewModel? existing,
        RuntimePhpListenerState state,
        PhpVersionInfo version,
        bool isRunning,
        string statusText)
    {
        var logPath = PhpLogPaths.GetCgiStderrLogPath(_paths.LogsRoot, state.VersionId);
        var errorLogPath = PhpLogPaths.GetErrorLogPath(_paths.LogsRoot, state.VersionId);
        var logExists = File.Exists(logPath) || File.Exists(errorLogPath);
        var statusColor = ResolvePhpListenerStatusColor(isRunning, statusText);
        var capturedVersion = state.VersionId;

        if (existing is null)
        {
            var listener = new DashboardPhpListenerViewModel
            {
                VersionId = state.VersionId,
                IsRequired = state.IsRequired,
                Endpoint = state.Endpoint,
                StatusColor = statusColor,
                ShowLogButton = logExists || isRunning,
                OpenLogCommand = new RelayCommand(_ => OpenPhpLog(logPath, errorLogPath), _ => logExists || isRunning),
                StopCommand = new RelayCommand(_ => _ = StopPhpCgiAsync(capturedVersion), _ => isRunning),
                RestartCommand = null!,
                IsRunning = isRunning,
                StatusText = statusText
            };
            listener.RestartCommand = new RelayCommand(
                _ => _ = RestartPhpCgiAsync(capturedVersion),
                _ => version.IsRequired && !listener.IsRestarting);
            ApplyPhpListenerUptime(listener, isRunning);
            PhpListeners.Add(listener);
            return;
        }

        if (!string.Equals(existing.Endpoint, state.Endpoint, StringComparison.Ordinal))
        {
            existing.Endpoint = state.Endpoint;
        }

        if (existing.IsRunning != isRunning
            || existing.StatusText != statusText
            || existing.StatusColor != statusColor)
        {
            if (existing.IsRunning != isRunning || existing.StatusText != statusText)
            {
                _diagnostics.LogActivity(
                    "Dashboard",
                    $"PHP listener status: {state.VersionId} {existing.StatusText} → {statusText}");
            }

            existing.IsRunning = isRunning;
            existing.StatusText = statusText;
            existing.StatusColor = statusColor;
        }

        existing.ShowLogButton = logExists || isRunning;
        existing.StopCommand.RaiseCanExecuteChanged();
        existing.RestartCommand.RaiseCanExecuteChanged();
        ApplyPhpListenerUptime(existing, isRunning);
    }

    private void RequestRequiredPhpRecovery()
    {
        _ = RecoverRequiredPhpAsync();
    }

    private async Task RecoverRequiredPhpAsync()
    {
        try
        {
            await _serviceManager.TryRecoverRequiredPhpAsync(urgent: true).ConfigureAwait(true);
        }
        catch
        {
            // Recovery is best-effort; the next snapshot will reflect the result.
        }
    }

    private void ApplyProcessStatusesFromSnapshot(RuntimeStateSnapshot snapshot)
    {
        var snapshotKey = string.Join('\n', snapshot.Processes.Select(process =>
            $"{process.Id}|{process.Status}|{process.Available}|{process.Pid}"));
        if (string.Equals(snapshotKey, _lastProcessStatusSnapshot, StringComparison.Ordinal))
        {
            return;
        }

        _lastProcessStatusSnapshot = snapshotKey;

        foreach (var process in snapshot.Processes)
        {
            var row = EnabledProcesses.FirstOrDefault(candidate => candidate.Id == process.Id);
            if (row is null)
            {
                continue;
            }

            if (row.StatusText != process.Status.ToString()
                || row.IsRunning != (process.Status is ProcessStatus.Running or ProcessStatus.Restarting))
            {
                _diagnostics.LogActivity("Dashboard",
                    $"Process status: {row.Name} {row.StatusText} → {process.Status}");
            }

            ApplyProcessStatus(row, process.Status, process.Available, process.Pid);
        }
    }

    private void OnProcessManagerChanged(object? sender, EventArgs e)
    {
        Application.Current?.Dispatcher.BeginInvoke(SyncProcesses);
    }

    private async Task RefreshAsync(bool silent = false, bool resyncStructure = false)
    {
        if (silent)
        {
            if (_isSilentRefreshing || IsRefreshing)
            {
                return;
            }

            _isSilentRefreshing = true;
        }
        else if (IsRefreshing)
        {
            return;
        }
        else
        {
            IsRefreshing = true;
        }

        try
        {
            if (resyncStructure || !_dashboardInitialized)
            {
                await SyncServiceRowsAsync();
                await SyncProcessesAsync();
                NotifyStructureChanged();
                await _runtimeState.RefreshAsync();
                await SyncPhpListenersAsync();
                await RunOnUiAsync(() => ApplyFromRuntimeState(_runtimeState.LatestSnapshot)).ConfigureAwait(true);
            }
            else
            {
                await _runtimeState.RefreshAsync();
                await RunOnUiAsync(() => ApplyFromRuntimeState(_runtimeState.LatestSnapshot)).ConfigureAwait(true);
            }

            await SyncQuickLinksAsync();
            var snapshot = _runtimeState.LatestSnapshot;
            if (snapshot is not null)
            {
                await RunOnUiAsync(() => RefreshUptimeFromSnapshot(snapshot)).ConfigureAwait(true);
            }

            NotifyAggregatePropertiesChanged();
        }
        catch (Exception ex) when (!silent)
        {
            ErrorMessage = ex.Message;
        }
        finally
        {
            if (silent)
            {
                _isSilentRefreshing = false;
            }
            else
            {
                IsRefreshing = false;
                _dashboardInitialized = true;
                RaisePropertyChanged(nameof(ShowLoadingState));
                RaisePropertyChanged(nameof(ShowEmptyState));

                if (Interlocked.Exchange(ref _pendingStartupResync, 0) == 1)
                {
                    _ = RefreshAsync(resyncStructure: true);
                }
            }

            if (Interlocked.Exchange(ref _pendingRuntimeApply, 0) == 1)
            {
                TryApplyLatestRuntimeState();
            }

            if (Interlocked.Exchange(ref _pendingInstallTrackerResync, 0) == 1)
            {
                _ = OnInstallTrackerChangedAsync();
            }
        }
    }

    private static Task RunOnUiAsync(Action action)
    {
        var dispatcher = Application.Current?.Dispatcher;
        if (dispatcher is null || dispatcher.CheckAccess())
        {
            action();
            return Task.CompletedTask;
        }

        return dispatcher.InvokeAsync(action).Task;
    }

    private static Task<T> RunOnUiAsync<T>(Func<T> func)
    {
        var dispatcher = Application.Current?.Dispatcher;
        if (dispatcher is null || dispatcher.CheckAccess())
        {
            return Task.FromResult(func());
        }

        return dispatcher.InvokeAsync(func).Task;
    }

    private static Task RunOnUiAsync(Func<Task> func)
    {
        var dispatcher = Application.Current?.Dispatcher;
        if (dispatcher is null || dispatcher.CheckAccess())
        {
            return func();
        }

        return dispatcher.InvokeAsync(func).Task.Unwrap();
    }

    private void NotifyStructureChanged()
    {
        if (Services.Count != _lastReportedServiceCount)
        {
            _lastReportedServiceCount = Services.Count;
            RaisePropertyChanged(nameof(HasServices));
        }

        RaisePropertyChanged(nameof(ShowEmptyState));
        RaisePropertyChanged(nameof(ShowLoadingState));
        RaisePropertyChanged(nameof(ShowHealthBadge));
        RaisePropertyChanged(nameof(HasPhpListeners));
        RaisePropertyChanged(nameof(HasEnabledProcesses));
    }

    private void NotifyAggregatePropertiesChanged()
    {
        if (RunningCount != _lastRunningCount)
        {
            _lastRunningCount = RunningCount;
            RaisePropertyChanged(nameof(RunningCount));
        }

        if (StoppedCount != _lastStoppedCount)
        {
            _lastStoppedCount = StoppedCount;
            RaisePropertyChanged(nameof(StoppedCount));
        }

        if (AnyRunning != _lastAnyRunning)
        {
            _lastAnyRunning = AnyRunning;
            RaisePropertyChanged(nameof(AnyRunning));
            RaisePropertyChanged(nameof(StartOrRestartAllLabel));
            StopAllCommand.RaiseCanExecuteChanged();
            StartOrRestartAllCommand.RaiseCanExecuteChanged();
        }

        if (AnyProcessRunning != _lastAnyProcessRunning)
        {
            _lastAnyProcessRunning = AnyProcessRunning;
            RaisePropertyChanged(nameof(AnyProcessRunning));
            StopAllProcessesCommand.RaiseCanExecuteChanged();
            StartAllProcessesCommand.RaiseCanExecuteChanged();
        }

        if (RunningProcessCount != _lastRunningProcessCount)
        {
            _lastRunningProcessCount = RunningProcessCount;
            RaisePropertyChanged(nameof(RunningProcessCount));
        }

        if (StoppedProcessCount != _lastStoppedProcessCount)
        {
            _lastStoppedProcessCount = StoppedProcessCount;
            RaisePropertyChanged(nameof(StoppedProcessCount));
        }

        if (ErrorProcessCount != _lastErrorProcessCount)
        {
            _lastErrorProcessCount = ErrorProcessCount;
            RaisePropertyChanged(nameof(ErrorProcessCount));
            RaisePropertyChanged(nameof(HasProcessErrors));
        }
    }

    private void OnServiceLiveStatusChanged(object? sender, ServiceInfo info)
    {
        var dispatcher = Application.Current?.Dispatcher;
        var priority = _startupIsComplete
            ? DispatcherPriority.Normal
            : DispatcherPriority.ContextIdle;
        if (dispatcher is null || dispatcher.CheckAccess())
        {
            ApplyLiveServiceInfo(info);
            return;
        }

        dispatcher.BeginInvoke(() => ApplyLiveServiceInfo(info), priority);
    }

    private void OnTestDnsStatusChanged(object? sender, EventArgs e)
    {
        var dispatcher = Application.Current?.Dispatcher;
        if (dispatcher is null || dispatcher.HasShutdownStarted)
        {
            return;
        }

        if (dispatcher.CheckAccess())
        {
            ApplyTestDnsRowFromCoordinator();
            return;
        }

        dispatcher.BeginInvoke(ApplyTestDnsRowFromCoordinator, DispatcherPriority.ContextIdle);
    }

    private void ApplyTestDnsRowFromCoordinator()
    {
        if (ApplicationShutdownState.IsClosing)
        {
            return;
        }

        var service = Services.FirstOrDefault(row =>
            string.Equals(row.ServiceKey, "testdns", StringComparison.OrdinalIgnoreCase));
        if (service is null)
        {
            return;
        }

        var status = _services.GetRequiredService<TestDnsCoordinator>().GetCachedStatus();
        ApplyTestDnsRowStatus(service, ToRuntimeTestDnsState(status));
        ScheduleCoalescedStartupPresentationUpdate();
    }

    private static RuntimeTestDnsState ToRuntimeTestDnsState(TestDnsStatus status) =>
        new()
        {
            Enabled = status.Enabled,
            Running = status.Running,
            NrptActive = status.NrptActive,
            Message = status.Message
        };

    private void ApplyLiveServiceInfo(ServiceInfo info)
    {
        if (ApplicationShutdownState.IsClosing)
        {
            return;
        }

        var service = Services.FirstOrDefault(row =>
            string.Equals(row.ServiceKey, info.Id, StringComparison.OrdinalIgnoreCase));
        if (service is null)
        {
            return;
        }

        ApplyDashboardServiceStatusFromLive(service, info, "live-event");
        ScheduleCoalescedStartupPresentationUpdate();
    }

    private void ScheduleCoalescedStartupPresentationUpdate()
    {
        if (_startupIsComplete)
        {
            UpdateEnvironmentHealth();
            ScanKeepAliveServiceNotifications();
            NotifyAggregatePropertiesChanged();
            return;
        }

        if (Interlocked.CompareExchange(ref _startupPresentationUpdateScheduled, 1, 0) != 0)
        {
            return;
        }

        var dispatcher = Application.Current?.Dispatcher;
        if (dispatcher is null || dispatcher.HasShutdownStarted)
        {
            Interlocked.Exchange(ref _startupPresentationUpdateScheduled, 0);
            return;
        }

        dispatcher.BeginInvoke(
            () =>
            {
                Interlocked.Exchange(ref _startupPresentationUpdateScheduled, 0);
                if (ApplicationShutdownState.IsClosing || _startupIsComplete)
                {
                    return;
                }

                UpdateEnvironmentHealth();
                NotifyAggregatePropertiesChanged();
            },
            DispatcherPriority.ContextIdle);
    }

    private void OnServiceSupervisionAlert(object? sender, ServiceSupervisionAlertEventArgs e)
    {
        var dispatcher = Application.Current?.Dispatcher;
        if (dispatcher is null || dispatcher.CheckAccess())
        {
            HandleServiceSupervisionAlert(e);
            return;
        }

        dispatcher.BeginInvoke(() => HandleServiceSupervisionAlert(e), DispatcherPriority.Normal);
    }

    private void HandleServiceSupervisionAlert(ServiceSupervisionAlertEventArgs e)
    {
        if (!_startupIsComplete || ApplicationShutdownState.IsClosing)
        {
            return;
        }

        var alertKey = $"{e.ServiceKey}:{e.FailureCount}";
        if (!_supervisionAlertNotified.Add(alertKey))
        {
            return;
        }

        try
        {
            _services.GetRequiredService<IToastService>().Show(
                "Keep-alive recovery",
                $"{e.ServiceName} is still down after {e.FailureCount} restart attempts. Stackroot will keep trying — check Dashboard or logs.");
        }
        catch
        {
            // Toast is best-effort.
        }

        _activity.LogWarning(
            "Services",
            $"{e.ServiceName} keep-alive: {e.FailureCount} failed restart attempts — still retrying.");

        UpdateEnvironmentHealth();
    }

    private void ScanKeepAliveServiceNotifications()
    {
        if (!_startupIsComplete || ApplicationShutdownState.IsClosing)
        {
            return;
        }

        foreach (var service in Services)
        {
            MaybeNotifyKeepAliveServiceDown(service);
        }
    }

    private void MaybeNotifyKeepAliveServiceDown(DashboardServiceRowViewModel service)
    {
        if (!service.IsSupervised || !ExpectsToBeUp(service))
        {
            return;
        }

        if (!IsKeepAliveEnabled(service))
        {
            return;
        }

        if (!service.ExpectRunning || _serviceManager.IsUserStoppedIntent(service.ServiceKey))
        {
            _keepAliveDownNotified.Remove(service.ServiceKey);
            return;
        }

        if (service.IsRunning || service.IsBusy || service.StatusText is "Starting" or "Restarting")
        {
            if (service.IsRunning && _keepAliveDownNotified.Remove(service.ServiceKey))
            {
                TryShowToast(
                    "Keep-alive recovered",
                    $"{service.Name} is running again.");
            }

            return;
        }

        if (service.StatusText is not ("Stopped" or "Error"))
        {
            return;
        }

        if (!_keepAliveDownNotified.Add(service.ServiceKey))
        {
            return;
        }

        var detail = service.StatusText == "Error" && !string.IsNullOrWhiteSpace(service.Message)
            ? $" {service.Message}"
            : string.Empty;

        TryShowToast(
            "Keep-alive service down",
            $"{service.Name} stopped unexpectedly.{detail} Stackroot is trying to restart it.");

        _activity.LogWarning(
            "Services",
            $"{service.Name} stopped unexpectedly — keep-alive restart in progress.");
    }

    private void TryShowToast(string title, string message)
    {
        if (ApplicationShutdownState.IsClosing)
        {
            return;
        }

        try
        {
            _services.GetRequiredService<IToastService>().Show(title, message);
        }
        catch
        {
            // Toast is best-effort.
        }
    }

    private void ApplyDashboardServiceStatusFromLive(
        DashboardServiceRowViewModel service,
        ServiceInfo? liveInfo,
        string source)
    {
        var previousStatus = service.StatusText;

        if (source == "snapshot" && ShouldIgnoreStaleSnapshot(service, liveInfo))
        {
            return;
        }

        if (source == "snapshot"
            && string.Equals(service.StatusText, "Running", StringComparison.Ordinal)
            && liveInfo is { Status: ServiceStatus.Starting })
        {
            return;
        }

        if (service.IsBusy
            && string.Equals(service.StatusText, "Starting", StringComparison.Ordinal)
            && liveInfo?.PortOpen != true
            && liveInfo?.Status != ServiceStatus.Starting
            && string.IsNullOrWhiteSpace(liveInfo?.Message))
        {
            return;
        }

        if (service.IsBusy
            && string.Equals(service.StatusText, "Stopping", StringComparison.Ordinal)
            && liveInfo is not { PortOpen: false }
            && liveInfo?.Status != ServiceStatus.Stopped)
        {
            return;
        }

        if (service.IsBusy
            && string.Equals(service.StatusText, "Restarting", StringComparison.Ordinal)
            && liveInfo?.PortOpen != true)
        {
            return;
        }

        if (liveInfo is null)
        {
            if (source == "snapshot")
            {
                return;
            }

            if (service.Message is not null || service.IsRunning)
            {
                service.Message = null;
                ApplyDashboardServiceStatus(service, false);
                ApplyServiceUptime(service, false, null);
                LogServiceStatusChange(service, previousStatus);
            }

            return;
        }

        if (liveInfo.Status == ServiceStatus.Error)
        {
            if (service.IsRunning || service.StatusText != "Error" || service.Message != liveInfo.Message)
            {
                service.Message = liveInfo.Message;
                service.IsBusy = false;
                service.IsRunning = false;
                service.StatusText = "Error";
                service.StatusColor = "#EAAAB0";
                ApplyServiceUptime(service, false, null);
                LogServiceStatusChange(service, previousStatus);
            }

            return;
        }

        if (liveInfo.Status == ServiceStatus.Starting)
        {
            if (string.Equals(service.StatusText, "Running", StringComparison.Ordinal) && service.IsRunning)
            {
                return;
            }

            service.NoteStarted();
            var applyMessage = !string.IsNullOrWhiteSpace(liveInfo.Message)
                || string.IsNullOrWhiteSpace(service.Message);
            if (service.IsRunning
                || service.StatusText != "Starting"
                || (applyMessage && service.Message != liveInfo.Message))
            {
                if (applyMessage)
                {
                    service.Message = liveInfo.Message;
                }

                service.SetStartupProgress(true);
                service.IsRunning = false;
                service.StatusText = "Starting";
                service.StatusColor = "#E9BD5B";
                ApplyServiceUptime(service, false, null);
                LogServiceStatusChange(service, previousStatus);
            }

            return;
        }

        var isRunning = liveInfo.PortOpen == true;
        if (isRunning)
        {
            service.NoteStarted();
        }

        if (service.Message != liveInfo.Message)
        {
            service.Message = liveInfo.Message;
        }

        ApplyDashboardServiceStatus(service, isRunning);
        ApplyServiceUptime(service, isRunning, liveInfo.Pid);
        ApplyKeepAliveRowMessage(service);
        if (isRunning)
        {
            _serviceLastStableRunningAt[service.ServiceKey] = DateTimeOffset.UtcNow;
        }

        LogServiceStatusChange(service, previousStatus);
    }

    private bool ShouldIgnoreStaleSnapshot(DashboardServiceRowViewModel service, ServiceInfo? liveInfo)
    {
        if (liveInfo is null)
        {
            return false;
        }

        // Never override a user's explicit stop with stale Running/Starting snapshot data.
        if (_serviceManager.IsUserStoppedIntent(service.ServiceKey))
        {
            return liveInfo.PortOpen == true || liveInfo.Status == ServiceStatus.Starting;
        }

        if (liveInfo.Status == ServiceStatus.Error)
        {
            return false;
        }

        if (liveInfo.Status == ServiceStatus.Running && liveInfo.PortOpen == true)
        {
            return false;
        }

        // Ignore stale stopped snapshots only while a start/restart is still in progress.
        if (liveInfo.Status == ServiceStatus.Stopped && liveInfo.PortOpen != true)
        {
            if (service.ShowStartupProgress
                || string.Equals(service.StatusText, "Starting", StringComparison.Ordinal)
                || string.Equals(service.StatusText, "Restarting", StringComparison.Ordinal)
                || service.IsBusy)
            {
                return true;
            }

            if (service.IsRunning
                && _serviceLastStableRunningAt.TryGetValue(service.ServiceKey, out var lastRunning)
                && DateTimeOffset.UtcNow - lastRunning < ServiceSnapshotStabilization)
            {
                return true;
            }
        }

        // Unstick rows: a running snapshot must override a stale Starting presentation.
        if (string.Equals(service.StatusText, "Starting", StringComparison.Ordinal)
            && liveInfo is { PortOpen: true })
        {
            return false;
        }

        if (string.Equals(service.StatusText, "Starting", StringComparison.Ordinal)
            && liveInfo.Status == ServiceStatus.Starting
            && !string.IsNullOrWhiteSpace(service.Message)
            && !string.Equals(service.Message, liveInfo.Message, StringComparison.Ordinal))
        {
            return true;
        }

        return false;
    }

    private void LogServiceStatusChange(DashboardServiceRowViewModel service, string previousStatus)
    {
        if (string.Equals(service.StatusText, previousStatus, StringComparison.Ordinal))
        {
            return;
        }

        _diagnostics.LogActivity(
            "Dashboard",
            $"Service status: {service.ServiceKey} {previousStatus} → {service.StatusText}");
    }

    private static void ApplyServiceUptime(DashboardServiceRowViewModel service, bool isRunning, int? pid)
    {
        service.SetUptimeFromPid(isRunning ? pid : null);
    }

    private static void ApplyPhpListenerUptime(DashboardPhpListenerViewModel listener, bool isRunning)
    {
        listener.SyncUptimeFromListener(isRunning);
    }

    private static void ApplyProcessUptime(DashboardProcessRowViewModel row, ProcessStatus status, int? pid)
    {
        var isRunning = status is ProcessStatus.Running or ProcessStatus.Restarting;
        row.SetUptimeFromPid(isRunning ? pid : null);
    }

    private static void ApplyDashboardServiceStatus(DashboardServiceRowViewModel service, bool isRunning)
    {
        var statusText = isRunning
            ? service.ServiceKey is "imagemagick" ? "Ready" : "Running"
            : "Stopped";
        var statusColor = isRunning ? "#8FD6B6" : "#91A0B5";

        if (service.IsRunning == isRunning && service.StatusText == statusText && service.StatusColor == statusColor && !service.IsBusy)
        {
            return;
        }

        service.IsBusy = false;
        service.SetStartupProgress(false);
        service.IsRunning = isRunning;
        service.StatusText = statusText;
        service.StatusColor = statusColor;
        if (isRunning)
        {
            service.NoteStarted();
        }
    }

    private void RefreshUptimeFromSnapshot(RuntimeStateSnapshot snapshot)
    {
        foreach (var service in Services)
        {
            if (!service.IsRunning)
            {
                ApplyServiceUptime(service, false, null);
                continue;
            }

            if (string.Equals(service.ServiceKey, "mailpit", StringComparison.OrdinalIgnoreCase))
            {
                ApplyServiceUptime(service, true, snapshot.Mailpit?.Pid);
                continue;
            }

            var live = snapshot.Services.FirstOrDefault(row =>
                string.Equals(row.Id, service.ServiceKey, StringComparison.OrdinalIgnoreCase));
            ApplyServiceUptime(service, true, live?.Pid);
        }

        foreach (var listener in PhpListeners)
        {
            ApplyPhpListenerUptime(listener, listener.IsRunning);
        }

        foreach (var row in EnabledProcesses)
        {
            var process = snapshot.Processes.FirstOrDefault(candidate => candidate.Id == row.Id);
            if (process is null)
            {
                ApplyProcessUptime(row, ProcessStatus.Stopped, null);
                continue;
            }

            ApplyProcessUptime(row, process.Status, process.Pid);
        }
    }

    private async Task SyncPhpListenersAsync()
    {
        var settings = _settingsStore.Load();
        var host = string.IsNullOrWhiteSpace(settings.Php.FpmHost) ? "127.0.0.1" : settings.Php.FpmHost;
        var requiredVersionIds = ResolveRequiredPhpVersionIds();
        var phpVersions = _phpConfigWriter.ListInstalledPhpVersions(settings, requiredVersionIds);
        var rows = new List<(PhpVersionInfo Version, string Endpoint, bool IsRunning, string StatusText, string LogPath, bool LogExists)>();

        foreach (var version in phpVersions)
        {
            var port = version.FastCgiPort ?? 0;
            if (port <= 0)
            {
                continue;
            }

            var endpoint = $"{host}:{port}";
            var listenerState = _runtimeState.LatestSnapshot?.PhpListeners.FirstOrDefault(row =>
                string.Equals(row.VersionId, version.Id, StringComparison.OrdinalIgnoreCase));
            var isRunning = listenerState?.IsRunning ?? _runtimeState.IsManagedPhpListenerRunning(version.Id);
            if (!isRunning && !version.IsRequired)
            {
                continue;
            }

            var statusText = isRunning
                ? "Running"
                : (version.IsRequired ? "Stopped" : listenerState?.StatusText ?? "Stopped");
            var logPath = PhpLogPaths.GetCgiStderrLogPath(_paths.LogsRoot, version.Id);
            var errorLogPath = PhpLogPaths.GetErrorLogPath(_paths.LogsRoot, version.Id);
            rows.Add((version, endpoint, isRunning, statusText, logPath, File.Exists(logPath) || File.Exists(errorLogPath)));
        }

        await RunOnUiAsync(() =>
        {
            foreach (var stale in PhpListeners.Where(row => rows.All(r => !string.Equals(r.Version.Id, row.VersionId, StringComparison.OrdinalIgnoreCase))).ToList())
            {
                PhpListeners.Remove(stale);
            }

            foreach (var (version, endpoint, isRunning, statusText, logPath, logExists) in rows)
            {
                var existing = PhpListeners.FirstOrDefault(row => string.Equals(row.VersionId, version.Id, StringComparison.OrdinalIgnoreCase));
                if (existing is null)
                {
                    var capturedVersion = version.Id;
                    var listener = new DashboardPhpListenerViewModel
                    {
                        VersionId = version.Id,
                        IsRequired = version.IsRequired,
                        Endpoint = endpoint,
                        StatusColor = ResolvePhpListenerStatusColor(isRunning, statusText),
                        ShowLogButton = logExists || isRunning,
                        OpenLogCommand = new RelayCommand(_ => OpenPhpLog(logPath, PhpLogPaths.GetErrorLogPath(_paths.LogsRoot, version.Id)), _ => logExists || isRunning),
                        StopCommand = new RelayCommand(_ => _ = StopPhpCgiAsync(capturedVersion), _ => isRunning),
                        RestartCommand = null!, // filled below
                        IsRunning = isRunning,
                        StatusText = statusText
                    };
                    listener.RestartCommand = new RelayCommand(
                        _ => _ = RestartPhpCgiAsync(capturedVersion),
                        _ => version.IsRequired && !listener.IsRestarting);

                    ApplyPhpListenerUptime(listener, isRunning);
                    PhpListeners.Add(listener);
                    continue;
                }

                if (!string.Equals(existing.Endpoint, endpoint, StringComparison.Ordinal))
                {
                    existing.Endpoint = endpoint;
                }

                if (existing.IsRunning != isRunning || existing.StatusText != statusText)
                {
                    _diagnostics.LogActivity("Dashboard",
                        $"PHP listener status: {version.Id} {existing.StatusText} → {statusText} (port open={isRunning})");
                    existing.IsRunning = isRunning;
                    existing.StatusText = statusText;
                    existing.StatusColor = ResolvePhpListenerStatusColor(isRunning, statusText);
                }

                existing.ShowLogButton = logExists || isRunning;
                existing.StopCommand.RaiseCanExecuteChanged();
                existing.RestartCommand.RaiseCanExecuteChanged();
                ApplyPhpListenerUptime(existing, isRunning);
            }

            RaisePropertyChanged(nameof(HasPhpListeners));
        }).ConfigureAwait(true);
    }

    private IReadOnlyList<string> ResolveRequiredPhpVersionIds() =>
        _serviceManager.ResolveRequiredPhpVersionIds();

    private static void OpenPhpLog(string stderrLogPath, string errorLogPath)
    {
        var path = File.Exists(stderrLogPath)
            ? stderrLogPath
            : File.Exists(errorLogPath)
                ? errorLogPath
                : null;

        if (path is null)
        {
            return;
        }

        var dialogVm = new FileLogDialogViewModel(
            new SiteLogSession(path),
            $"Log — {Path.GetFileName(path)}");
        var owner = Application.Current?.MainWindow;
        var dialog = new SiteProcessLogDialog
        {
            DataContext = dialogVm,
            Owner = owner
        };

        dialogVm.RequestClose += (_, _) => dialog.Close();
        dialog.Closed += (_, _) => dialogVm.Dispose();
        dialog.Show();
    }

    private async Task StopPhpCgiAsync(string versionId)
    {
        try
        {
            await _serviceManager.StopPhpCgiAsync(versionId);
        }
        catch (Exception ex)
        {
            ErrorMessage = ex.Message;
        }
        finally
        {
            await RefreshAsync(silent: true);
        }
    }

    private async Task RestartPhpCgiAsync(string versionId)
    {
        var listener = PhpListeners.FirstOrDefault(l => string.Equals(l.VersionId, versionId, StringComparison.OrdinalIgnoreCase));
        if (listener is not null)
        {
            listener.IsRestarting = true;
            listener.StatusText = "Restarting…";
            listener.StatusColor = "#E9BD5B";
            listener.IsRunning = false;
        }

        try
        {
            var result = await _serviceManager.RestartPhpFastCgiAsync([versionId]);
            if (result.Success)
            {
                _activity.LogSuccess("PHP", SessionActivityMessages.PhpVersionRestarted(versionId));
            }
            else if (!string.IsNullOrWhiteSpace(result.Message))
            {
                ErrorMessage = result.Message;
                _activity.LogError(
                    "PHP",
                    result.Message ?? SessionActivityMessages.ServiceAction($"PHP {versionId}", "restart", false));
            }
        }
        catch (Exception ex)
        {
            ErrorMessage = ex.Message;
            _activity.LogError("PHP", ex.Message, ex);
        }
        finally
        {
            if (listener is not null)
            {
                listener.IsRestarting = false;
            }

            await RefreshAsync(silent: true);
        }
    }

    private void SyncProcesses() => _ = SyncProcessesAsync();

    private async Task SyncProcessesAsync()
    {
        var processes = await Task.Run(() =>
            _processManager.List()
                .Where(process => process.Enabled)
                .OrderBy(process => process.Featured == true ? 0 : 1)
                .ThenBy(process => process.Name, StringComparer.OrdinalIgnoreCase)
                .ToList()).ConfigureAwait(false);

        await RunOnUiAsync(() => ApplyProcessShellItems(processes)).ConfigureAwait(true);
    }

    private void ApplyProcessShellItems(IReadOnlyList<ProcessInfo> processes)
    {
        foreach (var stale in EnabledProcesses.Where(row => processes.All(process => process.Id != row.Id)).ToList())
        {
            EnabledProcesses.Remove(stale);
        }

        foreach (var process in processes)
        {
            var existing = EnabledProcesses.FirstOrDefault(row => row.Id == process.Id);
            if (existing is not null && !IsProcessRowCurrent(existing, process))
            {
                EnabledProcesses.Remove(existing);
                existing = null;
            }

            if (existing is null)
            {
                var id = process.Id;
                var isActive = process.Status is ProcessStatus.Running or ProcessStatus.Restarting;
                var row = new DashboardProcessRowViewModel
                {
                    Id = process.Id,
                    Name = process.Name,
                    SiteLabel = string.IsNullOrWhiteSpace(process.SiteId) ? "global" : process.SiteId,
                    RuntimeLabel = process.RuntimeLabel,
                    CommandLine = process.CommandLine ?? string.Empty,
                    WorkDir = process.ResolvedCwd ?? process.WorkDir ?? string.Empty,
                    IsRunning = isActive,
                    IsFeatured = process.Featured == true,
                    AutoStart = process.AutoStart,
                    RestartDelaySeconds = process.RestartDelaySeconds,
                    IsSupervised = process.Supervised != false,
                    ShowLogButton = process.HasLog == true || isActive || process.Status == ProcessStatus.Error,
                    OpenLogCommand = new RelayCommand(_ => OpenProcessLog(id, process.Name)),
                    StartCommand = new RelayCommand(_ => RefreshAfterProcessAction(id, () => _processManager.Start(id), "start"), _ => CanStartDashboardProcess(id)),
                    StopCommand = new RelayCommand(_ => RefreshAfterProcessAction(id, () => _processManager.Stop(id), "stop"), _ => CanStopDashboardProcess(id)),
                    RestartCommand = new RelayCommand(_ => _ = RefreshAfterProcessRestartAsync(id), _ => CanRestartDashboardProcess(id))
                };
                ApplyProcessStatus(row, process.Status, process.Available, process.Pid);
                EnabledProcesses.Add(row);
                continue;
            }

            ApplyProcessStatus(existing, process.Status, process.Available, process.Pid);
            existing.AutoStart = process.AutoStart;
            existing.RestartDelaySeconds = process.RestartDelaySeconds;
            existing.IsSupervised = process.Supervised != false;
            existing.StartCommand.RaiseCanExecuteChanged();
            existing.StopCommand.RaiseCanExecuteChanged();
            existing.RestartCommand.RaiseCanExecuteChanged();
        }

        StartAllProcessesCommand.RaiseCanExecuteChanged();
        StopAllProcessesCommand.RaiseCanExecuteChanged();

        // Reorder: featured processes first
        var ordered = EnabledProcesses.OrderBy(p => p.IsFeatured ? 0 : 1).ToList();
        for (var i = 0; i < ordered.Count; i++)
        {
            var current = EnabledProcesses[i];
            var target = ordered[i];
            if (current != target)
            {
                var idx = EnabledProcesses.IndexOf(target);
                EnabledProcesses.Move(idx, i);
            }
        }
    }

    private static bool IsProcessRowCurrent(DashboardProcessRowViewModel row, ProcessInfo process)
        => string.Equals(row.Name, process.Name, StringComparison.Ordinal)
           && string.Equals(row.SiteLabel, string.IsNullOrWhiteSpace(process.SiteId) ? "global" : process.SiteId, StringComparison.Ordinal)
           && string.Equals(row.RuntimeLabel, process.RuntimeLabel, StringComparison.Ordinal)
           && string.Equals(row.CommandLine, process.CommandLine ?? string.Empty, StringComparison.Ordinal)
           && string.Equals(row.WorkDir, process.ResolvedCwd ?? process.WorkDir ?? string.Empty, StringComparison.Ordinal)
           && row.IsFeatured == (process.Featured == true)
           && row.AutoStart == process.AutoStart
           && row.RestartDelaySeconds == process.RestartDelaySeconds
           && row.IsSupervised == (process.Supervised != false)
           && row.ShowLogButton == (process.HasLog == true
               || process.Status is ProcessStatus.Running or ProcessStatus.Restarting or ProcessStatus.Error);

    private void RebuildProcesses()
    {
        EnabledProcesses.Clear();
        foreach (var process in _processManager.List().Where(p => p.Enabled).OrderBy(p => p.Name, StringComparer.OrdinalIgnoreCase))
        {
            var id = process.Id;
            var isActive = process.Status is ProcessStatus.Running or ProcessStatus.Restarting;
            var row = new DashboardProcessRowViewModel
            {
                Id = process.Id,
                Name = process.Name,
                SiteLabel = string.IsNullOrWhiteSpace(process.SiteId) ? "global" : process.SiteId,
                RuntimeLabel = process.RuntimeLabel,
                CommandLine = process.CommandLine ?? string.Empty,
                WorkDir = process.ResolvedCwd ?? process.WorkDir ?? string.Empty,
                IsRunning = isActive,
                IsFeatured = process.Featured == true,
                AutoStart = process.AutoStart,
                RestartDelaySeconds = process.RestartDelaySeconds,
                IsSupervised = process.Supervised != false,
                ShowLogButton = process.HasLog == true || isActive || process.Status == ProcessStatus.Error,
                OpenLogCommand = new RelayCommand(_ => OpenProcessLog(id, process.Name)),
                StartCommand = new RelayCommand(_ => RefreshAfterProcessAction(id, () => _processManager.Start(id), "start"), _ => CanStartDashboardProcess(id)),
                StopCommand = new RelayCommand(_ => RefreshAfterProcessAction(id, () => _processManager.Stop(id), "stop"), _ => CanStopDashboardProcess(id)),
                RestartCommand = new RelayCommand(_ => _ = RefreshAfterProcessRestartAsync(id), _ => CanRestartDashboardProcess(id))
            };

            ApplyProcessStatus(row, process.Status, process.Available, process.Pid);
            EnabledProcesses.Add(row);
        }
    }

    private void OpenProcessLog(string processId, string processName)
    {
        var dialogVm = new SiteProcessLogDialogViewModel(_processManager, processId, processName);
        var owner = Application.Current?.MainWindow;
        var dialog = new SiteProcessLogDialog
        {
            DataContext = dialogVm,
            Owner = owner
        };

        dialogVm.RequestClose += (_, _) => dialog.Close();
        dialog.Closed += (_, _) => dialogVm.Dispose();
        dialog.Show();
    }

    private void RefreshAfterProcessAction(string id, Func<ProcessInfo> action, string verb) =>
        _ = RefreshAfterProcessActionAsync(id, action, verb);

    private async Task RefreshAfterProcessActionAsync(string id, Func<ProcessInfo> action, string verb)
    {
        var row = EnabledProcesses.FirstOrDefault(candidate => string.Equals(candidate.Id, id, StringComparison.Ordinal));
        MarkProcessBusy(row, verb);

        try
        {
            var result = await Task.Run(action).ConfigureAwait(true);
            _activityCoordinator.NotifyProcessAction(result, verb);
        }
        catch (Exception ex)
        {
            if (row is not null)
            {
                row.IsBusy = false;
                row.StatusText = "Error";
                row.StatusColor = "#EAAAB0";
                row.IsRunning = false;
            }

            ErrorMessage = ex.Message;
        }

        await RefreshAsync(silent: true);
    }

    private async Task RefreshAfterProcessRestartAsync(string id)
    {
        var row = EnabledProcesses.FirstOrDefault(candidate => string.Equals(candidate.Id, id, StringComparison.Ordinal));
        MarkProcessBusy(row, "restart");

        try
        {
            var result = await Task.Run(() =>
            {
                _processManager.Stop(id);
                Thread.Sleep(300);
                return _processManager.Start(id);
            }).ConfigureAwait(true);
            _activityCoordinator.NotifyProcessAction(result, "restart");
        }
        catch (Exception ex)
        {
            if (row is not null)
            {
                row.IsBusy = false;
                row.StatusText = "Error";
                row.StatusColor = "#EAAAB0";
                row.IsRunning = false;
            }

            ErrorMessage = ex.Message;
        }

        await RefreshAsync(silent: true);
    }

    private static void MarkProcessBusy(DashboardProcessRowViewModel? row, string verb)
    {
        if (row is null)
        {
            return;
        }

        row.IsBusy = true;
        row.StatusColor = "#E9BD5B";
        row.StatusText = verb switch
        {
            "start" => "Starting",
            "stop" => "Stopping",
            "restart" => "Restarting",
            _ => "Working"
        };
    }

    private bool CanStartDashboardProcess(string id)
    {
        var row = EnabledProcesses.FirstOrDefault(candidate => string.Equals(candidate.Id, id, StringComparison.Ordinal));
        return row is not null &&
               row.ShowStartButton &&
               row.StatusText is "Stopped" or "Error";
    }

    private bool CanStopDashboardProcess(string id)
    {
        var row = EnabledProcesses.FirstOrDefault(candidate => string.Equals(candidate.Id, id, StringComparison.Ordinal));
        return row is not null && row.ShowStopButton;
    }

    private bool CanRestartDashboardProcess(string id)
    {
        var row = EnabledProcesses.FirstOrDefault(candidate => string.Equals(candidate.Id, id, StringComparison.Ordinal));
        return row is not null && row.ShowRestartButton;
    }

    private static void ApplyProcessStatus(
        DashboardProcessRowViewModel row,
        ProcessStatus status,
        bool available,
        int? pid = null)
    {
        string statusText;
        string statusColor;
        bool isRunning;

        if (status is ProcessStatus.Running or ProcessStatus.Restarting)
        {
            statusText = status == ProcessStatus.Restarting ? "Restarting" : "Running";
            statusColor = status == ProcessStatus.Restarting ? "#E9BD5B" : "#8FD6B6";
            isRunning = true;
        }
        else if (!available)
        {
            statusText = "Unavailable";
            statusColor = "#91A0B5";
            isRunning = false;
        }
        else
        {
            switch (status)
            {
                case ProcessStatus.Error:
                    statusText = "Error";
                    statusColor = "#EAAAB0";
                    isRunning = false;
                    break;
                default:
                    statusText = "Stopped";
                    statusColor = "#91A0B5";
                    isRunning = false;
                    break;
            }
        }

        if (row.StatusText != statusText || row.StatusColor != statusColor || row.IsRunning != isRunning || row.IsBusy)
        {
            row.IsBusy = false;
            row.StatusText = statusText;
            row.StatusColor = statusColor;
            row.IsRunning = isRunning;
            row.StartCommand.RaiseCanExecuteChanged();
            row.StopCommand.RaiseCanExecuteChanged();
            row.RestartCommand.RaiseCanExecuteChanged();
        }

        if (!isRunning)
        {
            ApplyProcessUptime(row, status, null);
        }
        else if (pid is not null)
        {
            ApplyProcessUptime(row, status, pid);
        }
    }

    private Task SyncQuickLinksAsync()
        => RunOnUiAsync(SyncQuickLinksOnUiAsync);

    private async Task SyncQuickLinksOnUiAsync()
    {
        var desired = new List<DashboardQuickLinkViewModel>();
        var settings = _settingsStore.Load();
        var domain = string.IsNullOrWhiteSpace(settings.General.AppDomain) ? "stackroot.test" : settings.General.AppDomain.Trim();
        var nginx = Services.FirstOrDefault(s => s.ServiceKey == "nginx");

        var nginxRunning = nginx?.IsRunning == true;

        if (nginxRunning)
        {
            desired.Add(CreateQuickLink("Hub", $"http://{domain}/"));
        }

        var pma = _phpMyAdminManager.GetStatus();
        if (pma.Enabled && pma.Configured && !string.IsNullOrWhiteSpace(pma.Url) && nginxRunning)
        {
            desired.Add(CreateQuickLink("phpMyAdmin", pma.Url));
        }

        var pra = _phpRedisAdminManager.GetStatus();
        if (pra.Enabled && pra.Ready && !string.IsNullOrWhiteSpace(pra.Url) && nginxRunning)
        {
            desired.Add(CreateQuickLink("phpRedisAdmin", pra.Url));
        }

        var mailpit = await _mailpitManager.GetStatusAsync().ConfigureAwait(true);
        if (mailpit.Enabled && mailpit.Running && !string.IsNullOrWhiteSpace(mailpit.WebUrl))
        {
            desired.Add(CreateQuickLink("Mail", mailpit.WebUrl));
        }

        foreach (var stale in QuickLinks.Where(link => desired.All(d => d.Label != link.Label)).ToList())
        {
            QuickLinks.Remove(stale);
        }

        foreach (var link in desired)
        {
            if (QuickLinks.Any(existing => existing.Label == link.Label))
            {
                continue;
            }

            QuickLinks.Add(link);
        }

        if (QuickLinks.Count != _lastQuickLinkCount)
        {
            _lastQuickLinkCount = QuickLinks.Count;
            RaisePropertyChanged(nameof(HasQuickLinks));
        }
    }

    private static DashboardQuickLinkViewModel CreateQuickLink(string label, string url)
    {
        return new DashboardQuickLinkViewModel
        {
            Label = label,
            Url = url,
            OpenCommand = new RelayCommand(_ => Process.Start(new ProcessStartInfo { FileName = url, UseShellExecute = true }))
        };
    }

    private void StartAllProcesses() => _ = StartAllProcessesAsync();

    private async Task StartAllProcessesAsync()
    {
        IsProcessBulkBusy = true;
        try
        {
            var started = await Task.Run(() => _processManager.StartAll()).ConfigureAwait(true);
            _activityCoordinator.NotifyProcessActions(started, "start");
            if (started.Count > 0)
            {
                _activity.LogSuccess("Processes", SessionActivityMessages.ProcessBulkStarted(started.Count));
            }
        }
        catch (Exception ex)
        {
            ErrorMessage = ex.Message;
        }
        finally
        {
            IsProcessBulkBusy = false;
            await RefreshAsync(silent: true);
        }
    }

    private void StopAllProcesses() => _ = StopAllProcessesAsync();

    private async Task StopAllProcessesAsync()
    {
        IsProcessBulkBusy = true;
        try
        {
            var results = await Task.Run(() => _processManager.StopAll()).ConfigureAwait(true);

            _activityCoordinator.NotifyProcessActions(results, "stop");
            if (results.Count > 0)
            {
                _activity.LogSuccess("Processes", SessionActivityMessages.ProcessBulkStopped(results.Count));
            }
        }
        catch (Exception ex)
        {
            ErrorMessage = ex.Message;
        }
        finally
        {
            IsProcessBulkBusy = false;
            await RefreshAsync(silent: true);
        }
    }

    private async Task SyncServiceRowsAsync()
    {
        var items = await Task.Run(BuildServiceShellItems).ConfigureAwait(false);
        await RunOnUiAsync(() => ApplyServiceShellItems(items)).ConfigureAwait(true);
    }

    private List<ServiceShellItem> BuildServiceShellItems()
    {
        var settings = _settingsStore.Load();
        var desired = new List<ServiceShellItem>();

        foreach (var definition in SettingsDefaults.ServiceDefinitions)
        {
            if (definition.Runtime == ServiceRuntime.Library)
            {
                continue;
            }

            if (!settings.Services.TryGetValue(definition.Id, out var serviceSettings))
            {
                continue;
            }

            if (!serviceSettings.Enabled || !IsPackageInstalled(definition, serviceSettings))
            {
                continue;
            }

            desired.Add(new ServiceShellItem(definition.Id.ToString().ToLowerInvariant(), definition));
        }

        var mailpitSettings = settings.Mailpit;
        if (mailpitSettings.Enabled && _registryStore.IsInstalled(mailpitSettings.PackageId))
        {
            desired.Add(new ServiceShellItem("mailpit", null, DashboardShellServiceKind.Mailpit));
        }

        if (settings.TestDns.Enabled)
        {
            desired.Add(new ServiceShellItem("testdns", null, DashboardShellServiceKind.TestDns));
        }

        return desired;
    }

    private void ApplyServiceShellItems(IReadOnlyList<ServiceShellItem> desired)
    {
        var settings = _settingsStore.Load();

        foreach (var stale in Services.Where(row => desired.All(d => !string.Equals(d.Key, row.ServiceKey, StringComparison.OrdinalIgnoreCase))).ToList())
        {
            Services.Remove(stale);
        }

        foreach (var item in desired)
        {
            var key = item.Key;
            var existing = Services.FirstOrDefault(row =>
                string.Equals(row.ServiceKey, key, StringComparison.OrdinalIgnoreCase));
            if (existing is not null)
            {
                ApplyServiceRowPresentation(existing, settings, item);
                continue;
            }

            if (item.Kind == DashboardShellServiceKind.Mailpit)
            {
                Services.Add(new DashboardServiceRowViewModel
                {
                    ServiceKey = "mailpit",
                    Name = "Mailpit",
                    Description = "Local SMTP catcher and web UI",
                    PortLabel = $"/{MailpitManager.WebPath}",
                    IsSupervised = settings.Mailpit.Supervise && settings.Mailpit.Enabled && _registryStore.IsInstalled(settings.Mailpit.PackageId),
                    StatusText = "Loading",
                    StatusColor = "#91A0B5",
                    SettingsCommand = new RelayCommand(_ => OpenMailpitSettings()),
                    StartCommand = new RelayCommand(_ => _ = StartAsync("mailpit"), _ => CanStartService("mailpit")),
                    StopCommand = new RelayCommand(_ => _ = StopAsync("mailpit"), _ => CanStopService("mailpit")),
                    RestartCommand = new RelayCommand(_ => _ = RestartAsync("mailpit"), _ => CanStopService("mailpit"))
                });
                continue;
            }

            if (item.Kind == DashboardShellServiceKind.TestDns)
            {
                var testDnsRow = new DashboardServiceRowViewModel
                {
                    ServiceKey = "testdns",
                    Name = "Test DNS",
                    Description = "Wildcard .test resolution via 127.0.0.1:53",
                    PortLabel = "53",
                    IsSupervised = false,
                    StatusText = IsTestDnsAutoStartExpected() ? "Starting" : "Loading",
                    StatusColor = IsTestDnsAutoStartExpected() ? "#E9BD5B" : "#91A0B5",
                    SettingsCommand = new RelayCommand(_ => OpenTestDnsSettings()),
                    StartCommand = new RelayCommand(_ => _ = StartAsync("testdns"), _ => CanStartService("testdns")),
                    StopCommand = new RelayCommand(_ => _ = StopAsync("testdns"), _ => CanStopService("testdns")),
                    RestartCommand = new RelayCommand(_ => _ = RestartAsync("testdns"), _ => CanStopService("testdns"))
                };
                if (IsTestDnsAutoStartExpected())
                {
                    testDnsRow.SetStartupProgress(true);
                }

                Services.Add(testDnsRow);
                continue;
            }

            var definition = item.Definition!;
            var capturedKey = key;
            Services.Add(new DashboardServiceRowViewModel
            {
                ServiceKey = key,
                Name = definition!.Name,
                Description = definition.Description,
                PortLabel = definition.DefaultPort > 0 ? definition.DefaultPort.ToString() : "Library",
                IsSupervised = settings.Services.TryGetValue(definition.Id, out var svc) && svc.Supervise && svc.Enabled,
                StatusText = "Loading",
                StatusColor = "#91A0B5",
                SettingsCommand = new RelayCommand(_ => OpenServiceSettings(definition.Id)),
                StartCommand = new RelayCommand(_ => _ = StartAsync(capturedKey), _ => CanStartService(capturedKey)),
                StopCommand = new RelayCommand(_ => _ = StopAsync(capturedKey), _ => CanStopService(capturedKey)),
                RestartCommand = new RelayCommand(_ => _ = RestartAsync(capturedKey), _ => CanStopService(capturedKey))
            });
        }
    }

    /// <summary>
    /// Refreshes keep-alive icon and related presentation after settings change (Dashboard or Services page).
    /// </summary>
    public void SyncServicePresentationFromSettings(ServiceId? serviceId = null, bool mailpit = false, bool testDns = false)
    {
        var dispatcher = Application.Current?.Dispatcher;
        if (dispatcher is not null && !dispatcher.CheckAccess())
        {
            dispatcher.Invoke(() => SyncServicePresentationFromSettings(serviceId, mailpit, testDns));
            return;
        }

        var settings = _settingsStore.Load();

        if (testDns)
        {
            _ = RefreshAsync(resyncStructure: true);
            UpdateEnvironmentHealth();
            return;
        }

        if (mailpit || serviceId == ServiceId.Mailpit)
        {
            var row = Services.FirstOrDefault(item =>
                string.Equals(item.ServiceKey, "mailpit", StringComparison.OrdinalIgnoreCase));
            if (row is not null)
            {
                ApplyMailpitRowPresentation(row, settings);
                if (!row.IsSupervised)
                {
                    _keepAliveDownNotified.Remove(row.ServiceKey);
                }
            }

            UpdateEnvironmentHealth();
            return;
        }

        if (serviceId is not null)
        {
            var key = serviceId.Value.ToString().ToLowerInvariant();
            var row = Services.FirstOrDefault(item =>
                string.Equals(item.ServiceKey, key, StringComparison.OrdinalIgnoreCase));
            if (row is not null
                && settings.Services.TryGetValue(serviceId.Value, out var serviceSettings))
            {
                row.IsSupervised = serviceSettings.Supervise && serviceSettings.Enabled;
                if (!row.IsSupervised)
                {
                    _keepAliveDownNotified.Remove(row.ServiceKey);
                }
            }
        }
        else
        {
            foreach (var row in Services)
            {
                if (string.Equals(row.ServiceKey, "mailpit", StringComparison.OrdinalIgnoreCase))
                {
                    ApplyMailpitRowPresentation(row, settings);
                    continue;
                }

                if (Enum.TryParse<ServiceId>(row.ServiceKey, ignoreCase: true, out var parsedId)
                    && settings.Services.TryGetValue(parsedId, out var serviceSettings))
                {
                    row.IsSupervised = serviceSettings.Supervise && serviceSettings.Enabled;
                }
            }
        }

        UpdateEnvironmentHealth();
    }

    private void ApplyMailpitRowPresentation(DashboardServiceRowViewModel row, AppSettings settings)
    {
        var mailpit = settings.Mailpit;
        row.IsSupervised = mailpit.Supervise
            && mailpit.Enabled
            && _registryStore.IsInstalled(mailpit.PackageId);
    }

    private void ApplyTestDnsRowStatus(DashboardServiceRowViewModel row, RuntimeTestDnsState state)
    {
        if (!state.Enabled)
        {
            row.MarkStopped();
            row.SetStartupProgress(false);
            return;
        }

        if (state.Running && state.NrptActive)
        {
            row.MarkRunning();
            row.Message = null;
            row.SetStartupProgress(false);
            return;
        }

        if (!_startupIsComplete && IsTestDnsAutoStartExpected())
        {
            row.SetStartupProgress(true);
            row.MarkStarting();
            return;
        }

        if (!string.IsNullOrWhiteSpace(state.Message))
        {
            row.MarkError(state.Message);
            row.SetStartupProgress(false);
            return;
        }

        row.MarkStopped();
        row.SetStartupProgress(false);
    }

    private bool IsTestDnsAutoStartExpected()
    {
        var settings = _settingsStore.Load().TestDns;
        return settings.Enabled && settings.AutoStart;
    }

    private void ApplyServiceRowPresentation(
        DashboardServiceRowViewModel row,
        AppSettings settings,
        ServiceShellItem item)
    {
        if (item.Kind == DashboardShellServiceKind.Mailpit)
        {
            ApplyMailpitRowPresentation(row, settings);
            return;
        }

        if (item.Kind == DashboardShellServiceKind.TestDns)
        {
            row.IsSupervised = false;
            return;
        }

        var definition = item.Definition!;
        if (settings.Services.TryGetValue(definition.Id, out var serviceSettings))
        {
            row.IsSupervised = serviceSettings.Supervise && serviceSettings.Enabled;
        }
    }

    private void RebuildServiceRows()
    {
        Services.Clear();
        var settings = _settingsStore.Load();

        foreach (var definition in SettingsDefaults.ServiceDefinitions)
        {
            if (definition.Runtime == ServiceRuntime.Library)
            {
                continue;
            }

            if (!settings.Services.TryGetValue(definition.Id, out var serviceSettings))
            {
                continue;
            }

            if (!serviceSettings.Enabled || !IsPackageInstalled(definition, serviceSettings))
            {
                continue;
            }

            var serviceKey = definition.Id.ToString().ToLowerInvariant();
            var capturedKey = serviceKey;
            Services.Add(new DashboardServiceRowViewModel
            {
                ServiceKey = serviceKey,
                Name = definition.Name,
                Description = definition.Description,
                PortLabel = definition.DefaultPort > 0 ? definition.DefaultPort.ToString() : "Library",
                IsSupervised = serviceSettings.Supervise && serviceSettings.Enabled,
                SettingsCommand = new RelayCommand(_ => OpenServiceSettings(definition.Id)),
                StartCommand = new RelayCommand(_ => _ = StartAsync(capturedKey), _ => CanStartService(capturedKey)),
                StopCommand = new RelayCommand(_ => _ = StopAsync(capturedKey), _ => CanStopService(capturedKey)),
                RestartCommand = new RelayCommand(_ => _ = RestartAsync(capturedKey), _ => CanStopService(capturedKey))
            });
        }

        var mailpitSettings = settings.Mailpit;
        if (mailpitSettings.Enabled && _registryStore.IsInstalled(mailpitSettings.PackageId))
        {
            Services.Add(new DashboardServiceRowViewModel
            {
                ServiceKey = "mailpit",
                Name = "Mailpit",
                Description = "Local SMTP catcher and web UI",
                PortLabel = $"/{MailpitManager.WebPath}",
                IsSupervised = mailpitSettings.Supervise && mailpitSettings.Enabled && _registryStore.IsInstalled(mailpitSettings.PackageId),
                SettingsCommand = new RelayCommand(_ => OpenMailpitSettings()),
                StartCommand = new RelayCommand(_ => _ = StartAsync("mailpit"), _ => CanStartService("mailpit")),
                StopCommand = new RelayCommand(_ => _ = StopAsync("mailpit"), _ => CanStopService("mailpit")),
                RestartCommand = new RelayCommand(_ => _ = RestartAsync("mailpit"), _ => CanStopService("mailpit"))
            });
        }

        if (settings.TestDns.Enabled)
        {
            Services.Add(new DashboardServiceRowViewModel
            {
                ServiceKey = "testdns",
                Name = "Test DNS",
                Description = "Wildcard .test resolution via 127.0.0.1:53",
                PortLabel = "53",
                IsSupervised = false,
                SettingsCommand = new RelayCommand(_ => OpenTestDnsSettings()),
                StartCommand = new RelayCommand(_ => _ = StartAsync("testdns"), _ => CanStartService("testdns")),
                StopCommand = new RelayCommand(_ => _ = StopAsync("testdns"), _ => CanStopService("testdns")),
                RestartCommand = new RelayCommand(_ => _ = RestartAsync("testdns"), _ => CanStopService("testdns"))
            });
        }
    }

    private bool CanStartService(string serviceKey)
    {
        var row = Services.FirstOrDefault(s => s.ServiceKey == serviceKey);
        return row is not null && !row.IsBusy && !row.IsRunning;
    }

    private bool CanStopService(string serviceKey)
    {
        var row = Services.FirstOrDefault(s => s.ServiceKey == serviceKey);
        return row is not null && !row.IsBusy && row.IsRunning;
    }

    private async Task StartOrRestartAllAsync()
    {
        if (AnyRunning)
        {
            await StopAllAsync();
        }

        await StartAllAsync();
    }

    private async Task StartAllAsync()
    {
        if (IsAllBusy || Services.Count == 0)
        {
            return;
        }

        IsAllBusy = true;
        ErrorMessage = null;
        var targets = Services.Where(s => !s.IsRunning && !s.IsBusy).ToList();
        if (targets.Count == 0)
        {
            IsAllBusy = false;
            NotifyAggregatePropertiesChanged();
            return;
        }
        try
        {
            foreach (var service in targets)
            {
                service.MarkStarting();
            }

            NotifyAggregatePropertiesChanged();

            if (targets.Any(service => string.Equals(service.ServiceKey, "nginx", StringComparison.OrdinalIgnoreCase)))
            {
                await PrepareWebStackForNginxAsync();
            }

            var launchTasks = new List<Task>();
            foreach (var service in targets)
            {
                if (string.Equals(service.ServiceKey, "mailpit", StringComparison.OrdinalIgnoreCase))
                {
                    launchTasks.Add(StartMailpitFromDashboardAsync(service));
                    continue;
                }

                if (string.Equals(service.ServiceKey, "testdns", StringComparison.OrdinalIgnoreCase))
                {
                    launchTasks.Add(StartTestDnsFromDashboardAsync(service));
                    continue;
                }

                launchTasks.Add(StartManagedServiceFromDashboardAsync(service, ServiceStartMode.WaitUntilReady));
            }

            await Task.WhenAll(launchTasks);

            var runningCount = targets.Count(service => service.IsRunning);
            var errorRows = targets.Where(service => string.Equals(service.StatusText, "Error", StringComparison.OrdinalIgnoreCase)).ToList();
            var pendingCount = targets.Count - runningCount - errorRows.Count;

            if (errorRows.Count == 0 && pendingCount == 0)
            {
                _activity.LogSuccess("Services", SessionActivityMessages.ServiceBulkStarted(runningCount));
            }
            else
            {
                var summary = $"Service bulk start summary: {runningCount} running, {errorRows.Count} failed, {pendingCount} pending.";
                _activity.LogWarning("Services", summary);
                var firstError = errorRows.FirstOrDefault(row => !string.IsNullOrWhiteSpace(row.Message));
                if (firstError is not null)
                {
                    ErrorMessage = firstError.Message;
                }
            }

            var nginx = targets.FirstOrDefault(service =>
                string.Equals(service.ServiceKey, "nginx", StringComparison.OrdinalIgnoreCase));
            if (nginx is not null && !string.IsNullOrWhiteSpace(nginx.Message))
            {
                ErrorMessage = nginx.Message;
            }
        }
        finally
        {
            IsAllBusy = false;
            NotifyAggregatePropertiesChanged();
            await SyncQuickLinksAsync();
        }
    }

    private async Task StartMailpitFromDashboardAsync(DashboardServiceRowViewModel item)
    {
        try
        {
            var result = await Task.Run(async () => await _mailpitManager.StartAsync().ConfigureAwait(false))
                .ConfigureAwait(true);
            if (result.Running)
            {
                item.MarkRunning();
            }
            else if (!string.IsNullOrWhiteSpace(result.Message))
            {
                item.MarkError(result.Message);
            }
        }
        catch (Exception ex)
        {
            item.MarkError(ex.Message);
        }
    }

    private async Task StartManagedServiceFromDashboardAsync(
        DashboardServiceRowViewModel item,
        ServiceStartMode mode)
    {
        try
        {
            var resultInfo = await Task.Run(async () =>
                    await _serviceManager.StartAsync(item.ServiceKey, default, mode).ConfigureAwait(false))
                .ConfigureAwait(true);
            if (resultInfo.PortOpen == true || resultInfo.Status == ServiceStatus.Running)
            {
                item.MarkRunning();
            }
            else if (resultInfo.Status == ServiceStatus.Starting)
            {
                ApplyDashboardServiceStatusFromLive(item, resultInfo, "manual");
            }
            else if (!string.IsNullOrWhiteSpace(resultInfo.Message))
            {
                item.MarkError(resultInfo.Message);
            }
            else
            {
                item.MarkStopped();
            }
        }
        catch (Exception ex)
        {
            item.MarkError(ex.Message);
        }
    }

    private async Task StartTestDnsFromDashboardAsync(DashboardServiceRowViewModel item)
    {
        try
        {
            var coordinator = _services.GetRequiredService<TestDnsCoordinator>();
            await Task.Run(async () => await coordinator.EnableAsync().ConfigureAwait(false))
                .ConfigureAwait(true);
            var status = coordinator.GetStatus();
            if (status.Running && status.NrptActive)
            {
                item.MarkRunning();
            }
            else if (!string.IsNullOrWhiteSpace(status.Message))
            {
                item.MarkError(status.Message);
            }
        }
        catch (Exception ex)
        {
            item.MarkError(ex.Message);
        }
    }

    private async Task StopAllAsync()
    {
        if (IsAllBusy)
        {
            return;
        }

        IsAllBusy = true;
        ErrorMessage = null;
        var targets = Services.Where(s => s.IsRunning).ToList();
        try
        {
            foreach (var service in targets)
            {
                service.NoteUserStopped();
                _serviceManager.MarkUserStoppedIntent(service.ServiceKey);
            }

            foreach (var service in targets)
            {
                await StopAsync(service.ServiceKey);
            }

            if (targets.Count > 0)
            {
                _activity.LogSuccess("Services", SessionActivityMessages.ServiceBulkStopped(targets.Count));
            }
        }
        finally
        {
            IsAllBusy = false;
            NotifyAggregatePropertiesChanged();
        }
    }

    private async Task PrepareWebStackForNginxAsync(CancellationToken cancellationToken = default)
    {
        try
        {
            await _phpMyAdminManager.ApplyAsync(cancellationToken).ConfigureAwait(false);
        }
        catch
        {
            // Admin tools are optional; nginx should still start.
        }

        try
        {
            await _phpRedisAdminManager.ApplyAsync(cancellationToken).ConfigureAwait(false);
        }
        catch
        {
            // phpRedisAdmin vendor/composer issues must not block nginx or other services.
        }

        var appDomainWriter = _services.GetRequiredService<AppDomainConfigWriter>();
        appDomainWriter.Write();
    }

    private async Task StartAsync(string serviceKey)
    {
        var item = Services.FirstOrDefault(s => s.ServiceKey == serviceKey);
        if (item is null || item.IsBusy)
        {
            return;
        }

        item.MarkStarting();
        try
        {
            if (string.Equals(serviceKey, "mailpit", StringComparison.OrdinalIgnoreCase))
            {
                var result = await Task.Run(async () =>
                        await _mailpitManager.StartAsync().ConfigureAwait(false))
                    .ConfigureAwait(true);
                if (result.Running)
                {
                    item.MarkRunning();
                }
                else if (!string.IsNullOrWhiteSpace(result.Message))
                {
                    item.MarkError(result.Message);
                }

                return;
            }

            if (string.Equals(serviceKey, "testdns", StringComparison.OrdinalIgnoreCase))
            {
                await StartTestDnsFromDashboardAsync(item).ConfigureAwait(true);
                return;
            }

            if (string.Equals(serviceKey, "nginx", StringComparison.OrdinalIgnoreCase))
            {
                await PrepareWebStackForNginxAsync().ConfigureAwait(true);
            }

            var resultInfo = await Task.Run(async () =>
                    await _serviceManager.StartAsync(serviceKey).ConfigureAwait(false))
                .ConfigureAwait(true);
            if (resultInfo.PortOpen == true || resultInfo.Status == ServiceStatus.Running)
            {
                item.MarkRunning();
            }
            else if (resultInfo.Status == ServiceStatus.Starting)
            {
                ApplyDashboardServiceStatusFromLive(item, resultInfo, "manual");
            }
            else if (!string.IsNullOrWhiteSpace(resultInfo.Message))
            {
                item.MarkError(resultInfo.Message);
            }
        }
        catch (Exception ex)
        {
            item.MarkError(ex.Message);
        }
        finally
        {
            item.RefreshCommandStates();
            NotifyAggregatePropertiesChanged();
        }
    }

    private async Task StopAsync(string serviceKey)
    {
        var item = Services.FirstOrDefault(s => s.ServiceKey == serviceKey);
        if (item is null || item.IsBusy)
        {
            return;
        }

        item.IsBusy = true;
        item.StatusText = "Stopping";
        item.StatusColor = "#E9BD5B";
        item.NoteUserStopped();
        if (TryParseServiceId(serviceKey, out _))
        {
            _serviceManager.MarkUserStoppedIntent(serviceKey);
        }

        try
        {
            if (string.Equals(serviceKey, "mailpit", StringComparison.OrdinalIgnoreCase))
            {
                await Task.Run(async () => await _mailpitManager.StopAsync().ConfigureAwait(false))
                    .ConfigureAwait(true);
                item.MarkStopped();
                return;
            }

            if (string.Equals(serviceKey, "testdns", StringComparison.OrdinalIgnoreCase))
            {
                var coordinator = _services.GetRequiredService<TestDnsCoordinator>();
                await Task.Run(async () => await coordinator.StopRuntimeAsync().ConfigureAwait(false))
                    .ConfigureAwait(true);
                item.MarkStopped();
                return;
            }

            if (!TryParseServiceId(serviceKey, out var serviceId))
            {
                return;
            }

            var result = await Task.Run(async () =>
                    await _serviceManager.StopAsync(serviceKey).ConfigureAwait(false))
                .ConfigureAwait(true);
            ApplyDashboardServiceStatusFromLive(item, result, "manual");
        }
        catch (Exception ex)
        {
            item.MarkError(ex.Message);
        }
        finally
        {
            item.IsBusy = false;
            item.RefreshCommandStates();
            NotifyAggregatePropertiesChanged();
            UpdateEnvironmentHealth();
        }
    }

    private async Task RestartAsync(string serviceKey)
    {
        var item = Services.FirstOrDefault(s => s.ServiceKey == serviceKey);
        if (item is null || item.IsBusy)
        {
            return;
        }

        var label = FormatServiceLabel(serviceKey);
        var activityId = _activity.Begin("Services", $"Restarting {label}…");
        item.MarkRestarting();
        using (_activityCoordinator.Suppress(serviceKey))
        {
            try
            {
                if (string.Equals(serviceKey, "testdns", StringComparison.OrdinalIgnoreCase))
                {
                    var coordinator = _services.GetRequiredService<TestDnsCoordinator>();
                    await Task.Run(async () =>
                            await coordinator.RestartRuntimeAsync().ConfigureAwait(false))
                        .ConfigureAwait(true);

                    var status = coordinator.GetStatus();
                    if (status.Running && status.NrptActive)
                    {
                        item.MarkRunning();
                        _activity.Complete(activityId, "Services", SessionActivityMessages.ServiceAction(label, "restarted", true));
                    }
                    else
                    {
                        item.MarkError(status.Message ?? "Test DNS did not start");
                        _activity.Fail(activityId, "Services", SessionActivityMessages.ServiceAction(label, "restart", false, item.Message));
                    }

                    return;
                }

                var resultInfo = await Task.Run(async () =>
                        await _serviceManager.RestartAsync(serviceKey).ConfigureAwait(false))
                    .ConfigureAwait(true);

                var live = _serviceManager.TryBuildLiveInfo(serviceKey) ?? resultInfo;
                ApplyDashboardServiceStatusFromLive(item, live, "manual");

                if (item.StatusText is "Running" or "Ready")
                {
                    _activity.Complete(activityId, "Services", SessionActivityMessages.ServiceAction(label, "restarted", true));
                }
                else if (item.StatusText == "Error")
                {
                    _activity.Fail(activityId, "Services", SessionActivityMessages.ServiceAction(label, "restart", false, item.Message));
                }
                else
                {
                    _activity.Fail(activityId, "Services", SessionActivityMessages.ServiceAction(label, "restart", false));
                }
            }
            catch (Exception ex)
            {
                item.MarkError(ex.Message);
                _activity.Fail(activityId, "Services", ex.Message, ex);
            }
            finally
            {
                item.RefreshCommandStates();
                NotifyAggregatePropertiesChanged();
            }
        }
    }

    private void OpenServiceSettings(ServiceId id)
    {
        var settings = _settingsStore.Load().Services[id];
        var definition = SettingsDefaults.ServiceDefinitions.First(d => d.Id == id);
        var dialogVm = new ServiceSettingsDialogViewModel(
            _settingsStore,
            definition,
            settings,
            ResolvePackageLabel(settings.PackageId ?? definition.PackageId));

        var owner = Application.Current?.MainWindow;
        var dialog = new ServiceSettingsDialog
        {
            DataContext = dialogVm,
            Owner = owner
        };

        dialogVm.RequestClose += (_, _) => dialog.Close();

        SettingsSaveFeedback.DeferredSettingsSave? deferred = null;
        var restartAfterSave = false;
        dialogVm.SettingsSaved += (_, _) =>
        {
            SyncServicePresentationFromSettings(id);

            if (dialogVm.ClosedAfterSave)
            {
                var serviceRow = Services.FirstOrDefault(row =>
                    string.Equals(row.ServiceKey, id.ToString().ToLowerInvariant(), StringComparison.OrdinalIgnoreCase));
                restartAfterSave = dialogVm.NeedsRestart && (serviceRow?.IsRunning ?? false);
                deferred = new SettingsSaveFeedback.DeferredSettingsSave(
                    SessionActivityMessages.SavingSettings(definition.Name),
                    dialogVm.StatusMessage);
                return;
            }

            if (!string.IsNullOrWhiteSpace(dialogVm.StatusMessage))
            {
                _activity.LogSuccess("Services", dialogVm.StatusMessage);
            }
        };

        dialog.ShowDialog();

        if (deferred is { } save)
        {
            var shouldRestart = restartAfterSave;
            var rebuildNginx = id == ServiceId.Nginx;
            var serviceKey = id.ToString().ToLowerInvariant();
            _ = SettingsSaveFeedback.RunDeferredOnSessionActivityAsync(_activity, save, async () =>
            {
                if (rebuildNginx)
                {
                    await _services.GetRequiredService<NginxWebStackRebuilder>().RebuildAsync();
                }

                await RefreshAsync(resyncStructure: true);
                if (shouldRestart
                    && _settingsStore.Load().Services[id].Enabled
                    && definition.Runtime != ServiceRuntime.Library)
                {
                    await RestartAsync(serviceKey);
                }
            });
        }
    }

    private void OpenTestDnsSettings()
    {
        var coordinator = _services.GetService<TestDnsCoordinator>();
        var dialogVm = new TestDnsSettingsDialogViewModel(_settingsStore, coordinator);
        var owner = Application.Current?.MainWindow;
        var dialog = new TestDnsSettingsDialog
        {
            DataContext = dialogVm,
            Owner = owner
        };

        dialogVm.RequestClose += (_, _) => dialog.Close();

        SettingsSaveFeedback.DeferredSettingsSave? deferred = null;
        dialogVm.SettingsSaved += (_, _) =>
        {
            SyncServicePresentationFromSettings(testDns: true);

            deferred = new SettingsSaveFeedback.DeferredSettingsSave(
                "Saving Test DNS settings…",
                dialogVm.StatusMessage,
                async () => await RefreshAsync(resyncStructure: true));
        };

        dialog.ShowDialog();

        if (deferred is { } save)
        {
            _ = SettingsSaveFeedback.RunDeferredOnSessionActivityAsync(_activity, save);
        }
    }

    private void OpenMailpitSettings()
    {
        var dialogVm = new MailpitSettingsDialogViewModel(_settingsStore);
        var owner = Application.Current?.MainWindow;
        var dialog = new MailpitSettingsDialog
        {
            DataContext = dialogVm,
            Owner = owner
        };

        dialogVm.RequestClose += (_, _) => dialog.Close();

        SettingsSaveFeedback.DeferredSettingsSave? deferred = null;
        dialogVm.SettingsSaved += (_, _) =>
        {
            SyncServicePresentationFromSettings(mailpit: true);

            deferred = new SettingsSaveFeedback.DeferredSettingsSave(
                "Saving Mailpit settings…",
                dialogVm.StatusMessage,
                async () =>
                {
                    await _mailpitManager.ApplyAsync();
                    await RefreshAsync(resyncStructure: true);
                });
        };

        dialog.ShowDialog();

        if (deferred is { } save)
        {
            _ = SettingsSaveFeedback.RunDeferredOnSessionActivityAsync(_activity, save);
        }
    }

    private string ResolvePackageLabel(string? packageId)
    {
        if (string.IsNullOrWhiteSpace(packageId))
        {
            return "—";
        }

        var catalog = _services.GetService<PackageCatalogStore>();
        return catalog?.GetById(packageId)?.Label ?? packageId;
    }

    private bool IsPackageInstalled(ServiceDefinition definition, ServicePortSettings settings)
    {
        var packageId = settings.PackageId ?? definition.PackageId;
        return !string.IsNullOrWhiteSpace(packageId) && _registryStore.IsInstalled(packageId);
    }

    private void OpenDataFolder()
    {
        Directory.CreateDirectory(_paths.DataRoot);
        Process.Start(new ProcessStartInfo
        {
            FileName = _paths.DataRoot,
            UseShellExecute = true
        });
    }

    private static bool TryParseServiceId(string serviceKey, out ServiceId serviceId) =>
        Enum.TryParse(serviceKey, ignoreCase: true, out serviceId);

    private static ProcessScope GlobalScope(ServiceId id)
    {
        return new ProcessScope
        {
            Type = ProcessScopeType.Global,
            ProcessId = id.ToString().ToLowerInvariant()
        };
    }

    private static string FormatServiceLabel(string serviceKey)
    {
        if (string.IsNullOrWhiteSpace(serviceKey))
        {
            return "Service";
        }

        if (string.Equals(serviceKey, "testdns", StringComparison.OrdinalIgnoreCase))
        {
            return "Test DNS";
        }

        return char.ToUpperInvariant(serviceKey[0]) + serviceKey[1..];
    }
}
