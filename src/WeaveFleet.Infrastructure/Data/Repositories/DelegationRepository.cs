using System.Data;
using System.Data.Common;
using WeaveFleet.Application.Data;
using WeaveFleet.Application.Services;
using WeaveFleet.Domain.Entities;
using WeaveFleet.Domain.Repositories;

namespace WeaveFleet.Infrastructure.Data.Repositories;

public sealed class DelegationRepository : IDelegationRepository
{
    private readonly IDbConnectionFactory _connectionFactory;
    private readonly IUserContext _userContext;

    public DelegationRepository(IDbConnectionFactory connectionFactory, IUserContext userContext)
    {
        _connectionFactory = connectionFactory;
        _userContext = userContext;
    }

    public async Task InsertAsync(Delegation delegation)
    {
        using var conn = _connectionFactory.CreateConnection();
        await InsertAsync(conn, null, delegation);
    }

    public async Task InsertAsync(IDbConnection connection, IDbTransaction? transaction, Delegation delegation)
    {
        await connection.ExecuteNonQueryAsync(
            """
            INSERT INTO delegations (
                id, parent_session_id, child_session_id, parent_tool_call_id, title, status, created_at, updated_at, completed_at,
                kind, work_id, label, background, can_stop, can_read_output, ended_reason, detail)
            SELECT @Id, @ParentSessionId, @ChildSessionId, @ParentToolCallId, @Title, @Status, @CreatedAt, @UpdatedAt, @CompletedAt,
                @Kind, @WorkId, @Label, @Background, @CanStop, @CanReadOutput, @EndedReason, @Detail
            FROM sessions parent_session
            WHERE parent_session.id = @ParentSessionId
              AND parent_session.user_id = @UserId
              AND (
                  @ChildSessionId IS NULL
                  OR EXISTS (
                      SELECT 1 FROM sessions child_session
                      WHERE child_session.id = @ChildSessionId AND child_session.user_id = @UserId))
            """,
            cmd =>
            {
                cmd.AddParameter("Id", delegation.Id);
                cmd.AddParameter("ParentSessionId", delegation.ParentSessionId);
                cmd.AddParameter("ChildSessionId", delegation.ChildSessionId);
                cmd.AddParameter("ParentToolCallId", delegation.ParentToolCallId);
                cmd.AddParameter("Title", delegation.Title);
                cmd.AddParameter("Status", delegation.Status);
                cmd.AddParameter("CreatedAt", delegation.CreatedAt);
                cmd.AddParameter("UpdatedAt", delegation.UpdatedAt);
                cmd.AddParameter("CompletedAt", delegation.CompletedAt);
                cmd.AddParameter("Kind", delegation.Kind);
                // A subagent is known by the call that started it, unless the harness gave it a handle of its own.
                cmd.AddParameter("WorkId", string.IsNullOrEmpty(delegation.WorkId) ? delegation.ParentToolCallId ?? delegation.Id : delegation.WorkId);
                cmd.AddParameter("Label", delegation.Label);
                cmd.AddParameter("Background", delegation.Background ? 1 : 0);
                cmd.AddParameter("CanStop", delegation.CanStop ? 1 : 0);
                cmd.AddParameter("CanReadOutput", delegation.CanReadOutput ? 1 : 0);
                cmd.AddParameter("EndedReason", delegation.EndedReason);
                cmd.AddParameter("Detail", delegation.Detail);
                cmd.AddParameter("UserId", _userContext.UserId);
            },
            transaction);
    }

    public async Task<Delegation?> GetByIdAsync(string id)
    {
        using var conn = _connectionFactory.CreateConnection();
        return await conn.QueryFirstOrDefaultAsync(
            """
            SELECT d.*
            FROM delegations d
            INNER JOIN sessions parent_session ON parent_session.id = d.parent_session_id
            WHERE d.id = @Id AND parent_session.user_id = @UserId
            """,
            cmd =>
            {
                cmd.AddParameter("Id", id);
                cmd.AddParameter("UserId", _userContext.UserId);
            },
            ReadDelegation);
    }

    public async Task<IReadOnlyList<Delegation>> GetByParentSessionIdAsync(string parentSessionId)
    {
        using var conn = _connectionFactory.CreateConnection();
        return await conn.QueryAsync(
            """
            SELECT d.*
            FROM delegations d
            INNER JOIN sessions parent_session ON parent_session.id = d.parent_session_id
            WHERE d.parent_session_id = @ParentSessionId AND parent_session.user_id = @UserId
            ORDER BY d.created_at ASC
            """,
            cmd =>
            {
                cmd.AddParameter("ParentSessionId", parentSessionId);
                cmd.AddParameter("UserId", _userContext.UserId);
            },
            ReadDelegation);
    }

