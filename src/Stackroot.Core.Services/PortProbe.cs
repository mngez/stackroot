using System.Net.Sockets;

namespace Stackroot.Core.Services;

public enum PortProbeResult
{
    Open,
    Closed,
    Inconclusive
}

public static class PortProbe
{
    public static Action<string, Exception>? FailureLogger { get; set; }

    public static async Task<PortProbeResult> ProbePortAsync(string host, int port, int timeoutMs = 800)
    {
        using var client = new TcpClient();
        using var cts = new CancellationTokenSource(timeoutMs);

        try
        {
            await client.ConnectAsync(host, port, cts.Token).ConfigureAwait(false);
            return PortProbeResult.Open;
        }
        catch (OperationCanceledException)
        {
            return PortProbeResult.Inconclusive;
        }
        catch (SocketException ex) when (ex.SocketErrorCode == SocketError.ConnectionRefused)
        {
            return PortProbeResult.Closed;
        }
        catch (SocketException ex) when (ex.SocketErrorCode == SocketError.ConnectionReset)
        {
            return PortProbeResult.Closed;
        }
        catch (Exception ex)
        {
            FailureLogger?.Invoke("PortProbe", ex);
            return PortProbeResult.Inconclusive;
        }
    }

    public static async Task<bool> IsPortOpenAsync(string host, int port, int timeoutMs = 800)
    {
        return await ProbePortAsync(host, port, timeoutMs).ConfigureAwait(false) == PortProbeResult.Open;
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
