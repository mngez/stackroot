using System.Net.Sockets;
using Stackroot.Core.Abstractions;
using Stackroot.Core.Catalog;
using Stackroot.Core.Settings;

namespace Stackroot.Core.Databases;

public static class MongoDatabaseClient
{
    public static void CreateDatabase(
        InstallRegistryStore registry,
        AppSettings settings,
        string name)
    {
        var validatedName = ValidateDatabaseName(name);
        var (_, serviceSettings) = ResolveInstall(registry, settings);
        EnsureServiceRunning(serviceSettings);
        // MongoDB auto-creates databases on first write — no shell command needed.
    }

    public static void DropDatabase(
        InstallRegistryStore registry,
        AppSettings settings,
        string name)
    {
        var validatedName = ValidateDatabaseName(name);
        var (installed, serviceSettings) = ResolveInstall(registry, settings);
        EnsureServiceRunning(serviceSettings);

        // MongoDB 8+ doesn't bundle mongosh — look for it in the install registry.
        var shell = ResolveMongosh(registry, installed.InstallPath);

        var host = string.IsNullOrWhiteSpace(serviceSettings.Host) ? "127.0.0.1" : serviceSettings.Host;
        var port = serviceSettings.Port <= 0 ? 27017 : serviceSettings.Port;

        using var process = new System.Diagnostics.Process
        {
            StartInfo = ProcessStreamEncoding.Create(shell)
        };
        process.StartInfo.ArgumentList.Add("--host");
        process.StartInfo.ArgumentList.Add(host);
        process.StartInfo.ArgumentList.Add("--port");
        process.StartInfo.ArgumentList.Add(port.ToString());
        process.StartInfo.ArgumentList.Add("--eval");
        process.StartInfo.ArgumentList.Add($"db.getSiblingDB('{validatedName}').dropDatabase()");

        process.Start();
        var error = process.StandardError.ReadToEnd();
        process.WaitForExit();
        if (process.ExitCode != 0)
        {
            throw new InvalidOperationException(
                string.IsNullOrWhiteSpace(error)
                    ? $"Failed to delete database '{validatedName}' from MongoDB. Exit code {process.ExitCode}."
                    : error);
        }
    }

    private const string MongoshInstallHint = "mongosh is not bundled with MongoDB 8+. Install it: https://www.mongodb.com/try/download/shell";
    private const string ToolsInstallHint = "MongoDB Database Tools are not bundled with MongoDB 8+. Install them: https://www.mongodb.com/try/download/database-tools";