    public async Task<Delegation?> GetByChildSessionIdAsync(string childSessionId)
    {
        using var conn = _connectionFactory.CreateConnection();
        return await conn.QueryFirstOrDefaultAsync(
            """
            SELECT d.*
            FROM delegations d
            INNER JOIN sessions parent_session ON parent_session.id = d.parent_session_id
            WHERE d.child_session_id = @ChildSessionId AND parent_session.user_id = @UserId
            LIMIT 1
            """,
            cmd =>
            {
                cmd.AddParameter("ChildSessionId", childSessionId);
                cmd.AddParameter("UserId", _userContext.UserId);
            },
            ReadDelegation);
    }

    public async Task<Delegation?> GetByParentToolCallIdAsync(string parentSessionId, string toolCallId)
    {
        using var conn = _connectionFactory.CreateConnection();
        return await conn.QueryFirstOrDefaultAsync(
            """
            SELECT d.*
            FROM delegations d
            INNER JOIN sessions parent_session ON parent_session.id = d.parent_session_id
            WHERE d.parent_session_id = @ParentSessionId
              AND d.parent_tool_call_id = @ToolCallId
              AND parent_session.user_id = @UserId
            LIMIT 1
            """,
            cmd =>
            {
                cmd.AddParameter("ParentSessionId", parentSessionId);
                cmd.AddParameter("ToolCallId", toolCallId);
                cmd.AddParameter("UserId", _userContext.UserId);
            },
            ReadDelegation);
    }

    public async Task<Delegation?> GetByWorkIdAsync(string parentSessionId, string workId)
    {
        using var conn = _connectionFactory.CreateConnection();
        return await conn.QueryFirstOrDefaultAsync(
            """
            SELECT d.*
            FROM delegations d
            INNER JOIN sessions parent_session ON parent_session.id = d.parent_session_id
            WHERE d.parent_session_id = @ParentSessionId
              AND d.work_id = @WorkId
              AND parent_session.user_id = @UserId
            ORDER BY d.created_at DESC
            LIMIT 1
            """,
            cmd =>
            {
                cmd.AddParameter("ParentSessionId", parentSessionId);
                cmd.AddParameter("WorkId", workId);
                cmd.AddParameter("UserId", _userContext.UserId);
            },
            ReadDelegation);
    }

    public async Task<IReadOnlyList<Delegation>> ListRunningAsync()
    {
        using var conn = _connectionFactory.CreateConnection();
        return await conn.QueryAsync(
            """
            SELECT d.*
            FROM delegations d
            INNER JOIN sessions parent_session ON parent_session.id = d.parent_session_id
            WHERE d.status IN ('pending', 'running') AND parent_session.user_id = @UserId
            ORDER BY d.created_at ASC, d.id ASC
            """,
            cmd => cmd.AddParameter("UserId", _userContext.UserId),
            ReadDelegation);
    }

    public async Task<IReadOnlyDictionary<string, int>> CountRunningAsync(IReadOnlyCollection<string> parentSessionIds)
    {
        var counts = new Dictionary<string, int>(StringComparer.Ordinal);
        if (parentSessionIds.Count == 0)
            return counts;

        using var conn = _connectionFactory.CreateConnection();
        var rows = await conn.QueryAsync(
            """
            SELECT d.parent_session_id, COUNT(*) AS running
            FROM delegations d
            INNER JOIN sessions parent_session ON parent_session.id = d.parent_session_id
            WHERE d.status IN ('pending', 'running') AND parent_session.user_id = @UserId
            GROUP BY d.parent_session_id
            """,
            cmd => cmd.AddParameter("UserId", _userContext.UserId),
            r => (SessionId: r.GetString(0), Running: (int)r.GetInt64(1)));

        // One query for the user's running work: there's little of it, and a list of ids would need one parameter each.
        var wanted = parentSessionIds as IReadOnlySet<string> ?? parentSessionIds.ToHashSet(StringComparer.Ordinal);
        foreach (var (sessionId, running) in rows)
        {
            if (wanted.Contains(sessionId))
                counts[sessionId] = running;
        }

        return counts;
    }

