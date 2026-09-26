using System.Net;
using System.Net.Sockets;
using System.Net.WebSockets;
using WeaveFleet.Infrastructure.Browser;

namespace WeaveFleet.Infrastructure.Tests.Browser;

public sealed class CdpConnectionTests
{
    [Fact]
    public async Task A_browser_that_never_answers_fails_the_call_instead_of_hanging_it()
    {
        // A wedged Chrome keeps its DevTools socket open and says nothing; with no caller token, a call used to wait
        // forever (a CI run sat on the screenshot tests until the job timed out).
        using var browser = new SilentBrowser();
        await using var cdp = await CdpConnection.ConnectAsync(browser.Url, CancellationToken.None, replyTimeout: TimeSpan.FromMilliseconds(200));

        var call = cdp.SendAsync("Target.createTarget", write => write.WriteString("url", "about:blank"));

        var error = await Should.ThrowAsync<CdpException>(call.WaitAsync(TimeSpan.FromSeconds(10)));
        error.Message.ShouldBe("The browser didn't answer Target.createTarget within 0.2 seconds.");
    }

    /// <summary>A DevTools endpoint that accepts the websocket and then reads without ever replying.</summary>
    private sealed class SilentBrowser : IDisposable
    {
        private readonly HttpListener _listener = new();

        public SilentBrowser()
        {
            var probe = new TcpListener(IPAddress.Loopback, 0);
            probe.Start();
            var port = ((IPEndPoint)probe.LocalEndpoint).Port;
            probe.Stop();

            _listener.Prefixes.Add($"http://127.0.0.1:{port}/");
            _listener.Start();
            Url = new Uri($"ws://127.0.0.1:{port}/devtools/browser/silent");
            _ = Task.Run(async () =>
            {
                try
                {
                    var context = await _listener.GetContextAsync();
                    var socket = (await context.AcceptWebSocketAsync(subProtocol: null)).WebSocket;
                    var buffer = new byte[4096];
                    while (socket.State == WebSocketState.Open)
                        await socket.ReceiveAsync(buffer, CancellationToken.None);
                }
                catch (Exception error) when (error is HttpListenerException or ObjectDisposedException or WebSocketException or InvalidOperationException)
                {
                    // The test is over.
                }
            });
        }

        public Uri Url { get; }

        public void Dispose() => _listener.Close();
    }
}
