using System.Collections.Concurrent;
using System.Diagnostics;

namespace Stackroot.Core.Windows;

public static class ProcessPortTools
{
    private static readonly ConcurrentDictionary<int, PortCacheEntry> PortLookupCache = new();
    private static readonly TimeSpan PortCacheTtl = TimeSpan.FromSeconds(30);

    private readonly record struct PortCacheEntry(DateTimeOffset ExpiresAt, IReadOnlyList<int> Pids);
    public static int? TryResolveListenPort(string executable, IReadOnlyList<string> arguments)
    {
        var parsed = TryParsePortFromArguments(arguments);
        if (parsed is > 0)
        {
            return parsed;
        }

        var name = Path.GetFileNameWithoutExtension(executable);
        if (string.Equals(name, "chromedriver", StringComparison.OrdinalIgnoreCase))
        {
            return 9515;
        }

        return null;
    }

    public static int? TryParsePortFromArguments(IReadOnlyList<string> arguments)
    {
        for (var i = 0; i < arguments.Count; i++)
        {
            var arg = arguments[i];
            if (arg.StartsWith("--port=", StringComparison.OrdinalIgnoreCase))
            {
                if (int.TryParse(arg.AsSpan("--port=".Length), out var port) && port > 0)
                {
                    return port;
                }
            }
            else if (string.Equals(arg, "--port", StringComparison.OrdinalIgnoreCase) && i + 1 < arguments.Count)
            {
                if (int.TryParse(arguments[i + 1], out var port) && port > 0)
                {
                    return port;
                }
            }
        }

        return null;
    }

    public static void InvalidatePortCache(int? port = null)
    {
        if (port is not > 0)
        {
            return;
        }

        PortLookupCache.TryRemove(port.Value, out _);
    }

    public static IReadOnlyList<int> FindPidsListeningOnPort(int port)
    {
        if (port <= 0)
        {
            return [];
        }

        if (PortLookupCache.TryGetValue(port, out var cached) && cached.ExpiresAt > DateTimeOffset.UtcNow)
        {
            return cached.Pids;
        }

        var pids = FindPidsListeningOnPortUncached(port);
        PortLookupCache[port] = new PortCacheEntry(DateTimeOffset.UtcNow.Add(PortCacheTtl), pids);
        return pids;
    }

    private static IReadOnlyList<int> FindPidsListeningOnPortUncached(int port)
    {
        using var process = new Process
        {
            StartInfo = new ProcessStartInfo
            {
                FileName = "netstat",
                Arguments = "-ano",
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

        if (!process.WaitForExit(300))
        {
            try
            {
                process.Kill(entireProcessTree: true);
                process.WaitForExit(200);
            }
            catch
            {
                // Best effort.
            }

            DrainProcessStreams(process);
            return [];
        }

        var output = process.StandardOutput.ReadToEnd();
        DrainProcessStreams(process);

        var pids = new HashSet<int>();
        var targetPort = $":{port}";
        foreach (var rawLine in output.Split('\n', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
        {
            if (!rawLine.StartsWith("TCP", StringComparison.OrdinalIgnoreCase))
            {
                continue;
            }

            var columns = rawLine.Split(' ', StringSplitOptions.RemoveEmptyEntries);
            if (columns.Length < 5)
            {
                continue;
            }

            var localAddress = columns[1];
            var state = columns[3];
            if (!state.Equals("LISTENING", StringComparison.OrdinalIgnoreCase))
            {
                continue;
            }

            if (!LocalAddressMatchesPort(localAddress, targetPort))
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

    public static void KillMatchingListenersOnPort(int port, string executablePath)
    {
        var expectedName = Path.GetFileName(executablePath);
        string? expectedFullPath = null;
        try
        {
            expectedFullPath = Path.GetFullPath(executablePath);
        }
        catch
        {
            // Ignore invalid paths.
        }

        foreach (var pid in FindPidsListeningOnPort(port))
        {
            if (IsMatchingExecutable(pid, expectedName, expectedFullPath))
            {
                ProcessKiller.TryKill(pid);
            }
        }
    }

    private static bool IsMatchingExecutable(int pid, string expectedName, string? expectedFullPath)
    {
        try
        {
            using var process = Process.GetProcessById(pid);
            var executablePath = process.MainModule?.FileName;
            if (string.IsNullOrWhiteSpace(executablePath))
            {
                return process.ProcessName.Contains(Path.GetFileNameWithoutExtension(expectedName), StringComparison.OrdinalIgnoreCase);
            }

            if (expectedFullPath is not null &&
                string.Equals(Path.GetFullPath(executablePath), expectedFullPath, StringComparison.OrdinalIgnoreCase))
            {
                return true;
            }

            return string.Equals(Path.GetFileName(executablePath), expectedName, StringComparison.OrdinalIgnoreCase);
        }
        catch
        {
            return false;
        }
    }

    private static void DrainProcessStreams(Process process)
    {
        try
        {
            _ = process.StandardError.ReadToEnd();
        }
        catch
        {
            // Best effort — process may already be gone.
        }
    }

    private static bool LocalAddressMatchesPort(string localAddress, string targetPort)
    {
        if (localAddress.EndsWith(targetPort, StringComparison.Ordinal))
        {
            return true;
        }

        if (localAddress.StartsWith("[", StringComparison.Ordinal))
        {
            var endBracket = localAddress.IndexOf(']');
            if (endBracket > -1)
            {
                var suffix = localAddress[(endBracket + 1)..];
                if (suffix.Equals(targetPort, StringComparison.Ordinal))
                {
                    return true;
                }
            }
        }

        return false;
    }
}
