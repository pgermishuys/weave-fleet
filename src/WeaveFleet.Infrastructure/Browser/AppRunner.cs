using System.Collections.Concurrent;
using System.Diagnostics;
using System.Net;
using System.Net.Sockets;
using System.Text.RegularExpressions;
using Microsoft.Extensions.Logging;
using WeaveFleet.Application.Browser;
using WeaveFleet.Application.Canvases;
using WeaveFleet.Application.Configuration;
using WeaveFleet.Application.Terminals;

namespace WeaveFleet.Infrastructure.Browser;

/// <summary>
/// Runs session commands (dev servers) as child processes of Fleet, and works out which page they serve:
/// first from the local addresses they print, then from the ports their process tree listens on. Every run
/// is killed, with its whole tree, when Fleet shuts down. Starts, restarts and stops take turns, so the caps
/// in <see cref="BrowserOptions"/> hold.
/// </summary>
public sealed partial class AppRunner(FleetOptions options, ILogger<AppRunner> logger) : IAppRunner, ISessionAppCleanup, IDisposable
{
    private const int MaxLogLines = 2000;
    private static readonly TimeSpan PollInterval = TimeSpan.FromMilliseconds(500);

    /// <summary>How long a port has to be open before Fleet tries it without a printed address to go on.</summary>
    private static readonly TimeSpan PortGrace = TimeSpan.FromSeconds(3);

    /// <summary>How long Fleet holds out for something that looks like a page before it takes any answer (an API with nothing at /).</summary>
    private static readonly TimeSpan LenientAfter = TimeSpan.FromSeconds(15);

    /// <summary>
    /// Makes the run the kernel's first choice when memory runs out, ahead of Fleet and the agent's harness.
    /// Children inherit it. Raising your own score needs no privilege; on failure the command runs anyway.
    /// </summary>
    internal const string LinuxOomPrefix = "{ echo 1000 > /proc/self/oom_score_adj; } 2>/dev/null; ";

    private static readonly Action<ILogger, string, string, string, Exception?> LogStarted =
        LoggerMessage.Define<string, string, string>(LogLevel.Information, new EventId(1, "AppStarted"),
            "Started app {AppId}: {Command} in {Directory}");

    private static readonly Action<ILogger, string, string, Exception?> LogReady =
        LoggerMessage.Define<string, string>(LogLevel.Information, new EventId(2, "AppReady"),
            "App {AppId} serves {Url}");

    private static readonly Action<ILogger, string, int, Exception?> LogExited =
        LoggerMessage.Define<string, int>(LogLevel.Information, new EventId(3, "AppExited"),
            "App {AppId} exited with code {ExitCode}");

    private static readonly Action<ILogger, string, Exception?> LogMonitorFailed =
        LoggerMessage.Define<string>(LogLevel.Warning, new EventId(4, "AppMonitorFailed"),
            "Watching app {AppId} for its page failed; trying again");

    private static readonly Action<ILogger, Exception?> LogChangedHandlerFailed =
        LoggerMessage.Define(LogLevel.Warning, new EventId(5, "AppChangedHandlerFailed"),
            "A handler of app changes threw");

    private static readonly Action<ILogger, int, Exception?> LogLeftoverKilled =
        LoggerMessage.Define<int>(LogLevel.Information, new EventId(6, "AppLeftoverKilled"),
            "Killed process {Pid}, left running by a previous Fleet");

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
    private readonly SemaphoreSlim _gate = new(1, 1);

    public event Action<AppRunChange>? Changed;

    public async Task<AppStartOutcome> StartAsync(AppRunRequest request)
    {
        await _gate.WaitAsync();
        try
        {
            if (CapRefusal(request.SessionId, request.Id) is { } refusal)
                return new AppStartOutcome(null, refusal);

            if (_runs.TryGetValue(request.Id, out var known))
            {
                // Quietly: the old process's exit is part of the restart, not news.
                known.MarkStopped();
                await KillAsync(known);
                known.AddLine("── restarted by Fleet ──");
                Launch(known, AppChangeReason.Restarted);
                return new AppStartOutcome(known.Snapshot(), null);
            }

            var run = new Run(request.Id, request.SessionId, request.UserId, request.Command, request.Directory, PortFor(request.Port));
            _runs[run.Id] = run;
            Launch(run, AppChangeReason.Started);
            return new AppStartOutcome(run.Snapshot(), null);
        }
        finally
        {
            _gate.Release();
        }
    }

