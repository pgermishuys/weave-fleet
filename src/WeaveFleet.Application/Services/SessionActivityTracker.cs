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
public sealed class SessionActivityTracker
{
    private readonly ConcurrentDictionary<string, SessionActivitySnapshot> _state = new();

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
    /// </summary>
    public void Remove(string fleetSessionId)
    {
        _state.TryRemove(fleetSessionId, out _);
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
