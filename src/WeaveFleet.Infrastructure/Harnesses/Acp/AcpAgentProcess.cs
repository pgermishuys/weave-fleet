using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Text;
using Microsoft.Extensions.Logging;
using WeaveFleet.Application.Terminals;

namespace WeaveFleet.Infrastructure.Harnesses.Acp;

/// <summary>An ACP agent a session talks to: a child process, or a fake one in tests.</summary>
internal interface IAcpAgent : IAsyncDisposable
{
    AcpClient Client { get; }

    int ProcessId { get; }

    bool IsRunning { get; }

    /// <summary>What the agent wrote to stderr, which says why it stopped when it did.</summary>
    string Stderr { get; }
}

/// <summary>An ACP agent running as a child process, with the client that talks to its stdio.</summary>
internal sealed class AcpAgentProcess : IAcpAgent
{
    private readonly Process _process;
    private readonly SafeHandle? _group;
    private readonly StringBuilder _stderr = new();
    private readonly ILogger _logger;

    private AcpAgentProcess(Process process, SafeHandle? group, AcpClient client, ILogger logger)
    {
        _process = process;
        _group = group;
        Client = client;
        _logger = logger;
    }

    public AcpClient Client { get; }

    public int ProcessId => _process.Id;

    public bool IsRunning => !_process.HasExited;

    public string Stderr
    {
        get
        {
            lock (_stderr)
                return _stderr.ToString().Trim();
        }
    }

    public static AcpAgentProcess Start(string executable, IReadOnlyList<string> arguments, string workingDirectory,
        IReadOnlyDictionary<string, string> environment, ILogger logger)
    {
        var start = new ProcessStartInfo
        {
            FileName = executable,
            WorkingDirectory = workingDirectory,
            UseShellExecute = false,
            RedirectStandardInput = true,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            CreateNoWindow = true,
            StandardOutputEncoding = new UTF8Encoding(false),
            StandardInputEncoding = new UTF8Encoding(false),
        };
        foreach (var argument in arguments)
            start.ArgumentList.Add(argument);

        // Fleet's variables stay with Fleet: the agent's shell tool would pass them on to every command it runs.
        TerminalEnvironment.RemoveFleetOwned(start.Environment);
        foreach (var (key, value) in environment)
            start.Environment[key] = value;

        var process = new Process { StartInfo = start, EnableRaisingEvents = true };
        process.Start();
        var group = ProcessGroupHelper.AssignToProcessGroup(process, logger);
        var agent = new AcpAgentProcess(process, group, new AcpClient(process.StandardOutput, process.StandardInput, logger), logger);
        process.ErrorDataReceived += (_, e) =>
        {
            if (e.Data is null)
                return;
            lock (agent._stderr)
                agent._stderr.AppendLine(e.Data);
        };
        process.BeginErrorReadLine();
        return agent;
    }

    public async ValueTask DisposeAsync()
    {
        try
        {
            _process.StandardInput.Close();
            await _process.WaitForExitAsync().WaitAsync(TimeSpan.FromSeconds(3)).ConfigureAwait(false);
        }
        catch (Exception ex) when (ex is TimeoutException or IOException or InvalidOperationException)
        {
            ProcessGroupHelper.KillProcessGroup(_process, _logger);
        }

        await Client.DisposeAsync().ConfigureAwait(false);
        _process.Dispose();
        _group?.Dispose();
    }
}
