using System.Diagnostics;
using System.Text.Json;
using Microsoft.Extensions.Logging;
using WeaveFleet.Application.Mods.Host;

namespace WeaveFleet.Infrastructure.Mods.Host;

/// <summary>A running mod host: its process and the JSON-RPC peer over its pipes, as the typed requests of the protocol.</summary>
internal sealed class ModHostConnection : IModHostConnection
{
    private static readonly TimeSpan RequestTimeout = TimeSpan.FromSeconds(15);

    private readonly ModHostProcess _process;
    private readonly ModHostRpc _rpc;
    private bool _disposed;

    public ModHostConnection(ModHostProcess process, ModHostRpc rpc, ModHostInitializeResult host)
    {
        _process = process;
        _rpc = rpc;
        Host = host;
    }

    public int ProcessId => _process.ProcessId;

    public ModHostInitializeResult Host { get; }

    public Task<int> Exited => _process.Exited;

    public Task<JsonElement> CheckAsync(string root, string manifest, CancellationToken ct)
        => _rpc.RequestAsync("check", Serialize(new CheckParams(root, manifest), ModHostJsonContext.Default.CheckParams), RequestTimeout, ct);

    public async Task<ModLoadResult> LoadAsync(ModLoadParams load, CancellationToken ct)
    {
        var answer = await _rpc.RequestAsync("load", Serialize(load, ModHostJsonContext.Default.ModLoadParams), RequestTimeout, ct).ConfigureAwait(false);
        return Read(answer, ModHostJsonContext.Default.ModLoadResult, "load");
    }

    public async Task UnloadAsync(string id, CancellationToken ct)
        => await _rpc.RequestAsync("unload", Serialize(new UnloadParams(id), ModHostJsonContext.Default.UnloadParams), RequestTimeout, ct).ConfigureAwait(false);

    public async Task<ModWireDispatchResult> DispatchAsync(ModWireDispatch dispatch, TimeSpan timeout, CancellationToken ct)
    {
        var answer = await _rpc.RequestAsync("dispatch", Serialize(dispatch, ModHostJsonContext.Default.ModWireDispatch), timeout, ct).ConfigureAwait(false);
        return Read(answer, ModHostJsonContext.Default.ModWireDispatchResult, "dispatch");
    }

    public async Task ForgetAsync(string sessionId, CancellationToken ct)
        => await _rpc.RequestAsync("forget", Serialize(new ForgetParams(sessionId), ModHostJsonContext.Default.ForgetParams), RequestTimeout, ct).ConfigureAwait(false);

    public async Task ShutdownAsync(TimeSpan grace)
    {
        var clock = Stopwatch.StartNew();
        try
        {
            await _rpc.RequestAsync("shutdown", Serialize(new EmptyParams(), ModHostJsonContext.Default.EmptyParams), grace, CancellationToken.None).ConfigureAwait(false);
        }
        catch (Exception e) when (e is TimeoutException or ModHostRpcException or ModHostClosedException)
        {
            // A host that won't answer shutdown is killed below.
        }

        var left = grace - clock.Elapsed;
        if (left > TimeSpan.Zero)
        {
            try
            {
                await _process.Exited.WaitAsync(left).ConfigureAwait(false);
            }
            catch (TimeoutException)
            {
                // Still running after the grace.
            }
        }

        Kill();
    }

    public void Kill()
    {
        if (!_process.Exited.IsCompleted)
            _process.Kill();
    }

    public async ValueTask DisposeAsync()
    {
        if (_disposed)
            return;
        _disposed = true;

        Kill();
        await _rpc.DisposeAsync().ConfigureAwait(false);
        await _process.DisposeAsync().ConfigureAwait(false);
    }

    private static JsonElement Serialize<T>(T value, System.Text.Json.Serialization.Metadata.JsonTypeInfo<T> info)
        => JsonSerializer.SerializeToElement(value, info);

    private static T Read<T>(JsonElement answer, System.Text.Json.Serialization.Metadata.JsonTypeInfo<T> info, string method)
    {
        try
        {
            return answer.Deserialize(info) ?? throw new ModHostRpcException(ModHostErrorCodes.Internal, $"The mod host answered {method} with nothing.");
        }
        catch (JsonException e)
        {
            throw new ModHostRpcException(ModHostErrorCodes.Internal, $"The mod host answered {method} with something Fleet doesn't understand: {e.Message}");
        }
    }
}

/// <summary>Starts mod host processes and checks they speak protocol 1.</summary>
public sealed class ModHostConnectionFactory(ILogger<ModHostConnectionFactory> logger) : IModHostConnectionFactory
{
    private const int Protocol = 1;
    private static readonly TimeSpan InitializeTimeout = TimeSpan.FromSeconds(10);
    private static readonly TimeSpan StderrGrace = TimeSpan.FromMilliseconds(500);

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
        catch (Exception e) when (e is System.ComponentModel.Win32Exception or IOException or UnauthorizedAccessException or InvalidOperationException)
        {
            throw new ModHostNotReadyException($"The mod host couldn't be started: {e.Message}");
        }

        var rpc = new ModHostRpc(process.StandardOutput, process.StandardInput, calls, logger);
        try
        {
            var answer = await rpc.RequestAsync("initialize", JsonSerializer.SerializeToElement(new InitializeParams(Protocol, launch.FleetVersion), ModHostJsonContext.Default.InitializeParams), InitializeTimeout, ct).ConfigureAwait(false);
            var host = answer.Deserialize(ModHostJsonContext.Default.ModHostInitializeResult)
                ?? throw new ModHostNotReadyException("The mod host answered initialize with nothing.");
            if (host.Protocol != Protocol)
                throw new ModHostNotReadyException($"The mod host speaks protocol {host.Protocol}; this Fleet needs protocol {Protocol}.");
            return new ModHostConnection(process, rpc, host);
        }
        catch (Exception e)
        {
            var problem = await ProblemOfAsync(e, process).ConfigureAwait(false);
            process.Kill();
            await rpc.DisposeAsync().ConfigureAwait(false);
            await process.DisposeAsync().ConfigureAwait(false);
            if (problem is null)
                throw;
            throw problem;
        }
    }

    /// <summary>The sentence for the user when starting failed with <paramref name="e"/>; null to let <paramref name="e"/> through.</summary>
    private static async Task<Exception?> ProblemOfAsync(Exception e, ModHostProcess process)
    {
        switch (e)
        {
            case ModHostNotReadyException:
                return e;
            case ModHostClosedException:
                // Let the process's last words arrive.
                try
                {
                    await process.StderrDone.WaitAsync(StderrGrace).ConfigureAwait(false);
                }
                catch (TimeoutException)
                {
                }

                return new ModHostNotReadyException(process.LastStderrLine is { } last
                    ? $"The mod host stopped while starting: {last}"
                    : "The mod host stopped while starting.");
            case ModHostRpcException rpc:
                return new ModHostNotReadyException($"The mod host refused to start: {rpc.Message}");
            case TimeoutException:
                return new ModHostNotReadyException($"The mod host didn't answer within {InitializeTimeout.TotalSeconds:0} seconds of starting.");
            case JsonException:
                return new ModHostNotReadyException("The mod host answered initialize with something Fleet doesn't understand.");
            default:
                return null;
        }
    }
}
