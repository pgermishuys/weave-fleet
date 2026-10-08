using System.Diagnostics;
using System.Runtime.InteropServices;
using Microsoft.Extensions.Logging;
using WeaveFleet.Application.Configuration;
using WeaveFleet.Infrastructure.Harnesses;

namespace WeaveFleet.Infrastructure.Browser;

/// <summary>
/// The one headless Chrome (or Edge, or Chromium) Fleet drives over the DevTools protocol, shared by screenshots and
/// the tabs agents use. Starting it costs about half a second, so it's kept running while anyone holds a
/// <see cref="ChromeLease"/>, and quits <see cref="IdleTimeout"/> after the last one is let go to give its memory back:
/// this machine may be running the app being shot as well.
/// <para>
/// The browser goes where the harnesses go (<see cref="ProcessGroupHelper"/>), so it doesn't outlive Fleet: a Fleet
/// that is killed, crashes or restarts to update never gets to quit it, and a headless browser nobody drives stays
/// running for good. On Windows that's a Job Object, which also takes the processes the browser started.
/// </para>
/// </summary>
public sealed class ChromeHost(FleetOptions options, ILogger<ChromeHost> logger) : IAsyncDisposable
{
    /// <summary>How long the browser waits, once nobody holds it, before quitting.</summary>
    public static readonly TimeSpan IdleTimeout = TimeSpan.FromMinutes(2);

    /// <summary>How long a browser gets to open its DevTools port before Fleet tries another.</summary>
    internal TimeSpan LaunchTimeout { get; init; } = TimeSpan.FromSeconds(20);

    private static readonly Action<ILogger, string, Exception?> LogBrowser =
        LoggerMessage.Define<string>(LogLevel.Information, new EventId(1, "ChromeBrowser"), "Fleet's headless browser is {Path}");

    private static readonly Action<ILogger, string, Exception?> LogSandbox =
        LoggerMessage.Define<string>(LogLevel.Warning, new EventId(4, "ChromeNoSandbox"),
            "{Path} couldn't open its sandbox, so Fleet runs it with --no-sandbox");

    private static readonly Action<ILogger, string, double, Exception?> LogStalled =
        LoggerMessage.Define<string, double>(LogLevel.Warning, new EventId(5, "ChromeStalled"),
            "{Path} was still running but hadn't opened its DevTools port after {Seconds} s, so Fleet starts it again");

    private readonly SemaphoreSlim _gate = new(1, 1);
    private Process? _browser;
    private SafeHandle? _job;
    private CdpConnection? _cdp;
    private string? _profile;
    private Timer? _idle;
    private int _holders;
    private bool _sandbox = true;
    private bool _disposed;

    /// <summary>
    /// The running browser, started if it isn't, held until the lease is disposed. When there's no browser to drive,
    /// <see cref="ChromeLease.Problem"/> says why in words the agent can act on.
    /// </summary>
    internal async Task<ChromeLease> AcquireAsync(CancellationToken ct)
    {
        await _gate.WaitAsync(ct);
        try
        {
            if (_disposed)
                return new ChromeLease(null, null, "Fleet is shutting down.");

            var (connection, problem) = await ConnectedAsync(ct);
            if (connection is null)
                return new ChromeLease(null, null, problem);

            Interlocked.Increment(ref _holders);
            _idle?.Change(Timeout.InfiniteTimeSpan, Timeout.InfiniteTimeSpan);
            return new ChromeLease(this, connection, null);
        }
        finally
        {
            _gate.Release();
        }
    }

    /// <summary>
    /// Quits the browser behind <paramref name="broken"/> when it's still the current one: a browser that died or
    /// stopped answering mustn't poison the next call. Everyone holding it loses their tabs.
    /// </summary>
    internal async Task ResetAsync(CdpConnection broken)
    {
        await _gate.WaitAsync();
        try
        {
            if (ReferenceEquals(_cdp, broken))
                await QuitAsync();
        }
        finally
        {
            _gate.Release();
        }
    }

