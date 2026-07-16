using System.Buffers.Binary;
using System.Net;
using System.Net.Sockets;

namespace Stackroot.Core.Dns;

/// <summary>
/// Sends one query to all upstream servers in parallel over a single UDP socket;
/// the first valid response wins. Probing upstreams sequentially (2s each) is what
/// pushed real-TLD lookups past browser timeouts when one upstream was slow.
/// </summary>
public static class DnsForwarder
{
    private const ushort FlagQr = 0x8000;

    public static async Task<byte[]?> QueryAsync(
        ReadOnlyMemory<byte> query,
        IReadOnlyList<IPEndPoint> upstreams,
        TimeSpan timeout,
        CancellationToken cancellationToken = default)
    {
        if (query.Length < 12 || upstreams.Count == 0)
        {
            return null;
        }

        var queryId = BinaryPrimitives.ReadUInt16BigEndian(query.Span);

        using var client = new UdpClient(AddressFamily.InterNetwork);
        UdpSocketHardening.Apply(client.Client);
        using var timeoutCts = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        timeoutCts.CancelAfter(timeout);

        try
        {
            var sent = 0;
            foreach (var upstream in upstreams)
            {
                try
                {
                    await client.SendAsync(query, upstream, timeoutCts.Token).ConfigureAwait(false);
                    sent++;
                }
                catch (OperationCanceledException)
                {
                    throw;
                }
                catch
                {
                    // One unreachable upstream must not stop the race.
                }
            }

            if (sent == 0)
            {
                return null;
            }

            while (true)
            {
                UdpReceiveResult result;
                try
                {
                    result = await client.ReceiveAsync(timeoutCts.Token).ConfigureAwait(false);
                }
                catch (SocketException)
                {
                    // A dead upstream may still surface as a receive error; keep
                    // waiting for the remaining upstreams until the deadline.
                    continue;
                }

                var buffer = result.Buffer;
                if (buffer.Length < 12
                    || BinaryPrimitives.ReadUInt16BigEndian(buffer) != queryId
                    || (BinaryPrimitives.ReadUInt16BigEndian(buffer.AsSpan(2)) & FlagQr) == 0
                    || !IsKnownUpstream(result.RemoteEndPoint, upstreams))
                {
                    continue;
                }

                return buffer;
            }
        }
        catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
        {
            return null;
        }
    }

    private static bool IsKnownUpstream(IPEndPoint remote, IReadOnlyList<IPEndPoint> upstreams)
    {
        foreach (var upstream in upstreams)
        {
            if (upstream.Port == remote.Port && upstream.Address.Equals(remote.Address))
            {
                return true;
            }
        }

        return false;
    }
}
