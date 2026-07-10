using System;
using System.IO;
using System.Threading;
using System.Threading.Tasks;
using Stackroot.Core.Abstractions;
using Stackroot.Core.AdminTools;
using Stackroot.Core.Observability;
using Stackroot.Core.Services;
using Stackroot.Core.Supervisor;
using Stackroot.Core.Windows;
using Stackroot.App.ViewModels;

namespace Stackroot.App.Services;

public sealed class StackrootShutdownCoordinator
{
    public static readonly TimeSpan DefaultShutdownTimeout = TimeSpan.FromSeconds(15);

    /// <summary>
    /// Set to true when shutdown begins. Volatile read allows other components
    /// (e.g. background timers) to avoid work during shutdown.
    /// </summary>
    public static bool IsShuttingDown
    {
        get => ApplicationShutdownState.IsShuttingDown;
        private set => ApplicationShutdownState.IsShuttingDown = value;
    }

    private readonly ServiceManager _serviceManager;
    private readonly GlobalProcessManager _globalProcessManager;
    private readonly MailpitManager _mailpitManager;
    private readonly DeferredStartupCoordinator _deferredStartup;
    private readonly IProcessJobManager _processJobManager;
    private readonly IDiagnosticsReporter _diagnostics;
    private readonly ShellViewModel _shellViewModel;
    private readonly RuntimeStateService _runtimeStateService;
    private readonly RuntimeMetricsService _runtimeMetricsService;
    private int _shutdownCompleted;

    public StackrootShutdownCoordinator(
        ServiceManager serviceManager,
        GlobalProcessManager globalProcessManager,
        MailpitManager mailpitManager,
        DeferredStartupCoordinator deferredStartup,
        IProcessJobManager processJobManager,
        IDiagnosticsReporter diagnostics,
        ShellViewModel shellViewModel,
        RuntimeStateService runtimeStateService,
        RuntimeMetricsService runtimeMetricsService)
    {
        _serviceManager = serviceManager;
        _globalProcessManager = globalProcessManager;
        _mailpitManager = mailpitManager;
        _deferredStartup = deferredStartup;
        _processJobManager = processJobManager;
        _diagnostics = diagnostics;
        _shellViewModel = shellViewModel;
        _runtimeStateService = runtimeStateService;
        _runtimeMetricsService = runtimeMetricsService;
    }

    /// <param name="maxBackgroundOperationWait">
    /// How long to wait for in-flight background work (e.g. backups) before stopping processes.
    /// Defaults to 3 minutes for an explicit user-initiated quit. Pass a short bound when reacting
    /// to an OS session-ending event, where there is no time to spare and no one to see a prompt.
    /// </param>
    public async Task ShutdownAsync(
        TimeSpan timeout,
        CancellationToken cancellationToken = default,
        TimeSpan? maxBackgroundOperationWait = null)
    {
        if (Interlocked.Exchange(ref _shutdownCompleted, 1) == 1)
        {
            return;
        }

        BackgroundOperationTracker.RequestShutdown();
        ApplicationShutdownState.ShutdownRequested = true;
        IsShuttingDown = true;
        _deferredStartup.Cancel();
        _runtimeStateService.StopPolling();
        _runtimeMetricsService.StopPolling();
        _serviceManager.PrepareForShutdown();

        try
        {
            await BackgroundOperationTracker.WaitForCompletionAsync(
                maxBackgroundOperationWait ?? TimeSpan.FromMinutes(3),
                cancellationToken,
                active =>
                {
                    if (active > 0)
                    {
                        _shellViewModel.ShowShutdownOverlay(
                            active == 1
                                ? "Waiting for a background task to finish…"
                                : $"Waiting for {active} background tasks to finish…");
                    }
                }).ConfigureAwait(false);

            if (BackgroundOperationTracker.ActiveOperations > 0)
            {
                _diagnostics.LogUserError(
                    "Shutdown",
                    $"Continuing shutdown while {BackgroundOperationTracker.ActiveOperations} background task(s) are still running.");
            }
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            throw;
        }

        using var scope = _diagnostics.BeginAction("Shutdown", "Stop managed processes");

        using var timeoutCts = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        timeoutCts.CancelAfter(timeout);

        try
        {
            await StopAllManagedAsync(timeoutCts.Token).ConfigureAwait(false);
        }
        catch (OperationCanceledException) when (timeoutCts.IsCancellationRequested)
        {
            _diagnostics.LogActivity("Shutdown", "Shutdown timed out — releasing managed process jobs");
            await ReleaseManagedJobsAsync().ConfigureAwait(false);
        }
        catch (Exception ex)
        {
            _diagnostics.LogException("Shutdown", ex);
            await ReleaseManagedJobsAsync().ConfigureAwait(false);
        }
    }

    private async Task StopAllManagedAsync(CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();

        try
        {
            _diagnostics.LogActivity("Shutdown", "Leaving Test DNS to Stackroot DNS Helper (if enabled)…");
        }
        catch (Exception ex)
        {
            _diagnostics.LogException("Shutdown", ex);
        }

        try
        {
            _diagnostics.LogActivity("Shutdown", "Stopping global processes…");
            _shellViewModel.ShowShutdownOverlay("Stopping processes…");
            var procWatch = System.Diagnostics.Stopwatch.StartNew();
            
            _globalProcessManager.StopAll();
            _diagnostics.LogActivity("Shutdown", $"Global processes stopped in {procWatch.ElapsedMilliseconds}ms");
        }
        catch (Exception ex)
        {
            _diagnostics.LogActivity("Shutdown", $"Global processes exception: {ex.Message}");
            _diagnostics.LogException("Shutdown", ex);
        }

        try
        {
            _diagnostics.LogActivity("Shutdown", "Stopping Mailpit…");
            _shellViewModel.ShowShutdownOverlay("Stopping Mailpit…");
            var mailpitWatch = System.Diagnostics.Stopwatch.StartNew();
            await _mailpitManager.StopForShutdownAsync(cancellationToken).ConfigureAwait(false);
            _diagnostics.LogActivity("Shutdown", $"Mailpit stopped in {mailpitWatch.ElapsedMilliseconds}ms");
        }
        catch (Exception ex)
        {
            _diagnostics.LogActivity("Shutdown", $"Mailpit exception: {ex.Message}");
            _diagnostics.LogException("Shutdown", ex);
        }

        try
        {
            _diagnostics.LogActivity("Shutdown", "Stopping managed services…");
            var svcWatch = System.Diagnostics.Stopwatch.StartNew();
            await _serviceManager.StopAllForceQuickAsync(
                cancellationToken,
                onServiceStopping: serviceId =>
                {
                    _shellViewModel.ShowShutdownOverlay($"Closing {serviceId}…");
                }).ConfigureAwait(false);
            _diagnostics.LogActivity("Shutdown", $"Managed services stopped in {svcWatch.ElapsedMilliseconds}ms");
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            _diagnostics.LogActivity("Shutdown", "Service stop timed out — continuing shutdown");
        }
        catch (Exception ex)
        {
            _diagnostics.LogActivity("Shutdown", $"Service stop exception: {ex.Message}");
            _diagnostics.LogException("Shutdown", ex);
        }

        await ReleaseManagedJobsAsync().ConfigureAwait(false);
    }

    private async Task ReleaseManagedJobsAsync()
    {
        try
        {
            await _processJobManager.ShutdownAsync(CancellationToken.None).ConfigureAwait(false);
        }
        catch (Exception ex)
        {
            _diagnostics.LogException("Shutdown", ex);
        }
    }
}
