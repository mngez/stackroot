using System.Diagnostics;
using System.Net.Sockets;
using Stackroot.Core.Abstractions;
using Stackroot.Core.Catalog;
using Stackroot.Core.Windows;

namespace Stackroot.Core.Nginx;

public static class NginxControl
{
    public sealed record NginxReloadResult(bool Ok, bool Restarted, string? Message = null, int? Pid = null);

    public static void stopNginx(string prefix, string binPath, IProcessJobManager jobManager)
    {
        StopNginx(prefix, binPath, jobManager);
    }

    public static Task<NginxReloadResult> reloadOrRestartNginx(
        StackrootPaths paths,
        string nginxInstallPath,
        IProcessJobManager jobManager,
        string? host = null,
        int? listenPort = null,
        CancellationToken cancellationToken = default)
    {
        return ReloadOrStartNginxAsync(paths, nginxInstallPath, jobManager, host, listenPort, cancellationToken);
    }

    public static void StopNginx(string prefix, string binPath, IProcessJobManager jobManager)
    {
        if (!File.Exists(binPath))
        {
            return;
        }

        var pid = ReadMasterPid(prefix);
        if (pid is > 0 && IsPidAlive(pid.Value))
        {
            using var stopProcess = StartManagedUtility(binPath, ["-p", prefix, "-s", "stop"], prefix, jobManager);
            stopProcess.WaitForExit(3000);
        }
        else if (pid is > 0)
        {
            ProcessKiller.TryKill(pid.Value);
            RemoveStalePidFile(prefix);
        }

        RemoveStalePidFile(prefix);
    }

    public static void StopManagedNginx(
        StackrootPaths paths,
        string nginxInstallPath,
        IProcessJobManager jobManager,
        int listenPort,
        int? trackedPid = null)
    {
        var prefix = NginxRuntime.nginxPrefix(paths);
        var binPath = PackageBinaryResolver.ResolvePackageBinary(nginxInstallPath, "nginx.exe");
        if (binPath is null)
        {
            return;
        }

        StopNginx(prefix, binPath, jobManager);
        if (listenPort > 0)
        {
            KillOwnedNginxListenersOnPort(prefix, nginxInstallPath, listenPort, trackedPid);
        }

        RemoveStalePidFile(prefix);
    }

    public static async Task<NginxReloadResult> ReloadNginxAsync(
        StackrootPaths paths,
        string nginxInstallPath,
        IProcessJobManager jobManager,
        string? host = null,
        int? listenPort = null,
        CancellationToken cancellationToken = default)
    {
        var prefix = NginxRuntime.nginxPrefix(paths);
        var binPath = PackageBinaryResolver.ResolvePackageBinary(nginxInstallPath, "nginx.exe");
        if (binPath is null)
        {
            return new NginxReloadResult(false, false, "nginx.exe not found");
        }

        var test = RunManagedUtility(binPath, ["-t", "-p", prefix], prefix, jobManager);
        if (test.ExitCode != 0)
        {
            return new NginxReloadResult(false, false, test.ErrorOutput ?? test.Output ?? "Configuration test failed");
        }

        var targetHost = string.IsNullOrWhiteSpace(host) ? "127.0.0.1" : host;
        var targetPort = listenPort.GetValueOrDefault(80);
        var pid = ReadMasterPid(prefix);
        var masterAlive = pid is > 0 && IsPidAlive(pid.Value);
        var portOpen = await IsPortOpenAsync(targetHost, targetPort, cancellationToken);

        if (!masterAlive || !portOpen)
        {
            return new NginxReloadResult(false, false, "Nginx is not running — start it manually first.");
        }

        var reload = RunManagedUtility(binPath, ["-p", prefix, "-s", "reload"], prefix, jobManager);
        return reload.ExitCode == 0
            ? new NginxReloadResult(true, false, Pid: pid)
            : new NginxReloadResult(false, false, reload.ErrorOutput ?? reload.Output ?? "Reload failed");
    }

