using System.Diagnostics;
using System.Net.Sockets;
using System.Text;
using Stackroot.Core.Abstractions;
using Stackroot.Core.Catalog;

namespace Stackroot.Core.Databases;

public static class MariaDbCredentialSync
{
    private const int SqlTimeoutMs = 8000;
    private static readonly Encoding Utf8NoBom = new UTF8Encoding(encoderShouldEmitUTF8Identifier: false);

    public static Action<string>? ActivityLog { get; set; }

    public static StackrootPaths? Paths { get; set; }

    public static Func<ServiceId, CancellationToken, Task>? RestartServiceAsync { get; set; }

    public static void Configure(
        StackrootPaths paths,
        Func<ServiceId, CancellationToken, Task> restartServiceAsync)
    {
        Paths = paths;
        RestartServiceAsync = restartServiceAsync;
    }

    public static string DescribeClientConnection(ServicePortSettings serviceSettings)
    {
        var port = serviceSettings.Port <= 0 ? 3306 : serviceSettings.Port;
        return $"mysql client host={ClientHost(serviceSettings)} port={port}";
    }

    public static async Task<bool> EnsureCredentialsWhenReadyAsync(
        string installPath,
        ServiceId serviceId,
        ServicePortSettings serviceSettings,
        string? username,
        string? password,
        CancellationToken cancellationToken = default,
        int maxAttempts = 12,
        bool portAlreadyOpen = false)
    {
        if (ResolveMysqlClient(installPath) is null)
        {
            return false;
        }

        var port = serviceSettings.Port <= 0 ? 3306 : serviceSettings.Port;
        var portHost = NormalizePortProbeHost(serviceSettings.Host);

        if (!portAlreadyOpen
            && !await WaitForSqlPortAsync(portHost, port, cancellationToken, maxAttempts: 24, delayMs: 250).ConfigureAwait(false))
        {
            return false;
        }

        LogActivity($"SQL credential sync using {DescribeClientConnection(serviceSettings)}");

        var attempts = portAlreadyOpen ? 6 : maxAttempts;
        for (var attempt = 0; attempt < attempts; attempt++)
        {
            cancellationToken.ThrowIfCancellationRequested();
            try
            {
                await EnsureRootPasswordAsync(installPath, serviceId, serviceSettings, username, password, cancellationToken)
                    .ConfigureAwait(false);
                return true;
            }
            catch
            {
                // Server may still be finishing startup.
            }

            if (attempt < attempts - 1)
            {
                await Task.Delay(portAlreadyOpen ? 200 : 300, cancellationToken).ConfigureAwait(false);
            }
        }

        return false;
    }

    /// <summary>
    /// Legacy Stackroot behavior from mysql-client.ts: same host, bootstrap empty password, ALTER USER only.
    /// When root@127.0.0.1 has USAGE-only grants, repair via mysqld --init-file on restart.
    /// </summary>
    public static void EnsureRootPassword(
        string installPath,
        ServiceId serviceId,
        ServicePortSettings serviceSettings,
        string? username,
        string? password)
    {
        EnsureRootPasswordAsync(installPath, serviceId, serviceSettings, username, password)
            .ConfigureAwait(false)
            .GetAwaiter()
            .GetResult();
    }

