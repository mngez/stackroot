using System.Collections.ObjectModel;
using Microsoft.Extensions.DependencyInjection;
using Stackroot.App.Localization;
using Stackroot.App.Commands;
using Stackroot.App.Services;
using Stackroot.App.Services.AppUpdate;
using Stackroot.App.Views.Pages;
using Stackroot.Core.Abstractions;
using Stackroot.Core.Settings;
using Stackroot.Core.Sites.Management;
using SiteModel = Stackroot.Core.Sites.Models.Site;

namespace Stackroot.App.ViewModels;

public sealed class ShellViewModel : ViewModelBase
{
    private readonly IServiceProvider _services;
    private readonly IDiagnosticsReporter _diagnostics;
    private readonly RuntimeStateService _runtimeStateService;
    private readonly RuntimeMetricsService _runtimeMetricsService;
    private readonly SettingsStore _settingsStore;
    private readonly Dictionary<string, Func<System.Windows.Controls.UserControl>> _pageFactories;
    private object? _currentPage;
    private string _selectedRoute = string.Empty;
    private bool _isShutdownOverlayVisible;
    private string _shutdownMessage = "Closing services...";
    private string _overlayTitle = "Stackroot";
    private bool _detailedPollingPausedForTray;
    private bool _systemPowerSaving;
    private bool _showHeaderMetrics = true;

    private DashboardViewModel? _dashboardViewModel;

    public ShellViewModel(
        IServiceProvider services,
        DownloadTrayViewModel downloadTray,
        SessionActivityTrayViewModel activityTray,
        RuntimeMetricsTrayViewModel runtimeMetricsTray,
        RuntimeStateService runtimeStateService,
        RuntimeMetricsService runtimeMetricsService,
        SettingsStore settingsStore,
        AppUpdateViewModel appUpdate,
        SslTrustPromptViewModel sslTrustPrompt,
        IDiagnosticsReporter diagnostics)
    {
        _services = services;
        _diagnostics = diagnostics;
        _runtimeStateService = runtimeStateService;
        _runtimeMetricsService = runtimeMetricsService;
        _settingsStore = settingsStore;
        ActivityTray = activityTray;
        DownloadTray = downloadTray;
        RuntimeMetricsTray = runtimeMetricsTray;
        AppUpdate = appUpdate;
        SslTrustPrompt = sslTrustPrompt;
        _pageFactories = new Dictionary<string, Func<System.Windows.Controls.UserControl>>(StringComparer.OrdinalIgnoreCase)
        {
            ["dashboard"] = () => services.GetRequiredService<DashboardPage>(),
            ["downloads"] = () => services.GetRequiredService<DownloadsPage>(),
            ["settings"] = () => services.GetRequiredService<GeneralSettingsPage>(),
            ["sites"] = () => services.GetRequiredService<SitesPage>(),
            ["processes"] = () => services.GetRequiredService<ProcessesPage>(),
            ["performance"] = () => services.GetRequiredService<PerformancePage>(),
            ["logs"] = () => services.GetRequiredService<LogsPage>(),
            ["php"] = () => services.GetRequiredService<PhpPage>(),
            ["node"] = () => services.GetRequiredService<NodePage>(),
            ["services"] = () => services.GetRequiredService<ServicesPage>(),
            ["tools"] = () => services.GetRequiredService<ToolsPage>(),
            ["databases"] = () => services.GetRequiredService<DatabasesPage>(),
            ["scheduled"] = () => services.GetRequiredService<ScheduledTasksPage>()
        };

        MainNavigationItems = new ObservableCollection<NavigationItem>();
        BottomNavigationItems = new ObservableCollection<NavigationItem>();
        PopulateNavigationItems();

        LocalizationManager.LanguageChanged += (s, e) => PopulateNavigationItems();

        NavigateTo("dashboard");
        runtimeStateService.StartBackgroundPolling();
        ApplyHeaderMetricsFromSettings();
        _diagnostics.LogActivity("App", "Shell initialized");
    }

    public void OnWindowHiddenToTray()
    {
        if (_detailedPollingPausedForTray)
        {
            return;
        }

        _detailedPollingPausedForTray = true;
        SetDetailedPollingForCurrentRoute(enabled: false);
        UpdatePowerSavingMode();
    }

    public void OnWindowShownFromTray()
    {
        if (!_detailedPollingPausedForTray)
        {
            return;
        }

        _detailedPollingPausedForTray = false;
        SetDetailedPollingForCurrentRoute(enabled: true);
        UpdatePowerSavingMode();
        SyncCurrentPageAfterTrayReturn();
    }

    private void SyncCurrentPageAfterTrayReturn()
    {
        if (!string.Equals(SelectedRoute, "dashboard", StringComparison.OrdinalIgnoreCase))
        {
            return;
        }

        _services.GetRequiredService<DashboardViewModel>().SyncPresentationWhenVisible();
    }

