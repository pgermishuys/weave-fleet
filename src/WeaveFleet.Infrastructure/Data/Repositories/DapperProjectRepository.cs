using System.Data.Common;
using WeaveFleet.Application.Data;
using WeaveFleet.Application.Services;
using WeaveFleet.Domain.Entities;
using WeaveFleet.Domain.Repositories;

namespace WeaveFleet.Infrastructure.Data.Repositories;

public sealed class DapperProjectRepository(
    IDbConnectionFactory connectionFactory,
    IUserContext userContext) : IProjectRepository
{
    public async Task<Project?> GetByIdAsync(string id)
    {
        using var conn = connectionFactory.CreateConnection();
        return await conn.QueryFirstOrDefaultAsync(
            "SELECT * FROM projects WHERE id = @Id AND user_id = @UserId",
            cmd =>
            {
                cmd.AddParameter("Id", id);
                cmd.AddParameter("UserId", userContext.UserId);
            },
            ReadProject);
    }

    public async Task<Project?> GetScratchProjectAsync()
    {
        using var conn = connectionFactory.CreateConnection();
        return await conn.QueryFirstOrDefaultAsync(
            "SELECT * FROM projects WHERE type = 'scratch' AND user_id = @UserId LIMIT 1",
            cmd => { cmd.AddParameter("UserId", userContext.UserId); },
            ReadProject);
    }

    public async Task<IReadOnlyList<Project>> ListAsync()
    {
        using var conn = connectionFactory.CreateConnection();
        return await conn.QueryAsync(
            "SELECT * FROM projects WHERE user_id = @UserId ORDER BY position ASC",
            cmd => { cmd.AddParameter("UserId", userContext.UserId); },
            ReadProject);
    }

    public async Task InsertAsync(Project project)
    {
        using var conn = connectionFactory.CreateConnection();
        await conn.ExecuteNonQueryAsync(
            """
            INSERT INTO projects (id, name, description, type, position, created_at, updated_at, user_id)
            VALUES (@Id, @Name, @Description, @Type, @Position, @CreatedAt, @UpdatedAt, @UserId)
            """,
            cmd =>
            {
                cmd.AddParameter("Id", project.Id);
                cmd.AddParameter("Name", project.Name);
                cmd.AddParameter("Description", project.Description);
                cmd.AddParameter("Type", project.Type);
                cmd.AddParameter("Position", project.Position);
                cmd.AddParameter("CreatedAt", project.CreatedAt);
                cmd.AddParameter("UpdatedAt", project.UpdatedAt);
                cmd.AddParameter("UserId", project.UserId);
            });
    }

    public async Task UpdateAsync(Project project)
    {
        using var conn = connectionFactory.CreateConnection();
        await conn.ExecuteNonQueryAsync(
            """
            UPDATE projects
            SET name = @Name, description = @Description, updated_at = @UpdatedAt
            WHERE id = @Id AND user_id = @UserId
            """,
            cmd =>
            {
                cmd.AddParameter("Name", project.Name);
                cmd.AddParameter("Description", project.Description);
                cmd.AddParameter("UpdatedAt", project.UpdatedAt);
                cmd.AddParameter("Id", project.Id);
                cmd.AddParameter("UserId", project.UserId);
            });
    }

    public async Task DeleteAsync(string id)
    {
        using var conn = connectionFactory.CreateConnection();
        await conn.ExecuteNonQueryAsync(
            "DELETE FROM projects WHERE id = @Id AND user_id = @UserId",
            cmd =>
            {
                cmd.AddParameter("Id", id);
                cmd.AddParameter("UserId", userContext.UserId);
            });
    }

    public async Task ReorderAsync(string id, int newPosition)
    {
        using var conn = connectionFactory.CreateConnection();
        await conn.ExecuteNonQueryAsync(
            "UPDATE projects SET position = @Position, updated_at = datetime('now') WHERE id = @Id AND user_id = @UserId",
            cmd =>
            {
                cmd.AddParameter("Id", id);
                cmd.AddParameter("Position", newPosition);
                cmd.AddParameter("UserId", userContext.UserId);
            });
    }

    public async Task<int> GetSessionCountAsync(string projectId)
    {
        using var conn = connectionFactory.CreateConnection();
        return (int)(conn.ExecuteScalarAsync<long>(
            "SELECT COUNT(*) FROM sessions WHERE project_id = @ProjectId AND user_id = @UserId",
            cmd =>
            {
                cmd.AddParameter("ProjectId", projectId);
                cmd.AddParameter("UserId", userContext.UserId);
            }).Result);
    }

    public async Task MoveSessionsToProjectAsync(string fromProjectId, string toProjectId)
    {
        using var conn = connectionFactory.CreateConnection();
        await conn.ExecuteNonQueryAsync(
            "UPDATE sessions SET project_id = @ToProjectId WHERE project_id = @FromProjectId AND user_id = @UserId",
            cmd =>
            {
                cmd.AddParameter("FromProjectId", fromProjectId);
                cmd.AddParameter("ToProjectId", toProjectId);
                cmd.AddParameter("UserId", userContext.UserId);
            });
    }

    private static Project ReadProject(DbDataReader r) => new()
    {
        Id = r.GetString(r.GetOrdinal("id")),
        Name = r.GetString(r.GetOrdinal("name")),
        Description = r.GetNullableString(r.GetOrdinal("description")),
        Type = r.GetString(r.GetOrdinal("type")),
        Position = (int)r.GetInt64(r.GetOrdinal("position")),
        CreatedAt = r.GetString(r.GetOrdinal("created_at")),
        UpdatedAt = r.GetString(r.GetOrdinal("updated_at")),
        UserId = r.GetString(r.GetOrdinal("user_id")),
    };
}
