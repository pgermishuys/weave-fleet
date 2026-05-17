using WeaveFleet.Application.Data;
using WeaveFleet.Application.Services;
using WeaveFleet.Domain.Repositories;

namespace WeaveFleet.Infrastructure.Data.Repositories;

/// <summary>
/// ADO.NET-backed implementation of <see cref="IUserPreferenceRepository"/>.
/// Uses <see cref="DbCommandHelper"/> for NativeAOT compatibility (no Dapper).
/// All queries are scoped to the current user via <see cref="IUserContext"/>.
/// </summary>
public sealed class DapperUserPreferenceRepository(
    IDbConnectionFactory connectionFactory,
    IUserContext userContext) : IUserPreferenceRepository
{
    public async Task<string?> GetAsync(string key)
    {
        using var conn = connectionFactory.CreateConnection();
        return await conn.ExecuteScalarAsync<string>(
            "SELECT value FROM user_preferences WHERE user_id = @UserId AND key = @Key",
            cmd =>
            {
                cmd.AddParameter("@UserId", userContext.UserId);
                cmd.AddParameter("@Key", key);
            }).ConfigureAwait(false);
    }

    public async Task<IReadOnlyDictionary<string, string>> GetAllAsync()
    {
        using var conn = connectionFactory.CreateConnection();
        var rows = await conn.QueryAsync(
            "SELECT key, value FROM user_preferences WHERE user_id = @UserId",
            cmd => cmd.AddParameter("@UserId", userContext.UserId),
            reader => (Key: reader.GetString(0), Value: reader.GetString(1))).ConfigureAwait(false);

        var dict = new Dictionary<string, string>(StringComparer.Ordinal);
        foreach (var (key, value) in rows)
        {
            dict[key] = value;
        }

        return dict;
    }

    public async Task SetAsync(string key, string value)
    {
        using var conn = connectionFactory.CreateConnection();
        await conn.ExecuteNonQueryAsync(
            """
            INSERT INTO user_preferences (user_id, key, value, updated_at)
            VALUES (@UserId, @Key, @Value, datetime('now'))
            ON CONFLICT(user_id, key) DO UPDATE SET
                value = excluded.value,
                updated_at = excluded.updated_at
            """,
            cmd =>
            {
                cmd.AddParameter("@UserId", userContext.UserId);
                cmd.AddParameter("@Key", key);
                cmd.AddParameter("@Value", value);
            }).ConfigureAwait(false);
    }
}
