using System.Net.Sockets;

namespace Stackroot.Core.Dns;

internal static class UdpSocketHardening
{
    // On Windows, a UDP send to a port that answers with ICMP "port unreachable"
    // poisons the socket: the NEXT ReceiveAsync throws SocketException(ConnectionReset).
    // Clients that give up under load close their ports constantly, so an unhardened
    // listener degrades exactly when it is needed most. SIO_UDP_CONNRESET disables
    // that behavior.
    private const int SioUdpConnReset = unchecked((int)0x9800000C);

    public static void Apply(Socket socket, int? receiveBufferBytes = null)
    {
        if (OperatingSystem.IsWindows())
        {
            try
            {
                socket.IOControl(SioUdpConnReset, [0, 0, 0, 0], null);
            }
            catch
            {
                // Best effort — the receive loop also tolerates ConnectionReset directly.
            }
        }

        if (receiveBufferBytes is { } size)
        {
            try
            {
                socket.ReceiveBufferSize = size;
            }
            catch
            {
            }
        }
    }
}
