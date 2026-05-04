using System.Data.Common;
using WeaveFleet.Application.Data;
using WeaveFleet.Application.Services;
using WeaveFleet.Domain.Entities;
using WeaveFleet.Domain.Repositories;

namespace WeaveFleet.Infrastructure.Data.Repositories;

public sealed class WorkspaceRootRepository(
    IDbConnectionFactory connectionFactory,
    IUserContext userContext) : IWorkspaceRootRepository
{
    public async Task InsertAsync(WorkspaceRoot root)
    {
        using var conn = connectionFactory.CreateConnection();
        await conn.ExecuteNonQueryAsync(
            "INSERT INTO workspace_roots (id, path, created_at, user_id) VALUES (@Id, @Path, @CreatedAt, @UserId)",
            cmd =>
            {
                cmd.AddParameter("Id", root.Id);
                cmd.AddParameter("Path", root.Path);
                cmd.AddParameter("CreatedAt", root.CreatedAt);
                cmd.AddParameter("UserId", root.UserId);
            });
    }

    public async Task<IReadOnlyList<WorkspaceRoot>> ListAsync()
    {
        using var conn = connectionFactory.CreateConnection();
        return await conn.QueryAsync(
            "SELECT * FROM workspace_roots WHERE user_id = @UserId ORDER BY created_at ASC",
            cmd => { cmd.AddParameter("UserId", userContext.UserId); },
            ReadWorkspaceRoot);
    }

    public async Task<bool> DeleteAsync(string id)
    {
        using var conn = connectionFactory.CreateConnection();
        var rows = await conn.ExecuteNonQueryAsync(
            "DELETE FROM workspace_roots WHERE id = @Id AND user_id = @UserId",
            cmd =>
            {
                cmd.AddParameter("Id", id);
                cmd.AddParameter("UserId", userContext.UserId);
            });
        return rows > 0;
    }

    public async Task<WorkspaceRoot?> GetByPathAsync(string path)
    {
        using var conn = connectionFactory.CreateConnection();
        return await conn.QueryFirstOrDefaultAsync(
            "SELECT * FROM workspace_roots WHERE path = @Path AND user_id = @UserId",
            cmd =>
            {
                cmd.AddParameter("Path", path);
                cmd.AddParameter("UserId", userContext.UserId);
            },
            ReadWorkspaceRoot);
    }

    private static WorkspaceRoot ReadWorkspaceRoot(DbDataReader r) => new()
    {
        Id = r.GetString(r.GetOrdinal("id")),
        Path = r.GetString(r.GetOrdinal("path")),
        CreatedAt = r.GetString(r.GetOrdinal("created_at")),
        UserId = r.GetString(r.GetOrdinal("user_id")),
    };
}
