using System.Text.Json;

namespace WeaveFleet.Application.Mods;

/// <summary>
/// What the agent's mod tools need from the mod host: loading a draft in its session, sending a sample event through
/// the session's mods, and reading the load problem and log of a mod.
/// </summary>
public interface IModDraftRunner
{
    /// <summary>Why no mod host can run for the user now, in a sentence; null when one can.</summary>
    string? NotReadyReason(string userId);

    Task<ModDraftLoad> ReloadAsync(string userId, string sessionId, string name, CancellationToken ct = default);

    Task<ModDraftTestRun> DispatchAsync(string userId, string sessionId, string eventName, JsonElement e, CancellationToken ct = default);

    /// <summary>The last load problem of the draft; null when it loaded or never tried.</summary>
    ModDraftProblem? LoadProblem(string userId, string sessionId, string name);

    /// <summary>The mod's log, oldest first: the draft's when <paramref name="sessionId"/> is given, the kept mod's otherwise.</summary>
    IReadOnlyList<ModDraftLogLine> Log(string userId, string name, string? sessionId);
}

/// <summary>The answer to loading a draft: whether it loaded, why not, and what <c>register</c> registered.</summary>
public sealed record ModDraftLoad(bool Loaded, string? Message, JsonElement? Report, JsonElement Hooks);

/// <summary>The answer to a sample event: whether a hook matched, the tree or value that came back, the mods that drew it, and the failures.</summary>
public sealed record ModDraftTestRun(bool Dispatched, JsonElement? Result, IReadOnlyList<string> DrawnBy, IReadOnlyList<ModDraftFailure> Failures);

public sealed record ModDraftFailure(string Mod, string Event, string Kind, string Message, int Strikes);

public sealed record ModDraftProblem(string Message, JsonElement? Report, DateTimeOffset At);

public sealed record ModDraftLogLine(DateTimeOffset At, string Level, string Text);

/// <summary>The runner until the mod host is part of this Fleet: never ready, so the tools that need it say so.</summary>
public sealed class NoModDraftRunner : IModDraftRunner
{
    public string? NotReadyReason(string userId) => "the mod host isn't part of this Fleet yet.";

    public Task<ModDraftLoad> ReloadAsync(string userId, string sessionId, string name, CancellationToken ct = default)
        => throw new InvalidOperationException(NotReadyReason(userId));

    public Task<ModDraftTestRun> DispatchAsync(string userId, string sessionId, string eventName, JsonElement e, CancellationToken ct = default)
        => throw new InvalidOperationException(NotReadyReason(userId));

    public ModDraftProblem? LoadProblem(string userId, string sessionId, string name) => null;

    public IReadOnlyList<ModDraftLogLine> Log(string userId, string name, string? sessionId) => [];
}