    public void OnSystemPowerModeChanged(bool suspended)
    {
        _systemPowerSaving = suspended;
        UpdatePowerSavingMode();
    }

    public void ApplyHeaderMetricsFromSettings()
    {
        var enabled = _settingsStore.Load().General.ShellMetricsEnabled ?? true;
        if (ShowHeaderMetrics != enabled)
        {
            ShowHeaderMetrics = enabled;
        }

        UpdatePowerSavingMode();
    }

    private void UpdatePowerSavingMode()
    {
        var powerSaving = _detailedPollingPausedForTray || _systemPowerSaving;
        var general = _settingsStore.Load().General;
        var metricsEnabled = ShowHeaderMetrics && !powerSaving;
        var cpuRefreshSeconds = general.ShellMetricsCpuRefreshSeconds ?? ShellMetricsDefaults.CpuRefreshSeconds;
        _runtimeStateService.SetPowerSavingMode(powerSaving);
        _runtimeMetricsService.ConfigureHeaderMetrics(metricsEnabled, cpuRefreshSeconds);
    }

    public bool ShowHeaderMetrics
    {
        get => _showHeaderMetrics;
        private set => SetProperty(ref _showHeaderMetrics, value);
    }

    private void SetDetailedPollingForCurrentRoute(bool enabled)
    {
        switch (SelectedRoute.ToLowerInvariant())
        {
            case "dashboard":
                var dashboard = _services.GetRequiredService<DashboardViewModel>();
                if (enabled)
                {
                    dashboard.BeginLoading();
                }
                else
                {
                    dashboard.EndLoading();
                }

                break;

            case "performance":
                if (CurrentPage is System.Windows.FrameworkElement { DataContext: PerformanceViewModel performance })
                {
                    if (enabled)
                    {
                        performance.BeginLoading();
                    }
                    else
                    {
                        performance.EndLoading();
                    }
                }

                break;

            case "processes":
                if (CurrentPage is System.Windows.FrameworkElement { DataContext: ProcessesViewModel processes })
                {
                    if (enabled)
                    {
                        processes.BeginLoading();
                    }
                    else
                    {
                        processes.EndLoading();
                    }
                }

                break;
        }
    }

    public void RefreshSiteNavFromStore(SiteManager siteManager)
    {
        var db = siteManager.GetDashboard();
        var featured = db.Featured.Where(s => s.Enabled).ToArray();

        // Remove old featured site entries from nav
        var toRemove = MainNavigationItems.Where(n => n.IsFeaturedSite).ToList();
        foreach (var item in toRemove)
            MainNavigationItems.Remove(item);

        // Find the "sites" item position and insert featured sites after it
        var sitesIndex = -1;
        for (var i = 0; i < MainNavigationItems.Count; i++)
        {
            if (MainNavigationItems[i].Key == "sites")
            {
                sitesIndex = i;
                break;
            }
        }

        if (sitesIndex >= 0)
        {
            var insertAt = sitesIndex + 1;
            foreach (var site in featured)
            {
                var siteId = site.Id;
                MainNavigationItems.Insert(insertAt++, new NavigationItem(
                    "site-" + siteId,
                    site.Domain,
                    "\uE735",
                    new RelayCommand(_ => NavigateToSiteManage(siteId)))
                { IsFeaturedSite = true });
            }
        }
    }


    public ObservableCollection<NavigationItem> MainNavigationItems { get; }
    public ObservableCollection<NavigationItem> BottomNavigationItems { get; }

    public DownloadTrayViewModel DownloadTray { get; }

    public RuntimeMetricsTrayViewModel RuntimeMetricsTray { get; }

    public DashboardViewModel Dashboard => _dashboardViewModel ??= _services.GetRequiredService<DashboardViewModel>();

    public SessionActivityTrayViewModel ActivityTray { get; }

    public AppUpdateViewModel AppUpdate { get; }

    public SslTrustPromptViewModel SslTrustPrompt { get; }

    public string AppVersionLabel => AppVersion.Current;

    public string SelectedRoute
    {
        get => _selectedRoute;
        set => SetProperty(ref _selectedRoute, value);
    }

    public object? CurrentPage
    {
        get => _currentPage;
        private set => SetProperty(ref _currentPage, value);
    }

    public bool IsShutdownOverlayVisible
    {
        get => _isShutdownOverlayVisible;
        private set => SetProperty(ref _isShutdownOverlayVisible, value);
    }

    public string ShutdownMessage
    {
        get => _shutdownMessage;
        private set => SetProperty(ref _shutdownMessage, value);
    }

    public string OverlayTitle
    {
        get => _overlayTitle;
        private set => SetProperty(ref _overlayTitle, value);
    }

    public void ShowShutdownOverlay(string? message = null)
    {
        OverlayTitle = "Shutting down Stackroot";
        if (!string.IsNullOrWhiteSpace(message))
        {
            ShutdownMessage = message;
        }

        IsShutdownOverlayVisible = true;
    }

