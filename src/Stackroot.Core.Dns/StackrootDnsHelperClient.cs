namespace Stackroot.Core.Dns;

public sealed class StackrootDnsHelperClient
{
    public DnsHelperRuntimeStatus? ReadCachedStatus() => DnsHelperConfigStore.TryReadStatus();

    public Task PublishAndRefreshAsync(DnsHelperRuntimeConfig config, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(config);
        DnsHelperConfigStore.Publish(config);
        return Task.CompletedTask;
    }

    public async Task EnsureRunningAsync(DnsHelperRuntimeConfig config, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(config);
        await PublishAndRefreshAsync(config, cancellationToken).ConfigureAwait(false);

        if (TryReuseExistingService(config, out var reuseError))
        {
            if (reuseError is not null)
            {
                throw new InvalidOperationException(reuseError);
            }

            await WaitForHealthyAsync(config, TimeSpan.FromSeconds(20), cancellationToken).ConfigureAwait(false);
            return;
        }

        var helperPath = DnsHelperDeployer.EnsureDeployed(AppContext.BaseDirectory);
        if (!StackrootDnsServiceInstaller.TryEnsureInstalled(helperPath, out var installError))
        {
            throw new InvalidOperationException(installError ?? "Could not install the Stackroot DNS Helper service.");
        }

        if (!StackrootDnsServiceInstaller.TryStart(out var startError))
        {
            throw new InvalidOperationException(startError ?? "Could not start the Stackroot DNS Helper service.");
        }

        await WaitForHealthyAsync(config, TimeSpan.FromSeconds(20), cancellationToken).ConfigureAwait(false);
    }

    /// <summary>
    /// Dev and steady-state fast path: publish config only when the running service already has the same helper build.
    /// </summary>
    private static bool TryReuseExistingService(DnsHelperRuntimeConfig config, out string? error)
    {
        error = null;
        if (!StackrootDnsServiceInstaller.IsInstalled())
        {
            return false;
        }

        var configuredExe = StackrootDnsServiceInstaller.TryGetConfiguredHelperExePath();
        if (configuredExe is null || !File.Exists(configuredExe))
        {
            return false;
        }

        var sourceExe = DnsHelperDeployer.ResolveSourceHelperExePath(AppContext.BaseDirectory);
        if (!File.Exists(sourceExe))
        {
            return false;
        }

        var helperUnchanged = DnsHelperBuildIdentity.FilesMatch(sourceExe, configuredExe);
        var isDevLayout = StackrootDnsHelperLayout.IsDevLayout(AppContext.BaseDirectory);

        if (isDevLayout && StackrootDnsServiceInstaller.IsRunning())
        {
            return true;
        }

        if (!helperUnchanged)
        {
            return false;
        }

        if (!config.Enabled || !config.Listen)
        {
            return StackrootDnsServiceInstaller.IsRunning();
        }

        if (StackrootDnsServiceInstaller.IsRunning())
        {
            return true;
        }

        if (!StackrootDnsServiceInstaller.TryStart(out error))
        {
            return false;
        }

        return true;
    }

    public async Task WaitForHealthyAsync(
        DnsHelperRuntimeConfig config,
        TimeSpan timeout,
        CancellationToken cancellationToken = default)
    {
        var deadline = DateTimeOffset.UtcNow + timeout;
        while (DateTimeOffset.UtcNow < deadline)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var status = ReadCachedStatus();
            if (status is not null && MatchesDesiredState(config, status))
            {
                if (string.IsNullOrWhiteSpace(status.LastError))
                {
                    return;
                }

                throw new InvalidOperationException(status.LastError);
            }

            await Task.Delay(250, cancellationToken).ConfigureAwait(false);
        }

        var last = ReadCachedStatus();
        throw new InvalidOperationException(last?.LastError ?? "Timed out waiting for the DNS helper to become ready.");
    }

    private static bool MatchesDesiredState(DnsHelperRuntimeConfig config, DnsHelperRuntimeStatus status)
    {
        if (!config.Enabled)
        {
            return !status.ListenerRunning && !status.NrptActive;
        }

        if (!config.Listen)
        {
            return !status.ListenerRunning && !status.NrptActive;
        }

        if (!status.NrptActive)
        {
            return false;
        }

        return status.ListenerRunning;
    }
}
