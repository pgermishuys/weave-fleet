using Dapper;
using WeaveFleet.Application.Data;
using WeaveFleet.Application.Services;
using WeaveFleet.Domain.Entities;
using WeaveFleet.Domain.Repositories;

namespace WeaveFleet.Infrastructure.Data.Repositories;

public sealed class DapperSessionCallbackRepository : ISessionCallbackRepository
{
    private readonly IDbConnectionFactory _connectionFactory;

    private readonly IUserContext _userContext;

    public DapperSessionCallbackRepository(IDbConnectionFactory connectionFactory, IUserContext userContext)
    {
        _connectionFactory = connectionFactory;
        _userContext = userContext;
    }

    public async Task InsertAsync(SessionCallback callback)
    {
        using var conn = _connectionFactory.CreateConnection();
        await conn.ExecuteAsync(
            """
            INSERT INTO session_callbacks (id, source_session_id, target_session_id, target_instance_id, status, created_at, fired_at)
            SELECT @Id, @SourceSessionId, @TargetSessionId, @TargetInstanceId, @Status, @CreatedAt, @FiredAt
            FROM sessions source_session
            WHERE source_session.id = @SourceSessionId
              AND source_session.user_id = @UserId
              AND EXISTS (
                  SELECT 1 FROM sessions target_session
                  WHERE target_session.id = @TargetSessionId AND target_session.user_id = @UserId)
              AND EXISTS (
                  SELECT 1 FROM instances target_instance
                  WHERE target_instance.id = @TargetInstanceId AND target_instance.user_id = @UserId)
            """,
            new
            {
                callback.Id,
                callback.SourceSessionId,
                callback.TargetSessionId,
                callback.TargetInstanceId,
                callback.Status,
                callback.CreatedAt,
                callback.FiredAt,
                UserId = _userContext.UserId
            });
    }

    public async Task<IReadOnlyList<SessionCallback>> GetPendingForSessionAsync(string sourceSessionId)
    {
        using var conn = _connectionFactory.CreateConnection();
        var results = await conn.QueryAsync<SessionCallback>(
            """
            SELECT sc.*
            FROM session_callbacks sc
            INNER JOIN sessions source_session ON source_session.id = sc.source_session_id
            WHERE sc.source_session_id = @SourceSessionId
              AND sc.status = 'pending'
              AND source_session.user_id = @UserId
            """,
            new { SourceSessionId = sourceSessionId, UserId = _userContext.UserId });
        return results.AsList();
    }

    public async Task MarkFiredAsync(string id)
    {
        using var conn = _connectionFactory.CreateConnection();
        await conn.ExecuteAsync(
            """
            UPDATE session_callbacks
            SET status = 'fired',
                fired_at = datetime('now')
            WHERE id = @Id
              AND EXISTS (
                  SELECT 1
                  FROM sessions source_session
                  WHERE source_session.id = session_callbacks.source_session_id AND source_session.user_id = @UserId)
            """,
            new { Id = id, UserId = _userContext.UserId });
    }

    public async Task<bool> ClaimPendingAsync(string id)
    {
        using var conn = _connectionFactory.CreateConnection();
        var rows = await conn.ExecuteAsync(
            """
            UPDATE session_callbacks
            SET status = 'claimed'
            WHERE id = @Id
              AND status = 'pending'
              AND EXISTS (
                  SELECT 1
                  FROM sessions source_session
                  WHERE source_session.id = session_callbacks.source_session_id AND source_session.user_id = @UserId)
            """,
            new { Id = id, UserId = _userContext.UserId });
        return rows > 0;
    }

    public async Task<IReadOnlyList<SessionCallback>> GetAllPendingAsync()
    {
        using var conn = _connectionFactory.CreateConnection();
        var results = await conn.QueryAsync<SessionCallback>(
            """
            SELECT sc.*
            FROM session_callbacks sc
            INNER JOIN sessions source_session ON source_session.id = sc.source_session_id
            WHERE sc.status = 'pending' AND source_session.user_id = @UserId
            ORDER BY sc.created_at ASC
            """,
            new { UserId = _userContext.UserId });
        return results.AsList();
    }

    public async Task<int> DeleteForSessionAsync(string sessionId)
    {
        using var conn = _connectionFactory.CreateConnection();
        return await conn.ExecuteAsync(
            """
            DELETE FROM session_callbacks
            WHERE (
                source_session_id = @SessionId
                AND EXISTS (
                    SELECT 1
                    FROM sessions source_session
                    WHERE source_session.id = session_callbacks.source_session_id AND source_session.user_id = @UserId))
               OR (
                target_session_id = @SessionId
                AND EXISTS (
                    SELECT 1
                    FROM sessions target_session
                    WHERE target_session.id = session_callbacks.target_session_id AND target_session.user_id = @UserId))
            """,
            new { SessionId = sessionId, UserId = _userContext.UserId });
    }
}
