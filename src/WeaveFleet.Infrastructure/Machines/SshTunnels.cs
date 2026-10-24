using System.Collections.Concurrent;
using System.ComponentModel;
using System.Diagnostics;
using System.Net;
using System.Net.Sockets;
using System.Runtime.InteropServices;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using WeaveFleet.Application.Machines;
using WeaveFleet.Domain.Entities;
using WeaveFleet.Infrastructure.Harnesses;

namespace WeaveFleet.Infrastructure.Machines;

/// <summary>How Fleet runs the user's <c>ssh</c>.</summary>
public sealed class SshOptions
{
    /// <summary>The ssh to run: a name looked up on the PATH, or a full path.</summary>
    public string Executable { get; set; } = "ssh";

    public int ConnectTimeoutSeconds { get; set; } = 10;

    /// <summary>The port Fleet listens on at the other end of the tunnel (<c>fleet node</c>'s default).</summary>
    public int RemotePort { get; set; } = SshMachineService.NodePort;

    public TimeSpan InitialBackoff { get; set; } = TimeSpan.FromSeconds(1);

    public TimeSpan MaxBackoff { get; set; } = TimeSpan.FromSeconds(30);

    /// <summary>A tunnel that stayed up this long starts its backoff over when it drops.</summary>
    public TimeSpan StableAfter { get; set; } = TimeSpan.FromSeconds(30);

    /// <summary>How long a new tunnel gets to carry traffic before Fleet says it didn't open.</summary>
    public TimeSpan ReadyTimeout { get; set; } = TimeSpan.FromSeconds(15);

    /// <summary>Common options for every ssh Fleet runs: never a prompt, the user's own host-key rules.</summary>
    internal List<string> BaseArguments() =>
    [
        "-o", "BatchMode=yes",
        "-o", $"ConnectTimeout={ConnectTimeoutSeconds}",
    ];
}

/// <summary>Runs one command over SSH (<see cref="ISshRunner"/>) with the user's own <c>ssh</c>.</summary>
public sealed class SshProcessRunner(SshOptions options) : ISshRunner
{
    public async Task<SshResult> RunAsync(SshDestination destination, string command, CancellationToken cancellationToken, TimeSpan? timeout = null)
    {
        var start = SshProcess.Start(options.Executable);
        foreach (var argument in options.BaseArguments())
            start.ArgumentList.Add(argument);
        if (destination.Port is { } port)
        {
            start.ArgumentList.Add("-p");
            start.ArgumentList.Add(port.ToString(System.Globalization.CultureInfo.InvariantCulture));
        }

        start.ArgumentList.Add("--");
        start.ArgumentList.Add(destination.Target);
        start.ArgumentList.Add(command);

        using var limit = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        limit.CancelAfter(timeout ?? TimeSpan.FromSeconds(options.ConnectTimeoutSeconds + 20));
        Process process;
        try
        {
            process = Process.Start(start)!;
        }
        catch (Exception ex) when (ex is Win32Exception or InvalidOperationException)
        {
            return new SshResult(-1, "", ex.Message);
        }

        using (process)
        {
            ProcessGroupHelper.AssignToProcessGroup(process);
            process.StandardInput.Close();
            var output = process.StandardOutput.ReadToEndAsync(limit.Token);
            var error = process.StandardError.ReadToEndAsync(limit.Token);
            try
            {
                await process.WaitForExitAsync(limit.Token);
                return new SshResult(process.ExitCode, await output, await error);
            }
            catch (OperationCanceledException)
            {
                ProcessGroupHelper.KillProcessGroup(process);
                cancellationToken.ThrowIfCancellationRequested();
                return new SshResult(255, "", "ssh timed out.");
            }
        }
    }
}

internal static class SshProcess
{
    internal static ProcessStartInfo Start(string executable) => new(executable)
    {
        RedirectStandardInput = true,
        RedirectStandardOutput = true,
        RedirectStandardError = true,
        UseShellExecute = false,
        CreateNoWindow = true,
    };
}

