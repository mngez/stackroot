using System.Buffers.Binary;
using System.Net;
using System.Net.Sockets;
using System.Text;
using Stackroot.Core.Dns;
using Xunit;

namespace Stackroot.Core.Tests.Dns;

public sealed class LocalDnsSuffixTests
{
    [Theory]
    [InlineData(".test", ".test")]
    [InlineData("dev", ".dev")]
    [InlineData(".DEV", ".dev")]
    public void TryNormalize_accepts_valid_suffixes(string input, string expected)
    {
        Assert.Equal(expected, LocalDnsSuffix.TryNormalize(input));
    }

    [Theory]
    [InlineData("")]
    [InlineData(".")]
    [InlineData(".-bad")]
    [InlineData("a..b")]
    public void TryNormalize_rejects_invalid_suffixes(string input)
    {
        Assert.Null(LocalDnsSuffix.TryNormalize(input));
    }

    [Fact]
    public void TryNormalize_accepts_catch_all_when_dangerous_mode_enabled()
    {
        Assert.Equal(".", LocalDnsSuffix.TryNormalize(".", allowDangerous: true));
    }

    [Fact]
    public void ValidateText_blocks_catch_all_without_dangerous_mode()
    {
        Assert.Contains("allowDangerousSettings", LocalDnsSuffix.ValidateText(".")!, StringComparison.Ordinal);
        Assert.Contains("allowDangerousSettings", LocalDnsSuffix.ValidateText(".test\n.")!, StringComparison.Ordinal);
    }

    [Fact]
    public void ValidateText_allows_catch_all_with_dangerous_mode()
    {
        Assert.Null(LocalDnsSuffix.ValidateText(".", allowDangerous: true));
        Assert.Null(LocalDnsSuffix.ValidateText(".test\n.", allowDangerous: true));
    }

    [Fact]
    public void FormatText_preserves_catch_all_suffix()
    {
        Assert.Equal(".", LocalDnsSuffix.FormatText(["."]));
    }

    [Fact]
    public void IsSafeSuffix_recognizes_reserved_suffixes()
    {
        Assert.True(LocalDnsSuffix.IsSafeSuffix(".test"));
        Assert.False(LocalDnsSuffix.IsSafeSuffix(".dev"));
        Assert.False(LocalDnsSuffix.IsSafeSuffix(".com"));
    }

    [Fact]
    public void ValidateText_requires_at_least_one_valid_suffix()
    {
        Assert.Null(LocalDnsSuffix.ValidateText(".test\n.dev"));
        Assert.NotNull(LocalDnsSuffix.ValidateText(string.Empty));
        Assert.Contains("Invalid suffix", LocalDnsSuffix.ValidateText(".test\n.-bad")!, StringComparison.Ordinal);
    }
}

public sealed class LocalDnsNameMatcherTests
{
  private static readonly IReadOnlyList<string> Suffixes = [".test", ".dev"];

    [Fact]
    public void Safe_suffix_matches_any_name()
    {
        Assert.True(LocalDnsNameMatcher.ShouldAnswerLocally(
            "anything.test",
            Suffixes,
            []));
    }

    [Fact]
    public void Public_suffix_matches_only_local_names()
    {
        var localNames = new[] { "domin.dev", "*.domin.dev" };

        Assert.True(LocalDnsNameMatcher.ShouldAnswerLocally("domin.dev", Suffixes, localNames));
        Assert.True(LocalDnsNameMatcher.ShouldAnswerLocally("app.domin.dev", Suffixes, localNames));
        Assert.False(LocalDnsNameMatcher.ShouldAnswerLocally("google.dev", Suffixes, localNames));
    }

    [Fact]
    public void Catch_all_matches_only_configured_local_names()
    {
        var suffixes = new[] { "." };
        var localNames = new[] { "site.test", "*.site.test" };

        Assert.True(LocalDnsNameMatcher.ShouldAnswerLocally("site.test", suffixes, localNames));
        Assert.True(LocalDnsNameMatcher.ShouldAnswerLocally("api.site.test", suffixes, localNames));
        Assert.False(LocalDnsNameMatcher.ShouldAnswerLocally("google.com", suffixes, localNames));
    }

