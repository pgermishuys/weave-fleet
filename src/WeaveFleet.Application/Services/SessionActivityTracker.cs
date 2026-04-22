using System.Collections.Concurrent;

namespace WeaveFleet.Application.Services;

/// <summary>
/// Snapshot of a session's current activity state, captured from ephemeral harness events.
/// </summary>
public sealed record SessionActivitySnapshot(
    string FleetSessionId,
    string ActivityStatus,
    string? UserId,
    DateTimeOffset UpdatedAt);

/// <summary>
/// Thread-safe in-memory tracker for per-session activity status (busy/idle).
/// Registered as a singleton. State is ephemeral — not persisted to the database.
/// Used to send an initial activity state snapshot to new WebSocket subscribers
/// so that page refresh shows the correct busy/idle status immediately.
/// </summary>
/// <remarks>
/// In addition to per-session status, the tracker maintains a parent-child relationship
/// index so that a parent session's effective activity status can be derived from its
/// delegated child sessions. A parent is considered "busy" if it is itself busy
/// <em>or</em> any of its registered child sessions are busy.
/// </remarks>
public sealed class SessionActivityTracker
{
    private readonly ConcurrentDictionary<string, SessionActivitySnapshot> _state = new(StringComparer.Ordinal);

    // child session id → parent session id
    private readonly ConcurrentDictionary<string, string> _childToParent = new(StringComparer.Ordinal);

    // parent session id → set of child session ids
    private readonly ConcurrentDictionary<string, ConcurrentDictionary<string, byte>> _parentToChildren = new(StringComparer.Ordinal);

    /// <summary>
    /// Update (or insert) the activity status for a fleet session.
    /// </summary>
    public void Update(string fleetSessionId, string activityStatus, string? userId)
    {
        _state[fleetSessionId] = new SessionActivitySnapshot(
            fleetSessionId,
            activityStatus,
            userId,
            DateTimeOffset.UtcNow);
    }

    /// <summary>
    /// Remove the tracked state for a fleet session (e.g. on harness disconnect).
    /// Also cleans up any parent-child relationships involving this session.
    /// </summary>
    public void Remove(string fleetSessionId)
    {
        _state.TryRemove(fleetSessionId, out _);

        // Remove as a child (if it was one)
        UnregisterChild(fleetSessionId);

        // Remove as a parent — unlink all known children
        if (_parentToChildren.TryRemove(fleetSessionId, out var children))
        {
            foreach (var childId in children.Keys)
                _childToParent.TryRemove(childId, out _);
        }
    }

    /// <summary>
    /// Register a parent-child delegation relationship so that parent busy state
    /// can be derived from child activity.
    /// </summary>
    public void RegisterChild(string childSessionId, string parentSessionId)
    {
        _childToParent[childSessionId] = parentSessionId;
        _parentToChildren
            .GetOrAdd(parentSessionId, _ => new ConcurrentDictionary<string, byte>(StringComparer.Ordinal))
            .TryAdd(childSessionId, 0);
    }

    /// <summary>
    /// Remove a previously registered parent-child delegation relationship
    /// (e.g. when the delegation completes, fails, or is cancelled).
    /// </summary>
    public void UnregisterChild(string childSessionId)
    {
        if (_childToParent.TryRemove(childSessionId, out var parentSessionId))
        {
            if (_parentToChildren.TryGetValue(parentSessionId, out var children))
                children.TryRemove(childSessionId, out _);
        }
    }

    /// <summary>
    /// Returns the parent session id for a registered child, or <c>null</c> if the
    /// session is not a known child.
    /// </summary>
    public string? GetParentSessionId(string childSessionId)
        => _childToParent.TryGetValue(childSessionId, out var parentId) ? parentId : null;

    /// <summary>
    /// Returns the effective activity status for <paramref name="sessionId"/>.
    /// Returns <c>"busy"</c> if the session itself is busy <em>or</em> any registered
    /// child session is busy. Returns <c>null</c> if the session is not tracked.
    /// </summary>
    public string? GetEffectiveActivityStatus(string sessionId)
    {
        var own = Get(sessionId);
        if (own?.ActivityStatus == "busy")
            return "busy";

        if (_parentToChildren.TryGetValue(sessionId, out var children))
        {
            foreach (var childId in children.Keys)
            {
                var child = Get(childId);
                if (child?.ActivityStatus == "busy")
                    return "busy";
            }
        }

        return own?.ActivityStatus;
    }

    /// <summary>
    /// Returns a snapshot of all currently tracked session activity states.
    /// </summary>
    public IReadOnlyDictionary<string, SessionActivitySnapshot> GetAll()
        => _state;

    /// <summary>
    /// Returns the activity snapshot for a single session, or <c>null</c> if not tracked.
    /// </summary>
    public SessionActivitySnapshot? Get(string fleetSessionId)
        => _state.TryGetValue(fleetSessionId, out var snapshot) ? snapshot : null;
}
