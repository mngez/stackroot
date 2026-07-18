using System.Diagnostics;
using System.IO;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Threading;
using Microsoft.Extensions.DependencyInjection;
using Stackroot.App.Helpers;
using Stackroot.App.Localization;
using Stackroot.App.Services;
using Stackroot.App.Services.AppUpdate;
using Stackroot.App.Services.SslTrust;
using Stackroot.App.ViewModels;
using Stackroot.App.Views;
using Stackroot.App.Views.Pages;
using Stackroot.App.Scheduling;
using Stackroot.App.Windows;
using Stackroot.Core.Abstractions;
using Stackroot.Core.Databases;
using Stackroot.Core.IO;
using Stackroot.Core.IO.Migrations;
using Stackroot.Core.Observability;
using Stackroot.Core.Services;
using Stackroot.Core.Settings;
using Stackroot.Core.Sites.Management;
using Stackroot.Core.Sites.Persistence;
using Stackroot.Core.Windows;

namespace Stackroot.App;

public partial class App : System.Windows.Application
{
    private ServiceProvider? _services;
    public IServiceProvider Services => _services!;
    private AppErrorLogger _errorLogger = AppErrorLogger.ForDefaultPaths();
    private IDiagnosticsReporter _diagnostics = NoOpDiagnosticsReporter.Instance;
    private bool _failureDialogShown;
    private bool _isShuttingDown;
    private bool _isFirstRun;
    private FirstRunSetupWindow? _firstRunSetupWindow;
    private FirstRunSetupViewModel? _firstRunSetupViewModel;

    protected override void OnStartup(StartupEventArgs e)
    {
        base.OnStartup(e);

        RegisterScrollWheelForwarding();

        DispatcherUnhandledException += (_, args) =>
        {
            ReportFailure(args.Exception, "UI thread", shutdown: false);
            args.Handled = true;
        };

        AppDomain.CurrentDomain.UnhandledException += (_, args) =>
        {
            if (args.ExceptionObject is Exception ex)
            {
                ReportFailure(ex, "AppDomain", shutdown: false);
            }
        };

        TaskScheduler.UnobservedTaskException += (_, args) =>
        {
            ReportFailure(args.Exception, "Background task", shutdown: false);
            args.SetObserved();
        };

        try
        {
            WindowsTheme.EnableForAllWindows();
        }
        catch (Exception ex)
        {
            _errorLogger.Log(ex, "Startup theme");
        }

        try
        {
            // Move runtime packages to LocalAppData before any DI/path consumers start.
            RuntimeRootMigration.Run();

            var paths = StackrootPathResolver.Resolve();
            DataMigrationRunner.Run(paths);

            _isFirstRun = FirstRunState.IsFirstRun(paths.DataRoot);

            if (_isFirstRun)
            {
                ShutdownMode = ShutdownMode.OnExplicitShutdown;
                ShowFirstRunSetupWindow();
                _ = InitializeFirstRunAsync();
                return;
            }

            InitializeApplicationCore();
            ShowMainShell();
            _ = RunBackgroundStartupAsync();
        }
        catch (Exception ex)
        {
            _errorLogger.Log(ex, "Startup");
            ReportFailure(ex, "Startup", shutdown: true);
        }
    }

    private static void RegisterScrollWheelForwarding()
    {
        EventManager.RegisterClassHandler(
            typeof(UserControl),
            UIElement.PreviewMouseWheelEvent,
            new MouseWheelEventHandler(ScrollWheelForwarder.OnPreviewMouseWheel));

        EventManager.RegisterClassHandler(
            typeof(DataGrid),
            UIElement.PreviewMouseWheelEvent,
            new MouseWheelEventHandler(ScrollWheelForwarder.OnPreviewMouseWheel));

        EventManager.RegisterClassHandler(
            typeof(ScrollViewer),
            UIElement.PreviewMouseWheelEvent,
            new MouseWheelEventHandler(ScrollWheelForwarder.OnPreviewMouseWheel));
    }

    private void ShowFirstRunSetupWindow()
    {
        _firstRunSetupViewModel = new FirstRunSetupViewModel();
        _firstRunSetupViewModel.OpenStackrootRequested += OnFirstRunOpenStackrootRequested;
        _firstRunSetupWindow = new FirstRunSetupWindow
        {
            DataContext = _firstRunSetupViewModel
        };
        _firstRunSetupWindow.Show();
        _firstRunSetupWindow.Activate();
    }

