using System.Data.Common;
using System.Globalization;
using WeaveFleet.Application.Data;
using WeaveFleet.Domain.Entities;
using WeaveFleet.Domain.Repositories;

namespace WeaveFleet.Infrastructure.Data.Repositories;

/// <summary>The <c>machines</c> and <c>device_grants</c> tables (migration 053).</summary>
public sealed class RemoteMachineRepository(IDbConnectionFactory connectionFactory) : IRemoteMachineRepository
{
    private const string MachineColumns = "id, name, base_url, encrypted_token, os, added_at, last_seen_at, status, agents_allowed";
    private const string GrantColumns = "device_id, machine_id, remote_device_id, created_at, revoked_at";

    public async Task<IReadOnlyList<RemoteMachine>> ListAsync()
    {
        using var conn = connectionFactory.CreateConnection();
        return await conn.QueryAsync($"SELECT {MachineColumns} FROM machines ORDER BY added_at, id", _ => { }, ReadMachine);
    }

    public async Task<RemoteMachine?> GetAsync(string id)
    {
        using var conn = connectionFactory.CreateConnection();
        return await conn.QueryFirstOrDefaultAsync($"SELECT {MachineColumns} FROM machines WHERE id = @Id", cmd => cmd.AddParameter("Id", id), ReadMachine);
    }

    public async Task UpsertAsync(RemoteMachine machine)
    {
        using var conn = connectionFactory.CreateConnection();
        await conn.ExecuteNonQueryAsync(
            """
            INSERT INTO machines (id, name, base_url, encrypted_token, os, added_at, last_seen_at, status, agents_allowed)
            VALUES (@Id, @Name, @BaseUrl, @Token, @Os, @AddedAt, @LastSeenAt, @Status, @AgentsAllowed)
            ON CONFLICT (id) DO UPDATE SET
              name = excluded.name,
              base_url = excluded.base_url,
              encrypted_token = excluded.encrypted_token,
              os = excluded.os,
              last_seen_at = COALESCE(excluded.last_seen_at, machines.last_seen_at),
              status = excluded.status,
              agents_allowed = excluded.agents_allowed
            """,
            cmd =>
            {
                cmd.AddParameter("Id", machine.Id);
                cmd.AddParameter("Name", machine.Name);
                cmd.AddParameter("BaseUrl", machine.BaseUrl);
                cmd.AddParameter("Token", machine.EncryptedToken);
                cmd.AddParameter("Os", machine.Os);
                cmd.AddParameter("AddedAt", Format(machine.AddedAt));
                cmd.AddParameter("LastSeenAt", machine.LastSeenAt is { } seen ? Format(seen) : null);
                cmd.AddParameter("Status", machine.Status);
                cmd.AddParameter("AgentsAllowed", machine.AgentsAllowed ? 1 : 0);
            });
    }

    public async Task<bool> DeleteAsync(string id)
    {
        using var conn = connectionFactory.CreateConnection();
        await conn.ExecuteNonQueryAsync("DELETE FROM device_grants WHERE machine_id = @Id", cmd => cmd.AddParameter("Id", id));
        return await conn.ExecuteNonQueryAsync("DELETE FROM machines WHERE id = @Id", cmd => cmd.AddParameter("Id", id)) > 0;
    }

    public async Task UpdateStatusAsync(string id, string status, DateTimeOffset? lastSeenAt)
    {
        using var conn = connectionFactory.CreateConnection();
        await conn.ExecuteNonQueryAsync(
            "UPDATE machines SET status = @Status, last_seen_at = COALESCE(@LastSeenAt, last_seen_at) WHERE id = @Id",
            cmd =>
            {
                cmd.AddParameter("Id", id);
                cmd.AddParameter("Status", status);
                cmd.AddParameter("LastSeenAt", lastSeenAt is { } seen ? Format(seen) : null);
            });
    }

    public async Task<DeviceGrant?> GetGrantAsync(string deviceId, string machineId)
    {
        using var conn = connectionFactory.CreateConnection();
        return await conn.QueryFirstOrDefaultAsync(
            $"SELECT {GrantColumns} FROM device_grants WHERE device_id = @DeviceId AND machine_id = @MachineId",
            cmd =>
            {
                cmd.AddParameter("DeviceId", deviceId);
                cmd.AddParameter("MachineId", machineId);
            },
            ReadGrant);
    }

