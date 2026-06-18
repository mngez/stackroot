using System.Diagnostics;
using System.IO;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Threading;
using Microsoft.Extensions.DependencyInjection;
using Stackroot.App.Helpers;
using Stackroot.App.Services;
using Stackroot.App.ViewModels;
using Stackroot.App.Views;
using Stackroot.App.Views.Pages;
using Stackroot.App.Scheduling;
using Stackroot.App.Windows;
using Stackroot.Core.Abstractions;
using Stackroot.Core.Databases;
using Stackroot.Core.IO;
using Stackroot.Core.Observability;
using Stackroot.Core.Services;
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
        DevelopmentReportLogRotation.RotateSessionLog(
            Path.Combine(
                Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData),
                "Stackroot",
                "logs"));

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
            TryWriteEarlyDevelopmentReport(ex, "Startup theme");
        }

        try
        {
            var dataRoot = Path.Combine(
                Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData),
                "Stackroot");
            _isFirstRun = FirstRunState.IsFirstRun(dataRoot);

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
            TryWriteEarlyDevelopmentReport(ex, "Startup");
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

    private static void TryWriteEarlyDevelopmentReport(Exception ex, string area)
    {
        try
        {
            var logsRoot = Path.Combine(
                Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData),
                "Stackroot",
                "logs");
            Directory.CreateDirectory(logsRoot);
            var path = Path.Combine(logsRoot, "development-report.log");
            var text =
                $"[{DateTimeOffset.Now:u}] ERROR [{area}] {ex.Message}{Environment.NewLine}{ex}{Environment.NewLine}---{Environment.NewLine}";
            File.AppendAllText(path, text);
        }
        catch
        {
            // Best-effort only.
        }
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
            await Application.Current.Dispatcher.InvokeAsync(() =>
            {
                _diagnostics.LogActivity("App", "Core startup finished — deferred tasks continue in background");
                if (_services is not null)
                {
                    _ = _services.GetRequiredService<DashboardViewModel>().RefreshAfterStartupAsync();
                }
            });
        }
    }

    private void OnDeferredStartupCompleted()
    {
        if (_services is null)
        {
            return;
        }

        // Notify user that startup is complete
        try
        {
            _services.GetRequiredService<IToastService>().Show(
                "Stackroot",
                "All services started — your environment is ready.");
        }
        catch { /* toast is best-effort */ }

        // Signal the dashboard that startup is complete so its auto-refresh
        // timer may now perform PHP recovery etc.
        _services.GetRequiredService<DashboardViewModel>().NotifyStartupCompleted();

        // Start the cron scheduler
        _services.GetRequiredService<TaskSchedulerService>().Start();
        try { _services.GetRequiredService<IToastService>().Show("Scheduled Tasks", "Cron scheduler is running."); }
        catch { /* toast is best-effort */ }

        // Dispatch dashboard refresh first, then stagger services refresh to
        // avoid freezing the UI momentarily when both run their synchronous
        // setup on the UI thread simultaneously.
        _ = Application.Current.Dispatcher.InvokeAsync(() =>
        {
            _diagnostics.LogActivity("App", "Deferred startup finished — refreshing dashboard");
            _ = _services.GetRequiredService<DashboardViewModel>().RefreshAfterStartupAsync();
        }, System.Windows.Threading.DispatcherPriority.Background);

        _ = Application.Current.Dispatcher.InvokeAsync(() =>
        {
            _services.GetRequiredService<ServicesViewModel>().RefreshAfterDeferredStartup();
        }, System.Windows.Threading.DispatcherPriority.ApplicationIdle);

        // Preload heavy pages so they are ready when the user navigates to them
        _ = Application.Current.Dispatcher.InvokeAsync(() =>
        {
            _services.GetRequiredService<PhpViewModel>();
            _services.GetRequiredService<NodeViewModel>();
        }, System.Windows.Threading.DispatcherPriority.ApplicationIdle);
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
                shutdown.ShutdownAsync(StackrootShutdownCoordinator.DefaultShutdownTimeout)
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
