using System.Text;
using Stackroot.Core.Abstractions;

namespace Stackroot.Engine.Runtime;

public static class RuntimeStateSnapshotFingerprint
{
    public static string Compute(RuntimeStateSnapshot snapshot)
    {
        var sb = new StringBuilder(512);
        foreach (var service in snapshot.Services.OrderBy(row => row.Id, StringComparer.OrdinalIgnoreCase))
        {
            sb.Append(service.Id)
                .Append(':')
                .Append((int)service.Status)
                .Append(':')
                .Append(service.PortOpen)
                .Append(':')
                .Append(service.Pid)
                .Append(':')
                .Append(service.Message ?? string.Empty)
                .Append('|');
        }

        foreach (var listener in snapshot.PhpListeners.OrderBy(row => row.VersionId, StringComparer.OrdinalIgnoreCase))
        {
            sb.Append(listener.VersionId)
                .Append(':')
                .Append(listener.IsRunning)
                .Append(':')
                .Append(listener.Pid)
                .Append('|');
        }

        foreach (var process in snapshot.Processes.OrderBy(row => row.Id, StringComparer.OrdinalIgnoreCase))
        {
            sb.Append(process.Id)
                .Append(':')
                .Append((int)process.Status)
                .Append(':')
                .Append(process.Pid)
                .Append('|');
        }

        if (snapshot.Mailpit is { } mailpit)
        {
            sb.Append("mailpit:")
                .Append(mailpit.Running)
                .Append(':')
                .Append(mailpit.Pid)
                .Append('|');
        }

        if (snapshot.TestDns is { } testDns)
        {
            sb.Append("testdns:")
                .Append(testDns.Running)
                .Append(':')
                .Append(testDns.NrptActive)
                .Append(':')
                .Append(testDns.Message ?? string.Empty)
                .Append('|');
        }

        return sb.ToString();
    }
}
