using System.Net.Sockets;
using Stackroot.Core.Abstractions;
using Stackroot.Core.Catalog;
using Stackroot.Core.Settings;
using Stackroot.Core.Windows;

namespace Stackroot.Core.Databases;

public static class MariaDbProvisioner
{
    public static async Task<bool> ProvisionAfterInstallAsync(
        StackrootPaths paths,
        InstallRegistryStore registry,
        AppSettings settings,
        ServiceId serviceId,
        Func<ServiceId, CancellationToken, Task<ServiceInfo>> startServiceAsync,
        CancellationToken cancellationToken = default)
    {
        if (serviceId is not (ServiceId.Mysql or ServiceId.Mariadb))
        {
            return false;
        }

        var engine = serviceId == ServiceId.Mariadb ? "mariadb" : "mysql";
        var serviceSettings = ResolveServiceSettings(settings, serviceId);
        var packageId = serviceSettings.PackageId ?? SettingsDefaults.DefaultServices()[serviceId].PackageId;
        var installed = string.IsNullOrWhiteSpace(packageId) ? null : registry.GetById(packageId);
        if (installed is null)
        {
            return false;
        }

        var mysqldPath = ResolveMysqldPath(installed.InstallPath, serviceId);
        if (mysqldPath is null)
        {
            return false;
        }

        var (configPath, dataDir) = DatabaseConfigWriter.WriteMariaDbConfig(paths, serviceSettings, engine);
        var isFreshInstall = !DatabaseConfigWriter.IsMariaDbDataDirInitialized(dataDir);
        DatabaseConfigWriter.EnsureMariaDbInitialized(
            mysqldPath,
            configPath,
            dataDir,
            Path.GetDirectoryName(mysqldPath) ?? installed.InstallPath,
            engine);

        if (isFreshInstall)
        {
            var creds = serviceId == ServiceId.Mariadb ? settings.Databases.Mariadb : settings.Databases.Mysql;
            DatabaseConfigWriter.WriteMariaDbBootstrapInit(paths, engine, creds.Username, creds.Password);
        }

        var startResult = await startServiceAsync(serviceId, cancellationToken).ConfigureAwait(false);
        var serverAvailable = startResult.Status == ServiceStatus.Running
            || (startResult.PortOpen ?? false)
            || await IsPortOpenAsync(serviceSettings.Host, serviceSettings.Port).ConfigureAwait(false);

        if (!serverAvailable)
        {
            return false;
        }

        var applied = await ApplyConfiguredCredentialsAsync(
            installed.InstallPath,
            serviceId,
            settings,
            cancellationToken).ConfigureAwait(false);
        if (applied)
        {
            ClearBootstrapInit(paths, serviceId);
        }

        return applied;
    }

    private static void ClearBootstrapInit(StackrootPaths paths, ServiceId serviceId)
    {
        if (serviceId is not (ServiceId.Mysql or ServiceId.Mariadb))
        {
            return;
        }

        var engine = serviceId == ServiceId.Mariadb ? "mariadb" : "mysql";
        DatabaseConfigWriter.ClearMariaDbBootstrapInit(paths, engine);
    }

    public static async Task<bool> SyncEnabledCredentialsAsync(
        InstallRegistryStore registry,
        AppSettings settings,
        CancellationToken cancellationToken = default)
    {
        var anyAttempted = false;
        var allSucceeded = true;

        foreach (var serviceId in new[] { ServiceId.Mysql, ServiceId.Mariadb })
        {
            cancellationToken.ThrowIfCancellationRequested();
            var serviceSettings = ResolveServiceSettings(settings, serviceId);
            if (!serviceSettings.Enabled)
            {
                continue;
            }

            var packageId = serviceSettings.PackageId ?? SettingsDefaults.DefaultServices()[serviceId].PackageId;
            var installed = string.IsNullOrWhiteSpace(packageId) ? null : registry.GetById(packageId);
            if (installed is null)
            {
                continue;
            }

            var port = serviceSettings.Port <= 0 ? 3306 : serviceSettings.Port;
            if (!await IsPortOpenAsync(serviceSettings.Host, port).ConfigureAwait(false))
            {
                continue;
            }

            // Only sync credentials if the process on this port actually
            // belongs to this service, not another service sharing the port.
            if (!IsPortOwnedByService(port, installed.InstallPath))
            {
                continue;
            }

            anyAttempted = true;
            var applied = await ApplyConfiguredCredentialsAsync(
                installed.InstallPath,
                serviceId,
                settings,
                cancellationToken).ConfigureAwait(false);
            if (!applied)
            {
                allSucceeded = false;
            }
        }

        return !anyAttempted || allSucceeded;
    }

    private static bool IsPortOwnedByService(int port, string installPath)
    {
        try
        {
            foreach (var pid in ProcessPortTools.FindPidsListeningOnPort(port))
            {
                try
                {
                    using var process = System.Diagnostics.Process.GetProcessById(pid);
                    var exePath = process.MainModule?.FileName;
                    if (!string.IsNullOrWhiteSpace(exePath)
                        && exePath.StartsWith(installPath, StringComparison.OrdinalIgnoreCase))
                    {
                        return true;
                    }
                }
                catch
                {
                    // Cannot inspect this process — skip.
                }
            }
        }
        catch
        {
            // Best effort.
        }

        return false;
    }
    public static async Task<bool> ApplyConfiguredCredentialsAsync(
        string installPath,
        ServiceId serviceId,
        AppSettings settings,
        CancellationToken cancellationToken = default)
    {
        if (serviceId is not (ServiceId.Mysql or ServiceId.Mariadb))
        {
            return false;
        }

        var serviceSettings = ResolveServiceSettings(settings, serviceId);
        var creds = serviceId == ServiceId.Mariadb ? settings.Databases.Mariadb : settings.Databases.Mysql;
        var port = serviceSettings.Port <= 0 ? 3306 : serviceSettings.Port;
        if (!await IsPortOpenAsync(serviceSettings.Host, port).ConfigureAwait(false))
        {
            return false;
        }

        return await MariaDbCredentialSync.EnsureCredentialsWhenReadyAsync(
            installPath,
            serviceId,
            serviceSettings,
            creds.Username,
            creds.Password,
            cancellationToken,
            portAlreadyOpen: true).ConfigureAwait(false);
    }

    private static string DescribeEngine(ServiceId serviceId)
        => serviceId == ServiceId.Mariadb ? "MariaDB" : "MySQL";

    private static ServicePortSettings ResolveServiceSettings(AppSettings settings, ServiceId serviceId)
        => settings.Services.TryGetValue(serviceId, out var configured)
            ? configured
            : SettingsDefaults.DefaultServices()[serviceId];

    private static string? ResolveMysqldPath(string installPath, ServiceId serviceId)
    {
        var executable = SettingsDefaults.ServiceDefinitions.First(definition => definition.Id == serviceId).Executable ?? "bin/mysqld.exe";
        return PackageBinaryResolver.ResolvePackageBinary(installPath, executable);
    }

    private static async Task<bool> IsPortOpenAsync(string host, int port)
    {
        using var client = new TcpClient();
        using var cts = new CancellationTokenSource(800);

        try
        {
            await client.ConnectAsync(host, port, cts.Token).ConfigureAwait(false);
            return true;
        }
        catch
        {
            return false;
        }
    }
}
