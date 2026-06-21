using Stackroot.Core.Abstractions;
using Stackroot.Core.Settings;
using Stackroot.Core.Windows.Dns;

namespace Stackroot.App.Services;

public sealed record TestDnsStatus(bool Enabled, bool Running, bool NrptActive, string? Message);

public sealed class TestDnsCoordinator
{
    private readonly TestDnsServer _server = new();
    private readonly WindowsNrptManager _nrpt = new();
    private readonly SettingsStore _settingsStore;
    private readonly IDiagnosticsReporter? _diagnostics;

    public TestDnsCoordinator(SettingsStore settingsStore, IDiagnosticsReporter? diagnostics = null)
    {
        _settingsStore = settingsStore;
        _diagnostics = diagnostics;
    }

    public event EventHandler? StatusChanged;

    public bool IsActive => _settingsStore.Load().TestDns.Enabled && _server.IsRunning;

    public string? LastError => _server.LastError;

    public TestDnsStatus GetStatus()
    {
        var enabled = _settingsStore.Load().TestDns.Enabled;
        return new TestDnsStatus(
            enabled,
            _server.IsRunning,
            enabled && _nrpt.IsRulePresent(),
            _server.LastError);
    }

    public async Task<TestDnsStatus> EnsureAutoStartAsync(CancellationToken cancellationToken = default)
    {
        if (!_settingsStore.Load().TestDns.Enabled)
        {
            _diagnostics?.LogActivity("Test DNS", "Auto-start skipped (disabled in settings)");
            return GetStatus();
        }

        if (!_settingsStore.Load().TestDns.AutoStart)
        {
            _diagnostics?.LogActivity("Test DNS", "Auto-start skipped (auto-start off in settings)");
            return GetStatus();
        }

        var status = GetStatus();
        if (status.Running && status.NrptActive)
        {
            _diagnostics?.LogActivity("Test DNS", "Auto-start skipped (already running)");
            NotifyStatusChanged();
            return status;
        }

        _diagnostics?.LogActivity("Test DNS", "Auto-starting local .test DNS");
        try
        {
            await EnableAsync(cancellationToken).ConfigureAwait(false);
        }
        catch (Exception ex)
        {
            _diagnostics?.LogUserError("Test DNS", ex.Message);
        }

        NotifyStatusChanged();
        return GetStatus();
    }

    public async Task ApplySettingsAsync(CancellationToken cancellationToken = default)
    {
        if (_settingsStore.Load().TestDns.Enabled)
        {
            await EnableAsync(cancellationToken).ConfigureAwait(false);
        }
        else
        {
            await DisableAsync(cancellationToken).ConfigureAwait(false);
        }
    }

    public async Task EnableAsync(CancellationToken cancellationToken = default)
    {
        try
        {
            await _server.StartAsync(cancellationToken).ConfigureAwait(false);
        }
        catch (Exception ex)
        {
            _diagnostics?.LogUserError("Test DNS", $"Could not start local .test DNS: {ex.Message}");
            NotifyStatusChanged();
            throw;
        }

        if (!_nrpt.TryEnable(out var nrptError))
        {
            await _server.StopAsync().ConfigureAwait(false);
            var message = nrptError ?? "Could not register .test DNS routing.";
            _diagnostics?.LogUserError("Test DNS", message);
            NotifyStatusChanged();
            throw new InvalidOperationException(message);
        }

        _diagnostics?.LogActivity("Test DNS", "Local .test DNS enabled (127.0.0.1:53 + NRPT)");
        NotifyStatusChanged();
    }

    public async Task DisableAsync(CancellationToken cancellationToken = default)
    {
        _ = cancellationToken;
        _nrpt.TryDisable(out var nrptError);
        if (!string.IsNullOrWhiteSpace(nrptError))
        {
            _diagnostics?.LogUserError("Test DNS", nrptError);
        }

        await _server.StopAsync().ConfigureAwait(false);
        _diagnostics?.LogActivity("Test DNS", "Local .test DNS disabled");
        NotifyStatusChanged();
    }

    /// <summary>
    /// Stops the local resolver only. Keeps the NRPT rule so Start/Restart do not need admin again.
    /// </summary>
    public async Task StopRuntimeAsync(CancellationToken cancellationToken = default)
    {
        _ = cancellationToken;
        await _server.StopAsync().ConfigureAwait(false);
        _diagnostics?.LogActivity("Test DNS", "Local .test DNS listener stopped");
        NotifyStatusChanged();
    }

    /// <summary>
    /// Restarts the UDP listener without removing or re-adding the NRPT rule (no extra UAC prompts).
    /// </summary>
    public async Task RestartRuntimeAsync(CancellationToken cancellationToken = default)
    {
        await _server.StopAsync().ConfigureAwait(false);
        try
        {
            await _server.StartAsync(cancellationToken).ConfigureAwait(false);
        }
        catch (Exception ex)
        {
            _diagnostics?.LogUserError("Test DNS", $"Could not restart local .test DNS: {ex.Message}");
            NotifyStatusChanged();
            throw;
        }

        if (!_nrpt.IsRulePresent())
        {
            if (!_nrpt.TryEnable(out var nrptError))
            {
                await _server.StopAsync().ConfigureAwait(false);
                var message = nrptError ?? "Could not register .test DNS routing.";
                _diagnostics?.LogUserError("Test DNS", message);
                NotifyStatusChanged();
                throw new InvalidOperationException(message);
            }
        }

        _diagnostics?.LogActivity("Test DNS", "Local .test DNS restarted");
        NotifyStatusChanged();
    }

    public async Task StopForShutdownAsync()
    {
        await _server.StopAsync().ConfigureAwait(false);
    }

    private void NotifyStatusChanged() => StatusChanged?.Invoke(this, EventArgs.Empty);
}
