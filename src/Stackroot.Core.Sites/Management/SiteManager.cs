using Stackroot.Core.Nginx;
using Stackroot.Core.Sites.Models;
using Stackroot.Core.Sites.Commands;
using Stackroot.Core.Sites.Installers;
using Stackroot.Core.Catalog;
using Stackroot.Core.Sites.Nginx;
using Stackroot.Core.Sites.Persistence;
using Stackroot.Core.Windows;
using Stackroot.Core.Abstractions;
using Stackroot.Core.Settings;
using Stackroot.Core.Sites;
using Stackroot.Core.Databases;
using Stackroot.Core.Supervisor;
using SiteModel = Stackroot.Core.Sites.Models.Site;
using CreateSiteInputModel = Stackroot.Core.Sites.Models.CreateSiteInput;
using UpdateSiteInputModel = Stackroot.Core.Sites.Models.UpdateSiteInput;

namespace Stackroot.Core.Sites.Management;

public sealed class SiteManager
{
    private readonly SiteStore _store;
    private readonly SiteNginxVhostWriter _vhostWriter;
    private readonly HostsFileEditor _hostsFileEditor;
    private readonly SettingsStore _settingsStore;
    private readonly InstallRegistryStore _registryStore;
    private readonly SiteCommandRunner _commandRunner;
    private readonly DatabaseManager? _databaseManager;
    private readonly SiteInstallerRegistry? _installerRegistry;
    private readonly IDiagnosticsReporter? _diagnostics;
    private readonly StackrootPaths _paths;

    public SiteManager(
        SiteStore store,
        SiteNginxVhostWriter vhostWriter,
        HostsFileEditor hostsFileEditor,
        SettingsStore settingsStore,
        InstallRegistryStore registryStore,
        SiteCommandRunner commandRunner,
        StackrootPaths paths,
        DatabaseManager? databaseManager = null,
        SiteInstallerRegistry? installerRegistry = null,
        IDiagnosticsReporter? diagnostics = null,
        int nginxHttpPort = 80,
        bool autoHosts = true)
    {
        _store = store;
        _vhostWriter = vhostWriter;
        _hostsFileEditor = hostsFileEditor;
        _settingsStore = settingsStore;
        _registryStore = registryStore;
        _commandRunner = commandRunner;
        _paths = paths;
        _databaseManager = databaseManager;
        _installerRegistry = installerRegistry;
        _diagnostics = diagnostics;
        _ = nginxHttpPort;
        _ = autoHosts;
    }

    public IReadOnlyList<SiteModel> List() => _store.List();

    public SiteModel? Get(string id) => _store.GetById(id);

    public SiteModel Create(CreateSiteInputModel input)
    {
        var settings = _settingsStore.Load();
        var template = SiteTemplates.Resolve(input.Template);
        var sitePath = SitePaths.ResolveSitePath(input, settings.General.WwwPath);
        var customPath = SitePaths.IsCustomPathMode(input);

        if (customPath)
        {
            if (!Directory.Exists(sitePath))
            {
                throw new InvalidOperationException($"Folder not found: {sitePath}");
            }
        }
        else if (Directory.Exists(sitePath))
        {
            throw new InvalidOperationException($"Directory already exists: {sitePath}");
        }

        var site = _store.Create(input);
        try
        {
            SiteProvisioner.ScaffoldDirectory(site.Path, site.DocumentRoot);
            SiteProvisioner.ScaffoldFiles(site);
            _ = EnsureDevSslForCurrentDomains();
            SyncSiteRuntime(site);
            return site;
        }
        catch
        {
            _store.Remove(site.Id);
            if (!customPath && Directory.Exists(site.Path))
            {
                TryDeleteDirectory(site.Path);
            }

            throw;
        }
    }

    /// <summary>Run the one-click installer for a site that was just created.</summary>
    public async Task<SiteInstallResult> InstallSiteAsync(
        SiteModel site,
        SiteInstallOptions options,
        Action<InstallerMessage> onMessage,
        CancellationToken cancel)
    {
        if (_installerRegistry is null)
            throw new InvalidOperationException("No installers registered.");

        var installer = _installerRegistry.Get(site.Template)
            ?? throw new InvalidOperationException($"No installer for template '{site.Template}'.");

        if (!installer.CanInstall(site))
            throw new InvalidOperationException($"Site directory is not ready for {installer.DisplayName} install.");

        return await installer.InstallAsync(site, options, onMessage, cancel).ConfigureAwait(false);
    }

    public bool HasInstaller(string templateId) =>
        _installerRegistry?.HasInstaller(templateId) ?? false;

