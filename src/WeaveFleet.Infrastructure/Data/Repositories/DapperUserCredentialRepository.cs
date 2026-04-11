using Dapper;
using WeaveFleet.Application.Data;
using WeaveFleet.Application.Services;
using WeaveFleet.Domain.Entities;
using WeaveFleet.Domain.Repositories;

namespace WeaveFleet.Infrastructure.Data.Repositories;

/// <summary>
/// Dapper-backed implementation of <see cref="IUserCredentialRepository"/>.
/// All queries are scoped to the current user via <see cref="IUserContext"/>.
/// </summary>
public sealed class DapperUserCredentialRepository(
    IDbConnectionFactory connectionFactory,
    IUserContext userContext) : IUserCredentialRepository
{
    public async Task<UserCredential?> GetByIdAsync(string id)
    {
        using var conn = connectionFactory.CreateConnection();
        return await conn.QueryFirstOrDefaultAsync<UserCredential>(
            "SELECT * FROM user_credentials WHERE id = @Id AND user_id = @UserId",
            new { Id = id, UserId = userContext.UserId });
    }

    public async Task<IReadOnlyList<UserCredential>> ListByUserAsync()
    {
        using var conn = connectionFactory.CreateConnection();
        var results = await conn.QueryAsync<UserCredential>(
            "SELECT * FROM user_credentials WHERE user_id = @UserId ORDER BY created_at ASC",
            new { UserId = userContext.UserId });
        return results.AsList();
    }

    public async Task<IReadOnlyList<UserCredential>> ListByUserAndNamespaceAsync(string credentialNamespace)
    {
        using var conn = connectionFactory.CreateConnection();
        var results = await conn.QueryAsync<UserCredential>(
            "SELECT * FROM user_credentials WHERE user_id = @UserId AND namespace = @Namespace ORDER BY created_at ASC",
            new { UserId = userContext.UserId, Namespace = credentialNamespace });
        return results.AsList();
    }

    public async Task<IReadOnlyList<UserCredential>> ListByUserNamespaceAndKindAsync(string credentialNamespace, string kind)
    {
        using var conn = connectionFactory.CreateConnection();
        var results = await conn.QueryAsync<UserCredential>(
            "SELECT * FROM user_credentials WHERE user_id = @UserId AND namespace = @Namespace AND kind = @Kind ORDER BY created_at ASC",
            new { UserId = userContext.UserId, Namespace = credentialNamespace, Kind = kind });
        return results.AsList();
    }

    public async Task UpsertAsync(UserCredential credential)
    {
        using var conn = connectionFactory.CreateConnection();
        await conn.ExecuteAsync(
            """
            INSERT INTO user_credentials (id, user_id, namespace, kind, label, encrypted_value, display_hint, metadata, created_at, updated_at)
            VALUES (@Id, @UserId, @Namespace, @Kind, @Label, @EncryptedValue, @DisplayHint, @Metadata, @CreatedAt, @UpdatedAt)
            ON CONFLICT(user_id, label) DO UPDATE SET
                encrypted_value = excluded.encrypted_value,
                display_hint = excluded.display_hint,
                metadata = excluded.metadata,
                updated_at = excluded.updated_at
            """,
            new
            {
                credential.Id,
                UserId = userContext.UserId,
                credential.Namespace,
                credential.Kind,
                credential.Label,
                credential.EncryptedValue,
                credential.DisplayHint,
                credential.Metadata,
                credential.CreatedAt,
                credential.UpdatedAt
            });
    }

    public async Task DeleteAsync(string id)
    {
        using var conn = connectionFactory.CreateConnection();
        await conn.ExecuteAsync(
            "DELETE FROM user_credentials WHERE id = @Id AND user_id = @UserId",
            new { Id = id, UserId = userContext.UserId });
    }
}
