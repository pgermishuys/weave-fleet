using System.Diagnostics;
using System.Runtime.InteropServices;
using Microsoft.Extensions.Logging;
using WeaveFleet.Application.Mods.Host;
using WeaveFleet.Infrastructure.Harnesses;

namespace WeaveFleet.Infrastructure.Mods.Host;

/// <summary>
/// One <c>{bun} {host.js} --stdio</c> child process: an empty environment, Fleet's process group (it dies with Fleet),
/// its stderr in Fleet's log.
/// </summary>
internal sealed class ModHostProcess : IDisposable
{
    private static readonly Action<ILogger, string, string, Exception?> LogStderr =
        LoggerMessage.Define<string, string>(LogLevel.Information, new EventId(1, "HostStderr"), "mods-host[{UserKey}]: {Line}");

    private static readonly Action<ILogger, Exception?> LogKillFailed =
        LoggerMessage.Define(LogLevel.Debug, new EventId(2, "KillFailed"), "Failed to kill the mod host; it may have already exited.");

    private readonly Process _process = new() { EnableRaisingEvents = true };
    private readonly ILogger _logger;
    private SafeHandle? _jobObject;
    private string? _lastStderrLine;

    private ModHostProcess(ModHostLaunch launch, ILogger logger)
    {
        _logger = logger;
        var psi = _process.StartInfo;
        psi.FileName = launch.BunPath;
        psi.WorkingDirectory = launch.WorkingDirectory;
        psi.RedirectStandardInput = psi.RedirectStandardOutput = psi.RedirectStandardError = true;
        psi.CreateNoWindow = true;
        psi.ArgumentList.Add(launch.HostScript);
        psi.ArgumentList.Add("--stdio");

        // Mods are other people's code: nothing of Fleet's environment (tokens, paths, keys) reaches the host.
        psi.Environment.Clear();
        if (OperatingSystem.IsWindows())
        {
            // Windows runtimes can't open sockets or use crypto without these two.
            foreach (var name in new[] { "SystemRoot", "WINDIR" })
                psi.Environment[name] = Environment.GetEnvironmentVariable(name);
        }

        _process.ErrorDataReceived += (_, e) =>
        {
            if (e.Data is null)
                return;
            Volatile.Write(ref _lastStderrLine, e.Data);
            LogStderr(logger, launch.UserKey, e.Data, null);
        };
    }

    public int ProcessId { get; private set; }

    /// <summary>Completes with the exit code once the process has exited and its stderr has been read to the end.</summary>
    public Task<int> Exited { get; private set; } = Task.FromResult(-1);

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
        host.Exited = host.WaitAsync();
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

    public void Dispose()
    {
        Kill();
        _process.Dispose();
        _jobObject?.Dispose();
    }

    private async Task<int> WaitAsync()
    {
        try
        {
            await _process.WaitForExitAsync().ConfigureAwait(false);
            return _process.ExitCode;
        }
        catch (Exception e) when (e is InvalidOperationException or ObjectDisposedException)
        {
            return -1;
        }
    }
}
