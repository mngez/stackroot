using System.Buffers.Binary;
using System.Net;
using System.Net.Sockets;
using System.Text;

namespace Stackroot.Core.Windows.Dns;

/// <summary>
/// Minimal UDP DNS responder for <c>.test</c> names on 127.0.0.1:53.
/// </summary>
public sealed class TestDnsServer : IAsyncDisposable
{
    public const int ListenPort = 53;
    public const string DevSuffix = ".test";

    private UdpClient? _client;
    private CancellationTokenSource? _cts;
    private Task? _receiveTask;

    public bool IsRunning { get; private set; }

    public string? LastError { get; private set; }

    public Task StartAsync(CancellationToken cancellationToken = default)
    {
        if (IsRunning)
        {
            return Task.CompletedTask;
        }

        try
        {
            _client = new UdpClient(AddressFamily.InterNetwork);
            _client.Client.SetSocketOption(SocketOptionLevel.Socket, SocketOptionName.ReuseAddress, true);
            _client.Client.Bind(new IPEndPoint(IPAddress.Loopback, ListenPort));
            _cts = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
            _receiveTask = ReceiveLoopAsync(_cts.Token);
            IsRunning = true;
            LastError = null;
            return Task.CompletedTask;
        }
        catch (Exception ex)
        {
            LastError = ex.Message;
            IsRunning = false;
            _client?.Dispose();
            _client = null;
            return Task.FromException(ex);
        }
    }

    public async Task StopAsync()
    {
        if (!IsRunning)
        {
            return;
        }

        IsRunning = false;
        if (_cts is not null)
        {
            await _cts.CancelAsync().ConfigureAwait(false);
        }

        if (_receiveTask is not null)
        {
            try
            {
                await _receiveTask.ConfigureAwait(false);
            }
            catch (OperationCanceledException)
            {
            }
        }

        _cts?.Dispose();
        _cts = null;
        _receiveTask = null;
        _client?.Dispose();
        _client = null;
    }

    public async ValueTask DisposeAsync() => await StopAsync().ConfigureAwait(false);

    private async Task ReceiveLoopAsync(CancellationToken cancellationToken)
    {
        while (!cancellationToken.IsCancellationRequested)
        {
            UdpReceiveResult result;
            try
            {
                result = await _client!.ReceiveAsync(cancellationToken).ConfigureAwait(false);
            }
            catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
            {
                break;
            }
            catch (ObjectDisposedException)
            {
                break;
            }

            var response = TryBuildResponse(result.Buffer);
            if (response is null)
            {
                continue;
            }

            try
            {
                await _client.SendAsync(response, result.RemoteEndPoint, cancellationToken).ConfigureAwait(false);
            }
            catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
            {
                break;
            }
            catch (ObjectDisposedException)
            {
                break;
            }
        }
    }

    internal static byte[]? TryBuildResponse(ReadOnlySpan<byte> query)
    {
        if (query.Length < 12)
        {
            return null;
        }

        var qdCount = BinaryPrimitives.ReadUInt16BigEndian(query.Slice(4));
        if (qdCount != 1)
        {
            return null;
        }

        var offset = 12;
        var qname = ReadDomainName(query, ref offset);
        if (offset + 4 > query.Length || string.IsNullOrWhiteSpace(qname))
        {
            return null;
        }

        var qtype = BinaryPrimitives.ReadUInt16BigEndian(query.Slice(offset));
        offset += 2;
        _ = BinaryPrimitives.ReadUInt16BigEndian(query.Slice(offset));
        offset += 2;

        if (!IsDevDomain(qname))
        {
            return null;
        }

        var questionLength = offset - 12;
        byte[] answer = qtype switch
        {
            1 => BuildAddressAnswer(query, 12, [127, 0, 0, 1]),
            28 => BuildAddressAnswer(query, 12, [0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 1]),
            _ => []
        };

        var responseLength = 12 + questionLength + answer.Length;
        var response = new byte[responseLength];
        query[..12].CopyTo(response);

        var flags = BinaryPrimitives.ReadUInt16BigEndian(query.Slice(2));
        var recursionDesired = (flags & 0x0100) != 0;
        var responseFlags = (ushort)(0x8000 | (recursionDesired ? 0x0100 : 0) | 0x0080);
        BinaryPrimitives.WriteUInt16BigEndian(response.AsSpan(2), responseFlags);
        BinaryPrimitives.WriteUInt16BigEndian(response.AsSpan(6), answer.Length == 0 ? (ushort)0 : (ushort)1);

        query.Slice(12, questionLength).CopyTo(response.AsSpan(12));
        if (answer.Length > 0)
        {
            answer.CopyTo(response.AsSpan(12 + questionLength));
        }

        return response;
    }

    private static bool IsDevDomain(string qname)
    {
        var normalized = qname.Trim().TrimEnd('.').ToLowerInvariant();
        return normalized.EndsWith(DevSuffix, StringComparison.Ordinal);
    }

    private static byte[] BuildAddressAnswer(ReadOnlySpan<byte> query, int nameOffset, byte[] addressBytes)
    {
        _ = query;
        var answer = new byte[12 + addressBytes.Length];
        var pointer = (ushort)(0xC000 | nameOffset);
        BinaryPrimitives.WriteUInt16BigEndian(answer, pointer);
        BinaryPrimitives.WriteUInt16BigEndian(answer.AsSpan(2), addressBytes.Length == 4 ? (ushort)1 : (ushort)28);
        BinaryPrimitives.WriteUInt16BigEndian(answer.AsSpan(4), 1);
        BinaryPrimitives.WriteUInt32BigEndian(answer.AsSpan(6), 30);
        BinaryPrimitives.WriteUInt16BigEndian(answer.AsSpan(10), (ushort)addressBytes.Length);
        addressBytes.CopyTo(answer.AsSpan(12));
        return answer;
    }

    private static string ReadDomainName(ReadOnlySpan<byte> packet, ref int offset)
    {
        var labels = new List<string>();
        var jumps = 0;

        while (offset < packet.Length && jumps < 8)
        {
            var length = packet[offset++];
            if (length == 0)
            {
                break;
            }

            if ((length & 0xC0) == 0xC0)
            {
                if (offset >= packet.Length)
                {
                    break;
                }

                var pointer = ((length & 0x3F) << 8) | packet[offset++];
                offset = pointer;
                jumps++;
                continue;
            }

            if (offset + length > packet.Length)
            {
                break;
            }

            labels.Add(Encoding.ASCII.GetString(packet.Slice(offset, length)));
            offset += length;
        }

        return string.Join('.', labels);
    }
}
