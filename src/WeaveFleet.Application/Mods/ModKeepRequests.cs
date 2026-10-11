using System.Collections.Concurrent;

namespace WeaveFleet.Application.Mods;

/// <summary>A keep request as the draft card shows it: the agent's note and when it asked.</summary>
public sealed record ModKeepRequestView(string? Note, DateTimeOffset At);

/// <summary>
/// The agent's asks to keep a draft (<c>fleet_mod_keep</c>), in memory, one per user, session and draft name: a new request
/// replaces the old one. The user keeping the draft, turning it off or declining clears it. Fleet restarting forgets them;
/// the agent can ask again.
/// </summary>
public sealed class ModKeepRequests(TimeProvider clock)
{
    private readonly ConcurrentDictionary<(string User, string Session, string Name), ModKeepRequestView> _requests = new();

    public ModKeepRequestView Request(string userId, string sessionId, string name, string? note)
        => _requests[(userId, sessionId, name)] = new ModKeepRequestView(note, clock.GetUtcNow());

    public ModKeepRequestView? Get(string userId, string sessionId, string name) => _requests.GetValueOrDefault((userId, sessionId, name));

    public void Clear(string userId, string sessionId, string name) => _requests.TryRemove((userId, sessionId, name), out _);
}
