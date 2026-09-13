using System.Collections.Concurrent;
using System.Diagnostics;
using System.Net;
using System.Net.Sockets;
using System.Text.RegularExpressions;
using Microsoft.Extensions.Logging;
using WeaveFleet.Application.Browser;
using WeaveFleet.Application.Canvases;

namespace WeaveFleet.Infrastructure.Browser;

/// <summary>
/// Runs session commands (dev servers) as child processes of Fleet, and works out which page they serve:
/// first from the local addresses they print, then from the ports their process tree listens on. Every run
/// is killed, with its whole tree, when Fleet shuts down.
/// </summary>
public sealed partial class AppRunner(ILogger<AppRunner> logger) : IAppRunner, IDisposable
{
    private const int MaxLogLines = 2000;
    private static readonly TimeSpan PollInterval = TimeSpan.FromMilliseconds(500);

    /// <summary>How long a port has to be open before Fleet tries it without a printed address to go on.</summary>
    private static readonly TimeSpan PortGrace = TimeSpan.FromSeconds(3);

    /// <summary>How long Fleet holds out for something that looks like a page before it takes any answer (an API with nothing at /).</summary>
    private static readonly TimeSpan LenientAfter = TimeSpan.FromSeconds(15);

    private static readonly Action<ILogger, string, string, string, Exception?> LogStarted =
        LoggerMessage.Define<string, string, string>(LogLevel.Information, new EventId(1, "AppStarted"),
            "Started app {AppId}: {Command} in {Directory}");

    private static readonly Action<ILogger, string, string, Exception?> LogReady =
        LoggerMessage.Define<string, string>(LogLevel.Information, new EventId(2, "AppReady"),
            "App {AppId} serves {Url}");

    private static readonly Action<ILogger, string, int, Exception?> LogExited =
        LoggerMessage.Define<string, int>(LogLevel.Information, new EventId(3, "AppExited"),
            "App {AppId} exited with code {ExitCode}");

    // Probes follow nothing and trust any certificate: the target is a dev server on this machine, usually
    // with a self-signed development certificate.
#pragma warning disable CA5359
    private static readonly HttpClient Probe = new(new SocketsHttpHandler
    {
        AllowAutoRedirect = false,
        UseCookies = false,
        ConnectTimeout = TimeSpan.FromSeconds(1),
        SslOptions = { RemoteCertificateValidationCallback = (_, _, _, _) => true },
    })
    {
        Timeout = TimeSpan.FromSeconds(3),
    };
#pragma warning restore CA5359

    private readonly ConcurrentDictionary<string, Run> _runs = new(StringComparer.Ordinal);

    public AppRunSnapshot Start(string sessionId, string directory, string command)
    {
        var run = new Run("app_" + Ulid.NewUlid(), sessionId, command, directory);
        _runs[run.Id] = run;
        Launch(run);
        return run.Snapshot();
    }

    public AppRunSnapshot? Find(string appId) => _runs.TryGetValue(appId, out var run) ? run.Snapshot() : null;

    public AppRunSnapshot? FindActive(string sessionId, string command)
        => _runs.Values
            .Where(run => run.SessionId == sessionId && run.Command == command && run.Status != AppRunStatus.Exited)
            .OrderByDescending(run => run.StartedAt)
            .Select(run => run.Snapshot())
            .FirstOrDefault();

    public async Task<AppReadiness> WaitUntilReadyAsync(string appId, TimeSpan timeout, CancellationToken ct = default)
    {
        if (!_runs.TryGetValue(appId, out var run))
            return new AppReadiness(null, $"No app {appId}.");

        var deadline = DateTimeOffset.UtcNow + timeout;
        while (DateTimeOffset.UtcNow < deadline)
        {
            var snapshot = run.Snapshot();
            if (snapshot.Url is not null)
                return new AppReadiness(snapshot.Url, null);
            if (snapshot.Status == AppRunStatus.Exited)
                return new AppReadiness(null, $"`{run.Command}` exited with code {snapshot.ExitCode} before serving a page.");

            await Task.Delay(250, ct);
        }

        return new AppReadiness(null, $"No page answered within {timeout.TotalMinutes:0} minutes. `{run.Command}` is still running ({run.Id}).");
    }

