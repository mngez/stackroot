using System.Diagnostics;
using System.Text;
using Stackroot.Core.Abstractions;

namespace Stackroot.Core.Databases;

public static class DatabaseConfigWriter
{
    private static readonly Encoding Utf8NoBom = new UTF8Encoding(encoderShouldEmitUTF8Identifier: false);

    public static string DatabaseDataDir(StackrootPaths paths, string engine)
        => Path.Combine(paths.DataRoot, "data", engine);

    public static (string ConfigPath, string DataDir) WriteMariaDbConfig(
        StackrootPaths paths,
        ServicePortSettings settings,
        string engine)
    {
        var dataDir = DatabaseDataDir(paths, engine);
        var confDir = Path.Combine(paths.ConfigRoot, engine);
        Directory.CreateDirectory(confDir);
        Directory.CreateDirectory(dataDir);

        var configPath = Path.Combine(confDir, $"{engine}.ini");
        var logFile = Path.Combine(paths.LogsRoot, $"{engine}.log").Replace('\\', '/');
        var content = $"""
            [client]
            port={settings.Port}
            host={settings.Host}

            [mysqld]
            port={settings.Port}
            bind-address={settings.Host}
            datadir={dataDir.Replace('\\', '/')}
            log-error={logFile}
            character-set-server=utf8mb4
            collation-server=utf8mb4_unicode_ci
            default-storage-engine=InnoDB
            max_connections=200
            wait_timeout=28800
            interactive_timeout=28800
            net_read_timeout=120
            net_write_timeout=120
            max_allowed_packet=256M

            """;

        File.WriteAllText(configPath, content, Utf8NoBom);
        return (configPath, dataDir);
    }

    public static void EnsureMariaDbInitialized(
        string mysqldPath,
        string configPath,
        string dataDir,
        string workingDirectory,
        string engine = "mysql")
    {
        if (IsMariaDbDataDirInitialized(dataDir))
        {
            return;
        }

        Directory.CreateDirectory(dataDir);
        if (string.Equals(engine, "mariadb", StringComparison.OrdinalIgnoreCase))
        {
            EnsureMariaDbViaInstallDb(mysqldPath, dataDir, workingDirectory);
            return;
        }

        using var process = new Process
        {
            StartInfo = ProcessStreamEncoding.Create(mysqldPath, workingDirectory)
        };
        process.StartInfo.ArgumentList.Add($"--defaults-file={configPath}");
        process.StartInfo.ArgumentList.Add("--initialize-insecure");

        process.Start();
        var output = process.StandardOutput.ReadToEnd();
        var error = process.StandardError.ReadToEnd();
        process.WaitForExit();
        if (process.ExitCode != 0)
        {
            var detail = string.Join(
                Environment.NewLine,
                new[] { error, output }.Where(line => !string.IsNullOrWhiteSpace(line)));
            throw new InvalidOperationException(
                string.IsNullOrWhiteSpace(detail)
                    ? $"MySQL initialize-insecure failed with exit code {process.ExitCode}."
                    : detail);
        }
    }

    private static void EnsureMariaDbViaInstallDb(string mysqldPath, string dataDir, string workingDirectory)
    {
        var binDir = Path.GetDirectoryName(mysqldPath)
            ?? throw new InvalidOperationException("MariaDB binary directory was not found.");
        var installDb = Path.Combine(binDir, "mariadb-install-db.exe");
        if (!File.Exists(installDb))
        {
            installDb = Path.Combine(binDir, "mysql_install_db.exe");
        }

        if (!File.Exists(installDb))
        {
            throw new FileNotFoundException("MariaDB install-db executable was not found.", installDb);
        }

        using var process = new Process
        {
            StartInfo = ProcessStreamEncoding.Create(installDb, workingDirectory)
        };
        process.StartInfo.ArgumentList.Add($"--datadir={dataDir}");

        process.Start();
        var output = process.StandardOutput.ReadToEnd();
        var error = process.StandardError.ReadToEnd();
        process.WaitForExit();
        if (process.ExitCode != 0 || !IsMariaDbDataDirInitialized(dataDir))
        {
            var detail = string.Join(
                Environment.NewLine,
                new[] { error, output }.Where(line => !string.IsNullOrWhiteSpace(line)));
            throw new InvalidOperationException(
                string.IsNullOrWhiteSpace(detail)
                    ? $"MariaDB install-db failed with exit code {process.ExitCode}."
                    : detail);
        }
    }

