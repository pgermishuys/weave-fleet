using WeaveFleet.Domain.Entities;

namespace WeaveFleet.Domain.Repositories;

public interface IBoardRepository
{
    Task<Board?> GetByIdAsync(string id, string userId);
    Task<IReadOnlyList<Board>> ListByUserAsync(string userId);
    Task InsertAsync(Board board);
    Task UpdateAsync(Board board);
    Task<bool> DeleteAsync(string id, string userId);

    Task<BoardLane?> GetLaneByIdAsync(string boardId, string laneId, string userId);
    Task<IReadOnlyList<BoardLane>> ListLanesAsync(string boardId, string userId);
    Task InsertLaneAsync(BoardLane lane);
    Task UpdateLaneAsync(BoardLane lane);
    Task<bool> DeleteLaneAsync(string boardId, string laneId, string userId);
    Task ReorderLanesAsync(string boardId, string userId, IReadOnlyList<string> laneIds);
    Task<bool> SetInboxLaneAsync(string boardId, string laneId, string userId);

    Task<BoardCard?> GetCardByIdAsync(string boardId, string cardId, string userId);
    Task<IReadOnlyList<BoardCard>> ListCardsAsync(string boardId, string userId);
    Task InsertCardAsync(BoardCard card);
    Task UpdateCardAsync(BoardCard card);
    Task<bool> DeleteCardAsync(string boardId, string cardId, string userId);
    Task<BoardCard?> ArchiveCardAsync(string boardId, string cardId, string userId, string archivedAt);
    Task<BoardCard?> MoveCardAsync(string boardId, string cardId, string laneId, int position, string userId);
    Task<BoardCard?> ReorderCardAsync(string boardId, string cardId, int position, string userId);
}
