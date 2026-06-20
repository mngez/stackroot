using System.Diagnostics;
using Stackroot.Core.Windows;

namespace Stackroot.Core.Services;

internal static class ServiceProcessTools
{
    public static Process StartProcess(
        string fileName,
        IEnumerable<string> arguments,
        string workingDirectory,
        IProcessJobManager jobManager,
        IReadOnlyDictionary<string, string?>? environment = null)
    {
        var startInfo = new ProcessStartInfo
        {
            FileName = fileName,
            WorkingDirectory = workingDirectory,
            UseShellExecute = false,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            CreateNoWindow = true
        };

        foreach (var argument in arguments)
        {
            startInfo.ArgumentList.Add(argument);
        }

        if (environment is not null)
        {
            foreach (var pair in environment)
            {
                startInfo.Environment[pair.Key] = pair.Value ?? string.Empty;
            }
        }

        var process = new Process
        {
            StartInfo = startInfo
        };

        if (!process.Start())
        {
            throw new InvalidOperationException($"Failed to start process: {fileName}");
        }

        try
        {
            jobManager.AssignProcess(process.Id);
        }
        catch
        {
            ProcessKiller.TryKill(process.Id);
            throw;
        }

        ServiceProcessPriority.Apply(process);
        return process;
    }

    public static IReadOnlyList<int> FindPidsListeningOnPort(int port) =>
        ProcessPortTools.FindPidsListeningOnPort(port);

    public static bool IsProcessAlive(int pid)
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

    public static bool IsExecutableUnderInstallPath(int pid, string installPath)
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

    public static bool ProcessNameContains(int pid, string fragment)
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

    public static void TryKillListenersOnPort(int port, Func<int, bool> shouldKill)
    {
        foreach (var pid in FindPidsListeningOnPort(port))
        {
            if (shouldKill(pid))
            {
                ProcessKiller.TryKill(pid);
            }
        }
    }

    public static void TryKillPids(IEnumerable<int> pids)
    {
        foreach (var pid in pids)
        {
            if (pid > 0)
            {
                ProcessKiller.TryKill(pid);
            }
        }
    }

    [Obsolete("Use StackrootManagedProcessResolver — kills every listener on the port.")]
    public static void KillListenersOnPort(int port) =>
        KillForeignListenersOnPort(port, keepPids: null);

    public static void KillForeignListenersOnPort(int port, IEnumerable<int>? keepPids)
    {
        var keep = keepPids is null
            ? new HashSet<int>()
            : keepPids.Where(pid => pid > 0).ToHashSet();

        foreach (var pid in FindPidsListeningOnPort(port))
        {
            if (keep.Contains(pid))
            {
                continue;
            }

            ProcessKiller.TryKill(pid);
        }
    }

}

