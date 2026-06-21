using Stackroot.Core.Windows;
using Xunit;

namespace Stackroot.Core.Tests;

public sealed class TcpPortEndianTests
{
    [Theory]
    [InlineData(0x5000u, 80)]
    [InlineData(0x901Fu, 8080)]
    [InlineData(0xF71Fu, 8183)]
    public void NetworkOrderToPort_converts_windows_tcp_row_port(uint networkPort, int expectedPort)
    {
        Assert.Equal(expectedPort, TcpPortEndian.NetworkOrderToPort(networkPort));
    }
}
