using System.Text.Json;

namespace WeaveFleet.Application.Mods.Host;

/// <summary>
/// Fleet's side of the mod host (<c>docs/mods/api.md</c>, "The protocol"). One Bun process per user runs that user's kept
/// mods and drafts; this starts it when it's needed, keeps it in step with the store, and runs events through it.
/// Every method takes the user explicitly: mod ids (<c>name@v3</c>) carry no user, so users never share a host.
/// </summary>
public interface IModHost
{
    /// <summary>Where the user's host stands.</summary>
    ModHostStatus GetStatus(string userId);

    /// <summary>
    /// Brings the user's host in line with the store and the Mods switch: starts or stops it, and loads or unloads mods.
    /// Fleet calls this itself on <c>mods.changed</c>; callers only need it to wait until a change has been applied.
    /// </summary>
    Task EnsureAsync(string userId, CancellationToken ct = default);

    /// <summary>
    /// Runs the static check on <paramref name="folder"/> (a staged copy, never a live draft) and returns the
    /// <c>CheckReport</c>. Starts the host for the check if it isn't running and the Mods switch is on. Throws
    /// <see cref="ModHostNotReadyException"/> when no host can run (no Bun, Mods off, the host won't start).
    /// </summary>
    Task<JsonElement> CheckAsync(string userId, string folder, CancellationToken ct = default);

    /// <summary>
    /// Runs one event through the chain Fleet computes for the session (kept mods by name, then the session's drafts by
    /// name, a draft replacing its kept mod there), narrowed to mods whose registered hooks match the event. Answers
    /// <see cref="ModDispatchResult.NotDispatched"/> without crossing the pipe when no mod matches (control events are
    /// always sent: the drawing mod's callback ends their chain), when the host isn't running, or when the host failed
    /// to answer within the 15 s limit (it is then restarted and the suspects struck).
    /// </summary>
    Task<ModDispatchResult> DispatchAsync(string userId, ModDispatchRequest request, CancellationToken ct = default);

    /// <summary>The session is archived or gone: the host drops its <c>$.state</c>, timers and handles.</summary>
    Task ForgetSessionAsync(string userId, string sessionId, CancellationToken ct = default);

    /// <summary>Why the host last refused to load <paramref name="modId"/>, with the report; null when it loaded.</summary>
    ModLoadProblem? GetLoadProblem(string userId, string modId);

    /// <summary>The mod's last log lines (<c>$.ui.log</c>, <c>console</c>, failures), oldest first.</summary>
    IReadOnlyList<ModLogLine> GetLog(string userId, string modId);
}

/// <summary>The host's states. Sent to clients as <c>state</c>.</summary>
public static class ModHostStates
{
    /// <summary>Not running, and nothing needs it (Mods off, safe mode, no mod kept or drafted).</summary>
    public const string Stopped = "stopped";

    /// <summary>Starting: the process is up and Fleet is initialising it and loading mods.</summary>
    public const string Starting = "starting";

    public const string Running = "running";

    /// <summary>The host died or hung; Fleet is waiting out the backoff before starting it again.</summary>
    public const string Restarting = "restarting";

    /// <summary>Something needs the host but it can't run: no Bun, no <c>host.js</c>, or it refused to start. See the reason.</summary>
    public const string NotReady = "not-ready";
}

/// <param name="State">One of <see cref="ModHostStates"/>.</param>
/// <param name="Reason">Why it's stopped, not ready or restarting, in a sentence; null when running.</param>
/// <param name="Loaded">The ids loaded now (<c>name@v3</c>, <c>name@draft:{sessionId}</c>), sorted.</param>
/// <param name="Restarts">How many times Fleet has restarted the host since Fleet started.</param>
/// <param name="LastExit">How the host last stopped without Fleet asking; null if it never has.</param>
public sealed record ModHostStatus(
    string State,
    string? Reason,
    int? ProcessId,
    string? BunPath,
    string? BunVersion,
    string? HostVersion,
    IReadOnlyList<string> Loaded,
    int Restarts,
    DateTimeOffset? StartedAt,
    ModHostExit? LastExit)
{
    public static ModHostStatus StoppedWith(string? reason) => new(ModHostStates.Stopped, reason, null, null, null, null, [], 0, null, null);
}

/// <summary>How the host stopped when Fleet didn't ask it to.</summary>
/// <param name="ExitCode">The process's exit code; null when Fleet killed it after a hang.</param>
/// <param name="Reason">What happened, in a sentence: "exited with code 1", "didn't answer a dispatch within 15 s".</param>
public sealed record ModHostExit(DateTimeOffset At, int? ExitCode, string Reason);

/// <summary>One event for one session.</summary>
/// <param name="Event"><c>session.start</c>, <c>turn.complete</c>, <c>ui.render</c>, <c>ui.press</c>, <c>ui.input</c> or <c>ui.select</c>.</param>
/// <param name="E">The event as mods see it, before any hook. Control events carry the control's <c>handle</c>.</param>
/// <param name="Surface"><c>desktop</c> or <c>phone</c>, for control events.</param>
public sealed record ModDispatchRequest(string Event, string SessionId, JsonElement E, string? Surface = null);

/// <param name="Dispatched">False when nothing crossed the pipe or the host didn't answer: draw Fleet's own.</param>
/// <param name="Result">The chain's result: for <c>ui.render</c> a <c>WireElement</c>, <c>null</c>, or <c>{ "type": "Fleet" }</c>.</param>
/// <param name="DrawnBy">Which mods' trees are in a <c>ui.render</c> result, for Draft marking.</param>
/// <param name="Failures">Hooks that failed in this dispatch.</param>
public sealed record ModDispatchResult(bool Dispatched, JsonElement? Result, IReadOnlyList<string> DrawnBy, IReadOnlyList<ModHookFailure> Failures)
{
    public static ModDispatchResult NotDispatched { get; } = new(false, null, [], []);
}

/// <summary>A hook, timer or callback that failed (<c>HookFailureReport</c> on the wire, plus the session for <c>failed</c>).</summary>
/// <param name="Kind"><c>throw</c> or <c>timeout</c>.</param>
/// <param name="Strikes">Failures in a row for the mod, after this one, as the host counted them.</param>
public sealed record ModHookFailure(string Mod, string Event, string Kind, string Message, int Strikes, string? SessionId = null);

/// <summary>A line in a mod's log.</summary>
/// <param name="Level"><c>info</c>, <c>warn</c> or <c>error</c>.</param>
public sealed record ModLogLine(DateTimeOffset At, string Level, string Text, string? SessionId);

/// <summary>A load the host refused (error -32001): the message and the check report it sent, for the draft card.</summary>
public sealed record ModLoadProblem(string Message, JsonElement? Report, DateTimeOffset At);

/// <summary>No mod host can run for the user now; the message says why, for the user.</summary>
public sealed class ModHostNotReadyException(string message) : Exception(message);
