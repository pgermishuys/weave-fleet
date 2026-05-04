using System.Data.Common;
using WeaveFleet.Application.Data;
using WeaveFleet.Application.Services;
using WeaveFleet.Domain.Entities;
using WeaveFleet.Domain.Repositories;

namespace WeaveFleet.Infrastructure.Data.Repositories;

public sealed class InstanceRepository(
    IDbConnectionFactory connectionFactory,
    IUserContext userContext) : IInstanceRepository
{
    public async Task InsertAsync(Instance instance)
    {
        var insertUserId = string.IsNullOrWhiteSpace(instance.UserId)
            ? userContext.UserId
            : instance.UserId;

        using var conn = connectionFactory.CreateConnection();
        await conn.ExecuteNonQueryAsync(
            """
            INSERT INTO instances (id, port, pid, directory, url, status, created_at, stopped_at, user_id)
            VALUES (@Id, @Port, @Pid, @Directory, @Url, @Status, @CreatedAt, @StoppedAt, @UserId)
            """,
            cmd =>
            {
                cmd.AddParameter("Id", instance.Id);
                cmd.AddParameter("Port", instance.Port);
                cmd.AddParameter("Pid", instance.Pid);
                cmd.AddParameter("Directory", instance.Directory);
                cmd.AddParameter("Url", instance.Url);
                cmd.AddParameter("Status", instance.Status);
                cmd.AddParameter("CreatedAt", instance.CreatedAt);
                cmd.AddParameter("StoppedAt", instance.StoppedAt);
                cmd.AddParameter("UserId", insertUserId);
            });
    }

    public async Task<Instance?> GetByIdAsync(string id)
    {
        using var conn = connectionFactory.CreateConnection();
        return await conn.QueryFirstOrDefaultAsync(
            "SELECT * FROM instances WHERE id = @Id AND user_id = @UserId",
            cmd =>
            {
                cmd.AddParameter("Id", id);
                cmd.AddParameter("UserId", userContext.UserId);
            },
            ReadInstance);
    }

    public async Task<Instance?> GetByDirectoryAsync(string directory)
    {
        using var conn = connectionFactory.CreateConnection();
        return await conn.QueryFirstOrDefaultAsync(
            "SELECT * FROM instances WHERE directory = @Directory AND status = 'running' AND user_id = @UserId LIMIT 1",
            cmd =>
            {
                cmd.AddParameter("Directory", directory);
                cmd.AddParameter("UserId", userContext.UserId);
            },
            ReadInstance);
    }

    public async Task<IReadOnlyList<Instance>> ListAsync()
    {
        using var conn = connectionFactory.CreateConnection();
        return await conn.QueryAsync(
            "SELECT * FROM instances WHERE user_id = @UserId ORDER BY created_at DESC",
            cmd => { cmd.AddParameter("UserId", userContext.UserId); },
            ReadInstance);
    }

    public async Task UpdateStatusAsync(string id, string status, string? stoppedAt = null)
    {
        using var conn = connectionFactory.CreateConnection();
        await conn.ExecuteNonQueryAsync(
            "UPDATE instances SET status = @Status, stopped_at = @StoppedAt WHERE id = @Id AND user_id = @UserId",
            cmd =>
            {
                cmd.AddParameter("Id", id);
                cmd.AddParameter("Status", status);
                cmd.AddParameter("StoppedAt", stoppedAt);
                cmd.AddParameter("UserId", userContext.UserId);
            });
    }

    public async Task<IReadOnlyList<Instance>> GetRunningAsync()
    {
        // System-level lookup — used by recovery; no user filter
        using var conn = connectionFactory.CreateConnection();
        return await conn.QueryAsync(
            "SELECT * FROM instances WHERE status = 'running'",
            ReadInstance);
    }

    public async Task<int> MarkAllStoppedAsync(string stoppedAt)
    {
        // System-level recovery operation — no user filter
        using var conn = connectionFactory.CreateConnection();
        return await conn.ExecuteNonQueryAsync(
            "UPDATE instances SET status = 'stopped', stopped_at = @StoppedAt WHERE status = 'running'",
            cmd => { cmd.AddParameter("StoppedAt", stoppedAt); });
    }

    private static Instance ReadInstance(DbDataReader r) => new()
    {
        Id = r.GetString(r.GetOrdinal("id")),
        Port = (int)r.GetInt64(r.GetOrdinal("port")),
        Pid = r.GetNullableInt32(r.GetOrdinal("pid")),
        Directory = r.GetString(r.GetOrdinal("directory")),
        Url = r.GetString(r.GetOrdinal("url")),
        Status = r.GetString(r.GetOrdinal("status")),
        CreatedAt = r.GetString(r.GetOrdinal("created_at")),
        StoppedAt = r.GetNullableString(r.GetOrdinal("stopped_at")),
        UserId = r.GetString(r.GetOrdinal("user_id")),
    };
}