    public static async Task EnsureRootPasswordAsync(
        string installPath,
        ServiceId serviceId,
        ServicePortSettings serviceSettings,
        string? username,
        string? password,
        CancellationToken cancellationToken = default)
    {
        var mysqlClient = ResolveMysqlClient(installPath)
            ?? throw new InvalidOperationException("mysql.exe not found.");

        var user = NormalizeUser(username);
        var desiredPassword = password ?? string.Empty;
        var port = serviceSettings.Port <= 0 ? 3306 : serviceSettings.Port;
        var clientHost = ClientHost(serviceSettings);

        LogActivity($"EnsureRootPassword ({DescribeEngine(serviceId)}) via {DescribeClientConnection(serviceSettings)}");

        if (CanConnect(mysqlClient, clientHost, port, user, desiredPassword))
        {
            await EnsureClientCanCreateDatabasesAsync(
                    serviceId,
                    installPath,
                    serviceSettings,
                    user,
                    desiredPassword,
                    port,
                    cancellationToken)
                .ConfigureAwait(false);
            return;
        }

        if (!CanConnect(mysqlClient, clientHost, port, user, string.Empty))
        {
            throw new InvalidOperationException(
                "Cannot connect to MySQL/MariaDB — check that the service is running and credentials are correct.");
        }

        var escaped = desiredPassword.Replace("'", "''", StringComparison.Ordinal);
        var bootstrapSql =
            $"ALTER USER '{user}'@'localhost' IDENTIFIED BY '{escaped}'; " +
            $"ALTER USER '{user}'@'127.0.0.1' IDENTIFIED BY '{escaped}'; " +
            "FLUSH PRIVILEGES;";

        if (!RunMysql(mysqlClient, clientHost, port, user, string.Empty, bootstrapSql, out var bootstrapError))
        {
            throw new InvalidOperationException(bootstrapError ?? "Failed to set root password.");
        }

        LogActivity("Root password applied from bootstrap connection");

        if (!CanConnect(mysqlClient, clientHost, port, user, desiredPassword))
        {
            throw new InvalidOperationException(
                "Cannot connect to MySQL/MariaDB using credentials from Database settings.");
        }

        await EnsureClientCanCreateDatabasesAsync(
                serviceId,
                installPath,
                serviceSettings,
                user,
                desiredPassword,
                port,
                cancellationToken)
            .ConfigureAwait(false);
    }

    public static bool TryRepairCreatePrivilege(
        string installPath,
        ServiceId serviceId,
        ServicePortSettings serviceSettings,
        string? username,
        string? password)
    {
        var user = NormalizeUser(username);
        var desiredPassword = password ?? string.Empty;
        LogActivity($"Repairing CREATE privilege for {user}@127.0.0.1 via init-file restart");
        return TryRepairGrantsViaInitFileRestart(serviceId, installPath, serviceSettings, user, desiredPassword);
    }

    public static bool IsInsufficientPrivilegeError(string? error)
    {
        if (string.IsNullOrWhiteSpace(error))
        {
            return false;
        }

        return error.Contains("1044", StringComparison.Ordinal)
               || error.Contains("1045", StringComparison.Ordinal)
               || error.Contains("Access denied", StringComparison.OrdinalIgnoreCase);
    }

    public static void EnsureCredentials(
        string installPath,
        ServiceId serviceId,
        ServicePortSettings serviceSettings,
        string? username,
        string? password)
    {
        EnsureRootPassword(installPath, serviceId, serviceSettings, username, password);
    }

    public static bool ApplyConfiguredCredentials(
        string installPath,
        ServiceId serviceId,
        ServicePortSettings serviceSettings,
        string? username,
        string? password)
    {
        try
        {
            EnsureRootPassword(installPath, serviceId, serviceSettings, username, password);
            return true;
        }
        catch
        {
            return false;
        }
    }

    public static bool TryExecuteSql(
        string installPath,
        ServicePortSettings serviceSettings,
        string? username,
        string? password,
        string sql)
        => TryExecuteSql(installPath, serviceSettings, username, password, sql, out _);

    public static bool TryExecuteSql(
        string installPath,
        ServicePortSettings serviceSettings,
        string? username,
        string? password,
        string sql,
        out string? error)
    {
        error = null;
        var mysqlClient = ResolveMysqlClient(installPath);
        if (mysqlClient is null)
        {
            error = "mysql.exe not found.";
            return false;
        }

        var user = NormalizeUser(username);
        var desiredPassword = password ?? string.Empty;
        var port = serviceSettings.Port <= 0 ? 3306 : serviceSettings.Port;
        return RunMysql(
            mysqlClient,
            ClientHost(serviceSettings),
            port,
            user,
            desiredPassword,
            sql,
            out error);
    }

    public static void LogCreateDatabaseAttempt(ServicePortSettings serviceSettings, string databaseName)
        => LogActivity($"Create database '{databaseName}' via {DescribeClientConnection(serviceSettings)}");

    public static void LogActivity(string message)
        => ActivityLog?.Invoke(message);

