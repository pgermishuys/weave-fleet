using System.Collections.Concurrent;

namespace WeaveFleet.Application.Mods;

/// <summary>A keep request as the draft card shows it: the agent's note and when it asked.</summary>
public sealed record ModKeepRequestView(string? Note, DateTimeOffset At);

/// <summary>How a keep request ended. <see cref="Superseded"/> is a newer request replacing it, or the request being cleared.</summary>
public enum ModKeepOutcome
{
    Kept,
    Declined,
    Superseded,
}

/// <param name="Outcome">How the request ended.</param>
/// <param name="Version">The version number the draft was kept as, for <see cref="ModKeepOutcome.Kept"/>.</param>
public sealed record ModKeepDecision(ModKeepOutcome Outcome, int? Version = null);

/// <summary>One request the agent made. The waiting tool call holds it and awaits <see cref="Decision"/>.</summary>
public sealed class ModKeepRequest
{
    private readonly TaskCompletionSource<ModKeepDecision> _decision = new(TaskCreationOptions.RunContinuationsAsynchronously);

    internal ModKeepRequest(string? note, DateTimeOffset at)
    {
        Note = note;
        At = at;
    }

    public string? Note { get; }

    public DateTimeOffset At { get; }

    public Task<ModKeepDecision> Decision => _decision.Task;

    internal void Complete(ModKeepDecision decision) => _decision.TrySetResult(decision);
}

/// <summary>
/// The agent's asks to keep a draft, in memory, one per user, session and draft name: a new request replaces the old one.
/// The user keeping it, turning it off or declining it ends the request. Nothing here holds a lock while a tool call waits.
/// </summary>
public sealed class ModKeepRequests(TimeProvider clock)
{
    private readonly ConcurrentDictionary<(string User, string Session, string Name), ModKeepRequest> _requests = new();

    /// <summary>Records the request, ending the one it replaces.</summary>
    public ModKeepRequest Request(string userId, string sessionId, string name, string? note)
    {
        var request = new ModKeepRequest(note, clock.GetUtcNow());
        ModKeepRequest? previous = null;
        _requests.AddOrUpdate(
            (userId, sessionId, name),
            request,
            (_, old) =>
            {
                previous = old;
                return request;
            });
        previous?.Complete(new ModKeepDecision(ModKeepOutcome.Superseded));
        return request;
    }

    public ModKeepRequestView? Get(string userId, string sessionId, string name)
        => _requests.TryGetValue((userId, sessionId, name), out var request) ? new ModKeepRequestView(request.Note, request.At) : null;

    /// <summary>Ends the request with the user's decision. Without a request, nothing happens.</summary>
    public void Resolve(string userId, string sessionId, string name, ModKeepDecision decision)
    {
        if (_requests.TryRemove((userId, sessionId, name), out var request))
            request.Complete(decision);
    }

    /// <summary>Drops the request; a tool call waiting on it stops waiting.</summary>
    public void Clear(string userId, string sessionId, string name)
        => Resolve(userId, sessionId, name, new ModKeepDecision(ModKeepOutcome.Superseded));

    /// <summary>The decision, or null when none came within <paramref name="timeout"/> (counted on the provider's clock).</summary>
    public async Task<ModKeepDecision?> WaitAsync(ModKeepRequest request, TimeSpan timeout, CancellationToken ct = default)
    {
        try
        {
            return await request.Decision.WaitAsync(timeout, clock, ct).ConfigureAwait(false);
        }
        catch (TimeoutException)
        {
            return null;
        }
    }
}
