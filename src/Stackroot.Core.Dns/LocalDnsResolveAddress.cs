using System.Net;
using System.Net.Sockets;

namespace Stackroot.Core.Dns;

public static class LocalDnsResolveAddress
{
    public const string Default = "127.0.0.1";

    public static string Normalize(string? value)
    {
        if (TryParse(value, out var normalized))
        {
            return normalized;
        }

        return Default;
    }

    public static string? Validate(string? value)
    {
        return TryParse(value, out _) ? null : "Enter a valid IPv4 or IPv6 address (default 127.0.0.1).";
    }

    public static bool TryParse(string? value, out string normalized)
    {
        normalized = Default;
        if (string.IsNullOrWhiteSpace(value))
        {
            normalized = Default;
            return true;
        }

        if (!IPAddress.TryParse(value.Trim(), out var address))
        {
            return false;
        }

        if (address.AddressFamily is AddressFamily.InterNetwork or AddressFamily.InterNetworkV6)
        {
            normalized = address.ToString();
            return true;
        }

        return false;
    }

    public static byte[] GetAddressBytes(string normalized)
    {
        var address = IPAddress.Parse(Normalize(normalized));
        return address.GetAddressBytes();
    }

    public static bool IsIpv4(string normalized) =>
        IPAddress.TryParse(normalized, out var address) && address.AddressFamily == AddressFamily.InterNetwork;
}
