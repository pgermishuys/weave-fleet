using Dapper;
using WeaveFleet.Application.Data;
using WeaveFleet.Domain.Entities;
using WeaveFleet.Domain.Repositories;

namespace WeaveFleet.Infrastructure.Data.Repositories;

public sealed class DapperUserRepository(IDbConnectionFactory connectionFactory) : IUserRepository
{
    public async Task<User?> GetByIdAsync(string id)
    {
        using var conn = connectionFactory.CreateConnection();
        return await conn.QueryFirstOrDefaultAsync<User>(
            "SELECT * FROM users WHERE id = @Id", new { Id = id });
    }

    public async Task UpsertAsync(User user)
    {
        using var conn = connectionFactory.CreateConnection();
        await conn.ExecuteAsync(
            """
            INSERT INTO users (id, email, display_name, status, created_at, last_login_at, onboarding_completed_at)
            VALUES (@Id, @Email, @DisplayName, @Status, @CreatedAt, @LastLoginAt, @OnboardingCompletedAt)
            ON CONFLICT(id) DO UPDATE SET
                email = excluded.email,
                display_name = excluded.display_name,
                last_login_at = excluded.last_login_at
            """, user);
    }

    public async Task UpdateLastLoginAsync(string id, string lastLoginAt)
    {
        using var conn = connectionFactory.CreateConnection();
        await conn.ExecuteAsync(
            "UPDATE users SET last_login_at = @LastLoginAt WHERE id = @Id",
            new { Id = id, LastLoginAt = lastLoginAt });
    }

    public async Task UpdateOnboardingCompletedAsync(string id, string completedAt)
    {
        using var conn = connectionFactory.CreateConnection();
        await conn.ExecuteAsync(
            "UPDATE users SET onboarding_completed_at = @CompletedAt WHERE id = @Id",
            new { Id = id, CompletedAt = completedAt });
    }
}
