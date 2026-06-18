using System.Net.Sockets;
using Stackroot.Core.Abstractions;
using Stackroot.Core.Catalog;
using Stackroot.Core.Settings;

namespace Stackroot.Core.Databases;

public static class MysqlDatabaseClient
{
    public static void EnsureRootPassword(
        InstallRegistryStore registry,
        AppSettings settings,
        SqlEngine engine)
    {
        var serviceId = ResolveServiceId(engine);
        var serviceSettings = ResolveServiceSettings(settings, serviceId);
        var installed = ResolveInstalledPackage(registry, serviceSettings, serviceId);
        if (installed is null)
        {
            return;
        }

        var creds = engine == SqlEngine.Mariadb ? settings.Databases.Mariadb : settings.Databases.Mysql;
        MariaDbCredentialSync.EnsureRootPassword(
            installed.InstallPath,
            serviceId,
            serviceSettings,
            creds.Username,
            creds.Password);
    }

    public static void CreateDatabase(
        InstallRegistryStore registry,
        AppSettings settings,
        SqlEngine engine,
        string name)
    {
        var validatedName = ValidateDatabaseName(name);
        var serviceId = ResolveServiceId(engine);
        var serviceSettings = ResolveServiceSettings(settings, serviceId);
        var installed = ResolveInstalledPackage(registry, serviceSettings, serviceId)
            ?? throw new InvalidOperationException($"Install {engine} from Services first.");

        EnsureServiceRunning(engine, serviceSettings);

        var creds = engine == SqlEngine.Mariadb ? settings.Databases.Mariadb : settings.Databases.Mysql;
        MariaDbCredentialSync.EnsureRootPassword(
            installed.InstallPath,
            serviceId,
            serviceSettings,
            creds.Username,
            creds.Password);

        MariaDbCredentialSync.LogCreateDatabaseAttempt(serviceSettings, validatedName);

        ExecuteDatabaseSql(
            installed.InstallPath,
            serviceId,
            serviceSettings,
            creds.Username,
            creds.Password,
            $"CREATE DATABASE IF NOT EXISTS {validatedName} CHARACTER SET utf8mb4 COLLATE utf8mb4_unicode_ci",
            $"Failed to create database '{validatedName}' on {engine}");
    }

    public static void DropDatabase(
        InstallRegistryStore registry,
        AppSettings settings,
        SqlEngine engine,
        string name)
    {
        var validatedName = ValidateDatabaseName(name);
        var serviceId = ResolveServiceId(engine);
        var serviceSettings = ResolveServiceSettings(settings, serviceId);
        var installed = ResolveInstalledPackage(registry, serviceSettings, serviceId)
            ?? throw new InvalidOperationException($"Install {engine} from Services first.");

        EnsureServiceRunning(engine, serviceSettings);

        var creds = engine == SqlEngine.Mariadb ? settings.Databases.Mariadb : settings.Databases.Mysql;
        MariaDbCredentialSync.EnsureRootPassword(
            installed.InstallPath,
            serviceId,
            serviceSettings,
            creds.Username,
            creds.Password);

        ExecuteDatabaseSql(
            installed.InstallPath,
            serviceId,
            serviceSettings,
            creds.Username,
            creds.Password,
            $"DROP DATABASE IF EXISTS `{validatedName}`",
            $"Failed to delete database '{validatedName}' from {engine}");
    }

    public static void ImportSqlFile(
        InstallRegistryStore registry,
        AppSettings settings,
        SqlEngine engine,
        string databaseName,
        string backupPath)
    {
        var validatedName = ValidateDatabaseName(databaseName);
        var serviceId = ResolveServiceId(engine);
        var serviceSettings = ResolveServiceSettings(settings, serviceId);
        var installed = ResolveInstalledPackage(registry, serviceSettings, serviceId)
            ?? throw new InvalidOperationException($"Install {engine} from Services first.");

        EnsureServiceRunning(engine, serviceSettings);

        var creds = engine == SqlEngine.Mariadb ? settings.Databases.Mariadb : settings.Databases.Mysql;
        MariaDbCredentialSync.EnsureRootPassword(
            installed.InstallPath,
            serviceId,
            serviceSettings,
            creds.Username,
            creds.Password);

        if (!MariaDbCredentialSync.TryImportSqlFile(
                installed.InstallPath,
                serviceSettings,
                creds.Username,
                creds.Password,
                validatedName,
                backupPath,
                out var error))
        {
            throw new InvalidOperationException(
                string.IsNullOrWhiteSpace(error)
                    ? $"Failed to restore backup into '{validatedName}'."
                    : error);
        }
    }