    private static void EnsureClientCanCreateDatabases(
        ServiceId serviceId,
        string installPath,
        ServicePortSettings serviceSettings,
        string user,
        string desiredPassword,
        int port)
    {
        EnsureClientCanCreateDatabasesAsync(
                serviceId,
                installPath,
                serviceSettings,
                user,
                desiredPassword,
                port,
                CancellationToken.None)
            .ConfigureAwait(false)
            .GetAwaiter()
            .GetResult();
    }

    private static async Task EnsureClientCanCreateDatabasesAsync(
        ServiceId serviceId,
        string installPath,
        ServicePortSettings serviceSettings,
        string user,
        string desiredPassword,
        int port,
        CancellationToken cancellationToken)
    {
        var mysqlClient = ResolveMysqlClient(installPath)!;
        var clientHost = ClientHost(serviceSettings);
        if (HasCreateDatabasePrivilege(mysqlClient, clientHost, port, user, desiredPassword))
        {
            return;
        }

        LogActivity("Detected USAGE-only grants for TCP client — repairing via init-file restart");
        if (!await TryRepairGrantsViaInitFileRestartAsync(
                serviceId,
                installPath,
                serviceSettings,
                user,
                desiredPassword,
                cancellationToken)
            .ConfigureAwait(false))
        {
            throw new InvalidOperationException(
                "MySQL root account is missing CREATE DATABASE privileges and automatic repair failed.");
        }
    }

    private static bool TryRepairGrantsViaInitFileRestart(
        ServiceId serviceId,
        string installPath,
        ServicePortSettings serviceSettings,
        string user,
        string desiredPassword)
    {
        return Task.Run(() => TryRepairGrantsViaInitFileRestartAsync(
                serviceId,
                installPath,
                serviceSettings,
                user,
                desiredPassword,
                CancellationToken.None))
            .ConfigureAwait(false)
            .GetAwaiter()
            .GetResult();
    }

    private static async Task<bool> TryRepairGrantsViaInitFileRestartAsync(
        ServiceId serviceId,
        string installPath,
        ServicePortSettings serviceSettings,
        string user,
        string desiredPassword,
        CancellationToken cancellationToken)
    {
        if (Paths is null || RestartServiceAsync is null)
        {
            LogActivity("Grant repair unavailable — Stackroot paths or service restart hook is not configured");
            return false;
        }

        var engine = serviceId == ServiceId.Mariadb ? "mariadb" : "mysql";
        DatabaseConfigWriter.WriteMariaDbBootstrapInit(Paths, engine, user, desiredPassword);

        try
        {
            if (RestartServiceAsync is null)
            {
                return false;
            }

            await RestartServiceAsync(serviceId, cancellationToken).ConfigureAwait(false);
        }
        catch (Exception ex)
        {
            LogActivity($"Grant repair restart failed: {ex.Message}");
            return false;
        }

        var port = serviceSettings.Port <= 0 ? 3306 : serviceSettings.Port;
        if (!await WaitForSqlPortAsync(
                NormalizePortProbeHost(serviceSettings.Host),
                port,
                cancellationToken,
                maxAttempts: 40,
                delayMs: 250).ConfigureAwait(false))
        {
            LogActivity("Grant repair restart finished but SQL port did not reopen");
            return false;
        }

        var mysqlClient = ResolveMysqlClient(installPath);
        if (mysqlClient is null)
        {
            return false;
        }

        if (!HasCreateDatabasePrivilege(mysqlClient, ClientHost(serviceSettings), port, user, desiredPassword))
        {
            LogActivity("Grant repair init-file ran but CREATE privilege is still missing");
            return false;
        }

        DatabaseConfigWriter.ClearMariaDbBootstrapInit(Paths, engine);
        LogActivity("Grant repair completed via init-file restart");
        return true;
    }