    public IReadOnlyList<string> Logs(string appId, int maxLines)
        => _runs.TryGetValue(appId, out var run) ? run.Tail(maxLines) : [];

    public async Task<bool> StopAsync(string appId)
    {
        if (!_runs.TryGetValue(appId, out var run))
            return false;

        await KillAsync(run);
        return true;
    }

    public async Task<AppRunSnapshot?> RestartAsync(string appId)
    {
        if (!_runs.TryGetValue(appId, out var run))
            return null;

        await KillAsync(run);
        run.AddLine("── restarted by Fleet ──");
        Launch(run);
        return run.Snapshot();
    }

    public void Dispose()
    {
        foreach (var run in _runs.Values)
            run.Kill();
    }

    private void Launch(Run run)
    {
        var shell = OperatingSystem.IsWindows()
            ? new ProcessStartInfo("cmd.exe") { ArgumentList = { "/c", run.Command } }
            : new ProcessStartInfo("/bin/sh") { ArgumentList = { "-c", run.Command } };
        shell.WorkingDirectory = run.Directory;
        shell.UseShellExecute = false;
        shell.CreateNoWindow = true;
        shell.RedirectStandardInput = true;
        shell.RedirectStandardOutput = true;
        shell.RedirectStandardError = true;

        // A free port for servers that read PORT (Bun, Express, Next), the same one on every restart so the
        // page keeps its address. The rest pick their own; Fleet finds them.
        shell.Environment["PORT"] = run.Port.ToString(System.Globalization.CultureInfo.InvariantCulture);
        // Nobody is at a terminal: don't open a browser, don't colour, don't stop to ask.
        shell.Environment["BROWSER"] = "none";
        shell.Environment["NO_COLOR"] = "1";
        shell.Environment["DOTNET_WATCH_SUPPRESS_LAUNCH_BROWSER"] = "1";
        shell.Environment["DOTNET_WATCH_RESTART_ON_RUDE_EDIT"] = "1";

        var process = new Process { StartInfo = shell, EnableRaisingEvents = true };
        var generation = run.BeginGeneration(process);
        process.OutputDataReceived += (_, e) => OnLine(run, generation, e.Data);
        process.ErrorDataReceived += (_, e) => OnLine(run, generation, e.Data);
        process.Exited += (_, _) => OnExited(run, generation, process);

        try
        {
            process.Start();
        }
        catch (Exception ex) when (ex is System.ComponentModel.Win32Exception or InvalidOperationException)
        {
            run.AddLine($"Fleet couldn't start the command: {ex.Message}");
            run.MarkExited(generation, -1);
            return;
        }

        process.BeginOutputReadLine();
        process.BeginErrorReadLine();
        process.StandardInput.Close();
        LogStarted(logger, run.Id, run.Command, run.Directory, null);

        _ = MonitorAsync(run, generation, process.Id);
    }

    private static void OnLine(Run run, int generation, string? line)
    {
        if (line is null || !run.IsGeneration(generation))
            return;

        var clean = AnsiEscape().Replace(line, string.Empty);
        run.AddLine(clean);
        var announced = ListeningLine().IsMatch(clean);
        foreach (Match match in PrintedUrl().Matches(clean))
        {
            if (NormalizePrintedUrl(match.Value) is { } url)
                run.AddPrintedUrl(url, announced);
        }
    }

    private void OnExited(Run run, int generation, Process process)
    {
        int code;
        try
        {
            code = process.ExitCode;
        }
        catch (InvalidOperationException)
        {
            code = -1;
        }

        if (run.MarkExited(generation, code))
            LogExited(logger, run.Id, code, null);
    }

    /// <summary>Keeps the ports current and looks for the page until one answers, then keeps the ports current.</summary>
    private async Task MonitorAsync(Run run, int generation, int pid)
    {
        DateTimeOffset? firstPortAt = null;
        while (run.IsGeneration(generation) && run.Status != AppRunStatus.Exited)
        {
            var ports = LinuxListeningPorts.ForProcessTree(pid);
            run.SetPorts(ports);
            if (ports.Count > 0)
                firstPortAt ??= DateTimeOffset.UtcNow;

            if (run.Url is null)
            {
                var listeningFor = firstPortAt is { } at ? DateTimeOffset.UtcNow - at : TimeSpan.Zero;
                var url = await FindPageAsync(run, ports, portsAllowed: listeningFor >= PortGrace, lenient: listeningFor >= LenientAfter);
                if (url is not null && run.SetUrl(generation, url))
                    LogReady(logger, run.Id, url, null);
            }

            await Task.Delay(run.Url is null ? PollInterval : PollInterval * 4);
        }
    }

