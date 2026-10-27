using System.Net;
using System.Net.Sockets;

namespace WeaveFleet.Infrastructure.Tests;

/// <summary>
/// Starts an <see cref="HttpListener"/> on a free loopback port. Finding a free port and then listening on it races
/// the other tests doing the same in parallel, so a listener that loses the port tries again on another.
/// </summary>
public static class LoopbackListener
{
    /// <summary>A started listener for <c>http://127.0.0.1:{port}/</c>.</summary>
    public static HttpListener Start(out int port)
    {
        for (var attempt = 1; ; attempt++)
        {
            port = FreePort();
            var listener = new HttpListener();
            listener.Prefixes.Add($"http://127.0.0.1:{port}/");
            try
            {
                listener.Start();
                return listener;
            }
            catch (HttpListenerException) when (attempt < 10)
            {
                listener.Close();
            }
        }
    }

    private static int FreePort()
    {
        var probe = new TcpListener(IPAddress.Loopback, 0);
        probe.Start();
        var port = ((IPEndPoint)probe.LocalEndpoint).Port;
        probe.Stop();
        return port;
    }
}
