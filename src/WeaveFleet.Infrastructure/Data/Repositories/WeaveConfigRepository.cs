using WeaveFleet.Application.Data;
using WeaveFleet.Application.Services;
using WeaveFleet.Domain.Entities;
using WeaveFleet.Domain.Repositories;

namespace WeaveFleet.Infrastructure.Data.Repositories;

public sealed class WeaveConfigRepository(
    IDbConnectionFactory connectionFactory,
    IUserContext userContext) : IWeaveConfigRepository
{
    public async Task<WeaveConfig> GetAsync()
    {
        using var conn = connectionFactory.CreateConnection();
        var row = await conn.QueryFirstOrDefaultAsync(
            "SELECT source, updated_at FROM weave_configs WHERE user_id = @UserId",
            cmd => cmd.AddParameter("UserId", userContext.UserId),
            r => new Tuple<string, string>(r.GetString(0), r.GetString(1)));
        if (row is null)
            return new WeaveConfig();

        var files = await conn.QueryAsync(
            "SELECT path, content FROM weave_config_files WHERE user_id = @UserId ORDER BY path",
            cmd => cmd.AddParameter("UserId", userContext.UserId),
            r => (Path: r.GetString(0), Content: r.GetString(1)));

        return new WeaveConfig
        {
            Source = row.Item1 == "fleet" ? WeaveConfigSource.Fleet : WeaveConfigSource.Own,
            Files = files.ToDictionary(file => file.Path, file => file.Content, StringComparer.Ordinal),
            UpdatedAt = row.Item2,
        };
    }

    public async Task SaveAsync(WeaveConfig config)
    {
        using var conn = connectionFactory.CreateConnection();
        using var tx = conn.BeginTransaction();
        await conn.ExecuteNonQueryAsync(
            """
            INSERT INTO weave_configs (user_id, source, updated_at) VALUES (@UserId, @Source, @UpdatedAt)
            ON CONFLICT(user_id) DO UPDATE SET source = excluded.source, updated_at = excluded.updated_at
            """,
            cmd =>
            {
                cmd.AddParameter("UserId", userContext.UserId);
                cmd.AddParameter("Source", config.Source == WeaveConfigSource.Fleet ? "fleet" : "own");
                cmd.AddParameter("UpdatedAt", config.UpdatedAt ?? DateTimeOffset.UtcNow.ToString("O"));
            },
            tx);
        await conn.ExecuteNonQueryAsync(
            "DELETE FROM weave_config_files WHERE user_id = @UserId",
            cmd => cmd.AddParameter("UserId", userContext.UserId),
            tx);
        foreach (var (path, content) in config.Files)
        {
            await conn.ExecuteNonQueryAsync(
                "INSERT INTO weave_config_files (user_id, path, content) VALUES (@UserId, @Path, @Content)",
                cmd =>
                {
                    cmd.AddParameter("UserId", userContext.UserId);
                    cmd.AddParameter("Path", path);
                    cmd.AddParameter("Content", content);
                },
                tx);
        }
        tx.Commit();
    }
}
