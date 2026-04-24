using WeaveFleet.Domain.Common;

namespace WeaveFleet.Application.Services;

public interface IBoardSyncService
{
    Task<Result<BoardSyncResult>> SyncAsync(string boardId);
    Task<Result<BoardSyncResult>> SyncAsync(string boardId, CancellationToken cancellationToken);
}

public sealed record BoardSyncResult(
    int SourcesProcessed,
    int IssuesFetched,
    int CardsCreated,
    int CardsUpdated,
    int CardsMarkedStale,
    string SyncedAt);
