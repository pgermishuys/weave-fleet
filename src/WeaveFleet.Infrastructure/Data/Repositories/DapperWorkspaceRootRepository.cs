using Dapper;
using WeaveFleet.Application.Data;
using WeaveFleet.Domain.Entities;
using WeaveFleet.Domain.Repositories;

namespace WeaveFleet.Infrastructure.Data.Repositories;

public sealed class DapperWorkspaceRootRepository(IDbConnectionFactory connectionFactory) : IWorkspaceRootRepository
{
    public async Task InsertAsync(WorkspaceRoot root)
    {
        using var conn = connectionFactory.CreateConnection();
        await conn.ExecuteAsync(
            "INSERT INTO workspace_roots (id, path, created_at) VALUES (@Id, @Path, @CreatedAt)",
            root);
    }

    public async Task<IReadOnlyList<WorkspaceRoot>> ListAsync()
    {
        using var conn = connectionFactory.CreateConnection();
        var results = await conn.QueryAsync<WorkspaceRoot>(
            "SELECT * FROM workspace_roots ORDER BY created_at ASC");
        return results.AsList();
    }

    public async Task<bool> DeleteAsync(string id)
    {
        using var conn = connectionFactory.CreateConnection();
        var rows = await conn.ExecuteAsync(
            "DELETE FROM workspace_roots WHERE id = @Id", new { Id = id });
        return rows > 0;
    }

    public async Task<WorkspaceRoot?> GetByPathAsync(string path)
    {
        using var conn = connectionFactory.CreateConnection();
        return await conn.QueryFirstOrDefaultAsync<WorkspaceRoot>(
            "SELECT * FROM workspace_roots WHERE path = @Path", new { Path = path });
    }
}
