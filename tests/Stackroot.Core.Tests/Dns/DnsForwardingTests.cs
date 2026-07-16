using System.Buffers.Binary;
using System.Net;
using System.Net.Sockets;
using Stackroot.Core.Abstractions;
using Stackroot.Core.Dns;
using Xunit;

namespace Stackroot.Core.Tests.Dns;

public sealed class DnsForwardingTests
{
    [Fact]
    public async Task Forwarder_races_upstreams_and_fastest_wins()
    {
        await using var slow = new FakeUpstream(address: [9, 9, 9, 9], delay: TimeSpan.FromMilliseconds(500));
        await using var fast = new FakeUpstream(address: [1, 2, 3, 4]);

        var query = BuildAQuery("racing.example.com", id: 0x1234);
        var response = await DnsForwarder.QueryAsync(
            query,
            [slow.EndPoint, fast.EndPoint],
            TimeSpan.FromSeconds(3));

        Assert.NotNull(response);
        Assert.Equal(0x1234, BinaryPrimitives.ReadUInt16BigEndian(response!));
        Assert.Equal([1, 2, 3, 4], response[^4..]);
    }

    [Fact]
    public async Task Forwarder_returns_null_when_no_upstream_answers()
    {
        using var silent = new UdpClient(new IPEndPoint(IPAddress.Loopback, 0));
        var endpoint = (IPEndPoint)silent.Client.LocalEndPoint!;

        var response = await DnsForwarder.QueryAsync(
            BuildAQuery("dead.example.com", id: 7),
            [endpoint],
            TimeSpan.FromMilliseconds(300));

        Assert.Null(response);
    }

    [Fact]
    public async Task Second_query_is_served_from_cache_without_touching_upstream()
    {
        await using var upstream = new FakeUpstream(address: [5, 6, 7, 8], ttl: 300);
        var options = LocalDnsServerOptions.Create([".com"], []);
        var cache = new DnsForwardCache();

        var (first, firstDisposition) = await TestDnsServer.TryResolveQueryAsync(
            BuildAQuery("cached.example.com", id: 100), options, cache, () => [upstream.EndPoint]);
        var (second, secondDisposition) = await TestDnsServer.TryResolveQueryAsync(
            BuildAQuery("cached.example.com", id: 200), options, cache, () => [upstream.EndPoint]);

        Assert.NotNull(first);
        Assert.NotNull(second);
        Assert.Equal("forward", firstDisposition);
        Assert.Equal("cache", secondDisposition);
        Assert.Equal(1, upstream.Hits);
        // The cached packet must carry the SECOND client's transaction ID.
        Assert.Equal(200, BinaryPrimitives.ReadUInt16BigEndian(second!));
        Assert.Equal([5, 6, 7, 8], second[^4..]);
    }

    [Fact]
    public async Task Local_name_always_wins_over_cached_upstream_answer()
    {
        await using var upstream = new FakeUpstream(address: [93, 184, 216, 34], ttl: 600);
        var cache = new DnsForwardCache();

        // 1) x.com is not local yet — forwarded and cached with a long TTL.
        var before = LocalDnsServerOptions.Create([".com"], []);
        var (forwarded, forwardedDisposition) = await TestDnsServer.TryResolveQueryAsync(
            BuildAQuery("x.com", id: 1), before, cache, () => [upstream.EndPoint]);
        Assert.Equal("forward", forwardedDisposition);
        Assert.Equal([93, 184, 216, 34], forwarded![^4..]);

        // 2) The user maps x.com to a local IP. Same cache instance still holds the
        //    upstream answer — the local mapping must win anyway, on the next query.
        var after = LocalDnsServerOptions.Create([".com"], ["x.com"], "192.168.0.10");
        var (local, localDisposition) = await TestDnsServer.TryResolveQueryAsync(
            BuildAQuery("x.com", id: 2), after, cache, () => [upstream.EndPoint]);

        Assert.Equal("local", localDisposition);
        Assert.Equal([192, 168, 0, 10], local![^4..]);
        Assert.Equal(1, upstream.Hits);
    }

