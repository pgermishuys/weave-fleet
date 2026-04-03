using Dapper;
using WeaveFleet.Application.Data;
using WeaveFleet.Domain.Entities;
using WeaveFleet.Domain.Repositories;

namespace WeaveFleet.Infrastructure.Data.Repositories;

public sealed class DapperProjectRepository(IDbConnectionFactory connectionFactory) : IProjectRepository
{
    public async Task<Project?> GetByIdAsync(string id)
    {
        using var conn = connectionFactory.CreateConnection();
        return await conn.QueryFirstOrDefaultAsync<Project>(
            "SELECT * FROM projects WHERE id = @Id", new { Id = id });
    }

    public async Task<Project?> GetScratchProjectAsync()
    {
        using var conn = connectionFactory.CreateConnection();
        return await conn.QueryFirstOrDefaultAsync<Project>(
            "SELECT * FROM projects WHERE type = 'scratch' LIMIT 1");
    }

    public async Task<IReadOnlyList<Project>> ListAsync()
    {
        using var conn = connectionFactory.CreateConnection();
        var results = await conn.QueryAsync<Project>(
            "SELECT * FROM projects ORDER BY position ASC");
        return results.AsList();
    }

    public async Task InsertAsync(Project project)
    {
        using var conn = connectionFactory.CreateConnection();
        await conn.ExecuteAsync(
            """
            INSERT INTO projects (id, name, description, type, position, created_at, updated_at)
            VALUES (@Id, @Name, @Description, @Type, @Position, @CreatedAt, @UpdatedAt)
            """, project);
    }

    public async Task UpdateAsync(Project project)
    {
        using var conn = connectionFactory.CreateConnection();
        await conn.ExecuteAsync(
            """
            UPDATE projects
            SET name = @Name, description = @Description, updated_at = @UpdatedAt
            WHERE id = @Id
            """, project);
    }

    public async Task DeleteAsync(string id)
    {
        using var conn = connectionFactory.CreateConnection();
        await conn.ExecuteAsync("DELETE FROM projects WHERE id = @Id", new { Id = id });
    }

    public async Task ReorderAsync(string id, int newPosition)
    {
        using var conn = connectionFactory.CreateConnection();
        await conn.ExecuteAsync(
            "UPDATE projects SET position = @Position, updated_at = datetime('now') WHERE id = @Id",
            new { Id = id, Position = newPosition });
    }

    public async Task<int> GetSessionCountAsync(string projectId)
    {
        using var conn = connectionFactory.CreateConnection();
        return await conn.ExecuteScalarAsync<int>(
            "SELECT COUNT(*) FROM sessions WHERE project_id = @ProjectId",
            new { ProjectId = projectId });
    }

    public async Task MoveSessionsToProjectAsync(string fromProjectId, string toProjectId)
    {
        using var conn = connectionFactory.CreateConnection();
        await conn.ExecuteAsync(
            "UPDATE sessions SET project_id = @ToProjectId WHERE project_id = @FromProjectId",
            new { FromProjectId = fromProjectId, ToProjectId = toProjectId });
    }
}
