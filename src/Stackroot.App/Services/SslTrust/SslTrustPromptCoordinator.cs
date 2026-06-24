using System.Windows;
using System.Windows.Threading;
using Stackroot.App.Helpers;
using Stackroot.App.Services;
using Stackroot.App.ViewModels;
using Stackroot.Core.Abstractions;
using Stackroot.Core.Nginx;
using Stackroot.Core.Settings;
using Stackroot.Core.Sites.Management;

namespace Stackroot.App.Services.SslTrust;

public sealed class SslTrustPromptCoordinator
{
    private static readonly TimeSpan PostStartupDelay = TimeSpan.FromSeconds(2);

    private readonly StackrootPaths _paths;
    private readonly SettingsStore _settingsStore;
    private readonly SiteManager _siteManager;
    private readonly SslTrustStateStore _stateStore;
    private readonly SslTrustPromptViewModel _viewModel;
    private readonly BackgroundWorkQueue _workQueue;
    private readonly DeferredStartupCoordinator _deferredStartup;
    private readonly IDiagnosticsReporter _diagnostics;
    private readonly Dispatcher _dispatcher;
    private bool _started;

    public SslTrustPromptCoordinator(
        StackrootPaths paths,
        SettingsStore settingsStore,
        SiteManager siteManager,
        SslTrustStateStore stateStore,
        SslTrustPromptViewModel viewModel,
        BackgroundWorkQueue workQueue,
        DeferredStartupCoordinator deferredStartup,
        IDiagnosticsReporter diagnostics)
    {
        _paths = paths;
        _settingsStore = settingsStore;
        _siteManager = siteManager;
        _stateStore = stateStore;
        _viewModel = viewModel;
        _workQueue = workQueue;
        _deferredStartup = deferredStartup;
        _diagnostics = diagnostics;
        _dispatcher = Application.Current.Dispatcher;
        _viewModel.Bind(TrustCertificateAsync, Dismiss);
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

    public void ScheduleCheck()
    {
        _workQueue.Enqueue("SSL", "Check local HTTPS trust", CheckAsync);
    }

    public void Dismiss()
    {
        var thumbprint = DevSslCertificateManager.GetLocalCaThumbprint(_paths);
        if (!string.IsNullOrWhiteSpace(thumbprint))
        {
            _stateStore.SetDismissedCaThumbprint(thumbprint);
        }

        RunOnUi(_viewModel.ClearBanner);
        _diagnostics.LogActivity("SSL", "Trust prompt dismissed");
    }

    public async Task TrustCertificateAsync()
    {
        if (_viewModel.IsBusy)
        {
            return;
        }

        RunOnUi(() => _viewModel.SetBusy(GetTrustBusyMessage()));

        try
        {
            var result = await Task.Run(_siteManager.TrustDevSslCertificate).ConfigureAwait(true);
            if (result.Ok)
            {
                RunOnUi(_viewModel.ClearBanner);
                _diagnostics.LogActivity("SSL", result.Message ?? "Local CA trusted.");
            }
            else
            {
                RunOnUi(() =>
                {
                    _viewModel.FinishBusy();
                    _viewModel.SetStatus(SessionActivityMessages.SslCertificateTrust(false, result.Message));
                });
            }
        }
        catch (Exception ex)
        {
            _diagnostics.LogException("SSL", ex);
            RunOnUi(() =>
            {
                _viewModel.FinishBusy();
                _viewModel.SetStatus(ex.Message);
            });
        }
    }

    private void OnDeferredStartupCompleted()
    {
        ScheduleCheck();
    }

    private async Task CheckAsync(CancellationToken cancellationToken)
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

            if (!ShouldShowPrompt())
            {
                RunOnUi(_viewModel.ClearBanner);
                return;
            }

            _diagnostics.LogActivity("SSL", "Local HTTPS CA is not trusted — showing trust prompt");
            RunOnUi(_viewModel.PresentPrompt);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception ex)
        {
            _diagnostics.LogException("SSL", ex);
        }
    }

    private bool ShouldShowPrompt()
    {
        if (!OperatingSystem.IsWindows())
        {
            return false;
        }

        if (!IsNginxSslEnabled())
        {
            return false;
        }

        if (!DevSslCertificateManager.ShouldPromptForLocalCaTrust(_paths))
        {
            return false;
        }

        var thumbprint = DevSslCertificateManager.GetLocalCaThumbprint(_paths);
        if (string.IsNullOrWhiteSpace(thumbprint))
        {
            return false;
        }

        var dismissed = _stateStore.GetDismissedCaThumbprint();
        return !string.Equals(dismissed, thumbprint, StringComparison.OrdinalIgnoreCase);
    }

    private bool IsNginxSslEnabled()
    {
        var settings = _settingsStore.Load();
        if (!settings.Services.TryGetValue(ServiceId.Nginx, out var nginx))
        {
            return true;
        }

        return nginx.SslEnabled != false;
    }

    private string GetTrustBusyMessage()
    {
        var machineWide = _settingsStore.Load().General.TrustSslCaMachineWide ?? false;
        return machineWide
            ? "Installing local CA to Windows trusted roots (all users)…"
            : "Installing local CA to Windows trusted roots (current user)…";
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
}
