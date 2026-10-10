using System.Globalization;
using System.Text.Json;

namespace WeaveFleet.Application.Mods.Host;

/// <summary>
/// One running mod host process, initialised with protocol 1: the <c>FleetToHost</c> requests of the
/// <c>fleet-mods/protocol</c> types, typed. Requests the host sends back go to the <see cref="IModHostCalls"/> it was
/// started with. Errors the host answers with are <see cref="ModHostRpcException"/>; a closed pipe or a dead process is
/// <see cref="ModHostClosedException"/>.
/// </summary>
public interface IModHostConnection : IAsyncDisposable
{
    int ProcessId { get; }

    /// <summary>What <c>initialize</c> answered.</summary>
    ModHostInitializeResult Host { get; }

    /// <summary>Completes when the process has exited, with its exit code.</summary>
    Task<int> Exited { get; }

    Task<JsonElement> CheckAsync(string root, string manifest, CancellationToken ct);

    /// <summary>Error -32001 (<see cref="ModHostErrorCodes.NotLoaded"/>) carries the check report in <see cref="ModHostRpcException.Data"/>.</summary>
    Task<ModLoadResult> LoadAsync(ModLoadParams load, CancellationToken ct);

    Task UnloadAsync(string id, CancellationToken ct);

    /// <summary>Throws <see cref="TimeoutException"/> when the host hasn't answered within <paramref name="timeout"/>.</summary>
    Task<ModWireDispatchResult> DispatchAsync(ModWireDispatch dispatch, TimeSpan timeout, CancellationToken ct);

    Task ForgetAsync(string sessionId, CancellationToken ct);

    /// <summary>Sends <c>shutdown</c>, waits up to <paramref name="grace"/> for the process to exit, then kills it.</summary>
    Task ShutdownAsync(TimeSpan grace);

    /// <summary>Kills the process (and anything it started) now. For a host that stopped answering.</summary>
    void Kill();
}

/// <summary>Starts mod host processes.</summary>
public interface IModHostConnectionFactory
{
    /// <summary>
    /// Runs <c>{bun} {host.js} --stdio</c> with an empty environment in <see cref="ModHostLaunch.WorkingDirectory"/>, in
    /// Fleet's process group so it dies with Fleet, with its stderr in Fleet's log; then sends <c>initialize</c> with
    /// protocol 1. Throws <see cref="ModHostNotReadyException"/> when it won't start, or answers with another protocol
    /// (the process is killed first).
    /// </summary>
    Task<IModHostConnection> StartAsync(ModHostLaunch launch, IModHostCalls calls, CancellationToken ct);
}

/// <param name="UserKey">Who the host is for, as Fleet's log should name it (never the raw user id).</param>
public sealed record ModHostLaunch(string BunPath, string HostScript, string WorkingDirectory, string FleetVersion, string UserKey);

/// <summary>What a host asks of Fleet: its <c>HostToFleet</c> requests and <c>HostNotifications</c>.</summary>
public interface IModHostCalls
{
    /// <summary>Answers a request (<c>store.get</c>, <c>session.get</c>, <c>ui.open</c>…). Throw <see cref="ModHostRpcException"/> to answer with an error.</summary>
    Task<JsonElement> HandleRequestAsync(string method, JsonElement parameters, CancellationToken ct);

    /// <summary>Receives a notification (<c>invalidate</c>, <c>log</c>, <c>failed</c>). Must not throw.</summary>
    void HandleNotification(string method, JsonElement parameters);
}

/// <summary>JSON-RPC error codes the protocol uses.</summary>
public static class ModHostErrorCodes
{
    public const int InvalidParams = -32602;
    public const int MethodNotFound = -32601;
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

/// <summary>The pipe to the host closed (the process exited or was killed) before an answer came.</summary>
public sealed class ModHostClosedException(string message) : Exception(message);

// ── Protocol 1 payloads (fleet-mods/protocol). camelCase on the wire; nulls left out. ──

public sealed record ModHostInitializeResult(int Protocol, string HostVersion, string BunVersion);

/// <param name="Version">Fleet's version number for a kept mod, or the string <c>draft</c>.</param>
/// <param name="SessionId">The draft's session; null for a kept mod.</param>
/// <param name="Root">The mod's folder, holding <c>mod.json</c>.</param>
public sealed record ModLoadParams(string Id, string Name, JsonElement Version, string? SessionId, string Root)
{
    public static ModLoadParams Kept(string name, int number, string root)
        => new(ModIds.Kept(name, number), name, Json(number.ToString(CultureInfo.InvariantCulture)), null, root);

    public static ModLoadParams Draft(string name, string sessionId, string root)
        => new(ModIds.Draft(name, sessionId), name, Json("\"draft\""), sessionId, root);

    private static JsonElement Json(string text)
    {
        using var document = JsonDocument.Parse(text);
        return document.RootElement.Clone();
    }
}

/// <param name="Check">The <c>CheckReport</c>.</param>
/// <param name="Hooks">What <c>register</c> registered, in order: Fleet routes with these.</param>
public sealed record ModLoadResult(JsonElement Check, IReadOnlyList<ModHookSpec> Hooks);

/// <param name="Matcher">The matcher as JSON, a RegExp as <c>{ "$regex": source, "flags": flags }</c>; null for none.</param>
public sealed record ModHookSpec(string Event, JsonElement? Matcher);

/// <param name="Mods">The chain, outermost first.</param>
public sealed record ModWireDispatch(string Event, string SessionId, JsonElement E, IReadOnlyList<string> Mods, string? Surface);

public sealed record ModWireDispatchResult(JsonElement Result, IReadOnlyList<string>? DrawnBy, IReadOnlyList<ModHookFailure> Failures);

/// <summary>Mod ids as the protocol writes them.</summary>
public static class ModIds
{
    public static string Kept(string name, int number) => $"{name}@v{number}";

    public static string Draft(string name, string sessionId) => $"{name}@draft:{sessionId}";

    /// <summary>The name part of an id: what <c>$.store</c> and strikes are recorded under.</summary>
    public static string NameOf(string id) => id.IndexOf('@') is var at and > 0 ? id[..at] : id;

    /// <summary>The session of a draft id; null for a kept id.</summary>
    public static string? DraftSessionOf(string id)
        => id.IndexOf("@draft:", StringComparison.Ordinal) is var at and > 0 ? id[(at + "@draft:".Length)..] : null;
}
