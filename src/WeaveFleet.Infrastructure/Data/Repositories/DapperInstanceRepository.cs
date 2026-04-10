using Dapper;
using WeaveFleet.Application.Data;
using WeaveFleet.Application.Services;
using WeaveFleet.Domain.Entities;
using WeaveFleet.Domain.Repositories;

namespace WeaveFleet.Infrastructure.Data.Repositories;

public sealed class DapperInstanceRepository(
    IDbConnectionFactory connectionFactory,
    IUserContext userContext) : IInstanceRepository
{
    public async Task InsertAsync(Instance instance)
    {
        var insertUserId = string.IsNullOrWhiteSpace(instance.UserId)
            ? userContext.UserId
            : instance.UserId;

        using var conn = connectionFactory.CreateConnection();
        await conn.ExecuteAsync(
            """
            INSERT INTO instances (id, port, pid, directory, url, status, created_at, stopped_at, user_id)
            VALUES (@Id, @Port, @Pid, @Directory, @Url, @Status, @CreatedAt, @StoppedAt, @UserId)
            """,
            new
            {
                instance.Id,
                instance.Port,
                instance.Pid,
                instance.Directory,
                instance.Url,
                instance.Status,
                instance.CreatedAt,
                instance.StoppedAt,
                UserId = insertUserId
            });
    }

    public async Task<Instance?> GetByIdAsync(string id)
    {
        using var conn = connectionFactory.CreateConnection();
        return await conn.QueryFirstOrDefaultAsync<Instance>(
            "SELECT * FROM instances WHERE id = @Id AND user_id = @UserId",
            new { Id = id, UserId = userContext.UserId });
    }

    public async Task<Instance?> GetByDirectoryAsync(string directory)
    {
        using var conn = connectionFactory.CreateConnection();
        return await conn.QueryFirstOrDefaultAsync<Instance>(
            "SELECT * FROM instances WHERE directory = @Directory AND status = 'running' AND user_id = @UserId LIMIT 1",
            new { Directory = directory, UserId = userContext.UserId });
    }

    public async Task<IReadOnlyList<Instance>> ListAsync()
    {
        using var conn = connectionFactory.CreateConnection();
        var results = await conn.QueryAsync<Instance>(
            "SELECT * FROM instances WHERE user_id = @UserId ORDER BY created_at DESC",
            new { UserId = userContext.UserId });
        return results.AsList();
    }

    public async Task UpdateStatusAsync(string id, string status, string? stoppedAt = null)
    {
        using var conn = connectionFactory.CreateConnection();
        await conn.ExecuteAsync(
            "UPDATE instances SET status = @Status, stopped_at = @StoppedAt WHERE id = @Id AND user_id = @UserId",
            new { Id = id, Status = status, StoppedAt = stoppedAt, UserId = userContext.UserId });
    }

    public async Task<IReadOnlyList<Instance>> GetRunningAsync()
    {
        // System-level lookup — used by recovery; no user filter
        using var conn = connectionFactory.CreateConnection();
        var results = await conn.QueryAsync<Instance>(
            "SELECT * FROM instances WHERE status = 'running'");
        return results.AsList();
    }

    public async Task<int> MarkAllStoppedAsync(string stoppedAt)
    {
        // System-level recovery operation — no user filter
        using var conn = connectionFactory.CreateConnection();
        return await conn.ExecuteAsync(
            "UPDATE instances SET status = 'stopped', stopped_at = @StoppedAt WHERE status = 'running'",
            new { StoppedAt = stoppedAt });
    }
}
