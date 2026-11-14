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

    [Fact]
    public async Task A_browser_that_takes_the_connection_and_never_answers_fails_the_connect_instead_of_hanging_it()
    {
        // A Chrome that wrote its DevTools port and then wedged: the socket connects (the kernel accepts it), and the
        // websocket upgrade is never answered. A macOS CI run sat on this until the job's 15 minutes ran out.
        using var port = new MuteBrowser();

        var connect = CdpConnection.ConnectAsync(port.Url, CancellationToken.None, connectTimeout: TimeSpan.FromMilliseconds(300));

        var error = await Should.ThrowAsync<CdpException>(connect.WaitAsync(TimeSpan.FromSeconds(10)));
        error.Message.ShouldBe("The browser didn't answer on its DevTools port within 0.3 seconds.");
    }

    [Fact]
    public async Task A_caller_that_gives_up_on_the_connect_is_told_it_was_cancelled()
    {
        using var port = new MuteBrowser();
        using var giveUp = new CancellationTokenSource(TimeSpan.FromMilliseconds(200));

        var connect = CdpConnection.ConnectAsync(port.Url, giveUp.Token, connectTimeout: TimeSpan.FromSeconds(30));

        await Should.ThrowAsync<OperationCanceledException>(connect.WaitAsync(TimeSpan.FromSeconds(10)));
    }

    /// <summary>A port that takes the connection and never says anything back, not even to the websocket upgrade.</summary>
    internal sealed class MuteBrowser : IDisposable
    {
        private readonly TcpListener _listener = new(IPAddress.Loopback, 0);

        public MuteBrowser()
        {
            _listener.Start();
            Port = ((IPEndPoint)_listener.LocalEndpoint).Port;
            Url = new Uri($"ws://127.0.0.1:{Port}/devtools/browser/mute");
        }

        public int Port { get; }

        public Uri Url { get; }

        public void Dispose() => _listener.Stop();
    }

    /// <summary>A DevTools endpoint that accepts the websocket and then reads without ever replying.</summary>
    private sealed class SilentBrowser : IDisposable
    {
        private readonly HttpListener _listener;

        public SilentBrowser()
        {
            _listener = LoopbackListener.Start(out var port);
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
