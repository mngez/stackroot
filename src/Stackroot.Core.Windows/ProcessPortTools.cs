using System.Collections.Concurrent;
using System.Diagnostics;
using Stackroot.Core.Abstractions;

namespace Stackroot.Core.Windows;

public static class ProcessPortTools
{
    private static readonly ConcurrentDictionary<int, PortCacheEntry> PortLookupCache = new();
    private static readonly TimeSpan PortCacheTtl = TimeSpan.FromSeconds(10);
    private static readonly TimeSpan PortCachePurgeInterval = TimeSpan.FromMinutes(5);
    private const int PortCacheHighWaterMark = 48;
    private static DateTimeOffset _lastPortCachePurgeAt;
    private static readonly object NetstatMapSync = new();
    private static readonly TimeSpan NetstatMapTtl = TimeSpan.FromSeconds(10);
    private static IReadOnlyDictionary<int, List<int>>? _netstatPortPidMap;
    private static DateTimeOffset _netstatMapExpiresAt;

    public static void PurgeExpiredPortCacheEntries()
    {
        var now = DateTimeOffset.UtcNow;
        var overCapacity = PortLookupCache.Count > PortCacheHighWaterMark;
        if (!overCapacity && now - _lastPortCachePurgeAt < PortCachePurgeInterval)
        {
            return;
        }

        _lastPortCachePurgeAt = now;
        foreach (var (port, entry) in PortLookupCache)
        {
            if (entry.ExpiresAt <= now)
            {
                PortLookupCache.TryRemove(port, out _);
            }
        }
    }

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
        lock (NetstatMapSync)
        {
            _netstatPortPidMap = null;
        }

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
            var remaining = cached.ExpiresAt - DateTimeOffset.UtcNow;
            if (remaining > PortCacheTtl * 0.4)
            {
                return cached.Pids;
            }

            return FilterAlivePids(cached.Pids, port);
        }

        var pids = FilterAlivePids(FindPidsListeningOnPortUncached(port), port);
        PortLookupCache[port] = new PortCacheEntry(DateTimeOffset.UtcNow.Add(PortCacheTtl), pids);
        return pids;
    }

    private static IReadOnlyList<int> FilterAlivePids(IReadOnlyList<int> pids, int port)
    {
        if (pids.Count == 0)
        {
            return pids;
        }

        var alive = pids.Where(IsProcessAlive).ToList();
        if (alive.Count != pids.Count)
        {
            InvalidatePortCache(port);
        }

        return alive;
    }

    private static bool IsProcessAlive(int pid)
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

    private static IReadOnlyList<int> FindPidsListeningOnPortUncached(int port)
    {
        var map = GetPortPidMap();
        return map.TryGetValue(port, out var pids) ? pids : [];
    }

    private static IReadOnlyDictionary<int, List<int>> GetPortPidMap()
    {
        lock (NetstatMapSync)
        {
            if (_netstatPortPidMap is not null && _netstatMapExpiresAt > DateTimeOffset.UtcNow)
            {
                return _netstatPortPidMap;
            }

            var tcpTableMap = TcpOwnerPidTable.TryGetListeningPortPidMap();
            var netstatOutput = RunNetstatOnce();
            var udpMap = NetstatPortMapParser.ParseUdp(netstatOutput);
            if (tcpTableMap is not null)
            {
                DiagnosticsCounters.RecordTcpTableInvocation();
                _netstatPortPidMap = NetstatPortMapParser.MergeMaps(tcpTableMap, udpMap);
            }
            else
            {
                _netstatPortPidMap = NetstatPortMapParser.Parse(netstatOutput);
            }

            _netstatMapExpiresAt = DateTimeOffset.UtcNow.Add(NetstatMapTtl);
            return _netstatPortPidMap;
        }
    }

    private static string RunNetstatOnce()
    {
        DiagnosticsCounters.RecordNetstatInvocation();

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
            return string.Empty;
        }

        if (!process.WaitForExit(800))
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
            return string.Empty;
        }

        var output = process.StandardOutput.ReadToEnd();
        DrainProcessStreams(process);
        return output;
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
}
