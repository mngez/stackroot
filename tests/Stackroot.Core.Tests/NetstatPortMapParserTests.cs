using Stackroot.Core.Windows;
using Xunit;

namespace Stackroot.Core.Tests;

public sealed class NetstatPortMapParserTests
{
    [Fact]
    public void Parse_maps_listening_tcp_ports_to_pids()
    {
        const string output = """
            Active Connections

              Proto  Local Address          Foreign Address        State           PID
              TCP    0.0.0.0:80             0.0.0.0:0              LISTENING       1234
              TCP    127.0.0.1:6379         0.0.0.0:0              LISTENING       5678
              TCP    [::1]:3306             [::]:0                 LISTENING       9012
              TCP    127.0.0.1:8080         127.0.0.1:54321        ESTABLISHED     4444
            """;

        var map = NetstatPortMapParser.Parse(output);

        Assert.Equal(new[] { 1234 }, map[80]);
        Assert.Equal(new[] { 5678 }, map[6379]);
        Assert.Equal(new[] { 9012 }, map[3306]);
        Assert.False(map.ContainsKey(8080));
        Assert.False(map.ContainsKey(54321));
    }

    [Fact]
    public void Parse_deduplicates_pids_for_same_port()
    {
        const string output = """
              TCP    0.0.0.0:9000           0.0.0.0:0              LISTENING       42
              TCP    127.0.0.1:9000         0.0.0.0:0              LISTENING       42
            """;

        var map = NetstatPortMapParser.Parse(output);

        Assert.Single(map[9000]);
        Assert.Equal(42, map[9000][0]);
    }

    [Fact]
    public void ParseUdp_maps_bound_udp_ports_to_pids()
    {
        const string output = """
              UDP    0.0.0.0:11211          *:*                                    4321
              UDP    [::]:11211               *:*                                    4321
            """;

        var map = NetstatPortMapParser.ParseUdp(output);

        Assert.Equal(new[] { 4321 }, map[11211]);
    }

    [Fact]
    public void Parse_merges_tcp_and_udp_on_same_port()
    {
        const string output = """
              TCP    0.0.0.0:11211          0.0.0.0:0              LISTENING       1111
              UDP    0.0.0.0:11211          *:*                                    2222
            """;

        var map = NetstatPortMapParser.Parse(output);

        Assert.Equal(new[] { 1111, 2222 }, map[11211].OrderBy(static pid => pid).ToArray());
    }
}
