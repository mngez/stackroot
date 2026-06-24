using System.Buffers.Binary;
using System.Net;
using System.Net.Sockets;
using System.Text;

namespace Stackroot.Core.Dns;

/// <summary>
/// Minimal UDP/TCP DNS responder on 127.0.0.1:53 for configured dev suffixes.
/// Safe suffixes (e.g. .test) resolve to loopback; public suffixes only match local site names and forward the rest.
/// </summary>
public sealed class TestDnsServer : IAsyncDisposable
{
    public const int ListenPort = 53;
    public const string DevSuffix = ".test";

    private readonly int _listenPort;

    private readonly object _optionsGate = new();
    private LocalDnsServerOptions _options = LocalDnsServerOptions.Default;

    private static readonly TimeSpan ForwardTimeout = TimeSpan.FromSeconds(2);

    private const ushort FlagQr = 0x8000;
    private const ushort FlagAa = 0x0400;
    private const ushort FlagRd = 0x0100;
    private const ushort FlagRa = 0x0080;
    private const ushort RcodeNxDomain = 0x0003;

    private UdpClient? _client;
    private TcpListener? _tcpListener;
    private CancellationTokenSource? _cts;
    private Task? _receiveTask;
    private Task? _tcpAcceptTask;
    private SemaphoreSlim? _sendLock;
    private volatile ITestDnsQueryLogger? _queryLogger;

    public bool IsRunning { get; private set; }

    public string? LastError { get; private set; }

    public TestDnsServer(int listenPort = ListenPort)
    {
        _listenPort = listenPort;
    }

    public void Configure(LocalDnsServerOptions options)
    {
        ArgumentNullException.ThrowIfNull(options);
        lock (_optionsGate)
        {
            _options = options;
        }
    }

    public void SetQueryLogger(ITestDnsQueryLogger? logger) => _queryLogger = logger;