    public static bool IsMariaDbDataDirInitialized(string dataDir)
        => Directory.Exists(Path.Combine(dataDir, "mysql"));

    public static string BootstrapInitPath(StackrootPaths paths, string engine)
        => Path.Combine(paths.ConfigRoot, engine, "bootstrap-init.sql");

    public static void WriteMariaDbBootstrapInit(
        StackrootPaths paths,
        string engine,
        string? username,
        string? password)
    {
        var user = string.IsNullOrWhiteSpace(username) ? "root" : username.Trim();
        var escaped = (password ?? string.Empty).Replace("'", "''", StringComparison.Ordinal);
        var bootstrapPath = BootstrapInitPath(paths, engine);
        Directory.CreateDirectory(Path.GetDirectoryName(bootstrapPath)!);
        var sql =
            $"ALTER USER '{user}'@'localhost' IDENTIFIED BY '{escaped}';" +
            $"CREATE USER IF NOT EXISTS '{user}'@'127.0.0.1' IDENTIFIED BY '{escaped}';" +
            $"CREATE USER IF NOT EXISTS '{user}'@'%' IDENTIFIED BY '{escaped}';" +
            $"GRANT ALL PRIVILEGES ON *.* TO '{user}'@'localhost' WITH GRANT OPTION;" +
            $"GRANT ALL PRIVILEGES ON *.* TO '{user}'@'127.0.0.1' WITH GRANT OPTION;" +
            $"GRANT ALL PRIVILEGES ON *.* TO '{user}'@'%' WITH GRANT OPTION;" +
            "FLUSH PRIVILEGES;";

        File.WriteAllText(bootstrapPath, sql, Utf8NoBom);
    }

    public static void ClearMariaDbBootstrapInit(StackrootPaths paths, string engine)
    {
        var bootstrapPath = BootstrapInitPath(paths, engine);
        if (File.Exists(bootstrapPath))
        {
            File.Delete(bootstrapPath);
        }
    }

    public static (string DataDir, string ConfigPath) WritePostgreSqlConfig(
        StackrootPaths paths,
        ServicePortSettings settings)
    {
        var dataDir = DatabaseDataDir(paths, "postgresql");
        Directory.CreateDirectory(dataDir);

        var configPath = Path.Combine(dataDir, "postgresql.conf");
        if (!File.Exists(Path.Combine(dataDir, "PG_VERSION")))
        {
            return (dataDir, configPath);
        }

        var content = $"""
            # Generated by Stackroot
            listen_addresses = '{settings.Host}'
            port = {settings.Port}
            max_connections = 200
            shared_buffers = 256MB
            work_mem = 16MB
            maintenance_work_mem = 128MB
            logging_collector = off

            """;

        File.WriteAllText(configPath, content, Utf8NoBom);
        return (dataDir, configPath);
    }

    public static void EnsurePostgreSqlInitialized(string initdbPath, string dataDir)
    {
        if (File.Exists(Path.Combine(dataDir, "PG_VERSION")))
        {
            return;
        }

        Directory.CreateDirectory(dataDir);
        var startInfo = ProcessStreamEncoding.Create(initdbPath);
        startInfo.Arguments = $"-D \"{dataDir}\" -U postgres -E UTF8 -A trust";
        using var process = new Process { StartInfo = startInfo };

        process.Start();
        var output = process.StandardOutput.ReadToEnd();
        var error = process.StandardError.ReadToEnd();
        process.WaitForExit();
        if (process.ExitCode != 0)
        {
            throw new InvalidOperationException(string.IsNullOrWhiteSpace(error) ? output : error);
        }
    }

    public static string WriteMongoDbConfig(StackrootPaths paths, ServicePortSettings settings)
    {
        var dataDir = DatabaseDataDir(paths, "mongodb");
        var confDir = Path.Combine(paths.ConfigRoot, "mongodb");
        Directory.CreateDirectory(confDir);
        Directory.CreateDirectory(dataDir);

        var configPath = Path.Combine(confDir, "mongod.conf");
        var logPath = Path.Combine(confDir, "mongod.log").Replace('\\', '/');
        var content = $"""
            # Generated by Stackroot
            storage:
              dbPath: {dataDir.Replace('\\', '/')}
            systemLog:
              destination: file
              path: {logPath}
              logAppend: true
            net:
              port: {settings.Port}
              bindIp: {settings.Host}

            """;

        File.WriteAllText(configPath, content, Encoding.UTF8);
        return configPath;
    }
}
