using System.Net.Sockets;

namespace Stackroot.Core.Services;

public static class PortProbe
{
    public static async Task<bool> IsPortOpenAsync(string host, int port, int timeoutMs = 800)
    {
        using var client = new TcpClient();
        using var cts = new CancellationTokenSource(timeoutMs);

        try
        {
            await client.ConnectAsync(host, port, cts.Token);
            return true;
        }
        catch
        {
            return false;
        }
    }

    public static Task SleepAsync(int milliseconds, CancellationToken cancellationToken = default)
    {
        return Task.Delay(milliseconds, cancellationToken);
    }

    public static async Task<bool> WaitForPortAsync(
        string host,
        int port,
        int attempts = 20,
        int delayMs = 500,
        CancellationToken cancellationToken = default)
    {
        for (var i = 0; i < attempts; i++)
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (await IsPortOpenAsync(host, port))
            {
                return true;
            }

            if (i < attempts - 1)
            {
                await SleepAsync(delayMs, cancellationToken);
            }
        }

        return false;
    }

    public static async Task<bool> WaitForPortClosedAsync(
        string host,
        int port,
        int attempts = 15,
        int delayMs = 200,
        CancellationToken cancellationToken = default)
    {
        for (var i = 0; i < attempts; i++)
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (!await IsPortOpenAsync(host, port))
            {
                return true;
            }

            if (i < attempts - 1)
            {
                await SleepAsync(delayMs, cancellationToken);
            }
        }

        return false;
    }
}
