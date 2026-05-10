using System.Data;
using System.Data.Common;
using System.Text;
using WeaveFleet.Application.Data;
using WeaveFleet.Application.Services;
using WeaveFleet.Domain.Entities;
using WeaveFleet.Domain.Repositories;

namespace WeaveFleet.Infrastructure.Data.Repositories;

public sealed class SessionRepository(
    IDbConnectionFactory connectionFactory,
    IUserContext userContext) : ISessionRepository
{
    public async Task InsertAsync(Session session)
    {
        using var conn = connectionFactory.CreateConnection();
        await InsertAsync(conn, null, session);
    }

    public async Task InsertAsync(IDbConnection connection, IDbTransaction? transaction, Session session)
    {
        var insertUserId = string.IsNullOrWhiteSpace(session.UserId)
            ? userContext.UserId
            : session.UserId;

        await connection.ExecuteNonQueryAsync(
            """
            INSERT INTO sessions (id, workspace_id, instance_id, project_id, opencode_session_id, title,
                status, directory, created_at, stopped_at, parent_session_id, activity_status,
                lifecycle_status, retention_status, archived_at, is_hidden, total_tokens, total_cost,
                harness_type, harness_resume_token, user_id, view_mode)
            SELECT @Id, @WorkspaceId, @InstanceId, @ProjectId, @OpencodeSessionId, @Title,
                @Status, @Directory, @CreatedAt, @StoppedAt, @ParentSessionId, @ActivityStatus,
                @LifecycleStatus, @RetentionStatus, @ArchivedAt, @IsHidden, @TotalTokens, @TotalCost,
                @HarnessType, @HarnessResumeToken, @UserId, @ViewMode
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
            cmd =>
            {
                cmd.AddParameter("Id", session.Id);
                cmd.AddParameter("WorkspaceId", session.WorkspaceId);
                cmd.AddParameter("InstanceId", session.InstanceId);
                cmd.AddParameter("ProjectId", session.ProjectId);
                cmd.AddParameter("OpencodeSessionId", session.OpencodeSessionId);
                cmd.AddParameter("Title", session.Title);
                cmd.AddParameter("Status", session.Status);
                cmd.AddParameter("Directory", session.Directory);
                cmd.AddParameter("CreatedAt", session.CreatedAt);
                cmd.AddParameter("StoppedAt", session.StoppedAt);
                cmd.AddParameter("ParentSessionId", session.ParentSessionId);
                cmd.AddParameter("ActivityStatus", session.ActivityStatus);
                cmd.AddParameter("LifecycleStatus", session.LifecycleStatus);
                cmd.AddParameter("RetentionStatus", session.RetentionStatus);
                cmd.AddParameter("ArchivedAt", session.ArchivedAt);
                cmd.AddParameter("IsHidden", session.IsHidden);
                cmd.AddParameter("TotalTokens", session.TotalTokens);
                cmd.AddParameter("TotalCost", session.TotalCost);
                cmd.AddParameter("HarnessType", session.HarnessType);
                cmd.AddParameter("HarnessResumeToken", session.HarnessResumeToken);
                cmd.AddParameter("UserId", insertUserId);
                cmd.AddParameter("ViewMode", session.ViewMode);
            },
            transaction);
    }

    public async Task<Session?> GetByIdAsync(string id)
    {
        using var conn = connectionFactory.CreateConnection();
        return await conn.QueryFirstOrDefaultAsync(
            "SELECT * FROM sessions WHERE id = @Id AND user_id = @UserId",
            cmd =>
            {
                cmd.AddParameter("Id", id);
                cmd.AddParameter("UserId", userContext.UserId);
            },
            ReadSession);
    }

    public async Task<Session?> GetByHarnessIdAsync(string harnessSessionId)
    {
        using var conn = connectionFactory.CreateConnection();
        return await conn.QueryFirstOrDefaultAsync(
            "SELECT * FROM sessions WHERE opencode_session_id = @HarnessSessionId AND user_id = @UserId LIMIT 1",
            cmd =>
            {
                cmd.AddParameter("HarnessSessionId", harnessSessionId);
                cmd.AddParameter("UserId", userContext.UserId);
            },
            ReadSession);
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
        var dbConn = (DbConnection)conn;
        await using var cmd = dbConn.CreateCommand();

        var sql = new StringBuilder("SELECT * FROM sessions WHERE user_id = @UserId AND parent_session_id IS NULL");
        cmd.AddParameter("UserId", userContext.UserId);
        cmd.AddParameter("Limit", limit);
        cmd.AddParameter("Offset", offset);

        if (statuses is { Count: > 0 })
        {
            sql.Append(" AND status ");
            SqlInExpander.AppendInClause(sql, cmd, "Status", statuses);
        }
        if (projectId is not null)
        {
            sql.Append(" AND project_id = @ProjectId");
            cmd.AddParameter("ProjectId", projectId);
        }
        if (retentionStatuses is { Count: > 0 })
        {
            sql.Append(" AND retention_status ");
            SqlInExpander.AppendInClause(sql, cmd, "RetentionStatus", retentionStatuses);
        }

        sql.Append(" ORDER BY created_at DESC LIMIT @Limit OFFSET @Offset");

        cmd.CommandText = sql.ToString();
        await using var reader = await cmd.ExecuteReaderAsync();
        var list = new List<Session>();
        while (await reader.ReadAsync())
            list.Add(ReadSession(reader));
        return list;
    }

    public async Task<IReadOnlyList<Session>> ListAsync(
        int limit,
        int offset,
        IReadOnlyList<string>? statuses,
        string? projectId,
        IReadOnlyList<string>? retentionStatuses,
        string viewMode)
    {
        using var conn = connectionFactory.CreateConnection();
        var dbConn = (DbConnection)conn;
        await using var cmd = dbConn.CreateCommand();

        var sql = new StringBuilder("SELECT * FROM sessions WHERE user_id = @UserId AND parent_session_id IS NULL AND view_mode = @ViewMode");
        cmd.AddParameter("UserId", userContext.UserId);
        cmd.AddParameter("ViewMode", viewMode);
        cmd.AddParameter("Limit", limit);
        cmd.AddParameter("Offset", offset);

        if (statuses is { Count: > 0 })
        {
            sql.Append(" AND status ");
            SqlInExpander.AppendInClause(sql, cmd, "Status", statuses);
        }
        if (projectId is not null)
        {
            sql.Append(" AND project_id = @ProjectId");
            cmd.AddParameter("ProjectId", projectId);
        }
        if (retentionStatuses is { Count: > 0 })
        {
            sql.Append(" AND retention_status ");
            SqlInExpander.AppendInClause(sql, cmd, "RetentionStatus", retentionStatuses);
        }

        sql.Append(" ORDER BY created_at DESC LIMIT @Limit OFFSET @Offset");

        cmd.CommandText = sql.ToString();
        await using var reader = await cmd.ExecuteReaderAsync();
        var list = new List<Session>();
        while (await reader.ReadAsync())
            list.Add(ReadSession(reader));
        return list;
    }

    public async Task DeleteByProjectIdAsync(string projectId)
    {
        using var conn = connectionFactory.CreateConnection();
        await conn.ExecuteNonQueryAsync(
            "DELETE FROM sessions WHERE project_id = @ProjectId AND user_id = @UserId",
            cmd =>
            {
                cmd.AddParameter("ProjectId", projectId);
                cmd.AddParameter("UserId", userContext.UserId);
            });
    }

    public async Task<int> CountAsync(IReadOnlyList<string>? statuses = null)
        => await CountAsync(statuses, retentionStatuses: null);

    public async Task<int> CountAsync(
        IReadOnlyList<string>? statuses,
        IReadOnlyList<string>? retentionStatuses)
    {
        using var conn = connectionFactory.CreateConnection();
        var dbConn = (DbConnection)conn;
        await using var cmd = dbConn.CreateCommand();

        var sql = new StringBuilder("SELECT COUNT(*) FROM sessions WHERE user_id = @UserId");
        cmd.AddParameter("UserId", userContext.UserId);

        if (statuses is { Count: > 0 })
        {
            sql.Append(" AND status ");
            SqlInExpander.AppendInClause(sql, cmd, "Status", statuses);
        }
        if (retentionStatuses is { Count: > 0 })
        {
            sql.Append(" AND retention_status ");
            SqlInExpander.AppendInClause(sql, cmd, "RetentionStatus", retentionStatuses);
        }

        cmd.CommandText = sql.ToString();
        var result = await cmd.ExecuteScalarAsync();
        return (int)(long)(result ?? 0L);
    }

    public async Task<int> CountAsync(
        IReadOnlyList<string>? statuses,
        IReadOnlyList<string>? retentionStatuses,
        string viewMode)
    {
        using var conn = connectionFactory.CreateConnection();
        var dbConn = (DbConnection)conn;
        await using var cmd = dbConn.CreateCommand();

        var sql = new StringBuilder("SELECT COUNT(*) FROM sessions WHERE user_id = @UserId AND view_mode = @ViewMode");
        cmd.AddParameter("UserId", userContext.UserId);
        cmd.AddParameter("ViewMode", viewMode);

        if (statuses is { Count: > 0 })
        {
            sql.Append(" AND status ");
            SqlInExpander.AppendInClause(sql, cmd, "Status", statuses);
        }
        if (retentionStatuses is { Count: > 0 })
        {
            sql.Append(" AND retention_status ");
            SqlInExpander.AppendInClause(sql, cmd, "RetentionStatus", retentionStatuses);
        }

        cmd.CommandText = sql.ToString();
        var result = await cmd.ExecuteScalarAsync();
        return (int)(long)(result ?? 0L);
    }

    public async Task<(int Active, int Idle)> GetStatusCountsAsync()
    {
        using var conn = connectionFactory.CreateConnection();
        return await GetStatusCountsInternalAsync(conn, viewMode: null);
    }

    public async Task<(int Active, int Idle)> GetStatusCountsAsync(string viewMode)
    {
        using var conn = connectionFactory.CreateConnection();
        return await GetStatusCountsInternalAsync(conn, viewMode);
    }

    private async Task<(int Active, int Idle)> GetStatusCountsInternalAsync(IDbConnection conn, string? viewMode)
    {
        var whereClause = viewMode is not null
            ? "WHERE status = 'active' AND user_id = @UserId AND view_mode = @ViewMode"
            : "WHERE status = 'active' AND user_id = @UserId";

        var rows = await conn.QueryAsync(
            $"""
            SELECT activity_status, COUNT(*) as count
            FROM sessions
            {whereClause}
            GROUP BY activity_status
            """,
            cmd =>
            {
                cmd.AddParameter("UserId", userContext.UserId);
                if (viewMode is not null)
                    cmd.AddParameter("ViewMode", viewMode);
            },
            r => (
                ActivityStatus: r.IsDBNull(r.GetOrdinal("activity_status")) ? null : r.GetString(r.GetOrdinal("activity_status")),
                Count: (int)r.GetInt64(r.GetOrdinal("count"))
            ));

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
        var dbConn = (DbConnection)conn;
        await using var cmd = dbConn.CreateCommand();

        var sql = new StringBuilder("SELECT * FROM sessions WHERE status = 'active' AND user_id = @UserId");
        cmd.AddParameter("UserId", userContext.UserId);

        if (retentionStatuses is { Count: > 0 })
        {
            sql.Append(" AND retention_status ");
            SqlInExpander.AppendInClause(sql, cmd, "RetentionStatus", retentionStatuses);
        }

        sql.Append(" ORDER BY created_at DESC");

        cmd.CommandText = sql.ToString();
        await using var reader = await cmd.ExecuteReaderAsync();
        var list = new List<Session>();
        while (await reader.ReadAsync())
            list.Add(ReadSession(reader));
        return list;
    }

    public async Task<IReadOnlyList<Session>> ListActiveAsync(IReadOnlyList<string>? retentionStatuses, string viewMode)
    {
        using var conn = connectionFactory.CreateConnection();
        var dbConn = (DbConnection)conn;
        await using var cmd = dbConn.CreateCommand();

        var sql = new StringBuilder("SELECT * FROM sessions WHERE status = 'active' AND user_id = @UserId AND view_mode = @ViewMode");
        cmd.AddParameter("UserId", userContext.UserId);
        cmd.AddParameter("ViewMode", viewMode);

        if (retentionStatuses is { Count: > 0 })
        {
            sql.Append(" AND retention_status ");
            SqlInExpander.AppendInClause(sql, cmd, "RetentionStatus", retentionStatuses);
        }

        sql.Append(" ORDER BY created_at DESC");

        cmd.CommandText = sql.ToString();
        await using var reader = await cmd.ExecuteReaderAsync();
        var list = new List<Session>();
        while (await reader.ReadAsync())
            list.Add(ReadSession(reader));
        return list;
    }

    public async Task UpdateStatusAsync(string id, string status, string? stoppedAt = null)
    {
        using var conn = connectionFactory.CreateConnection();
        await UpdateStatusAsync(conn, null, id, status, stoppedAt);
    }

    public async Task UpdateStatusAsync(IDbConnection connection, IDbTransaction? transaction, string id, string status, string? stoppedAt)
    {
        var lifecycleStatus = status switch
        {
            "stopped" => "stopped",
            "completed" => "completed",
            _ => "running"
        };
        await connection.ExecuteNonQueryAsync(
            "UPDATE sessions SET status = @Status, stopped_at = @StoppedAt, lifecycle_status = @LifecycleStatus WHERE id = @Id AND user_id = @UserId",
            cmd =>
            {
                cmd.AddParameter("Id", id);
                cmd.AddParameter("Status", status);
                cmd.AddParameter("StoppedAt", stoppedAt);
                cmd.AddParameter("LifecycleStatus", lifecycleStatus);
                cmd.AddParameter("UserId", userContext.UserId);
            },
            transaction);
    }

    public async Task ArchiveAsync(string id, string archivedAt)
    {
        using var conn = connectionFactory.CreateConnection();
        await ArchiveAsync(conn, null, id, archivedAt);
    }

    public async Task ArchiveAsync(IDbConnection connection, IDbTransaction? transaction, string id, string archivedAt)
    {
        await connection.ExecuteNonQueryAsync(
            "UPDATE sessions SET retention_status = 'archived', archived_at = @ArchivedAt WHERE id = @Id AND user_id = @UserId",
            cmd =>
            {
                cmd.AddParameter("Id", id);
                cmd.AddParameter("ArchivedAt", archivedAt);
                cmd.AddParameter("UserId", userContext.UserId);
            },
            transaction);
    }

    public async Task UnarchiveAsync(string id)
    {
        using var conn = connectionFactory.CreateConnection();
        await UnarchiveAsync(conn, null, id);
    }

    public async Task UnarchiveAsync(IDbConnection connection, IDbTransaction? transaction, string id)
    {
        await connection.ExecuteNonQueryAsync(
            "UPDATE sessions SET retention_status = 'active', archived_at = NULL WHERE id = @Id AND user_id = @UserId",
            cmd =>
            {
                cmd.AddParameter("Id", id);
                cmd.AddParameter("UserId", userContext.UserId);
            },
            transaction);
    }

    public async Task<IReadOnlyList<Session>> GetForInstanceAsync(string instanceId)
    {
        using var conn = connectionFactory.CreateConnection();
        return await conn.QueryAsync(
            "SELECT * FROM sessions WHERE instance_id = @InstanceId AND user_id = @UserId ORDER BY created_at DESC",
            cmd =>
            {
                cmd.AddParameter("InstanceId", instanceId);
                cmd.AddParameter("UserId", userContext.UserId);
            },
            ReadSession);
    }

    public async Task<Session?> GetAnyForInstanceAsync(string instanceId)
    {
        // System-level lookup — no user filter (used by HarnessEventRelay and recovery)
        using var conn = connectionFactory.CreateConnection();
        return await conn.QueryFirstOrDefaultAsync(
            "SELECT * FROM sessions WHERE instance_id = @InstanceId LIMIT 1",
            cmd => { cmd.AddParameter("InstanceId", instanceId); },
            ReadSession);
    }

    public async Task<IReadOnlyList<Session>> GetNonTerminalForInstanceAsync(string instanceId)
    {
        // System-level lookup — no user filter (used by instance stop recovery)
        using var conn = connectionFactory.CreateConnection();
        return await conn.QueryAsync(
            "SELECT * FROM sessions WHERE instance_id = @InstanceId AND status NOT IN ('stopped', 'completed', 'error')",
            cmd => { cmd.AddParameter("InstanceId", instanceId); },
            ReadSession);
    }

    public async Task UpdateTitleAsync(string id, string title)
    {
        using var conn = connectionFactory.CreateConnection();
        await conn.ExecuteNonQueryAsync(
            "UPDATE sessions SET title = @Title WHERE id = @Id AND user_id = @UserId",
            cmd =>
            {
                cmd.AddParameter("Id", id);
                cmd.AddParameter("Title", title);
                cmd.AddParameter("UserId", userContext.UserId);
            });
    }

    public async Task UpdateForResumeAsync(string id, string instanceId)
    {
        using var conn = connectionFactory.CreateConnection();
        await conn.ExecuteNonQueryAsync(
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
            cmd =>
            {
                cmd.AddParameter("Id", id);
                cmd.AddParameter("InstanceId", instanceId);
                cmd.AddParameter("UserId", userContext.UserId);
            });
    }

    public async Task UpdateResumeTokenAsync(string id, string resumeToken)
    {
        using var conn = connectionFactory.CreateConnection();
        await conn.ExecuteNonQueryAsync(
            "UPDATE sessions SET harness_resume_token = @ResumeToken WHERE id = @Id AND user_id = @UserId",
            cmd =>
            {
                cmd.AddParameter("Id", id);
                cmd.AddParameter("ResumeToken", resumeToken);
                cmd.AddParameter("UserId", userContext.UserId);
            });
    }

    public async Task<IReadOnlyList<Session>> GetActiveChildrenAsync(string parentDbId)
    {
        // Child sessions are owned by the same user as the parent; filter by user for safety
        using var conn = connectionFactory.CreateConnection();
        return await conn.QueryAsync(
            "SELECT * FROM sessions WHERE parent_session_id = @ParentId AND status = 'active' AND user_id = @UserId",
            cmd =>
            {
                cmd.AddParameter("ParentId", parentDbId);
                cmd.AddParameter("UserId", userContext.UserId);
            },
            ReadSession);
    }

    public async Task<IReadOnlySet<string>> GetIdsWithActiveChildrenAsync()
    {
        using var conn = connectionFactory.CreateConnection();
        var ids = await conn.QueryAsync(
            """
            SELECT DISTINCT parent_session_id
            FROM sessions
            WHERE parent_session_id IS NOT NULL AND status = 'active' AND user_id = @UserId
            """,
            cmd => { cmd.AddParameter("UserId", userContext.UserId); },
            r => r.GetString(r.GetOrdinal("parent_session_id")));
        return ids.ToHashSet();
    }

    public async Task<IReadOnlyList<Session>> GetForWorkspaceAsync(string workspaceId)
        => await GetForWorkspaceAsync(workspaceId, retentionStatuses: null);

    public async Task<IReadOnlyList<Session>> GetForWorkspaceAsync(
        string workspaceId,
        IReadOnlyList<string>? retentionStatuses)
    {
        using var conn = connectionFactory.CreateConnection();
        var dbConn = (DbConnection)conn;
        await using var cmd = dbConn.CreateCommand();

        var sql = new StringBuilder("SELECT * FROM sessions WHERE workspace_id = @WorkspaceId AND user_id = @UserId");
        cmd.AddParameter("WorkspaceId", workspaceId);
        cmd.AddParameter("UserId", userContext.UserId);

        if (retentionStatuses is { Count: > 0 })
        {
            sql.Append(" AND retention_status ");
            SqlInExpander.AppendInClause(sql, cmd, "RetentionStatus", retentionStatuses);
        }

        sql.Append(" ORDER BY created_at DESC");

        cmd.CommandText = sql.ToString();
        await using var reader = await cmd.ExecuteReaderAsync();
        var list = new List<Session>();
        while (await reader.ReadAsync())
            list.Add(ReadSession(reader));
        return list;
    }

    public async Task<bool> DeleteAsync(string id)
    {
        using var conn = connectionFactory.CreateConnection();
        return await DeleteAsync(conn, null, id);
    }

    public async Task<bool> DeleteAsync(IDbConnection connection, IDbTransaction? transaction, string id)
    {
        var rows = await connection.ExecuteNonQueryAsync(
            "DELETE FROM sessions WHERE id = @Id AND user_id = @UserId",
            cmd =>
            {
                cmd.AddParameter("Id", id);
                cmd.AddParameter("UserId", userContext.UserId);
            },
            transaction);
        return rows > 0;
    }

    public async Task<(int TotalTokens, double TotalCost)?> IncrementTokensAsync(
        string id, int tokens, double cost)
    {
        using var conn = connectionFactory.CreateConnection();
        await conn.ExecuteNonQueryAsync(
            "UPDATE sessions SET total_tokens = total_tokens + @Tokens, total_cost = total_cost + @Cost WHERE id = @Id AND user_id = @UserId",
            cmd =>
            {
                cmd.AddParameter("Id", id);
                cmd.AddParameter("Tokens", tokens);
                cmd.AddParameter("Cost", cost);
                cmd.AddParameter("UserId", userContext.UserId);
            });

        var rows = await conn.QueryAsync(
            "SELECT total_tokens, total_cost FROM sessions WHERE id = @Id AND user_id = @UserId",
            cmd =>
            {
                cmd.AddParameter("Id", id);
                cmd.AddParameter("UserId", userContext.UserId);
            },
            r => (
                TotalTokens: (int)r.GetInt64(r.GetOrdinal("total_tokens")),
                TotalCost: r.GetDouble(r.GetOrdinal("total_cost"))
            ));

        return rows.Count > 0 ? rows[0] : null;
    }

    public async Task<(int TotalTokens, double TotalCost)> GetFleetTokenTotalsAsync()
    {
        using var conn = connectionFactory.CreateConnection();
        return await conn.QueryFirstAsync(
            "SELECT COALESCE(SUM(total_tokens), 0) as total_tokens, COALESCE(SUM(total_cost), 0.0) as total_cost FROM sessions WHERE user_id = @UserId",
            cmd => { cmd.AddParameter("UserId", userContext.UserId); },
            r => (
                TotalTokens: (int)r.GetInt64(r.GetOrdinal("total_tokens")),
                TotalCost: r.GetDouble(r.GetOrdinal("total_cost"))
            ));
    }

    public async Task<int> MarkAllNonTerminalStoppedAsync(string stoppedAt)
    {
        // System-level recovery operation — no user filter
        using var conn = connectionFactory.CreateConnection();
        return await conn.ExecuteNonQueryAsync(
            "UPDATE sessions SET status = 'stopped', stopped_at = @StoppedAt, lifecycle_status = 'stopped' WHERE status NOT IN ('stopped', 'completed', 'error')",
            cmd => { cmd.AddParameter("StoppedAt", stoppedAt); });
    }

    public async Task UpdateProjectAsync(string id, string? projectId)
    {
        using var conn = connectionFactory.CreateConnection();
        await conn.ExecuteNonQueryAsync(
            "UPDATE sessions SET project_id = @ProjectId WHERE id = @Id AND user_id = @UserId",
            cmd =>
            {
                cmd.AddParameter("Id", id);
                cmd.AddParameter("ProjectId", projectId);
                cmd.AddParameter("UserId", userContext.UserId);
            });
    }

    public async Task UpdateSelectedModelAsync(string id, string providerId, string modelId)
    {
        using var conn = connectionFactory.CreateConnection();
        await conn.ExecuteNonQueryAsync(
            "UPDATE sessions SET selected_provider_id = @ProviderId, selected_model_id = @ModelId WHERE id = @Id AND user_id = @UserId",
            cmd =>
            {
                cmd.AddParameter("Id", id);
                cmd.AddParameter("ProviderId", providerId);
                cmd.AddParameter("ModelId", modelId);
                cmd.AddParameter("UserId", userContext.UserId);
            });
    }

    private static Session ReadSession(DbDataReader r) => new()
    {
        Id = r.GetString(r.GetOrdinal("id")),
        WorkspaceId = r.GetString(r.GetOrdinal("workspace_id")),
        InstanceId = r.GetString(r.GetOrdinal("instance_id")),
        ProjectId = r.GetNullableString(r.GetOrdinal("project_id")),
        OpencodeSessionId = r.GetString(r.GetOrdinal("opencode_session_id")),
        Title = r.GetString(r.GetOrdinal("title")),
        Status = r.GetString(r.GetOrdinal("status")),
        Directory = r.GetString(r.GetOrdinal("directory")),
        CreatedAt = r.GetString(r.GetOrdinal("created_at")),
        StoppedAt = r.GetNullableString(r.GetOrdinal("stopped_at")),
        ParentSessionId = r.GetNullableString(r.GetOrdinal("parent_session_id")),
        ActivityStatus = r.GetNullableString(r.GetOrdinal("activity_status")),
        LifecycleStatus = r.GetNullableString(r.GetOrdinal("lifecycle_status")),
        RetentionStatus = r.GetString(r.GetOrdinal("retention_status")),
        ArchivedAt = r.GetNullableString(r.GetOrdinal("archived_at")),
        IsHidden = r.GetInt64(r.GetOrdinal("is_hidden")) != 0,
        TotalTokens = (int)r.GetInt64(r.GetOrdinal("total_tokens")),
        TotalCost = r.GetDouble(r.GetOrdinal("total_cost")),
        HarnessType = r.GetString(r.GetOrdinal("harness_type")),
        HarnessResumeToken = r.GetNullableString(r.GetOrdinal("harness_resume_token")),
        UserId = r.GetString(r.GetOrdinal("user_id")),
        SelectedProviderId = r.GetNullableString(r.GetOrdinal("selected_provider_id")),
        SelectedModelId = r.GetNullableString(r.GetOrdinal("selected_model_id")),
        ViewMode = r.GetString(r.GetOrdinal("view_mode")),
    };
}