    /// <summary>
    /// Printed addresses come first, those on a "listening" line before the rest. A page has to look like one
    /// (a success, a redirect, or HTML): dev tools and child processes open other ports too, like a harness
    /// that answers 401. Bare ports are tried once they've been open a moment, and after
    /// <see cref="LenientAfter"/> any answer on a printed address will do.
    /// </summary>
    private static async Task<string?> FindPageAsync(Run run, IReadOnlyList<int> ports, bool portsAllowed, bool lenient)
    {
        // On Linux Fleet knows the ports, so a printed address only counts on one the app listens on, not,
        // say, a telemetry endpoint it sends to. Elsewhere every printed address is a candidate.
        var knowsPorts = OperatingSystem.IsLinux();
        if (knowsPorts && ports.Count == 0)
            return null;

        var printed = run.PrintedUrlsSnapshot().Where(url => !knowsPorts || ports.Contains(new Uri(url).Port)).ToList();
        foreach (var url in printed)
        {
            if (await AnswersAsync(url, strict: true))
                return url;
        }

        if (portsAllowed)
        {
            foreach (var port in ports)
            {
                if (printed.Any(p => new Uri(p).Port == port))
                    continue;
                var url = $"http://localhost:{port}/";
                if (await AnswersAsync(url, strict: true))
                    return url;
            }
        }

        if (lenient)
        {
            foreach (var url in printed)
            {
                if (await AnswersAsync(url, strict: false))
                    return url;
            }
        }

        return null;
    }

    private static async Task<bool> AnswersAsync(string url, bool strict)
    {
        try
        {
            using var response = await Probe.GetAsync(url, HttpCompletionOption.ResponseHeadersRead);
            if (!strict)
                return true;

            var status = (int)response.StatusCode;
            var html = response.Content.Headers.ContentType?.MediaType?.Contains("html", StringComparison.OrdinalIgnoreCase) == true;
            return status is >= 200 and < 400 || html;
        }
        catch (Exception ex) when (ex is HttpRequestException or TaskCanceledException)
        {
            return false;
        }
    }

    /// <summary><c>0.0.0.0</c> and <c>[::]</c> become <c>localhost</c>; anything not on this machine is dropped.</summary>
    internal static string? NormalizePrintedUrl(string printed)
    {
        var trimmed = printed.TrimEnd('.', ',', ';', ')', ']', '\'', '"');
        if (!Uri.TryCreate(trimmed, UriKind.Absolute, out var uri) || !LoopbackUrl.IsLoopbackHost(uri.Host))
            return null;

        var builder = new UriBuilder(uri);
        if (IPAddress.TryParse(uri.Host.Trim('[', ']'), out var address)
            && (address.Equals(IPAddress.Any) || address.Equals(IPAddress.IPv6Any)))
        {
            builder.Host = "localhost";
        }

        return builder.Uri.ToString();
    }

    private static int FreePort()
    {
        using var listener = new TcpListener(IPAddress.Loopback, 0);
        listener.Start();
        return ((IPEndPoint)listener.LocalEndpoint).Port;
    }

    private static async Task KillAsync(Run run)
    {
        var process = run.Kill();
        if (process is null)
            return;

        try
        {
            await process.WaitForExitAsync().WaitAsync(TimeSpan.FromSeconds(5));
        }
        catch (TimeoutException)
        {
            // Killed; the Exited handler marks it when the OS catches up.
        }
    }

    /// <summary>Lines where a server announces its own address: "Now listening on", "Local:", "running at", "Access … at".</summary>
    [GeneratedRegex(@"listen|local:|running (?:at|on)|serving|server (?:at|on|running)|available (?:at|on)|ready (?:at|on)|development server|access\b.*\bat\b", RegexOptions.IgnoreCase)]
    private static partial Regex ListeningLine();