    public Task<AppStartOutcome> RestartAsync(string appId)
        => _runs.TryGetValue(appId, out var run)
            ? StartAsync(new AppRunRequest(run.Id, run.SessionId, run.UserId, run.Directory, run.Command, run.Port))
            : Task.FromResult(new AppStartOutcome(null, $"No app {appId}."));

    public AppRunSnapshot? Find(string appId) => _runs.TryGetValue(appId, out var run) ? run.Snapshot() : null;

    public AppRunSnapshot? FindActive(string sessionId, string command)
        => _runs.Values
            .Select(run => run.Snapshot())
            .Where(run => run.SessionId == sessionId && run.Command == command && run.IsLive)
            .OrderByDescending(run => run.StartedAt)
            .FirstOrDefault();

    public async Task<AppReadiness> WaitUntilReadyAsync(string appId, TimeSpan timeout, CancellationToken ct = default)
    {
        if (!_runs.TryGetValue(appId, out var run))
            return new AppReadiness(null, $"No app {appId}.");

        var deadline = DateTimeOffset.UtcNow + timeout;
        while (DateTimeOffset.UtcNow < deadline)
        {
            var snapshot = run.Snapshot();
            if (snapshot.Url is not null && snapshot.Status == AppRunStatus.Running)
                return new AppReadiness(snapshot.Url, null);
            if (snapshot.Status == AppRunStatus.Exited)
                return new AppReadiness(null, $"`{run.Command}` exited with code {snapshot.ExitCode} before serving a page.");
            if (snapshot.Status == AppRunStatus.Stopped)
                return new AppReadiness(null, $"`{run.Command}` was stopped before it served a page.");

            await Task.Delay(250, ct);
        }

        return new AppReadiness(null, $"No page answered within {timeout.TotalMinutes:0} minutes. `{run.Command}` is still running ({run.Id}).");
    }

    public IReadOnlyList<string> Logs(string appId, int maxLines)
        => _runs.TryGetValue(appId, out var run) ? run.Tail(maxLines) : [];

    public AppOutput Output(string appId, long after)
        => _runs.TryGetValue(appId, out var run) ? run.Since(after) : new AppOutput([], 0);

    public async Task<bool> StopAsync(string appId)
    {
        if (!_runs.TryGetValue(appId, out var run))
            return false;

        await _gate.WaitAsync();
        try
        {
            var stopped = run.MarkStopped();
            await KillAsync(run);
            if (stopped)
                Raise(run, AppChangeReason.Stopped);
            return true;
        }
        finally
        {
            _gate.Release();
        }
    }

    public async Task StopSessionAppsAsync(string sessionId, CancellationToken ct = default)
    {
        foreach (var run in _runs.Values.Where(run => run.SessionId == sessionId).ToList())
            await StopAsync(run.Id);
    }

    public bool KillLeftover(int pid, DateTimeOffset startedAt)
    {
        try
        {
            using var process = Process.GetProcessById(pid);
            if (Math.Abs((process.StartTime.ToUniversalTime() - startedAt.UtcDateTime).TotalSeconds) > 2)
                return false;

            process.Kill(entireProcessTree: true);
            LogLeftoverKilled(logger, pid, null);
            return true;
        }
        catch (Exception ex) when (ex is ArgumentException or InvalidOperationException or System.ComponentModel.Win32Exception or NotSupportedException)
        {
            // Not running any more, or not ours to see.
            return false;
        }
    }

    public void Dispose()
    {
        foreach (var run in _runs.Values)
            run.Kill();
        _gate.Dispose();
    }

    /// <summary>A reason not to start (or restart) <paramref name="appId"/>, or null. Runs already live don't count against themselves.</summary>
    private string? CapRefusal(string sessionId, string appId)
    {
        var live = _runs.Values.Where(run => run.Id != appId).Select(run => run.Snapshot()).Where(run => run.IsLive).ToList();
        var inSession = live.Where(run => run.SessionId == sessionId).ToList();

        if (inSession.Count >= options.Browser.MaxAppsPerSession)
            return $"This session already runs {inSession.Count} apps, its limit: {Describe(inSession)}. Stop one from its Browser tab first, or restart one of these instead.";
        if (live.Count >= options.Browser.MaxApps)
            return $"Fleet already runs {live.Count} apps across sessions, its limit: {Describe(live)}. Stop one first.";
        return null;

        static string Describe(IEnumerable<AppRunSnapshot> runs) => string.Join(", ", runs.Select(run => $"`{run.Command}` ({run.Id})"));
    }

