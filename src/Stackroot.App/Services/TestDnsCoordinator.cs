using Stackroot.Core.Abstractions;
using Stackroot.Core.Settings;
using Stackroot.Core.Windows.Dns;

namespace Stackroot.App.Services;

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

    public bool IsActive => _settingsStore.Load().Sites.TestDnsEnabled && _server.IsRunning;

    public string? LastError => _server.LastError;

    public async Task ApplySettingsAsync(CancellationToken cancellationToken = default)
    {
        if (_settingsStore.Load().Sites.TestDnsEnabled)
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
            throw;
        }

        if (!_nrpt.TryEnable(out var nrptError))
        {
            await _server.StopAsync().ConfigureAwait(false);
            var message = nrptError ?? "Could not register .test DNS routing.";
            _diagnostics?.LogUserError("Test DNS", message);
            throw new InvalidOperationException(message);
        }

        _diagnostics?.LogActivity("Test DNS", "Local .test DNS enabled (127.0.0.1:53 + NRPT)");
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
    }

    public async Task StopForShutdownAsync()
    {
        await _server.StopAsync().ConfigureAwait(false);
    }
}
