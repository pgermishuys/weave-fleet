using System.Data.Common;
using WeaveFleet.Application.Data;
using WeaveFleet.Application.Services;
using WeaveFleet.Domain.Entities;
using WeaveFleet.Domain.Repositories;

namespace WeaveFleet.Infrastructure.Data.Repositories;

public sealed class HarnessProfileRepository(
    IDbConnectionFactory connectionFactory,
    IUserContext userContext) : IHarnessProfileRepository
{
    public async Task<IReadOnlyList<HarnessProfile>> ListAsync(string harnessType)
    {
        using var conn = connectionFactory.CreateConnection();
        return await conn.QueryAsync(
            "SELECT * FROM harness_profiles WHERE user_id = @UserId AND harness_type = @HarnessType ORDER BY created_at ASC",
            cmd =>
            {
                cmd.AddParameter("UserId", userContext.UserId);
                cmd.AddParameter("HarnessType", harnessType);
            },
            ReadProfile);
    }

    public async Task<HarnessProfile?> GetByIdAsync(string id)
    {
        using var conn = connectionFactory.CreateConnection();
        return await conn.QueryFirstOrDefaultAsync(
            "SELECT * FROM harness_profiles WHERE id = @Id AND user_id = @UserId",
            cmd =>
            {
                cmd.AddParameter("Id", id);
                cmd.AddParameter("UserId", userContext.UserId);
            },
            ReadProfile);
    }

    public async Task<HarnessProfile?> GetDefaultAsync(string harnessType)
    {
        using var conn = connectionFactory.CreateConnection();
        return await conn.QueryFirstOrDefaultAsync(
            "SELECT * FROM harness_profiles WHERE user_id = @UserId AND harness_type = @HarnessType AND is_default = 1 LIMIT 1",
            cmd =>
            {
                cmd.AddParameter("UserId", userContext.UserId);
                cmd.AddParameter("HarnessType", harnessType);
            },
            ReadProfile);
    }

    public async Task InsertAsync(HarnessProfile profile)
    {
        using var conn = connectionFactory.CreateConnection();
        await conn.ExecuteNonQueryAsync(
            """
            INSERT INTO harness_profiles (id, user_id, harness_type, name, content, is_default, created_at, updated_at)
            VALUES (@Id, @UserId, @HarnessType, @Name, @Content, 0, @CreatedAt, @UpdatedAt)
            """,
            cmd =>
            {
                cmd.AddParameter("Id", profile.Id);
                cmd.AddParameter("UserId", userContext.UserId);
                cmd.AddParameter("HarnessType", profile.HarnessType);
                cmd.AddParameter("Name", profile.Name);
                cmd.AddParameter("Content", profile.Content);
                cmd.AddParameter("CreatedAt", profile.CreatedAt);
                cmd.AddParameter("UpdatedAt", profile.UpdatedAt);
            });
    }

    public async Task<bool> UpdateAsync(HarnessProfile profile)
    {
        using var conn = connectionFactory.CreateConnection();
        var rows = await conn.ExecuteNonQueryAsync(
            "UPDATE harness_profiles SET name = @Name, content = @Content, updated_at = @UpdatedAt WHERE id = @Id AND user_id = @UserId",
            cmd =>
            {
                cmd.AddParameter("Id", profile.Id);
                cmd.AddParameter("UserId", userContext.UserId);
                cmd.AddParameter("Name", profile.Name);
                cmd.AddParameter("Content", profile.Content);
                cmd.AddParameter("UpdatedAt", profile.UpdatedAt);
            });
        return rows > 0;
    }

    public async Task<bool> DeleteAsync(string id)
    {
        using var conn = connectionFactory.CreateConnection();
        var rows = await conn.ExecuteNonQueryAsync(
            "DELETE FROM harness_profiles WHERE id = @Id AND user_id = @UserId",
            cmd =>
            {
                cmd.AddParameter("Id", id);
                cmd.AddParameter("UserId", userContext.UserId);
            });
        return rows > 0;
    }

    public async Task SetDefaultAsync(string harnessType, string? id)
    {
        using var conn = connectionFactory.CreateConnection();
        using var tx = conn.BeginTransaction();
        await conn.ExecuteNonQueryAsync(
            "UPDATE harness_profiles SET is_default = 0 WHERE user_id = @UserId AND harness_type = @HarnessType",
            cmd =>
            {
                cmd.AddParameter("UserId", userContext.UserId);
                cmd.AddParameter("HarnessType", harnessType);
            },
            tx);
        if (id is not null)
        {
            await conn.ExecuteNonQueryAsync(
                "UPDATE harness_profiles SET is_default = 1 WHERE id = @Id AND user_id = @UserId AND harness_type = @HarnessType",
                cmd =>
                {
                    cmd.AddParameter("Id", id);
                    cmd.AddParameter("UserId", userContext.UserId);
                    cmd.AddParameter("HarnessType", harnessType);
                },
                tx);
        }
        tx.Commit();
    }

    public async Task<IReadOnlyDictionary<string, int>> CountOpenSessionsAsync(string harnessType)
    {
        using var conn = connectionFactory.CreateConnection();
        var rows = await conn.QueryAsync(
            """
            SELECT harness_profile_id, COUNT(*) AS open_sessions FROM sessions
            WHERE user_id = @UserId AND harness_type = @HarnessType AND harness_profile_id IS NOT NULL
              AND parent_session_id IS NULL AND retention_status <> 'archived'
            GROUP BY harness_profile_id
            """,
            cmd =>
            {
                cmd.AddParameter("UserId", userContext.UserId);
                cmd.AddParameter("HarnessType", harnessType);
            },
            r => (Id: r.GetString(0), Count: (int)r.GetInt64(1)));
        return rows.ToDictionary(row => row.Id, row => row.Count, StringComparer.Ordinal);
    }

    private static HarnessProfile ReadProfile(DbDataReader r) => new()
    {
        Id = r.GetString(r.GetOrdinal("id")),
        UserId = r.GetString(r.GetOrdinal("user_id")),
        HarnessType = r.GetString(r.GetOrdinal("harness_type")),
        Name = r.GetString(r.GetOrdinal("name")),
        Content = r.GetString(r.GetOrdinal("content")),
        IsDefault = r.GetInt64(r.GetOrdinal("is_default")) != 0,
        CreatedAt = r.GetString(r.GetOrdinal("created_at")),
        UpdatedAt = r.GetString(r.GetOrdinal("updated_at")),
    };
}