    public async Task UpdateWorkAsync(Delegation delegation)
    {
        using var conn = _connectionFactory.CreateConnection();
        await conn.ExecuteNonQueryAsync(
            """
            UPDATE delegations
            SET kind = @Kind,
                title = @Title,
                label = @Label,
                parent_tool_call_id = @ParentToolCallId,
                background = @Background,
                can_stop = @CanStop,
                can_read_output = @CanReadOutput,
                detail = @Detail,
                updated_at = @UpdatedAt
            WHERE id = @Id
              AND EXISTS (
                  SELECT 1
                  FROM sessions parent_session
                  WHERE parent_session.id = delegations.parent_session_id AND parent_session.user_id = @UserId)
            """,
            cmd =>
            {
                cmd.AddParameter("Id", delegation.Id);
                cmd.AddParameter("Kind", delegation.Kind);
                cmd.AddParameter("Title", delegation.Title);
                cmd.AddParameter("Label", delegation.Label);
                cmd.AddParameter("ParentToolCallId", delegation.ParentToolCallId);
                cmd.AddParameter("Background", delegation.Background ? 1 : 0);
                cmd.AddParameter("CanStop", delegation.CanStop ? 1 : 0);
                cmd.AddParameter("CanReadOutput", delegation.CanReadOutput ? 1 : 0);
                cmd.AddParameter("Detail", delegation.Detail);
                cmd.AddParameter("UpdatedAt", delegation.UpdatedAt);
                cmd.AddParameter("UserId", _userContext.UserId);
            });
    }

    public async Task EndAsync(string id, string status, string endedReason, string? detail, string endedAt)
    {
        using var conn = _connectionFactory.CreateConnection();
        await conn.ExecuteNonQueryAsync(
            """
            UPDATE delegations
            SET status = @Status,
                ended_reason = @EndedReason,
                detail = COALESCE(@Detail, detail),
                updated_at = @EndedAt,
                completed_at = @EndedAt
            WHERE id = @Id
              AND EXISTS (
                  SELECT 1
                  FROM sessions parent_session
                  WHERE parent_session.id = delegations.parent_session_id AND parent_session.user_id = @UserId)
            """,
            cmd =>
            {
                cmd.AddParameter("Id", id);
                cmd.AddParameter("Status", status);
                cmd.AddParameter("EndedReason", endedReason);
                cmd.AddParameter("Detail", detail);
                cmd.AddParameter("EndedAt", endedAt);
                cmd.AddParameter("UserId", _userContext.UserId);
            });
    }

    public async Task<int> CancelAllUnfinishedAsync(string completedAt)
    {
        // System-level recovery operation — no user filter
        using var conn = _connectionFactory.CreateConnection();
        return await conn.ExecuteNonQueryAsync(
            """
            UPDATE delegations
            SET status = 'cancelled',
                ended_reason = 'lost',
                updated_at = @CompletedAt,
                completed_at = @CompletedAt
            WHERE status IN ('pending', 'running')
            """,
            cmd => { cmd.AddParameter("CompletedAt", completedAt); });
    }

    public async Task<IReadOnlyList<Delegation>> GetUnreportedLostAsync(string parentSessionId)
    {
        using var conn = _connectionFactory.CreateConnection();
        return await conn.QueryAsync(
            """
            SELECT d.*
            FROM delegations d
            INNER JOIN sessions parent_session ON parent_session.id = d.parent_session_id
            WHERE d.parent_session_id = @ParentSessionId
              AND d.ended_reason = 'lost' AND d.lost_reported_at IS NULL
              AND parent_session.user_id = @UserId
            ORDER BY d.created_at ASC, d.id ASC
            """,
            cmd =>
            {
                cmd.AddParameter("ParentSessionId", parentSessionId);
                cmd.AddParameter("UserId", _userContext.UserId);
            },
            ReadDelegation);
    }

    public async Task MarkLostReportedAsync(IReadOnlyCollection<string> ids, string reportedAt)
    {
        if (ids.Count == 0)
            return;

        using var conn = _connectionFactory.CreateConnection();
        foreach (var id in ids)
        {
            await conn.ExecuteNonQueryAsync(
                """
                UPDATE delegations
                SET lost_reported_at = @ReportedAt
                WHERE id = @Id
                  AND EXISTS (
                      SELECT 1
                      FROM sessions parent_session
                      WHERE parent_session.id = delegations.parent_session_id AND parent_session.user_id = @UserId)
                """,
                cmd =>
                {
                    cmd.AddParameter("Id", id);
                    cmd.AddParameter("ReportedAt", reportedAt);
                    cmd.AddParameter("UserId", _userContext.UserId);
                });
        }
    }

    public async Task UpdateStatusAsync(string id, string status, string updatedAt, string? completedAt)
    {
        using var conn = _connectionFactory.CreateConnection();
        await UpdateStatusAsync(conn, null, id, status, updatedAt, completedAt);
    }

