using System.Diagnostics;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using WeaveFleet.Application.Browser;
using WeaveFleet.Application.Configuration;

namespace WeaveFleet.Infrastructure.Browser;

/// <summary>
/// Takes pictures of pages with Fleet's headless browser (<see cref="ChromeHost"/>), shared with the tabs agents use.
/// Shots are taken one at a time, each in its own tab, so nothing a page does reaches the next one; the browser stays
/// up between shots and quits <see cref="ChromeHost.IdleTimeout"/> after nobody needs it.
/// </summary>
public sealed class HeadlessChromeScreenshotter : IScreenshotter, IAsyncDisposable
{
    /// <summary>How long the browser waits for another shot (or an agent's tab) before quitting.</summary>
    public static TimeSpan IdleTimeout => ChromeHost.IdleTimeout;

    private static readonly TimeSpan LoadTimeout = TimeSpan.FromSeconds(10);

    /// <summary>After the load event: enough for a framework to paint its first frame, short enough not to drag.</summary>
    internal static readonly TimeSpan SettleDelay = TimeSpan.FromMilliseconds(400);

    private static readonly Action<ILogger, string, int, int, long, Exception?> LogCaptured =
        LoggerMessage.Define<string, int, int, long>(LogLevel.Information, new EventId(2, "ScreenshotCaptured"),
            "Captured {Url} at {Width}x{Height} in {Elapsed} ms");

    private static readonly Action<ILogger, string, Exception?> LogFailed =
        LoggerMessage.Define<string>(LogLevel.Warning, new EventId(3, "ScreenshotFailed"), "Taking a screenshot failed: {Problem}");

    private readonly SemaphoreSlim _gate = new(1, 1);
    private readonly ChromeHost _host;
    private readonly bool _ownsHost;
    private readonly ILogger _logger;
    private bool _disposed;

    /// <summary>Shoots with the browser <paramref name="host"/> keeps, shared with the agents' tabs.</summary>
    public HeadlessChromeScreenshotter(ChromeHost host, ILogger<HeadlessChromeScreenshotter> logger)
    {
        _host = host;
        _logger = logger;
    }

    /// <summary>Shoots with a browser of its own, quit when this is disposed.</summary>
    public HeadlessChromeScreenshotter(FleetOptions options, ILogger<HeadlessChromeScreenshotter> logger)
        : this(new ChromeHost(options, NullLogger<ChromeHost>.Instance), logger)
    {
        _ownsHost = true;
    }

    public async Task<ScreenshotOutcome> CaptureAsync(ScreenshotRequest request, CancellationToken ct = default)
    {
        await _gate.WaitAsync(ct);
        try
        {
            if (_disposed)
                return ScreenshotOutcome.Fail("Fleet is shutting down.");

            using var lease = await _host.AcquireAsync(ct);
            if (lease.Connection is not { } cdp)
                return ScreenshotOutcome.Fail(lease.Problem ?? ChromeFinder.NotFound);

            try
            {
                var started = Stopwatch.StartNew();
                var shot = await ShootAsync(cdp, request, ct);
                if (shot.Image is not null)
                    LogCaptured(_logger, request.Url, request.Width, request.Height, started.ElapsedMilliseconds, null);
                else if (shot.Problem is { } failure)
                    LogFailed(_logger, failure, null);
                return shot;
            }
            catch (CdpException error)
            {
                // A browser that died mid-shot shouldn't poison the next call.
                await _host.ResetAsync(cdp);
                LogFailed(_logger, error.Message, null);
                return ScreenshotOutcome.Fail(error.Message);
            }
        }
        finally
        {
            _gate.Release();
        }
    }

    private static async Task<ScreenshotOutcome> ShootAsync(CdpConnection cdp, ScreenshotRequest request, CancellationToken ct)
    {
        await using var tab = await HeadlessTab.OpenAsync(cdp, request.Width, request.Height, ct);

        var (error, loaded) = await tab.NavigateAsync(request.Url, LoadTimeout, ct);
        if (error is not null)
            return ScreenshotOutcome.Fail($"The browser couldn't open {request.Url}: {error}. Is the app still running?");

        // A page that never fires "load" still has something on it, and a picture of it says more than an
        // error does: dev servers hold connections open (HMR sockets, a slow asset), and the agent asked to
        // see the page, not to hear about its network.
        var note = loaded
            ? null
            : $"The page hadn't finished loading after {LoadTimeout.TotalSeconds:0} seconds; this is how far it had got.";

        await Task.Delay(SettleDelay, ct);

        // Like every call here, it gives up when the browser stops answering (CdpConnection.DefaultReplyTimeout),
        // and CaptureAsync quits that browser so the next shot starts a fresh one.
        using var captured = await cdp.SendAsync("Page.captureScreenshot", write =>
        {
            write.WriteString("format", "png");
            write.WriteBoolean("captureBeyondViewport", false);
        }, tab.Session, ct);

        var data = captured.RootElement.GetProperty("result").GetProperty("data").GetString();
        return string.IsNullOrEmpty(data)
            ? ScreenshotOutcome.Fail("The browser returned an empty screenshot.")
            : ScreenshotOutcome.Ok(Convert.FromBase64String(data), request.Width, request.Height, note);
    }

    public async ValueTask DisposeAsync()
    {
        await _gate.WaitAsync();
        try
        {
            _disposed = true;
            if (_ownsHost)
                await _host.DisposeAsync();
        }
        finally
        {
            _gate.Release();
        }
    }
}
