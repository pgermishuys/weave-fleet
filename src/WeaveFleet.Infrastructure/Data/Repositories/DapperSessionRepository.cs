using System.Text;
using Dapper;
using WeaveFleet.Application.Data;
using WeaveFleet.Domain.Entities;
using WeaveFleet.Domain.Repositories;

namespace WeaveFleet.Infrastructure.Data.Repositories;

public sealed class DapperSessionRepository(IDbConnectionFactory connectionFactory) : ISessionRepository
{
    private static readonly string[] TerminalStatuses = ["stopped", "completed", "error"];

    public async Task InsertAsync(Session session)
    {
        using var conn = connectionFactory.CreateConnection();
        await conn.ExecuteAsync(
            """
            INSERT INTO sessions (id, workspace_id, instance_id, project_id, opencode_session_id, title,
                status, directory, created_at, stopped_at, parent_session_id, activity_status,
                lifecycle_status, total_tokens, total_cost)
            VALUES (@Id, @WorkspaceId, @InstanceId, @ProjectId, @OpencodeSessionId, @Title,
                @Status, @Directory, @CreatedAt, @StoppedAt, @ParentSessionId, @ActivityStatus,
                @LifecycleStatus, @TotalTokens, @TotalCost)
            """, session);
    }

    public async Task<Session?> GetByIdAsync(string id)
    {
        using var conn = connectionFactory.CreateConnection();
        return await conn.QueryFirstOrDefaultAsync<Session>(
            "SELECT * FROM sessions WHERE id = @Id", new { Id = id });
    }

    public async Task<Session?> GetByHarnessIdAsync(string harnessSessionId)
    {
        using var conn = connectionFactory.CreateConnection();
        return await conn.QueryFirstOrDefaultAsync<Session>(
            "SELECT * FROM sessions WHERE opencode_session_id = @HarnessSessionId LIMIT 1",
            new { HarnessSessionId = harnessSessionId });
    }

    public async Task<IReadOnlyList<Session>> ListAsync(
        int limit = 100,
        int offset = 0,
        IReadOnlyList<string>? statuses = null,
        string? projectId = null)
    {
        using var conn = connectionFactory.CreateConnection();

        var sql = new StringBuilder("SELECT * FROM sessions");
        var conditions = new List<string>();

        if (statuses is { Count: > 0 })
            conditions.Add("status IN @Statuses");
        if (projectId is not null)
            conditions.Add("project_id = @ProjectId");

        if (conditions.Count > 0)
            sql.Append(" WHERE ").Append(string.Join(" AND ", conditions));

        sql.Append(" ORDER BY created_at DESC LIMIT @Limit OFFSET @Offset");

        var parameters = new
        {
            Statuses = statuses,
            ProjectId = projectId,
            Limit = limit,
            Offset = offset
        };

        var results = await conn.QueryAsync<Session>(sql.ToString(), parameters);
        return results.AsList();
    }

    public async Task DeleteByProjectIdAsync(string projectId)
    {
        using var conn = connectionFactory.CreateConnection();
        await conn.ExecuteAsync(
            "DELETE FROM sessions WHERE project_id = @ProjectId",
            new { ProjectId = projectId });
    }

    public async Task<int> CountAsync(IReadOnlyList<string>? statuses = null)
    {
        using var conn = connectionFactory.CreateConnection();

        if (statuses is { Count: > 0 })
        {
            return await conn.ExecuteScalarAsync<int>(
                "SELECT COUNT(*) FROM sessions WHERE status IN @Statuses",
                new { Statuses = statuses });
        }

        return await conn.ExecuteScalarAsync<int>("SELECT COUNT(*) FROM sessions");
    }

    public async Task<(int Active, int Idle)> GetStatusCountsAsync()
    {
        using var conn = connectionFactory.CreateConnection();
        var rows = await conn.QueryAsync<(string ActivityStatus, int Count)>(
            """
            SELECT activity_status, COUNT(*) as count
            FROM sessions
            WHERE status = 'active'
            GROUP BY activity_status
            """);

        var active = 0;
        var idle = 0;
        foreach (var (activityStatus, count) in rows)
        {
            if (activityStatus == "working")
                active += count;
            else
                idle += count;
        }

        return (active, idle);
    }

    public async Task<IReadOnlyList<Session>> ListActiveAsync()
    {
        using var conn = connectionFactory.CreateConnection();
        var results = await conn.QueryAsync<Session>(
            "SELECT * FROM sessions WHERE status = 'active' ORDER BY created_at DESC");
        return results.AsList();
    }

    public async Task UpdateStatusAsync(string id, string status, string? stoppedAt = null)
    {
        using var conn = connectionFactory.CreateConnection();
        await conn.ExecuteAsync(
            "UPDATE sessions SET status = @Status, stopped_at = @StoppedAt WHERE id = @Id",
            new { Id = id, Status = status, StoppedAt = stoppedAt });
    }