    private void OnFirstRunOpenStackrootRequested()
    {
        if (_services is null || _firstRunSetupViewModel is null || _firstRunSetupWindow is null)
        {
            return;
        }

        var services = _services;
        var setupWindow = _firstRunSetupWindow;
        var setupViewModel = _firstRunSetupViewModel;

        setupViewModel.MarkDismissed();
        ShowMainShell();
        ShutdownMode = ShutdownMode.OnMainWindowClose;

        _firstRunSetupWindow = null;
        _firstRunSetupViewModel = null;
        setupWindow.Close();

        _diagnostics.LogActivity("App", "First-run setup finished — deferred tasks continue in background");
        _ = services.GetRequiredService<DashboardViewModel>().RefreshAfterStartupAsync();
    }

    private void BuildServiceContainer()
    {
        var collection = new ServiceCollection();
        ConfigureServices(collection);
        _services = collection.BuildServiceProvider();
        StackrootDiagnosticsHooks.Wire(_services);
    }

    private void InitializeApplicationCore()
    {
        var startupWatch = Stopwatch.StartNew();
        BuildServiceContainer();
        WireApplicationServices(startupWatch);
    }

    private void WireApplicationServices(Stopwatch? startupWatch = null)
    {
        if (_services is null)
        {
            return;
        }

        _errorLogger = _services.GetRequiredService<AppErrorLogger>();
        _diagnostics = _services.GetRequiredService<IDiagnosticsReporter>();

        var settingsStore = _services.GetRequiredService<SettingsStore>();
        _services.GetRequiredService<SettingsLoadState>().Initialize(settingsStore);
        LocalizationManager.Apply(settingsStore.Load().General.Language);

        var siteStore = _services.GetRequiredService<SiteStore>();
        _services.GetRequiredService<SitesLoadState>().Initialize(siteStore);

        UiInteractionDiagnostics.Register(_diagnostics);
        MariaDbCredentialSync.ActivityLog = message => _diagnostics.LogActivity("MariaDb", message);

        var paths = _services.GetRequiredService<StackrootPaths>();
        var serviceManager = _services.GetRequiredService<ServiceManager>();
        MariaDbCredentialSync.Configure(
            paths,
            async (serviceId, cancellationToken) =>
            {
                var serviceKey = serviceId == ServiceId.Mariadb ? "mariadb" : "mysql";
                await serviceManager.RestartAsync(serviceKey, cancellationToken).ConfigureAwait(false);
            });

        if (startupWatch is not null)
        {
            _diagnostics.LogActivity("App", $"Application started (container ready in {startupWatch.ElapsedMilliseconds}ms)");
        }

        var deferredStartup = _services.GetRequiredService<DeferredStartupCoordinator>();
        deferredStartup.Completed -= OnDeferredStartupCompleted;
        deferredStartup.Completed += OnDeferredStartupCompleted;

        var startupReadyGate = _services.GetRequiredService<StackrootStartupReadyGate>();
        startupReadyGate.Ready -= OnStartupFullyReady;
        startupReadyGate.Ready += OnStartupFullyReady;

        _services.GetRequiredService<AppUpdateCoordinator>().Start();
        _services.GetRequiredService<SslTrustPromptCoordinator>().Start();

        _ = _services.GetRequiredService<SessionActivityCoordinator>().BeginSessionAsync();
    }

    private async Task InitializeFirstRunAsync()
    {
        if (_firstRunSetupViewModel is null)
        {
            return;
        }

        try
        {
            _firstRunSetupViewModel.BeginStep("init", "Initializing application");
            await Task.Run(BuildServiceContainer).ConfigureAwait(false);
            await Dispatcher.InvokeAsync(() => WireApplicationServices());
            _firstRunSetupViewModel.CompleteStep("init");

            _firstRunSetupViewModel.BeginStep("folders", "Creating data folders");
            var paths = _services!.GetRequiredService<StackrootPaths>();
            _firstRunSetupViewModel.CompleteStep("folders");

            var services = _services!;
            var progress = _firstRunSetupViewModel;
            await Task.Run(() => StackrootBootstrap.RunStartupTasksAsync(services, progress)).ConfigureAwait(false);

            await Dispatcher.InvokeAsync(() =>
            {
                _firstRunSetupViewModel!.BeginStep("finalize", "Completing setup");
                FirstRunState.MarkInstalled(paths.DataRoot);
                _firstRunSetupViewModel.CompleteStep("finalize");
                _firstRunSetupViewModel.SetComplete();
            });
        }
        catch (Exception ex)
        {
            await Dispatcher.InvokeAsync(() =>
            {
                if (_firstRunSetupViewModel?.HasFailed != true)
                {
                    _firstRunSetupViewModel?.FailStep("finalize", ex.Message);
                }

                ReportFailure(ex, "First-run setup", shutdown: false);
            });
        }
    }