    public Task StartAsync(CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();

        if (IsRunning && _receiveTask is { IsCompleted: false })
        {
            return Task.CompletedTask;
        }

        try
        {
            DisposeSocket();
            _client = new UdpClient(new IPEndPoint(IPAddress.Loopback, _listenPort));
            _tcpListener = new TcpListener(IPAddress.Loopback, _listenPort);
            _tcpListener.Start();
            _sendLock = new SemaphoreSlim(1, 1);
            _cts = new CancellationTokenSource();
            _receiveTask = ReceiveLoopAsync(_cts.Token);
            _tcpAcceptTask = AcceptTcpLoopAsync(_cts.Token);
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
        if (!IsRunning && _client is null)
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

        if (_tcpAcceptTask is not null)
        {
            try
            {
                await _tcpAcceptTask.ConfigureAwait(false);
            }
            catch (OperationCanceledException)
            {
            }
        }

        DisposeSocket();
    }

    private void DisposeSocket()
    {
        _cts?.Dispose();
        _cts = null;
        _receiveTask = null;
        _tcpAcceptTask = null;
        _client?.Dispose();
        _client = null;
        if (_tcpListener is not null)
        {
            try
            {
                _tcpListener.Stop();
            }
            catch
            {
            }

            _tcpListener = null;
        }

        _sendLock?.Dispose();
        _sendLock = null;
        IsRunning = false;
    }

    public async ValueTask DisposeAsync() => await StopAsync().ConfigureAwait(false);

    private async Task ReceiveLoopAsync(CancellationToken cancellationToken)
    {
        try
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
                catch (SocketException ex)
                {
                    LastError = ex.Message;
                    await Task.Delay(50, cancellationToken).ConfigureAwait(false);
                    continue;
                }

                _ = ProcessQueryAsync(result, cancellationToken);
            }
        }
        catch (Exception ex)
        {
            LastError = ex.Message;
        }
        finally
        {
            IsRunning = false;
        }
    }

    private async Task AcceptTcpLoopAsync(CancellationToken cancellationToken)
    {
        try
        {
            while (!cancellationToken.IsCancellationRequested)
            {
                TcpClient client;
                try
                {
                    client = await _tcpListener!.AcceptTcpClientAsync(cancellationToken).ConfigureAwait(false);
                }
                catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
                {
                    break;
                }
                catch (ObjectDisposedException)
                {
                    break;
                }

                _ = ProcessTcpClientAsync(client, cancellationToken);
            }
        }
        catch (Exception ex)
        {
            LastError = ex.Message;
        }
    }

    private async Task ProcessTcpClientAsync(TcpClient client, CancellationToken cancellationToken)
    {
        using (client)
        {
            try
            {
                var stream = client.GetStream();
                var lengthBuffer = new byte[2];
                if (!await ReadExactAsync(stream, lengthBuffer, cancellationToken).ConfigureAwait(false))
                {
                    return;
                }

                var messageLength = BinaryPrimitives.ReadUInt16BigEndian(lengthBuffer);
                if (messageLength is < 12 or > 4096)
                {
                    return;
                }

                var query = new byte[messageLength];
                if (!await ReadExactAsync(stream, query, cancellationToken).ConfigureAwait(false))
                {
                    return;
                }

                var options = GetOptionsSnapshot();
                var (response, disposition) = await TryResolveQueryAsync(query, options, cancellationToken).ConfigureAwait(false);
                LogQuery("tcp", client.Client.RemoteEndPoint?.ToString(), query, disposition);
                if (response is null)
                {
                    return;
                }

                var responseHeader = new byte[2];
                BinaryPrimitives.WriteUInt16BigEndian(responseHeader, (ushort)response.Length);
                await stream.WriteAsync(responseHeader, cancellationToken).ConfigureAwait(false);
                await stream.WriteAsync(response, cancellationToken).ConfigureAwait(false);
            }
            catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
            {
            }
            catch (IOException)
            {
            }
            catch (SocketException)
            {
            }
        }
    }

    private static async Task<bool> ReadExactAsync(NetworkStream stream, byte[] buffer, CancellationToken cancellationToken)
    {
        var offset = 0;
        while (offset < buffer.Length)
        {
            var read = await stream.ReadAsync(buffer.AsMemory(offset), cancellationToken).ConfigureAwait(false);
            if (read == 0)
            {
                return false;
            }

            offset += read;
        }

        return true;
    }

    private async Task ProcessQueryAsync(UdpReceiveResult result, CancellationToken cancellationToken)
    {
        byte[]? response;
        string disposition;
        try
        {
            var options = GetOptionsSnapshot();
            (response, disposition) = await TryResolveQueryAsync(result.Buffer, options, cancellationToken).ConfigureAwait(false);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            return;
        }
        catch
        {
            return;
        }

        LogQuery("udp", result.RemoteEndPoint.ToString(), result.Buffer, disposition);

        if (response is null || _sendLock is null || _client is null)
        {
            return;
        }

        try
        {
            await _sendLock.WaitAsync(cancellationToken).ConfigureAwait(false);
            try
            {
                await _client.SendAsync(response, result.RemoteEndPoint, cancellationToken).ConfigureAwait(false);
            }
            finally
            {
                _sendLock.Release();
            }
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
        }
        catch (ObjectDisposedException)
        {
        }
    }

    private void LogQuery(string transport, string? remoteEndPoint, ReadOnlyMemory<byte> query, string disposition)
    {
        var logger = _queryLogger;
        if (logger is null)
        {
            return;
        }

        if (!TryParseQuestion(query.Span, out var qname, out _, out _, out var qtype))
        {
            logger.Log(transport, remoteEndPoint, string.Empty, 0, disposition);
            return;
        }

        logger.Log(transport, remoteEndPoint, qname, qtype, disposition);
    }

    private LocalDnsServerOptions GetOptionsSnapshot()
    {
        lock (_optionsGate)
        {
            return _options;
        }
    }

    internal static async Task<(byte[]? Response, string Disposition)> TryResolveQueryAsync(
        ReadOnlyMemory<byte> query,
        LocalDnsServerOptions options,
        CancellationToken cancellationToken = default)
    {
        if (!TryParseQuestion(query.Span, out var qname, out var questionOffset, out var questionLength, out var qtype))
        {
            return (null, "unparseable");
        }

        if (IsLoopbackPtrQuery(qname, qtype))
        {
            return (BuildLoopbackPtrResponse(query.Span, questionOffset, questionLength), "ptr");
        }

        if (!LocalDnsNameMatcher.ShouldAnswerLocally(qname, options.Suffixes, options.LocalNames))
        {
            if (ShouldForward(qname, options))
            {
                var forwarded = await ForwardQueryAsync(query, cancellationToken).ConfigureAwait(false);
                return (forwarded, forwarded is null ? "forward-timeout" : "forward");
            }

            return (BuildNxDomainResponse(query.Span, questionOffset, questionLength), "nxdomain");
        }

        return (BuildLocalAddressResponse(query.Span, questionOffset, questionLength, qtype, options), "local");
    }

    internal static byte[]? TryBuildResponse(ReadOnlySpan<byte> query, LocalDnsServerOptions options) =>
        TryResolveQueryAsync(query.ToArray(), options).GetAwaiter().GetResult().Response;

    private static bool ShouldForward(string qname, LocalDnsServerOptions options)
    {
        if (LocalDnsSuffix.ContainsCatchAll(options.Suffixes))
        {
            var normalized = qname.Trim().TrimEnd('.').ToLowerInvariant();
            return !LocalDnsNameMatcher.MatchesLocalName(normalized, options.LocalNames);
        }

        var normalizedQuery = qname.Trim().TrimEnd('.').ToLowerInvariant();
        foreach (var suffix in options.Suffixes)
        {
            if (!LocalDnsSuffix.EndsWithSuffix(normalizedQuery, suffix))
            {
                continue;
            }

            return !LocalDnsSuffix.IsSafeSuffix(suffix);
        }

        return false;
    }

    private static async Task<byte[]?> ForwardQueryAsync(ReadOnlyMemory<byte> query, CancellationToken cancellationToken)
    {
        foreach (var upstream in DnsUpstreamResolver.GetUsable())
        {
            using var client = new UdpClient(AddressFamily.InterNetwork);
            using var timeoutCts = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
            timeoutCts.CancelAfter(ForwardTimeout);

            try
            {
                await client.SendAsync(query, new IPEndPoint(upstream, 53), timeoutCts.Token).ConfigureAwait(false);
                var result = await client.ReceiveAsync(timeoutCts.Token).ConfigureAwait(false);
                if (result.Buffer.Length > 0)
                {
                    return result.Buffer;
                }
            }
            catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
            {
            }
            catch
            {
            }
        }

        return null;
    }

    private static bool TryParseQuestion(
        ReadOnlySpan<byte> query,
        out string qname,
        out int questionOffset,
        out int questionLength,
        out ushort qtype)
    {
        qname = string.Empty;
        questionOffset = 0;
        questionLength = 0;
        qtype = 0;

        if (query.Length < 12)
        {
            return false;
        }

        var qdCount = BinaryPrimitives.ReadUInt16BigEndian(query.Slice(4));
        if (qdCount != 1)
        {
            return false;
        }

        questionOffset = 12;
        var offset = 12;
        qname = ReadDomainName(query, ref offset);
        if (offset + 4 > query.Length || string.IsNullOrWhiteSpace(qname))
        {
            return false;
        }

        qtype = BinaryPrimitives.ReadUInt16BigEndian(query.Slice(offset));
        offset += 4;
        questionLength = offset - questionOffset;
        return true;
    }

    private static bool IsLoopbackPtrQuery(string qname, ushort qtype)
    {
        if (qtype != 12)
        {
            return false;
        }

        var normalized = qname.Trim().TrimEnd('.').ToLowerInvariant();
        return normalized is "1.0.0.127.in-addr.arpa"
            or "1.0.0.0.0.0.0.0.0.0.0.0.0.0.0.0.0.0.0.0.0.0.0.0.0.1.ip6.arpa";
    }

    private static ushort BuildAuthoritativeFlags(ReadOnlySpan<byte> query, ushort rcode = 0)
    {
        var flags = BinaryPrimitives.ReadUInt16BigEndian(query.Slice(2));
        var recursionDesired = (flags & FlagRd) != 0;
        return (ushort)(FlagQr | FlagAa | (recursionDesired ? FlagRd : 0) | FlagRa | rcode);
    }

    private static byte[] BuildResponsePacket(ReadOnlySpan<byte> query, int questionOffset, int questionLength, byte[] answer, ushort rcode = 0)
    {
        var responseLength = 12 + questionLength + answer.Length;
        var response = new byte[responseLength];
        query[..12].CopyTo(response);
        BinaryPrimitives.WriteUInt16BigEndian(response.AsSpan(2), BuildAuthoritativeFlags(query, rcode));
        BinaryPrimitives.WriteUInt16BigEndian(response.AsSpan(6), answer.Length == 0 ? (ushort)0 : (ushort)1);
        query.Slice(questionOffset, questionLength).CopyTo(response.AsSpan(12));
        if (answer.Length > 0)
        {
            answer.CopyTo(response.AsSpan(12 + questionLength));
        }

        return response;
    }

    private static byte[] BuildLocalAddressResponse(
        ReadOnlySpan<byte> query,
        int questionOffset,
        int questionLength,
        ushort qtype,
        LocalDnsServerOptions options)
    {
        var resolveAddress = LocalDnsResolveAddress.Normalize(options.ResolveAddress);
        byte[] answer = qtype switch
        {
            1 when LocalDnsResolveAddress.IsIpv4(resolveAddress) =>
                BuildAddressAnswer(query, questionOffset, LocalDnsResolveAddress.GetAddressBytes(resolveAddress)),
            28 when IPAddress.TryParse(resolveAddress, out var parsed) && parsed.AddressFamily == System.Net.Sockets.AddressFamily.InterNetworkV6 =>
                BuildAddressAnswer(query, questionOffset, parsed.GetAddressBytes()),
            28 when resolveAddress == LocalDnsResolveAddress.Default =>
                BuildAddressAnswer(query, questionOffset, [0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 1]),
            _ => []
        };

        return BuildResponsePacket(query, questionOffset, questionLength, answer);
    }

    private static byte[] BuildLoopbackPtrResponse(ReadOnlySpan<byte> query, int questionOffset, int questionLength)
    {
        var answer = BuildPointerAnswer(query, questionOffset, "localhost");
        return BuildResponsePacket(query, questionOffset, questionLength, answer);
    }

    private static byte[] BuildNxDomainResponse(ReadOnlySpan<byte> query, int questionOffset, int questionLength) =>
        BuildResponsePacket(query, questionOffset, questionLength, [], RcodeNxDomain);

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

    private static byte[] BuildPointerAnswer(ReadOnlySpan<byte> query, int nameOffset, string targetHost)
    {
        _ = query;
        var targetBytes = EncodeDomainName(targetHost);
        var answer = new byte[12 + targetBytes.Length];
        var pointer = (ushort)(0xC000 | nameOffset);
        BinaryPrimitives.WriteUInt16BigEndian(answer, pointer);
        BinaryPrimitives.WriteUInt16BigEndian(answer.AsSpan(2), 12);
        BinaryPrimitives.WriteUInt16BigEndian(answer.AsSpan(4), 1);
        BinaryPrimitives.WriteUInt32BigEndian(answer.AsSpan(6), 30);
        BinaryPrimitives.WriteUInt16BigEndian(answer.AsSpan(10), (ushort)targetBytes.Length);
        targetBytes.CopyTo(answer.AsSpan(12));
        return answer;
    }

    private static byte[] EncodeDomainName(string hostName)
    {
        var labels = hostName.Split('.', StringSplitOptions.RemoveEmptyEntries);
        var length = labels.Sum(static label => label.Length + 1) + 1;
        var encoded = new byte[length];
        var offset = 0;
        foreach (var label in labels)
        {
            encoded[offset++] = (byte)label.Length;
            Encoding.ASCII.GetBytes(label).CopyTo(encoded.AsSpan(offset));
            offset += label.Length;
        }

        encoded[offset] = 0;
        return encoded;
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