    [Fact]
    public void Wildcard_alias_matches_single_label_only()
    {
        var localNames = new[] { "*.domin.dev" };

        Assert.True(LocalDnsNameMatcher.MatchesLocalName("api.domin.dev", localNames));
        Assert.False(LocalDnsNameMatcher.MatchesLocalName("a.b.domin.dev", localNames));
        Assert.False(LocalDnsNameMatcher.MatchesLocalName("domin.dev", localNames));
    }
}

public sealed class TestDnsServerResponseTests
{
    [Fact]
    public void TryBuildResponse_answers_safe_suffix_with_loopback()
    {
        var query = BuildAQuery("app.example.test");
        var options = LocalDnsServerOptions.Create([".test"], []);

        var response = TestDnsServer.TryBuildResponse(query, options);

        Assert.NotNull(response);
        Assert.True(response!.Length > 12);
    }

    [Fact]
    public void TryBuildResponse_uses_configured_resolve_address()
    {
        var query = BuildAQuery("app.example.test");
        var options = LocalDnsServerOptions.Create([".test"], [], "192.168.0.10");

        var response = TestDnsServer.TryBuildResponse(query, options);

        Assert.NotNull(response);
        Assert.Equal(192, response![^4]);
        Assert.Equal(168, response[^3]);
        Assert.Equal(0, response[^2]);
        Assert.Equal(10, response[^1]);
    }

    [Fact]
    public void TryBuildResponse_ignores_unconfigured_suffix()
    {
        var query = BuildAQuery("app.example.local");
        var options = LocalDnsServerOptions.Create([".test"], []);

        var response = TestDnsServer.TryBuildResponse(query, options);

        Assert.NotNull(response);
        var flags = BinaryPrimitives.ReadUInt16BigEndian(response!.AsSpan(2));
        Assert.True((flags & 0x0400) != 0, "AA bit should be set");
        Assert.Equal(3, flags & 0x000F);
    }

    [Fact]
    public void TryBuildResponse_marks_local_answers_authoritative()
    {
        var query = BuildAQuery("app.example.test");
        var options = LocalDnsServerOptions.Create([".test"], []);

        var response = TestDnsServer.TryBuildResponse(query, options);

        Assert.NotNull(response);
        var flags = BinaryPrimitives.ReadUInt16BigEndian(response!.AsSpan(2));
        Assert.True((flags & 0x0400) != 0, "AA bit should be set");
        Assert.Equal(0, flags & 0x000F);
    }

    [Fact]
    public void TryBuildResponse_answers_loopback_ptr()
    {
        var query = BuildPtrQuery("1.0.0.127.in-addr.arpa");
        var options = LocalDnsServerOptions.Create([".test"], []);

        var response = TestDnsServer.TryBuildResponse(query, options);

        Assert.NotNull(response);
        var flags = BinaryPrimitives.ReadUInt16BigEndian(response!.AsSpan(2));
        Assert.True((flags & 0x0400) != 0, "AA bit should be set");
        Assert.Equal(1, BinaryPrimitives.ReadUInt16BigEndian(response.AsSpan(6)));
    }

    [Fact]
    public void TryBuildResponse_answers_wildcard_dev_alias_for_site()
    {
        var query = BuildAQuery("api.example.dev");
        var localNames = new[] { "example.dev", "*.example.dev" };
        var options = LocalDnsServerOptions.Create([".test", ".dev", ".com"], localNames);

        var response = TestDnsServer.TryBuildResponse(query, options);

        Assert.NotNull(response);
        Assert.True(response!.Length > 12);
    }

    [Fact]
    public async Task Server_restarts_cleanly_after_stop()
    {
        var port = FindFreeLoopbackPort();
        await using var server = new TestDnsServer(port);
        server.Configure(LocalDnsServerOptions.Create([".test"], []));

        await server.StartAsync();
        await server.StopAsync();
        await server.StartAsync();

        Assert.True(server.IsRunning);

        using var client = new UdpClient();
        client.Connect(IPAddress.Loopback, port);
        await client.SendAsync(BuildAQuery("app.example.test"));

        using var timeoutCts = new CancellationTokenSource(TimeSpan.FromSeconds(3));
        var result = await client.ReceiveAsync(timeoutCts.Token);

        Assert.True(result.Buffer.Length > 12);
    }

