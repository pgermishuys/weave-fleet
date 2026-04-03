using Dapper;
using WeaveFleet.Application.Data;
using WeaveFleet.Domain.Entities;
using WeaveFleet.Domain.Repositories;

namespace WeaveFleet.Infrastructure.Data.Repositories;

public sealed class DapperInstanceRepository(IDbConnectionFactory connectionFactory) : IInstanceRepository
{
    public async Task InsertAsync(Instance instance)
    {
        using var conn = connectionFactory.CreateConnection();
        await conn.ExecuteAsync(
            """
            INSERT INTO instances (id, port, pid, directory, url, status, created_at, stopped_at)
            VALUES (@Id, @Port, @Pid, @Directory, @Url, @Status, @CreatedAt, @StoppedAt)
            """, instance);
    }

    public async Task<Instance?> GetByIdAsync(string id)
    {
        using var conn = connectionFactory.CreateConnection();
        return await conn.QueryFirstOrDefaultAsync<Instance>(
            "SELECT * FROM instances WHERE id = @Id", new { Id = id });
    }

    public async Task<Instance?> GetByDirectoryAsync(string directory)
    {
        using var conn = connectionFactory.CreateConnection();
        return await conn.QueryFirstOrDefaultAsync<Instance>(
            "SELECT * FROM instances WHERE directory = @Directory AND status = 'running' LIMIT 1",
            new { Directory = directory });
    }

    public async Task<IReadOnlyList<Instance>> ListAsync()
    {
        using var conn = connectionFactory.CreateConnection();
        var results = await conn.QueryAsync<Instance>(
            "SELECT * FROM instances ORDER BY created_at DESC");
        return results.AsList();
    }

    public async Task UpdateStatusAsync(string id, string status, string? stoppedAt = null)
    {
        using var conn = connectionFactory.CreateConnection();
        await conn.ExecuteAsync(
            "UPDATE instances SET status = @Status, stopped_at = @StoppedAt WHERE id = @Id",
            new { Id = id, Status = status, StoppedAt = stoppedAt });
    }

    public async Task<IReadOnlyList<Instance>> GetRunningAsync()
    {
        using var conn = connectionFactory.CreateConnection();
        var results = await conn.QueryAsync<Instance>(
            "SELECT * FROM instances WHERE status = 'running'");
        return results.AsList();
    }

    public async Task<int> MarkAllStoppedAsync(string stoppedAt)
    {
        using var conn = connectionFactory.CreateConnection();
        return await conn.ExecuteAsync(
            "UPDATE instances SET status = 'stopped', stopped_at = @StoppedAt WHERE status = 'running'",
            new { StoppedAt = stoppedAt });
    }
}