    public async Task<IReadOnlyList<Session>> GetForInstanceAsync(string instanceId)
    {
        using var conn = connectionFactory.CreateConnection();
        var results = await conn.QueryAsync<Session>(
            "SELECT * FROM sessions WHERE instance_id = @InstanceId ORDER BY created_at DESC",
            new { InstanceId = instanceId });
        return results.AsList();
    }

    public async Task<Session?> GetAnyForInstanceAsync(string instanceId)
    {
        using var conn = connectionFactory.CreateConnection();
        return await conn.QueryFirstOrDefaultAsync<Session>(
            "SELECT * FROM sessions WHERE instance_id = @InstanceId LIMIT 1",
            new { InstanceId = instanceId });
    }

    public async Task<IReadOnlyList<Session>> GetNonTerminalForInstanceAsync(string instanceId)
    {
        using var conn = connectionFactory.CreateConnection();
        var results = await conn.QueryAsync<Session>(
            "SELECT * FROM sessions WHERE instance_id = @InstanceId AND status NOT IN @TerminalStatuses",
            new { InstanceId = instanceId, TerminalStatuses });
        return results.AsList();
    }

    public async Task UpdateTitleAsync(string id, string title)
    {
        using var conn = connectionFactory.CreateConnection();
        await conn.ExecuteAsync(
            "UPDATE sessions SET title = @Title WHERE id = @Id",
            new { Id = id, Title = title });
    }

    public async Task UpdateForResumeAsync(string id, string instanceId)
    {
        using var conn = connectionFactory.CreateConnection();
        await conn.ExecuteAsync(
            "UPDATE sessions SET instance_id = @InstanceId, status = 'active', stopped_at = NULL WHERE id = @Id",
            new { Id = id, InstanceId = instanceId });
    }

    public async Task<IReadOnlyList<Session>> GetActiveChildrenAsync(string parentDbId)
    {
        using var conn = connectionFactory.CreateConnection();
        var results = await conn.QueryAsync<Session>(
            "SELECT * FROM sessions WHERE parent_session_id = @ParentId AND status = 'active'",
            new { ParentId = parentDbId });
        return results.AsList();
    }

    public async Task<IReadOnlySet<string>> GetIdsWithActiveChildrenAsync()
    {
        using var conn = connectionFactory.CreateConnection();
        var ids = await conn.QueryAsync<string>(
            """
            SELECT DISTINCT parent_session_id
            FROM sessions
            WHERE parent_session_id IS NOT NULL AND status = 'active'
            """);
        return ids.ToHashSet();
    }

    public async Task<IReadOnlyList<Session>> GetForWorkspaceAsync(string workspaceId)
    {
        using var conn = connectionFactory.CreateConnection();
        var results = await conn.QueryAsync<Session>(
            "SELECT * FROM sessions WHERE workspace_id = @WorkspaceId ORDER BY created_at DESC",
            new { WorkspaceId = workspaceId });
        return results.AsList();
    }

    public async Task<bool> DeleteAsync(string id)
    {
        using var conn = connectionFactory.CreateConnection();
        var rows = await conn.ExecuteAsync(
            "DELETE FROM sessions WHERE id = @Id", new { Id = id });
        return rows > 0;
    }

    public async Task<(int TotalTokens, double TotalCost)?> IncrementTokensAsync(
        string id, int tokens, double cost)
    {
        using var conn = connectionFactory.CreateConnection();
        await conn.ExecuteAsync(
            "UPDATE sessions SET total_tokens = total_tokens + @Tokens, total_cost = total_cost + @Cost WHERE id = @Id",
            new { Id = id, Tokens = tokens, Cost = cost });

        var result = await conn.QueryFirstOrDefaultAsync<(int TotalTokens, double TotalCost)?>(
            "SELECT total_tokens, total_cost FROM sessions WHERE id = @Id",
            new { Id = id });

        return result;
    }

    public async Task<(int TotalTokens, double TotalCost)> GetFleetTokenTotalsAsync()
    {
        using var conn = connectionFactory.CreateConnection();
        var result = await conn.QueryFirstAsync<(int TotalTokens, double TotalCost)>(
            "SELECT COALESCE(SUM(total_tokens), 0) as total_tokens, COALESCE(SUM(total_cost), 0.0) as total_cost FROM sessions");
        return result;
    }

    public async Task<int> MarkAllNonTerminalStoppedAsync(string stoppedAt)
    {
        using var conn = connectionFactory.CreateConnection();
        return await conn.ExecuteAsync(
            "UPDATE sessions SET status = 'stopped', stopped_at = @StoppedAt WHERE status NOT IN @TerminalStatuses",
            new { StoppedAt = stoppedAt, TerminalStatuses });
    }

    public async Task UpdateProjectAsync(string id, string? projectId)
    {
        using var conn = connectionFactory.CreateConnection();
        await conn.ExecuteAsync(
            "UPDATE sessions SET project_id = @ProjectId WHERE id = @Id",
            new { Id = id, ProjectId = projectId });
    }
}
