using System.Collections.Concurrent;
using System.Diagnostics;
using System.Text;
using Stackroot.Core.Windows;

namespace Stackroot.Core.Services;

internal static class ServiceProcessTools
{
    private static readonly ConcurrentDictionary<int, StderrLogSink> StderrSinks = new();

    public static Process StartProcess(
        string fileName,
        IEnumerable<string> arguments,
        string workingDirectory,
        IProcessJobManager jobManager,
        IReadOnlyDictionary<string, string?>? environment = null,
        string? stderrLogPath = null)
    {
        var startInfo = new ProcessStartInfo
        {
            FileName = fileName,
            WorkingDirectory = workingDirectory,
            UseShellExecute = false,
            RedirectStandardOutput = !string.IsNullOrWhiteSpace(stderrLogPath),
            RedirectStandardError = !string.IsNullOrWhiteSpace(stderrLogPath),
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

        if (!string.IsNullOrWhiteSpace(stderrLogPath))
        {
            StderrSinks[process.Id] = new StderrLogSink(process, stderrLogPath);
        }

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

    public static bool IsExecutableUnderInstallPath(int pid, string installPath) =>
        ProcessImageTools.IsExecutableUnderInstallPath(pid, installPath);

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

    private sealed class StderrLogSink : IDisposable
    {
        private readonly StreamWriter _writer;
        private readonly Process _process;
        private int _disposed;

        public StderrLogSink(Process process, string logPath)
        {
            _process = process;
            Directory.CreateDirectory(Path.GetDirectoryName(logPath)!);
            _writer = new StreamWriter(logPath, append: true, new UTF8Encoding(encoderShouldEmitUTF8Identifier: false))
            {
                AutoFlush = true
            };
            _writer.WriteLine($"[{DateTimeOffset.UtcNow:u}] --- started pid={process.Id} ---");

            process.EnableRaisingEvents = true;
            process.ErrorDataReceived += OnErrorData;
            process.Exited += OnExited;
            process.BeginErrorReadLine();
        }

        private void OnErrorData(object? sender, DataReceivedEventArgs e)
        {
            if (!string.IsNullOrEmpty(e.Data))
            {
                _writer.WriteLine(e.Data);
            }
        }

        private void OnExited(object? sender, EventArgs e)
        {
            var code = SafeExitCode(_process);
            _writer.WriteLine($"[{DateTimeOffset.UtcNow:u}] --- exited code={code} ---");
            Dispose();
            StderrSinks.TryRemove(_process.Id, out _);
        }

        public void Dispose()
        {
            if (Interlocked.Exchange(ref _disposed, 1) != 0)
            {
                return;
            }

            try
            {
                _writer.Dispose();
            }
            catch
            {
                // Best effort only.
            }
        }

        private static string SafeExitCode(Process process)
        {
            try
            {
                return process.ExitCode.ToString();
            }
            catch
            {
                return "unknown";
            }
        }
    }
}

