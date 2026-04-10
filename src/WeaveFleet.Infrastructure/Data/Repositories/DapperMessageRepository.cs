using Dapper;
using WeaveFleet.Application.Data;
using WeaveFleet.Application.Services;
using WeaveFleet.Domain.Entities;
using WeaveFleet.Domain.Repositories;

namespace WeaveFleet.Infrastructure.Data.Repositories;

public sealed class DapperMessageRepository : IMessageRepository
{
    private readonly IDbConnectionFactory _connectionFactory;

    private readonly IUserContext _userContext;

    public DapperMessageRepository(IDbConnectionFactory connectionFactory, IUserContext userContext)
    {
        _connectionFactory = connectionFactory;
        _userContext = userContext;
    }

    public async Task UpsertAsync(PersistedMessage message)
    {
        using var conn = _connectionFactory.CreateConnection();
        await conn.ExecuteAsync(
            """
            INSERT INTO messages (id, session_id, role, parts_json, timestamp, created_at, agent_name)
            SELECT @Id, @SessionId, @Role, @PartsJson, @Timestamp, @CreatedAt, @AgentName
            FROM sessions
            WHERE id = @SessionId AND user_id = @UserId
            ON CONFLICT(id, session_id) DO UPDATE SET
                role = excluded.role,
                parts_json = excluded.parts_json,
                timestamp = excluded.timestamp,
                agent_name = COALESCE(excluded.agent_name, messages.agent_name)
            """,
            new
            {
                message.Id,
                message.SessionId,
                message.Role,
                message.PartsJson,
                message.Timestamp,
                message.CreatedAt,
                message.AgentName,
                UserId = _userContext.UserId
            });
    }

    public async Task UpsertBatchAsync(IReadOnlyList<PersistedMessage> messages)
    {
        if (messages.Count == 0)
            return;

        using var conn = _connectionFactory.CreateConnection();
        using var tx = conn.BeginTransaction();
        try
        {
            foreach (var message in messages)
            {
                await conn.ExecuteAsync(
                    """
                    INSERT INTO messages (id, session_id, role, parts_json, timestamp, created_at, agent_name)
                    SELECT @Id, @SessionId, @Role, @PartsJson, @Timestamp, @CreatedAt, @AgentName
                    FROM sessions
                    WHERE id = @SessionId AND user_id = @UserId
                    ON CONFLICT(id, session_id) DO UPDATE SET
                        role = excluded.role,
                        parts_json = excluded.parts_json,
                        timestamp = excluded.timestamp,
                        agent_name = COALESCE(excluded.agent_name, messages.agent_name)
                    """,
                    new
                    {
                        message.Id,
                        message.SessionId,
                        message.Role,
                        message.PartsJson,
                        message.Timestamp,
                        message.CreatedAt,
                        message.AgentName,
                        UserId = _userContext.UserId
                    },
                    tx);
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
        using var conn = _connectionFactory.CreateConnection();

        IEnumerable<PersistedMessage> results;

        if (beforeMessageId is null)
        {
            // No cursor: get the last N messages ordered by timestamp DESC, then reverse
            results = await conn.QueryAsync<PersistedMessage>(
                """
                SELECT m.*
                FROM messages m
                INNER JOIN sessions s ON s.id = m.session_id
                WHERE m.session_id = @SessionId AND s.user_id = @UserId
                ORDER BY m.timestamp DESC, m.id DESC
                LIMIT @Limit
                """,
                new { SessionId = sessionId, Limit = limit, UserId = _userContext.UserId });
        }
        else
        {
            // Cursor: first look up the cursor message's timestamp
            var cursorTimestamp = await conn.ExecuteScalarAsync<string?>(
                """
                SELECT m.timestamp
                FROM messages m
                INNER JOIN sessions s ON s.id = m.session_id
                WHERE m.session_id = @SessionId AND m.id = @Id AND s.user_id = @UserId
                """,
                new { SessionId = sessionId, Id = beforeMessageId, UserId = _userContext.UserId });

            if (cursorTimestamp is null)
            {
                // Cursor not found — fall back to no-cursor behavior
                results = await conn.QueryAsync<PersistedMessage>(
                    """
                    SELECT m.*
                    FROM messages m
                    INNER JOIN sessions s ON s.id = m.session_id
                    WHERE m.session_id = @SessionId AND s.user_id = @UserId
                    ORDER BY m.timestamp DESC, m.id DESC
                    LIMIT @Limit
                    """,
                    new { SessionId = sessionId, Limit = limit, UserId = _userContext.UserId });
            }
            else
            {
                // Compound comparison for deterministic cursor behavior
                results = await conn.QueryAsync<PersistedMessage>(
                    """
                    SELECT m.*
                    FROM messages m
                    INNER JOIN sessions s ON s.id = m.session_id
                    WHERE m.session_id = @SessionId
                      AND s.user_id = @UserId
                      AND (m.timestamp < @CursorTimestamp
                           OR (m.timestamp = @CursorTimestamp AND m.id < @CursorId))
                    ORDER BY m.timestamp DESC, m.id DESC
                    LIMIT @Limit
                    """,
                    new { SessionId = sessionId, CursorTimestamp = cursorTimestamp, CursorId = beforeMessageId, Limit = limit, UserId = _userContext.UserId });
            }
        }

        // Reverse to ascending order (oldest first), matching live API behavior
        var list = results.ToList();
        list.Reverse();
        return list;
    }

    public async Task<int> CountBySessionAsync(string sessionId)
    {
        using var conn = _connectionFactory.CreateConnection();
        return await conn.ExecuteScalarAsync<int>(
            """
            SELECT COUNT(*)
            FROM messages m
            INNER JOIN sessions s ON s.id = m.session_id
            WHERE m.session_id = @SessionId AND s.user_id = @UserId
            """,
            new { SessionId = sessionId, UserId = _userContext.UserId });
    }

    public async Task<bool> HasMessagesAsync(string sessionId)
    {
        using var conn = _connectionFactory.CreateConnection();
        return await conn.ExecuteScalarAsync<int>(
            """
            SELECT CASE WHEN EXISTS (
                SELECT 1
                FROM messages m
                INNER JOIN sessions s ON s.id = m.session_id
                WHERE m.session_id = @SessionId AND s.user_id = @UserId)
            THEN 1 ELSE 0 END
            """,
            new { SessionId = sessionId, UserId = _userContext.UserId }) > 0;
    }

    public async Task<PersistedMessage?> GetByIdAsync(string id, string sessionId)
    {
        using var conn = _connectionFactory.CreateConnection();
        return await conn.QuerySingleOrDefaultAsync<PersistedMessage>(
            """
            SELECT m.*
            FROM messages m
            INNER JOIN sessions s ON s.id = m.session_id
            WHERE m.id = @Id AND m.session_id = @SessionId AND s.user_id = @UserId
            """,
            new { Id = id, SessionId = sessionId, UserId = _userContext.UserId });
    }

    public async Task DeleteBySessionAsync(string sessionId)
    {
        using var conn = _connectionFactory.CreateConnection();
        await conn.ExecuteAsync(
            """
            DELETE FROM messages
            WHERE session_id = @SessionId
              AND EXISTS (
                  SELECT 1
                  FROM sessions s
                  WHERE s.id = messages.session_id AND s.user_id = @UserId)
            """,
            new { SessionId = sessionId, UserId = _userContext.UserId });
    }
}