    private void ShowMainShell()
    {
        if (_services is null)
        {
            return;
        }

        MainWindow = _services.GetRequiredService<MainWindow>();
        MainWindow.Show();
        MainWindow.Activate();
        _services.GetRequiredService<DashboardViewModel>().BeginLoading();
        Current.Dispatcher.BeginInvoke(
            () => _services.GetRequiredService<ServicesViewModel>(),
            System.Windows.Threading.DispatcherPriority.Background);
    }

    private async Task RunBackgroundStartupAsync()
    {
        if (_services is null)
        {
            return;
        }

        try
        {
            var services = _services;
            await Task.Run(() => StackrootBootstrap.RunStartupTasksAsync(services)).ConfigureAwait(false);
        }
        catch (Exception ex)
        {
            ReportFailure(ex, "Background startup", shutdown: false);
        }
        finally
        {
            if (_services is not null)
            {
                await Application.Current.Dispatcher.InvokeAsync(() =>
                {
                    _diagnostics.LogActivity("App", "Core startup finished — deferred tasks continue in background");
                }, System.Windows.Threading.DispatcherPriority.Background);
            }
        }
    }

    private void OnDeferredStartupCompleted()
    {
        if (_services is null)
        {
            return;
        }

        _ = Task.Run(RunPostStartupPresentationAsync);
    }

    private async Task RunPostStartupPresentationAsync()
    {
        if (_services is null || ApplicationShutdownState.IsClosing)
        {
            return;
        }

        _diagnostics.LogActivity("App", "Deferred startup finished — refreshing dashboard");

        try
        {
            await _services.GetRequiredService<DashboardViewModel>()
                .RefreshAfterStartupAsync()
                .ConfigureAwait(false);

            // Warm the Services page off the UI thread; no arbitrary delay — runs after dashboard apply finishes.
            await _services.GetRequiredService<ServicesViewModel>()
                .RefreshFromExternalAsync()
                .ConfigureAwait(false);
        }
        catch
        {
            // Post-startup presentation is best-effort.
        }

        if (ApplicationShutdownState.IsClosing || _services is null)
        {
            return;
        }

        await Application.Current.Dispatcher.InvokeAsync(
            WarmSecondaryPages,
            System.Windows.Threading.DispatcherPriority.ApplicationIdle);

        if (ApplicationShutdownState.IsClosing || _services is null)
        {
            return;
        }

        _ = Application.Current.Dispatcher.InvokeAsync(() =>
        {
            try
            {
                var siteManager = _services.GetRequiredService<SiteManager>();
                _services.GetRequiredService<ShellViewModel>().RefreshSiteNavFromStore(siteManager);
            }
            catch
            {
                // Featured nav is best-effort.
            }
        }, System.Windows.Threading.DispatcherPriority.ApplicationIdle);
    }

    private void WarmSecondaryPages()
    {
        if (_services is null || ApplicationShutdownState.IsClosing)
        {
            return;
        }

        // After dashboard + services presentation — warm heavy pages without blocking the post-toast window.
        _ = _services.GetRequiredService<PhpViewModel>();
        _services.GetRequiredService<NodeViewModel>().BeginLoading();
    }

    private void OnStartupFullyReady()
    {
        if (_services is null)
        {
            return;
        }

        try
        {
            var scheduler = _services.GetRequiredService<TaskSchedulerService>();
            var readyMessage = scheduler.IsStarted
                ? "All services started — your environment is ready. Cron scheduler running."
                : "All services started — your environment is ready.";
            _services.GetRequiredService<IToastService>().Show("Stackroot", readyMessage);
        }
        catch
        {
            // Toast is best-effort.
        }

        _services.GetRequiredService<DashboardViewModel>().NotifyStartupCompleted();
        _services.GetRequiredService<ServiceManager>().ActivateServiceSupervision();
    }

    private void ReportFailure(Exception ex, string context, bool shutdown)
    {
        _errorLogger.Log(ex, context);
        try
        {
            _diagnostics.LogException(context, ex);
        }
        catch
        {
            // Ignore logging failures during error handling.
        }

        var dispatcher = Current?.Dispatcher;
        if (dispatcher is not null && !dispatcher.CheckAccess())
        {
            dispatcher.BeginInvoke(() => ShowFailureUi(ex, context, shutdown));
            return;
        }

        ShowFailureUi(ex, context, shutdown);
    }

