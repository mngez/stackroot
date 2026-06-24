using Stackroot.Core.Abstractions;
using Stackroot.Core.Settings;
using Stackroot.Core.Sites;
using Stackroot.Core.Sites.Management;
using Stackroot.Core.Dns;

namespace Stackroot.App.Services;

public sealed record TestDnsStatus(bool Enabled, bool Running, bool NrptActive, string? Message);

public sealed class TestDnsCoordinator
{
    private static readonly TimeSpan RuntimeStatusTrustTtl = TimeSpan.FromSeconds(45);

    private readonly StackrootDnsHelperClient _client = new();
    private readonly WindowsNrptManager _nrpt = new();
    private readonly SettingsStore _settingsStore;
    private readonly SiteManager _siteManager;
    private readonly StackrootPaths _paths;
    private readonly IDiagnosticsReporter? _diagnostics;

    public TestDnsCoordinator(
        SettingsStore settingsStore,
        SiteManager siteManager,
        StackrootPaths paths,
        IDiagnosticsReporter? diagnostics = null)
    {
        _settingsStore = settingsStore;
        _siteManager = siteManager;
        _paths = paths;
        _diagnostics = diagnostics;
    }

    public event EventHandler? StatusChanged;

    public bool IsActive
    {
        get
        {
            var settings = _settingsStore.Load().TestDns;
            if (!settings.Enabled)
            {
                return false;
            }

            return GetStatus().Running;
        }
    }

    public string? LastError => _client.ReadCachedStatus()?.LastError;

    /// <summary>
    /// File-only status for polling — reconciles stale helper status when the Windows service is stopped.
    /// </summary>
    public TestDnsStatus GetCachedStatus()
    {
        var settings = _settingsStore.Load().TestDns;
        var suffixes = NormalizeConfiguredSuffixes(settings);
        var serviceRunning = CheckHelperServiceRunning();
        SyncStaleRuntimeStatusIfNeeded(settings, suffixes, serviceRunning);
        return BuildStatus(settings, suffixes, serviceRunning, probeNrpt: false);
    }

    public TestDnsStatus GetStatus()
    {
        var settings = _settingsStore.Load().TestDns;
        var suffixes = NormalizeConfiguredSuffixes(settings);
        var serviceRunning = CheckHelperServiceRunning();
        SyncStaleRuntimeStatusIfNeeded(settings, suffixes, serviceRunning);

        var runtime = _client.ReadCachedStatus();
        if (serviceRunning && IsRuntimeStatusFresh(runtime))
        {
            var listenerRunning = runtime!.ListenerRunning;
            var nrptRulesPresent = settings.Enabled && runtime.NrptActive;
            var nrptActive = settings.Enabled && nrptRulesPresent;
            var message = ResolveStatusMessage(settings.Enabled, listenerRunning, nrptRulesPresent, runtime.LastError);
            return new TestDnsStatus(settings.Enabled, listenerRunning, nrptActive, message);
        }

        return BuildStatus(settings, suffixes, serviceRunning, probeNrpt: true);
    }

    public async Task<TestDnsStatus> EnsureAutoStartAsync(CancellationToken cancellationToken = default)
    {
        var testDns = _settingsStore.Load().TestDns;
        if (!testDns.Enabled)
        {
            _diagnostics?.LogActivity("Test DNS", "Auto-start skipped (disabled in settings)");
            return GetStatus();
        }

        if (!testDns.AutoStart)
        {
            _diagnostics?.LogActivity("Test DNS", "Auto-start skipped (auto-start off in settings)");
            return GetStatus();
        }

        var status = GetStatus();
        if (IsHelperOperational(status))
        {
            _diagnostics?.LogActivity("Test DNS", "Auto-start skipped (already running)");
            NotifyStatusChanged();
            return status;
        }

        if (StackrootDnsServiceInstaller.IsInstalled()
            && !StackrootDnsServiceInstaller.IsRunning()
            && _client.ReadCachedStatus()?.ListenerRunning == true)
        {
            _diagnostics?.LogActivity("Test DNS", "Stale DNS helper status detected — restarting after stop or upgrade");
        }

        _diagnostics?.LogActivity("Test DNS", "Auto-starting local dev DNS via Stackroot DNS Helper");
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

        _siteManager.RefreshManagedHosts();
        _siteManager.RefreshDevSslCertificates();
    }

