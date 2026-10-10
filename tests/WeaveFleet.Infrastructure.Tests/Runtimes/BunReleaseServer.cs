using System.Net;
using System.Net.Sockets;

namespace WeaveFleet.Infrastructure.Tests.Runtimes;

/// <summary>How the fixture answers a request for one path.</summary>
internal enum BunServeMode
{
    /// <summary>Send the whole body.</summary>
    Normal,

    /// <summary>Declare more bytes than are sent, then drop the connection.</summary>
    Truncate,

    /// <summary>Send a few bytes, then say nothing until the fixture is disposed.</summary>
    Stall,

    /// <summary>Send a few bytes, wait for <see cref="BunReleaseServer.ReleaseHeld"/>, then send the rest.</summary>
    Hold,

    /// <summary>Accept the request and never answer it, so the client waits for the response headers.</summary>
    Silent,
}

/// <summary>A loopback HTTP server that plays GitHub's release downloads for the Bun installer tests.</summary>
internal sealed class BunReleaseServer : IDisposable
{
    private readonly HttpListener _listener = new();
    private readonly CancellationTokenSource _stop = new();
    private readonly Dictionary<string, (byte[] Body, int Status, BunServeMode Mode)> _routes = [];
    private readonly List<string> _requests = [];
    private readonly TaskCompletionSource _stalled = new(TaskCreationOptions.RunContinuationsAsynchronously);
    private readonly TaskCompletionSource _held = new(TaskCreationOptions.RunContinuationsAsynchronously);
    private readonly TaskCompletionSource _release = new(TaskCreationOptions.RunContinuationsAsynchronously);

    public BunReleaseServer()
    {
        var probe = new TcpListener(IPAddress.Loopback, 0);
        probe.Start();
        var port = ((IPEndPoint)probe.LocalEndpoint).Port;
        probe.Stop();

        BaseUri = new Uri($"http://127.0.0.1:{port}/");
        _listener.Prefixes.Add(BaseUri.ToString());
        _listener.Start();
        _ = Task.Run(AcceptAsync);
    }

    /// <summary>The address to use as the installer's download base.</summary>
    public Uri BaseUri { get; }

    /// <summary>Completes once a <see cref="BunServeMode.Stall"/> response has sent its first bytes.</summary>
    public Task Stalled => _stalled.Task;

    /// <summary>Completes once a <see cref="BunServeMode.Hold"/> response has sent its first bytes and is waiting.</summary>
    public Task Held => _held.Task;

    /// <summary>Lets every <see cref="BunServeMode.Hold"/> response send the rest of its body.</summary>
    public void ReleaseHeld() => _release.TrySetResult();

    /// <summary>The paths requested so far, in order.</summary>
    public IReadOnlyList<string> Requests
    {
        get
        {
            lock (_requests)
                return [.. _requests];
        }
    }

    /// <summary>Answers requests for <paramref name="path"/> (e.g. <c>/bun-v1.4.2/bun.zip</c>) from now on.</summary>
    public void Serve(string path, byte[] body, int status = 200, BunServeMode mode = BunServeMode.Normal)
    {
        lock (_routes)
            _routes[path] = (body, status, mode);
    }

    public void Dispose()
    {
        _stop.Cancel();
        _listener.Close();
    }

    private async Task AcceptAsync()
    {
        while (!_stop.IsCancellationRequested)
        {
            HttpListenerContext context;
            try
            {
                context = await _listener.GetContextAsync();
            }
            catch (Exception error) when (error is HttpListenerException or ObjectDisposedException or InvalidOperationException)
            {
                return;
            }

            _ = Task.Run(() => RespondAsync(context));
        }
    }

    private async Task RespondAsync(HttpListenerContext context)
    {
        var path = context.Request.Url!.AbsolutePath;
        lock (_requests)
            _requests.Add(path);

        (byte[] Body, int Status, BunServeMode Mode) route;
        bool found;
        lock (_routes)
            found = _routes.TryGetValue(path, out route);

        var response = context.Response;
        try
        {
            if (!found)
            {
                response.StatusCode = 404;
                response.Close();
                return;
            }

            if (route.Status != 200)
            {
                response.StatusCode = route.Status;
                response.Close();
                return;
            }

            switch (route.Mode)
            {
                case BunServeMode.Silent:
                    await Task.Delay(Timeout.Infinite, _stop.Token);
                    break;
                case BunServeMode.Normal:
                    response.ContentLength64 = route.Body.Length;
                    await response.OutputStream.WriteAsync(route.Body);
                    response.Close();
                    break;
                case BunServeMode.Truncate:
                    response.ContentLength64 = route.Body.Length + 100;
                    await response.OutputStream.WriteAsync(route.Body);
                    await response.OutputStream.FlushAsync();
                    response.Abort();
                    break;
                case BunServeMode.Stall:
                    response.ContentLength64 = route.Body.Length;
                    await response.OutputStream.WriteAsync(route.Body.AsMemory(0, Math.Min(10, route.Body.Length)));
                    await response.OutputStream.FlushAsync();
                    _stalled.TrySetResult();
                    await Task.Delay(Timeout.Infinite, _stop.Token);
                    break;
                case BunServeMode.Hold:
                    var first = Math.Min(10, route.Body.Length);
                    response.ContentLength64 = route.Body.Length;
                    await response.OutputStream.WriteAsync(route.Body.AsMemory(0, first));
                    await response.OutputStream.FlushAsync();
                    _held.TrySetResult();
                    await _release.Task.WaitAsync(_stop.Token);
                    await response.OutputStream.WriteAsync(route.Body.AsMemory(first));
                    response.Close();
                    break;
            }
        }
        catch (Exception error) when (error is HttpListenerException or ObjectDisposedException or IOException or OperationCanceledException)
        {
            // The client went away or the test is over.
        }
    }
}
