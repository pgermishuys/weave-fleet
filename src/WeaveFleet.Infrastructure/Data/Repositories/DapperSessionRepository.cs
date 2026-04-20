using System.Text;
using Dapper;
using WeaveFleet.Application.Data;
using WeaveFleet.Application.Services;
using WeaveFleet.Domain.Entities;
using WeaveFleet.Domain.Repositories;

namespace WeaveFleet.Infrastructure.Data.Repositories;

public sealed class DapperSessionRepository(
    IDbConnectionFactory connectionFactory,
    IUserContext userContext) : ISessionRepository
{
    private static readonly string[] TerminalStatuses = ["stopped", "completed", "error"];

    public async Task InsertAsync(Session session)
    {
        using var conn = connectionFactory.CreateConnection();
        await InsertAsync(conn, null, session);
    }

    public async Task InsertAsync(System.Data.IDbConnection connection, System.Data.IDbTransaction? transaction, Session session)
    {
        var insertUserId = string.IsNullOrWhiteSpace(session.UserId)
            ? userContext.UserId
            : session.UserId;

        await connection.ExecuteAsync(
            """
            INSERT INTO sessions (id, workspace_id, instance_id, project_id, opencode_session_id, title,
                status, directory, created_at, stopped_at, parent_session_id, activity_status,
                lifecycle_status, retention_status, archived_at, is_hidden, total_tokens, total_cost,
                harness_type, harness_resume_token, user_id)
            SELECT @Id, @WorkspaceId, @InstanceId, @ProjectId, @OpencodeSessionId, @Title,
                @Status, @Directory, @CreatedAt, @StoppedAt, @ParentSessionId, @ActivityStatus,
                @LifecycleStatus, @RetentionStatus, @ArchivedAt, @IsHidden, @TotalTokens, @TotalCost,
                @HarnessType, @HarnessResumeToken, @UserId
            FROM workspaces workspace_row
            WHERE workspace_row.id = @WorkspaceId
              AND workspace_row.user_id = @UserId
              AND EXISTS (
                  SELECT 1 FROM instances instance_row
                  WHERE instance_row.id = @InstanceId AND instance_row.user_id = @UserId)
              AND (
                  @ProjectId IS NULL
                  OR EXISTS (
                      SELECT 1 FROM projects project_row
                      WHERE project_row.id = @ProjectId AND project_row.user_id = @UserId))
            """,
            new
            {
                session.Id,
                session.WorkspaceId,
                session.InstanceId,
                session.ProjectId,
                session.OpencodeSessionId,
                session.Title,
                session.Status,
                session.Directory,
                session.CreatedAt,
                session.StoppedAt,
                session.ParentSessionId,
                session.ActivityStatus,
                session.LifecycleStatus,
                session.RetentionStatus,
                session.ArchivedAt,
                session.IsHidden,
                session.TotalTokens,
                session.TotalCost,
                session.HarnessType,
                session.HarnessResumeToken,
                UserId = insertUserId
            },
            transaction);
    }

    public async Task<Session?> GetByIdAsync(string id)
    {
        using var conn = connectionFactory.CreateConnection();
        return await conn.QueryFirstOrDefaultAsync<Session>(
            "SELECT * FROM sessions WHERE id = @Id AND user_id = @UserId",
            new { Id = id, UserId = userContext.UserId });
    }

    public async Task<Session?> GetByHarnessIdAsync(string harnessSessionId)
    {
        using var conn = connectionFactory.CreateConnection();
        return await conn.QueryFirstOrDefaultAsync<Session>(
            "SELECT * FROM sessions WHERE opencode_session_id = @HarnessSessionId AND user_id = @UserId LIMIT 1",
            new { HarnessSessionId = harnessSessionId, UserId = userContext.UserId });
    }

    public async Task<IReadOnlyList<Session>> ListAsync(
        int limit = 100,
        int offset = 0,
        IReadOnlyList<string>? statuses = null,
        string? projectId = null)
        => await ListAsync(limit, offset, statuses, projectId, retentionStatuses: null);

    public async Task<IReadOnlyList<Session>> ListAsync(
        int limit,
        int offset,
        IReadOnlyList<string>? statuses,
        string? projectId,
        IReadOnlyList<string>? retentionStatuses)
    {
        using var conn = connectionFactory.CreateConnection();

        var sql = new StringBuilder("SELECT * FROM sessions WHERE user_id = @UserId AND parent_session_id IS NULL");
        var conditions = new List<string>();

        if (statuses is { Count: > 0 })
            conditions.Add("status IN @Statuses");
        if (projectId is not null)
            conditions.Add("project_id = @ProjectId");
        if (retentionStatuses is { Count: > 0 })
            conditions.Add("retention_status IN @RetentionStatuses");

        if (conditions.Count > 0)
            sql.Append(" AND ").Append(string.Join(" AND ", conditions));

        sql.Append(" ORDER BY created_at DESC LIMIT @Limit OFFSET @Offset");

        var parameters = new
        {
            UserId = userContext.UserId,
            Statuses = statuses,
            ProjectId = projectId,
            RetentionStatuses = retentionStatuses,
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
            "DELETE FROM sessions WHERE project_id = @ProjectId AND user_id = @UserId",
            new { ProjectId = projectId, UserId = userContext.UserId });
    }

    public async Task<int> CountAsync(IReadOnlyList<string>? statuses = null)
        => await CountAsync(statuses, retentionStatuses: null);

    public async Task<int> CountAsync(
        IReadOnlyList<string>? statuses,
        IReadOnlyList<string>? retentionStatuses)
    {
        using var conn = connectionFactory.CreateConnection();

        var sql = new StringBuilder("SELECT COUNT(*) FROM sessions WHERE user_id = @UserId");

        if (statuses is { Count: > 0 })
            sql.Append(" AND status IN @Statuses");
        if (retentionStatuses is { Count: > 0 })
            sql.Append(" AND retention_status IN @RetentionStatuses");

        return await conn.ExecuteScalarAsync<int>(
            sql.ToString(),
            new { UserId = userContext.UserId, Statuses = statuses, RetentionStatuses = retentionStatuses });
    }

    public async Task<(int Active, int Idle)> GetStatusCountsAsync()
    {
        using var conn = connectionFactory.CreateConnection();
        var rows = await conn.QueryAsync<(string ActivityStatus, int Count)>(
            """
            SELECT activity_status, COUNT(*) as count
            FROM sessions
            WHERE status = 'active' AND user_id = @UserId
            GROUP BY activity_status
            """,
            new { UserId = userContext.UserId });

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
        => await ListActiveAsync(retentionStatuses: null);

    public async Task<IReadOnlyList<Session>> ListActiveAsync(IReadOnlyList<string>? retentionStatuses)
    {
        using var conn = connectionFactory.CreateConnection();
        var sql = new StringBuilder("SELECT * FROM sessions WHERE status = 'active' AND user_id = @UserId");
        if (retentionStatuses is { Count: > 0 })
            sql.Append(" AND retention_status IN @RetentionStatuses");

        sql.Append(" ORDER BY created_at DESC");

        var results = await conn.QueryAsync<Session>(
            sql.ToString(),
            new { UserId = userContext.UserId, RetentionStatuses = retentionStatuses });
        return results.AsList();
    }

    public async Task UpdateStatusAsync(string id, string status, string? stoppedAt = null)
    {
        using var conn = connectionFactory.CreateConnection();
        await UpdateStatusAsync(conn, null, id, status, stoppedAt);
    }

    public async Task UpdateStatusAsync(System.Data.IDbConnection connection, System.Data.IDbTransaction? transaction, string id, string status, string? stoppedAt)
    {
        var lifecycleStatus = status switch
        {
            "stopped" => "stopped",
            "completed" => "completed",
            _ => "running"
        };
        await connection.ExecuteAsync(
            "UPDATE sessions SET status = @Status, stopped_at = @StoppedAt, lifecycle_status = @LifecycleStatus WHERE id = @Id AND user_id = @UserId",
            new { Id = id, Status = status, StoppedAt = stoppedAt, LifecycleStatus = lifecycleStatus, UserId = userContext.UserId },
            transaction);
    }

    public async Task ArchiveAsync(string id, string archivedAt)
    {
        using var conn = connectionFactory.CreateConnection();
        await ArchiveAsync(conn, null, id, archivedAt);
    }

    public async Task ArchiveAsync(System.Data.IDbConnection connection, System.Data.IDbTransaction? transaction, string id, string archivedAt)
    {
        await connection.ExecuteAsync(
            "UPDATE sessions SET retention_status = 'archived', archived_at = @ArchivedAt WHERE id = @Id AND user_id = @UserId",
            new { Id = id, ArchivedAt = archivedAt, UserId = userContext.UserId },
            transaction);
    }

    public async Task UnarchiveAsync(string id)
    {
        using var conn = connectionFactory.CreateConnection();
        await UnarchiveAsync(conn, null, id);
    }

    public async Task UnarchiveAsync(System.Data.IDbConnection connection, System.Data.IDbTransaction? transaction, string id)
    {
        await connection.ExecuteAsync(
            "UPDATE sessions SET retention_status = 'active', archived_at = NULL WHERE id = @Id AND user_id = @UserId",
            new { Id = id, UserId = userContext.UserId },
            transaction);
    }

    public async Task<IReadOnlyList<Session>> GetForInstanceAsync(string instanceId)
    {
        using var conn = connectionFactory.CreateConnection();
        var results = await conn.QueryAsync<Session>(
            "SELECT * FROM sessions WHERE instance_id = @InstanceId AND user_id = @UserId ORDER BY created_at DESC",
            new { InstanceId = instanceId, UserId = userContext.UserId });
        return results.AsList();
    }

    public async Task<Session?> GetAnyForInstanceAsync(string instanceId)
    {
        // System-level lookup — no user filter (used by HarnessEventRelay and recovery)
        using var conn = connectionFactory.CreateConnection();
        return await conn.QueryFirstOrDefaultAsync<Session>(
            "SELECT * FROM sessions WHERE instance_id = @InstanceId LIMIT 1",
            new { InstanceId = instanceId });
    }

    public async Task<IReadOnlyList<Session>> GetNonTerminalForInstanceAsync(string instanceId)
    {
        // System-level lookup — no user filter (used by instance stop recovery)
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
            "UPDATE sessions SET title = @Title WHERE id = @Id AND user_id = @UserId",
            new { Id = id, Title = title, UserId = userContext.UserId });
    }

    public async Task UpdateForResumeAsync(string id, string instanceId)
    {
        using var conn = connectionFactory.CreateConnection();
        await conn.ExecuteAsync(
            """
            UPDATE sessions
            SET instance_id = @InstanceId,
                status = 'active',
                stopped_at = NULL,
                lifecycle_status = 'running',
                activity_status = NULL
            WHERE id = @Id
              AND user_id = @UserId
              AND EXISTS (
                  SELECT 1 FROM instances instance_row
                  WHERE instance_row.id = @InstanceId AND instance_row.user_id = @UserId)
            """,
            new { Id = id, InstanceId = instanceId, UserId = userContext.UserId });
    }

    public async Task UpdateResumeTokenAsync(string id, string resumeToken)
    {
        using var conn = connectionFactory.CreateConnection();
        await conn.ExecuteAsync(
            "UPDATE sessions SET harness_resume_token = @ResumeToken WHERE id = @Id AND user_id = @UserId",
            new { Id = id, ResumeToken = resumeToken, UserId = userContext.UserId });
    }

    public async Task<IReadOnlyList<Session>> GetActiveChildrenAsync(string parentDbId)
    {
        // Child sessions are owned by the same user as the parent; filter by user for safety
        using var conn = connectionFactory.CreateConnection();
        var results = await conn.QueryAsync<Session>(
            "SELECT * FROM sessions WHERE parent_session_id = @ParentId AND status = 'active' AND user_id = @UserId",
            new { ParentId = parentDbId, UserId = userContext.UserId });
        return results.AsList();
    }

    public async Task<IReadOnlySet<string>> GetIdsWithActiveChildrenAsync()
    {
        using var conn = connectionFactory.CreateConnection();
        var ids = await conn.QueryAsync<string>(
            """
            SELECT DISTINCT parent_session_id
            FROM sessions
            WHERE parent_session_id IS NOT NULL AND status = 'active' AND user_id = @UserId
            """,
            new { UserId = userContext.UserId });
        return ids.ToHashSet();
    }

    public async Task<IReadOnlyList<Session>> GetForWorkspaceAsync(string workspaceId)
        => await GetForWorkspaceAsync(workspaceId, retentionStatuses: null);

    public async Task<IReadOnlyList<Session>> GetForWorkspaceAsync(
        string workspaceId,
        IReadOnlyList<string>? retentionStatuses)
    {
        using var conn = connectionFactory.CreateConnection();
        var sql = new StringBuilder("SELECT * FROM sessions WHERE workspace_id = @WorkspaceId AND user_id = @UserId");
        if (retentionStatuses is { Count: > 0 })
            sql.Append(" AND retention_status IN @RetentionStatuses");

        sql.Append(" ORDER BY created_at DESC");

        var results = await conn.QueryAsync<Session>(
            sql.ToString(),
            new { WorkspaceId = workspaceId, UserId = userContext.UserId, RetentionStatuses = retentionStatuses });
        return results.AsList();
    }

    public async Task<bool> DeleteAsync(string id)
    {
        using var conn = connectionFactory.CreateConnection();
        return await DeleteAsync(conn, null, id);
    }

    public async Task<bool> DeleteAsync(System.Data.IDbConnection connection, System.Data.IDbTransaction? transaction, string id)
    {
        var rows = await connection.ExecuteAsync(
            "DELETE FROM sessions WHERE id = @Id AND user_id = @UserId",
            new { Id = id, UserId = userContext.UserId },
            transaction);
        return rows > 0;
    }

    public async Task<(int TotalTokens, double TotalCost)?> IncrementTokensAsync(
        string id, int tokens, double cost)
    {
        using var conn = connectionFactory.CreateConnection();
        await conn.ExecuteAsync(
            "UPDATE sessions SET total_tokens = total_tokens + @Tokens, total_cost = total_cost + @Cost WHERE id = @Id AND user_id = @UserId",
            new { Id = id, Tokens = tokens, Cost = cost, UserId = userContext.UserId });

        var result = await conn.QueryFirstOrDefaultAsync<(int TotalTokens, double TotalCost)?>(
            "SELECT total_tokens, total_cost FROM sessions WHERE id = @Id AND user_id = @UserId",
            new { Id = id, UserId = userContext.UserId });

        return result;
    }

    public async Task<(int TotalTokens, double TotalCost)> GetFleetTokenTotalsAsync()
    {
        using var conn = connectionFactory.CreateConnection();
        var result = await conn.QueryFirstAsync<(int TotalTokens, double TotalCost)>(
            "SELECT COALESCE(SUM(total_tokens), 0) as total_tokens, COALESCE(SUM(total_cost), 0.0) as total_cost FROM sessions WHERE user_id = @UserId",
            new { UserId = userContext.UserId });
        return result;
    }

    public async Task<int> MarkAllNonTerminalStoppedAsync(string stoppedAt)
    {
        // System-level recovery operation — no user filter
        using var conn = connectionFactory.CreateConnection();
        return await conn.ExecuteAsync(
            "UPDATE sessions SET status = 'stopped', stopped_at = @StoppedAt, lifecycle_status = 'stopped' WHERE status NOT IN @TerminalStatuses",
            new { StoppedAt = stoppedAt, TerminalStatuses });
    }

    public async Task UpdateProjectAsync(string id, string? projectId)
    {
        using var conn = connectionFactory.CreateConnection();
        await conn.ExecuteAsync(
            "UPDATE sessions SET project_id = @ProjectId WHERE id = @Id AND user_id = @UserId",
            new { Id = id, ProjectId = projectId, UserId = userContext.UserId });
    }
}
