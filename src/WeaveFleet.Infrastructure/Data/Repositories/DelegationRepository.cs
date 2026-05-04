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
            INSERT INTO delegations (id, parent_session_id, child_session_id, parent_tool_call_id, title, status, created_at, updated_at, completed_at)
            SELECT @Id, @ParentSessionId, @ChildSessionId, @ParentToolCallId, @Title, @Status, @CreatedAt, @UpdatedAt, @CompletedAt
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
                completed_at = @CompletedAt
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

    private static Delegation ReadDelegation(DbDataReader r) => new()
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
    };
}