    private static bool HasCreateDatabasePrivilege(
        string mysqlClient,
        string host,
        int port,
        string user,
        string password)
    {
        if (!RunMysql(mysqlClient, host, port, user, password, "SHOW GRANTS", out _, out var grants))
        {
            return false;
        }

        if (string.IsNullOrWhiteSpace(grants))
        {
            return false;
        }

        return grants.Contains("ALL PRIVILEGES", StringComparison.OrdinalIgnoreCase)
               || grants.Contains(" CREATE ", StringComparison.OrdinalIgnoreCase)
               || grants.Contains("CREATE,", StringComparison.OrdinalIgnoreCase)
               || grants.Contains("\tCREATE ", StringComparison.OrdinalIgnoreCase);
    }

    private static bool WaitForSqlPortSync(string host, int port, int maxAttempts, int delayMs)
    {
        for (var attempt = 0; attempt < maxAttempts; attempt++)
        {
            if (IsTcpPortOpen(host, port))
            {
                return true;
            }

            if (attempt < maxAttempts - 1)
            {
                Thread.Sleep(delayMs);
            }
        }

        return false;
    }

    private static bool IsTcpPortOpen(string host, int port)
    {
        using var client = new TcpClient();
        using var cts = new CancellationTokenSource(800);

        try
        {
            client.ConnectAsync(NormalizePortProbeHost(host), port, cts.Token).GetAwaiter().GetResult();
            return true;
        }
        catch
        {
            return false;
        }
    }

    private static async Task<bool> WaitForSqlPortAsync(
        string host,
        int port,
        CancellationToken cancellationToken,
        int maxAttempts = 24,
        int delayMs = 250)
    {
        for (var attempt = 0; attempt < maxAttempts; attempt++)
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (await IsTcpPortOpenAsync(host, port, cancellationToken).ConfigureAwait(false))
            {
                return true;
            }

            if (attempt < maxAttempts - 1)
            {
                await Task.Delay(delayMs, cancellationToken).ConfigureAwait(false);
            }
        }

