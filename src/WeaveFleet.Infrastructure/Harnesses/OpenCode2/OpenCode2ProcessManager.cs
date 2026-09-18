using System.Diagnostics;
using System.Text.RegularExpressions;
using Microsoft.Extensions.Logging;
using WeaveFleet.Application.Terminals;

namespace WeaveFleet.Infrastructure.Harnesses.OpenCode2;

/// <summary>Options for starting <c>opencode2 serve</c>.</summary>
internal sealed record OpenCode2ProcessOptions
{
    public required string ExecutablePath { get; init; }
    public required string WorkingDirectory { get; init; }
    public required string Password { get; init; }
    public IReadOnlyDictionary<string, string> EnvironmentVariables { get; init; } = new Dictionary<string, string>();
    public TimeSpan StartupTimeout { get; init; } = TimeSpan.FromSeconds(30);
}

/// <summary>
/// Starts and stops one <c>opencode2 serve</c> process. It listens on a port it picks itself (<c>--port 0</c>) and
/// prints <c>server listening on http://127.0.0.1:PORT</c> when it's ready.
/// </summary>
internal sealed partial class OpenCode2ProcessManager(ILogger<OpenCode2ProcessManager> logger) : IAsyncDisposable
{
    private const int OutputTailLines = 20;

    private readonly Queue<string> _outputTail = new();
    private readonly Lock _outputLock = new();
    private Process? _process;
    private System.Runtime.InteropServices.SafeHandle? _jobObjectHandle;
    private bool _disposed;

    /// <summary>Raised once when the process exits, with its exit code.</summary>
    public event EventHandler<int>? Exited;

    public bool IsRunning => _process is { HasExited: false };

    public int? ProcessId => _process?.Id;

    /// <summary>The last lines the server wrote, for saying why it failed.</summary>
    public string OutputTail
    {
        get
        {
            lock (_outputLock)
                return string.Join('\n', _outputTail);
        }
    }

    /// <summary>Starts the server and returns its address once it's listening.</summary>
    public async Task<Uri> StartAsync(OpenCode2ProcessOptions options, CancellationToken ct)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        if (_process is not null)
            throw new InvalidOperationException("This process manager has already started a server.");

        var psi = new ProcessStartInfo
        {
            FileName = options.ExecutablePath,
            WorkingDirectory = options.WorkingDirectory,
            UseShellExecute = false,
            RedirectStandardInput = true,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            CreateNoWindow = true,
        };
        foreach (var argument in (string[])["serve", "--port", "0", "--hostname", "127.0.0.1"])
            psi.ArgumentList.Add(argument);

        // Fleet's variables stay with Fleet: the agent's shell tool would pass them on to every command it runs.
        TerminalEnvironment.RemoveFleetOwned(psi.Environment);
        foreach (var (key, value) in options.EnvironmentVariables)
            psi.Environment[key] = value;
        psi.Environment["OPENCODE_SERVER_PASSWORD"] = options.Password;

        var listening = new TaskCompletionSource<Uri>(TaskCreationOptions.RunContinuationsAsynchronously);
        var process = new Process { StartInfo = psi, EnableRaisingEvents = true };
        process.OutputDataReceived += (_, e) => OnOutput(e.Data, listening);
        process.ErrorDataReceived += (_, e) => OnOutput(e.Data, listening: null);
        process.Exited += (_, _) =>
        {
            var exitCode = process.ExitCode;
            LogExited(logger, process.Id, exitCode);
            listening.TrySetException(new InvalidOperationException(
                $"OpenCode 2 exited with code {exitCode} before it started listening. {OutputTail}".TrimEnd()));
            Exited?.Invoke(this, exitCode);
        };

        _process = process;
        process.Start();
        _jobObjectHandle = ProcessGroupHelper.AssignToProcessGroup(process, logger);
        process.BeginOutputReadLine();
        process.BeginErrorReadLine();
        LogStarted(logger, process.Id, options.ExecutablePath);

        try
        {
            return await listening.Task.WaitAsync(options.StartupTimeout, ct).ConfigureAwait(false);
        }
        catch (TimeoutException)
        {
            throw new TimeoutException(
                $"OpenCode 2 didn't start listening within {options.StartupTimeout.TotalSeconds:0} seconds. {OutputTail}".TrimEnd());
        }
    }

    public async Task StopAsync(TimeSpan timeout)
    {
        if (_process is not { HasExited: false } process)
            return;

        try
        {
            ProcessGroupHelper.KillProcessGroup(process.Id, logger);
            await process.WaitForExitAsync().WaitAsync(timeout).ConfigureAwait(false);
        }
        catch (Exception ex) when (ex is TimeoutException or InvalidOperationException)
        {
            try
            {
                process.Kill(entireProcessTree: true);
            }
            catch (InvalidOperationException)
            {
                // Already exited.
            }
        }
    }

    public async ValueTask DisposeAsync()
    {
        if (_disposed)
            return;
        _disposed = true;

        await StopAsync(TimeSpan.FromSeconds(5)).ConfigureAwait(false);
        _process?.Dispose();
        _jobObjectHandle?.Dispose();
    }

    private void OnOutput(string? line, TaskCompletionSource<Uri>? listening)
    {
        if (line is null)
            return;

        lock (_outputLock)
        {
            _outputTail.Enqueue(line);
            while (_outputTail.Count > OutputTailLines)
                _outputTail.Dequeue();
        }

        if (listening is not null && ParseListeningUrl(line) is { } url)
            listening.TrySetResult(url);
        else
            LogOutput(logger, line);
    }

    /// <summary>The address in V2's <c>server listening on http://127.0.0.1:PORT</c> line.</summary>
    internal static Uri? ParseListeningUrl(string line)
    {
        var match = ListeningPattern().Match(line);
        return match.Success && Uri.TryCreate(match.Groups["url"].Value.TrimEnd('/') + "/", UriKind.Absolute, out var url)
            ? url
            : null;
    }

    [GeneratedRegex(@"server listening on (?<url>https?://\S+)")]
    private static partial Regex ListeningPattern();

    [LoggerMessage(Level = LogLevel.Information, Message = "OpenCode 2 server started: pid={ProcessId} executable={ExecutablePath}")]
    private static partial void LogStarted(ILogger logger, int processId, string executablePath);

    [LoggerMessage(Level = LogLevel.Information, Message = "OpenCode 2 server {ProcessId} exited with code {ExitCode}")]
    private static partial void LogExited(ILogger logger, int processId, int exitCode);

    [LoggerMessage(Level = LogLevel.Debug, Message = "opencode2: {Line}")]
    private static partial void LogOutput(ILogger logger, string line);
}