    private void Release()
    {
        if (Interlocked.Decrement(ref _holders) == 0)
            _idle?.Change(IdleTimeout, Timeout.InfiniteTimeSpan);
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

        // A browser that's running but silent has stalled on its way up; on a busy machine (CI running every test
        // project at once) that happens now and then, and a second start with a fresh profile comes up. This goes
        // first, so the sandbox check below looks at whichever start died.
        if (launched.Problem is not null && launched.Stalled)
        {
            LogStalled(logger, path, LaunchTimeout.TotalSeconds, null);
            launched = await LaunchAsync(path, _sandbox, ct);
        }

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

    private async Task<(bool Started, string? Problem, bool Sandbox, bool Stalled)> LaunchAsync(string path, bool sandbox, CancellationToken ct)
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
            return (false, $"Fleet couldn't start {path}: {error.Message}", false, false);
        }

        if (_browser is null)
            return (false, $"Fleet couldn't start {path}.", false, false);

        _job = ProcessGroupHelper.AssignToProcessGroup(_browser, logger);

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
            var stalled = !_browser.HasExited;
            await QuitAsync();
            var what = stalled
                ? $"was still running after {LaunchTimeout.TotalSeconds:0} s without opening its DevTools port"
                : "exited before opening its DevTools port";
            return (false, $"{path} started but {what}, so Fleet can't drive it.{complaints}", complaints.MentionsSandbox, stalled);
        }

        try
        {
            _cdp = await CdpConnection.ConnectAsync(endpoint, ct);
        }
        catch (Exception error) when (error is not OperationCanceledException)
        {
            await QuitAsync();
            return (false, $"Fleet couldn't connect to the browser it started: {error.Message}", false, false);
        }

        return (true, null, false, false);
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
    private async Task<Uri?> DevToolsUrlAsync(string profile, Process browser, CancellationToken ct)
    {
        var file = Path.Combine(profile, "DevToolsActivePort");
        var deadline = DateTimeOffset.UtcNow + LaunchTimeout;
        while (DateTimeOffset.UtcNow < deadline)
        {
            if (browser.HasExited)
                return null;

            if (await ReadDevToolsUrlAsync(file, ct) is { } url)
                return url;

            await Task.Delay(25, ct);
        }

        // Once more past the deadline: on a busy machine this loop can sleep through it while Chrome is already up.
        return await ReadDevToolsUrlAsync(file, ct);
    }

    private static async Task<Uri?> ReadDevToolsUrlAsync(string file, CancellationToken ct)
    {
        if (!File.Exists(file))
            return null;

        try
        {
            var lines = await File.ReadAllLinesAsync(file, ct);
            return lines.Length >= 2 && int.TryParse(lines[0], out var port)
                ? new Uri($"ws://127.0.0.1:{port}{lines[1]}")
                : null;
        }
        catch (IOException)
        {
            // Chrome is still writing it.
            return null;
        }
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
            // Someone took a lease between the timer firing and here.
            if (Volatile.Read(ref _holders) == 0)
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

        // Closing the job kills whatever the browser left running, such as its crash reporter.
        if (_job is { } job)
        {
            _job = null;
            job.Dispose();
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
            if (_disposed)
                return;
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
        }
    }

    /// <summary>
    /// Holds the browser (<see cref="Connection"/>) running until disposed, or says why there isn't one
    /// (<see cref="Problem"/>). Dispose it once; a second dispose does nothing.
    /// </summary>
    internal sealed class ChromeLease(ChromeHost? host, CdpConnection? connection, string? problem) : IDisposable
    {
        private ChromeHost? _host = host;

        public CdpConnection? Connection { get; } = connection;
        public string? Problem { get; } = problem;

        public void Dispose() => Interlocked.Exchange(ref _host, null)?.Release();
    }
}
