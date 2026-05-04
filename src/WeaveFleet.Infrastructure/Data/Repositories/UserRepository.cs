using System.Data.Common;
using WeaveFleet.Application.Data;
using WeaveFleet.Domain.Entities;
using WeaveFleet.Domain.Repositories;

namespace WeaveFleet.Infrastructure.Data.Repositories;

public sealed class UserRepository(IDbConnectionFactory connectionFactory) : IUserRepository
{
    public async Task<User?> GetByIdAsync(string id)
    {
        using var conn = connectionFactory.CreateConnection();
        return await conn.QueryFirstOrDefaultAsync(
            "SELECT * FROM users WHERE id = @Id",
            cmd => { cmd.AddParameter("Id", id); },
            ReadUser);
    }

    public async Task UpsertAsync(User user)
    {
        using var conn = connectionFactory.CreateConnection();
        await conn.ExecuteNonQueryAsync(
            """
            INSERT INTO users (id, email, display_name, status, created_at, last_login_at, onboarding_completed_at)
            VALUES (@Id, @Email, @DisplayName, @Status, @CreatedAt, @LastLoginAt, @OnboardingCompletedAt)
            ON CONFLICT(id) DO UPDATE SET
                email = excluded.email,
                display_name = excluded.display_name,
                last_login_at = excluded.last_login_at
            """,
            cmd =>
            {
                cmd.AddParameter("Id", user.Id);
                cmd.AddParameter("Email", user.Email);
                cmd.AddParameter("DisplayName", user.DisplayName);
                cmd.AddParameter("Status", user.Status);
                cmd.AddParameter("CreatedAt", user.CreatedAt);
                cmd.AddParameter("LastLoginAt", user.LastLoginAt);
                cmd.AddParameter("OnboardingCompletedAt", user.OnboardingCompletedAt);
            });
    }

    public async Task UpdateLastLoginAsync(string id, string lastLoginAt)
    {
        using var conn = connectionFactory.CreateConnection();
        await conn.ExecuteNonQueryAsync(
            "UPDATE users SET last_login_at = @LastLoginAt WHERE id = @Id",
            cmd =>
            {
                cmd.AddParameter("Id", id);
                cmd.AddParameter("LastLoginAt", lastLoginAt);
            });
    }

    public async Task UpdateOnboardingCompletedAsync(string id, string completedAt)
    {
        using var conn = connectionFactory.CreateConnection();
        await conn.ExecuteNonQueryAsync(
            "UPDATE users SET onboarding_completed_at = @CompletedAt WHERE id = @Id",
            cmd =>
            {
                cmd.AddParameter("Id", id);
                cmd.AddParameter("CompletedAt", completedAt);
            });
    }

    private static User ReadUser(DbDataReader r) => new()
    {
        Id = r.GetString(r.GetOrdinal("id")),
        Email = r.GetString(r.GetOrdinal("email")),
        DisplayName = r.GetNullableString(r.GetOrdinal("display_name")),
        Status = r.GetString(r.GetOrdinal("status")),
        CreatedAt = r.GetString(r.GetOrdinal("created_at")),
        LastLoginAt = r.GetNullableString(r.GetOrdinal("last_login_at")),
        OnboardingCompletedAt = r.GetNullableString(r.GetOrdinal("onboarding_completed_at")),
    };
}