    private static int FindFreeLoopbackPort()
    {
        using var listener = new TcpListener(IPAddress.Loopback, 0);
        listener.Start();
        var port = ((IPEndPoint)listener.LocalEndpoint).Port;
        listener.Stop();
        return port;
    }

    internal static byte[] BuildAQuery(string hostName)
    {
        var labels = hostName.Split('.');
        var questionNameLength = labels.Sum(static label => label.Length + 1) + 1;
        var packet = new byte[12 + questionNameLength + 4];
        BinaryPrimitives.WriteUInt16BigEndian(packet.AsSpan(4), 1);

        var offset = 12;
        foreach (var label in labels)
        {
            packet[offset++] = (byte)label.Length;
            Encoding.ASCII.GetBytes(label).CopyTo(packet.AsSpan(offset));
            offset += label.Length;
        }

        packet[offset++] = 0;
        BinaryPrimitives.WriteUInt16BigEndian(packet.AsSpan(offset), 1);
        BinaryPrimitives.WriteUInt16BigEndian(packet.AsSpan(offset + 2), 1);
        return packet;
    }

    internal static byte[] BuildPtrQuery(string hostName)
    {
        var labels = hostName.Split('.');
        var questionNameLength = labels.Sum(static label => label.Length + 1) + 1;
        var packet = new byte[12 + questionNameLength + 4];
        BinaryPrimitives.WriteUInt16BigEndian(packet.AsSpan(4), 1);

        var offset = 12;
        foreach (var label in labels)
        {
            packet[offset++] = (byte)label.Length;
            Encoding.ASCII.GetBytes(label).CopyTo(packet.AsSpan(offset));
            offset += label.Length;
        }

        packet[offset++] = 0;
        BinaryPrimitives.WriteUInt16BigEndian(packet.AsSpan(offset), 12);
        BinaryPrimitives.WriteUInt16BigEndian(packet.AsSpan(offset + 2), 1);
        return packet;
    }
}

public sealed class TestDnsServerLiveTests
{
    private const int TestListenPort = 55353;

    [Fact]
    public async Task Server_responds_to_wildcard_dev_query_over_udp()
    {
        await using var server = new TestDnsServer(TestListenPort);
        server.Configure(LocalDnsServerOptions.Create(
            [".test", ".dev", ".com"],
            ["example.dev", "*.example.dev"]));

        await server.StartAsync();

        using var client = new System.Net.Sockets.UdpClient();
        client.Client.ReceiveTimeout = 3000;
        client.Connect(System.Net.IPAddress.Loopback, TestListenPort);

        var query = TestDnsServerResponseTests.BuildAQuery("api.example.dev");
        await client.SendAsync(query);

        using var timeoutCts = new CancellationTokenSource(TimeSpan.FromSeconds(3));
        var result = await client.ReceiveAsync(timeoutCts.Token);

        Assert.True(result.Buffer.Length > 12);
    }

    [Fact]
    public async Task Server_survives_cancelled_startup_token()
    {
        using var startupCts = new CancellationTokenSource();
        await using var server = new TestDnsServer(TestListenPort);
        server.Configure(LocalDnsServerOptions.Create([".test"], []));

        await server.StartAsync(startupCts.Token);
        await startupCts.CancelAsync();
        await Task.Delay(50);

        Assert.True(server.IsRunning);

        using var client = new System.Net.Sockets.UdpClient();
        client.Connect(System.Net.IPAddress.Loopback, TestListenPort);
        await client.SendAsync(TestDnsServerResponseTests.BuildAQuery("app.example.test"));

        using var timeoutCts = new CancellationTokenSource(TimeSpan.FromSeconds(3));
        var result = await client.ReceiveAsync(timeoutCts.Token);

        Assert.True(result.Buffer.Length > 12);
    }
}
