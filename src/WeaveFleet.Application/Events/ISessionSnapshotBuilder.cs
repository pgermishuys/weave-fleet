using WeaveFleet.Domain.Events;

namespace WeaveFleet.Application.Events;

/// <summary>
/// Builds a fully materialized snapshot for a Fleet session from persisted state.
/// </summary>
public interface ISessionSnapshotBuilder
{
    /// <summary>
    /// Builds a session snapshot for <paramref name="sessionId"/>.
    /// </summary>
    /// <param name="sessionId">The Fleet session identifier.</param>
    /// <param name="pageSize">
    /// The number of messages to include. When <paramref name="cursor"/> is <see langword="null"/>,
    /// this returns the most recent page.
    /// </param>
    /// <param name="cursor">
    /// An opaque cursor identifying the oldest included message from a previous page.
    /// When supplied, the snapshot contains messages older than that message.
    /// </param>
    /// <returns>The materialized snapshot for the requested session.</returns>
    Task<SessionSnapshot> BuildAsync(string sessionId, int pageSize = 100, string? cursor = null);
}