    public async Task UpdateStatusAsync(IDbConnection connection, IDbTransaction? transaction, string id, string status, string updatedAt, string? completedAt)
    {
        await connection.ExecuteNonQueryAsync(
            """
            UPDATE delegations
            SET status = @Status,
                updated_at = @UpdatedAt,
                completed_at = @CompletedAt,
                ended_reason = CASE WHEN @Status IN ('completed', 'error', 'cancelled') THEN COALESCE(ended_reason, @Status) END
            WHERE id = @Id
              AND EXISTS (
                  SELECT 1
                  FROM sessions parent_session
                  WHERE parent_session.id = delegations.parent_session_id AND parent_session.user_id = @UserId)
            """,
            cmd =>
            {
                cmd.AddParameter("Id", id);
                cmd.AddParameter("Status", status);
                cmd.AddParameter("UpdatedAt", updatedAt);
                cmd.AddParameter("CompletedAt", completedAt);
                cmd.AddParameter("UserId", _userContext.UserId);
            },
            transaction);
    }

    public async Task UpdateChildSessionIdAsync(string id, string? childSessionId, string updatedAt)
    {
        using var conn = _connectionFactory.CreateConnection();
        await UpdateChildSessionIdAsync(conn, null, id, childSessionId, updatedAt);
    }

    public async Task UpdateChildSessionIdAsync(IDbConnection connection, IDbTransaction? transaction, string id, string? childSessionId, string updatedAt)
    {
        await connection.ExecuteNonQueryAsync(
            """
            UPDATE delegations
            SET child_session_id = @ChildSessionId,
                updated_at = @UpdatedAt
            WHERE id = @Id
              AND EXISTS (
                  SELECT 1
                  FROM sessions parent_session
                  WHERE parent_session.id = delegations.parent_session_id AND parent_session.user_id = @UserId)
              AND (
                  @ChildSessionId IS NULL
                  OR EXISTS (
                      SELECT 1
                      FROM sessions child_session
                      WHERE child_session.id = @ChildSessionId AND child_session.user_id = @UserId))
            """,
            cmd =>
            {
                cmd.AddParameter("Id", id);
                cmd.AddParameter("ChildSessionId", childSessionId);
                cmd.AddParameter("UpdatedAt", updatedAt);
                cmd.AddParameter("UserId", _userContext.UserId);
            },
            transaction);
    }

    public async Task DeleteByParentSessionIdAsync(string parentSessionId)
    {
        using var conn = _connectionFactory.CreateConnection();
        await DeleteByParentSessionIdAsync(conn, null, parentSessionId);
    }

    public async Task DeleteByParentSessionIdAsync(IDbConnection connection, IDbTransaction? transaction, string parentSessionId)
    {
        await connection.ExecuteNonQueryAsync(
            """
            DELETE FROM delegations
            WHERE parent_session_id = @ParentSessionId
              AND EXISTS (
                  SELECT 1
                  FROM sessions parent_session
                  WHERE parent_session.id = delegations.parent_session_id AND parent_session.user_id = @UserId)
            """,
            cmd =>
            {
                cmd.AddParameter("ParentSessionId", parentSessionId);
                cmd.AddParameter("UserId", _userContext.UserId);
            },
            transaction);
    }

    internal static Delegation ReadDelegation(DbDataReader r) => new()
    {
        Id = r.GetString(r.GetOrdinal("id")),
        ParentSessionId = r.GetString(r.GetOrdinal("parent_session_id")),
        ChildSessionId = r.GetNullableString(r.GetOrdinal("child_session_id")),
        ParentToolCallId = r.GetNullableString(r.GetOrdinal("parent_tool_call_id")),
        Title = r.GetString(r.GetOrdinal("title")),
        Status = r.GetString(r.GetOrdinal("status")),
        CreatedAt = r.GetString(r.GetOrdinal("created_at")),
        UpdatedAt = r.GetString(r.GetOrdinal("updated_at")),
        CompletedAt = r.GetNullableString(r.GetOrdinal("completed_at")),
        Kind = r.GetString(r.GetOrdinal("kind")),
        WorkId = r.GetNullableString(r.GetOrdinal("work_id")) ?? string.Empty,
        Label = r.GetNullableString(r.GetOrdinal("label")),
        Background = r.GetInt64(r.GetOrdinal("background")) != 0,
        CanStop = r.GetInt64(r.GetOrdinal("can_stop")) != 0,
        CanReadOutput = r.GetInt64(r.GetOrdinal("can_read_output")) != 0,
        EndedReason = r.GetNullableString(r.GetOrdinal("ended_reason")),
        Detail = r.GetNullableString(r.GetOrdinal("detail")),
    };
}