    public static void ExportDump(
        InstallRegistryStore registry,
        AppSettings settings,
        string databaseName,
        string backupPath)
    {
        var validatedName = ValidateDatabaseName(databaseName);
        var (installed, serviceSettings) = ResolveInstall(registry, settings);
        EnsureServiceRunning(serviceSettings);

        var mongoDump = ResolveMongoTool(registry, installed.InstallPath, "bin/mongodump.exe", "mongodump");

        var host = string.IsNullOrWhiteSpace(serviceSettings.Host) ? "127.0.0.1" : serviceSettings.Host;
        var port = serviceSettings.Port <= 0 ? 27017 : serviceSettings.Port;

        using var process = new System.Diagnostics.Process
        {
            StartInfo = ProcessStreamEncoding.Create(mongoDump)
        };
        process.StartInfo.ArgumentList.Add("--host");
        process.StartInfo.ArgumentList.Add(host);
        process.StartInfo.ArgumentList.Add("--port");
        process.StartInfo.ArgumentList.Add(port.ToString());
        process.StartInfo.ArgumentList.Add("--db");
        process.StartInfo.ArgumentList.Add(validatedName);
        process.StartInfo.ArgumentList.Add("--archive=" + backupPath);

        process.Start();
        var error = process.StandardError.ReadToEnd();
        process.WaitForExit();
        if (process.ExitCode != 0)
        {
            throw new InvalidOperationException(
                string.IsNullOrWhiteSpace(error)
                    ? $"mongodump failed with exit code {process.ExitCode}."
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

        var mongoRestore = ResolveMongoTool(registry, installed.InstallPath, "bin/mongorestore.exe", "mongorestore");

        var host = string.IsNullOrWhiteSpace(serviceSettings.Host) ? "127.0.0.1" : serviceSettings.Host;
        var port = serviceSettings.Port <= 0 ? 27017 : serviceSettings.Port;

        using var process = new System.Diagnostics.Process
        {
            StartInfo = ProcessStreamEncoding.Create(mongoRestore)
        };
        process.StartInfo.ArgumentList.Add("--host");
        process.StartInfo.ArgumentList.Add(host);
        process.StartInfo.ArgumentList.Add("--port");
        process.StartInfo.ArgumentList.Add(port.ToString());
        process.StartInfo.ArgumentList.Add("--archive=" + backupPath);
        process.StartInfo.ArgumentList.Add("--nsInclude");
        process.StartInfo.ArgumentList.Add($"{validatedName}.*");

        process.Start();
        var error = process.StandardError.ReadToEnd();
        process.WaitForExit();
        if (process.ExitCode != 0)
        {
            throw new InvalidOperationException(
                string.IsNullOrWhiteSpace(error)
                    ? $"mongorestore failed with exit code {process.ExitCode}."
                    : error);
        }
    }

    private static string ResolveMongosh(InstallRegistryStore registry, string mongoInstallPath)
    {
        // 1. Check if mongosh is bundled with the server (pre-8.0)
        var bundled = PackageBinaryResolver.ResolvePackageBinary(mongoInstallPath, "bin/mongosh.exe")
                      ?? PackageBinaryResolver.ResolvePackageBinary(mongoInstallPath, "bin/mongo.exe");
        if (bundled is not null) return bundled;

        // 2. Look for a separately installed mongosh package (id starts with "mongosh-")
        var mongoshPkg = registry.List().FirstOrDefault(p => p.Id.StartsWith("mongosh-", StringComparison.OrdinalIgnoreCase));
        if (mongoshPkg is not null)
        {
            var path = PackageBinaryResolver.ResolvePackageBinary(mongoshPkg.InstallPath, "bin/mongosh.exe");
            if (path is not null) return path;
        }

        throw new MongoToolMissingException(PackageType.Mongosh, "MongoDB Shell", MongoshInstallHint);
    }

    private static string ResolveMongoTool(InstallRegistryStore registry, string mongoInstallPath, string relativePath, string toolName)
    {
        // 1. Check if bundled with the server
        var bundled = PackageBinaryResolver.ResolvePackageBinary(mongoInstallPath, relativePath);
        if (bundled is not null) return bundled;

        // 2. Look for a separately installed tools package (id starts with "mongodb-tools-")
        var toolsPkg = registry.List().FirstOrDefault(p => p.Id.StartsWith("mongodb-tools-", StringComparison.OrdinalIgnoreCase));
        if (toolsPkg is not null)
        {
            var path = PackageBinaryResolver.ResolvePackageBinary(toolsPkg.InstallPath, relativePath);
            if (path is not null) return path;
        }

        throw new MongoToolMissingException(PackageType.MongodbTools, "MongoDB Tools", ToolsInstallHint);
    }

    private static (InstalledPackage Installed, ServicePortSettings ServiceSettings) ResolveInstall(
        InstallRegistryStore registry,
        AppSettings settings)
    {
        var serviceSettings = settings.Services.TryGetValue(ServiceId.Mongodb, out var configured)
            ? configured
            : SettingsDefaults.DefaultServices()[ServiceId.Mongodb];

        var packageId = serviceSettings.PackageId
            ?? SettingsDefaults.DefaultServices()[ServiceId.Mongodb].PackageId;
        var installed = string.IsNullOrWhiteSpace(packageId)
            ? null
            : registry.GetById(packageId);
        if (installed is null)
        {
            throw new InvalidOperationException("Install MongoDB from Services first.");
        }

        return (installed, serviceSettings);
    }

    private static void EnsureServiceRunning(ServicePortSettings serviceSettings)
    {
        if (!serviceSettings.Enabled)
        {
            throw new InvalidOperationException("MongoDB is disabled in Services settings.");
        }

        var host = string.IsNullOrWhiteSpace(serviceSettings.Host) ? "127.0.0.1" : serviceSettings.Host;
        var port = serviceSettings.Port <= 0 ? 27017 : serviceSettings.Port;
        if (!IsPortOpen(host, port, timeoutMs: 800))
        {
            throw new InvalidOperationException(
                $"MongoDB is not running yet (port {port} is closed). Start it from Services first.");
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

    private static string ValidateDatabaseName(string name)
    {
        var trimmed = name.Trim();
        if (string.IsNullOrWhiteSpace(trimmed))
        {
            throw new ArgumentException("Database name is required.", nameof(name));
        }

        if (!trimmed.All(ch => char.IsLetterOrDigit(ch) || ch is '_' || ch is '-'))
        {
            throw new ArgumentException(
                "Database name may only contain letters, numbers, underscores, and hyphens.",
                nameof(name));
        }

        return trimmed;
    }
}
