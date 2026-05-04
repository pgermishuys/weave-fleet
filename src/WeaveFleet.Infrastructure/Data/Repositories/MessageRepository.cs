using System.Data;
using System.Data.Common;
using System.Text.Json;
using WeaveFleet.Application.Data;
using WeaveFleet.Application.Services;
using WeaveFleet.Domain.Entities;
using WeaveFleet.Domain.Repositories;

namespace WeaveFleet.Infrastructure.Data.Repositories;

public sealed class MessageRepository : IMessageRepository
{
    private readonly IDbConnectionFactory _connectionFactory;

    private readonly IUserContext _userContext;

    public MessageRepository(IDbConnectionFactory connectionFactory, IUserContext userContext)
    {
        _connectionFactory = connectionFactory;
        _userContext = userContext;
    }

    public async Task UpsertAsync(PersistedMessage message)
    {
        using var conn = _connectionFactory.CreateConnection();
        await UpsertAsync(conn, null, message);
    }

    public async Task UpsertAsync(IDbConnection connection, IDbTransaction? transaction, PersistedMessage message)
    {
        await connection.ExecuteNonQueryAsync(
            """
            INSERT INTO messages (id, session_id, role, parts_json, timestamp, created_at, agent_name, model_id)
            SELECT @Id, @SessionId, @Role, @PartsJson, @Timestamp, @CreatedAt, @AgentName, @ModelId
            FROM sessions
            WHERE id = @SessionId AND user_id = @UserId
            ON CONFLICT(id, session_id) DO UPDATE SET
                role = excluded.role,
                parts_json = excluded.parts_json,
                timestamp = excluded.timestamp,
                -- created_at is intentionally immutable after the first insert so tail-history
                -- pagination remains anchored to durable insertion order, not later rewrites.
                agent_name = COALESCE(excluded.agent_name, messages.agent_name),
                model_id = COALESCE(excluded.model_id, messages.model_id)
            """,
            cmd =>
            {
                cmd.AddParameter("Id", message.Id);
                cmd.AddParameter("SessionId", message.SessionId);
                cmd.AddParameter("Role", message.Role);
                cmd.AddParameter("PartsJson", message.PartsJson);
                cmd.AddParameter("Timestamp", message.Timestamp);
                cmd.AddParameter("CreatedAt", message.CreatedAt);
                cmd.AddParameter("AgentName", message.AgentName);
                cmd.AddParameter("ModelId", message.ModelId);
                cmd.AddParameter("UserId", _userContext.UserId);
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

    public async Task UpsertBatchAsync(IDbConnection connection, IDbTransaction transaction, IReadOnlyList<PersistedMessage> messages)
    {
        foreach (var message in messages)
            await UpsertAsync(connection, transaction, message);
    }

    public async Task<IReadOnlyList<PersistedMessage>> GetBySessionAsync(
        string sessionId, int limit, string? beforeMessageId)
    {
        using var conn = _connectionFactory.CreateConnection();

        List<PersistedMessage> results;

        if (beforeMessageId is null)
        {
            results = await conn.QueryAsync(
                """
                SELECT m.*
                FROM messages m
                INNER JOIN sessions s ON s.id = m.session_id
                WHERE m.session_id = @SessionId AND s.user_id = @UserId
                ORDER BY m.created_at DESC, m.id DESC
                LIMIT @Limit
                """,
                cmd =>
                {
                    cmd.AddParameter("SessionId", sessionId);
                    cmd.AddParameter("Limit", limit);
                    cmd.AddParameter("UserId", _userContext.UserId);
                },
                ReadMessage);
        }
        else
        {
            var cursorCreatedAt = await conn.ExecuteScalarAsync<string>(
                """
                SELECT m.created_at
                FROM messages m
                INNER JOIN sessions s ON s.id = m.session_id
                WHERE m.session_id = @SessionId AND m.id = @Id AND s.user_id = @UserId
                """,
                cmd =>
                {
                    cmd.AddParameter("SessionId", sessionId);
                    cmd.AddParameter("Id", beforeMessageId);
                    cmd.AddParameter("UserId", _userContext.UserId);
                });

            if (cursorCreatedAt is null)
            {
                results = await conn.QueryAsync(
                    """
                    SELECT m.*
                    FROM messages m
                    INNER JOIN sessions s ON s.id = m.session_id
                    WHERE m.session_id = @SessionId AND s.user_id = @UserId
                    ORDER BY m.created_at DESC, m.id DESC
                    LIMIT @Limit
                    """,
                    cmd =>
                    {
                        cmd.AddParameter("SessionId", sessionId);
                        cmd.AddParameter("Limit", limit);
                        cmd.AddParameter("UserId", _userContext.UserId);
                    },
                    ReadMessage);
            }
            else
            {
                results = await conn.QueryAsync(
                    """
                    SELECT m.*
                    FROM messages m
                    INNER JOIN sessions s ON s.id = m.session_id
                    WHERE m.session_id = @SessionId
                      AND s.user_id = @UserId
                      AND (m.created_at < @CursorCreatedAt
                           OR (m.created_at = @CursorCreatedAt AND m.id < @CursorId))
                    ORDER BY m.created_at DESC, m.id DESC
                    LIMIT @Limit
                    """,
                    cmd =>
                    {
                        cmd.AddParameter("SessionId", sessionId);
                        cmd.AddParameter("CursorCreatedAt", cursorCreatedAt);
                        cmd.AddParameter("CursorId", beforeMessageId);
                        cmd.AddParameter("Limit", limit);
                        cmd.AddParameter("UserId", _userContext.UserId);
                    },
                    ReadMessage);
            }
        }

        // Reverse to ascending order (oldest first), matching live API behavior
        results.Reverse();
        return results;
    }

    public async Task<int> CountBySessionAsync(string sessionId)
    {
        using var conn = _connectionFactory.CreateConnection();
        var result = await conn.ExecuteScalarAsync<long>(
            """
            SELECT COUNT(*)
            FROM messages m
            INNER JOIN sessions s ON s.id = m.session_id
            WHERE m.session_id = @SessionId AND s.user_id = @UserId
            """,
            cmd =>
            {
                cmd.AddParameter("SessionId", sessionId);
                cmd.AddParameter("UserId", _userContext.UserId);
            });
        return (int)result;
    }

    public async Task<bool> HasMessagesAsync(string sessionId)
    {
        using var conn = _connectionFactory.CreateConnection();
        var result = await conn.ExecuteScalarAsync<long>(
            """
            SELECT CASE WHEN EXISTS (
                SELECT 1
                FROM messages m
                INNER JOIN sessions s ON s.id = m.session_id
                WHERE m.session_id = @SessionId AND s.user_id = @UserId)
            THEN 1 ELSE 0 END
            """,
            cmd =>
            {
                cmd.AddParameter("SessionId", sessionId);
                cmd.AddParameter("UserId", _userContext.UserId);
            });
        return result > 0;
    }

    public async Task<PersistedMessage?> GetByIdAsync(string id, string sessionId)
    {
        using var conn = _connectionFactory.CreateConnection();
        return await conn.QueryFirstOrDefaultAsync(
            """
            SELECT m.*
            FROM messages m
            INNER JOIN sessions s ON s.id = m.session_id
            WHERE m.id = @Id AND m.session_id = @SessionId AND s.user_id = @UserId
            """,
            cmd =>
            {
                cmd.AddParameter("Id", id);
                cmd.AddParameter("SessionId", sessionId);
                cmd.AddParameter("UserId", _userContext.UserId);
            },
            ReadMessage);
    }

    public async Task DeleteBySessionAsync(string sessionId)
    {
        using var conn = _connectionFactory.CreateConnection();
        await conn.ExecuteNonQueryAsync(
            """
            DELETE FROM messages
            WHERE session_id = @SessionId
              AND EXISTS (
                  SELECT 1
                  FROM sessions s
                  WHERE s.id = messages.session_id AND s.user_id = @UserId)
            """,
            cmd =>
            {
                cmd.AddParameter("SessionId", sessionId);
                cmd.AddParameter("UserId", _userContext.UserId);
            });
    }

    public async Task DeleteByIdAsync(string id, string sessionId)
    {
        using var conn = _connectionFactory.CreateConnection();
        await conn.ExecuteNonQueryAsync(
            """
            DELETE FROM messages
            WHERE id = @Id AND session_id = @SessionId
              AND EXISTS (
                  SELECT 1
                  FROM sessions s
                  WHERE s.id = messages.session_id AND s.user_id = @UserId)
            """,
            cmd =>
            {
                cmd.AddParameter("Id", id);
                cmd.AddParameter("SessionId", sessionId);
                cmd.AddParameter("UserId", _userContext.UserId);
            });
    }

    public async Task RemovePartAsync(string messageId, string sessionId, string partId)
    {
        using var conn = _connectionFactory.CreateConnection();
        var existing = await conn.QueryFirstOrDefaultAsync(
            """
            SELECT m.*
            FROM messages m
            INNER JOIN sessions s ON s.id = m.session_id
            WHERE m.id = @MessageId AND m.session_id = @SessionId AND s.user_id = @UserId
            """,
            cmd =>
            {
                cmd.AddParameter("MessageId", messageId);
                cmd.AddParameter("SessionId", sessionId);
                cmd.AddParameter("UserId", _userContext.UserId);
            },
            ReadMessage);

        if (existing is null)
            return;

        var parts = JsonSerializer.Deserialize(existing.PartsJson, InfrastructureJsonContext.Default.ListJsonElement) ?? [];
        var filtered = parts.Where(p =>
        {
            if (p.TryGetProperty("id", out var idEl) && idEl.GetString() == partId)
                return false;
            return true;
        }).ToList();

        string newPartsJson = JsonSerializer.Serialize(filtered, InfrastructureJsonContext.Default.ListJsonElement);
        await conn.ExecuteNonQueryAsync(
            """
            UPDATE messages SET parts_json = @PartsJson
            WHERE id = @MessageId AND session_id = @SessionId
              AND EXISTS (
                  SELECT 1
                  FROM sessions s
                  WHERE s.id = messages.session_id AND s.user_id = @UserId)
            """,
            cmd =>
            {
                cmd.AddParameter("PartsJson", newPartsJson);
                cmd.AddParameter("MessageId", messageId);
                cmd.AddParameter("SessionId", sessionId);
                cmd.AddParameter("UserId", _userContext.UserId);
            });
    }

    private static PersistedMessage ReadMessage(DbDataReader r) => new()
    {
        Id = r.GetString(r.GetOrdinal("id")),
        SessionId = r.GetString(r.GetOrdinal("session_id")),
        Role = r.GetString(r.GetOrdinal("role")),
        PartsJson = r.GetString(r.GetOrdinal("parts_json")),
        Timestamp = r.GetString(r.GetOrdinal("timestamp")),
        CreatedAt = r.GetString(r.GetOrdinal("created_at")),
        AgentName = r.GetNullableString(r.GetOrdinal("agent_name")),
        ModelId = r.GetNullableString(r.GetOrdinal("model_id")),
    };
}