    private static async Task<NginxReloadResult> ReloadOrStartNginxAsync(
        StackrootPaths paths,
        string nginxInstallPath,
        IProcessJobManager jobManager,
        string? host = null,
        int? listenPort = null,
        CancellationToken cancellationToken = default)
    {
        var prefix = NginxRuntime.nginxPrefix(paths);
        var binPath = PackageBinaryResolver.ResolvePackageBinary(nginxInstallPath, "nginx.exe");
        if (binPath is null)
        {
            return new NginxReloadResult(false, false, "nginx.exe not found");
        }

        var test = RunManagedUtility(binPath, ["-t", "-p", prefix], prefix, jobManager);
        if (test.ExitCode != 0)
        {
            return new NginxReloadResult(false, false, test.ErrorOutput ?? test.Output ?? "Configuration test failed");
        }

        var targetHost = string.IsNullOrWhiteSpace(host) ? "127.0.0.1" : host;
        var targetPort = listenPort.GetValueOrDefault(80);
        var pid = ReadMasterPid(prefix);
        var masterAlive = pid is > 0 && IsPidAlive(pid.Value);
        var portOpen = await IsPortOpenAsync(targetHost, targetPort, cancellationToken);

        if (masterAlive && portOpen)
        {
            var reload = RunManagedUtility(binPath, ["-p", prefix, "-s", "reload"], prefix, jobManager);
            return reload.ExitCode == 0
                ? new NginxReloadResult(true, false, Pid: pid)
                : new NginxReloadResult(false, false, reload.ErrorOutput ?? reload.Output ?? "Reload failed");
        }

        // Nginx not running — start it fresh
        StopNginx(prefix, binPath, jobManager);
        RemoveStalePidFile(prefix);

        var process = ServiceStart(binPath, ["-p", prefix], prefix, jobManager);
        await Task.Delay(350, cancellationToken);

        var started = await IsPortOpenAsync(targetHost, targetPort, cancellationToken);
        if (!started)
        {
            ProcessKiller.TryKill(process.Id);
            return new NginxReloadResult(false, true, "Nginx did not start listening");
        }

        return new NginxReloadResult(true, true, Pid: process.Id);
    }

    private static (int ExitCode, string? Output, string? ErrorOutput) RunManagedUtility(
        string fileName,
        IReadOnlyList<string> args,
        string cwd,
        IProcessJobManager jobManager)
    {
        using var process = StartManagedUtility(fileName, args, cwd, jobManager);
        process.WaitForExit(8000);
        var output = process.StandardOutput.ReadToEnd().Trim();
        var errorOutput = process.StandardError.ReadToEnd().Trim();
        return (process.ExitCode, string.IsNullOrWhiteSpace(output) ? null : output, string.IsNullOrWhiteSpace(errorOutput) ? null : errorOutput);
    }

    private static Process StartManagedUtility(
        string fileName,
        IReadOnlyList<string> args,
        string cwd,
        IProcessJobManager jobManager)
    {
        var startInfo = new ProcessStartInfo
        {
            FileName = fileName,
            WorkingDirectory = cwd,
            UseShellExecute = false,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            CreateNoWindow = true
        };

        foreach (var arg in args)
        {
            startInfo.ArgumentList.Add(arg);
        }

        var process = new Process { StartInfo = startInfo };
        if (!process.Start())
        {
            throw new InvalidOperationException($"Failed to start process: {fileName}");
        }

        jobManager.AssignProcess(process.Id);
        return process;
    }

    private static Process ServiceStart(
        string fileName,
        IReadOnlyList<string> args,
        string cwd,
        IProcessJobManager jobManager)
    {
        var startInfo = new ProcessStartInfo
        {
            FileName = fileName,
            WorkingDirectory = cwd,
            UseShellExecute = false,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            CreateNoWindow = true
        };

        foreach (var arg in args)
        {
            startInfo.ArgumentList.Add(arg);
        }

        var process = new Process { StartInfo = startInfo };
        if (!process.Start())
        {
            throw new InvalidOperationException($"Failed to start nginx process: {fileName}");
        }

        jobManager.AssignProcess(process.Id);
        return process;
    }

    private static int? ReadMasterPid(string prefix)
    {
        var pidPath = Path.Combine(prefix, "logs", "nginx.pid");
        if (!File.Exists(pidPath))
        {
            return null;
        }

        var text = File.ReadAllText(pidPath).Trim();
        return int.TryParse(text, out var pid) && pid > 0 ? pid : null;
    }

    private static bool IsPidAlive(int pid)
    {
        try
        {
            using var process = Process.GetProcessById(pid);
            return !process.HasExited;
        }
        catch
        {
            return false;
        }
    }

