using System.IO;
using Stackroot.Core.Abstractions;
using Stackroot.Core.AdminTools;
using Stackroot.Core.Catalog;
using Stackroot.Core.Nginx;
using Stackroot.Core.Services;
using Stackroot.Core.Settings;
using Stackroot.Core.Sites.Management;
using Stackroot.Core.Windows;

namespace Stackroot.App.Services;

public sealed class NginxWebStackRebuilder
{
    private readonly SiteManager _siteManager;
    private readonly AppDomainConfigWriter _appDomainConfigWriter;
    private readonly PhpMyAdminManager _phpMyAdminManager;
    private readonly PhpRedisAdminManager _phpRedisAdminManager;
    private readonly MailpitManager _mailpitManager;
    private readonly SettingsStore _settingsStore;
    private readonly InstallRegistryStore _registryStore;
    private readonly StackrootPaths _paths;
    private readonly IProcessJobManager _jobManager;
    private readonly ServiceManager _serviceManager;

    public NginxWebStackRebuilder(
        SiteManager siteManager,
        AppDomainConfigWriter appDomainConfigWriter,
        PhpMyAdminManager phpMyAdminManager,
        PhpRedisAdminManager phpRedisAdminManager,
        MailpitManager mailpitManager,
        SettingsStore settingsStore,
        InstallRegistryStore registryStore,
        StackrootPaths paths,
        IProcessJobManager jobManager,
        ServiceManager serviceManager)
    {
        _siteManager = siteManager;
        _appDomainConfigWriter = appDomainConfigWriter;
        _phpMyAdminManager = phpMyAdminManager;
        _phpRedisAdminManager = phpRedisAdminManager;
        _mailpitManager = mailpitManager;
        _settingsStore = settingsStore;
        _registryStore = registryStore;
        _paths = paths;
        _jobManager = jobManager;
        _serviceManager = serviceManager;
    }

    public async Task<string> RebuildAsync(CancellationToken cancellationToken = default)
    {
        MigrateLegacySiteVhosts(_paths);
        _siteManager.RegenerateAll();
        await ApplyAdminToolSafelyAsync(() => _phpMyAdminManager.ApplyAsync(cancellationToken));
        await ApplyAdminToolSafelyAsync(() => _phpRedisAdminManager.ApplyAsync(cancellationToken));
        await _mailpitManager.ApplyAsync(cancellationToken);
        await FinalizeAndReloadAsync(cancellationToken);
        return "Web configs rebuilt — nginx, sites, admin tools, and hosts.";
    }

    /// <summary>
    /// Ensures php-cgi listeners, applies an admin tool, writes app-domain nginx config, and reloads nginx.
    /// </summary>
    public async Task ApplyAdminToolAndReloadAsync(
        Func<Task> applyAdminTool,
        CancellationToken cancellationToken = default,
        bool forceNginxRestart = false)
    {
        var php = await _serviceManager.EnsureStackPhpCgiAsync(cancellationToken).ConfigureAwait(false);
        if (!php.Success)
        {
            throw new InvalidOperationException(php.Message ?? "Failed to start php-cgi listeners.");
        }

        await applyAdminTool().ConfigureAwait(false);
        await FinalizeAndReloadAsync(cancellationToken, forceNginxRestart).ConfigureAwait(false);
    }

    /// <summary>
    /// Writes stackroot-app.conf after admin-tool locations are registered, starts php-cgi, reloads nginx.
    /// </summary>
    public async Task FinalizeAndReloadAsync(
        CancellationToken cancellationToken = default,
        bool forceNginxRestart = false)
    {
        _appDomainConfigWriter.Write();
        var php = await _serviceManager.EnsureStackPhpCgiAsync(cancellationToken).ConfigureAwait(false);
        if (!php.Success)
        {
            throw new InvalidOperationException(php.Message ?? "Failed to start php-cgi listeners.");
        }

        await ReloadNginxAsync(cancellationToken, forceNginxRestart).ConfigureAwait(false);
    }

    private async Task ReloadNginxAsync(CancellationToken cancellationToken, bool forceRestart = false)
    {
        var settings = _settingsStore.Load();
        if (!settings.Services.TryGetValue(ServiceId.Nginx, out var nginxSettings))
        {
            return;
        }

        var definition = SettingsDefaults.ServiceDefinitions.First(d => d.Id == ServiceId.Nginx);
        var packageId = nginxSettings.PackageId ?? definition.PackageId;
        if (string.IsNullOrWhiteSpace(packageId))
        {
            return;
        }

        var installed = _registryStore.GetById(packageId);
        if (installed is null)
        {
            return;
        }

        NginxRuntime.setupNginxRuntime(_paths, installed.InstallPath);
        NginxRuntime.writeNginxConfig(_paths, nginxSettings);

        if (forceRestart)
        {
            NginxControl.StopManagedNginx(
                _paths,
                installed.InstallPath,
                _jobManager,
                nginxSettings.Port);
        }

        var reloadResult = await NginxControl.ReloadNginxAsync(
            _paths,
            installed.InstallPath,
            _jobManager,
            nginxSettings.Host,
            nginxSettings.Port,
            cancellationToken);

        if (!reloadResult.Ok)
        {
            throw new InvalidOperationException(reloadResult.Message ?? "Failed to reload nginx.");
        }
    }

    private async Task ApplyAdminToolSafelyAsync(Func<Task> apply)
    {
        try
        {
            await apply().ConfigureAwait(false);
        }
        catch
        {
            // Optional admin tools should not block nginx reload.
        }
    }

    public static void MigrateLegacySiteVhosts(StackrootPaths paths)
    {
        var legacyDir = Path.Combine(paths.ConfigRoot, "nginx", "sites-enabled");
        var targetDir = NginxRuntime.SitesEnabledDirectory(paths);
        Directory.CreateDirectory(targetDir);

        if (!Directory.Exists(legacyDir))
        {
            return;
        }

        foreach (var file in Directory.EnumerateFiles(legacyDir, "*.conf"))
        {
            var destination = Path.Combine(targetDir, Path.GetFileName(file));
            if (File.Exists(destination))
            {
                File.Delete(file);
                continue;
            }

            File.Move(file, destination);
        }

        if (!Directory.EnumerateFileSystemEntries(legacyDir).Any())
        {
            try
            {
                Directory.Delete(legacyDir);
            }
            catch
            {
                // Best effort cleanup of the legacy folder.
            }
        }
    }
}
