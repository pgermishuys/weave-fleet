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
    /// Returns all non-dismissed, non-terminal pull request smart links across all users.
    /// Used by the CI watcher background service.
    /// </summary>
    Task<IReadOnlyList<SmartLink>> ListNonTerminalPrLinksAsync(CancellationToken ct);

    /// <summary>
    /// Updates the metadata of a smart link by id, bypassing user-scoping.
    /// Used by the CI watcher background service.
    /// </summary>
    Task UpdateMetadataAsync(string id, string metadataJson, CancellationToken ct);
}