    public SiteModel Update(string id, UpdateSiteInputModel patch)
    {
        var previous = _store.GetById(id);

        if (previous is not null &&
            !string.IsNullOrWhiteSpace(patch.Path) &&
            !string.Equals(previous.Path, patch.Path.Trim(), StringComparison.OrdinalIgnoreCase) &&
            !Directory.Exists(patch.Path.Trim()))
        {
            throw new InvalidOperationException($"Folder not found: {patch.Path.Trim()}");
        }

        var templateChanged = previous is not null &&
                              !string.IsNullOrWhiteSpace(patch.Template) &&
                              !string.Equals(previous.Template, patch.Template, StringComparison.OrdinalIgnoreCase);

        var site = _store.Update(id, patch);

        if (templateChanged)
        {
            SiteProvisioner.ScaffoldDirectory(site.Path, site.DocumentRoot);
            SiteProvisioner.ScaffoldFiles(site);
        }

        _ = EnsureDevSslForCurrentDomains();
        SyncSiteRuntime(site);
        return site;
    }

    public SiteModel? Delete(string id, bool forceDeleteFiles = false)
    {
        var removed = _store.Remove(id);
        if (removed is not null)
        {
            _vhostWriter.Remove(removed);
            if (AutoHosts)
            {
                SyncManagedHosts();
            }

            _databaseManager?.UnlinkSite(id);

            var shouldDelete = forceDeleteFiles || !IsCustomPathSite(removed);
            if (shouldDelete && Directory.Exists(removed.Path))
            {
                TryDeleteDirectory(removed.Path);
            }
        }

        _ = EnsureDevSslForCurrentDomains();
        return removed;
    }

    public IReadOnlyList<SiteQuickActionDefinition> GetQuickActions(string siteId)
    {
        var site = _store.GetById(siteId) ?? throw new KeyNotFoundException($"Site not found: {siteId}");
        return SiteQuickActionPresets.ForTemplate(site.Template);
    }

    public IReadOnlyList<SiteProcessPresetDefinition> GetProcessPresets(string siteId)
    {
        var site = _store.GetById(siteId) ?? throw new KeyNotFoundException($"Site not found: {siteId}");
        return SiteProcessPresets.ForTemplate(site.Template);
    }

    public void RegenerateAll()
    {
        var sslPaths = BuildDevSslCertificates();
        if (ResolveNginxSslEnabled() && sslPaths is null)
        {
            _diagnostics?.LogUserError(
                "SSL",
                "HTTPS is enabled but dev certificates could not be created. Sites will use HTTP until certificates are available.");
        }

        if (!_store.TryLoad(out var registry, out _))
        {
            return;
        }

        var sites = registry.Sites.ToList();

        // Collect all hosts before the loop to batch-write once
        var hostEntries = BuildHostEntries(sites);

        // Batch-write all hosts entries ONCE
        if (AutoHosts)
        {
            _diagnostics?.LogActivity("Hosts", $"Syncing hosts — {hostEntries.Count} domain(s)");

            if (!_hostsFileEditor.SyncHosts(hostEntries))
            {
                _diagnostics?.LogUserError("Hosts", $"Failed to sync hosts: {_hostsFileEditor.LastError}");
            }
        }
        else
        {
            _diagnostics?.LogActivity("Hosts", "AutoHosts is disabled — skipping hosts sync");
        }

        // Clean up stale nginx vhosts
        var liveIds = sites.Where(site => site.Enabled).Select(site => site.Id).ToHashSet(StringComparer.Ordinal);
        if (Directory.Exists(_vhostWriter.NginxSitesEnabledDirectory))
        {
            foreach (var file in Directory.EnumerateFiles(_vhostWriter.NginxSitesEnabledDirectory, "*.conf"))
            {
                var fileName = Path.GetFileName(file);
                if (NginxSitesEnabledReservedFiles.IsReserved(fileName))
                {
                    continue;
                }

                var id = Path.GetFileNameWithoutExtension(file);
                if (!liveIds.Contains(id))
                {
                    File.Delete(file);
                }
            }
        }
    }

    public SiteCommandResult RunQuickAction(string siteId, string actionId, Action<SiteCommandLogStarted>? onLogCreated = null)
    {
        var site = _store.GetById(siteId) ?? throw new KeyNotFoundException($"Site not found: {siteId}");
        return _commandRunner.RunQuickAction(site, actionId, onLogCreated);
    }

    public SiteCommandResult RunCustomCommand(
        string siteId,
        string commandId,
        string commandLine,
        Action<SiteCommandLogStarted>? onLogCreated = null)
    {
        var site = _store.GetById(siteId) ?? throw new KeyNotFoundException($"Site not found: {siteId}");
        return _commandRunner.RunCustomCommand(site, commandId, commandLine, onLogCreated);
    }

    public bool IsSiteCommandRunning(string logPath) => _commandRunner.IsRunning(logPath);

    public bool CancelSiteCommand(string logPath) => _commandRunner.TryCancel(logPath);

    public DevSslTrustResult TrustDevSslCertificate()
    {
        _ = EnsureDevSslForCurrentDomains();
        return DevSslCertificateManager.TrustDevSslCertificate(_paths);
    }

