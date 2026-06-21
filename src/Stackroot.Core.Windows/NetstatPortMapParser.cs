namespace Stackroot.Core.Windows;

public static class NetstatPortMapParser
{
    public static Dictionary<int, List<int>> Parse(string output)
        => MergeMaps(ParseTcp(output), ParseUdp(output));

    public static Dictionary<int, List<int>> ParseTcp(string output)
    {
        var map = new Dictionary<int, List<int>>();
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

            if (!columns[3].Equals("LISTENING", StringComparison.OrdinalIgnoreCase))
            {
                continue;
            }

            if (!TryParseListenPort(columns[1], out var listenPort) || listenPort <= 0)
            {
                continue;
            }

            if (!int.TryParse(columns[4], out var pid) || pid <= 0)
            {
                continue;
            }

            AddPid(map, listenPort, pid);
        }

        return map;
    }

    public static Dictionary<int, List<int>> ParseUdp(string output)
    {
        var map = new Dictionary<int, List<int>>();
        foreach (var rawLine in output.Split('\n', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
        {
            if (!rawLine.StartsWith("UDP", StringComparison.OrdinalIgnoreCase))
            {
                continue;
            }

            var columns = rawLine.Split(' ', StringSplitOptions.RemoveEmptyEntries);
            if (columns.Length < 4)
            {
                continue;
            }

            if (!TryParseListenPort(columns[1], out var listenPort) || listenPort <= 0)
            {
                continue;
            }

            if (!int.TryParse(columns[^1], out var pid) || pid <= 0)
            {
                continue;
            }

            AddPid(map, listenPort, pid);
        }

        return map;
    }

    public static Dictionary<int, List<int>> MergeMaps(
        IReadOnlyDictionary<int, List<int>> primary,
        IReadOnlyDictionary<int, List<int>> secondary)
    {
        var merged = new Dictionary<int, List<int>>();
        foreach (var (port, pids) in primary)
        {
            foreach (var pid in pids)
            {
                AddPid(merged, port, pid);
            }
        }

        foreach (var (port, pids) in secondary)
        {
            foreach (var pid in pids)
            {
                AddPid(merged, port, pid);
            }
        }

        return merged;
    }

    private static void AddPid(Dictionary<int, List<int>> map, int port, int pid)
    {
        if (!map.TryGetValue(port, out var pids))
        {
            pids = [];
            map[port] = pids;
        }

        if (!pids.Contains(pid))
        {
            pids.Add(pid);
        }
    }

    private static bool TryParseListenPort(string localAddress, out int port)
    {
        port = 0;
        if (string.IsNullOrWhiteSpace(localAddress))
        {
            return false;
        }

        if (localAddress.StartsWith("[", StringComparison.Ordinal))
        {
            var endBracket = localAddress.IndexOf(']');
            if (endBracket < 0 || endBracket + 2 >= localAddress.Length)
            {
                return false;
            }

            return int.TryParse(localAddress[(endBracket + 2)..], out port) && port > 0;
        }

        var colon = localAddress.LastIndexOf(':');
        if (colon < 0 || colon + 1 >= localAddress.Length)
        {
            return false;
        }

        return int.TryParse(localAddress.AsSpan(colon + 1), out port) && port > 0;
    }
}
