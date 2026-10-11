using System.Text.Json;
using WeaveFleet.Application.Runtimes;

namespace WeaveFleet.Application.Mods.Host;

/// <summary>
/// Fleet's side of the mod host (<c>docs/mods/api.md</c>, "The protocol"). One Bun process per user runs that user's
/// mods: it's started when one of their mods should run, stopped when none should, and restarted when it dies or stops
/// answering. Every method takes the user, since mod ids (<c>name@v3</c>) carry none.
/// </summary>
public interface IModHost
{
    /// <summary>Where the user's host stands.</summary>
    ModHostStatus GetStatus(string userId);

    /// <summary>Starts or stops the user's host to match the Mods switch, safe mode and the store, and loads or unloads mods to match.</summary>
    Task EnsureAsync(string userId, CancellationToken ct = default);

    /// <summary>
    /// Runs one event through the chain for the session (<see cref="ModRouting.ChainFor"/>). Answers
    /// <see cref="ModDispatchResult.NotDispatched"/> without crossing the pipe when no mod hooks it (control events always
    /// cross), when the host isn't running, or when it didn't answer in time (it's then restarted).
    /// </summary>
    Task<ModDispatchResult> DispatchAsync(string userId, ModDispatchRequest request, CancellationToken ct = default);

    /// <summary>
    /// The static check of <paramref name="folder"/> (a staged copy): the <c>CheckReport</c>. Starts the host for it if the
    /// switch is on (a host up only for checks loads no mod). Throws <see cref="ModHostNotReadyException"/> when no host
    /// can run or it didn't answer in time.
    /// </summary>
    Task<JsonElement> CheckAsync(string userId, string folder, CancellationToken ct = default);

    /// <summary>Stages the session's draft again and reloads it: what saving a draft does, called by the agent's tools.</summary>
    Task ReloadDraftAsync(string userId, string sessionId, string name, CancellationToken ct = default);

    /// <summary>The session is archived or gone: the host drops its <c>$.state</c>, timers and handles.</summary>
    Task ForgetSessionAsync(string userId, string sessionId, CancellationToken ct = default);

    /// <summary>Why the host last refused to load <paramref name="modId"/>; null when it loaded.</summary>
    ModLoadProblem? GetLoadProblem(string userId, string modId);
}

/// <summary>The host's states. Sent to clients as <c>state</c>.</summary>
public static class ModHostStates
{
    /// <summary>Not running, and nothing needs it.</summary>
    public const string Stopped = "stopped";

    public const string Starting = "starting";

    public const string Running = "running";

    /// <summary>The host died or stopped answering; Fleet starts it again after a wait.</summary>
    public const string Restarting = "restarting";

    /// <summary>A mod should run but the host can't: no Bun, no <c>host.js</c>, or it refused to start. See the reason.</summary>
    public const string NotReady = "not-ready";
}

/// <param name="State">One of <see cref="ModHostStates"/>.</param>
/// <param name="Reason">Why it's stopped, not ready or restarting, in a sentence; null when running.</param>
/// <param name="Restarts">How many times Fleet has restarted the host since Fleet started.</param>
public sealed record ModHostStatus(string State, string? Reason, int? ProcessId, string? BunPath, string? HostVersion, int Restarts)
{
    public static ModHostStatus Stopped { get; } = new(ModHostStates.Stopped, null, null, null, null, 0);
}

/// <summary>No mod host can run for the user now; the message says why, for the user.</summary>
public sealed class ModHostNotReadyException(string message) : Exception(message);

/// <summary>
/// One running host process, initialised with protocol 1. <see cref="RequestAsync"/> sends a <c>FleetToHost</c> request;
/// requests the host sends back go to the <see cref="IModHostCalls"/> it was started with.
/// </summary>
public interface IModHostConnection : IAsyncDisposable
{
    int ProcessId { get; }

    /// <summary>What <c>initialize</c> answered.</summary>
    ModHostInitializeResult Host { get; }

    /// <summary>Completes with the exit code when the process has exited.</summary>
    Task<int> Exited { get; }

