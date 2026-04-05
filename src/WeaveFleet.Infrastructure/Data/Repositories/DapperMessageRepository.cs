using Dapper;
using WeaveFleet.Application.Data;
using WeaveFleet.Domain.Entities;
using WeaveFleet.Domain.Repositories;

namespace WeaveFleet.Infrastructure.Data.Repositories;

public sealed class DapperMessageRepository(IDbConnectionFactory connectionFactory) : IMessageRepository
{
    public async Task UpsertAsync(PersistedMessage message)
    {
        using var conn = connectionFactory.CreateConnection();
        await conn.ExecuteAsync(
            """
            INSERT INTO messages (id, session_id, role, parts_json, timestamp, created_at)
            VALUES (@Id, @SessionId, @Role, @PartsJson, @Timestamp, @CreatedAt)
            ON CONFLICT(id, session_id) DO UPDATE SET
                role = excluded.role,
                parts_json = excluded.parts_json,
                timestamp = excluded.timestamp
            """, message);
    }

    public async Task UpsertBatchAsync(IReadOnlyList<PersistedMessage> messages)
    {
        if (messages.Count == 0)
            return;

        using var conn = connectionFactory.CreateConnection();
        using var tx = conn.BeginTransaction();
        try
        {
            foreach (var message in messages)
            {
                await conn.ExecuteAsync(
                    """
                    INSERT INTO messages (id, session_id, role, parts_json, timestamp, created_at)
                    VALUES (@Id, @SessionId, @Role, @PartsJson, @Timestamp, @CreatedAt)
                    ON CONFLICT(id, session_id) DO UPDATE SET
                        role = excluded.role,
                        parts_json = excluded.parts_json,
                        timestamp = excluded.timestamp
                    """, message, tx);
            }
            tx.Commit();
        }
        catch
        {
            tx.Rollback();
            throw;
        }
    }

    public async Task<IReadOnlyList<PersistedMessage>> GetBySessionAsync(
        string sessionId, int limit, string? beforeMessageId)
    {
        using var conn = connectionFactory.CreateConnection();

        IEnumerable<PersistedMessage> results;

        if (beforeMessageId is null)
        {
            // No cursor: get the last N messages ordered by timestamp DESC, then reverse
            results = await conn.QueryAsync<PersistedMessage>(
                """
                SELECT * FROM messages
                WHERE session_id = @SessionId
                ORDER BY timestamp DESC, id DESC
                LIMIT @Limit
                """,
                new { SessionId = sessionId, Limit = limit });
        }
        else
        {
            // Cursor: first look up the cursor message's timestamp
            var cursorTimestamp = await conn.ExecuteScalarAsync<string?>(
                "SELECT timestamp FROM messages WHERE session_id = @SessionId AND id = @Id",
                new { SessionId = sessionId, Id = beforeMessageId });

            if (cursorTimestamp is null)
            {
                // Cursor not found — fall back to no-cursor behavior
                results = await conn.QueryAsync<PersistedMessage>(
                    """
                    SELECT * FROM messages
                    WHERE session_id = @SessionId
                    ORDER BY timestamp DESC, id DESC
                    LIMIT @Limit
                    """,
                    new { SessionId = sessionId, Limit = limit });
            }
            else
            {
                // Compound comparison for deterministic cursor behavior
                results = await conn.QueryAsync<PersistedMessage>(
                    """
                    SELECT * FROM messages
                    WHERE session_id = @SessionId
                      AND (timestamp < @CursorTimestamp
                           OR (timestamp = @CursorTimestamp AND id < @CursorId))
                    ORDER BY timestamp DESC, id DESC
                    LIMIT @Limit
                    """,
                    new { SessionId = sessionId, CursorTimestamp = cursorTimestamp, CursorId = beforeMessageId, Limit = limit });
            }
        }

        // Reverse to ascending order (oldest first), matching live API behavior
        var list = results.ToList();
        list.Reverse();
        return list;
    }

    public async Task<int> CountBySessionAsync(string sessionId)
    {
        using var conn = connectionFactory.CreateConnection();
        return await conn.ExecuteScalarAsync<int>(
            "SELECT COUNT(*) FROM messages WHERE session_id = @SessionId",
            new { SessionId = sessionId });
    }

    public async Task<bool> HasMessagesAsync(string sessionId)
    {
        using var conn = connectionFactory.CreateConnection();
        return await conn.ExecuteScalarAsync<int>(
            "SELECT COUNT(1) FROM messages WHERE session_id = @SessionId LIMIT 1",
            new { SessionId = sessionId }) > 0;
    }

    public async Task<PersistedMessage?> GetByIdAsync(string id, string sessionId)
    {
        using var conn = connectionFactory.CreateConnection();
        return await conn.QuerySingleOrDefaultAsync<PersistedMessage>(
            "SELECT * FROM messages WHERE id = @Id AND session_id = @SessionId",
            new { Id = id, SessionId = sessionId });
    }

    public async Task DeleteBySessionAsync(string sessionId)
    {
        using var conn = connectionFactory.CreateConnection();
        await conn.ExecuteAsync(
            "DELETE FROM messages WHERE session_id = @SessionId",
            new { SessionId = sessionId });
    }
}
