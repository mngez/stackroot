using Stackroot.Core.Abstractions;

namespace Stackroot.Core.Dns;

/// <summary>
/// Owns the local DNS listener and NRPT rules inside the DNS helper service.
/// </summary>
public sealed class DnsHelperEngine : IAsyncDisposable
{
    private readonly TestDnsServer _server = new();
    private readonly WindowsNrptManager _nrpt = new();
    private readonly object _gate = new();
    private TestDnsQueryLogger? _queryLogger;
    private string? _lastError;
    private Guid? _lastAppliedRestartToken;
    private Guid? _lastAppliedFlushCacheToken;

    public bool IsListenerRunning => _server.IsRunning;

    public string? LastError
    {
        get
        {
            lock (_gate)
            {
                return _lastError ?? _server.LastError;
            }
        }
    }

    public async Task ApplyAsync(DnsHelperRuntimeConfig config, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(config);
        cancellationToken.ThrowIfCancellationRequested();

        // Consumed exactly once per distinct token, regardless of which branch
        // below handles this apply - a restart request followed immediately by
        // a disable/stop must not leave a stale "pending restart" around.
        var forceRestart = config.RestartToken.HasValue && config.RestartToken != _lastAppliedRestartToken;
        _lastAppliedRestartToken = config.RestartToken;

        var flushCache = config.FlushCacheToken.HasValue && config.FlushCacheToken != _lastAppliedFlushCacheToken;
        _lastAppliedFlushCacheToken = config.FlushCacheToken;
        if (flushCache)
        {
            // The forward cache lives on the server instance independent of the
            // listener state, so flushing is safe in every branch below.
            _server.FlushForwardCache();
        }

        lock (_gate)
        {
            _lastError = null;
        }

        if (!config.Enabled)
        {
            await StopListenerAsync(cancellationToken).ConfigureAwait(false);
            if (!_nrpt.TryDisable(out var disableError, allowElevation: false))
            {
                SetError(disableError ?? "Could not remove Stackroot DNS routing rules.");
            }

            PublishStatus(config, listenerRunning: false);
            return;
        }

        var allowDangerous = LocalDnsSuffix.ContainsCatchAll(config.Suffixes);
        var suffixes = LocalDnsSuffix.NormalizeList(config.Suffixes, ensureDefaultTest: !allowDangerous, allowDangerous: allowDangerous);
        if (!config.Listen)
        {
            await StopListenerAsync(cancellationToken).ConfigureAwait(false);
            ConfigureLogging(config);
            if (!_nrpt.TryDisable(out var disableError, allowElevation: false))
            {
                SetError(disableError ?? "Could not remove Stackroot DNS routing rules.");
            }

            PublishStatus(
                config,
                listenerRunning: false,
                nrptActive: _nrpt.AreAllRulesPresent(suffixes));
            return;
        }

        if (!_nrpt.TrySyncRules(suffixes, out var nrptError, allowElevation: false))
        {
            await StopListenerAsync(cancellationToken).ConfigureAwait(false);
            var message = nrptError ?? "Could not register dev DNS routing rules.";
            SetError(message);
            PublishStatus(config, listenerRunning: false, nrptActive: _nrpt.AreAllRulesPresent(suffixes));
            return;
        }

        var options = LocalDnsServerOptions.Create(suffixes, config.LocalNames, config.ResolveAddress);
        _server.Configure(options);
        ConfigureLogging(config);

        try
        {
            if (forceRestart)
            {
                // StartAsync() no-ops when the listener already believes it's
                // running - which is exactly the state a wedged-but-not-crashed
                // receive loop leaves it in. An explicit restart request must
                // force a real stop (cancel + dispose the socket) before the
                // rebind, or "Restart" would never actually touch a stuck listener.
                await _server.StopAsync().ConfigureAwait(false);
            }

            await _server.StartAsync(cancellationToken).ConfigureAwait(false);
        }
        catch (Exception ex)
        {
            if (!_nrpt.TryDisable(out var rollbackError, allowElevation: false))
            {
                SetError(
                    $"Could not start local dev DNS: {ex.Message}. Routing cleanup also failed: {rollbackError}");
            }
            else
            {
                SetError($"Could not start local dev DNS: {ex.Message}");
            }

            PublishStatus(config, listenerRunning: false, nrptActive: false);
            throw;
        }

        PublishStatus(config, listenerRunning: true, nrptActive: true);
    }

    public DnsHelperRuntimeStatus GetStatus(DnsHelperRuntimeConfig? config)
    {
        var allowDangerous = LocalDnsSuffix.ContainsCatchAll(config?.Suffixes);
        var suffixes = LocalDnsSuffix.NormalizeList(
            config?.Suffixes ?? [".test"],
            ensureDefaultTest: !allowDangerous,
            allowDangerous: allowDangerous);
        return new DnsHelperRuntimeStatus
        {
            ListenerRunning = _server.IsRunning,
            NrptActive = config?.Enabled == true && _nrpt.AreAllRulesPresent(suffixes),
            LastError = LastError,
            UpdatedAt = DateTimeOffset.UtcNow
        };
    }

    private void ConfigureLogging(DnsHelperRuntimeConfig config)
    {
        if (config.LogRequests && !string.IsNullOrWhiteSpace(config.LogsRoot))
        {
            var paths = new StackrootPaths
            {
                DataRoot = config.DataRoot,
                LogsRoot = config.LogsRoot
            };
            _queryLogger ??= new TestDnsQueryLogger(paths);
            _server.SetQueryLogger(_queryLogger);
            return;
        }

        _server.SetQueryLogger(null);
        _queryLogger?.Dispose();
        _queryLogger = null;
    }

    private async Task StopListenerAsync(CancellationToken cancellationToken)
    {
        _server.SetQueryLogger(null);
        _queryLogger?.Dispose();
        _queryLogger = null;
        await _server.StopAsync().ConfigureAwait(false);
        cancellationToken.ThrowIfCancellationRequested();
    }

    private void PublishStatus(
        DnsHelperRuntimeConfig config,
        bool listenerRunning,
        bool? nrptActive = null,
        string? lastError = null)
    {
        var allowDangerous = LocalDnsSuffix.ContainsCatchAll(config.Suffixes);
        var suffixes = LocalDnsSuffix.NormalizeList(config.Suffixes, ensureDefaultTest: !allowDangerous, allowDangerous: allowDangerous);
        DnsHelperConfigStore.WriteStatus(new DnsHelperRuntimeStatus
        {
            ListenerRunning = listenerRunning,
            NrptActive = nrptActive ?? (config.Enabled && _nrpt.AreAllRulesPresent(suffixes)),
            LastError = lastError ?? LastError,
            UpdatedAt = DateTimeOffset.UtcNow
        });
    }

    private void SetError(string message)
    {
        lock (_gate)
        {
            _lastError = message;
        }
    }

    public async ValueTask DisposeAsync()
    {
        _queryLogger?.Dispose();
        await _server.DisposeAsync().ConfigureAwait(false);
    }
}