    [Fact]
    public async Task Unreachable_upstreams_yield_servfail_not_silence()
    {
        using var silent = new UdpClient(new IPEndPoint(IPAddress.Loopback, 0));
        var endpoint = (IPEndPoint)silent.Client.LocalEndPoint!;
        var options = LocalDnsServerOptions.Create([".com"], []);

        var (response, disposition) = await TestDnsServer.TryResolveQueryAsync(
            BuildAQuery("unreachable.example.com", id: 42), options, null, () => [endpoint]);

        Assert.Equal("forward-timeout", disposition);
        Assert.NotNull(response);
        var flags = BinaryPrimitives.ReadUInt16BigEndian(response!.AsSpan(2));
        Assert.Equal(2, flags & 0x000F);
        Assert.True((flags & 0x8000) != 0, "QR bit should be set");
        Assert.True((flags & 0x0400) == 0, "SERVFAIL for a forwarded name must not claim authority");
    }

    [Fact]
    public async Task Flush_forces_next_query_back_to_upstream()
    {
        await using var upstream = new FakeUpstream(ttl: 300);
        var options = LocalDnsServerOptions.Create([".com"], []);
        var cache = new DnsForwardCache();
        Func<IReadOnlyList<IPEndPoint>> provider = () => [upstream.EndPoint];

        await TestDnsServer.TryResolveQueryAsync(BuildAQuery("flushed.example.com", id: 1), options, cache, provider);
        cache.Flush();
        var (_, disposition) = await TestDnsServer.TryResolveQueryAsync(
            BuildAQuery("flushed.example.com", id: 2), options, cache, provider);

        Assert.Equal("forward", disposition);
        Assert.Equal(2, upstream.Hits);
    }

    [Fact]
    public async Task Zero_ttl_answers_are_not_cached()
    {
        await using var upstream = new FakeUpstream(ttl: 0);
        var options = LocalDnsServerOptions.Create([".com"], []);
        var cache = new DnsForwardCache();
        Func<IReadOnlyList<IPEndPoint>> provider = () => [upstream.EndPoint];

        await TestDnsServer.TryResolveQueryAsync(BuildAQuery("nocache.example.com", id: 1), options, cache, provider);
        await TestDnsServer.TryResolveQueryAsync(BuildAQuery("nocache.example.com", id: 2), options, cache, provider);

        Assert.Equal(2, upstream.Hits);
        Assert.Equal(0, cache.Count);
    }

    [Fact]
    public void ComputeCacheTtl_rejects_uncacheable_responses()
    {
        var query = BuildAQuery("ttl.example.com", id: 1);

        var truncated = (byte[])query.Clone();
        BinaryPrimitives.WriteUInt16BigEndian(truncated.AsSpan(2), 0x8200);
        Assert.Null(DnsForwardCache.ComputeCacheTtl(truncated));

        var servfail = (byte[])query.Clone();
        BinaryPrimitives.WriteUInt16BigEndian(servfail.AsSpan(2), 0x8182);
        Assert.Null(DnsForwardCache.ComputeCacheTtl(servfail));

        var nxdomain = (byte[])query.Clone();
        BinaryPrimitives.WriteUInt16BigEndian(nxdomain.AsSpan(2), 0x8183);
        Assert.Equal(TimeSpan.FromSeconds(30), DnsForwardCache.ComputeCacheTtl(nxdomain));
    }

    [Fact]
    public async Task Server_answers_full_burst_of_concurrent_local_and_forwarded_queries()
    {
        const int totalQueries = 200;

        await using var upstream = new FakeUpstream(ttl: 60);
        await using var server = new TestDnsServer(FindFreeLoopbackPort());
        server.UpstreamProviderOverride = () => [upstream.EndPoint];
        server.Configure(LocalDnsServerOptions.Create(["."], ["site.test", "*.site.test"]));
        await server.StartAsync();
        var serverPort = server.BoundUdpPort!.Value;

        var tasks = Enumerable.Range(0, totalQueries).Select(async i =>
        {
            // Unique forwarded names defeat the cache on purpose; every third
            // query exercises the local path instead.
            var name = i % 3 == 0 ? "api.site.test" : $"host{i}.burst.example.com";
            using var client = new UdpClient();
            client.Connect(IPAddress.Loopback, serverPort);
            await client.SendAsync(BuildAQuery(name, id: (ushort)(1000 + i)));
            using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(5));
            var result = await client.ReceiveAsync(timeout.Token);
            Assert.Equal(1000 + i, BinaryPrimitives.ReadUInt16BigEndian(result.Buffer));
            var flags = BinaryPrimitives.ReadUInt16BigEndian(result.Buffer.AsSpan(2));
            Assert.Equal(0, flags & 0x000F);
            return result.Buffer.Length;
        }).ToArray();