    public void ShowStartupOverlay(string? message = null)
    {
        OverlayTitle = "Starting Stackroot";
        ShutdownMessage = string.IsNullOrWhiteSpace(message) ? "Preparing services..." : message;
        IsShutdownOverlayVisible = true;
    }

    public void HideBusyOverlay()
    {
        IsShutdownOverlayVisible = false;
    }

    public void Navigate(string route) => ForceNavigateTo(route);

    public void EnsureInitialPageLoaded()
    {
        if (CurrentPage is null)
        {
            Navigate(SelectedRoute);
        }
    }

    public void NavigateToSiteManage(string siteId)
    {
        if (string.IsNullOrWhiteSpace(siteId))
        {
            return;
        }

        SelectedRoute = "sites";
        CurrentPage = ActivatorUtilities.CreateInstance<SiteManagePage>(_services, siteId.Trim());
        _diagnostics.LogActivity("Navigation", $"Opened site manage: {siteId}");
    }

    private NavigationItem CreateNav(string key, string title, string iconGlyph) =>
        new(key, title, iconGlyph, new RelayCommand(_ => Navigate(key)));

    private void PopulateNavigationItems()
    {
        MainNavigationItems.Clear();
        MainNavigationItems.Add(CreateNav("dashboard", LocalizationManager.Get("Loc.Dashboard.Title", "Dashboard"), "\uE80F"));
        MainNavigationItems.Add(CreateNav("sites", LocalizationManager.Get("Loc.Sites.Title", "Sites"), "\uE774"));
        MainNavigationItems.Add(CreateNav("php", LocalizationManager.Get("Loc.Php.Title", "PHP"), "\uE943"));
        MainNavigationItems.Add(CreateNav("node", LocalizationManager.Get("Loc.Node.Title", "Node"), "\uE8D7"));
        MainNavigationItems.Add(CreateNav("services", LocalizationManager.Get("Loc.Services.Title", "Services"), "\uE90F"));
        MainNavigationItems.Add(CreateNav("tools", LocalizationManager.Get("Loc.Tools.Title", "Tools"), "\uE115"));
        MainNavigationItems.Add(CreateNav("databases", LocalizationManager.Get("Loc.Databases.Title", "Databases"), "\uE8F1"));
        MainNavigationItems.Add(CreateNav("processes", LocalizationManager.Get("Loc.Processes.Title", "Processes"), "\uE768"));
        MainNavigationItems.Add(CreateNav("scheduled", LocalizationManager.Get("Loc.ScheduledTasks.Title", "Scheduled"), "\uE823"));
        MainNavigationItems.Add(CreateNav("logs", LocalizationManager.Get("Loc.Logs.Title", "Logs"), "\uE8EA"));
        MainNavigationItems.Add(CreateNav("performance", LocalizationManager.Get("Loc.Performance.Title", "Performance"), "\uE9D9"));

        BottomNavigationItems.Clear();
        BottomNavigationItems.Add(CreateNav("downloads", LocalizationManager.Get("Loc.Downloads.Title", "Downloads"), "\uE896"));
        BottomNavigationItems.Add(CreateNav("settings", LocalizationManager.Get("Loc.Common.Settings", "Settings"), "\uE713"));
    }

    private void ForceNavigateTo(string route)
    {
        var normalized = route.Trim().TrimStart('/');
        if (!_pageFactories.TryGetValue(normalized, out var factory))
            return;

        try
        {
            CurrentPage = factory();
            _diagnostics.LogActivity("Navigation", $"Opened page: {normalized}");
            if (!string.Equals(SelectedRoute, normalized, StringComparison.OrdinalIgnoreCase))
            {
                _selectedRoute = normalized;
                RaisePropertyChanged(nameof(SelectedRoute));
            }
        }
        catch (Exception ex)
        {
            _diagnostics.LogException($"Navigation:{normalized}", ex);
            throw;
        }
    }

    private void NavigateTo(string route)
    {
        var normalized = route.Trim().TrimStart('/');
        if (!_pageFactories.TryGetValue(normalized, out var factory))
        {
            return;
        }

        if (CurrentPage is not null && string.Equals(SelectedRoute, normalized, StringComparison.OrdinalIgnoreCase))
        {
            return;
        }

        try
        {
            CurrentPage = factory();
            _diagnostics.LogActivity("Navigation", $"Opened page: {normalized}");
            if (!string.Equals(SelectedRoute, normalized, StringComparison.OrdinalIgnoreCase))
            {
                _selectedRoute = normalized;
                RaisePropertyChanged(nameof(SelectedRoute));
            }
        }
        catch (Exception ex)
        {
            _diagnostics.LogException($"Navigation:{normalized}", ex);
            throw;
        }
    }
}
