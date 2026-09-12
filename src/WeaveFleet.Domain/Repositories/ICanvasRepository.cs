using WeaveFleet.Domain.Entities;

namespace WeaveFleet.Domain.Repositories;

/// <summary>
/// Stores session canvases and their revisions. Every query is scoped to the current user,
/// and every canvas lookup to the session that owns it.
/// </summary>
public interface ICanvasRepository
{
    /// <summary>
    /// Lists a session's canvases, oldest first. Closed canvases are left out unless <paramref name="includeClosed"/> is set.
    /// </summary>
    Task<IReadOnlyList<Canvas>> ListBySessionIdAsync(string sessionId, bool includeClosed = false);

    Task<Canvas?> GetByIdAsync(string sessionId, string canvasId);

    /// <summary>
    /// Returns the session's canvas with exactly this title, open or closed. If several match, the most recently updated wins.
    /// </summary>
    Task<Canvas?> GetByTitleAsync(string sessionId, string title);

    /// <summary>
    /// Inserts a new canvas together with the revision that produced its first version, in one transaction.
    /// Returns <c>false</c> if the session doesn't exist or belongs to another user.
    /// </summary>
    Task<bool> InsertAsync(Canvas canvas, CanvasRevision revision);

    /// <summary>
    /// Writes <paramref name="canvas"/> and <paramref name="revision"/> in one transaction, but only if the stored
    /// version is still <paramref name="expectedVersion"/>. Returns <c>false</c> when another writer got there first;
    /// the caller reloads the canvas and retries against the new state.
    /// <paramref name="canvas"/>.Version and <paramref name="revision"/>.Version must both be <paramref name="expectedVersion"/> + 1.
    /// <c>agent_seen_version</c> never moves backwards.
    /// </summary>
    Task<bool> TryUpdateAsync(Canvas canvas, int expectedVersion, CanvasRevision revision);

    /// <summary>
    /// Records that the agent has seen the canvas at <paramref name="version"/> without changing it. Never moves backwards.
    /// </summary>
    Task MarkAgentSeenAsync(string sessionId, string canvasId, int version);

    /// <summary>
    /// Closes the canvas, or reopens it when <paramref name="closedAt"/> is <c>null</c>. Returns <c>false</c> if it wasn't found.
    /// </summary>
    Task<bool> SetClosedAtAsync(string sessionId, string canvasId, string? closedAt);

    /// <summary>
    /// Lists a canvas's revisions after <paramref name="afterVersion"/>, oldest first.
    /// </summary>
    Task<IReadOnlyList<CanvasRevision>> ListRevisionsAsync(string canvasId, int afterVersion);
}
