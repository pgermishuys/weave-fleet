using Dapper;
using WeaveFleet.Application.Data;
using WeaveFleet.Domain.Entities;
using WeaveFleet.Domain.Repositories;

namespace WeaveFleet.Infrastructure.Data.Repositories;

public sealed class DapperSessionCallbackRepository(IDbConnectionFactory connectionFactory) : ISessionCallbackRepository
{
    public async Task InsertAsync(SessionCallback callback)
    {
        using var conn = connectionFactory.CreateConnection();
        await conn.ExecuteAsync(
            """
            INSERT INTO session_callbacks (id, source_session_id, target_session_id, target_instance_id, status, created_at, fired_at)
            VALUES (@Id, @SourceSessionId, @TargetSessionId, @TargetInstanceId, @Status, @CreatedAt, @FiredAt)
            """, callback);
    }

    public async Task<IReadOnlyList<SessionCallback>> GetPendingForSessionAsync(string sourceSessionId)
    {
        using var conn = connectionFactory.CreateConnection();
        var results = await conn.QueryAsync<SessionCallback>(
            "SELECT * FROM session_callbacks WHERE source_session_id = @SourceSessionId AND status = 'pending'",
            new { SourceSessionId = sourceSessionId });
        return results.AsList();
    }

    public async Task MarkFiredAsync(string id)
    {
        using var conn = connectionFactory.CreateConnection();
        await conn.ExecuteAsync(
            "UPDATE session_callbacks SET status = 'fired', fired_at = datetime('now') WHERE id = @Id",
            new { Id = id });
    }

    public async Task<bool> ClaimPendingAsync(string id)
    {
        using var conn = connectionFactory.CreateConnection();
        var rows = await conn.ExecuteAsync(
            "UPDATE session_callbacks SET status = 'claimed' WHERE id = @Id AND status = 'pending'",
            new { Id = id });
        return rows > 0;
    }

    public async Task<IReadOnlyList<SessionCallback>> GetAllPendingAsync()
    {
        using var conn = connectionFactory.CreateConnection();
        var results = await conn.QueryAsync<SessionCallback>(
            "SELECT * FROM session_callbacks WHERE status = 'pending' ORDER BY created_at ASC");
        return results.AsList();
    }

    public async Task<int> DeleteForSessionAsync(string sessionId)
    {
        using var conn = connectionFactory.CreateConnection();
        return await conn.ExecuteAsync(
            "DELETE FROM session_callbacks WHERE source_session_id = @SessionId OR target_session_id = @SessionId",
            new { SessionId = sessionId });
    }
}