    private void Launch(Run run, AppChangeReason reason)
    {
        var script = OperatingSystem.IsLinux() ? LinuxOomPrefix + run.Command : run.Command;
        var shell = OperatingSystem.IsWindows()
            ? new ProcessStartInfo("cmd.exe") { ArgumentList = { "/c", script } }
            : new ProcessStartInfo("/bin/sh") { ArgumentList = { "-c", script } };
        shell.WorkingDirectory = run.Directory;
        shell.UseShellExecute = false;
        shell.CreateNoWindow = true;
        shell.RedirectStandardInput = true;
        shell.RedirectStandardOutput = true;
        shell.RedirectStandardError = true;

        // Fleet's own settings (secrets included) and its ASP.NET variables stay with Fleet: an app that kept
        // ASPNETCORE_URLS would try to take Fleet's port.
        foreach (var key in shell.Environment.Keys.Where(TerminalEnvironment.IsFleetOwned).ToList())
            shell.Environment.Remove(key);

        // A free port for servers that read PORT (Bun, Express, Next), the same one on every restart so the
        // page keeps its address. The rest pick their own; Fleet finds them.
        var port = run.Port.ToString(System.Globalization.CultureInfo.InvariantCulture);
        shell.Environment["PORT"] = port;
        // ASP.NET ignores PORT, and the launch profile overwrites ASPNETCORE_URLS; DOTNET_URLS wins over both.
        if (DotnetUrlsApplies(run.Command, run.Directory))
            shell.Environment["DOTNET_URLS"] = $"http://localhost:{port}";
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
            if (run.MarkExited(generation, -1))
                Raise(run, AppChangeReason.Exited);
            return;
        }

        run.SetProcess(generation, process.Id, StartTimeOf(process));
        process.BeginOutputReadLine();
        process.BeginErrorReadLine();
        process.StandardInput.Close();
        LogStarted(logger, run.Id, run.Command, run.Directory, null);
        Raise(run, reason);
        // A command that ends at once may have exited already; its exit waited for the start to be told first.
        if (run.AnnounceStart(generation))
            Raise(run, AppChangeReason.Exited);