    public async Task EnableAsync(CancellationToken cancellationToken = default)
    {
        var config = BuildConfig(listen: true);
        try
        {
            await _client.EnsureRunningAsync(config, cancellationToken).ConfigureAwait(false);
        }
        catch (Exception ex)
        {
            _diagnostics?.LogUserError("Test DNS", ex.Message);
            NotifyStatusChanged();
            throw;
        }

        var suffixes = LocalDnsSuffix.NormalizeList(config.Suffixes);
        _diagnostics?.LogActivity(
            "Test DNS",
            $"Local dev DNS enabled via helper (127.0.0.1:53 + NRPT: {string.Join(", ", suffixes)})");
        NotifyStatusChanged();
    }

    public async Task DisableAsync(CancellationToken cancellationToken = default)
    {
        var config = BuildConfig(enabled: false, listen: false);
        try
        {
            StackrootDnsServiceInstaller.InvalidateServiceStateCache();
            if (CheckHelperServiceRunning())
            {
                await _client.EnsureRunningAsync(config, cancellationToken).ConfigureAwait(false);
            }
            else
            {
                await _client.PublishAndRefreshAsync(config, cancellationToken).ConfigureAwait(false);
            }

            if (!_nrpt.TryDisable(out var error, allowElevation: true))
            {
                throw new InvalidOperationException(error ?? "Could not remove Stackroot DNS routing rules.");
            }

            WriteStoppedStatus();
        }
        catch (Exception ex)
        {
            _diagnostics?.LogUserError("Test DNS", ex.Message);
            NotifyStatusChanged();
            throw;
        }

        _diagnostics?.LogActivity("Test DNS", "Local dev DNS disabled");
        NotifyStatusChanged();
    }

    public async Task EnsureRoutingConsistentAsync(CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        if (_settingsStore.Load().TestDns.Enabled || !_nrpt.HasAnyStackrootRules())
        {
            return;
        }

        _diagnostics?.LogActivity("Test DNS", "Removing leftover dev DNS routing rules (Test DNS is disabled)");
        if (await TryRemoveRoutingRulesAsync(cancellationToken).ConfigureAwait(false))
        {
            await _client.PublishAndRefreshAsync(BuildConfig(enabled: false, listen: false), cancellationToken)
                .ConfigureAwait(false);
            WriteStoppedStatus();
            NotifyStatusChanged();
            return;
        }

        await DisableAsync(cancellationToken).ConfigureAwait(false);
    }

    public async Task<(bool Ok, string? Error)> TryCleanupRoutingAsync(CancellationToken cancellationToken = default)
    {
        try
        {
            await DisableAsync(cancellationToken).ConfigureAwait(false);
            NotifyStatusChanged();
            return (true, null);
        }
        catch (Exception ex)
        {
            _diagnostics?.LogUserError("Test DNS", ex.Message);
            NotifyStatusChanged();
            return (false, ex.Message);
        }
    }

    public async Task StopRuntimeAsync(CancellationToken cancellationToken = default)
    {
        StackrootDnsServiceInstaller.InvalidateServiceStateCache();
        var config = BuildConfig(enabled: true, listen: false);
        await _client.PublishAndRefreshAsync(config, cancellationToken).ConfigureAwait(false);

        if (CheckHelperServiceRunning())
        {
            await _client.WaitForHealthyAsync(config, TimeSpan.FromSeconds(15), cancellationToken).ConfigureAwait(false);
        }
        else if (_nrpt.HasAnyStackrootRules())
        {
            if (!await TryRemoveRoutingRulesAsync(cancellationToken).ConfigureAwait(false))
            {
                throw new InvalidOperationException("Could not remove Stackroot DNS routing rules.");
            }

            WriteStoppedStatus();
        }
        else
        {
            WriteStoppedStatus();
        }

        _diagnostics?.LogActivity("Test DNS", "Local dev DNS stopped (listener and routing off)");
        NotifyStatusChanged();
    }