        var lengths = await Task.WhenAll(tasks);

        Assert.All(lengths, static length => Assert.True(length > 12));
    }

    [Fact]
    public async Task Query_logger_writes_all_lines_asynchronously()
    {
        var root = Directory.CreateTempSubdirectory("stackroot-dns-log-test");
        try
        {
            var paths = new StackrootPaths { DataRoot = root.FullName, LogsRoot = root.FullName };
            using (var logger = new TestDnsQueryLogger(paths))
            {
                for (var i = 0; i < 100; i++)
                {
                    logger.Log("udp", "127.0.0.1:5000", $"host{i}.example.com", 1, "forward");
                }
            }

            var lines = await File.ReadAllLinesAsync(Path.Combine(root.FullName, "test-dns-queries.jsonl"));
            Assert.Equal(100, lines.Length);
            Assert.All(lines, static line => Assert.Contains("\"disposition\":\"forward\"", line));
        }
        finally
        {
            root.Delete(recursive: true);
        }
    }

    private static int FindFreeLoopbackPort()
    {
        using var listener = new TcpListener(IPAddress.Loopback, 0);
        listener.Start();
        var port = ((IPEndPoint)listener.LocalEndpoint).Port;
        listener.Stop();
        return port;
    }

    internal static byte[] BuildAQuery(string hostName, ushort id)
    {
        var packet = TestDnsServerResponseTests.BuildAQuery(hostName);
        BinaryPrimitives.WriteUInt16BigEndian(packet, id);
        // RD set, like real stub resolvers.
        BinaryPrimitives.WriteUInt16BigEndian(packet.AsSpan(2), 0x0100);
        return packet;
    }

    private sealed class FakeUpstream : IAsyncDisposable
    {
        private readonly UdpClient _socket = new(new IPEndPoint(IPAddress.Loopback, 0));
        private readonly CancellationTokenSource _cts = new();
        private readonly Task _loop;
        private int _hits;

        public FakeUpstream(uint ttl = 60, byte[]? address = null, TimeSpan? delay = null)
        {
            var answerAddress = address ?? [1, 2, 3, 4];
            _loop = Task.Run(async () =>
            {
                while (!_cts.IsCancellationRequested)
                {
                    try
                    {
                        var request = await _socket.ReceiveAsync(_cts.Token);
                        Interlocked.Increment(ref _hits);
                        if (delay is { } wait)
                        {
                            await Task.Delay(wait, _cts.Token);
                        }

                        var response = BuildUpstreamResponse(request.Buffer, ttl, answerAddress);
                        await _socket.SendAsync(response, request.RemoteEndPoint, _cts.Token);
                    }
                    catch (OperationCanceledException)
                    {
                        break;
                    }
                    catch (ObjectDisposedException)
                    {
                        break;
                    }
                    catch (SocketException)
                    {
                    }
                }
            });
        }

        public int Hits => Volatile.Read(ref _hits);

        public IPEndPoint EndPoint => (IPEndPoint)_socket.Client.LocalEndPoint!;

        public async ValueTask DisposeAsync()
        {
            await _cts.CancelAsync();
            _socket.Dispose();
            try
            {
                await _loop;
            }
            catch
            {
            }

            _cts.Dispose();
        }

        private static byte[] BuildUpstreamResponse(byte[] query, uint ttl, byte[] address)
        {
            var questionLength = query.Length - 12;
            var response = new byte[12 + questionLength + 16];
            Array.Copy(query, response, 12 + questionLength);
            BinaryPrimitives.WriteUInt16BigEndian(response.AsSpan(2), 0x8180);
            BinaryPrimitives.WriteUInt16BigEndian(response.AsSpan(4), 1);
            BinaryPrimitives.WriteUInt16BigEndian(response.AsSpan(6), 1);
            var offset = 12 + questionLength;
            BinaryPrimitives.WriteUInt16BigEndian(response.AsSpan(offset), 0xC00C);
            BinaryPrimitives.WriteUInt16BigEndian(response.AsSpan(offset + 2), 1);
            BinaryPrimitives.WriteUInt16BigEndian(response.AsSpan(offset + 4), 1);
            BinaryPrimitives.WriteUInt32BigEndian(response.AsSpan(offset + 6), ttl);
            BinaryPrimitives.WriteUInt16BigEndian(response.AsSpan(offset + 10), 4);
            address.CopyTo(response.AsSpan(offset + 12));
            return response;
        }
    }
}
