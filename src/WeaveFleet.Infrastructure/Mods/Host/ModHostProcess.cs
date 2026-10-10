using System.Diagnostics;
using System.Runtime.InteropServices;
using Microsoft.Extensions.Logging;
using WeaveFleet.Application.Mods.Host;
using WeaveFleet.Infrastructure.Harnesses;

namespace WeaveFleet.Infrastructure.Mods.Host;

/// <summary>
/// One <c>{bun} {host.js} --stdio</c> child process: an empty environment, Fleet's process group (it dies with Fleet),
/// its stderr in Fleet's log. The caller speaks to it over <see cref="StandardInput"/> and <see cref="StandardOutput"/>.
/// </summary>
internal sealed class ModHostProcess : IAsyncDisposable
{
    private static readonly Action<ILogger, string, string, Exception?> LogStderr =
        LoggerMessage.Define<string, string>(LogLevel.Information, new EventId(1, "HostStderr"),
            "mods-host[{UserKey}]: {Line}");

    private static readonly Action<ILogger, int, string, Exception?> LogStarted =
        LoggerMessage.Define<int, string>(LogLevel.Information, new EventId(2, "HostStarted"),
            "mod host started: pid={ProcessId} user={UserKey}");

    private static readonly Action<ILogger, int, string, Exception?> LogExited =
        LoggerMessage.Define<int, string>(LogLevel.Information, new EventId(3, "HostExited"),
            "mod host exited with code {ExitCode}: user={UserKey}");

    private static readonly Action<ILogger, Exception?> LogKillFailed =
        LoggerMessage.Define(LogLevel.Debug, new EventId(4, "KillFailed"),
            "Failed to kill the mod host; it may have already exited.");

    private readonly Process _process;
    private readonly ILogger _logger;
    private readonly string _userKey;
    private readonly TaskCompletionSource<int> _exited = new(TaskCreationOptions.RunContinuationsAsynchronously);
    private readonly TaskCompletionSource _stderrDone = new(TaskCreationOptions.RunContinuationsAsynchronously);
    private SafeHandle? _jobObject;
    private string? _lastStderrLine;
    private bool _disposed;

    private ModHostProcess(ModHostLaunch launch, ILogger logger)
    {
        _logger = logger;
        _userKey = launch.UserKey;

        var psi = new ProcessStartInfo
        {
            FileName = launch.BunPath,
            WorkingDirectory = launch.WorkingDirectory,
            UseShellExecute = false,
            RedirectStandardInput = true,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            CreateNoWindow = true,
        };
        psi.ArgumentList.Add(launch.HostScript);
        psi.ArgumentList.Add("--stdio");

        // Mods are other people's code: nothing of Fleet's environment (tokens, paths, keys) reaches the host.
        psi.Environment.Clear();
        if (OperatingSystem.IsWindows())
        {
            // Windows runtimes can't open sockets or use crypto without these two.
            foreach (var name in new[] { "SystemRoot", "WINDIR" })
            {
                if (Environment.GetEnvironmentVariable(name) is { } value)
                    psi.Environment[name] = value;
            }
        }

        _process = new Process { StartInfo = psi, EnableRaisingEvents = true };
        _process.ErrorDataReceived += (_, e) => OnStderr(e.Data);
        _process.Exited += (_, _) => OnExited();
    }

    public int ProcessId { get; private set; }

    /// <summary>Completes with the exit code when the process has exited.</summary>
    public Task<int> Exited => _exited.Task;

    /// <summary>Completes once the process's stderr has been read to its end.</summary>
    public Task StderrDone => _stderrDone.Task;

    /// <summary>The last line the process wrote to stderr, if any.</summary>
    public string? LastStderrLine => Volatile.Read(ref _lastStderrLine);

    /// <summary>The process's stdin: what Fleet writes.</summary>
    public Stream StandardInput => _process.StandardInput.BaseStream;

    /// <summary>The process's stdout: what Fleet reads.</summary>
    public Stream StandardOutput => _process.StandardOutput.BaseStream;

    /// <summary>Starts the process. Throws when it can't be started.</summary>
    public static ModHostProcess Start(ModHostLaunch launch, ILogger logger)
    {
        Directory.CreateDirectory(launch.WorkingDirectory);

        var host = new ModHostProcess(launch, logger);
        try
        {
            host._process.Start();
        }
        catch
        {
            host._process.Dispose();
            throw;
        }

        host.ProcessId = host._process.Id;
        host._jobObject = ProcessGroupHelper.AssignToProcessGroup(host._process, logger);
        host._process.BeginErrorReadLine();
        LogStarted(logger, host.ProcessId, launch.UserKey, null);
        return host;
    }

    /// <summary>Kills the process and everything it started. Never throws.</summary>
    public void Kill()
    {
        try
        {
            ProcessGroupHelper.KillProcessGroup(_process, _logger);
            if (!_process.HasExited)
                _process.Kill(entireProcessTree: true);
        }
        catch (Exception e)
        {
            LogKillFailed(_logger, e);
        }
    }

    public ValueTask DisposeAsync()
    {
        if (_disposed)
            return ValueTask.CompletedTask;
        _disposed = true;

        Kill();
        _process.Dispose();
        _jobObject?.Dispose();
        return ValueTask.CompletedTask;
    }

    private void OnStderr(string? line)
    {
        if (line is null)
        {
            _stderrDone.TrySetResult();
            return;
        }

        Volatile.Write(ref _lastStderrLine, line);
        LogStderr(_logger, _userKey, line, null);
    }

    private void OnExited()
    {
        if (_exited.Task.IsCompleted)
            return;
        int code;
        try
        {
            code = _process.ExitCode;
        }
        catch (InvalidOperationException)
        {
            code = -1;
        }

        LogExited(_logger, code, _userKey, null);
        _exited.TrySetResult(code);
    }
}