    public async Task RestartRuntimeAsync(CancellationToken cancellationToken = default)
    {
        var config = BuildConfig(enabled: true, listen: true);
        await _client.EnsureRunningAsync(config, cancellationToken).ConfigureAwait(false);
        _diagnostics?.LogActivity("Test DNS", "Local dev DNS restarted via helper");
        NotifyStatusChanged();
    }

    public Task RefreshServerConfigurationAsync(CancellationToken cancellationToken = default)
        => RefreshServerConfigurationCoreAsync(cancellationToken);

    /// <summary>
    /// App exit does not stop DNS — StackrootDnsHelper keeps NRPT and the listener when Test DNS is enabled.
    /// </summary>
    public Task StopForShutdownAsync() => Task.CompletedTask;

    private async Task RefreshServerConfigurationCoreAsync(CancellationToken cancellationToken)
    {
        var settings = _settingsStore.Load();
        if (!settings.TestDns.Enabled)
        {
            await _client.PublishAndRefreshAsync(BuildConfig(settings, enabled: false, listen: false), cancellationToken)
                .ConfigureAwait(false);
            return;
        }

        var listen = ResolveDesiredListenState(settings.TestDns);
        var config = BuildConfig(settings, listen: listen);
        await _client.PublishAndRefreshAsync(config, cancellationToken).ConfigureAwait(false);

        if (!StackrootDnsServiceInstaller.IsInstalled() || !StackrootDnsServiceInstaller.IsRunning())
        {
            return;
        }

        try
        {
            await _client.WaitForHealthyAsync(config, TimeSpan.FromSeconds(20), cancellationToken).ConfigureAwait(false);
            _diagnostics?.LogActivity("Test DNS", "DNS catalog refreshed in the helper");
        }
        catch (Exception ex)
        {
            _diagnostics?.LogUserError("Test DNS", $"DNS catalog was published but the helper did not apply it: {ex.Message}");
            throw;
        }
    }

    private async Task<bool> TryRemoveRoutingRulesAsync(CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        return await Task.Run(() => _nrpt.TryDisable(out _, allowElevation: true), cancellationToken).ConfigureAwait(false);
    }

    private bool ResolveDesiredListenState(TestDnsSettings? testDns = null)
    {
        testDns ??= _settingsStore.Load().TestDns;
        if (!testDns.Enabled)
        {
            return false;
        }

        var runtime = _client.ReadCachedStatus();
        if (runtime is { ListenerRunning: true })
        {
            return true;
        }

        if (runtime is { ListenerRunning: false, NrptActive: false })
        {
            return false;
        }

        return true;
    }

    private static void WriteStoppedStatus()
    {
        DnsHelperConfigStore.WriteStatus(new DnsHelperRuntimeStatus
        {
            ListenerRunning = false,
            NrptActive = false
        });
    }

    private DnsHelperRuntimeConfig BuildConfig(bool? enabled = null, bool? listen = null)
        => BuildConfig(_settingsStore.Load(), enabled, listen);

    private DnsHelperRuntimeConfig BuildConfig(AppSettings settings, bool? enabled = null, bool? listen = null)
    {
        var siteNames = _siteManager.List()
            .Where(static site => site.Enabled)
            .SelectMany(SiteDomainNames.GetServerNames)
            .ToList();

        var config = DnsHelperConfigStore.Build(
            _paths,
            settings.TestDns,
            settings.General.AppDomain ?? "stackroot.test",
            siteNames);

        if (enabled.HasValue)
        {
            config.Enabled = enabled.Value;
        }

        if (listen.HasValue)
        {
            config.Listen = listen.Value;
        }
        else if (enabled.HasValue && !enabled.Value)
        {
            config.Listen = false;
        }

        return config;
    }

