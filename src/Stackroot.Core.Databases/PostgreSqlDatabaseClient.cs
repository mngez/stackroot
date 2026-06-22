using System.Net.Sockets;
using Stackroot.Core.Abstractions;
using Stackroot.Core.Catalog;
using Stackroot.Core.Settings;

namespace Stackroot.Core.Databases;

public static class PostgreSqlDatabaseClient
{
    public static void CreateDatabase(
        InstallRegistryStore registry,
        AppSettings settings,
        string name)
    {
        var validatedName = ValidateDatabaseName(name);
        var (installed, serviceSettings) = ResolveInstall(registry, settings);
        EnsureServiceRunning(serviceSettings);

        ExecutePsql(
            installed.InstallPath,
            serviceSettings,
            settings.Databases.Postgresql.Username,
            $"CREATE DATABASE \"{validatedName}\"",
            $"Failed to create database '{validatedName}' on PostgreSQL");
    }

    public static void DropDatabase(
        InstallRegistryStore registry,
        AppSettings settings,
        string name)
    {
        var validatedName = ValidateDatabaseName(name);
        var (installed, serviceSettings) = ResolveInstall(registry, settings);
        EnsureServiceRunning(serviceSettings);

        ExecutePsql(
            installed.InstallPath,
            serviceSettings,
            settings.Databases.Postgresql.Username,
            $"DROP DATABASE IF EXISTS \"{validatedName}\"",
            $"Failed to delete database '{validatedName}' from PostgreSQL");
    }

    public static void ExportDump(
        InstallRegistryStore registry,
        AppSettings settings,
        string databaseName,
        string backupPath)
    {
        var validatedName = ValidateDatabaseName(databaseName);
        var (installed, serviceSettings) = ResolveInstall(registry, settings);
        EnsureServiceRunning(serviceSettings);

        var pgDump = ResolveBinary(installed.InstallPath, "bin/pg_dump.exe", "pg_dump.exe");

        var host = string.IsNullOrWhiteSpace(serviceSettings.Host) ? "127.0.0.1" : serviceSettings.Host;
        var port = serviceSettings.Port <= 0 ? 5432 : serviceSettings.Port;

        using var process = new System.Diagnostics.Process
        {
            StartInfo = ProcessStreamEncoding.Create(pgDump)
        };
        process.StartInfo.Environment["PGPASSWORD"] = settings.Databases.Postgresql.Password;
        process.StartInfo.ArgumentList.Add("-h");
        process.StartInfo.ArgumentList.Add(host);
        process.StartInfo.ArgumentList.Add("-p");
        process.StartInfo.ArgumentList.Add(port.ToString());
        process.StartInfo.ArgumentList.Add("-U");
        process.StartInfo.ArgumentList.Add(settings.Databases.Postgresql.Username);
        process.StartInfo.ArgumentList.Add("-d");
        process.StartInfo.ArgumentList.Add(validatedName);
        process.StartInfo.ArgumentList.Add("-f");
        process.StartInfo.ArgumentList.Add(backupPath);

        process.Start();
        var error = process.StandardError.ReadToEnd();
        process.WaitForExit();
        if (process.ExitCode != 0)
        {
            throw new InvalidOperationException(
                string.IsNullOrWhiteSpace(error)
                    ? $"pg_dump failed with exit code {process.ExitCode}."
                    : error);
        }
    }

    public static void ImportDump(
        InstallRegistryStore registry,
        AppSettings settings,
        string databaseName,
        string backupPath)
    {
        var validatedName = ValidateDatabaseName(databaseName);
        var (installed, serviceSettings) = ResolveInstall(registry, settings);
        EnsureServiceRunning(serviceSettings);

        var psqlPath = ResolveBinary(installed.InstallPath, "bin/psql.exe", "psql.exe");

        var host = string.IsNullOrWhiteSpace(serviceSettings.Host) ? "127.0.0.1" : serviceSettings.Host;
        var port = serviceSettings.Port <= 0 ? 5432 : serviceSettings.Port;
        var user = settings.Databases.Postgresql.Username;

        // Create DB if not exists (ignore error if already exists)
        RunPsqlCommand(psqlPath, host, port, user, settings.Databases.Postgresql.Password,
            $"CREATE DATABASE \"{validatedName}\"", throwOnError: false);

        // Import
        using var process = new System.Diagnostics.Process
        {
            StartInfo = ProcessStreamEncoding.Create(psqlPath)
        };
        process.StartInfo.Environment["PGPASSWORD"] = settings.Databases.Postgresql.Password;
        process.StartInfo.ArgumentList.Add("-h");
        process.StartInfo.ArgumentList.Add(host);
        process.StartInfo.ArgumentList.Add("-p");
        process.StartInfo.ArgumentList.Add(port.ToString());
        process.StartInfo.ArgumentList.Add("-U");
        process.StartInfo.ArgumentList.Add(user);
        process.StartInfo.ArgumentList.Add("-d");
        process.StartInfo.ArgumentList.Add(validatedName);
        process.StartInfo.ArgumentList.Add("-f");
        process.StartInfo.ArgumentList.Add(backupPath);

        process.Start();
        var error = process.StandardError.ReadToEnd();
        process.WaitForExit();
        if (process.ExitCode != 0)
        {
            throw new InvalidOperationException(
                string.IsNullOrWhiteSpace(error)
                    ? $"psql import failed with exit code {process.ExitCode}."
                    : error);
        }
    }

