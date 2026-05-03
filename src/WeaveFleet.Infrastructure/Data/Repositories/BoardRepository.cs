using System.Data;
using System.Data.Common;
using WeaveFleet.Application.Data;
using WeaveFleet.Application.Services;
using WeaveFleet.Domain.Entities;
using WeaveFleet.Domain.Repositories;

namespace WeaveFleet.Infrastructure.Data.Repositories;

public sealed class BoardRepository(
    IDbConnectionFactory connectionFactory,
    IUserContext userContext) : IBoardRepository
{
    private const int PositionGap = 1_024;

    public async Task<Board?> GetByIdAsync(string id, string userId)
    {
        using var conn = connectionFactory.CreateConnection();
        return await conn.QueryFirstOrDefaultAsync(
            "SELECT * FROM boards WHERE id = @Id AND user_id = @UserId",
            cmd =>
            {
                cmd.AddParameter("Id", id);
                cmd.AddParameter("UserId", userId);
            },
            ReadBoard);
    }

    public async Task<IReadOnlyList<Board>> ListByUserAsync(string userId)
    {
        using var conn = connectionFactory.CreateConnection();
        return await conn.QueryAsync(
            "SELECT * FROM boards WHERE user_id = @UserId ORDER BY created_at ASC, id ASC",
            cmd => { cmd.AddParameter("UserId", userId); },
            ReadBoard);
    }

    public async Task InsertAsync(Board board)
    {
        var resolvedUserId = ResolveUserId(board.UserId);

        using var conn = connectionFactory.CreateConnection();
        await conn.ExecuteNonQueryAsync(
            """
            INSERT INTO boards (id, user_id, name, created_at, updated_at)
            VALUES (@Id, @UserId, @Name, @CreatedAt, @UpdatedAt)
            """,
            cmd =>
            {
                cmd.AddParameter("Id", board.Id);
                cmd.AddParameter("UserId", resolvedUserId);
                cmd.AddParameter("Name", board.Name);
                cmd.AddParameter("CreatedAt", board.CreatedAt);
                cmd.AddParameter("UpdatedAt", board.UpdatedAt);
            });
    }

    public async Task UpdateAsync(Board board)
    {
        var resolvedUserId = ResolveUserId(board.UserId);

        using var conn = connectionFactory.CreateConnection();
        await conn.ExecuteNonQueryAsync(
            """
            UPDATE boards
            SET name = @Name,
                updated_at = @UpdatedAt
            WHERE id = @Id AND user_id = @UserId
            """,
            cmd =>
            {
                cmd.AddParameter("Id", board.Id);
                cmd.AddParameter("UserId", resolvedUserId);
                cmd.AddParameter("Name", board.Name);
                cmd.AddParameter("UpdatedAt", board.UpdatedAt);
            });
    }

    public async Task<bool> DeleteAsync(string id, string userId)
    {
        using var conn = connectionFactory.CreateConnection();
        var rows = await conn.ExecuteNonQueryAsync(
            "DELETE FROM boards WHERE id = @Id AND user_id = @UserId",
            cmd =>
            {
                cmd.AddParameter("Id", id);
                cmd.AddParameter("UserId", userId);
            });
        return rows > 0;
    }

    public async Task<IReadOnlyList<BoardSource>> GetSourcesByBoardIdAsync(string boardId, string userId)
    {
        using var conn = connectionFactory.CreateConnection();
        return await conn.QueryAsync(
            """
            SELECT source.*
            FROM board_sources source
            INNER JOIN boards board ON board.id = source.board_id
            WHERE source.board_id = @BoardId
              AND board.user_id = @UserId
            ORDER BY source.created_at ASC, source.id ASC
            """,
            cmd =>
            {
                cmd.AddParameter("BoardId", boardId);
                cmd.AddParameter("UserId", userId);
            },
            ReadBoardSource);
    }

    public async Task InsertSourceAsync(BoardSource source)
    {
        using var conn = connectionFactory.CreateConnection();
        await conn.ExecuteNonQueryAsync(
            """
            INSERT INTO board_sources (id, board_id, provider_type, config, last_sync_at, created_at, updated_at)
            SELECT @Id, @BoardId, @ProviderType, @Config, @LastSyncAt, @CreatedAt, @UpdatedAt
            FROM boards board
            WHERE board.id = @BoardId
              AND board.user_id = @UserId
            """,
            cmd =>
            {
                cmd.AddParameter("Id", source.Id);
                cmd.AddParameter("BoardId", source.BoardId);
                cmd.AddParameter("ProviderType", source.ProviderType);
                cmd.AddParameter("Config", source.Config);
                cmd.AddParameter("LastSyncAt", source.LastSyncAt);
                cmd.AddParameter("CreatedAt", source.CreatedAt);
                cmd.AddParameter("UpdatedAt", source.UpdatedAt);
                cmd.AddParameter("UserId", userContext.UserId);
            });
    }

    public async Task UpdateSourceAsync(BoardSource source)
    {
        using var conn = connectionFactory.CreateConnection();
        await conn.ExecuteNonQueryAsync(
            """
            UPDATE board_sources
            SET provider_type = @ProviderType,
                config = @Config,
                last_sync_at = @LastSyncAt,
                updated_at = @UpdatedAt
            WHERE id = @Id
              AND board_id = @BoardId
              AND EXISTS (
                  SELECT 1
                  FROM boards board
                  WHERE board.id = @BoardId
                    AND board.user_id = @UserId)
            """,
            cmd =>
            {
                cmd.AddParameter("Id", source.Id);
                cmd.AddParameter("BoardId", source.BoardId);
                cmd.AddParameter("ProviderType", source.ProviderType);
                cmd.AddParameter("Config", source.Config);
                cmd.AddParameter("LastSyncAt", source.LastSyncAt);
                cmd.AddParameter("UpdatedAt", source.UpdatedAt);
                cmd.AddParameter("UserId", userContext.UserId);
            });
    }

    public async Task<bool> DeleteSourceAsync(string boardId, string sourceId, string userId)
    {
        using var conn = connectionFactory.CreateConnection();
        var rows = await conn.ExecuteNonQueryAsync(
            """
            DELETE FROM board_sources
            WHERE id = @SourceId
              AND board_id = @BoardId
              AND EXISTS (
                  SELECT 1
                  FROM boards board
                  WHERE board.id = @BoardId
                    AND board.user_id = @UserId)
            """,
            cmd =>
            {
                cmd.AddParameter("BoardId", boardId);
                cmd.AddParameter("SourceId", sourceId);
                cmd.AddParameter("UserId", userId);
            });
        return rows > 0;
    }

    public async Task<BoardLane?> GetLaneByIdAsync(string boardId, string laneId, string userId)
    {
        using var conn = connectionFactory.CreateConnection();
        return await GetLaneByIdAsync(conn, null, boardId, laneId, userId);
    }

    public async Task<IReadOnlyList<BoardLane>> ListLanesAsync(string boardId, string userId)
    {
        using var conn = connectionFactory.CreateConnection();
        return await conn.QueryAsync(
            """
            SELECT lane.*
            FROM board_lanes lane
            INNER JOIN boards board ON board.id = lane.board_id
            WHERE lane.board_id = @BoardId
              AND board.user_id = @UserId
            ORDER BY lane.position ASC, lane.created_at ASC, lane.id ASC
            """,
            cmd =>
            {
                cmd.AddParameter("BoardId", boardId);
                cmd.AddParameter("UserId", userId);
            },
            ReadBoardLane);
    }

    public async Task InsertLaneAsync(BoardLane lane)
    {
        using var conn = connectionFactory.CreateConnection();
        using var tx = conn.BeginTransaction();

        if (lane.IsInbox)
            await ClearInboxLaneAsync(conn, tx, lane.BoardId, null, userContext.UserId);

        var position = lane.Position > 0
            ? lane.Position
            : await GetNextLanePositionAsync(conn, tx, lane.BoardId, userContext.UserId);

        await conn.ExecuteNonQueryAsync(
            """
            INSERT INTO board_lanes (id, board_id, name, position, is_inbox, created_at, updated_at)
            SELECT @Id, @BoardId, @Name, @Position, @IsInbox, @CreatedAt, @UpdatedAt
            FROM boards board
            WHERE board.id = @BoardId
              AND board.user_id = @UserId
            """,
            cmd =>
            {
                cmd.AddParameter("Id", lane.Id);
                cmd.AddParameter("BoardId", lane.BoardId);
                cmd.AddParameter("Name", lane.Name);
                cmd.AddParameter("Position", position);
                cmd.AddParameter("IsInbox", lane.IsInbox);
                cmd.AddParameter("CreatedAt", lane.CreatedAt);
                cmd.AddParameter("UpdatedAt", lane.UpdatedAt);
                cmd.AddParameter("UserId", userContext.UserId);
            },
            tx);

        tx.Commit();
    }

    public async Task UpdateLaneAsync(BoardLane lane)
    {
        using var conn = connectionFactory.CreateConnection();
        using var tx = conn.BeginTransaction();

        if (lane.IsInbox)
            await ClearInboxLaneAsync(conn, tx, lane.BoardId, lane.Id, userContext.UserId);

        await conn.ExecuteNonQueryAsync(
            """
            UPDATE board_lanes
            SET name = @Name,
                position = @Position,
                is_inbox = @IsInbox,
                updated_at = @UpdatedAt
            WHERE id = @Id
              AND board_id = @BoardId
              AND EXISTS (
                  SELECT 1
                  FROM boards board
                  WHERE board.id = @BoardId
                    AND board.user_id = @UserId)
            """,
            cmd =>
            {
                cmd.AddParameter("Id", lane.Id);
                cmd.AddParameter("BoardId", lane.BoardId);
                cmd.AddParameter("Name", lane.Name);
                cmd.AddParameter("Position", lane.Position);
                cmd.AddParameter("IsInbox", lane.IsInbox);
                cmd.AddParameter("UpdatedAt", lane.UpdatedAt);
                cmd.AddParameter("UserId", userContext.UserId);
            },
            tx);

        tx.Commit();
    }

    public async Task<bool> DeleteLaneAsync(string boardId, string laneId, string userId)
    {
        using var conn = connectionFactory.CreateConnection();
        using var tx = conn.BeginTransaction();

        var cardCount = await conn.ExecuteScalarAsync<long>(
            """
            SELECT COUNT(*)
            FROM board_cards card
            INNER JOIN boards board ON board.id = card.board_id
            WHERE card.board_id = @BoardId
              AND card.lane_id = @LaneId
              AND board.user_id = @UserId
            """,
            cmd =>
            {
                cmd.AddParameter("BoardId", boardId);
                cmd.AddParameter("LaneId", laneId);
                cmd.AddParameter("UserId", userId);
            },
            tx);

        if (cardCount > 0)
            return false;

        var rows = await conn.ExecuteNonQueryAsync(
            """
            DELETE FROM board_lanes
            WHERE id = @LaneId
              AND board_id = @BoardId
              AND EXISTS (
                  SELECT 1
                  FROM boards board
                  WHERE board.id = @BoardId
                    AND board.user_id = @UserId)
            """,
            cmd =>
            {
                cmd.AddParameter("BoardId", boardId);
                cmd.AddParameter("LaneId", laneId);
                cmd.AddParameter("UserId", userId);
            },
            tx);

        if (rows == 0)
        {
            tx.Commit();
            return false;
        }

        await RebalanceLanePositionsAsync(conn, tx, boardId, userId);
        tx.Commit();
        return true;
    }

    public async Task ReorderLanesAsync(string boardId, string userId, IReadOnlyList<string> laneIds)
    {
        using var conn = connectionFactory.CreateConnection();
        using var tx = conn.BeginTransaction();

        var existingLaneIds = await GetOrderedLaneIdsAsync(conn, tx, boardId, userId);
        if (existingLaneIds.Count == 0)
        {
            tx.Commit();
            return;
        }

        var orderedLaneIds = MergeOrderedIds(existingLaneIds, laneIds);
        await RebalanceLanePositionsAsync(conn, tx, boardId, userId, orderedLaneIds);

        tx.Commit();
    }

    public async Task<bool> SetInboxLaneAsync(string boardId, string laneId, string userId)
    {
        using var conn = connectionFactory.CreateConnection();
        using var tx = conn.BeginTransaction();

        var lane = await GetLaneByIdAsync(conn, tx, boardId, laneId, userId);
        if (lane is null)
            return false;

        await ClearInboxLaneAsync(conn, tx, boardId, laneId, userId);

        await conn.ExecuteNonQueryAsync(
            """
            UPDATE board_lanes
            SET is_inbox = 1,
                updated_at = datetime('now')
            WHERE id = @LaneId
              AND board_id = @BoardId
              AND EXISTS (
                  SELECT 1
                  FROM boards board
                  WHERE board.id = @BoardId
                    AND board.user_id = @UserId)
            """,
            cmd =>
            {
                cmd.AddParameter("BoardId", boardId);
                cmd.AddParameter("LaneId", laneId);
                cmd.AddParameter("UserId", userId);
            },
            tx);

        tx.Commit();
        return true;
    }

    public async Task<BoardCard?> GetCardByIdAsync(string boardId, string cardId, string userId)
    {
        using var conn = connectionFactory.CreateConnection();
        return await GetCardByIdAsync(conn, null, boardId, cardId, userId);
    }

    public async Task<IReadOnlyList<BoardCard>> ListCardsAsync(string boardId, string userId)
    {
        using var conn = connectionFactory.CreateConnection();
        return await conn.QueryAsync(
            """
            SELECT card.*
            FROM board_cards card
            INNER JOIN boards board ON board.id = card.board_id
            LEFT JOIN board_lanes lane ON lane.id = card.lane_id AND lane.board_id = card.board_id
            WHERE card.board_id = @BoardId
              AND board.user_id = @UserId
            ORDER BY lane.position ASC,
                     CASE WHEN card.archived_at IS NULL THEN 0 ELSE 1 END ASC,
                     card.position ASC,
                     card.created_at ASC,
                     card.id ASC
            """,
            cmd =>
            {
                cmd.AddParameter("BoardId", boardId);
                cmd.AddParameter("UserId", userId);
            },
            ReadBoardCard);
    }

    public async Task InsertCardAsync(BoardCard card)
    {
        using var conn = connectionFactory.CreateConnection();
        var position = card.Position > 0
            ? card.Position
            : await GetNextCardPositionAsync(conn, null, card.BoardId, card.LaneId, userContext.UserId);

        await conn.ExecuteNonQueryAsync(
            """
            INSERT INTO board_cards (id, board_id, lane_id, title, source_type, source_key, metadata, position, archived_at, created_at, updated_at)
            SELECT @Id, @BoardId, @LaneId, @Title, @SourceType, @SourceKey, @Metadata, @Position, @ArchivedAt, @CreatedAt, @UpdatedAt
            FROM boards board
            WHERE board.id = @BoardId
              AND board.user_id = @UserId
              AND EXISTS (
                  SELECT 1
                  FROM board_lanes lane
                  WHERE lane.id = @LaneId
                    AND lane.board_id = @BoardId)
            """,
            cmd =>
            {
                cmd.AddParameter("Id", card.Id);
                cmd.AddParameter("BoardId", card.BoardId);
                cmd.AddParameter("LaneId", card.LaneId);
                cmd.AddParameter("Title", card.Title);
                cmd.AddParameter("SourceType", card.SourceType);
                cmd.AddParameter("SourceKey", card.SourceKey);
                cmd.AddParameter("Metadata", card.Metadata);
                cmd.AddParameter("Position", position);
                cmd.AddParameter("ArchivedAt", card.ArchivedAt);
                cmd.AddParameter("CreatedAt", card.CreatedAt);
                cmd.AddParameter("UpdatedAt", card.UpdatedAt);
                cmd.AddParameter("UserId", userContext.UserId);
            });
    }

    public async Task UpdateCardAsync(BoardCard card)
    {
        using var conn = connectionFactory.CreateConnection();
        await conn.ExecuteNonQueryAsync(
            """
            UPDATE board_cards
            SET lane_id = @LaneId,
                title = @Title,
                source_type = @SourceType,
                source_key = @SourceKey,
                metadata = @Metadata,
                position = @Position,
                archived_at = @ArchivedAt,
                updated_at = @UpdatedAt
            WHERE id = @Id
              AND board_id = @BoardId
              AND EXISTS (
                  SELECT 1
                  FROM boards board
                  WHERE board.id = @BoardId
                    AND board.user_id = @UserId)
              AND EXISTS (
                  SELECT 1
                  FROM board_lanes lane
                  WHERE lane.id = @LaneId
                    AND lane.board_id = @BoardId)
            """,
            cmd =>
            {
                cmd.AddParameter("Id", card.Id);
                cmd.AddParameter("BoardId", card.BoardId);
                cmd.AddParameter("LaneId", card.LaneId);
                cmd.AddParameter("Title", card.Title);
                cmd.AddParameter("SourceType", card.SourceType);
                cmd.AddParameter("SourceKey", card.SourceKey);
                cmd.AddParameter("Metadata", card.Metadata);
                cmd.AddParameter("Position", card.Position);
                cmd.AddParameter("ArchivedAt", card.ArchivedAt);
                cmd.AddParameter("UpdatedAt", card.UpdatedAt);
                cmd.AddParameter("UserId", userContext.UserId);
            });
    }

    public async Task<bool> DeleteCardAsync(string boardId, string cardId, string userId)
    {
        using var conn = connectionFactory.CreateConnection();
        using var tx = conn.BeginTransaction();

        var card = await GetCardByIdAsync(conn, tx, boardId, cardId, userId);
        if (card is null)
            return false;

        var rows = await conn.ExecuteNonQueryAsync(
            """
            DELETE FROM board_cards
            WHERE id = @CardId
              AND board_id = @BoardId
              AND EXISTS (
                  SELECT 1
                  FROM boards board
                  WHERE board.id = @BoardId
                    AND board.user_id = @UserId)
            """,
            cmd =>
            {
                cmd.AddParameter("BoardId", boardId);
                cmd.AddParameter("CardId", cardId);
                cmd.AddParameter("UserId", userId);
            },
            tx);

        if (rows == 0)
        {
            tx.Commit();
            return false;
        }

        if (card.ArchivedAt is null)
            await RebalanceCardPositionsAsync(conn, tx, boardId, card.LaneId, userId);

        tx.Commit();
        return true;
    }

    public async Task<BoardCard?> ArchiveCardAsync(string boardId, string cardId, string userId, string archivedAt)
    {
        using var conn = connectionFactory.CreateConnection();
        using var tx = conn.BeginTransaction();

        var card = await GetCardByIdAsync(conn, tx, boardId, cardId, userId);
        if (card is null)
            return null;

        await conn.ExecuteNonQueryAsync(
            """
            UPDATE board_cards
            SET archived_at = @ArchivedAt,
                updated_at = @ArchivedAt
            WHERE id = @CardId
              AND board_id = @BoardId
              AND EXISTS (
                  SELECT 1
                  FROM boards board
                  WHERE board.id = @BoardId
                    AND board.user_id = @UserId)
            """,
            cmd =>
            {
                cmd.AddParameter("BoardId", boardId);
                cmd.AddParameter("CardId", cardId);
                cmd.AddParameter("ArchivedAt", archivedAt);
                cmd.AddParameter("UserId", userId);
            },
            tx);

        await RebalanceCardPositionsAsync(conn, tx, boardId, card.LaneId, userId);

        var updatedCard = await GetCardByIdAsync(conn, tx, boardId, cardId, userId);
        tx.Commit();
        return updatedCard;
    }

    public async Task<BoardCard?> MoveCardAsync(string boardId, string cardId, string laneId, int position, string userId)
    {
        using var conn = connectionFactory.CreateConnection();
        using var tx = conn.BeginTransaction();

        var card = await GetCardByIdAsync(conn, tx, boardId, cardId, userId);
        if (card is null || card.ArchivedAt is not null)
            return null;

        var targetLane = await GetLaneByIdAsync(conn, tx, boardId, laneId, userId);
        if (targetLane is null)
            return null;

        var sourceLaneId = card.LaneId;
        var targetCardIds = await GetOrderedActiveCardIdsAsync(conn, tx, boardId, laneId, userId, cardId);
        var orderedTargetCardIds = InsertAtPosition(targetCardIds, cardId, position);

        await conn.ExecuteNonQueryAsync(
            """
            UPDATE board_cards
            SET lane_id = @LaneId,
                updated_at = datetime('now')
            WHERE id = @CardId
              AND board_id = @BoardId
              AND EXISTS (
                  SELECT 1
                  FROM boards board
                  WHERE board.id = @BoardId
                    AND board.user_id = @UserId)
            """,
            cmd =>
            {
                cmd.AddParameter("BoardId", boardId);
                cmd.AddParameter("CardId", cardId);
                cmd.AddParameter("LaneId", laneId);
                cmd.AddParameter("UserId", userId);
            },
            tx);

        if (!string.Equals(sourceLaneId, laneId, StringComparison.Ordinal))
            await RebalanceCardPositionsAsync(conn, tx, boardId, sourceLaneId, userId);

        await RebalanceCardPositionsAsync(conn, tx, boardId, laneId, userId, orderedTargetCardIds);

        var updatedCard = await GetCardByIdAsync(conn, tx, boardId, cardId, userId);
        tx.Commit();
        return updatedCard;
    }

    public async Task<BoardCard?> ReorderCardAsync(string boardId, string cardId, int position, string userId)
    {
        using var conn = connectionFactory.CreateConnection();
        using var tx = conn.BeginTransaction();

        var card = await GetCardByIdAsync(conn, tx, boardId, cardId, userId);
        if (card is null || card.ArchivedAt is not null)
            return null;

        var laneCardIds = await GetOrderedActiveCardIdsAsync(conn, tx, boardId, card.LaneId, userId, cardId);
        var orderedCardIds = InsertAtPosition(laneCardIds, cardId, position);

        await conn.ExecuteNonQueryAsync(
            """
            UPDATE board_cards
            SET updated_at = datetime('now')
            WHERE id = @CardId
              AND board_id = @BoardId
              AND EXISTS (
                  SELECT 1
                  FROM boards board
                  WHERE board.id = @BoardId
                    AND board.user_id = @UserId)
            """,
            cmd =>
            {
                cmd.AddParameter("BoardId", boardId);
                cmd.AddParameter("CardId", cardId);
                cmd.AddParameter("UserId", userId);
            },
            tx);

        await RebalanceCardPositionsAsync(conn, tx, boardId, card.LaneId, userId, orderedCardIds);

        var updatedCard = await GetCardByIdAsync(conn, tx, boardId, cardId, userId);
        tx.Commit();
        return updatedCard;
    }

    private static async Task<BoardLane?> GetLaneByIdAsync(
        IDbConnection connection,
        IDbTransaction? transaction,
        string boardId,
        string laneId,
        string userId)
        => await connection.QueryFirstOrDefaultAsync(
            """
            SELECT lane.*
            FROM board_lanes lane
            INNER JOIN boards board ON board.id = lane.board_id
            WHERE lane.id = @LaneId
              AND lane.board_id = @BoardId
              AND board.user_id = @UserId
            """,
            cmd =>
            {
                cmd.AddParameter("BoardId", boardId);
                cmd.AddParameter("LaneId", laneId);
                cmd.AddParameter("UserId", userId);
            },
            ReadBoardLane,
            transaction);

    private static async Task<BoardCard?> GetCardByIdAsync(
        IDbConnection connection,
        IDbTransaction? transaction,
        string boardId,
        string cardId,
        string userId)
        => await connection.QueryFirstOrDefaultAsync(
            """
            SELECT card.*
            FROM board_cards card
            INNER JOIN boards board ON board.id = card.board_id
            WHERE card.id = @CardId
              AND card.board_id = @BoardId
              AND board.user_id = @UserId
            """,
            cmd =>
            {
                cmd.AddParameter("BoardId", boardId);
                cmd.AddParameter("CardId", cardId);
                cmd.AddParameter("UserId", userId);
            },
            ReadBoardCard,
            transaction);

    private static async Task<int> GetNextLanePositionAsync(
        IDbConnection connection,
        IDbTransaction? transaction,
        string boardId,
        string userId)
    {
        var currentMax = await connection.ExecuteScalarAsync<long?>(
            """
            SELECT MAX(lane.position)
            FROM board_lanes lane
            INNER JOIN boards board ON board.id = lane.board_id
            WHERE lane.board_id = @BoardId
              AND board.user_id = @UserId
            """,
            cmd =>
            {
                cmd.AddParameter("BoardId", boardId);
                cmd.AddParameter("UserId", userId);
            },
            transaction);

        return checked((int)((currentMax ?? 0) + PositionGap));
    }

    private static async Task<int> GetNextCardPositionAsync(
        IDbConnection connection,
        IDbTransaction? transaction,
        string boardId,
        string laneId,
        string userId)
    {
        var currentMax = await connection.ExecuteScalarAsync<long?>(
            """
            SELECT MAX(card.position)
            FROM board_cards card
            INNER JOIN boards board ON board.id = card.board_id
            WHERE card.board_id = @BoardId
              AND card.lane_id = @LaneId
              AND card.archived_at IS NULL
              AND board.user_id = @UserId
            """,
            cmd =>
            {
                cmd.AddParameter("BoardId", boardId);
                cmd.AddParameter("LaneId", laneId);
                cmd.AddParameter("UserId", userId);
            },
            transaction);

        return checked((int)((currentMax ?? 0) + PositionGap));
    }

    private static async Task ClearInboxLaneAsync(
        IDbConnection connection,
        IDbTransaction transaction,
        string boardId,
        string? excludedLaneId,
        string userId)
    {
        await connection.ExecuteNonQueryAsync(
            """
            UPDATE board_lanes
            SET is_inbox = 0,
                updated_at = datetime('now')
            WHERE board_id = @BoardId
              AND (@ExcludedLaneId IS NULL OR id <> @ExcludedLaneId)
              AND EXISTS (
                  SELECT 1
                  FROM boards board
                  WHERE board.id = @BoardId
                    AND board.user_id = @UserId)
            """,
            cmd =>
            {
                cmd.AddParameter("BoardId", boardId);
                cmd.AddParameter("ExcludedLaneId", excludedLaneId);
                cmd.AddParameter("UserId", userId);
            },
            transaction);
    }

    private static async Task<IReadOnlyList<string>> GetOrderedLaneIdsAsync(
        IDbConnection connection,
        IDbTransaction? transaction,
        string boardId,
        string userId)
    {
        return await connection.QueryAsync(
            """
            SELECT lane.id
            FROM board_lanes lane
            INNER JOIN boards board ON board.id = lane.board_id
            WHERE lane.board_id = @BoardId
              AND board.user_id = @UserId
            ORDER BY lane.position ASC, lane.created_at ASC, lane.id ASC
            """,
            cmd =>
            {
                cmd.AddParameter("BoardId", boardId);
                cmd.AddParameter("UserId", userId);
            },
            r => r.GetString(r.GetOrdinal("id")),
            transaction);
    }

    private static async Task<IReadOnlyList<string>> GetOrderedActiveCardIdsAsync(
        IDbConnection connection,
        IDbTransaction? transaction,
        string boardId,
        string laneId,
        string userId,
        string? excludedCardId = null)
    {
        return await connection.QueryAsync(
            """
            SELECT card.id
            FROM board_cards card
            INNER JOIN boards board ON board.id = card.board_id
            WHERE card.board_id = @BoardId
              AND card.lane_id = @LaneId
              AND card.archived_at IS NULL
              AND (@ExcludedCardId IS NULL OR card.id <> @ExcludedCardId)
              AND board.user_id = @UserId
            ORDER BY card.position ASC, card.created_at ASC, card.id ASC
            """,
            cmd =>
            {
                cmd.AddParameter("BoardId", boardId);
                cmd.AddParameter("LaneId", laneId);
                cmd.AddParameter("ExcludedCardId", excludedCardId);
                cmd.AddParameter("UserId", userId);
            },
            r => r.GetString(r.GetOrdinal("id")),
            transaction);
    }

    private static async Task RebalanceLanePositionsAsync(
        IDbConnection connection,
        IDbTransaction transaction,
        string boardId,
        string userId,
        IReadOnlyList<string>? orderedLaneIds = null)
    {
        var laneIds = orderedLaneIds ?? await GetOrderedLaneIdsAsync(connection, transaction, boardId, userId);

        for (var index = 0; index < laneIds.Count; index++)
        {
            await connection.ExecuteNonQueryAsync(
                """
                UPDATE board_lanes
                SET position = @Position,
                    updated_at = datetime('now')
                WHERE id = @LaneId
                  AND board_id = @BoardId
                  AND EXISTS (
                      SELECT 1
                      FROM boards board
                      WHERE board.id = @BoardId
                        AND board.user_id = @UserId)
                """,
                cmd =>
                {
                    cmd.AddParameter("BoardId", boardId);
                    cmd.AddParameter("LaneId", laneIds[index]);
                    cmd.AddParameter("Position", checked((index + 1) * PositionGap));
                    cmd.AddParameter("UserId", userId);
                },
                transaction);
        }
    }

    private static async Task RebalanceCardPositionsAsync(
        IDbConnection connection,
        IDbTransaction transaction,
        string boardId,
        string laneId,
        string userId,
        IReadOnlyList<string>? orderedCardIds = null)
    {
        var cardIds = orderedCardIds ?? await GetOrderedActiveCardIdsAsync(connection, transaction, boardId, laneId, userId);

        for (var index = 0; index < cardIds.Count; index++)
        {
            await connection.ExecuteNonQueryAsync(
                """
                UPDATE board_cards
                SET position = @Position,
                    updated_at = datetime('now')
                WHERE id = @CardId
                  AND board_id = @BoardId
                  AND lane_id = @LaneId
                  AND archived_at IS NULL
                  AND EXISTS (
                      SELECT 1
                      FROM boards board
                      WHERE board.id = @BoardId
                        AND board.user_id = @UserId)
                """,
                cmd =>
                {
                    cmd.AddParameter("BoardId", boardId);
                    cmd.AddParameter("LaneId", laneId);
                    cmd.AddParameter("CardId", cardIds[index]);
                    cmd.AddParameter("Position", checked((index + 1) * PositionGap));
                    cmd.AddParameter("UserId", userId);
                },
                transaction);
        }
    }

    private static List<string> MergeOrderedIds(IReadOnlyList<string> existingIds, IReadOnlyList<string> requestedIds)
    {
        var existingSet = existingIds.ToHashSet(StringComparer.Ordinal);
        var orderedIds = new List<string>(existingIds.Count);

        foreach (var requestedId in requestedIds)
        {
            if (!existingSet.Contains(requestedId) || orderedIds.Contains(requestedId, StringComparer.Ordinal))
                continue;

            orderedIds.Add(requestedId);
        }

        foreach (var existingId in existingIds)
        {
            if (!orderedIds.Contains(existingId, StringComparer.Ordinal))
                orderedIds.Add(existingId);
        }

        return orderedIds;
    }

    private static List<string> InsertAtPosition(IReadOnlyList<string> ids, string value, int position)
    {
        var orderedIds = ids.ToList();
        var targetIndex = Math.Clamp(position, 0, orderedIds.Count);
        orderedIds.Insert(targetIndex, value);
        return orderedIds;
    }

    private string ResolveUserId(string? userId)
        => string.IsNullOrWhiteSpace(userId) ? userContext.UserId : userId;

    private static Board ReadBoard(DbDataReader r) => new()
    {
        Id = r.GetString(r.GetOrdinal("id")),
        UserId = r.GetString(r.GetOrdinal("user_id")),
        Name = r.GetString(r.GetOrdinal("name")),
        CreatedAt = r.GetString(r.GetOrdinal("created_at")),
        UpdatedAt = r.GetString(r.GetOrdinal("updated_at")),
    };

    private static BoardLane ReadBoardLane(DbDataReader r) => new()
    {
        Id = r.GetString(r.GetOrdinal("id")),
        BoardId = r.GetString(r.GetOrdinal("board_id")),
        Name = r.GetString(r.GetOrdinal("name")),
        Position = (int)r.GetInt64(r.GetOrdinal("position")),
        IsInbox = r.GetInt64(r.GetOrdinal("is_inbox")) != 0,
        CreatedAt = r.GetString(r.GetOrdinal("created_at")),
        UpdatedAt = r.GetString(r.GetOrdinal("updated_at")),
    };

    private static BoardCard ReadBoardCard(DbDataReader r) => new()
    {
        Id = r.GetString(r.GetOrdinal("id")),
        BoardId = r.GetString(r.GetOrdinal("board_id")),
        LaneId = r.GetString(r.GetOrdinal("lane_id")),
        Title = r.GetString(r.GetOrdinal("title")),
        SourceType = r.GetNullableString(r.GetOrdinal("source_type")),
        SourceKey = r.GetNullableString(r.GetOrdinal("source_key")),
        Metadata = r.GetNullableString(r.GetOrdinal("metadata")),
        Position = (int)r.GetInt64(r.GetOrdinal("position")),
        ArchivedAt = r.GetNullableString(r.GetOrdinal("archived_at")),
        CreatedAt = r.GetString(r.GetOrdinal("created_at")),
        UpdatedAt = r.GetString(r.GetOrdinal("updated_at")),
    };

    private static BoardSource ReadBoardSource(DbDataReader r) => new()
    {
        Id = r.GetString(r.GetOrdinal("id")),
        BoardId = r.GetString(r.GetOrdinal("board_id")),
        ProviderType = r.GetString(r.GetOrdinal("provider_type")),
        Config = r.GetString(r.GetOrdinal("config")),
        LastSyncAt = r.GetNullableString(r.GetOrdinal("last_sync_at")),
        CreatedAt = r.GetString(r.GetOrdinal("created_at")),
        UpdatedAt = r.GetString(r.GetOrdinal("updated_at")),
    };
}
