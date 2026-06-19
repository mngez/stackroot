using System.Collections.Concurrent;
using System.Collections.ObjectModel;
using System.Diagnostics;
using System.IO;
using System.Windows;
using System.Windows.Threading;
using Microsoft.Extensions.DependencyInjection;
using Stackroot.App.Commands;
using Stackroot.App.Helpers;
using Stackroot.App.Services;
using Stackroot.App.Views;
using Stackroot.Core.Abstractions;
using Stackroot.Core.AdminTools;
using Stackroot.Core.Catalog;
using Stackroot.Core.Services;
using Stackroot.Core.Services.Php;
using Stackroot.Core.Settings;
using Stackroot.Core.Supervisor;
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
    private readonly ConcurrentDictionary<string, ServiceInfo> _pendingLiveServiceUpdates = new(StringComparer.OrdinalIgnoreCase);
    private DispatcherTimer? _refreshTimer;
    private bool _isRefreshing;
    private bool _isSilentRefreshing;
    private bool _dashboardInitialized;
    private int _lastReportedServiceCount = -1;
    private int _lastRunningCount = -1;
    private int _lastStoppedCount = -1;
    private int _lastQuickLinkCount = -1;
    private bool _lastAnyRunning;
    private bool _lastAnyProcessRunning;
    private int _liveServiceUpdateScheduled;
    private int _mailpitUpdateScheduled;
    private int _lastRunningProcessCount = -1;
    private int _lastStoppedProcessCount = -1;
    private int _lastErrorProcessCount = -1;
    private bool _isAllBusy;
    private bool _isProcessBulkBusy;
    private string? _lastProcessStatusSnapshot;
    private string? _errorMessage;
    private int _pendingStartupResync;
    private int _pendingPhpListenerRefresh;
    private int _pendingInstallTrackerResync;
    private readonly HashSet<string> _handledCompletedStackPackages = new(StringComparer.OrdinalIgnoreCase);
    private bool _startupIsComplete;

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
        IDiagnosticsReporter diagnostics)
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
        _installTracker.Changed += (_, _) => OnInstallTrackerChanged();
        _serviceManager.LiveStatusChanged += OnServiceLiveStatusChanged;
        _mailpitManager.StatusChanged += OnMailpitStatusChanged;
        _serviceManager.PhpListenersChanged += OnPhpListenersChanged;
        _processManager.Changed += OnProcessManagerChanged;

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
        StartAutoRefresh();
        _ = InitializeDashboardShellAsync();
    }

    /// <summary>
    /// Called by App when the entire startup sequence finishes.
    /// Signals that auto-refresh may now perform PHP recovery etc.
    /// </summary>
    public void NotifyStartupCompleted()
    {
        _startupIsComplete = true;
    }

    public Task RefreshAfterStartupAsync()
    {
        if (IsRefreshing)
        {
            Interlocked.Exchange(ref _pendingStartupResync, 1);
            return Task.CompletedTask;
        }

        return RefreshAsync(resyncStructure: true);
    }

    private async Task InitializeDashboardShellAsync()
    {
        var serviceItems = await Task.Run(BuildServiceShellItems).ConfigureAwait(false);
        await RunOnUiAsync(() =>
        {
            ApplyServiceShellItems(serviceItems);
            NotifyStructureChanged();
        }).ConfigureAwait(false);
        await RunOnUiAsync(SyncProcessesAsync).ConfigureAwait(false);
        await RunOnUiAsync(NotifyStructureChanged).ConfigureAwait(false);
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
        StopAutoRefresh();
    }

    public ObservableCollection<DashboardServiceRowViewModel> Services { get; }
    public ObservableCollection<DashboardPhpListenerViewModel> PhpListeners { get; }
    public ObservableCollection<DashboardProcessRowViewModel> EnabledProcesses { get; }
    public ObservableCollection<DashboardQuickLinkViewModel> QuickLinks { get; }

    private sealed record ServiceShellItem(string Key, ServiceDefinition? Definition, bool IsMailpit);

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
    public string StartOrRestartAllLabel => AnyRunning ? "Restart all" : "Start all";

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

    private void StartAutoRefresh()
    {
        _refreshTimer ??= new DispatcherTimer
        {
            Interval = TimeSpan.FromSeconds(10)
        };
        _refreshTimer.Tick -= OnRefreshTimerTick;
        _refreshTimer.Tick += OnRefreshTimerTick;

        if (!_refreshTimer.IsEnabled)
        {
            _refreshTimer.Start();
        }
    }

    private void StopAutoRefresh()
    {
        if (_refreshTimer is null)
        {
            return;
        }

        _refreshTimer.Stop();
        _refreshTimer.Tick -= OnRefreshTimerTick;
    }

    private void OnRefreshTimerTick(object? sender, EventArgs e) => _ = RefreshAuxiliaryAsync();

    private async Task RefreshAuxiliaryAsync()
    {
        if (_isSilentRefreshing || IsRefreshing)
        {
            return;
        }

        // Avoid starting any work while the application is shutting down.
        if (StackrootShutdownCoordinator.IsShuttingDown)
        {
            StopAutoRefresh();
            return;
        }

        // Don't attempt PHP recovery during initial startup — the startup
        // pipeline handles service initialization in order.
        if (!_startupIsComplete)
        {
            return;
        }

        _isSilentRefreshing = true;
        try
        {
            await UpdatePhpListenerStatusesAsync();
            await _serviceManager.TryRecoverRequiredPhpAsync();
            await UpdateProcessStatusesAsync();
            await UpdateMailpitStatusAsync();
            await SyncQuickLinksAsync();
        }
        finally
        {
            _isSilentRefreshing = false;
        }
    }

    private void OnServiceLiveStatusChanged(object? sender, ServiceInfo info)
    {
        _pendingLiveServiceUpdates[info.Id] = info;
        var dispatcher = Application.Current?.Dispatcher;
        if (dispatcher is null)
        {
            return;
        }

        if (Interlocked.Exchange(ref _liveServiceUpdateScheduled, 1) == 1)
        {
            return;
        }

        dispatcher.BeginInvoke(ApplyPendingLiveServiceUpdates, DispatcherPriority.Background);
    }

    private void ApplyPendingLiveServiceUpdates()
    {
        Interlocked.Exchange(ref _liveServiceUpdateScheduled, 0);
        var updates = _pendingLiveServiceUpdates.ToArray();
        _pendingLiveServiceUpdates.Clear();
        foreach (var (_, info) in updates)
        {
            ApplyLiveServiceInfo(info);
        }
    }

    private void ApplyLiveServiceInfo(ServiceInfo info)
    {
        if (string.Equals(info.Id, "mailpit", StringComparison.OrdinalIgnoreCase))
        {
            ScheduleMailpitStatusUpdate();
            return;
        }

        var service = Services.FirstOrDefault(row =>
            string.Equals(row.ServiceKey, info.Id, StringComparison.OrdinalIgnoreCase));
        if (service is null)
        {
            return;
        }

        service.BumpVersion();
        if (service.IsRunning != (info.PortOpen == true))
        {
            _diagnostics.LogActivity("Dashboard",
                $"Live service status: {info.Id} {service.StatusText} → {(info.PortOpen == true ? "Running" : "Stopped")} (PortOpen={info.PortOpen})");
        }

        ApplyDashboardServiceStatusFromLive(service, info);
        NotifyAggregatePropertiesChanged();

        if (info.Id == "nginx")
        {
            _ = SyncQuickLinksAsync();
        }
    }

    private void OnMailpitStatusChanged(object? sender, EventArgs e)
        => ScheduleMailpitStatusUpdate();

    private void ScheduleMailpitStatusUpdate()
    {
        var dispatcher = Application.Current?.Dispatcher;
        if (dispatcher is null)
        {
            return;
        }

        if (Interlocked.Exchange(ref _mailpitUpdateScheduled, 1) == 1)
        {
            return;
        }

        dispatcher.BeginInvoke(
            async () =>
            {
                Interlocked.Exchange(ref _mailpitUpdateScheduled, 0);
                await UpdateMailpitStatusAsync().ConfigureAwait(true);
                await SyncQuickLinksAsync().ConfigureAwait(true);
            },
            DispatcherPriority.Background);
    }

    private void OnPhpListenersChanged(object? sender, EventArgs e)
    {
        var dispatcher = Application.Current?.Dispatcher;
        if (dispatcher is null)
        {
            return;
        }

        if (!dispatcher.CheckAccess())
        {
            dispatcher.BeginInvoke(() => _ = RefreshPhpListenersAsync());
            return;
        }

        _ = RefreshPhpListenersAsync();
    }

    private void OnProcessManagerChanged(object? sender, EventArgs e)
    {
        Application.Current?.Dispatcher.BeginInvoke(() => SyncProcesses());
    }

    private async Task RefreshPhpListenersAsync()
    {
        if (_isSilentRefreshing || IsRefreshing)
        {
            Interlocked.Exchange(ref _pendingPhpListenerRefresh, 1);
            return;
        }

        _isSilentRefreshing = true;
        try
        {
            Interlocked.Exchange(ref _pendingPhpListenerRefresh, 0);
            await SyncPhpListenersAsync().ConfigureAwait(true);
            NotifyAggregatePropertiesChanged();
        }
        finally
        {
            _isSilentRefreshing = false;
            if (Interlocked.Exchange(ref _pendingPhpListenerRefresh, 0) == 1)
            {
                _ = RefreshPhpListenersAsync();
            }
        }
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
                await SyncPhpListenersAsync();
            }
            else
            {
                await UpdatePhpListenerStatusesAsync();
                await UpdateProcessStatusesAsync();
            }

            await UpdateServiceStatusesAsync();
            await SyncQuickLinksAsync();
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

            if (Interlocked.Exchange(ref _pendingInstallTrackerResync, 0) == 1)
            {
                _ = OnInstallTrackerChangedAsync();
            }

            if (Interlocked.Exchange(ref _pendingPhpListenerRefresh, 0) == 1)
            {
                _ = RefreshPhpListenersAsync();
            }
        }
    }

    private async Task UpdateServiceStatusesAsync()
    {
        // Snapshot each service's current version so we can detect whether
        // a live event updated it while our background query was in-flight.
        var versions = new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase);
        await RunOnUiAsync(() =>
        {
            foreach (var s in Services)
            {
                versions[s.ServiceKey] = s.UpdateVersion;
            }
        }).ConfigureAwait(false);

        var live = await Task.Run(() => _serviceManager.ListLiveAsync().GetAwaiter().GetResult())
            .ConfigureAwait(false);
        var serviceRows = await RunOnUiAsync(() => Services.ToList()).ConfigureAwait(false);
        foreach (var service in serviceRows)
        {
            if (string.Equals(service.ServiceKey, "mailpit", StringComparison.OrdinalIgnoreCase))
            {
                await UpdateMailpitStatusAsync().ConfigureAwait(false);
                continue;
            }

            // If a live event updated this row while our query was running,
            // skip the stale full-refresh data.
            if (versions.TryGetValue(service.ServiceKey, out var capturedVersion)
                && capturedVersion != service.UpdateVersion)
            {
                _diagnostics.LogActivity("Dashboard",
                    $"Skipping stale update for {service.ServiceKey} (live event was newer)");
                continue;
            }

            var liveInfo = live.FirstOrDefault(row =>
                string.Equals(row.Id, service.ServiceKey, StringComparison.OrdinalIgnoreCase));
            if (service.IsRunning != (liveInfo?.PortOpen == true) || service.StatusText == "Loading")
            {
                _diagnostics.LogActivity("Dashboard",
                    $"Service status change: {service.ServiceKey} {service.StatusText} → {(liveInfo?.PortOpen == true ? "Running" : "Stopped")} (PortOpen={liveInfo?.PortOpen})");
            }

            await RunOnUiAsync(() => ApplyDashboardServiceStatusFromLive(service, liveInfo)).ConfigureAwait(false);
        }
    }

    private async Task UpdateMailpitStatusAsync()
    {
        var service = await RunOnUiAsync(() => Services.FirstOrDefault(row =>
            string.Equals(row.ServiceKey, "mailpit", StringComparison.OrdinalIgnoreCase))).ConfigureAwait(false);
        if (service is null)
        {
            return;
        }

        var mailpit = await _mailpitManager.GetStatusAsync().ConfigureAwait(false);
        await RunOnUiAsync(() =>
        {
            ApplyDashboardServiceStatusFromLive(service, new ServiceInfo
            {
                Id = "mailpit",
                PortOpen = mailpit.Enabled && mailpit.Running,
                Status = mailpit.Enabled && mailpit.Running ? ServiceStatus.Running : ServiceStatus.Stopped,
                Message = mailpit.Installed ? null : "Install Mailpit package first"
            });
            NotifyAggregatePropertiesChanged();
        }).ConfigureAwait(false);
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

    private static void ApplyDashboardServiceStatusFromLive(DashboardServiceRowViewModel service, ServiceInfo? liveInfo)
    {
        if (service.IsBusy
            && string.Equals(service.StatusText, "Starting", StringComparison.Ordinal)
            && liveInfo?.PortOpen != true)
        {
            return;
        }

        if (service.IsBusy
            && string.Equals(service.StatusText, "Stopping", StringComparison.Ordinal)
            && liveInfo is not { PortOpen: false })
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
            if (service.Message is not null || service.IsRunning)
            {
                service.Message = null;
                ApplyDashboardServiceStatus(service, false);
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
            }

            return;
        }

        if (liveInfo.Status == ServiceStatus.Starting)
        {
            if (service.IsRunning || service.StatusText != "Starting" || service.Message != liveInfo.Message)
            {
                service.Message = liveInfo.Message;
                service.IsRunning = false;
                service.StatusText = "Starting";
                service.StatusColor = "#E9BD5B";
            }

            return;
        }

        var isRunning = liveInfo.PortOpen == true;
        if (service.Message != liveInfo.Message)
        {
            service.Message = liveInfo.Message;
        }

        ApplyDashboardServiceStatus(service, isRunning);
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
        service.IsRunning = isRunning;
        service.StatusText = statusText;
        service.StatusColor = statusColor;
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
            if (port <= 0) continue;
            var endpoint = $"{host}:{port}";
            var isRunning = await _serviceManager.IsPortOpenAsync(host, port).ConfigureAwait(false);
            if (!isRunning) continue;
            var logPath = Path.Combine(_paths.LogsRoot, $"php-cgi-{version.Id}.stderr.log");
            rows.Add((version, endpoint, true, "Running", logPath, File.Exists(logPath)));
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
                        StatusColor = isRunning ? "#8FD6B6" : "#91A0B5",
                        ShowLogButton = logExists || isRunning,
                        OpenLogCommand = new RelayCommand(_ => OpenPhpLog(logPath), _ => File.Exists(logPath)),
                        StopCommand = new RelayCommand(_ => _ = StopPhpCgiAsync(capturedVersion), _ => isRunning),
                        RestartCommand = null!, // filled below
                        IsRunning = isRunning,
                        StatusText = statusText
                    };
                    listener.RestartCommand = new RelayCommand(
                        _ => _ = RestartPhpCgiAsync(capturedVersion),
                        _ => version.IsRequired && !listener.IsRestarting);

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
                    existing.StatusColor = isRunning ? "#8FD6B6" : "#91A0B5";
                }

                existing.ShowLogButton = logExists || isRunning;
                existing.StopCommand.RaiseCanExecuteChanged();
                existing.RestartCommand.RaiseCanExecuteChanged();
            }

            RaisePropertyChanged(nameof(HasPhpListeners));
        }).ConfigureAwait(true);
    }

    private IReadOnlyList<string> ResolveRequiredPhpVersionIds() =>
        _serviceManager.ResolveRequiredPhpVersionIds();

    private async Task UpdatePhpListenerStatusesAsync()
    {
        var settings = _settingsStore.Load();
        var host = string.IsNullOrWhiteSpace(settings.Php.FpmHost) ? "127.0.0.1" : settings.Php.FpmHost;
        var requiredVersionIds = ResolveRequiredPhpVersionIds();
        var phpVersions = _phpConfigWriter.ListInstalledPhpVersions(settings, requiredVersionIds)
            .ToDictionary(v => v.Id, StringComparer.OrdinalIgnoreCase);
        var updates = new List<(DashboardPhpListenerViewModel Listener, string Endpoint, bool IsRunning, string StatusText)>();

        foreach (var listener in PhpListeners)
        {
            if (!phpVersions.TryGetValue(listener.VersionId, out var version) || version.FastCgiPort is not int port)
            {
                continue;
            }

            var endpoint = $"{host}:{port}";
            var isRunning = await _serviceManager.IsPortOpenAsync(host, port).ConfigureAwait(false);
            var statusText = isRunning
                ? "Running"
                : version.IsRequired ? "Stopped" : "Not required";
            updates.Add((listener, endpoint, isRunning, statusText));
        }

        await RunOnUiAsync(() =>
        {
            foreach (var (listener, endpoint, isRunning, statusText) in updates)
            {
                if (!isRunning)
                {
                    PhpListeners.Remove(listener);
                    continue;
                }

                if (!string.Equals(listener.Endpoint, endpoint, StringComparison.Ordinal))
                {
                    listener.Endpoint = endpoint;
                }

                if (listener.IsRunning == isRunning && listener.StatusText == statusText)
                {
                    continue;
                }

                listener.IsRunning = isRunning;
                listener.StatusText = statusText;
                listener.StatusColor = isRunning ? "#8FD6B6" : "#91A0B5";
                listener.StopCommand.RaiseCanExecuteChanged();
            }
        }).ConfigureAwait(true);
    }

    private static void OpenPhpLog(string logPath)
    {
        if (!File.Exists(logPath))
        {
            return;
        }

        var dialogVm = new FileLogDialogViewModel(logPath, $"Log — {Path.GetFileName(logPath)}");
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
                .ToList()).ConfigureAwait(true);

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
                    ShowLogButton = process.HasLog == true || isActive || process.Status == ProcessStatus.Error,
                    OpenLogCommand = new RelayCommand(_ => OpenProcessLog(id, process.Name)),
                    StartCommand = new RelayCommand(_ => RefreshAfterProcessAction(id, () => _processManager.Start(id), "start"), _ => CanStartDashboardProcess(id)),
                    StopCommand = new RelayCommand(_ => RefreshAfterProcessAction(id, () => _processManager.Stop(id), "stop"), _ => CanStopDashboardProcess(id)),
                    RestartCommand = new RelayCommand(_ => _ = RefreshAfterProcessRestartAsync(id), _ => CanRestartDashboardProcess(id))
                };
                ApplyProcessStatus(row, process.Status, process.Available);
                EnabledProcesses.Add(row);
                continue;
            }

            ApplyProcessStatus(existing, process.Status, process.Available);
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
           && row.ShowLogButton == (process.HasLog == true
               || process.Status is ProcessStatus.Running or ProcessStatus.Restarting or ProcessStatus.Error);

    private async Task UpdateProcessStatusesAsync()
    {
        var snapshots = await Task.Run(() =>
            _processManager.List()
                .Where(process => process.Enabled)
                .Select(process => (process.Id, process.Status, process.Available))
                .ToList()).ConfigureAwait(true);

        var snapshotKey = string.Join('\n', snapshots.Select(snapshot =>
            $"{snapshot.Id}|{snapshot.Status}|{snapshot.Available}"));
        if (string.Equals(snapshotKey, _lastProcessStatusSnapshot, StringComparison.Ordinal))
        {
            return;
        }

        _lastProcessStatusSnapshot = snapshotKey;

        foreach (var snapshot in snapshots)
        {
            var row = EnabledProcesses.FirstOrDefault(r => r.Id == snapshot.Id);
            if (row is null)
            {
                continue;
            }

            if (row.StatusText != snapshot.Status.ToString() || row.IsRunning != (snapshot.Status is ProcessStatus.Running or ProcessStatus.Restarting))
            {
                _diagnostics.LogActivity("Dashboard",
                    $"Process status: {row.Name} {row.StatusText} → {snapshot.Status}");
            }

            ApplyProcessStatus(row, snapshot.Status, snapshot.Available);
        }
    }

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
                ShowLogButton = process.HasLog == true || isActive || process.Status == ProcessStatus.Error,
                OpenLogCommand = new RelayCommand(_ => OpenProcessLog(id, process.Name)),
                StartCommand = new RelayCommand(_ => RefreshAfterProcessAction(id, () => _processManager.Start(id), "start"), _ => CanStartDashboardProcess(id)),
                StopCommand = new RelayCommand(_ => RefreshAfterProcessAction(id, () => _processManager.Stop(id), "stop"), _ => CanStopDashboardProcess(id)),
                RestartCommand = new RelayCommand(_ => _ = RefreshAfterProcessRestartAsync(id), _ => CanRestartDashboardProcess(id))
            };

            ApplyProcessStatus(row, process.Status, process.Available);
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

    private static void ApplyProcessStatus(DashboardProcessRowViewModel row, ProcessStatus status, bool available)
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

        if (row.StatusText == statusText && row.StatusColor == statusColor && row.IsRunning == isRunning && !row.IsBusy)
        {
            return;
        }

        row.IsBusy = false;
        row.StatusText = statusText;
        row.StatusColor = statusColor;
        row.IsRunning = isRunning;
        row.StartCommand.RaiseCanExecuteChanged();
        row.StopCommand.RaiseCanExecuteChanged();
        row.RestartCommand.RaiseCanExecuteChanged();
    }

    private async Task SyncQuickLinksAsync()
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

        var mailpit = await _mailpitManager.GetStatusAsync();
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
            var processIds = EnabledProcesses.Select(process => process.Id).ToList();
            var results = await Task.Run(() =>
            {
                var stopped = new List<ProcessInfo>(processIds.Count);
                foreach (var processId in processIds)
                {
                    stopped.Add(_processManager.Stop(processId));
                }

                return stopped;
            }).ConfigureAwait(true);

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
        var items = await Task.Run(BuildServiceShellItems).ConfigureAwait(true);
        ApplyServiceShellItems(items);
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

            desired.Add(new ServiceShellItem(definition.Id.ToString().ToLowerInvariant(), definition, false));
        }

        var mailpitSettings = settings.Mailpit;
        if (mailpitSettings.Enabled && _registryStore.IsInstalled(mailpitSettings.PackageId))
        {
            desired.Add(new ServiceShellItem("mailpit", null, true));
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
            if (Services.Any(row => string.Equals(row.ServiceKey, key, StringComparison.OrdinalIgnoreCase)))
            {
                continue;
            }

            if (item.IsMailpit)
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
        var targets = Services.Where(s => !s.IsRunning).ToList();
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

                launchTasks.Add(_serviceManager.StartAsync(service.ServiceKey, default, ServiceStartMode.Background));
            }

            await Task.WhenAll(launchTasks);

            if (targets.Count > 0)
            {
                _activity.LogSuccess("Services", SessionActivityMessages.ServiceBulkStarted(targets.Count));
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
                ApplyDashboardServiceStatusFromLive(item, resultInfo);
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
        try
        {
            if (string.Equals(serviceKey, "mailpit", StringComparison.OrdinalIgnoreCase))
            {
                await Task.Run(async () => await _mailpitManager.StopAsync().ConfigureAwait(false))
                    .ConfigureAwait(true);
                item.MarkStopped();
                return;
            }

            if (!TryParseServiceId(serviceKey, out var serviceId))
            {
                return;
            }

            await Task.Run(async () => await _serviceManager.StopAsync(serviceKey).ConfigureAwait(false))
                .ConfigureAwait(true);
            var live = _serviceManager.TryBuildLiveInfo(serviceKey);
            if (live is { PortOpen: false })
            {
                item.MarkStopped();
            }
        }
        catch (Exception ex)
        {
            item.MarkError(ex.Message);
        }
        finally
        {
            var live = _serviceManager.TryBuildLiveInfo(serviceKey);
            if (live is not null)
            {
                ApplyDashboardServiceStatusFromLive(item, live);
            }
            else if (item.IsBusy)
            {
                item.MarkStopped();
            }

            item.RefreshCommandStates();
            NotifyAggregatePropertiesChanged();
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
                var resultInfo = await Task.Run(async () =>
                        await _serviceManager.RestartAsync(serviceKey).ConfigureAwait(false))
                    .ConfigureAwait(true);

                var live = _serviceManager.TryBuildLiveInfo(serviceKey) ?? resultInfo;
                ApplyDashboardServiceStatusFromLive(item, live);

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
            var serviceKey = id.ToString().ToLowerInvariant();
            _ = SettingsSaveFeedback.RunDeferredOnSessionActivityAsync(_activity, save, async () =>
            {
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

        return char.ToUpperInvariant(serviceKey[0]) + serviceKey[1..];
    }
}