/// <summary>
/// One <c>ssh -N -L 127.0.0.1:&lt;port&gt;:127.0.0.1:2113</c> per machine added Over SSH, kept open: if ssh exits it is
/// started again after a growing pause. Stopped when the machine is removed or Fleet stops; every ssh is also tracked
/// with the other processes Fleet starts (a Job Object on Windows, a parent watcher on Linux), so none outlives Fleet.
/// <para>
/// ssh asks nothing: <c>BatchMode=yes</c> means no password or passphrase prompt, and the host-key rules are the
/// user's own (<c>StrictHostKeyChecking</c> is never set here).
/// </para>
/// </summary>
public sealed partial class SshTunnels(
    SshOptions options,
    RemoteMachineService machines,
    ILogger<SshTunnels> logger,
    Func<int, CancellationToken, Task<bool>>? ready = null) : IHostedService, ISshTunnels, IAsyncDisposable
{
    internal SshOptions Options => options;

    internal ILogger Logger => logger;

    internal RemoteMachineService Machines => machines;

    private readonly object _gate = new();
    private readonly Dictionary<string, Tunnel> _tunnels = new(StringComparer.Ordinal);
    private readonly SafeHandle? _jobGroup = ProcessGroupHelper.CreateProcessGroup();
    private readonly Func<int, CancellationToken, Task<bool>> _ready = ready ?? AcceptsConnections;

    public async Task StartAsync(CancellationToken cancellationToken)
    {
        machines.Changed += OnChanged;
        foreach (var machine in await machines.ListAsync())
        {
            if (SshDestination.Parse(machine.SshDestination) is { } destination)
                await StartForSavedAsync(machine, destination);
        }
    }

    public Task StopAsync(CancellationToken cancellationToken)
    {
        machines.Changed -= OnChanged;
        foreach (var key in Keys())
            Close(key);
        return Task.CompletedTask;
    }

    public ValueTask DisposeAsync()
    {
        _ = StopAsync(CancellationToken.None);
        _jobGroup?.Dispose();
        return ValueTask.CompletedTask;
    }

    public bool IsUp(string key)
    {
        lock (_gate)
            return _tunnels.TryGetValue(key, out var tunnel) && tunnel.Up;
    }

    public void Close(string key)
    {
        Tunnel? tunnel;
        lock (_gate)
        {
            if (!_tunnels.Remove(key, out tunnel))
                return;
        }

        tunnel.Stop();
    }

    public async Task<(int Port, string? Error)> OpenAsync(string key, SshDestination destination, int? preferredPort, CancellationToken cancellationToken)
    {
        var port = ChoosePort(preferredPort);
        var tunnel = Add(key, destination, port);
        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        timeout.CancelAfter(options.ReadyTimeout);
        try
        {
            await tunnel.WaitUntilUpAsync(timeout.Token);
            return (port, null);
        }
        catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
        {
            Close(key);
            return (0, $"SSH logged in to {destination.Text}, but the tunnel to port {options.RemotePort} didn't open. {tunnel.LastError}".TrimEnd());
        }
        catch (OperationCanceledException)
        {
            Close(key);
            throw;
        }
    }

    internal IReadOnlyCollection<string> Keys()
    {
        lock (_gate)
            return [.. _tunnels.Keys];
    }

    private Tunnel Add(string key, SshDestination destination, int port)
    {
        var tunnel = new Tunnel(this, key, destination, port);
        Tunnel? replaced;
        lock (_gate)
        {
            _tunnels.Remove(key, out replaced);
            _tunnels[key] = tunnel;
        }

        replaced?.Stop();
        tunnel.Start();
        return tunnel;
    }

    private async Task StartForSavedAsync(RemoteMachine machine, SshDestination destination)
    {
        // The port it had, unless something else holds it now: then the machine's address moves with the new one.
        var previous = PortOf(machine.BaseUrl);
        var port = ChoosePort(previous);
        Add(machine.Id, destination, port);
        if (port != previous)
            await machines.MoveAsync(machine.Id, $"http://127.0.0.1:{port}");
    }

    private void OnChanged(string id) => _ = ReconcileAsync(id);

    private async Task ReconcileAsync(string id)
    {
        try
        {
            var machine = await machines.GetAsync(id);
            if (machine is null || SshDestination.Parse(machine.SshDestination) is not { } destination)
            {
                Close(id);
                return;
            }

            var port = PortOf(machine.BaseUrl);
            lock (_gate)
            {
                if (_tunnels.ContainsKey(id))
                    return;
                // The tunnel opened for a pairing that has just become this machine.
                var pending = _tunnels.FirstOrDefault(pair => pair.Key.StartsWith("pending:", StringComparison.Ordinal) && pair.Value.Port == port);
                if (pending.Value is not null)
                {
                    _tunnels.Remove(pending.Key);
                    pending.Value.Key = id;
                    _tunnels[id] = pending.Value;
                    return;
                }
            }

            Add(id, destination, ChoosePort(port));
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            LogReconcileFailed(logger, ex, id);
        }
    }

    private static int? PortOf(string baseUrl) => Uri.TryCreate(baseUrl, UriKind.Absolute, out var uri) ? uri.Port : null;

    private static int ChoosePort(int? preferred)
    {
        if (preferred is { } wanted && TryBind(wanted))
            return wanted;
        using var listener = new TcpListener(IPAddress.Loopback, 0);
        listener.Start();
        return ((IPEndPoint)listener.LocalEndpoint).Port;
    }

    private static bool TryBind(int port)
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

    private static async Task<bool> AcceptsConnections(int port, CancellationToken cancellationToken)
    {
        using var client = new TcpClient();
        try
        {
            await client.ConnectAsync(IPAddress.Loopback, port, cancellationToken);
            return true;
        }
        catch (SocketException)
        {
            return false;
        }
    }

    /// <summary>For tests: the arguments a tunnel to <paramref name="destination"/> is started with.</summary>
    internal List<string> Arguments(SshDestination destination, int port)
    {
        var arguments = new List<string> { "-N", "-L", $"127.0.0.1:{port}:127.0.0.1:{options.RemotePort}" };
        arguments.AddRange(options.BaseArguments());
        arguments.AddRange(["-o", "ExitOnForwardFailure=yes", "-o", "ServerAliveInterval=15", "-o", "ServerAliveCountMax=3"]);
        if (destination.Port is { } sshPort)
            arguments.AddRange(["-p", sshPort.ToString(System.Globalization.CultureInfo.InvariantCulture)]);
        arguments.AddRange(["--", destination.Target]);
        return arguments;
    }

    private sealed class Tunnel(SshTunnels owner, string key, SshDestination destination, int port) : IDisposable
    {
        private readonly CancellationTokenSource _stop = new();
        private readonly TaskCompletionSource _firstUp = new(TaskCreationOptions.RunContinuationsAsynchronously);
        private Process? _process;

        public string Key { get; set; } = key;

        public int Port { get; } = port;

        public volatile bool Up;

        public string LastError { get; private set; } = "";

        public void Start() => _ = Task.Run(RunAsync);

        public void Dispose() => _stop.Dispose();

        public void Stop()
        {
            try
            {
                _stop.Cancel();
            }
            catch (ObjectDisposedException)
            {
                return;
            }

            var process = _process;
            if (process is not null)
                ProcessGroupHelper.KillProcessGroup(process);
        }

        public async Task WaitUntilUpAsync(CancellationToken cancellationToken) => await _firstUp.Task.WaitAsync(cancellationToken);

        private async Task RunAsync()
        {
            using var _ = this;
            var options = owner.Options;
            var backoff = options.InitialBackoff;
            while (!_stop.IsCancellationRequested)
            {
                var startedAt = Stopwatch.GetTimestamp();
                try
                {
                    await RunOnceAsync();
                }
                catch (Exception ex) when (ex is Win32Exception or InvalidOperationException)
                {
                    LastError = ex.Message;
                }
                catch (OperationCanceledException)
                {
                    break;
                }

                SetUp(false);
                if (Stopwatch.GetElapsedTime(startedAt) >= options.StableAfter)
                    backoff = options.InitialBackoff;
                try
                {
                    await Task.Delay(backoff, _stop.Token);
                }
                catch (OperationCanceledException)
                {
                    break;
                }

                backoff = TimeSpan.FromTicks(Math.Min(backoff.Ticks * 2, options.MaxBackoff.Ticks));
            }
        }

        private async Task RunOnceAsync()
        {
            var start = SshProcess.Start(owner.Options.Executable);
            foreach (var argument in owner.Arguments(destination, Port))
                start.ArgumentList.Add(argument);

            using var process = Process.Start(start)!;
            _process = process;
            ProcessGroupHelper.AssignToProcessGroup(process, owner.Logger, owner._jobGroup);
            if (_stop.IsCancellationRequested)
                ProcessGroupHelper.KillProcessGroup(process);

            process.StandardInput.Close();
            var errors = Task.Run(async () =>
            {
                while (await process.StandardError.ReadLineAsync() is { } line)
                {
                    if (!string.IsNullOrWhiteSpace(line))
                        LastError = line.Trim();
                }
            });
            _ = process.StandardOutput.ReadToEndAsync();
            var watching = WatchReadyAsync(process);
            try
            {
                await process.WaitForExitAsync(_stop.Token);
            }
            catch (OperationCanceledException)
            {
                ProcessGroupHelper.KillProcessGroup(process);
                throw;
            }
            finally
            {
                _process = null;
            }

            await errors.WaitAsync(TimeSpan.FromSeconds(2)).ConfigureAwait(false);
            await watching.ConfigureAwait(false);
        }

        private async Task WatchReadyAsync(Process process)
        {
            using var cts = CancellationTokenSource.CreateLinkedTokenSource(_stop.Token);
            while (!cts.IsCancellationRequested)
            {
                try
                {
                    if (process.HasExited)
                        return;
                    if (await owner._ready(Port, cts.Token))
                    {
                        SetUp(true);
                        await process.WaitForExitAsync(cts.Token);
                        return;
                    }

                    await Task.Delay(100, cts.Token);
                }
                catch (Exception ex) when (ex is OperationCanceledException or InvalidOperationException)
                {
                    return;
                }
            }
        }

        private void SetUp(bool up)
        {
            if (Up == up)
                return;
            Up = up;
            if (up)
                _firstUp.TrySetResult();
            // The machine's row shows "Tunnel down"; the watcher reconnects when it is back. Pending tunnels have no row yet.
            if (!Key.StartsWith("pending:", StringComparison.Ordinal))
                owner.Machines.NotifyChanged(Key);
        }
    }

    [LoggerMessage(Level = LogLevel.Warning, Message = "Couldn't update the SSH tunnel for machine {MachineId}.")]
    private static partial void LogReconcileFailed(ILogger logger, Exception ex, string machineId);
}
