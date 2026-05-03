using System.Data.Common;
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
        await conn.ExecuteNonQueryAsync(
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
            cmd =>
            {
                cmd.AddParameter("Id", callback.Id);
                cmd.AddParameter("SourceSessionId", callback.SourceSessionId);
                cmd.AddParameter("TargetSessionId", callback.TargetSessionId);
                cmd.AddParameter("TargetInstanceId", callback.TargetInstanceId);
                cmd.AddParameter("Status", callback.Status);
                cmd.AddParameter("CreatedAt", callback.CreatedAt);
                cmd.AddParameter("FiredAt", callback.FiredAt);
                cmd.AddParameter("UserId", _userContext.UserId);
            });
    }

    public async Task<IReadOnlyList<SessionCallback>> GetPendingForSessionAsync(string sourceSessionId)
    {
        using var conn = _connectionFactory.CreateConnection();
        return await conn.QueryAsync(
            """
            SELECT sc.*
            FROM session_callbacks sc
            INNER JOIN sessions source_session ON source_session.id = sc.source_session_id
            WHERE sc.source_session_id = @SourceSessionId
              AND sc.status = 'pending'
              AND source_session.user_id = @UserId
            """,
            cmd =>
            {
                cmd.AddParameter("SourceSessionId", sourceSessionId);
                cmd.AddParameter("UserId", _userContext.UserId);
            },
            ReadSessionCallback);
    }

    public async Task MarkFiredAsync(string id)
    {
        using var conn = _connectionFactory.CreateConnection();
        await conn.ExecuteNonQueryAsync(
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
            cmd =>
            {
                cmd.AddParameter("Id", id);
                cmd.AddParameter("UserId", _userContext.UserId);
            });
    }

    public async Task<bool> ClaimPendingAsync(string id)
    {
        using var conn = _connectionFactory.CreateConnection();
        var rows = await conn.ExecuteNonQueryAsync(
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
            cmd =>
            {
                cmd.AddParameter("Id", id);
                cmd.AddParameter("UserId", _userContext.UserId);
            });
        return rows > 0;
    }

    public async Task<IReadOnlyList<SessionCallback>> GetAllPendingAsync()
    {
        using var conn = _connectionFactory.CreateConnection();
        return await conn.QueryAsync(
            """
            SELECT sc.*
            FROM session_callbacks sc
            INNER JOIN sessions source_session ON source_session.id = sc.source_session_id
            WHERE sc.status = 'pending' AND source_session.user_id = @UserId
            ORDER BY sc.created_at ASC
            """,
            cmd => { cmd.AddParameter("UserId", _userContext.UserId); },
            ReadSessionCallback);
    }

    public async Task<int> DeleteForSessionAsync(string sessionId)
    {
        using var conn = _connectionFactory.CreateConnection();
        return await conn.ExecuteNonQueryAsync(
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
            cmd =>
            {
                cmd.AddParameter("SessionId", sessionId);
                cmd.AddParameter("UserId", _userContext.UserId);
            });
    }

    private static SessionCallback ReadSessionCallback(DbDataReader r) => new()
    {
        Id = r.GetString(r.GetOrdinal("id")),
        SourceSessionId = r.GetString(r.GetOrdinal("source_session_id")),
        TargetSessionId = r.GetString(r.GetOrdinal("target_session_id")),
        TargetInstanceId = r.GetString(r.GetOrdinal("target_instance_id")),
        Status = r.GetString(r.GetOrdinal("status")),
        CreatedAt = r.GetString(r.GetOrdinal("created_at")),
        FiredAt = r.GetNullableString(r.GetOrdinal("fired_at")),
    };
}
