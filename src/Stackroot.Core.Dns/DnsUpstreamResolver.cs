using System.Net;
using System.Net.NetworkInformation;
using System.Net.Sockets;

namespace Stackroot.Core.Dns;

public static class DnsUpstreamResolver
{
    private static readonly IPAddress Fallback = IPAddress.Parse("1.1.1.1");

    // NetworkInterface.GetAllNetworkInterfaces() is one of the slowest Windows
    // networking calls (tens of ms, far worse under CPU pressure). Forwarding
    // happens for every non-local query, so the usable-upstream list is cached
    // and refreshed only when Windows reports a network change or the entry ages out.
    private static readonly TimeSpan CacheTtl = TimeSpan.FromMinutes(5);
    private static readonly object CacheGate = new();
    private static IReadOnlyList<IPAddress>? _cachedUsable;
    private static DateTimeOffset _cachedAt;

    static DnsUpstreamResolver()
    {
        try
        {
            NetworkChange.NetworkAddressChanged += (_, _) => Invalidate();
            NetworkChange.NetworkAvailabilityChanged += (_, _) => Invalidate();
        }
        catch
        {
            // If change notifications are unavailable the TTL alone keeps the list fresh.
        }
    }

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
        lock (CacheGate)
        {
            if (_cachedUsable is not null && DateTimeOffset.UtcNow - _cachedAt < CacheTtl)
            {
                return _cachedUsable;
            }
        }

        var servers = GetSystemDnsServers()
            .Where(static address => !IPAddress.IsLoopback(address))
            .Distinct()
            .ToList();

        IReadOnlyList<IPAddress> usable = servers.Count > 0 ? servers : [Fallback];
        lock (CacheGate)
        {
            _cachedUsable = usable;
            _cachedAt = DateTimeOffset.UtcNow;
        }

        return usable;
    }

    public static IPAddress GetPrimary() => GetUsable()[0];

    public static void Invalidate()
    {
        lock (CacheGate)
        {
            _cachedUsable = null;
        }
    }
}