    /// <summary>
    /// Sends a request and returns its <c>result</c>. Timed from the moment it's sent, writing included, whatever the
    /// caller does: no answer within <paramref name="timeout"/> throws <see cref="TimeoutException"/>. An error answer
    /// throws <see cref="ModHostRpcException"/>; a closed pipe <see cref="ModHostClosedException"/>.
    /// </summary>
    Task<JsonElement> RequestAsync(string method, JsonElement parameters, TimeSpan timeout);

    /// <summary>Sends <c>shutdown</c> and kills the process if it hasn't exited after <paramref name="grace"/>, never waiting longer.</summary>
    Task ShutdownAsync(TimeSpan grace);

    /// <summary>Kills the process and anything it started, now.</summary>
    void Kill();
}

/// <summary>Starts host processes.</summary>
public interface IModHostConnectionFactory
{
    /// <summary>
    /// Runs <c>{bun} {host.js} --stdio</c> with an empty environment in <see cref="ModHostLaunch.WorkingDirectory"/>, in
    /// Fleet's process group, with its stderr in Fleet's log, and sends <c>initialize</c> with protocol 1. Throws
    /// <see cref="ModHostNotReadyException"/> when it won't start or speaks another protocol (it's killed first).
    /// </summary>
    Task<IModHostConnection> StartAsync(ModHostLaunch launch, IModHostCalls calls, CancellationToken ct);
}

/// <param name="UserKey">Who the host is for, as Fleet's log names it (never the raw user id).</param>
public sealed record ModHostLaunch(string BunPath, string HostScript, string WorkingDirectory, string FleetVersion, string UserKey);

public sealed record ModHostInitializeResult(int Protocol, string HostVersion, string BunVersion);

/// <summary>What a host asks of Fleet: its <c>HostToFleet</c> requests and <c>HostNotifications</c>.</summary>
public interface IModHostCalls
{
    /// <summary>Answers a request. Throw <see cref="ModHostRpcException"/> to answer with an error.</summary>
    Task<JsonElement> HandleRequestAsync(string method, JsonElement parameters, CancellationToken ct);

    /// <summary>Receives a notification. Must not throw.</summary>
    void HandleNotification(string method, JsonElement parameters);
}

/// <summary>JSON-RPC error codes the protocol uses.</summary>
public static class ModHostErrorCodes
{
    public const int ParseError = -32700;
    public const int InvalidRequest = -32600;
    public const int MethodNotFound = -32601;
    public const int InvalidParams = -32602;
    public const int Internal = -32603;

    /// <summary><c>initialize</c> with a protocol the host doesn't speak.</summary>
    public const int Protocol = -32000;

    /// <summary><c>load</c> of a mod that doesn't load; <c>data</c> is the <c>CheckReport</c>.</summary>
    public const int NotLoaded = -32001;
}

/// <summary>A JSON-RPC error answer, either way.</summary>
public sealed class ModHostRpcException(int code, string message, JsonElement? data = null) : Exception(message)
{
    public int Code { get; } = code;

    public new JsonElement? Data { get; } = data;
}

/// <summary>The pipe to the host closed before an answer came.</summary>
public sealed class ModHostClosedException(string message) : Exception(message);

/// <summary>Whether a user's mods may run, read outside a request.</summary>
public interface IModUserGate
{
    Task<bool> IsSwitchedOnAsync(string userId, CancellationToken ct);

    bool IsSafeMode(string userId);
}

/// <summary>The Bun the host runs on. Found, never installed here (that's the Mods switch's job).</summary>
public interface IModHostBun
{
    /// <summary>The Bun to run now, or null when there's none yet.</summary>
    Task<BunLocation?> FindAsync(CancellationToken ct);
}

/// <summary>Where the host's script is, and Fleet's version for <c>initialize</c>.</summary>
public interface IModHostFiles
{
    /// <summary><c>{app}/mods-host/host.js</c>, or <c>mods/host/dist/host.js</c> when running from the repository; null when neither exists.</summary>
    string? HostScript { get; }

    string FleetVersion { get; }
}
