using System.Diagnostics;
using System.IO;
using System.Windows;
using System.Windows.Threading;
using Stackroot.App.ViewModels;
using Stackroot.Core.Abstractions;

namespace Stackroot.App.Services.AppUpdate;

public sealed class AppUpdateCoordinator : IDisposable
{
    private static readonly TimeSpan PostStartupDelay = TimeSpan.FromSeconds(5);
    private static readonly TimeSpan PeriodicCheckInterval = TimeSpan.FromHours(6);

    private readonly GitHubAppReleaseClient _releaseClient;
    private readonly AppUpdateStateStore _stateStore;
    private readonly AppUpdateViewModel _viewModel;
    private readonly StackrootShutdownCoordinator _shutdown;
    private readonly BackgroundWorkQueue _workQueue;
    private readonly DeferredStartupCoordinator _deferredStartup;
    private readonly IDiagnosticsReporter _diagnostics;
    private readonly Dispatcher _dispatcher;
    private bool _started;
    private int _periodicTimerStarted;
    private System.Threading.Timer? _periodicTimer;
    private AppReleaseInfo? _pendingRelease;
    private string? _downloadedInstallerPath;

    public AppUpdateCoordinator(
        GitHubAppReleaseClient releaseClient,
        AppUpdateStateStore stateStore,
        AppUpdateViewModel viewModel,
        StackrootShutdownCoordinator shutdown,
        BackgroundWorkQueue workQueue,
        DeferredStartupCoordinator deferredStartup,
        IDiagnosticsReporter diagnostics)
    {
        _releaseClient = releaseClient;
        _stateStore = stateStore;
        _viewModel = viewModel;
        _shutdown = shutdown;
        _workQueue = workQueue;
        _deferredStartup = deferredStartup;
        _diagnostics = diagnostics;
        _dispatcher = Application.Current.Dispatcher;
        _viewModel.Bind(ApplyUpdateAsync, Dismiss);
    }

    public void Start()
    {
        if (_started)
        {
            return;
        }

        _started = true;
        _deferredStartup.Completed += OnDeferredStartupCompleted;
    }

    public void Dismiss()
    {
        if (_pendingRelease is null)
        {
            RunOnUi(_viewModel.ClearBanner);
            return;
        }

        _stateStore.SetDismissedVersion(_pendingRelease.Version);
        _pendingRelease = null;
        RunOnUi(_viewModel.ClearBanner);
        _diagnostics.LogActivity("AppUpdate", "Update banner dismissed");
    }

    public async Task ApplyUpdateAsync()
    {
        if (_pendingRelease is null || _viewModel.IsBusy)
        {
            return;
        }

        var release = _pendingRelease;
        RunOnUi(() => _viewModel.SetBusy("Downloading update…"));

        try
        {
            var installerPath = await EnsureInstallerDownloadedAsync(release).ConfigureAwait(true);
            RunOnUi(() => _viewModel.SetStatus("Closing Stackroot and starting installer…"));

            if (Application.Current.MainWindow is MainWindow mainWindow)
            {
                await mainWindow.QuitForUpdateInstallerAsync(installerPath).ConfigureAwait(true);
                return;
            }

            await _shutdown.ShutdownAsync(StackrootShutdownCoordinator.DefaultShutdownTimeout)
                .ConfigureAwait(true);
            Process.Start(new ProcessStartInfo(installerPath) { UseShellExecute = true });
            Application.Current.Shutdown();
        }
        catch (Exception ex)
        {
            _diagnostics.LogException("AppUpdate", ex);
            RunOnUi(() =>
            {
                _viewModel.FinishBusy();
                _viewModel.SetStatus($"Update failed: {ex.Message}");
            });
        }
    }

    private void OnDeferredStartupCompleted()
    {
        _workQueue.Enqueue(
            "Startup",
            "Check for Stackroot updates",
            CheckForUpdatesAsync);
    }

    private async Task CheckForUpdatesAsync(CancellationToken cancellationToken)
    {
        if (ApplicationShutdownState.IsClosing)
        {
            return;
        }

        try
        {
            await Task.Delay(PostStartupDelay, cancellationToken).ConfigureAwait(false);

            if (ApplicationShutdownState.IsClosing)
            {
                return;
            }

            var currentVersion = AppVersion.Current;
            var release = await _releaseClient.GetLatestReleaseAsync(cancellationToken: cancellationToken)
                .ConfigureAwait(false);
            if (release is null)
            {
                _diagnostics.LogActivity("AppUpdate", "No release info returned");
                return;
            }

            if (!AppVersion.IsRemoteNewer(release.Version, currentVersion))
            {
                _diagnostics.LogActivity("AppUpdate", $"Up to date ({currentVersion})");
                return;
            }

            var dismissed = _stateStore.GetDismissedVersion();
            if (string.Equals(dismissed, release.Version, StringComparison.OrdinalIgnoreCase))
            {
                _diagnostics.LogActivity("AppUpdate", $"Update {release.Version} previously dismissed");
                return;
            }

            _pendingRelease = release;
            _diagnostics.LogActivity("AppUpdate", $"Update available: {release.Version} (current {currentVersion})");
            RunOnUi(() => _viewModel.PresentUpdate(currentVersion, release.Version));
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception ex)
        {
            _diagnostics.LogException("AppUpdate", ex);
        }
        finally
        {
            StartPeriodicTimer();
        }
    }

    private void StartPeriodicTimer()
    {
        if (Interlocked.CompareExchange(ref _periodicTimerStarted, 1, 0) != 0)
        {
            return;
        }

        _periodicTimer = new System.Threading.Timer(
            _ => SchedulePeriodicCheck(),
            null,
            PeriodicCheckInterval,
            PeriodicCheckInterval);
    }

    private void SchedulePeriodicCheck()
    {
        if (ApplicationShutdownState.IsClosing)
        {
            return;
        }

        _workQueue.Enqueue(
            "AppUpdate",
            "Periodic update check",
            CheckForUpdatesAsync);
    }

    private async Task<string> EnsureInstallerDownloadedAsync(AppReleaseInfo release)
    {
        if (!string.IsNullOrWhiteSpace(_downloadedInstallerPath) && File.Exists(_downloadedInstallerPath))
        {
            return _downloadedInstallerPath;
        }

        var directory = Path.Combine(Path.GetTempPath(), "Stackroot", "updates");
        var installerPath = Path.Combine(directory, $"Stackroot-Setup-{release.Version}.exe");
        if (File.Exists(installerPath) && new FileInfo(installerPath).Length > 0)
        {
            _downloadedInstallerPath = installerPath;
            return installerPath;
        }

        var progress = new Progress<double>(value =>
        {
            var percent = Math.Clamp((int)Math.Round(value * 100), 0, 100);
            RunOnUi(() => _viewModel.SetStatus($"Downloading update… {percent}%"));
        });

        await _releaseClient
            .DownloadInstallerAsync(release.InstallerDownloadUrl, installerPath, progress)
            .ConfigureAwait(false);

        if (!File.Exists(installerPath) || new FileInfo(installerPath).Length == 0)
        {
            throw new InvalidOperationException("Downloaded installer is missing or empty.");
        }

        _downloadedInstallerPath = installerPath;
        return installerPath;
    }

    private void RunOnUi(Action action)
    {
        if (_dispatcher.CheckAccess())
        {
            action();
            return;
        }

        _dispatcher.Invoke(action);
    }

    public void Dispose()
    {
        _periodicTimer?.Dispose();
        _periodicTimer = null;
    }
}
