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

    /// <summary>How long closing a tab may take after a shot; a browser that doesn't answer is quit anyway.</summary>
    private static readonly TimeSpan CloseTabTimeout = TimeSpan.FromSeconds(2);

    /// <summary>After the load event: enough for a framework to paint its first frame, short enough not to drag.</summary>
    private static readonly TimeSpan SettleDelay = TimeSpan.FromMilliseconds(400);

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
        var created = await cdp.SendAsync("Target.createTarget", write => write.WriteString("url", "about:blank"), ct: ct);
        string target;
        using (created)
        {
            target = created.RootElement.GetProperty("result").GetProperty("targetId").GetString()!;
        }

        try
        {
            var attached = await cdp.SendAsync("Target.attachToTarget", write =>
            {
                write.WriteString("targetId", target);
                write.WriteBoolean("flatten", true);
            }, ct: ct);

            string page;
            using (attached)
            {
                page = attached.RootElement.GetProperty("result").GetProperty("sessionId").GetString()!;
            }

            (await cdp.SendAsync("Page.enable", sessionId: page, ct: ct)).Dispose();
            (await cdp.SendAsync("Network.enable", sessionId: page, ct: ct)).Dispose();

            // The whole point of the tool is seeing the change just made. A warm browser that serves the page it
            // saw a minute ago would show the old one, and the agent would trust it.
            (await cdp.SendAsync("Network.setCacheDisabled", write => write.WriteBoolean("cacheDisabled", true), page, ct)).Dispose();

            // Not "mobile": that makes Chrome treat a page without a viewport meta tag as 980 CSS px wide and
            // shrink it to fit, so a narrow shot comes back as the desktop layout in miniature. A plain window of
            // the asked-for size is what a developer dragging their browser narrow sees, and fires the same
            // media queries.
            (await cdp.SendAsync("Emulation.setDeviceMetricsOverride", write =>
            {
                write.WriteNumber("width", request.Width);
                write.WriteNumber("height", request.Height);
                write.WriteNumber("deviceScaleFactor", 1);
                write.WriteBoolean("mobile", false);
            }, page, ct)).Dispose();

            using var loaded = cdp.Expect("Page.loadEventFired", page);
            var navigated = await cdp.SendAsync("Page.navigate", write => write.WriteString("url", request.Url), page, ct);
            using (navigated)
            {
                var result = navigated.RootElement.GetProperty("result");
                if (result.TryGetProperty("errorText", out var error) && error.GetString() is { Length: > 0 } text)
                    return ScreenshotOutcome.Fail($"The browser couldn't open {request.Url}: {text}. Is the app still running?");
            }

            // A page that never fires "load" still has something on it, and a picture of it says more than an
            // error does: dev servers hold connections open (HMR sockets, a slow asset), and the agent asked to
            // see the page, not to hear about its network.
            var note = await loaded.ArrivedAsync(LoadTimeout, ct)
                ? null
                : $"The page hadn't finished loading after {LoadTimeout.TotalSeconds:0} seconds; this is how far it had got.";

            await Task.Delay(SettleDelay, ct);

            // Like every call here, it gives up when the browser stops answering (CdpConnection.DefaultReplyTimeout),
            // and CaptureAsync quits that browser so the next shot starts a fresh one.
            using var captured = await cdp.SendAsync("Page.captureScreenshot", write =>
            {
                write.WriteString("format", "png");
                write.WriteBoolean("captureBeyondViewport", false);
            }, page, ct);

            var data = captured.RootElement.GetProperty("result").GetProperty("data").GetString();
            return string.IsNullOrEmpty(data)
                ? ScreenshotOutcome.Fail("The browser returned an empty screenshot.")
                : ScreenshotOutcome.Ok(Convert.FromBase64String(data), request.Width, request.Height, note);
        }
        finally
        {
            try
            {
                using var closing = new CancellationTokenSource(CloseTabTimeout);
                (await cdp.SendAsync("Target.closeTarget", write => write.WriteString("targetId", target), ct: closing.Token)).Dispose();
            }
            catch (Exception error) when (error is CdpException or OperationCanceledException)
            {
                // The tab goes with the browser.
            }
        }
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
