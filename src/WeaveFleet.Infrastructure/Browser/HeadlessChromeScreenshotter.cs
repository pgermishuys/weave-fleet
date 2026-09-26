using System.Diagnostics;
using System.Text.Json;
using Microsoft.Extensions.Logging;
using WeaveFleet.Application.Browser;
using WeaveFleet.Application.Configuration;

namespace WeaveFleet.Infrastructure.Browser;

/// <summary>
/// Takes pictures of pages with a headless Chrome (or Edge, or Chromium) that is already installed, driven over
/// the DevTools protocol. One browser serves every session: starting it costs about half a second, so it's kept
/// running between shots and quits after <see cref="IdleTimeout"/> to give its memory back — this machine may be
/// running the app being shot as well. Shots are taken one at a time, each in its own tab, so nothing a page does
/// reaches the next one.
/// </summary>
public sealed class HeadlessChromeScreenshotter(FleetOptions options, ILogger<HeadlessChromeScreenshotter> logger)
    : IScreenshotter, IAsyncDisposable
{
    /// <summary>How long the browser waits for another shot before quitting.</summary>
    public static readonly TimeSpan IdleTimeout = TimeSpan.FromMinutes(2);

    private static readonly TimeSpan LaunchTimeout = TimeSpan.FromSeconds(20);
    private static readonly TimeSpan LoadTimeout = TimeSpan.FromSeconds(10);

    /// <summary>How long closing a tab may take after a shot; a browser that doesn't answer is quit anyway.</summary>
    private static readonly TimeSpan CloseTabTimeout = TimeSpan.FromSeconds(2);

    /// <summary>After the load event: enough for a framework to paint its first frame, short enough not to drag.</summary>
    private static readonly TimeSpan SettleDelay = TimeSpan.FromMilliseconds(400);

    private static readonly Action<ILogger, string, Exception?> LogBrowser =
        LoggerMessage.Define<string>(LogLevel.Information, new EventId(1, "ScreenshotBrowser"), "Screenshots use {Path}");

    private static readonly Action<ILogger, string, int, int, long, Exception?> LogCaptured =
        LoggerMessage.Define<string, int, int, long>(LogLevel.Information, new EventId(2, "ScreenshotCaptured"),
            "Captured {Url} at {Width}x{Height} in {Elapsed} ms");

    private static readonly Action<ILogger, string, Exception?> LogSandbox =
        LoggerMessage.Define<string>(LogLevel.Warning, new EventId(4, "ScreenshotNoSandbox"),
            "{Path} couldn't open its sandbox, so Fleet runs it with --no-sandbox for screenshots");

    private static readonly Action<ILogger, string, Exception?> LogFailed =
        LoggerMessage.Define<string>(LogLevel.Warning, new EventId(3, "ScreenshotFailed"), "Taking a screenshot failed: {Problem}");

    private readonly SemaphoreSlim _gate = new(1, 1);
    private Process? _browser;
    private CdpConnection? _cdp;
    private string? _profile;
    private Timer? _idle;
    private bool _sandbox = true;
    private bool _disposed;

    public async Task<ScreenshotOutcome> CaptureAsync(ScreenshotRequest request, CancellationToken ct = default)
    {
        await _gate.WaitAsync(ct);
        try
        {
            if (_disposed)
                return ScreenshotOutcome.Fail("Fleet is shutting down.");

            var browser = await ConnectedAsync(ct);
            if (browser.Problem is { } problem)
                return ScreenshotOutcome.Fail(problem);

            var started = Stopwatch.StartNew();
            var shot = await ShootAsync(browser.Connection!, request, ct);
            if (shot.Image is not null)
                LogCaptured(logger, request.Url, request.Width, request.Height, started.ElapsedMilliseconds, null);
            else if (shot.Problem is { } failure)
                LogFailed(logger, failure, null);
            return shot;
        }
        catch (CdpException error)
        {
            // A browser that died mid-shot shouldn't poison the next call.
            await QuitAsync();
            LogFailed(logger, error.Message, null);
            return ScreenshotOutcome.Fail(error.Message);
        }
        finally
        {
            _idle?.Change(IdleTimeout, Timeout.InfiniteTimeSpan);
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

    private async Task<(CdpConnection? Connection, string? Problem)> ConnectedAsync(CancellationToken ct)
    {
        if (_cdp is { IsOpen: true } open && _browser is { HasExited: false })
            return (open, null);

        await QuitAsync();

        var path = ChromeFinder.Find(options.Browser.ChromePath);
        if (path is null)
            return (null, ChromeFinder.NotFound);

        var launched = await LaunchAsync(path, _sandbox, ct);

        // A Chrome outside a distribution's own package (Playwright's, a tarball) can't open its sandbox where
        // unprivileged user namespaces are locked down, and nor can a Fleet running as root. Both die before the
        // DevTools port, saying so; the second try drops the sandbox and every later launch skips straight to it.
        if (launched.Problem is not null && _sandbox && launched.Sandbox)
        {
            LogSandbox(logger, path, null);
            _sandbox = false;
            launched = await LaunchAsync(path, sandbox: false, ct);
        }

        if (launched.Problem is { } problem)
            return (null, problem);

        _idle ??= new Timer(_ => _ = QuitIdleAsync(), null, Timeout.Infinite, Timeout.Infinite);
        return (_cdp, null);
    }

    private async Task<(bool Started, string? Problem, bool Sandbox)> LaunchAsync(string path, bool sandbox, CancellationToken ct)
    {
        LogBrowser(logger, path, null);
        _profile = Path.Combine(Path.GetTempPath(), "fleet-screenshots-" + Guid.NewGuid().ToString("n"));
        Directory.CreateDirectory(_profile);

        var start = new ProcessStartInfo(path)
        {
            RedirectStandardError = true,
            RedirectStandardOutput = true,
            UseShellExecute = false,
        };
        foreach (var argument in Arguments(_profile, sandbox))
            start.ArgumentList.Add(argument);

        try
        {
            _browser = Process.Start(start);
        }
        catch (Exception error) when (error is System.ComponentModel.Win32Exception or InvalidOperationException)
        {
            return (false, $"Fleet couldn't start {path}: {error.Message}", false);
        }

        if (_browser is null)
            return (false, $"Fleet couldn't start {path}.", false);

        // Reading the pipes keeps Chrome from blocking on a full buffer once it gets chatty, and the last few
        // lines are what explains a launch that never came up.
        var complaints = new Complaints();
        _browser.OutputDataReceived += (_, line) => complaints.Add(line.Data);
        _browser.ErrorDataReceived += (_, line) => complaints.Add(line.Data);
        _browser.BeginOutputReadLine();
        _browser.BeginErrorReadLine();

        var endpoint = await DevToolsUrlAsync(_profile, _browser, ct);
        if (endpoint is null)
        {
            await QuitAsync();
            return (false, $"{path} started but never opened its DevTools port, so Fleet can't drive it.{complaints}", complaints.MentionsSandbox);
        }

        try
        {
            _cdp = await CdpConnection.ConnectAsync(endpoint, ct);
        }
        catch (Exception error) when (error is not OperationCanceledException)
        {
            await QuitAsync();
            return (false, $"Fleet couldn't connect to the browser it started: {error.Message}", false);
        }

        return (true, null, false);
    }

    /// <summary>
    /// The first lines the browser printed, for a message about a launch that didn't come up. The first ones,
    /// not the last: a Chrome that dies says why and then dumps a stack trace and its registers.
    /// </summary>
    private sealed class Complaints
    {
        private const int Keep = 4;
        private readonly List<string> _lines = [];

        /// <summary>Whether anything it said was about the sandbox it couldn't open.</summary>
        public bool MentionsSandbox { get; private set; }

        public void Add(string? line)
        {
            if (string.IsNullOrWhiteSpace(line))
                return;
            lock (_lines)
            {
                if (line.Contains("sandbox", StringComparison.OrdinalIgnoreCase)
                    || line.Contains("namespace", StringComparison.OrdinalIgnoreCase))
                {
                    MentionsSandbox = true;
                }

                if (_lines.Count < Keep)
                    _lines.Add(line.Trim());
            }
        }

        public override string ToString()
        {
            lock (_lines)
            {
                return _lines.Count == 0 ? string.Empty : "\nIt said:\n" + string.Join('\n', _lines);
            }
        }
    }

    /// <summary>
    /// Chrome writes "port\npath" to DevToolsActivePort in its profile once the debugger listens. Asking for
    /// port 0 and reading it back is the only way to get a free port without racing another process for it.
    /// </summary>
    private static async Task<Uri?> DevToolsUrlAsync(string profile, Process browser, CancellationToken ct)
    {
        var file = Path.Combine(profile, "DevToolsActivePort");
        var deadline = DateTimeOffset.UtcNow + LaunchTimeout;
        while (DateTimeOffset.UtcNow < deadline)
        {
            if (browser.HasExited)
                return null;

            if (File.Exists(file))
            {
                try
                {
                    var lines = await File.ReadAllLinesAsync(file, ct);
                    if (lines.Length >= 2 && int.TryParse(lines[0], out var port))
                        return new Uri($"ws://127.0.0.1:{port}{lines[1]}");
                }
                catch (IOException)
                {
                    // Chrome is still writing it.
                }
            }

            await Task.Delay(25, ct);
        }

        return null;
    }

    private static IEnumerable<string> Arguments(string profile, bool sandbox)
    {
        if (!sandbox)
            yield return "--no-sandbox";

        yield return "--headless=new";
        yield return "--remote-debugging-port=0";
        yield return "--user-data-dir=" + profile;
        yield return "--no-first-run";
        yield return "--no-default-browser-check";
        yield return "--disable-gpu";
        yield return "--hide-scrollbars";
        yield return "--mute-audio";
        yield return "--disable-dev-shm-usage";
        // Without these the first capture waits several seconds on Chrome talking to Google.
        yield return "--disable-background-networking";
        yield return "--disable-sync";
        yield return "--disable-default-apps";
        yield return "--disable-extensions";
        yield return "--disable-component-update";
        yield return "--metrics-recording-only";
        yield return "about:blank";
    }

    private async Task QuitIdleAsync()
    {
        if (!await _gate.WaitAsync(TimeSpan.Zero))
            return;
        try
        {
            await QuitAsync();
        }
        finally
        {
            _gate.Release();
        }
    }

    /// <summary>Closes the connection and the browser, and forgets both. The caller holds the gate.</summary>
    private async Task QuitAsync()
    {
        if (_cdp is { } cdp)
        {
            _cdp = null;
            try
            {
                if (cdp.IsOpen)
                    (await cdp.SendAsync("Browser.close", ct: CancellationToken.None).WaitAsync(TimeSpan.FromSeconds(2))).Dispose();
            }
            catch (Exception error) when (error is CdpException or TimeoutException)
            {
                // It's being killed next.
            }

            await cdp.DisposeAsync();
        }

        if (_browser is { } browser)
        {
            _browser = null;
            try
            {
                if (!browser.WaitForExit(2000))
                    browser.Kill(entireProcessTree: true);
            }
            catch (Exception error) when (error is InvalidOperationException or NotSupportedException or SystemException)
            {
                // Already gone.
            }

            browser.Dispose();
        }

        if (_profile is { } profile)
        {
            _profile = null;
            try
            {
                Directory.Delete(profile, recursive: true);
            }
            catch (Exception error) when (error is IOException or UnauthorizedAccessException)
            {
                // A temp folder Fleet will not use again.
            }
        }
    }

    public async ValueTask DisposeAsync()
    {
        await _gate.WaitAsync();
        try
        {
            _disposed = true;
            if (_idle is { } idle)
            {
                _idle = null;
                await idle.DisposeAsync();
            }

            await QuitAsync();
        }
        finally
        {
            _gate.Release();
            _gate.Dispose();
        }
    }
}