    public SitesDashboard GetDashboard()
    {
        var sites = _store.List().OrderBy(site => site.Name, StringComparer.OrdinalIgnoreCase).ToList();
        var featured = sites.Where(site => site.Enabled && site.Featured == true).ToList();
        var active = sites.Where(site => site.Enabled && site.Featured != true).ToList();
        var disabled = sites.Where(site => !site.Enabled).ToList();

        return new SitesDashboard
        {
            Featured = featured,
            Active = active,
            Disabled = disabled,
            TotalCount = sites.Count,
            ActiveCount = featured.Count + active.Count,
            DisabledCount = disabled.Count
        };
    }

    private void SyncSiteRuntime(SiteModel site, bool syncHosts = true, DevSslPaths? sslPaths = null)
    {
        if (site.Enabled)
        {
            var settings = _settingsStore.Load();
            var fastCgi = SitePhpFastCgiEndpoint.Resolve(settings, _registryStore, site.PhpVersionId);
            var phpRc = SitePhpFastCgiEndpoint.ResolvePhpRcPath(_paths, settings, site.PhpVersionId);
            sslPaths ??= DevSslCertificateManager.TryGetExisting(_paths);
            var sslEnabled = ResolveNginxSslEnabled() && sslPaths is not null;
            _vhostWriter.Write(
                site,
                ResolveNginxHttpPort(),
                fastCgi,
                phpRc,
                ResolveNginxHttpsPort(),
                sslEnabled);
            if (AutoHosts && syncHosts)
            {
                SyncManagedHosts();
            }
        }
        else
        {
            _vhostWriter.Remove(site);
            if (AutoHosts && syncHosts)
            {
                SyncManagedHosts();
            }
        }
    }

    private void SyncManagedHosts()
    {
        var hostEntries = BuildHostEntries(_store.List());
        if (!_hostsFileEditor.SyncHosts(hostEntries))
        {
            _diagnostics?.LogUserError("Hosts", $"Failed to sync hosts: {_hostsFileEditor.LastError}");
        }
    }

    private Dictionary<string, string> BuildHostEntries(IEnumerable<SiteModel> sites)
    {
        var hostEntries = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        if (!AutoHosts)
        {
            return hostEntries;
        }

        var appDomain = _settingsStore.Load().General.AppDomain;
        if (!string.IsNullOrWhiteSpace(appDomain))
        {
            hostEntries[appDomain] = "127.0.0.1";
        }

        foreach (var site in sites.Where(static site => site.Enabled))
        {
            foreach (var host in SiteDomainNames.GetHostsEligibleNames(site))
            {
                hostEntries[host] = "127.0.0.1";
            }
        }

        return hostEntries;
    }

    private int ResolveNginxHttpPort()
    {
        var settings = _settingsStore.Load();
        if (settings.Services.TryGetValue(ServiceId.Nginx, out var nginx) && nginx.Port > 0)
        {
            return nginx.Port;
        }

        return 80;
    }

    private int ResolveNginxHttpsPort()
    {
        var settings = _settingsStore.Load();
        if (settings.Services.TryGetValue(ServiceId.Nginx, out var nginx) && nginx.SslPort is > 0)
        {
            return nginx.SslPort.Value;
        }

        return 443;
    }

    private bool ResolveNginxSslEnabled()
    {
        var settings = _settingsStore.Load();
        if (settings.Services.TryGetValue(ServiceId.Nginx, out var nginx))
        {
            return nginx.SslEnabled != false;
        }

        return true;
    }

    private static bool IsCustomPathSite(SiteModel site) =>
        string.Equals(site.PathMode, "custom", StringComparison.OrdinalIgnoreCase);

    private static void TryDeleteDirectory(string path)
    {
        if (!Directory.Exists(path))
        {
            return;
        }

        for (var attempt = 0; attempt < 8; attempt++)
        {
            try
            {
                Directory.Delete(path, recursive: true);
                return;
            }
            catch (IOException) when (attempt < 7)
            {
                Thread.Sleep(250);
            }
            catch (UnauthorizedAccessException) when (attempt < 7)
            {
                Thread.Sleep(250);
            }
        }
    }

    private bool AutoHosts => _settingsStore.Load().Sites.AutoHosts;

    private DevSslPaths? BuildDevSslCertificates()
    {
        var settings = _settingsStore.Load();
        var domains = _store.List()
            .Where(static site => site.Enabled)
            .SelectMany(SiteDomainNames.GetSslSanNames)
            .Where(static domain => !string.IsNullOrWhiteSpace(domain))
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToList();

        var appDomain = settings.General.AppDomain;
        if (!string.IsNullOrWhiteSpace(appDomain) &&
            !domains.Contains(appDomain, StringComparer.OrdinalIgnoreCase))
        {
            domains.Add(appDomain);
        }

        if (domains.Count == 0)
        {
            if (ResolveNginxSslEnabled() && !DevSslCertificateManager.CertificatesExist(_paths))
            {
                return DevSslCertificateManager.EnsureDevSslCertificate(_paths, ["localhost"]);
            }

            return DevSslCertificateManager.TryGetExisting(_paths);
        }

        return DevSslCertificateManager.EnsureDevSslCertificate(_paths, domains);
    }

    private DevSslPaths? EnsureDevSslForCurrentDomains()
        => BuildDevSslCertificates();
}
