using Stackroot.Core.Abstractions;
using Stackroot.Core.Settings;

namespace Stackroot.Core.Nginx;

/// <summary>
/// Ensures HTTPS material and nginx configuration are consistent before nginx starts or reloads.
/// </summary>
public sealed class NginxWebStackCoordinator
{
    private readonly StackrootPaths _paths;
    private readonly SettingsStore _settingsStore;
    private readonly IDiagnosticsReporter? _diagnostics;
    private readonly Func<CancellationToken, Task>? _regenerateSitesAsync;
    private readonly SemaphoreSlim _sync = new(1, 1);
    private DateTimeOffset _lastPreparedAt;
    private bool _mainConfigPrepared;

    public NginxWebStackCoordinator(
        StackrootPaths paths,
        SettingsStore settingsStore,
        Func<CancellationToken, Task>? regenerateSitesAsync = null,
        IDiagnosticsReporter? diagnostics = null)
    {
        _paths = paths;
        _settingsStore = settingsStore;
        _regenerateSitesAsync = regenerateSitesAsync;
        _diagnostics = diagnostics;
    }

    public Task PrepareForNginxAsync(CancellationToken cancellationToken = default)
        => PrepareForNginxAsync(writeMainConfig: true, cancellationToken);

    public async Task PrepareForNginxAsync(bool writeMainConfig, CancellationToken cancellationToken = default)
    {
        await _sync.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            if (_regenerateSitesAsync is not null)
            {
                await _regenerateSitesAsync(cancellationToken).ConfigureAwait(false);
            }

            if (writeMainConfig)
            {
                WriteMainNginxConfig();
                _mainConfigPrepared = true;
            }

            _lastPreparedAt = DateTimeOffset.UtcNow;
        }
        finally
        {
            _sync.Release();
        }
    }

    public bool WasMainConfigPreparedRecently(TimeSpan window)
        => _mainConfigPrepared && DateTimeOffset.UtcNow - _lastPreparedAt < window;

    public void WriteMainNginxConfig()
    {
        var settings = _settingsStore.Load();
        if (!settings.Services.TryGetValue(ServiceId.Nginx, out var nginxSettings))
        {
            return;
        }

        var definition = SettingsDefaults.ServiceDefinitions.First(static d => d.Id == ServiceId.Nginx);
        var packageId = nginxSettings.PackageId ?? definition.PackageId;
        if (string.IsNullOrWhiteSpace(packageId))
        {
            return;
        }

        NginxRuntime.writeNginxConfig(_paths, nginxSettings, settings.NginxHttp);
        _diagnostics?.LogActivity(
            "Nginx",
            settings.NginxHttp.ManageMainConfigManually
                ? "Main nginx.conf is managed manually — skipped auto-generation."
                : DevSslCertificateManager.CertificatesExist(_paths)
                    ? "HTTPS certificates ready — nginx config updated."
                    : "nginx config updated (HTTP only until HTTPS certificates are available).");
    }
}
