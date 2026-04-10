using Dapper;
using WeaveFleet.Application.Data;
using WeaveFleet.Application.Services;
using WeaveFleet.Domain.Entities;
using WeaveFleet.Domain.Repositories;

namespace WeaveFleet.Infrastructure.Data.Repositories;

public sealed class DapperDelegationRepository : IDelegationRepository
{
    private readonly IDbConnectionFactory _connectionFactory;

    private readonly IUserContext _userContext;

    public DapperDelegationRepository(IDbConnectionFactory connectionFactory, IUserContext userContext)
    {
        _connectionFactory = connectionFactory;
        _userContext = userContext;
    }

    public async Task InsertAsync(Delegation delegation)
    {
        using var conn = _connectionFactory.CreateConnection();
        await conn.ExecuteAsync(
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
            new
            {
                delegation.Id,
                delegation.ParentSessionId,
                delegation.ChildSessionId,
                delegation.ParentToolCallId,
                delegation.Title,
                delegation.Status,
                delegation.CreatedAt,
                delegation.UpdatedAt,
                delegation.CompletedAt,
                UserId = _userContext.UserId
            });
    }

    public async Task<Delegation?> GetByIdAsync(string id)
    {
        using var conn = _connectionFactory.CreateConnection();
        return await conn.QueryFirstOrDefaultAsync<Delegation>(
            """
            SELECT d.*
            FROM delegations d
            INNER JOIN sessions parent_session ON parent_session.id = d.parent_session_id
            WHERE d.id = @Id AND parent_session.user_id = @UserId
            """,
            new { Id = id, UserId = _userContext.UserId });
    }

    public async Task<IReadOnlyList<Delegation>> GetByParentSessionIdAsync(string parentSessionId)
    {
        using var conn = _connectionFactory.CreateConnection();
        var results = await conn.QueryAsync<Delegation>(
            """
            SELECT d.*
            FROM delegations d
            INNER JOIN sessions parent_session ON parent_session.id = d.parent_session_id
            WHERE d.parent_session_id = @ParentSessionId AND parent_session.user_id = @UserId
            ORDER BY d.created_at ASC
            """,
            new { ParentSessionId = parentSessionId, UserId = _userContext.UserId });
        return results.AsList();
    }

    public async Task<Delegation?> GetByChildSessionIdAsync(string childSessionId)
    {
        using var conn = _connectionFactory.CreateConnection();
        return await conn.QueryFirstOrDefaultAsync<Delegation>(
            """
            SELECT d.*
            FROM delegations d
            INNER JOIN sessions parent_session ON parent_session.id = d.parent_session_id
            WHERE d.child_session_id = @ChildSessionId AND parent_session.user_id = @UserId
            LIMIT 1
            """,
            new { ChildSessionId = childSessionId, UserId = _userContext.UserId });
    }

    public async Task<Delegation?> GetByParentToolCallIdAsync(string parentSessionId, string toolCallId)
    {
        using var conn = _connectionFactory.CreateConnection();
        return await conn.QueryFirstOrDefaultAsync<Delegation>(
            """
            SELECT d.*
            FROM delegations d
            INNER JOIN sessions parent_session ON parent_session.id = d.parent_session_id
            WHERE d.parent_session_id = @ParentSessionId
              AND d.parent_tool_call_id = @ToolCallId
              AND parent_session.user_id = @UserId
            LIMIT 1
            """,
            new { ParentSessionId = parentSessionId, ToolCallId = toolCallId, UserId = _userContext.UserId });
    }

    public async Task UpdateStatusAsync(string id, string status, string updatedAt, string? completedAt)
    {
        using var conn = _connectionFactory.CreateConnection();
        await conn.ExecuteAsync(
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
            new { Id = id, Status = status, UpdatedAt = updatedAt, CompletedAt = completedAt, UserId = _userContext.UserId });
    }

    public async Task UpdateChildSessionIdAsync(string id, string childSessionId, string updatedAt)
    {
        using var conn = _connectionFactory.CreateConnection();
        await conn.ExecuteAsync(
            """
            UPDATE delegations
            SET child_session_id = @ChildSessionId,
                updated_at = @UpdatedAt
            WHERE id = @Id
              AND EXISTS (
                  SELECT 1
                  FROM sessions parent_session
                  WHERE parent_session.id = delegations.parent_session_id AND parent_session.user_id = @UserId)
              AND EXISTS (
                  SELECT 1
                  FROM sessions child_session
                  WHERE child_session.id = @ChildSessionId AND child_session.user_id = @UserId)
            """,
            new { Id = id, ChildSessionId = childSessionId, UpdatedAt = updatedAt, UserId = _userContext.UserId });
    }
}
