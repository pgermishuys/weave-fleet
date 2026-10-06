using System.Data.Common;
using System.Globalization;
using WeaveFleet.Application.Data;
using WeaveFleet.Domain.Entities;
using WeaveFleet.Domain.Repositories;

namespace WeaveFleet.Infrastructure.Data.Repositories;

/// <summary>The <c>devices</c> table (migration 051).</summary>
public sealed class DeviceRepository(IDbConnectionFactory connectionFactory) : IDeviceRepository
{
    private const string Columns = "id, name, platform, token_hash, paired_via, created_at, last_used_at, revoked_at";

    public async Task<Device?> GetAsync(string id)
    {
        using var conn = connectionFactory.CreateConnection();
        return await conn.QueryFirstOrDefaultAsync(
            $"SELECT {Columns} FROM devices WHERE id = @Id",
            cmd => cmd.AddParameter("Id", id),
            Read);
    }

    public async Task<IReadOnlyList<Device>> ListActiveAsync()
    {
        using var conn = connectionFactory.CreateConnection();
        return await conn.QueryAsync(
            $"SELECT {Columns} FROM devices WHERE revoked_at IS NULL ORDER BY created_at, id",
            _ => { },
            Read);
    }

    public async Task InsertAsync(Device device)
    {
        using var conn = connectionFactory.CreateConnection();
        await conn.ExecuteNonQueryAsync(
            """
            INSERT INTO devices (id, name, platform, token_hash, paired_via, created_at, last_used_at, revoked_at)
            VALUES (@Id, @Name, @Platform, @TokenHash, @PairedVia, @CreatedAt, @LastUsedAt, @RevokedAt)
            """,
            cmd =>
            {
                cmd.AddParameter("Id", device.Id);
                cmd.AddParameter("Name", device.Name);
                cmd.AddParameter("Platform", device.Platform);
                cmd.AddParameter("TokenHash", device.TokenHash);
                cmd.AddParameter("PairedVia", device.PairedVia);
                cmd.AddParameter("CreatedAt", Format(device.CreatedAt));
                cmd.AddParameter("LastUsedAt", Format(device.LastUsedAt));
                cmd.AddParameter("RevokedAt", device.RevokedAt is { } revoked ? Format(revoked) : null);
            });
    }

    public async Task TouchAsync(string id, DateTimeOffset lastUsedAt)
    {
        using var conn = connectionFactory.CreateConnection();
        await conn.ExecuteNonQueryAsync(
            "UPDATE devices SET last_used_at = @LastUsedAt WHERE id = @Id AND revoked_at IS NULL",
            cmd =>
            {
                cmd.AddParameter("Id", id);
                cmd.AddParameter("LastUsedAt", Format(lastUsedAt));
            });
    }

    public async Task<bool> ReplaceTokenHashAsync(string id, byte[] tokenHash, DateTimeOffset at)
    {
        using var conn = connectionFactory.CreateConnection();
        var changed = await conn.ExecuteNonQueryAsync(
            "UPDATE devices SET token_hash = @TokenHash, last_used_at = @At WHERE id = @Id AND revoked_at IS NULL",
            cmd =>
            {
                cmd.AddParameter("Id", id);
                cmd.AddParameter("TokenHash", tokenHash);
                cmd.AddParameter("At", Format(at));
            });
        return changed > 0;
    }

    public async Task<bool> RevokeAsync(string id, DateTimeOffset at)
    {
        using var conn = connectionFactory.CreateConnection();
        var changed = await conn.ExecuteNonQueryAsync(
            "UPDATE devices SET revoked_at = @At WHERE id = @Id AND revoked_at IS NULL",
            cmd =>
            {
                cmd.AddParameter("Id", id);
                cmd.AddParameter("At", Format(at));
            });
        return changed > 0;
    }

    private static string Format(DateTimeOffset value) => value.ToUniversalTime().ToString("O", CultureInfo.InvariantCulture);

    private static DateTimeOffset Parse(string value) =>
        DateTimeOffset.Parse(value, CultureInfo.InvariantCulture, DateTimeStyles.AssumeUniversal | DateTimeStyles.AdjustToUniversal);

    private static Device Read(DbDataReader r) => new()
    {
        Id = r.GetString(r.GetOrdinal("id")),
        Name = r.GetString(r.GetOrdinal("name")),
        Platform = r.GetNullableString(r.GetOrdinal("platform")),
        TokenHash = (byte[])r.GetValue(r.GetOrdinal("token_hash")),
        PairedVia = r.GetNullableString(r.GetOrdinal("paired_via")),
        CreatedAt = Parse(r.GetString(r.GetOrdinal("created_at"))),
        LastUsedAt = Parse(r.GetString(r.GetOrdinal("last_used_at"))),
        RevokedAt = r.GetNullableString(r.GetOrdinal("revoked_at")) is { } revoked ? Parse(revoked) : null,
    };
}