        return false;
    }

    private static async Task<bool> IsTcpPortOpenAsync(string host, int port, CancellationToken cancellationToken)
    {
        using var client = new TcpClient();
        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        timeout.CancelAfter(800);

        try
        {
            await client.ConnectAsync(NormalizePortProbeHost(host), port, timeout.Token).ConfigureAwait(false);
            return true;
        }
        catch
        {
            return false;
        }
    }

    private static bool CanConnect(
        string mysqlClient,
        string host,
        int port,
        string user,
        string password)
        => RunMysql(mysqlClient, host, port, user, password, "SELECT 1", out _);

    private static bool RunMysql(
        string mysqlClient,
        string host,
        int port,
        string user,
        string password,
        string sql,
        out string? error)
        => RunMysql(mysqlClient, host, port, user, password, sql, out error, out _);

    private static bool RunMysql(
        string mysqlClient,
        string host,
        int port,
        string user,
        string password,
        string sql,
        out string? error,
        out string stdout)
    {
        error = null;
        stdout = string.Empty;
        using var process = new Process
        {
            StartInfo = ProcessStreamEncoding.Create(mysqlClient)
        };

        AddConnectionArguments(process.StartInfo, host, port);
        process.StartInfo.ArgumentList.Add($"-u{user}");
        if (string.IsNullOrEmpty(password))
        {
            process.StartInfo.ArgumentList.Add("--skip-password");
        }
        else
        {
            process.StartInfo.Environment["MYSQL_PWD"] = password;
        }

        if (SupportsServerPublicKeyFlag(mysqlClient))
        {
            process.StartInfo.ArgumentList.Add("--get-server-public-key");
        }

        process.StartInfo.ArgumentList.Add("--batch");
        process.StartInfo.ArgumentList.Add("--skip-column-names");
        process.StartInfo.ArgumentList.Add("-e");
        process.StartInfo.ArgumentList.Add(sql);

        try
        {
            process.Start();
            var stdoutTask = Task.Run(() => process.StandardOutput.ReadToEnd());
            var stderrTask = Task.Run(() => process.StandardError.ReadToEnd());
            if (!process.WaitForExit(SqlTimeoutMs))
            {
                try
                {
                    process.Kill(entireProcessTree: true);
                }
                catch
                {
                    // Best effort.
                }

                error = $"mysql timed out (host={host} port={port}).";
                return false;
            }

            Task.WaitAll(stdoutTask, stderrTask);
            stdout = stdoutTask.Result;
            if (process.ExitCode != 0)
            {
                var stderr = stderrTask.Result.Trim();
                error = string.IsNullOrWhiteSpace(stderr)
                    ? $"mysql exited with code {process.ExitCode} (host={host} port={port})."
                    : stderr;
                return false;
            }

            return true;
        }
        catch (Exception ex)
        {
            error = ex.Message;
            return false;
        }
    }

    private static void AddConnectionArguments(ProcessStartInfo startInfo, string host, int port)
    {
        startInfo.ArgumentList.Add($"-h{host}");
        startInfo.ArgumentList.Add($"-P{port}");
    }

    private static string ClientHost(ServicePortSettings serviceSettings)
        => string.IsNullOrWhiteSpace(serviceSettings.Host) ? "127.0.0.1" : serviceSettings.Host.Trim();

    private static string NormalizePortProbeHost(string? host)
        => string.IsNullOrWhiteSpace(host) ? "127.0.0.1" : host.Trim();

    private static string NormalizeUser(string? username)
        => string.IsNullOrWhiteSpace(username) ? "root" : username.Trim();

    private static string DescribeEngine(ServiceId serviceId)
        => serviceId == ServiceId.Mariadb ? "MariaDB" : "MySQL";

    private static bool SupportsServerPublicKeyFlag(string mysqlClient)
        => !mysqlClient.Contains("mariadb", StringComparison.OrdinalIgnoreCase);

    public static bool TryImportSqlFile(
        string installPath,
        ServicePortSettings serviceSettings,
        string? username,
        string? password,
        string databaseName,
        string backupPath,
        out string? error)
    {
        error = null;
        var mysqlClient = ResolveMysqlClient(installPath);
        if (mysqlClient is null)
        {
            error = "mysql.exe not found.";
            return false;
        }

        if (!File.Exists(backupPath))
        {
            error = "Backup file was not found.";
            return false;
        }

        var user = NormalizeUser(username);
        var desiredPassword = password ?? string.Empty;
        var port = serviceSettings.Port <= 0 ? 3306 : serviceSettings.Port;
        var host = ClientHost(serviceSettings);

        using var process = new Process
        {
            StartInfo = ProcessStreamEncoding.Create(mysqlClient, redirectStdin: true)
        };

        AddConnectionArguments(process.StartInfo, host, port);
        process.StartInfo.ArgumentList.Add($"-u{user}");
        if (string.IsNullOrEmpty(desiredPassword))
        {
            process.StartInfo.ArgumentList.Add("--skip-password");
        }
        else
        {
            process.StartInfo.Environment["MYSQL_PWD"] = desiredPassword;
        }

        if (SupportsServerPublicKeyFlag(mysqlClient))
        {
            process.StartInfo.ArgumentList.Add("--get-server-public-key");
        }

        process.StartInfo.ArgumentList.Add("--batch");
        process.StartInfo.ArgumentList.Add("--default-character-set=utf8mb4");
        process.StartInfo.ArgumentList.Add(databaseName);

        try
        {
            if (!process.Start())
            {
                error = "Failed to start mysql client.";
                return false;
            }

            var stdoutTask = process.StandardOutput.ReadToEndAsync();
            var stderrTask = process.StandardError.ReadToEndAsync();

            try
            {
                var stdin = process.StandardInput.BaseStream;
                stdin.Write("SET NAMES utf8mb4 COLLATE utf8mb4_unicode_ci;"u8);
                stdin.Write("\n"u8);

                using (var backup = File.OpenRead(backupPath))
                using (var reader = new StreamReader(backup, Utf8NoBom, detectEncodingFromByteOrderMarks: true))
                {
                    string? line;
                    while ((line = reader.ReadLine()) is not null)
                    {
                        if (line.Contains("GTID_PURGED", StringComparison.OrdinalIgnoreCase))
                        {
                            continue;
                        }

                        var bytes = Utf8NoBom.GetBytes(line);
                        stdin.Write(bytes);
                        stdin.Write("\n"u8);
                    }
                }

                process.StandardInput.Close();
            }
            catch (IOException)
            {
                // mysql may close stdin early on SQL errors; stderr below has the real message.
            }

            if (!process.WaitForExit(120_000))
            {
                TryKill(process);
                error = "mysql import timed out.";
                return false;
            }

            var stdout = stdoutTask.GetAwaiter().GetResult();
            var stderr = stderrTask.GetAwaiter().GetResult();

            if (process.ExitCode == 0)
            {
                return true;
            }

            error = string.IsNullOrWhiteSpace(stderr) ? stdout : stderr;
            if (string.IsNullOrWhiteSpace(error))
            {
                error = "mysql import failed.";
            }

            return false;
        }
        catch (Exception ex)
        {
            error = ex.Message;
            return false;
        }
    }

    public static bool TryExportSqlFile(
        string installPath,
        ServicePortSettings serviceSettings,
        string? username,
        string? password,
        string databaseName,
        string backupPath,
        out string? error)
    {
        error = null;
        var dumpClient = ResolveMysqlDumpClient(installPath);
        if (dumpClient is null)
        {
            error = "mysqldump.exe not found.";
            return false;
        }

        var directory = Path.GetDirectoryName(backupPath);
        if (!string.IsNullOrWhiteSpace(directory))
        {
            Directory.CreateDirectory(directory);
        }

        var user = NormalizeUser(username);
        var desiredPassword = password ?? string.Empty;
        var port = serviceSettings.Port <= 0 ? 3306 : serviceSettings.Port;
        var host = ClientHost(serviceSettings);

        using var process = new Process
        {
            StartInfo = ProcessStreamEncoding.Create(dumpClient)
        };

        AddConnectionArguments(process.StartInfo, host, port);
        process.StartInfo.ArgumentList.Add($"-u{user}");
        if (string.IsNullOrEmpty(desiredPassword))
        {
            process.StartInfo.ArgumentList.Add("--skip-password");
        }
        else
        {
            process.StartInfo.Environment["MYSQL_PWD"] = desiredPassword;
        }

        if (SupportsServerPublicKeyFlag(dumpClient))
        {
            process.StartInfo.ArgumentList.Add("--get-server-public-key");
        }

        process.StartInfo.ArgumentList.Add("--single-transaction");
        process.StartInfo.ArgumentList.Add("--add-drop-table");
        process.StartInfo.ArgumentList.Add("--set-gtid-purged=OFF");
        process.StartInfo.ArgumentList.Add("--quick");
        process.StartInfo.ArgumentList.Add("--routines");
        process.StartInfo.ArgumentList.Add("--triggers");
        process.StartInfo.ArgumentList.Add("--events");
        process.StartInfo.ArgumentList.Add("--default-character-set=utf8mb4");
        process.StartInfo.ArgumentList.Add($"--result-file={backupPath}");
        process.StartInfo.ArgumentList.Add(databaseName);

        try
        {
            if (!process.Start())
            {
                error = "Failed to start mysqldump.";
                return false;
            }

            var stdout = process.StandardOutput.ReadToEnd();
            var stderr = process.StandardError.ReadToEnd();
            if (!process.WaitForExit(120_000))
            {
                TryKill(process);
                error = "mysqldump timed out.";
                return false;
            }

            if (process.ExitCode == 0 && File.Exists(backupPath) && new FileInfo(backupPath).Length > 0)
            {
                return true;
            }

            error = string.IsNullOrWhiteSpace(stderr) ? stdout : stderr;
            if (string.IsNullOrWhiteSpace(error))
            {
                error = "mysqldump completed without producing a backup file.";
            }

            return false;
        }
        catch (Exception ex)
        {
            error = ex.Message;
            return false;
        }
    }

    private static string? ResolveMysqlClient(string installPath)
        => PackageBinaryResolver.ResolvePackageBinary(installPath, "bin/mysql.exe");

    private static string? ResolveMysqlDumpClient(string installPath)
        => PackageBinaryResolver.ResolvePackageBinary(installPath, "bin/mysqldump.exe");

    private static void TryKill(Process process)
    {
        try
        {
            if (!process.HasExited)
            {
                process.Kill(entireProcessTree: true);
            }
        }
        catch
        {
            // Best effort after a timeout.
        }
    }
}
