using System.Net;
using System.Net.NetworkInformation;
using System.Net.Sockets;

namespace Stackroot.Core.Dns;

public static class DnsUpstreamResolver
{
    private static readonly IPAddress Fallback = IPAddress.Parse("1.1.1.1");

    public static IReadOnlyList<IPAddress> GetSystemDnsServers()
    {
        try
        {
            return NetworkInterface.GetAllNetworkInterfaces()
                .Where(static nic => nic.OperationalStatus == OperationalStatus.Up)
                .SelectMany(static nic => nic.GetIPProperties().DnsAddresses)
                .Where(static address => address.AddressFamily == AddressFamily.InterNetwork)
                .Distinct()
                .ToList();
        }
        catch
        {
            return [];
        }
    }

    /// <summary>
    /// System DNS servers suitable for upstream forwarding (never loopback — avoids self-deadlock on 127.0.0.1:53).
    /// </summary>
    public static IReadOnlyList<IPAddress> GetUsable()
    {
        var servers = GetSystemDnsServers()
            .Where(static address => !IPAddress.IsLoopback(address))
            .Distinct()
            .ToList();

        return servers.Count > 0 ? servers : [Fallback];
    }

    public static IPAddress GetPrimary() => GetUsable()[0];
}
