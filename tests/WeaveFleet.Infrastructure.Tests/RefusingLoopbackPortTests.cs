using System.Net;
using System.Net.Sockets;

namespace WeaveFleet.Infrastructure.Tests;

public sealed class RefusingLoopbackPortTests
{
    [Fact]
    public async Task A_connection_to_it_is_refused()
    {
        using var port = new RefusingLoopbackPort();
        using var client = new TcpClient();

        var error = await Should.ThrowAsync<SocketException>(() => client.ConnectAsync(IPAddress.Loopback, port.Port));

        error.SocketErrorCode.ShouldBe(SocketError.ConnectionRefused);
    }

    [Fact]
    public void Nothing_else_can_listen_on_it_while_it_is_held()
    {
        using var port = new RefusingLoopbackPort();
        var listener = new TcpListener(IPAddress.Loopback, port.Port);

        var error = Should.Throw<SocketException>(() => listener.Start());

        error.SocketErrorCode.ShouldBe(SocketError.AddressAlreadyInUse);
    }
}
