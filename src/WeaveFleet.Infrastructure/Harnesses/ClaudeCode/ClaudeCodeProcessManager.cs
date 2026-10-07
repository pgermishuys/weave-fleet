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

    /// <summary>Claude Code session ID for <c>--resume</c>. Null for a session's first process.</summary>
    public string? SessionId { get; init; }

    public string? Model { get; init; }

    /// <summary>The reasoning effort (<c>--effort</c>: <c>low</c> … <c>max</c>); null for the model's default.</summary>
    public string? Effort { get; init; }

    public required string PermissionMode { get; init; }

    /// <summary>
    /// Claude Code asks Fleet before a tool call its mode doesn't allow (<c>--permission-prompt-tool stdio</c>), and Fleet
    /// answers on stdin.
    /// </summary>
    public bool AsksForPermission { get; init; }
    public string[] AllowedTools { get; init; } = [];
    public int? MaxTurns { get; init; }
    public decimal? MaxBudgetUsd { get; init; }

    /// <summary>Text added to Claude Code's system prompt (<c>--append-system-prompt</c>): Fleet's memory notes. Null = none.</summary>
    public string? AppendSystemPrompt { get; init; }

    /// <summary>
    /// MCP servers Claude Code loads besides the user's own (<c>--mcp-config</c>, as JSON): Fleet's, for its tools. Claude
    /// Code expands <c>${VAR}</c> in it from the process's environment. Null = none.
    /// </summary>
    public string? McpConfig { get; init; }

    /// <summary>A folder whose <c>.claude/skills</c> Claude Code loads besides the user's own (<c>--add-dir</c>): Fleet's built-in skills. Null = none.</summary>
    public string? SkillsDirectory { get; init; }

    public IReadOnlyDictionary<string, string> EnvironmentVariables { get; init; }
        = new Dictionary<string, string>();
}

