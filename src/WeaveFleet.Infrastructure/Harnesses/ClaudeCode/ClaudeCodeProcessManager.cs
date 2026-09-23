using System.Diagnostics;
using System.Text;
using Microsoft.Extensions.Logging;
using WeaveFleet.Application.Terminals;

namespace WeaveFleet.Infrastructure.Harnesses.ClaudeCode;

/// <summary>Options for starting a <c>claude</c> CLI process.</summary>
internal sealed record ClaudeCodeProcessOptions
{
    public required string BinaryPath { get; init; }
    public required string WorkingDirectory { get; init; }
    public required string Prompt { get; init; }

    /// <summary>Claude Code session ID for <c>--resume</c>. Null for the first prompt.</summary>
    public string? SessionId { get; init; }

    public string? Model { get; init; }
    public required string PermissionMode { get; init; }
    public string[] AllowedTools { get; init; } = [];
    public int? MaxTurns { get; init; }
    public decimal? MaxBudgetUsd { get; init; }

    /// <summary>How long the prompt may run before the process is killed. Null = no limit.</summary>
    public TimeSpan? ProcessTimeout { get; init; }
    public IReadOnlyDictionary<string, string> EnvironmentVariables { get; init; }
        = new Dictionary<string, string>();
}

/// <summary>
/// Manages a single <c>claude</c> CLI subprocess.
/// One instance per prompt execution (not per session — sessions span multiple prompts).
/// </summary>
internal sealed class ClaudeCodeProcessManager : IAsyncDisposable
{
    private static readonly Action<ILogger, int, string, Exception?> LogProcessStarted =
        LoggerMessage.Define<int, string>(LogLevel.Information, new EventId(1, "ProcessStarted"),
            "claude process started: pid={ProcessId} cwd={WorkingDirectory}");

    private static readonly Action<ILogger, string, Exception?> LogStderr =
        LoggerMessage.Define<string>(LogLevel.Warning, new EventId(2, "Stderr"),
            "claude stderr: {Line}");

    private static readonly Action<ILogger, int, Exception?> LogProcessExited =
        LoggerMessage.Define<int>(LogLevel.Information, new EventId(3, "ProcessExited"),
            "claude process exited with code {ExitCode}.");

    private static readonly Action<ILogger, Exception?> LogForceKilled =
        LoggerMessage.Define(LogLevel.Warning, new EventId(4, "ForceKilled"),
            "claude process did not exit within the timeout; force-killing.");

    private const int StderrLinesKept = 10;

    private readonly ILogger<ClaudeCodeProcessManager> _logger;
    private readonly Queue<string> _stderrTail = new();
    private Process? _process;
    private bool _started;
    private bool _disposed;
    private System.Runtime.InteropServices.SafeHandle? _jobObjectHandle;

    /// <summary>Fired when the process exits.</summary>
    public event EventHandler<int>? ProcessExited;

    /// <summary>Initialises the manager. Call <see cref="StartAsync"/> to spawn the process.</summary>
    public ClaudeCodeProcessManager(ILogger<ClaudeCodeProcessManager> logger)
    {
        _logger = logger;
    }

    /// <summary><c>true</c> if the process has been started and has not yet exited.</summary>
    public bool IsRunning => _process is { HasExited: false };

    /// <summary>OS process ID, if the process has been started.</summary>
    public int? ProcessId => _process?.Id;

    /// <summary><c>true</c> when the process was killed for running past its timeout.</summary>
    public bool TimedOut { get; private set; }

    /// <summary>The last lines the process wrote to stderr, oldest first.</summary>
    public IReadOnlyList<string> StderrTail
    {
        get
        {
            lock (_stderrTail)
            {
                return [.. _stderrTail];
            }
        }
    }

    /// <summary>
    /// Spawns the <c>claude</c> CLI process, writes the prompt to its stdin and returns a
    /// <see cref="StreamReader"/> for its stdout.
    /// The caller is responsible for reading all output before calling <see cref="StopAsync"/> or <see cref="DisposeAsync"/>.
    /// </summary>
    public async Task<StreamReader> StartAsync(ClaudeCodeProcessOptions options, CancellationToken ct)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        if (_started)
        {
            throw new InvalidOperationException("Process manager has already started a process.");
        }

        ct.ThrowIfCancellationRequested();

        var utf8 = new UTF8Encoding(encoderShouldEmitUTF8Identifier: false);
        var psi = new ProcessStartInfo
        {
            FileName = ExecutableResolver.Resolve(options.BinaryPath),
            WorkingDirectory = options.WorkingDirectory,
            UseShellExecute = false,
            RedirectStandardInput = true,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            StandardInputEncoding = utf8,
            StandardOutputEncoding = utf8,
            StandardErrorEncoding = utf8,
            CreateNoWindow = true,
        };

