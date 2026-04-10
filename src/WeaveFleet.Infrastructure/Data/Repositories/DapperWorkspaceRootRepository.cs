using Dapper;
using WeaveFleet.Application.Data;
using WeaveFleet.Application.Services;
using WeaveFleet.Domain.Entities;
using WeaveFleet.Domain.Repositories;

namespace WeaveFleet.Infrastructure.Data.Repositories;

public sealed class DapperWorkspaceRootRepository(
    IDbConnectionFactory connectionFactory,
    IUserContext userContext) : IWorkspaceRootRepository
{
    public async Task InsertAsync(WorkspaceRoot root)
    {
        using var conn = connectionFactory.CreateConnection();
        await conn.ExecuteAsync(
            "INSERT INTO workspace_roots (id, path, created_at, user_id) VALUES (@Id, @Path, @CreatedAt, @UserId)",
            root);
    }

    public async Task<IReadOnlyList<WorkspaceRoot>> ListAsync()
    {
        using var conn = connectionFactory.CreateConnection();
        var results = await conn.QueryAsync<WorkspaceRoot>(
            "SELECT * FROM workspace_roots WHERE user_id = @UserId ORDER BY created_at ASC",
            new { UserId = userContext.UserId });
        return results.AsList();
    }

    public async Task<bool> DeleteAsync(string id)
    {
        using var conn = connectionFactory.CreateConnection();
        var rows = await conn.ExecuteAsync(
            "DELETE FROM workspace_roots WHERE id = @Id AND user_id = @UserId",
            new { Id = id, UserId = userContext.UserId });
        return rows > 0;
    }

    public async Task<WorkspaceRoot?> GetByPathAsync(string path)
    {
        using var conn = connectionFactory.CreateConnection();
        return await conn.QueryFirstOrDefaultAsync<WorkspaceRoot>(
            "SELECT * FROM workspace_roots WHERE path = @Path AND user_id = @UserId",
            new { Path = path, UserId = userContext.UserId });
    }
}