    private void ShowFailureUi(Exception ex, string context, bool shutdown)
    {
        if (!_failureDialogShown && !_isShuttingDown)
        {
            _failureDialogShown = true;
            var details = $"Log file:{Environment.NewLine}{_errorLogger.LogPath}";
            if (_diagnostics.IsEnabled)
            {
                details += $"{Environment.NewLine}{Environment.NewLine}Development report: enabled (see development-report.log)";
            }

            var title = string.Equals(context, "Startup", StringComparison.OrdinalIgnoreCase)
                ? "Stackroot could not start"
                : "Something went wrong";

            StackrootDialogs.ShowError(GetFailureDialogOwner(), title, ex.Message, details);
        }

        if (shutdown)
        {
            Current.Shutdown(1);
        }
    }

    private Window? GetFailureDialogOwner() => MainWindow ?? _firstRunSetupWindow;

    protected override void OnExit(ExitEventArgs e)
    {
        _isShuttingDown = true;

        if (_services is not null)
        {
            try
            {
                _diagnostics.LogActivity("App", "Application shutting down");
            }
            catch
            {
                // Ignore logging failures during shutdown.
            }

            // Stop the scheduler timer first — it fires every 30s on a
            // ThreadPool thread and must not execute tasks during shutdown.
            try { _services.GetRequiredService<TaskSchedulerService>().Dispose(); } catch { }

            try
            {
                var shutdown = _services.GetRequiredService<StackrootShutdownCoordinator>();
                // Run on the thread pool so dispatcher continuations inside ShutdownAsync
                // are not blocked by this Wait() holding the UI thread.
                Task.Run(() => shutdown.ShutdownAsync(StackrootShutdownCoordinator.DefaultShutdownTimeout))
                    .Wait(StackrootShutdownCoordinator.DefaultShutdownTimeout + TimeSpan.FromSeconds(3));
            }
            catch
            {
                // Best-effort shutdown.
            }

            try
            {
                _services.GetRequiredService<BackgroundWorkQueue>().Dispose();
            }
            catch
            {
                // Best-effort shutdown.
            }

            _services.Dispose();
            _services = null;
        }

        base.OnExit(e);
    }

    private static void ConfigureServices(IServiceCollection services)
    {
        StackrootBootstrap.Register(services);

        services.AddSingleton<SessionActivityService>();
        services.AddSingleton<SessionActivityReporter>();
        services.AddSingleton<SessionActivityCoordinator>();
        services.AddSingleton<SessionActivityTrayViewModel>();
        services.AddSingleton<InstallProgressTracker>();
        services.AddSingleton<DownloadTrayViewModel>();
        services.AddSingleton<GitHubAppReleaseClient>();
        services.AddSingleton<AppUpdateStateStore>();
        services.AddSingleton<AppUpdateViewModel>();
        services.AddSingleton<AppUpdateCoordinator>();
        services.AddSingleton<SslTrustStateStore>();
        services.AddSingleton<SslTrustPromptViewModel>();
        services.AddSingleton<SslTrustPromptCoordinator>();
        services.AddSingleton<ProcessArgvResolver>();
        services.AddSingleton<IGlobalProcessArgvResolver>(provider =>
            provider.GetRequiredService<ProcessArgvResolver>());

        services.AddSingleton<ShellViewModel>();
        services.AddSingleton<DashboardViewModel>();
        services.AddTransient<GeneralSettingsViewModel>();
        services.AddSingleton<PhpViewModel>();
        services.AddSingleton<ServicesViewModel>();
        services.AddTransient<SitesViewModel>();
        services.AddTransient<SiteManageViewModel>();
        services.AddTransient<ProcessesViewModel>();
        services.AddTransient<AddGlobalProcessDialogViewModel>();
        services.AddTransient<PerformanceViewModel>();
        services.AddTransient<LogsViewModel>();
        services.AddTransient<DownloadsViewModel>();
        services.AddSingleton<NodeViewModel>();
        services.AddTransient<DatabasesViewModel>();
        services.AddTransient<ToolsViewModel>();

        services.AddTransient<DashboardPage>();
        services.AddTransient<ServicesPage>();
        services.AddTransient<GeneralSettingsPage>();
        services.AddTransient<SitesPage>();
        services.AddTransient<SiteManagePage>();
        services.AddTransient<ProcessesPage>();
        services.AddTransient<PerformancePage>();
        services.AddTransient<LogsPage>();
        services.AddTransient<DownloadsPage>();
        services.AddTransient<PhpPage>();
        services.AddTransient<NodePage>();
        services.AddTransient<ToolsPage>();
        services.AddTransient<DatabasesPage>();

        services.AddSingleton<MainWindow>();
    }
}
