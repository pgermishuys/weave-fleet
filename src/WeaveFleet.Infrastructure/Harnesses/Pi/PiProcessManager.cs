using System.Diagnostics;
using System.Text;
using Microsoft.Extensions.Logging;

namespace WeaveFleet.Infrastructure.Harnesses.Pi;

/// <summary>Options for starting a <c>pi --mode rpc</c> process.</summary>
internal sealed record PiProcessOptions
{
    public const string DefaultBinaryPath = "pi";
    public const string DefaultProvider = "github-copilot";
    public const string DefaultModel = "claude-haiku-4.5";

    public string BinaryPath { get; init; } = DefaultBinaryPath;
    public string Provider { get; init; } = DefaultProvider;
    public string Model { get; init; } = DefaultModel;
    public string WorkingDirectory { get; init; } = Environment.CurrentDirectory;
    public IReadOnlyDictionary<string, string> EnvironmentVariables { get; init; }
        = new Dictionary<string, string>();
}

/// <summary>Open pipes and process metadata for a running Pi RPC process.</summary>
internal sealed record PiProcessConnection
{
    public required int ProcessId { get; init; }
    public required StreamWriter StandardInput { get; init; }
    public required StreamReader StandardOutput { get; init; }
}

/// <summary>
/// Encapsulates spawning and managing the lifecycle of a <c>pi --mode rpc</c> child process.
/// </summary>
internal interface IPiProcessManager : IAsyncDisposable
{
    /// <summary>Fired when the process exits.</summary>
    event EventHandler<int>? ProcessExited;

    /// <summary><c>true</c> if the process has been started and has not yet exited.</summary>
    bool IsRunning { get; }

    /// <summary>OS process ID, if the process has been started.</summary>
    int? ProcessId { get; }

    /// <summary>Stops the managed process.</summary>
    Task StopAsync(TimeSpan timeout);
}

/// <summary>
/// Encapsulates spawning and managing the lifecycle of a <c>pi --mode rpc</c> child process.
/// </summary>
internal sealed class PiProcessManager : IPiProcessManager
{
    private static readonly Action<ILogger, int, string, string, string, Exception?> LogProcessStarted =
        LoggerMessage.Define<int, string, string, string>(LogLevel.Information, new EventId(1, "ProcessStarted"),
            "pi process started: pid={ProcessId} cwd={WorkingDirectory} provider={Provider} model={Model}");

    private static readonly Action<ILogger, string, Exception?> LogStderr =
        LoggerMessage.Define<string>(LogLevel.Warning, new EventId(2, "Stderr"),
            "pi stderr: {Line}");

    private static readonly Action<ILogger, int, Exception?> LogProcessExited =
        LoggerMessage.Define<int>(LogLevel.Information, new EventId(3, "ProcessExited"),
            "pi process exited with code {ExitCode}.");

    private static readonly Action<ILogger, Exception?> LogCloseStdinFailed =
        LoggerMessage.Define(LogLevel.Debug, new EventId(4, "CloseStdinFailed"),
            "Failed to close pi stdin during graceful shutdown.");

    private static readonly Action<ILogger, Exception?> LogForceKilled =
        LoggerMessage.Define(LogLevel.Warning, new EventId(5, "ForceKilled"),
            "pi process did not exit within the timeout; force-killing.");

    private static readonly Action<ILogger, Exception?> LogKillFailed =
        LoggerMessage.Define(LogLevel.Debug, new EventId(6, "KillFailed"),
            "Failed to kill pi process; it may have already exited.");

    private readonly ILogger<PiProcessManager> _logger;
    private readonly StringBuilder _stderr = new();
    private readonly object _stderrLock = new();
    private Process? _process;
    private bool _started;
    private bool _disposed;
    private System.Runtime.InteropServices.SafeHandle? _jobObjectHandle;

    /// <summary>Fired when the process exits.</summary>
    public event EventHandler<int>? ProcessExited;

    /// <summary>Initialises the manager. Call <see cref="StartAsync"/> to spawn the process.</summary>
    public PiProcessManager(ILogger<PiProcessManager> logger)
    {
        _logger = logger;
    }

    /// <summary><c>true</c> if the process has been started and has not yet exited.</summary>
    public bool IsRunning => _process is { HasExited: false };

    /// <summary>OS process ID, if the process has been started.</summary>
    public int? ProcessId => _process?.Id;

    /// <summary>Captured stderr lines for process-startup and runtime diagnostics.</summary>
    public string Stderr
    {
        get
        {
            lock (_stderrLock)
            {
                return _stderr.ToString();
            }
        }
    }

    /// <summary>Exit code after the process has exited; otherwise <c>null</c>.</summary>
    public int? ExitCode => _process is { HasExited: true } process ? process.ExitCode : null;

    /// <summary>
    /// Spawns <c>pi --mode rpc --provider &lt;provider&gt; --model &lt;model&gt;</c> and returns its stdin/stdout pipes.
    /// The caller is responsible for reading stdout; this manager only captures stderr diagnostics.
    /// </summary>
    public Task<PiProcessConnection> StartAsync(PiProcessOptions options, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(options);
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
            RedirectStandardInput = true,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            CreateNoWindow = true,
        };

        psi.ArgumentList.Add("--mode");
        psi.ArgumentList.Add("rpc");
        psi.ArgumentList.Add("--provider");
        psi.ArgumentList.Add(options.Provider);
        psi.ArgumentList.Add("--model");
        psi.ArgumentList.Add(options.Model);

        foreach (var (key, value) in options.EnvironmentVariables)
        {
            psi.Environment[key] = value;
        }

        _process = new Process { StartInfo = psi, EnableRaisingEvents = true };

        _process.ErrorDataReceived += (_, e) =>
        {
            if (e.Data is null) return;
            lock (_stderrLock)
            {
                _stderr.AppendLine(e.Data);
            }

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

        LogProcessStarted(_logger, _process.Id, options.WorkingDirectory, options.Provider, options.Model, null);

        return Task.FromResult(new PiProcessConnection
        {
            ProcessId = _process.Id,
            StandardInput = _process.StandardInput,
            StandardOutput = _process.StandardOutput,
        });
    }

    /// <summary>
    /// Gracefully stops the process by closing stdin first, then force-killing the process group after
    /// <paramref name="timeout"/> if necessary.
    /// </summary>
    public async Task StopAsync(TimeSpan timeout)
    {
        if (_process is null || _process.HasExited)
        {
            return;
        }

        try
        {
            _process.StandardInput.Close();
        }
        catch (Exception ex)
        {
            LogCloseStdinFailed(_logger, ex);
        }

        try
        {
            await _process.WaitForExitAsync().WaitAsync(timeout).ConfigureAwait(false);
            return;
        }
        catch (TimeoutException)
        {
            LogForceKilled(_logger, null);
        }

        try
        {
            ProcessGroupHelper.KillProcessGroup(_process.Id, _logger);
            await _process.WaitForExitAsync().WaitAsync(TimeSpan.FromSeconds(1)).ConfigureAwait(false);
        }
        catch (TimeoutException)
        {
            try
            {
                _process.Kill(entireProcessTree: true);
            }
            catch (Exception ex)
            {
                LogKillFailed(_logger, ex);
            }
        }
        catch (Exception ex)
        {
            LogKillFailed(_logger, ex);
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
