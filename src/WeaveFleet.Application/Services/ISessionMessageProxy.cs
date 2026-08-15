using WeaveFleet.Domain.Events;
using WeaveFleet.Domain.Harnesses;

namespace WeaveFleet.Application.Services;

/// <summary>
/// Proxy for retrieving session messages from either the live harness (if available)
/// or the persisted message store (fallback).
/// </summary>
public interface ISessionMessageProxy
{
    /// <summary>
    /// Builds a full session snapshot including messages, delegations, and activity status.
    /// For opencode sessions with a live harness, fetches messages directly from the harness.
    /// Otherwise, falls back to persisted messages.
    /// </summary>
    /// <param name="fleetSessionId">The Fleet session identifier.</param>
    /// <param name="pageSize">Number of messages to include in the snapshot.</param>
    /// <param name="cursor">Opaque cursor for pagination (null for most recent page).</param>
    /// <param name="ct">Cancellation token.</param>
    /// <returns>A fully materialized session snapshot.</returns>
    Task<SessionSnapshot> GetSnapshotAsync(string fleetSessionId, int pageSize = 100, string? cursor = null, CancellationToken ct = default);

    /// <summary>
    /// Retrieves paginated messages for a session.
    /// For opencode sessions with a live harness, fetches messages directly from the harness.
    /// Otherwise, falls back to persisted messages.
    /// </summary>
    /// <param name="fleetSessionId">The Fleet session identifier.</param>
    /// <param name="limit">Maximum number of messages to return.</param>
    /// <param name="before">Cursor identifying the oldest message from a previous page.</param>
    /// <param name="ct">Cancellation token.</param>
    /// <returns>A page of messages with a continuation flag.</returns>
    Task<MessagePage> GetMessagesAsync(string fleetSessionId, int? limit = null, string? before = null, CancellationToken ct = default);
}