    private void NotifyStatusChanged() => StatusChanged?.Invoke(this, EventArgs.Empty);

    private bool IsHelperOperational(TestDnsStatus status)
        => status.Running && status.NrptActive;

    private static List<string> NormalizeConfiguredSuffixes(TestDnsSettings testDns) =>
        LocalDnsSuffix.NormalizeList(
            testDns.Suffixes,
            ensureDefaultTest: !LocalDnsSuffix.ContainsCatchAll(testDns.Suffixes),
            allowDangerous: testDns.AllowDangerousSettings);

    private static bool IsRuntimeStatusFresh(DnsHelperRuntimeStatus? runtime)
        => runtime is not null
           && runtime.UpdatedAt != default
           && DateTimeOffset.UtcNow - runtime.UpdatedAt < RuntimeStatusTrustTtl;

    private static bool CheckHelperServiceRunning()
    {
        if (!StackrootDnsServiceInstaller.IsInstalled())
        {
            return false;
        }

        StackrootDnsServiceInstaller.InvalidateServiceStateCache();
        return StackrootDnsServiceInstaller.IsRunningUncached();
    }

    private void SyncStaleRuntimeStatusIfNeeded(TestDnsSettings settings, IReadOnlyList<string> suffixes, bool serviceRunning)
    {
        if (!StackrootDnsServiceInstaller.IsInstalled())
        {
            return;
        }

        if (serviceRunning)
        {
            return;
        }

        var runtime = _client.ReadCachedStatus();
        var nrptActive = settings.Enabled && _nrpt.AreAllRulesPresent(suffixes);
        if (runtime is { ListenerRunning: false, NrptActive: var cachedNrpt } && cachedNrpt == nrptActive)
        {
            return;
        }

        DnsHelperConfigStore.WriteStatus(new DnsHelperRuntimeStatus
        {
            ListenerRunning = false,
            NrptActive = nrptActive,
            LastError = runtime?.LastError,
            UpdatedAt = DateTimeOffset.UtcNow
        });
    }

    private TestDnsStatus BuildStatus(TestDnsSettings settings, IReadOnlyList<string> suffixes, bool serviceRunning, bool probeNrpt)
    {
        var runtime = _client.ReadCachedStatus();
        var listenerRunning = serviceRunning && (runtime?.ListenerRunning ?? false);
        var nrptRulesPresent = ResolveNrptRulesPresent(
            settings,
            suffixes,
            runtime,
            listenerRunning,
            serviceRunning,
            probeNrpt);
        var nrptActive = settings.Enabled && nrptRulesPresent;
        var message = ResolveStatusMessage(settings.Enabled, listenerRunning, nrptRulesPresent, runtime?.LastError);
        return new TestDnsStatus(settings.Enabled, listenerRunning, nrptActive, message);
    }

    private bool ResolveNrptRulesPresent(
        TestDnsSettings settings,
        IReadOnlyList<string> suffixes,
        DnsHelperRuntimeStatus? runtime,
        bool listenerRunning,
        bool serviceRunning,
        bool probeNrpt)
    {
        if (!settings.Enabled)
        {
            return false;
        }

        if (listenerRunning && runtime is not null && serviceRunning)
        {
            return runtime.NrptActive;
        }

        if (!probeNrpt && runtime is not null)
        {
            return runtime.NrptActive;
        }

        return _nrpt.AreAllRulesPresent(suffixes);
    }

    private static string? ResolveStatusMessage(
        bool enabled,
        bool listenerRunning,
        bool nrptRulesPresent,
        string? lastError)
    {
        if (!string.IsNullOrWhiteSpace(lastError))
        {
            return lastError;
        }

        if (enabled && nrptRulesPresent && !listenerRunning)
        {
            return "DNS routing is active but the helper service is not running. Some dev domains may not resolve until it starts.";
        }

        return null;
    }
}