    private static void ExecutePsql(
        string installPath,
        ServicePortSettings serviceSettings,
        string username,
        string sql,
        string failurePrefix)
    {
        var psqlPath = ResolveBinary(installPath, "bin/psql.exe", "psql.exe");

        var host = string.IsNullOrWhiteSpace(serviceSettings.Host) ? "127.0.0.1" : serviceSettings.Host;
        var port = serviceSettings.Port <= 0 ? 5432 : serviceSettings.Port;

        RunPsqlCommand(psqlPath, host, port, username, string.Empty, sql, throwOnError: true, failurePrefix);
    }

    private static void RunPsqlCommand(
        string psqlPath,
        string host,
        int port,
        string username,
        string password,
        string sql,
        bool throwOnError,
        string? failurePrefix = null)
    {
        using var process = new System.Diagnostics.Process
        {
            StartInfo = ProcessStreamEncoding.Create(psqlPath)
        };
        process.StartInfo.Environment["PGPASSWORD"] = password;
        process.StartInfo.ArgumentList.Add("-h");
        process.StartInfo.ArgumentList.Add(host);
        process.StartInfo.ArgumentList.Add("-p");
        process.StartInfo.ArgumentList.Add(port.ToString());
        process.StartInfo.ArgumentList.Add("-U");
        process.StartInfo.ArgumentList.Add(username);
        process.StartInfo.ArgumentList.Add("-d");
        process.StartInfo.ArgumentList.Add("postgres");
        process.StartInfo.ArgumentList.Add("-c");
        process.StartInfo.ArgumentList.Add(sql);

        process.Start();
        var error = process.StandardError.ReadToEnd();
        process.WaitForExit();
        if (throwOnError && process.ExitCode != 0)
        {
            throw new InvalidOperationException(
                string.IsNullOrWhiteSpace(error)
                    ? $"{failurePrefix ?? "psql"} failed with exit code {process.ExitCode}."
                    : error);
        }
    }

    private static (InstalledPackage Installed, ServicePortSettings ServiceSettings) ResolveInstall(
        InstallRegistryStore registry,
        AppSettings settings)
    {
        var serviceSettings = settings.Services.TryGetValue(ServiceId.Postgresql, out var configured)
            ? configured
            : SettingsDefaults.DefaultServices()[ServiceId.Postgresql];

        var packageId = serviceSettings.PackageId
            ?? SettingsDefaults.DefaultServices()[ServiceId.Postgresql].PackageId;
        var installed = string.IsNullOrWhiteSpace(packageId)
            ? null
            : registry.GetById(packageId);
        if (installed is null)
        {
            throw new InvalidOperationException("Install PostgreSQL from Services first.");
        }

        return (installed, serviceSettings);
    }

    private static void EnsureServiceRunning(ServicePortSettings serviceSettings)
    {
        if (!serviceSettings.Enabled)
        {
            throw new InvalidOperationException("PostgreSQL is disabled in Services settings.");
        }

        var host = string.IsNullOrWhiteSpace(serviceSettings.Host) ? "127.0.0.1" : serviceSettings.Host;
        var port = serviceSettings.Port <= 0 ? 5432 : serviceSettings.Port;
        if (!IsPortOpen(host, port, timeoutMs: 800))
        {
            throw new InvalidOperationException(
                $"PostgreSQL is not running yet (port {port} is closed). Start it from Services first.");
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

    private static string ResolveBinary(string installPath, string relativePath, string toolName)
    {
        var resolved = PackageBinaryResolver.ResolvePackageBinary(installPath, relativePath);
        if (resolved is null || !File.Exists(resolved))
        {
            throw new FileNotFoundException($"{toolName} was not found.", resolved ?? Path.Combine(installPath, relativePath));
        }
        return resolved;
    }

    private static string ValidateDatabaseName(string name)
    {
        var trimmed = name.Trim();
        if (string.IsNullOrWhiteSpace(trimmed))
        {
            throw new ArgumentException("Database name is required.", nameof(name));
        }

        if (!trimmed.All(ch => char.IsLetterOrDigit(ch) || ch is '_'))
        {
            throw new ArgumentException(
                "Database name may only contain letters, numbers, and underscores.",
                nameof(name));
        }

        return trimmed;
    }
}