    public static void ExportSqlFile(
        InstallRegistryStore registry,
        AppSettings settings,
        SqlEngine engine,
        string databaseName,
        string backupPath)
    {
        var validatedName = ValidateDatabaseName(databaseName);
        var serviceId = ResolveServiceId(engine);
        var serviceSettings = ResolveServiceSettings(settings, serviceId);
        var installed = ResolveInstalledPackage(registry, serviceSettings, serviceId)
            ?? throw new InvalidOperationException($"Install {engine} from Services first.");

        EnsureServiceRunning(engine, serviceSettings);

        var creds = engine == SqlEngine.Mariadb ? settings.Databases.Mariadb : settings.Databases.Mysql;
        MariaDbCredentialSync.EnsureRootPassword(
            installed.InstallPath,
            serviceId,
            serviceSettings,
            creds.Username,
            creds.Password);

        if (!MariaDbCredentialSync.TryExportSqlFile(
                installed.InstallPath,
                serviceSettings,
                creds.Username,
                creds.Password,
                validatedName,
                backupPath,
                out var error))
        {
            throw new InvalidOperationException(
                string.IsNullOrWhiteSpace(error)
                    ? $"Failed to create backup for '{validatedName}'."
                    : error);
        }
    }

    private static void ExecuteDatabaseSql(
        string installPath,
        ServiceId serviceId,
        ServicePortSettings serviceSettings,
        string? username,
        string? password,
        string sql,
        string failurePrefix)
    {
        if (MariaDbCredentialSync.TryExecuteSql(installPath, serviceSettings, username, password, sql, out var error))
        {
            return;
        }

        if (MariaDbCredentialSync.IsInsufficientPrivilegeError(error))
        {
            MariaDbCredentialSync.TryRepairCreatePrivilege(
                installPath,
                serviceId,
                serviceSettings,
                username,
                password);
            if (MariaDbCredentialSync.TryExecuteSql(installPath, serviceSettings, username, password, sql, out error))
            {
                return;
            }
        }

        throw new InvalidOperationException($"{failurePrefix}. {error}");
    }

    private static void EnsureServiceRunning(SqlEngine engine, ServicePortSettings serviceSettings)
    {
        if (!serviceSettings.Enabled)
        {
            throw new InvalidOperationException($"{engine} is disabled in Services settings.");
        }

        var host = string.IsNullOrWhiteSpace(serviceSettings.Host) ? "127.0.0.1" : serviceSettings.Host;
        var port = serviceSettings.Port <= 0 ? 3306 : serviceSettings.Port;
        if (!IsPortOpen(host, port, timeoutMs: 800))
        {
            throw new InvalidOperationException(
                $"{engine} is not running yet (port {port} is closed). Start it from Services first.");
        }
    }

    private static bool IsPortOpen(string host, int port, int timeoutMs)
    {
        using var client = new TcpClient();
        using var cts = new CancellationTokenSource(timeoutMs);

        try
        {
            client.ConnectAsync(host, port, cts.Token).GetAwaiter().GetResult();
            return true;
        }
        catch
        {
            return false;
        }
    }

    private static ServiceId ResolveServiceId(SqlEngine engine)
        => engine == SqlEngine.Mariadb ? ServiceId.Mariadb : ServiceId.Mysql;

    private static ServicePortSettings ResolveServiceSettings(AppSettings settings, ServiceId serviceId)
        => settings.Services.TryGetValue(serviceId, out var configured)
            ? configured
            : SettingsDefaults.DefaultServices()[serviceId];

    private static InstalledPackage? ResolveInstalledPackage(
        InstallRegistryStore registry,
        ServicePortSettings serviceSettings,
        ServiceId serviceId)
    {
        var packageId = serviceSettings.PackageId ?? SettingsDefaults.DefaultServices()[serviceId].PackageId;
        return string.IsNullOrWhiteSpace(packageId) ? null : registry.GetById(packageId);
    }

    private static string ValidateDatabaseName(string name)
    {
        var trimmed = name.Trim();
        if (string.IsNullOrWhiteSpace(trimmed))
        {
            throw new ArgumentException("Database name is required.", nameof(name));
        }

        if (!trimmed.All(ch => char.IsLetterOrDigit(ch) || ch is '_' or '$'))
        {
            throw new ArgumentException(
                "Database name may only contain letters, numbers, underscores, and dollar signs.",
                nameof(name));
        }

        return trimmed;
    }
}
