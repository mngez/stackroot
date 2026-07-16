namespace Stackroot.Core.Dns;

public sealed class DnsHelperHostedLoop : IAsyncDisposable
{
    private readonly DnsHelperEngine _engine = new();
    private FileSystemWatcher? _watcher;
    private readonly SemaphoreSlim _applyGate = new(1, 1);

    public async Task RunAsync(CancellationToken stoppingToken)
    {
        Directory.CreateDirectory(StackrootDnsHelperConstants.ConfigDirectory);
        StackrootDnsServiceInstaller.TryEnsureAutomaticStart(out _);
        StartWatcher();
        await ApplyLatestAsync(stoppingToken).ConfigureAwait(false);

        try
        {
            await Task.Delay(Timeout.Infinite, stoppingToken).ConfigureAwait(false);
        }
        catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
        {
        }
        finally
        {
            _watcher?.Dispose();
            _watcher = null;
        }
    }

    private void StartWatcher()
    {
        _watcher = new FileSystemWatcher(StackrootDnsHelperConstants.ConfigDirectory, StackrootDnsHelperConstants.ConfigFileName)
        {
            NotifyFilter = NotifyFilters.LastWrite | NotifyFilters.FileName | NotifyFilters.CreationTime,
            EnableRaisingEvents = true
        };

        FileSystemEventHandler handler = (_, _) => _ = ApplyLatestAsync(CancellationToken.None);
        _watcher.Changed += handler;
        _watcher.Created += handler;
        _watcher.Renamed += (_, _) => _ = ApplyLatestAsync(CancellationToken.None);
    }

    private async Task ApplyLatestAsync(CancellationToken cancellationToken)
    {
        await _applyGate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            await Task.Delay(100, cancellationToken).ConfigureAwait(false);
            var config = DnsHelperConfigStore.TryRead();
            if (config is null)
            {
                DnsHelperConfigStore.WriteStatus(new DnsHelperRuntimeStatus
                {
                    ListenerRunning = false,
                    NrptActive = false
                });
                return;
            }

            var hadOneShotToken = config.RestartToken.HasValue || config.FlushCacheToken.HasValue;
            try
            {
                await _engine.ApplyAsync(config, cancellationToken).ConfigureAwait(false);
                if (hadOneShotToken)
                {
                    DnsHelperConfigStore.ClearOneShotTokens(config);
                }
            }
            catch (Exception ex)
            {
                DnsHelperConfigStore.WriteStatus(_engine.GetStatus(config) with { LastError = ex.Message });
            }
        }
        finally
        {
            _applyGate.Release();
        }
    }

    public async ValueTask DisposeAsync() => await _engine.DisposeAsync().ConfigureAwait(false);
}
