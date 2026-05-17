using System.Data;
using WeaveFleet.Domain.Entities;

namespace WeaveFleet.Domain.Repositories;

public interface ISmartLinkRepository
{
    Task<IReadOnlyList<SmartLink>> ListBySessionIdAsync(string sessionId);
    Task<IReadOnlyList<SmartLink>> ListActiveBySessionIdAsync(string sessionId);
    Task<SmartLink?> GetBySessionIdAndUrlAsync(string sessionId, string url);
    Task UpsertAsync(SmartLink smartLink);
    Task DismissAsync(string id);

    /// <summary>
    /// Deletes all smart links belonging to a session.
    /// </summary>
    Task DeleteBySessionIdAsync(string sessionId);

    /// <summary>
    /// Deletes all smart links belonging to a session (transactional overload).
    /// </summary>
    Task DeleteBySessionIdAsync(IDbConnection connection, IDbTransaction? transaction, string sessionId);

    /// <summary>
    /// Deletes orphaned smart links whose sessions no longer exist.
    /// Used at startup for reconciliation.
    /// </summary>
    Task DeleteOrphanedAsync(CancellationToken ct);

    /// <summary>
    /// Returns all non-dismissed, non-terminal pull request smart links whose sessions are running.
    /// Used by the CI watcher background service.
    /// </summary>
    Task<IReadOnlyList<SmartLink>> ListNonTerminalPrLinksAsync(CancellationToken ct);

    /// <summary>
    /// Updates the metadata of a smart link by id, bypassing user-scoping.
    /// Used by the CI watcher background service.
    /// </summary>
    Task UpdateMetadataAsync(string id, string metadataJson, CancellationToken ct);
}
