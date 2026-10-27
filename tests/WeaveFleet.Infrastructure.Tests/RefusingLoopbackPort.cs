using System.Net;
using System.Net.Sockets;

namespace WeaveFleet.Infrastructure.Tests;

/// <summary>
/// A loopback port that refuses connections for as long as this is held: a socket bound to it that never listens.
/// A port found free and let go can be taken by a test running in parallel before it's used, and then something
/// answers. While the socket holds the port, nothing else can bind it.
/// </summary>
public sealed class RefusingLoopbackPort : IDisposable
{
    private readonly Socket _socket = new(AddressFamily.InterNetwork, SocketType.Stream, ProtocolType.Tcp);

    public RefusingLoopbackPort()
    {
        _socket.Bind(new IPEndPoint(IPAddress.Loopback, 0));
        // On Unix, Bind turns SO_REUSEADDR on, which lets another socket with it on listen on a port this one only binds.
        _socket.SetSocketOption(SocketOptionLevel.Socket, SocketOptionName.ReuseAddress, false);
        Port = ((IPEndPoint)_socket.LocalEndPoint!).Port;
    }

    public int Port { get; }

    public void Dispose() => _socket.Dispose();
}