        _ = MonitorAsync(run, generation, process.Id);
    }

    private static void OnLine(Run run, int generation, string? line)
    {
        if (line is null || !run.IsGeneration(generation))
            return;

        var clean = AnsiEscape().Replace(line, string.Empty);
        run.AddLine(clean);
        if (HintFor(clean) is { } hint && run.TryGiveHint(generation, hint))
            run.AddLine(hint);

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
        {
            LogExited(logger, run.Id, code, null);
            if (run.ExitCanBeAnnounced(generation))
                Raise(run, AppChangeReason.Exited);
        }
    }

    /// <summary>Keeps the ports current and looks for the page until one answers, then keeps the ports current.</summary>
    private async Task MonitorAsync(Run run, int generation, int pid)
    {
        DateTimeOffset? firstPortAt = null;
        while (run.IsGeneration(generation) && run.Snapshot().IsLive)
        {
            try
            {
                var ports = LinuxListeningPorts.ForProcessTree(pid);
                run.SetPorts(ports);
                if (ports.App.Count > 0)
                    firstPortAt ??= DateTimeOffset.UtcNow;

                if (run.Url is null)
                {
                    var listeningFor = firstPortAt is { } at ? DateTimeOffset.UtcNow - at : TimeSpan.Zero;
                    var url = await FindPageAsync(run, ports.App, portsAllowed: listeningFor >= PortGrace, lenient: listeningFor >= LenientAfter);
                    if (url is not null && run.SetUrl(generation, url))
                    {
                        LogReady(logger, run.Id, url, null);
                        Raise(run, AppChangeReason.Ready);
                    }
                }
            }
            catch (Exception ex)
            {
                LogMonitorFailed(logger, run.Id, ex);
            }

            await Task.Delay(run.Url is null ? PollInterval : PollInterval * 4);
        }
    }

    private void Raise(Run run, AppChangeReason reason)
    {
        var handlers = Changed;
        if (handlers is null)
            return;

        try
        {
            handlers(new AppRunChange(run.Snapshot(), reason));
        }
        catch (Exception ex)
        {
            LogChangedHandlerFailed(logger, ex);
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

    /// <summary>
    /// Whether to give the command its port through <c>DOTNET_URLS</c>: <c>dotnet run</c> or <c>dotnet watch</c>
    /// of one project that doesn't choose its own URLs. Not for an Aspire AppHost, which would pass it to every
    /// service it starts, and they'd all try to take the one port.
    /// </summary>
    internal static bool DotnetUrlsApplies(string command, string directory)
    {
        var match = DotnetRunCommand().Match(command);
        if (!match.Success || command.Contains("--urls", StringComparison.OrdinalIgnoreCase))
            return false;

        try
        {
            var project = ProjectOption().Match(command) is { Success: true } option
                ? Path.GetFullPath(option.Groups["path"].Value.Trim('"', '\''), directory)
                : directory;
            string[] files = File.Exists(project)
                ? [project]
                : Directory.Exists(project) ? Directory.GetFiles(project, "*.csproj") : [];
            return files.Length == 1 && !File.ReadAllText(files[0]).Contains("Aspire.AppHost", StringComparison.OrdinalIgnoreCase);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or ArgumentException or NotSupportedException)
        {
            return false;
        }
    }

    /// <summary>A line Fleet adds after output it recognises as a machine limit, with what to do about it.</summary>
    internal static string? HintFor(string line)
    {
        if (line.Contains("inotify instances", StringComparison.OrdinalIgnoreCase))
        {
            return "── Fleet: this machine ran out of inotify instances, which file watchers such as dotnet watch need. "
                + "Stop another app or watcher, or raise the limit: sudo sysctl fs.inotify.max_user_instances=512 ──";
        }
        if (line.Contains("ENOSPC", StringComparison.Ordinal) && line.Contains("file watchers", StringComparison.OrdinalIgnoreCase))
        {
            return "── Fleet: this machine ran out of inotify watches for file watchers. "
                + "Stop another app or watcher, or raise the limit: sudo sysctl fs.inotify.max_user_watches=524288 ──";
        }
        return null;
    }

    private static int PortFor(int? stored)
    {
        if (stored is { } port and > 0 && IsFree(port))
            return port;

        using var listener = new TcpListener(IPAddress.Loopback, 0);
        listener.Start();
        return ((IPEndPoint)listener.LocalEndpoint).Port;
    }

    private static bool IsFree(int port)
    {
        try
        {
            using var listener = new TcpListener(IPAddress.Loopback, port);
            listener.Start();
            return true;
        }
        catch (SocketException)
        {
            return false;
        }
    }

    private static DateTimeOffset? StartTimeOf(Process process)
    {
        try
        {
            return new DateTimeOffset(process.StartTime.ToUniversalTime(), TimeSpan.Zero);
        }
        catch (Exception ex) when (ex is InvalidOperationException or System.ComponentModel.Win32Exception or NotSupportedException)
        {
            return null;
        }
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

    [GeneratedRegex(@"^\s*dotnet\s+(?:watch|run)\b", RegexOptions.IgnoreCase)]
    private static partial Regex DotnetRunCommand();

    [GeneratedRegex(@"(?:--project|-p)(?:\s+|=)(?<path>""[^""]+""|'[^']+'|\S+)")]
    private static partial Regex ProjectOption();

    /// <summary>One app. A restart starts a new generation, so events from the old process are ignored.</summary>
    private sealed class Run(string id, string sessionId, string userId, string command, string directory, int port)
    {
        private readonly Lock _lock = new();
        private readonly Queue<string> _lines = new();
        private readonly List<(string Url, bool Announced)> _printedUrls = [];
        private readonly HashSet<string> _hintsGiven = [];
        private long _droppedLines;
        private Process? _process;
        private int _generation;
        private TreePorts _ports = TreePorts.None;
        private int? _exitCode;
        private int? _pid;
        private DateTimeOffset? _pidStartedAt;
        private bool _startAnnounced;
        private bool _exitWaiting;

        public string Id { get; } = id;
        public string SessionId { get; } = sessionId;
        public string UserId { get; } = userId;
        public string Command { get; } = command;
        public string Directory { get; } = directory;
        public int Port { get; } = port;
        public DateTimeOffset StartedAt { get; private set; }
        public AppRunStatus Status { get; private set; } = AppRunStatus.Starting;
        public string? Url { get; private set; }

        public int BeginGeneration(Process process)
        {
            lock (_lock)
            {
                _process = process;
                _generation++;
                _printedUrls.Clear();
                _hintsGiven.Clear();
                _ports = TreePorts.None;
                _exitCode = null;
                _pid = null;
                _pidStartedAt = null;
                _startAnnounced = false;
                _exitWaiting = false;
                Url = null;
                Status = AppRunStatus.Starting;
                StartedAt = DateTimeOffset.UtcNow;
                return _generation;
            }
        }

        /// <summary>Records that the start was told. Returns whether an exit came first and is waiting to be told.</summary>
        public bool AnnounceStart(int generation)
        {
            lock (_lock)
            {
                if (_generation != generation)
                    return false;
                _startAnnounced = true;
                var waiting = _exitWaiting;
                _exitWaiting = false;
                return waiting;
            }
        }

        /// <summary>Whether the exit can be told now; if the start hasn't been, the exit waits for <see cref="AnnounceStart"/>.</summary>
        public bool ExitCanBeAnnounced(int generation)
        {
            lock (_lock)
            {
                if (_generation != generation)
                    return false;
                if (!_startAnnounced)
                    _exitWaiting = true;
                return _startAnnounced;
            }
        }

        public bool IsGeneration(int generation)
        {
            lock (_lock)
                return _generation == generation;
        }

        public void SetProcess(int generation, int pid, DateTimeOffset? startedAt)
        {
            lock (_lock)
            {
                if (_generation != generation)
                    return;
                _pid = pid;
                _pidStartedAt = startedAt;
            }
        }

        public void AddLine(string line)
        {
            lock (_lock)
            {
                _lines.Enqueue(line);
                while (_lines.Count > MaxLogLines)
                {
                    _lines.Dequeue();
                    _droppedLines++;
                }
            }
        }

        /// <summary>Whether this generation hasn't had <paramref name="hint"/> yet; each hint is given once.</summary>
        public bool TryGiveHint(int generation, string hint)
        {
            lock (_lock)
                return _generation == generation && _hintsGiven.Add(hint);
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

        public void SetPorts(TreePorts ports)
        {
            lock (_lock)
                _ports = ports;
        }

        public bool SetUrl(int generation, string url)
        {
            lock (_lock)
            {
                if (_generation != generation || Status != AppRunStatus.Starting)
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
                if (_generation != generation || Status is AppRunStatus.Exited or AppRunStatus.Stopped)
                    return false;
                Status = AppRunStatus.Exited;
                _exitCode = code;
                _ports = TreePorts.None;
                _pid = null;
                _pidStartedAt = null;
                return true;
            }
        }

        /// <summary>Marks a live run stopped before it's killed, so its exit isn't reported as its own. Returns whether it was live.</summary>
        public bool MarkStopped()
        {
            lock (_lock)
            {
                if (Status is AppRunStatus.Exited or AppRunStatus.Stopped)
                    return false;
                Status = AppRunStatus.Stopped;
                _exitCode = null;
                _ports = TreePorts.None;
                _pid = null;
                _pidStartedAt = null;
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

        public AppOutput Since(long after)
        {
            lock (_lock)
            {
                var next = _droppedLines + _lines.Count;
                var skip = Math.Clamp(after - _droppedLines, 0, _lines.Count);
                return new AppOutput(_lines.Skip((int)skip).ToList(), next);
            }
        }

        public AppRunSnapshot Snapshot()
        {
            lock (_lock)
            {
                return new AppRunSnapshot(
                    Id, SessionId, UserId, Command, Directory, Port, Status, _exitCode, Url,
                    _ports.App, _ports.Helper, [.. _printedUrls.Select(p => p.Url)], _pid, _pidStartedAt, StartedAt);
            }
        }
    }
}
