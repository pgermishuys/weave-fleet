using System.Diagnostics;
using System.Text;
using System.Text.RegularExpressions;
using Microsoft.Extensions.Logging;
using WeaveFleet.Infrastructure.Harnesses;

namespace WeaveFleet.Infrastructure.Harnesses.OpenCode;

/// <summary>Options for starting an <c>opencode serve</c> process.</summary>
internal sealed record OpenCodeProcessOptions
{
    public required int Port { get; init; }
    public required string Hostname { get; init; }
    public required string WorkingDirectory { get; init; }
    public required string Password { get; init; }
    public required string Username { get; init; }
    public IReadOnlyDictionary<string, string> EnvironmentVariables { get; init; }
        = new Dictionary<string, string>();
    public required TimeSpan StartupTimeout { get; init; }
}

/// <summary>Information about a successfully started <c>opencode serve</c> process.</summary>
internal sealed record OpenCodeProcessInfo
{
    public required int ProcessId { get; init; }
    public required int Port { get; init; }
    public required string Hostname { get; init; }
    public required Uri BaseUrl { get; init; }
}

/// <summary>
/// Encapsulates spawning and managing the lifecycle of an <c>opencode serve</c> child process.
/// </summary>
internal sealed class OpenCodeProcessManager : IAsyncDisposable
{
    private static readonly Regex ReadyPattern =
        new(@"opencode server listening on http://(?<host>[^:]+):(?<port>\d+)", RegexOptions.Compiled);

    private static readonly Action<ILogger, string, Exception?> LogProcessStarted =
        LoggerMessage.Define<string>(LogLevel.Information, new EventId(1, "ProcessStarted"),
            "opencode process started: {BaseUrl}");

    private static readonly Action<ILogger, string, Exception?> LogStderr =
        LoggerMessage.Define<string>(LogLevel.Warning, new EventId(2, "Stderr"),
            "opencode stderr: {Line}");

    private static readonly Action<ILogger, int, Exception?> LogProcessExited =
        LoggerMessage.Define<int>(LogLevel.Information, new EventId(3, "ProcessExited"),
            "opencode process exited with code {ExitCode}.");

    private static readonly Action<ILogger, int, Exception?> LogProcessExitedUnexpectedly =
        LoggerMessage.Define<int>(LogLevel.Error, new EventId(4, "UnexpectedExit"),
            "opencode process exited unexpectedly with code {ExitCode}.");

    private static readonly Action<ILogger, Exception?> LogForceKilled =
        LoggerMessage.Define(LogLevel.Warning, new EventId(5, "ForceKilled"),
            "opencode process did not exit within the timeout; force-killing.");

    private readonly ILogger<OpenCodeProcessManager> _logger;
    private Process? _process;
    private Uri? _baseUrl;
    private bool _started;
    private bool _disposed;
    private System.Runtime.InteropServices.SafeHandle? _jobObjectHandle;

    /// <summary>Fired when the process exits (expected or not).</summary>
    public event EventHandler<int>? ProcessExited;

    /// <summary>Initialises the manager. Call <see cref="StartAsync"/> to spawn the process.</summary>
    public OpenCodeProcessManager(ILogger<OpenCodeProcessManager> logger)
    {
        _logger = logger;
    }

    /// <summary><c>true</c> if the process has been started and has not yet exited.</summary>
    public bool IsRunning => _process is { HasExited: false };

    /// <summary>OS process ID, if the process has been started.</summary>
    public int? ProcessId => _process?.Id;

    /// <summary>Base URL of the running server, available after <see cref="StartAsync"/> returns.</summary>
    public Uri? BaseUrl => _baseUrl;

    /// <summary>
    /// Spawns <c>opencode serve</c> and waits for the ready signal.
    /// </summary>
    public async Task<OpenCodeProcessInfo> StartAsync(OpenCodeProcessOptions options, CancellationToken ct)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        if (_started)
        {
            throw new InvalidOperationException("Process manager has already started a process.");
        }

        var tcs = new TaskCompletionSource<OpenCodeProcessInfo>(TaskCreationOptions.RunContinuationsAsynchronously);
        var stderrLines = new StringBuilder();

        var psi = new ProcessStartInfo
        {
            FileName = ExecutableResolver.Resolve("opencode"),
            WorkingDirectory = options.WorkingDirectory,
            UseShellExecute = false,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            CreateNoWindow = true,
        };