    public async Task SaveGrantAsync(DeviceGrant grant)
    {
        using var conn = connectionFactory.CreateConnection();
        await conn.ExecuteNonQueryAsync(
            """
            INSERT INTO device_grants (device_id, machine_id, remote_device_id, created_at, revoked_at)
            VALUES (@DeviceId, @MachineId, @RemoteDeviceId, @CreatedAt, NULL)
            ON CONFLICT (device_id, machine_id) DO UPDATE SET
              remote_device_id = excluded.remote_device_id,
              created_at = excluded.created_at,
              revoked_at = NULL
            """,
            cmd =>
            {
                cmd.AddParameter("DeviceId", grant.DeviceId);
                cmd.AddParameter("MachineId", grant.MachineId);
                cmd.AddParameter("RemoteDeviceId", grant.RemoteDeviceId);
                cmd.AddParameter("CreatedAt", Format(grant.CreatedAt));
            });
    }

    public async Task<IReadOnlyList<DeviceGrant>> ListGrantsForDeviceAsync(string deviceId)
    {
        using var conn = connectionFactory.CreateConnection();
        return await conn.QueryAsync(
            $"SELECT {GrantColumns} FROM device_grants WHERE device_id = @DeviceId AND revoked_at IS NULL",
            cmd => cmd.AddParameter("DeviceId", deviceId),
            ReadGrant);
    }

    public async Task<IReadOnlyList<DeviceGrant>> ListGrantsForMachineAsync(string machineId)
    {
        using var conn = connectionFactory.CreateConnection();
        return await conn.QueryAsync(
            $"SELECT {GrantColumns} FROM device_grants WHERE machine_id = @MachineId",
            cmd => cmd.AddParameter("MachineId", machineId),
            ReadGrant);
    }

    public async Task<IReadOnlyList<DeviceGrant>> ListRevokedGrantsAsync()
    {
        using var conn = connectionFactory.CreateConnection();
        return await conn.QueryAsync($"SELECT {GrantColumns} FROM device_grants WHERE revoked_at IS NOT NULL", _ => { }, ReadGrant);
    }

    public async Task MarkGrantRevokedAsync(string deviceId, string machineId, DateTimeOffset at)
    {
        using var conn = connectionFactory.CreateConnection();
        await conn.ExecuteNonQueryAsync(
            "UPDATE device_grants SET revoked_at = @At WHERE device_id = @DeviceId AND machine_id = @MachineId",
            cmd =>
            {
                cmd.AddParameter("DeviceId", deviceId);
                cmd.AddParameter("MachineId", machineId);
                cmd.AddParameter("At", Format(at));
            });
    }

    public async Task DeleteGrantAsync(string deviceId, string machineId)
    {
        using var conn = connectionFactory.CreateConnection();
        await conn.ExecuteNonQueryAsync(
            "DELETE FROM device_grants WHERE device_id = @DeviceId AND machine_id = @MachineId",
            cmd =>
            {
                cmd.AddParameter("DeviceId", deviceId);
                cmd.AddParameter("MachineId", machineId);
            });
    }

    private static string Format(DateTimeOffset value) => value.ToUniversalTime().ToString("O", CultureInfo.InvariantCulture);

    private static DateTimeOffset Parse(string value) =>
        DateTimeOffset.Parse(value, CultureInfo.InvariantCulture, DateTimeStyles.AssumeUniversal | DateTimeStyles.AdjustToUniversal);

    private static RemoteMachine ReadMachine(DbDataReader r) => new()
    {
        Id = r.GetString(r.GetOrdinal("id")),
        Name = r.GetString(r.GetOrdinal("name")),
        BaseUrl = r.GetString(r.GetOrdinal("base_url")),
        EncryptedToken = r.GetString(r.GetOrdinal("encrypted_token")),
        Os = r.GetNullableString(r.GetOrdinal("os")),
        AddedAt = Parse(r.GetString(r.GetOrdinal("added_at"))),
        LastSeenAt = r.GetNullableString(r.GetOrdinal("last_seen_at")) is { } seen ? Parse(seen) : null,
        Status = r.GetString(r.GetOrdinal("status")),
        AgentsAllowed = r.GetInt64(r.GetOrdinal("agents_allowed")) != 0,
    };

    private static DeviceGrant ReadGrant(DbDataReader r) => new()
    {
        DeviceId = r.GetString(r.GetOrdinal("device_id")),
        MachineId = r.GetString(r.GetOrdinal("machine_id")),
        RemoteDeviceId = r.GetString(r.GetOrdinal("remote_device_id")),
        CreatedAt = Parse(r.GetString(r.GetOrdinal("created_at"))),
        RevokedAt = r.GetNullableString(r.GetOrdinal("revoked_at")) is { } at ? Parse(at) : null,
    };
}