        // Build argument list safely — avoids shell injection via ArgumentList.
        // The prompt goes to stdin: as an argument, one starting with "-" would be read as an option.
        psi.ArgumentList.Add("-p");

        psi.ArgumentList.Add("--output-format");
        psi.ArgumentList.Add("stream-json");

        // Print mode refuses stream-json output without it.
        psi.ArgumentList.Add("--verbose");

        if (options.SessionId is not null)
        {
            psi.ArgumentList.Add("--resume");
            psi.ArgumentList.Add(options.SessionId);
        }

        if (options.Model is not null)
        {
            psi.ArgumentList.Add("--model");
            psi.ArgumentList.Add(options.Model);
        }

        if (!string.IsNullOrEmpty(options.PermissionMode))
        {
            psi.ArgumentList.Add("--permission-mode");
            psi.ArgumentList.Add(options.PermissionMode);
        }

        if (options.AllowedTools.Length > 0)
        {
            psi.ArgumentList.Add("--allowedTools");
            psi.ArgumentList.Add(string.Join(",", options.AllowedTools));
        }

        if (options.MaxTurns.HasValue)
        {
            psi.ArgumentList.Add("--max-turns");
            psi.ArgumentList.Add(options.MaxTurns.Value.ToString(System.Globalization.CultureInfo.InvariantCulture));
        }

        if (options.MaxBudgetUsd.HasValue)
        {
            psi.ArgumentList.Add("--max-budget-usd");
            psi.ArgumentList.Add(options.MaxBudgetUsd.Value.ToString(System.Globalization.CultureInfo.InvariantCulture));
        }

        // Fleet's variables stay with Fleet: the agent's shell tool would pass them on to every command it runs.
        TerminalEnvironment.RemoveFleetOwned(psi.Environment);

        // Apply caller-supplied environment variables
        foreach (var (key, value) in options.EnvironmentVariables)
        {
            psi.Environment[key] = value;
        }

        _process = new Process { StartInfo = psi, EnableRaisingEvents = true };

        // Capture stderr for diagnostics — does not block stdout reading
        _process.ErrorDataReceived += (_, e) =>
        {
            if (e.Data is null) return;
            LogStderr(_logger, e.Data, null);

            lock (_stderrTail)
            {
                _stderrTail.Enqueue(e.Data);
                if (_stderrTail.Count > StderrLinesKept)
                    _stderrTail.Dequeue();
            }
        };

        _process.Exited += (_, _) =>
        {
            int exitCode = _process.ExitCode;
            LogProcessExited(_logger, exitCode, null);
            ProcessExited?.Invoke(this, exitCode);
        };

        _started = true;
        _process.Start();
        _jobObjectHandle = ProcessGroupHelper.AssignToProcessGroup(_process, _logger);
        _process.BeginErrorReadLine();

        LogProcessStarted(_logger, _process.Id, options.WorkingDirectory, null);

        // Closing stdin tells claude the prompt is complete.
        try
        {
            await _process.StandardInput.WriteAsync(options.Prompt.AsMemory(), ct).ConfigureAwait(false);
            await _process.StandardInput.FlushAsync(ct).ConfigureAwait(false);
        }
        catch (IOException)
        {
            // claude exited before reading its prompt; stderr and the exit code say why.
        }
        finally
        {
            _process.StandardInput.Close();
        }

        // Apply process timeout (fire-and-forget kill on timeout)
        if (options.ProcessTimeout is { } processTimeout)
        {
            var process = _process;
            _ = Task.Run(async () =>
            {
                await Task.Delay(processTimeout, CancellationToken.None).ConfigureAwait(false);
                if (!_disposed && !process.HasExited)
                {
                    LogForceKilled(_logger, null);
                    TimedOut = true;
                    process.Kill(entireProcessTree: true);
                }
            }, CancellationToken.None);
        }

        return _process.StandardOutput;
    }

    /// <summary>
    /// Waits for the process to exit and for its stderr to drain, up to <paramref name="timeout"/>.
    /// Returns the exit code, or null if it is still running.
    /// </summary>
    public async Task<int?> WaitForExitAsync(TimeSpan timeout)
    {
        if (_process is null)
            return null;

        try
        {
            await _process.WaitForExitAsync().WaitAsync(timeout).ConfigureAwait(false);
            return _process.ExitCode;
        }
        catch (TimeoutException)
        {
            return null;
        }
        catch (InvalidOperationException)
        {
            return null;
        }
    }

    /// <summary>
    /// Gracefully stops the process, force-killing after <paramref name="timeout"/> if necessary.
    /// </summary>
    public async Task StopAsync(TimeSpan timeout)
    {
        if (_process is null || _process.HasExited)
        {
            return;
        }

        try
        {
            // Kill the process and its children
            ProcessGroupHelper.KillProcessGroup(_process, _logger);

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
