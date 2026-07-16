using System.Runtime.InteropServices;

namespace Stackroot.Core.Dns;

/// <summary>
/// Flushes the Windows stub-resolver cache (same effect as `ipconfig /flushdns`).
/// Clearing only the helper's forward cache is not enough — Windows caches answers
/// per-machine and would keep serving the old records until their TTL expires.
/// </summary>
public static class WindowsDnsResolverCache
{
    [DllImport("dnsapi.dll", EntryPoint = "DnsFlushResolverCache", SetLastError = false)]
    private static extern uint DnsFlushResolverCache();

    public static bool TryFlush()
    {
        if (!OperatingSystem.IsWindows())
        {
            return false;
        }

        try
        {
            return DnsFlushResolverCache() != 0;
        }
        catch
        {
            return false;
        }
    }
}