        // Build argument list safely — avoids shell injection via string interpolation
        psi.ArgumentList.Add("serve");
        psi.ArgumentList.Add("--hostname");
        psi.ArgumentList.Add(options.Hostname);
        psi.ArgumentList.Add("--port");
        psi.ArgumentList.Add(options.Port.ToString(System.Globalization.CultureInfo.InvariantCulture));

        // Auth env vars
        psi.Environment["OPENCODE_SERVER_PASSWORD"] = options.Password;
        psi.Environment["OPENCODE_SERVER_USERNAME"] = options.Username;

        // Inline config: allow the question tool so the agent can ask structured
        // questions that are surfaced as interactive forms in the Fleet UI.
        psi.Environment["OPENCODE_CONFIG_CONTENT"] = """{"permission":{"question":"allow"}}""";

        // Additional caller-supplied env vars
        foreach (var (key, value) in options.EnvironmentVariables)
        {
            psi.Environment[key] = value;
        }

        _process = new Process { StartInfo = psi, EnableRaisingEvents = true };

        _process.OutputDataReceived += (_, e) =>
        {
            if (e.Data is null) return;

            var match = ReadyPattern.Match(e.Data);
            if (match.Success)
            {
                var host = match.Groups["host"].Value;
                var port = int.Parse(match.Groups["port"].Value, System.Globalization.CultureInfo.InvariantCulture);
                var baseUrl = new Uri($"http://{host}:{port}");

                _baseUrl = baseUrl;
                LogProcessStarted(_logger, baseUrl.ToString(), null);

                tcs.TrySetResult(new OpenCodeProcessInfo
                {
                    ProcessId = _process.Id,
                    Port = port,
                    Hostname = host,
                    BaseUrl = baseUrl,
                });
            }
        };

        _process.ErrorDataReceived += (_, e) =>
        {
            if (e.Data is null) return;
            stderrLines.AppendLine(e.Data);
            LogStderr(_logger, e.Data, null);
        };

        _process.Exited += (_, _) =>
        {
            int exitCode = _process.ExitCode;
            if (!tcs.Task.IsCompleted)
            {
                // Exited before ready signal
                string stderr = stderrLines.ToString();
                tcs.TrySetException(new InvalidOperationException(
                    $"opencode process exited (code {exitCode}) before signalling readiness. Stderr: {stderr}"));
                LogProcessExitedUnexpectedly(_logger, exitCode, null);
            }
            else
            {
                LogProcessExited(_logger, exitCode, null);
            }

            ProcessExited?.Invoke(this, exitCode);
        };

        _started = true;
        _process.Start();
        _jobObjectHandle = ProcessGroupHelper.AssignToProcessGroup(_process, _logger);
        _process.BeginOutputReadLine();
        _process.BeginErrorReadLine();

        // Race: ready signal vs timeout vs cancellation
        using var cts = CancellationTokenSource.CreateLinkedTokenSource(ct);
        cts.CancelAfter(options.StartupTimeout);

        try
        {
            await using var reg = cts.Token.Register(() =>
                tcs.TrySetCanceled(cts.Token));

            return await tcs.Task.ConfigureAwait(false);
        }
        catch (OperationCanceledException) when (!ct.IsCancellationRequested)
        {
            // Timeout expired, not external cancellation
            throw new TimeoutException(
                $"opencode did not become ready within {options.StartupTimeout.TotalSeconds:F0}s.");
        }
    }

    /// <summary>
    /// Gracefully stops the process, force-killing after <paramref name="timeout"/> if necessary.
    /// </summary>
    public async Task StopAsync(TimeSpan timeout)
    {
        if (_process is null || HasProcessExitedOrDetached(_process))
        {
            return;
        }

        try
        {
            // Kill the entire process group (Unix) or process tree (Windows)
            ProcessGroupHelper.KillProcessGroup(_process.Id, _logger);

            await _process.WaitForExitAsync().WaitAsync(timeout).ConfigureAwait(false);
        }
        catch (TimeoutException)
        {
            LogForceKilled(_logger, null);
            _process.Kill(entireProcessTree: true);
        }
        catch (Exception)
        {
            // Process may already be gone; ignore
        }
    }

    private static bool HasProcessExitedOrDetached(Process process)
    {
        try
        {
            return process.HasExited;
        }
        catch (InvalidOperationException)
        {
            // Test fakes and failed starts can leave a Process object with no associated OS process.
            return true;
        }
    }

    /// <inheritdoc />
    public async ValueTask DisposeAsync()
    {
        if (_disposed) return;
        _disposed = true;

        await StopAsync(TimeSpan.FromSeconds(5)).ConfigureAwait(false);
        _process?.Dispose();
        _jobObjectHandle?.Dispose();
    }
}