    [GeneratedRegex(@"\x1B\[[0-9;?]*[ -/]*[@-~]")]
    private static partial Regex AnsiEscape();

    [GeneratedRegex(@"https?://(?:localhost|127\.0\.0\.1|0\.0\.0\.0|\[::1?\]|\[::\]|[A-Za-z0-9-]+\.localhost)(?::\d{1,5})?(?:/[^\s'""<>]*)?", RegexOptions.IgnoreCase)]
    private static partial Regex PrintedUrl();

    /// <summary>One app. A restart starts a new generation, so events from the old process are ignored.</summary>
    private sealed class Run(string id, string sessionId, string command, string directory)
    {
        private readonly Lock _lock = new();
        private readonly Queue<string> _lines = new();
        private readonly List<(string Url, bool Announced)> _printedUrls = [];
        private Process? _process;
        private int _generation;
        private IReadOnlyList<int> _ports = [];
        private int? _exitCode;

        public string Id { get; } = id;
        public string SessionId { get; } = sessionId;
        public string Command { get; } = command;
        public string Directory { get; } = directory;
        public int Port { get; } = FreePort();
        public DateTimeOffset StartedAt { get; private set; }
        public AppRunStatus Status { get; private set; }
        public string? Url { get; private set; }

        public int BeginGeneration(Process process)
        {
            lock (_lock)
            {
                _process = process;
                _generation++;
                _printedUrls.Clear();
                _ports = [];
                _exitCode = null;
                Url = null;
                Status = AppRunStatus.Starting;
                StartedAt = DateTimeOffset.UtcNow;
                return _generation;
            }
        }

        public bool IsGeneration(int generation)
        {
            lock (_lock)
                return _generation == generation;
        }

        public void AddLine(string line)
        {
            lock (_lock)
            {
                _lines.Enqueue(line);
                while (_lines.Count > MaxLogLines)
                    _lines.Dequeue();
            }
        }

        public void AddPrintedUrl(string url, bool announced)
        {
            lock (_lock)
            {
                var index = _printedUrls.FindIndex(printed => printed.Url == url);
                if (index < 0)
                    _printedUrls.Add((url, announced));
                else if (announced)
                    _printedUrls[index] = (url, true);
            }
        }

        /// <summary>Announced addresses first, each group in the order they were printed.</summary>
        public List<string> PrintedUrlsSnapshot()
        {
            lock (_lock)
                return [.. _printedUrls.Where(p => p.Announced).Concat(_printedUrls.Where(p => !p.Announced)).Select(p => p.Url)];
        }

        public void SetPorts(IReadOnlyList<int> ports)
        {
            lock (_lock)
                _ports = ports;
        }

        public bool SetUrl(int generation, string url)
        {
            lock (_lock)
            {
                if (_generation != generation || Status == AppRunStatus.Exited)
                    return false;
                Url = url;
                Status = AppRunStatus.Running;
                return true;
            }
        }

        public bool MarkExited(int generation, int code)
        {
            lock (_lock)
            {
                if (_generation != generation || Status == AppRunStatus.Exited)
                    return false;
                Status = AppRunStatus.Exited;
                _exitCode = code;
                _ports = [];
                return true;
            }
        }

        /// <summary>Kills the current process and its tree, and returns it so the caller can wait for it.</summary>
        public Process? Kill()
        {
            Process? process;
            lock (_lock)
                process = _process;

            if (process is null)
                return null;

            try
            {
                if (!process.HasExited)
                    process.Kill(entireProcessTree: true);
            }
            catch (InvalidOperationException)
            {
                // Already gone.
            }

            return process;
        }

        public List<string> Tail(int maxLines)
        {
            lock (_lock)
                return _lines.Skip(Math.Max(0, _lines.Count - maxLines)).ToList();
        }

        public AppRunSnapshot Snapshot()
        {
            lock (_lock)
                return new AppRunSnapshot(Id, SessionId, Command, Directory, Status, _exitCode, Url, _ports, [.. _printedUrls.Select(p => p.Url)], StartedAt);
        }
    }
}
