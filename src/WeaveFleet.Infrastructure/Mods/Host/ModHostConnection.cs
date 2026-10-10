using System.ComponentModel;
using System.Diagnostics;
using System.Text.Json;
using Microsoft.Extensions.Logging;
using WeaveFleet.Application.Mods.Host;

namespace WeaveFleet.Infrastructure.Mods.Host;

/// <summary>A running mod host: its process and the JSON-RPC peer over its pipes.</summary>
internal sealed class ModHostConnection(ModHostProcess process, ModHostRpc rpc, ModHostInitializeResult host) : IModHostConnection
{
    public int ProcessId => process.ProcessId;

    public ModHostInitializeResult Host => host;

    public Task<int> Exited => process.Exited;

    public Task<JsonElement> RequestAsync(string method, JsonElement parameters, TimeSpan timeout)
        => rpc.RequestAsync(method, parameters, timeout);

    public async Task ShutdownAsync(TimeSpan grace)
    {
        var clock = Stopwatch.StartNew();
        try
        {
            // The request is timed by the grace, write included; a host that answers gets what is left of it to exit.
            await rpc.RequestAsync("shutdown", JsonSerializer.SerializeToElement(new EmptyParams(), ModHostJsonContext.Default.EmptyParams), grace).ConfigureAwait(false);
            await process.Exited.WaitAsync(TimeSpan.FromTicks(Math.Max(0, (grace - clock.Elapsed).Ticks))).ConfigureAwait(false);
        }
        catch (Exception e) when (e is TimeoutException or ModHostRpcException or ModHostClosedException)
        {
            // Whatever the host did, it is killed below.
        }

        Kill();
    }

    public void Kill() => process.Kill();

    public async ValueTask DisposeAsync()
    {
        Kill();
        await rpc.DisposeAsync().ConfigureAwait(false);
        process.Dispose();
    }
}

/// <summary>Starts mod host processes and checks they speak protocol 1.</summary>
public sealed class ModHostConnectionFactory(ILogger<ModHostConnectionFactory> logger) : IModHostConnectionFactory
{
    private const int Protocol = 1;
    private static readonly TimeSpan InitializeTimeout = TimeSpan.FromSeconds(10);

    public async Task<IModHostConnection> StartAsync(ModHostLaunch launch, IModHostCalls calls, CancellationToken ct)
    {
        if (!File.Exists(launch.BunPath))
            throw new ModHostNotReadyException($"Bun isn't installed where Fleet expects it ({launch.BunPath}).");
        if (!File.Exists(launch.HostScript))
            throw new ModHostNotReadyException($"The mod host's script (host.js) is missing ({launch.HostScript}).");

        ModHostProcess process;
        try
        {
            process = ModHostProcess.Start(launch, logger);
        }
        catch (Exception e) when (e is Win32Exception or IOException or UnauthorizedAccessException or InvalidOperationException)
        {
            throw new ModHostNotReadyException($"The mod host couldn't be started: {e.Message}");
        }

        var rpc = new ModHostRpc(process.StandardOutput, process.StandardInput, calls, logger);
        Exception? problem = null;
        var started = false;
        try
        {
            var parameters = JsonSerializer.SerializeToElement(new InitializeParams(Protocol, launch.FleetVersion), ModHostJsonContext.Default.InitializeParams);
            var answer = await rpc.RequestAsync("initialize", parameters, InitializeTimeout).WaitAsync(ct).ConfigureAwait(false);
            var host = answer.Deserialize(ModHostJsonContext.Default.ModHostInitializeResult);
            if (host is { Protocol: Protocol })
            {
                started = true;
                return new ModHostConnection(process, rpc, host);
            }

            problem = new ModHostNotReadyException(host is null
                ? "The mod host answered initialize with nothing."
                : $"The mod host speaks protocol {host.Protocol}; this Fleet needs protocol {Protocol}.");
        }
        catch (ModHostRpcException e)
        {
            problem = new ModHostNotReadyException($"The mod host refused to start: {e.Message}");
        }
        catch (TimeoutException)
        {
            problem = new ModHostNotReadyException($"The mod host didn't answer within {InitializeTimeout.TotalSeconds:0} seconds of starting.");
        }
        catch (JsonException)
        {
            problem = new ModHostNotReadyException("The mod host answered initialize with something Fleet doesn't understand.");
        }
        catch (ModHostClosedException)
        {
            // Let the process's last words arrive: Exited completes once its stderr is read.
            await Task.WhenAny(process.Exited, Task.Delay(TimeSpan.FromSeconds(2), CancellationToken.None)).ConfigureAwait(false);
            problem = new ModHostNotReadyException(process.LastStderrLine is { } last
                ? $"The mod host stopped while starting: {last}"
                : "The mod host stopped while starting.");
        }
        finally
        {
            if (!started)
            {
                process.Kill();
                await rpc.DisposeAsync().ConfigureAwait(false);
                await Task.WhenAny(process.Exited, Task.Delay(TimeSpan.FromSeconds(2), CancellationToken.None)).ConfigureAwait(false);
                process.Dispose();
            }
        }

        throw problem!;
    }
}
