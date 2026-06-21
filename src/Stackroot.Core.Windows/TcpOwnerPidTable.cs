using System.Runtime.InteropServices;

namespace Stackroot.Core.Windows;

public static class TcpPortEndian
{
    public static int NetworkOrderToPort(uint networkPort)
        => (int)(((networkPort >> 8) & 0xFF) | ((networkPort & 0xFF) << 8));
}

internal static class TcpOwnerPidTable
{
    private const int AfInet = 2;
    private const int AfInet6 = 23;
    private const int TcpTableOwnerPidListener = 3;
    private const uint MibTcpStateListen = 2;
    private const uint ErrorInsufficientBuffer = 122;
    private const int Ipv4RowSize = 24;
    private const int Ipv6RowSize = 56;

    public static Dictionary<int, List<int>>? TryGetListeningPortPidMap()
    {
        try
        {
            var map = new Dictionary<int, List<int>>();
            if (!TryAddTable(map, AfInet, Ipv4RowSize, isIpv6: false))
            {
                return null;
            }

            TryAddTable(map, AfInet6, Ipv6RowSize, isIpv6: true);
            return map;
        }
        catch
        {
            return null;
        }
    }

    private static bool TryAddTable(Dictionary<int, List<int>> map, int addressFamily, int rowSize, bool isIpv6)
    {
        var bufferSize = 0;
        var result = GetExtendedTcpTable(IntPtr.Zero, ref bufferSize, true, addressFamily, TcpTableOwnerPidListener, 0);
        if (result != 0 && result != ErrorInsufficientBuffer)
        {
            return isIpv6;
        }

        if (bufferSize <= 0)
        {
            return isIpv6;
        }

        var buffer = Marshal.AllocHGlobal(bufferSize);
        try
        {
            result = GetExtendedTcpTable(buffer, ref bufferSize, true, addressFamily, TcpTableOwnerPidListener, 0);
            if (result != 0)
            {
                return isIpv6;
            }

            var rowCount = Marshal.ReadInt32(buffer);
            var offset = sizeof(uint);
            for (var i = 0; i < rowCount; i++)
            {
                var rowPtr = buffer + offset;
                offset += rowSize;

                uint state;
                uint localPort;
                int owningPid;
                if (isIpv6)
                {
                    localPort = (uint)Marshal.ReadInt32(rowPtr + 20);
                    state = (uint)Marshal.ReadInt32(rowPtr + 48);
                    owningPid = Marshal.ReadInt32(rowPtr + 52);
                }
                else
                {
                    state = (uint)Marshal.ReadInt32(rowPtr);
                    localPort = (uint)Marshal.ReadInt32(rowPtr + 8);
                    owningPid = Marshal.ReadInt32(rowPtr + 20);
                }

                if (state != MibTcpStateListen || owningPid <= 0)
                {
                    continue;
                }

                AddPid(map, TcpPortEndian.NetworkOrderToPort(localPort), owningPid);
            }

            return true;
        }
        finally
        {
            Marshal.FreeHGlobal(buffer);
        }
    }

    private static void AddPid(Dictionary<int, List<int>> map, int port, int pid)
    {
        if (port <= 0 || pid <= 0)
        {
            return;
        }

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

    [DllImport("iphlpapi.dll", SetLastError = true)]
    private static extern uint GetExtendedTcpTable(
        IntPtr tcpTable,
        ref int size,
        bool sort,
        int ipVersion,
        int tableClass,
        uint reserved);
}
