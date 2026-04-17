using System.Diagnostics;
using System.Text;
using Microsoft.Extensions.Logging;

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
    public required TimeSpan ProcessTimeout { get; init; }
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

    private readonly ILogger<ClaudeCodeProcessManager> _logger;
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

    /// <summary>
    /// Spawns the <c>claude</c> CLI process and returns a <see cref="StreamReader"/> for its stdout.
    /// The caller is responsible for reading all output before calling <see cref="StopAsync"/> or <see cref="DisposeAsync"/>.
    /// </summary>
    public Task<StreamReader> StartAsync(ClaudeCodeProcessOptions options, CancellationToken ct)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        if (_started)
        {
            throw new InvalidOperationException("Process manager has already started a process.");
        }

        ct.ThrowIfCancellationRequested();

        var psi = new ProcessStartInfo
        {
            FileName = ExecutableResolver.Resolve(options.BinaryPath),
            WorkingDirectory = options.WorkingDirectory,
            UseShellExecute = false,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            CreateNoWindow = true,
        };

        // Build argument list safely — avoids shell injection via ArgumentList
        psi.ArgumentList.Add("-p");
        psi.ArgumentList.Add(options.Prompt);

        psi.ArgumentList.Add("--output-format");
        psi.ArgumentList.Add("stream-json");

        psi.ArgumentList.Add("--bare");

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

        // Apply process timeout via linked CTS (fire-and-forget kill on timeout)
        var processTimeout = options.ProcessTimeout;
        _ = Task.Run(async () =>
        {
            await Task.Delay(processTimeout, ct).ConfigureAwait(false);
            if (!_disposed && _process is { HasExited: false })
            {
                LogForceKilled(_logger, null);
                _process.Kill(entireProcessTree: true);
            }
        }, CancellationToken.None);

        return Task.FromResult(_process.StandardOutput);
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
