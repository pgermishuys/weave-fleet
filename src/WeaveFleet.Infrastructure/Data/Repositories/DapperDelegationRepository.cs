using Dapper;
using WeaveFleet.Application.Data;
using WeaveFleet.Domain.Entities;
using WeaveFleet.Domain.Repositories;

namespace WeaveFleet.Infrastructure.Data.Repositories;

public sealed class DapperDelegationRepository(IDbConnectionFactory connectionFactory) : IDelegationRepository
{
    public async Task InsertAsync(Delegation delegation)
    {
        using var conn = connectionFactory.CreateConnection();
        await conn.ExecuteAsync(
            """
            INSERT INTO delegations (id, parent_session_id, child_session_id, parent_tool_call_id, title, status, created_at, updated_at, completed_at)
            VALUES (@Id, @ParentSessionId, @ChildSessionId, @ParentToolCallId, @Title, @Status, @CreatedAt, @UpdatedAt, @CompletedAt)
            """,
            delegation);
    }

    public async Task<Delegation?> GetByIdAsync(string id)
    {
        using var conn = connectionFactory.CreateConnection();
        return await conn.QueryFirstOrDefaultAsync<Delegation>(
            "SELECT * FROM delegations WHERE id = @Id",
            new { Id = id });
    }

    public async Task<IReadOnlyList<Delegation>> GetByParentSessionIdAsync(string parentSessionId)
    {
        using var conn = connectionFactory.CreateConnection();
        var results = await conn.QueryAsync<Delegation>(
            "SELECT * FROM delegations WHERE parent_session_id = @ParentSessionId ORDER BY created_at ASC",
            new { ParentSessionId = parentSessionId });
        return results.AsList();
    }

    public async Task<Delegation?> GetByChildSessionIdAsync(string childSessionId)
    {
        using var conn = connectionFactory.CreateConnection();
        return await conn.QueryFirstOrDefaultAsync<Delegation>(
            "SELECT * FROM delegations WHERE child_session_id = @ChildSessionId LIMIT 1",
            new { ChildSessionId = childSessionId });
    }

    public async Task<Delegation?> GetByParentToolCallIdAsync(string parentSessionId, string toolCallId)
    {
        using var conn = connectionFactory.CreateConnection();
        return await conn.QueryFirstOrDefaultAsync<Delegation>(
            "SELECT * FROM delegations WHERE parent_session_id = @ParentSessionId AND parent_tool_call_id = @ToolCallId LIMIT 1",
            new { ParentSessionId = parentSessionId, ToolCallId = toolCallId });
    }

    public async Task UpdateStatusAsync(string id, string status, string updatedAt, string? completedAt)
    {
        using var conn = connectionFactory.CreateConnection();
        await conn.ExecuteAsync(
            "UPDATE delegations SET status = @Status, updated_at = @UpdatedAt, completed_at = @CompletedAt WHERE id = @Id",
            new { Id = id, Status = status, UpdatedAt = updatedAt, CompletedAt = completedAt });
    }

    public async Task UpdateChildSessionIdAsync(string id, string childSessionId, string updatedAt)
    {
        using var conn = connectionFactory.CreateConnection();
        await conn.ExecuteAsync(
            "UPDATE delegations SET child_session_id = @ChildSessionId, updated_at = @UpdatedAt WHERE id = @Id",
            new { Id = id, ChildSessionId = childSessionId, UpdatedAt = updatedAt });
    }
}
