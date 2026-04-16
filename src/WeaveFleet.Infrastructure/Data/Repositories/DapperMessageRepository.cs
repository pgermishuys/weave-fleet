using Dapper;
using System.Text.Json;
using System.Text.Json.Serialization;
using WeaveFleet.Application.Data;
using WeaveFleet.Application.Services;
using WeaveFleet.Domain.Entities;
using WeaveFleet.Domain.Repositories;

namespace WeaveFleet.Infrastructure.Data.Repositories;

public sealed class DapperMessageRepository : IMessageRepository
{
    // Matches MessagePersistenceService.SerializerOptions — camelCase + omit nulls.
    // Used when round-tripping PartsJson through JsonElement to preserve property casing.
    private static readonly JsonSerializerOptions PartsJsonOptions = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
    };

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
        await UpsertAsync(conn, null, message);
    }

    public async Task UpsertAsync(System.Data.IDbConnection connection, System.Data.IDbTransaction? transaction, PersistedMessage message)
    {
        await connection.ExecuteAsync(
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
            transaction);
    }

    public async Task UpsertBatchAsync(IReadOnlyList<PersistedMessage> messages)
    {
        if (messages.Count == 0)
            return;

        using var conn = _connectionFactory.CreateConnection();
        using var tx = conn.BeginTransaction();
        try
        {
            await UpsertBatchAsync(conn, tx, messages);
            tx.Commit();
        }
        catch
        {
            tx.Rollback();
            throw;
        }
    }

    public async Task UpsertBatchAsync(System.Data.IDbConnection connection, System.Data.IDbTransaction transaction, IReadOnlyList<PersistedMessage> messages)
    {
        foreach (var message in messages)
            await UpsertAsync(connection, transaction, message);
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

    public async Task DeleteByIdAsync(string id, string sessionId)
    {
        using var conn = _connectionFactory.CreateConnection();
        await conn.ExecuteAsync(
            """
            DELETE FROM messages
            WHERE id = @Id AND session_id = @SessionId
              AND EXISTS (
                  SELECT 1
                  FROM sessions s
                  WHERE s.id = messages.session_id AND s.user_id = @UserId)
            """,
            new { Id = id, SessionId = sessionId, UserId = _userContext.UserId });
    }

    public async Task RemovePartAsync(string messageId, string sessionId, string partId)
    {
        using var conn = _connectionFactory.CreateConnection();
        var existing = await conn.QuerySingleOrDefaultAsync<PersistedMessage>(
            """
            SELECT m.*
            FROM messages m
            INNER JOIN sessions s ON s.id = m.session_id
            WHERE m.id = @MessageId AND m.session_id = @SessionId AND s.user_id = @UserId
            """,
            new { MessageId = messageId, SessionId = sessionId, UserId = _userContext.UserId });

        if (existing is null)
            return;

        var parts = JsonSerializer.Deserialize<List<JsonElement>>(existing.PartsJson, PartsJsonOptions) ?? [];
        var filtered = parts.Where(p =>
        {
            if (p.TryGetProperty("id", out var idEl) && idEl.GetString() == partId)
                return false;
            return true;
        }).ToList();

        var newPartsJson = JsonSerializer.Serialize(filtered, PartsJsonOptions);
        await conn.ExecuteAsync(
            """
            UPDATE messages SET parts_json = @PartsJson
            WHERE id = @MessageId AND session_id = @SessionId
              AND EXISTS (
                  SELECT 1
                  FROM sessions s
                  WHERE s.id = messages.session_id AND s.user_id = @UserId)
            """,
            new { PartsJson = newPartsJson, MessageId = messageId, SessionId = sessionId, UserId = _userContext.UserId });
    }
}