/// <summary>
/// Manages a single <c>claude</c> CLI subprocess, which runs a Fleet session's prompts one after another: its stdin
/// (<c>--input-format stream-json</c>) stays open between turns, so the work an agent leaves running in the background
/// keeps going. Claude Code ends that work when its stdin closes, and so does killing the process group.
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
    private readonly SemaphoreSlim _inputLock = new(1, 1);
    private readonly System.Collections.Concurrent.ConcurrentDictionary<string, TaskCompletionSource<ClaudeCodeControlResponseBody?>> _requests =
        new(StringComparer.Ordinal);
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

    /// <summary>Why Fleet stopped the process, for the log; null when it exited by itself.</summary>
    public string? StopReason { get; set; }

    /// <summary>Whether Fleet has asked this process for the account's usage limits (<c>get_usage</c>).</summary>
    internal bool UsageAsked { get; set; }

    /// <summary>The token the process calls Fleet with (<c>FLEET_URL</c>); null when it has none.</summary>
    public string? BridgeToken { get; set; }

    /// <summary><c>true</c> when the process was killed because a turn ran past its timeout.</summary>
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
    /// Spawns the <c>claude</c> CLI process and returns a <see cref="StreamReader"/> for its stdout. Prompts go to it
    /// with <see cref="WriteLineAsync"/>.
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
        // Prompts go to stdin as stream-json user messages, one per turn, and stdin stays open between them.
        psi.ArgumentList.Add("-p");

        psi.ArgumentList.Add("--output-format");
        psi.ArgumentList.Add("stream-json");
        psi.ArgumentList.Add("--input-format");
        psi.ArgumentList.Add("stream-json");

        if (options.AsksForPermission)
        {
            psi.ArgumentList.Add("--permission-prompt-tool");
            psi.ArgumentList.Add("stdio");
        }

        // Print mode refuses stream-json output without it.
        psi.ArgumentList.Add("--verbose");

        // The text as the model writes it (stream_event lines), not a whole block at a time.
        psi.ArgumentList.Add("--include-partial-messages");

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

        if (options.Effort is not null)
        {
            psi.ArgumentList.Add("--effort");
            psi.ArgumentList.Add(options.Effort);
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

        // Without --strict-mcp-config, so the user's own MCP servers and the folder's .mcp.json still load.
        if (options.McpConfig is not null)
        {
            psi.ArgumentList.Add("--mcp-config");
            psi.ArgumentList.Add(options.McpConfig);
        }

        if (options.SkillsDirectory is not null)
        {
            psi.ArgumentList.Add("--add-dir");
            psi.ArgumentList.Add(options.SkillsDirectory);
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

        if (!string.IsNullOrWhiteSpace(options.AppendSystemPrompt))
        {
            psi.ArgumentList.Add("--append-system-prompt");
            psi.ArgumentList.Add(options.AppendSystemPrompt);
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
            FailRequests();
            ProcessExited?.Invoke(this, exitCode);
        };

        _started = true;
        _process.Start();
        _jobObjectHandle = ProcessGroupHelper.AssignToProcessGroup(_process, _logger);
        _process.BeginErrorReadLine();

        LogProcessStarted(_logger, _process.Id, options.WorkingDirectory, null);

        return Task.FromResult(_process.StandardOutput);
    }

    /// <summary>
    /// Writes one line to stdin: a prompt, a request, or an answer to one of Claude Code's asks. False when stdin is
    /// closed or the process gone: nothing reads it any more.
    /// </summary>
    public async Task<bool> WriteLineAsync(string line, CancellationToken ct)
    {
        if (_process is null || _disposed)
            return false;

        await _inputLock.WaitAsync(ct).ConfigureAwait(false);
        try
        {
            await _process.StandardInput.WriteLineAsync(line.AsMemory(), ct).ConfigureAwait(false);
            await _process.StandardInput.FlushAsync(ct).ConfigureAwait(false);
            return true;
        }
        catch (Exception ex) when (ex is IOException or ObjectDisposedException or InvalidOperationException)
        {
            // claude exited; stderr and the exit code say why.
            return false;
        }
        finally
        {
            _inputLock.Release();
        }
    }

    /// <summary>
    /// Sends a <c>control_request</c> line (<see cref="ClaudeCodeInput"/>) and waits for Claude Code's answer, which the
    /// stdout reader hands to <see cref="CompleteRequest"/>. Null when there's no answer within
    /// <paramref name="timeout"/>, or the process is gone.
    /// </summary>
    public async Task<ClaudeCodeControlResponseBody?> RequestAsync(string requestId, string line, TimeSpan timeout, CancellationToken ct)
    {
        var answer = new TaskCompletionSource<ClaudeCodeControlResponseBody?>(TaskCreationOptions.RunContinuationsAsynchronously);
        _requests[requestId] = answer;
        try
        {
            if (!await WriteLineAsync(line, ct).ConfigureAwait(false))
                return null;
            return await answer.Task.WaitAsync(timeout, ct).ConfigureAwait(false);
        }
        catch (TimeoutException)
        {
            return null;
        }
        finally
        {
            _requests.TryRemove(requestId, out _);
        }
    }

    /// <summary>Hands Claude Code's answer to the <see cref="RequestAsync"/> waiting for it.</summary>
    public void CompleteRequest(ClaudeCodeControlResponseBody response)
    {
        if (response.RequestId is { } id && _requests.TryGetValue(id, out var answer))
            answer.TrySetResult(response);
    }

    private void FailRequests()
    {
        foreach (var answer in _requests.Values)
            answer.TrySetResult(null);
    }

    /// <summary>Kills the process because a turn ran past Fleet's limit; <see cref="TimedOut"/> says so afterwards.</summary>
    public Task StopForTimeoutAsync(TimeSpan timeout)
    {
        LogForceKilled(_logger, null);
        TimedOut = true;
        return StopAsync(timeout);
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
        FailRequests();
        // _inputLock isn't disposed: a write racing the dispose still releases it. It holds no handle until asked for one.
        _process?.Dispose();
        _jobObjectHandle?.Dispose();
    }
}
