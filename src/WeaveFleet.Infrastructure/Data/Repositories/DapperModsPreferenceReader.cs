using WeaveFleet.Application.Data;
using WeaveFleet.Application.Mods;
using WeaveFleet.Application.Runtimes;

namespace WeaveFleet.Infrastructure.Data.Repositories;

/// <summary>
/// <see cref="IModsPreferenceReader"/> over <c>user_preferences</c>: the Mods switch and own-Bun rows of every user, in one
/// query. Not scoped to the current user, so it is for Fleet's own background work, never for a request.
/// </summary>
internal sealed class DapperModsPreferenceReader(IDbConnectionFactory connectionFactory) : IModsPreferenceReader
{
    /// <inheritdoc />
    public async Task<IReadOnlyList<ModsUserPreference>> ListAsync(CancellationToken ct)
    {
        using var conn = connectionFactory.CreateConnection();
        return await conn.QueryAsync(
            "SELECT user_id, key, value FROM user_preferences WHERE key IN (@Mods, @BunPath)",
            cmd =>
            {
                cmd.AddParameter("@Mods", ModsFeature.PreferenceKey);
                cmd.AddParameter("@BunPath", BunPathPreference.Key);
            },
            reader => new ModsUserPreference(reader.GetString(0), reader.GetString(1), reader.GetString(2)),
            ct).ConfigureAwait(false);
    }
}