    private static void RemoveStalePidFile(string prefix)
    {
        var pidFile = Path.Combine(prefix, "logs", "nginx.pid");
        if (!File.Exists(pidFile))
        {
            return;
        }

        var pid = ReadMasterPid(prefix);
        if (pid is null || !IsPidAlive(pid.Value))
        {
            File.Delete(pidFile);
        }
    }

    private static void KillOwnedNginxListenersOnPort(string prefix, string nginxInstallPath, int port, int? trackedPid)
    {
        if (port <= 0)
        {
            return;
        }

        var owned = new HashSet<int>();
        if (trackedPid is > 0 && IsPidAlive(trackedPid.Value))
        {
            owned.Add(trackedPid.Value);
        }

        var masterPid = ReadMasterPid(prefix);
        if (masterPid is > 0)
        {
            owned.Add(masterPid.Value);
        }

        foreach (var listenerPid in FindPidsListeningOnPort(port))
        {
            if (IsNginxFromInstall(listenerPid, nginxInstallPath))
            {
                owned.Add(listenerPid);
            }
        }

        foreach (var ownedPid in owned)
        {
            ProcessKiller.TryKill(ownedPid);
        }
    }

    private static bool IsNginxFromInstall(int pid, string nginxInstallPath)
    {
        if (!ProcessNameContains(pid, "nginx"))
        {
            return false;
        }

        return IsExecutableUnderInstallPath(pid, nginxInstallPath);
    }

    private static bool ProcessNameContains(int pid, string fragment)
    {
        try
        {
            using var process = Process.GetProcessById(pid);
            return process.ProcessName.Contains(fragment, StringComparison.OrdinalIgnoreCase);
        }
        catch
        {
            return false;
        }
    }

    private static bool IsExecutableUnderInstallPath(int pid, string installPath)
    {
        if (string.IsNullOrWhiteSpace(installPath))
        {
            return false;
        }

        try
        {
            using var process = Process.GetProcessById(pid);
            var executablePath = process.MainModule?.FileName;
            if (string.IsNullOrWhiteSpace(executablePath))
            {
                return false;
            }

            var normalizedExecutable = Path.GetFullPath(executablePath);
            var normalizedInstall = Path.GetFullPath(installPath.TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar));
            return normalizedExecutable.StartsWith(normalizedInstall + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase)
                || string.Equals(normalizedExecutable, normalizedInstall, StringComparison.OrdinalIgnoreCase);
        }
        catch
        {
            return false;
        }
    }

    private static IReadOnlyList<int> FindPidsListeningOnPort(int port)
    {
        if (port <= 0)
        {
            return [];
        }

        using var process = new Process
        {
            StartInfo = new ProcessStartInfo
            {
                FileName = "netstat",
                Arguments = "-ano -p tcp",
                UseShellExecute = false,
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                CreateNoWindow = true
            }
        };

        if (!process.Start())
        {
            return [];
        }

        var output = process.StandardOutput.ReadToEnd();
        process.WaitForExit(3000);
        var match = $":{port}";
        var pids = new HashSet<int>();
        foreach (var line in output.Split('\n', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
        {
            if (!line.StartsWith("TCP", StringComparison.OrdinalIgnoreCase))
            {
                continue;
            }

            var columns = line.Split(' ', StringSplitOptions.RemoveEmptyEntries);
            if (columns.Length < 5 || !columns[1].EndsWith(match, StringComparison.Ordinal) || !columns[3].Equals("LISTENING", StringComparison.OrdinalIgnoreCase))
            {
                continue;
            }

            if (int.TryParse(columns[4], out var pid) && pid > 0)
            {
                pids.Add(pid);
            }
        }

        return [.. pids];
    }

    private static async Task<bool> IsPortOpenAsync(string host, int port, CancellationToken cancellationToken = default)
    {
        try
        {
            using var socket = new TcpClient();
            using var cts = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
            cts.CancelAfter(800);
            await socket.ConnectAsync(host, port, cts.Token);
            return true;
        }
        catch
        {
            return false;
        }
    }

    private static async Task WaitForPortStateAsync(
        string host,
        int port,
        bool shouldBeOpen,
        int maxMs = 3000,
        CancellationToken cancellationToken = default)
    {
        var deadline = DateTime.UtcNow.AddMilliseconds(maxMs);
        while (DateTime.UtcNow < deadline)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var open = await IsPortOpenAsync(host, port, cancellationToken);
            if (open == shouldBeOpen)
            {
                return;
            }

            await Task.Delay(120, cancellationToken);
        }
    }
}
