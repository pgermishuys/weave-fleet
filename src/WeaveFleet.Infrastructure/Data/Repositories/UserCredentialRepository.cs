using System.Data.Common;
using WeaveFleet.Application.Data;
using WeaveFleet.Application.Services;
using WeaveFleet.Domain.Entities;
using WeaveFleet.Domain.Repositories;

namespace WeaveFleet.Infrastructure.Data.Repositories;

/// <summary>
/// Raw ADO.NET implementation of <see cref="IUserCredentialRepository"/>.
/// All queries are scoped to the current user via <see cref="IUserContext"/>.
/// </summary>
public sealed class UserCredentialRepository(
    IDbConnectionFactory connectionFactory,
    IUserContext userContext) : IUserCredentialRepository
{
    public async Task<UserCredential?> GetByIdAsync(string id)
        => await GetByIdAsync(id, userContext.UserId).ConfigureAwait(false);

    public async Task<UserCredential?> GetByIdAsync(string id, string userId)
    {
        using var conn = connectionFactory.CreateConnection();
        return await conn.QueryFirstOrDefaultAsync(
            "SELECT * FROM user_credentials WHERE id = @Id AND user_id = @UserId",
            cmd =>
            {
                cmd.AddParameter("Id", id);
                cmd.AddParameter("UserId", userId);
            },
            ReadUserCredential).ConfigureAwait(false);
    }

    public async Task<IReadOnlyList<UserCredential>> ListByUserAsync()
        => await ListByUserAsync(userContext.UserId).ConfigureAwait(false);

    public async Task<IReadOnlyList<UserCredential>> ListByUserAsync(string userId)
    {
        using var conn = connectionFactory.CreateConnection();
        return await conn.QueryAsync(
            "SELECT * FROM user_credentials WHERE user_id = @UserId ORDER BY created_at ASC",
            cmd => { cmd.AddParameter("UserId", userId); },
            ReadUserCredential).ConfigureAwait(false);
    }

    public async Task<IReadOnlyList<UserCredential>> ListByUserAndNamespaceAsync(string credentialNamespace)
        => await ListByUserAndNamespaceAsync(userContext.UserId, credentialNamespace).ConfigureAwait(false);

    public async Task<IReadOnlyList<UserCredential>> ListByUserAndNamespaceAsync(string userId, string credentialNamespace)
    {
        using var conn = connectionFactory.CreateConnection();
        return await conn.QueryAsync(
            "SELECT * FROM user_credentials WHERE user_id = @UserId AND namespace = @Namespace ORDER BY created_at ASC",
            cmd =>
            {
                cmd.AddParameter("UserId", userId);
                cmd.AddParameter("Namespace", credentialNamespace);
            },
            ReadUserCredential).ConfigureAwait(false);
    }

    public async Task<IReadOnlyList<UserCredential>> ListByUserNamespaceAndKindAsync(string credentialNamespace, string kind)
        => await ListByUserNamespaceAndKindAsync(userContext.UserId, credentialNamespace, kind).ConfigureAwait(false);

    public async Task<IReadOnlyList<UserCredential>> ListByUserNamespaceAndKindAsync(string userId, string credentialNamespace, string kind)
    {
        using var conn = connectionFactory.CreateConnection();
        return await conn.QueryAsync(
            "SELECT * FROM user_credentials WHERE user_id = @UserId AND namespace = @Namespace AND kind = @Kind ORDER BY created_at ASC",
            cmd =>
            {
                cmd.AddParameter("UserId", userId);
                cmd.AddParameter("Namespace", credentialNamespace);
                cmd.AddParameter("Kind", kind);
            },
            ReadUserCredential).ConfigureAwait(false);
    }

    public async Task UpsertAsync(UserCredential credential)
    {
        using var conn = connectionFactory.CreateConnection();
        await conn.ExecuteNonQueryAsync(
            """
            INSERT INTO user_credentials (id, user_id, namespace, kind, label, encrypted_value, display_hint, metadata, created_at, updated_at)
            VALUES (@Id, @UserId, @Namespace, @Kind, @Label, @EncryptedValue, @DisplayHint, @Metadata, @CreatedAt, @UpdatedAt)
            ON CONFLICT(user_id, label) DO UPDATE SET
                encrypted_value = excluded.encrypted_value,
                display_hint = excluded.display_hint,
                metadata = excluded.metadata,
                updated_at = excluded.updated_at
            """,
            cmd =>
            {
                cmd.AddParameter("Id", credential.Id);
                cmd.AddParameter("UserId", credential.UserId);
                cmd.AddParameter("Namespace", credential.Namespace);
                cmd.AddParameter("Kind", credential.Kind);
                cmd.AddParameter("Label", credential.Label);
                cmd.AddParameter("EncryptedValue", credential.EncryptedValue);
                cmd.AddParameter("DisplayHint", credential.DisplayHint);
                cmd.AddParameter("Metadata", credential.Metadata);
                cmd.AddParameter("CreatedAt", credential.CreatedAt);
                cmd.AddParameter("UpdatedAt", credential.UpdatedAt);
            }).ConfigureAwait(false);
    }

    public async Task DeleteAsync(string id)
        => await DeleteAsync(id, userContext.UserId).ConfigureAwait(false);

    public async Task DeleteAsync(string id, string userId)
    {
        using var conn = connectionFactory.CreateConnection();
        await conn.ExecuteNonQueryAsync(
            "DELETE FROM user_credentials WHERE id = @Id AND user_id = @UserId",
            cmd =>
            {
                cmd.AddParameter("Id", id);
                cmd.AddParameter("UserId", userId);
            }).ConfigureAwait(false);
    }

    private static UserCredential ReadUserCredential(DbDataReader r) => new()
    {
        Id = r.GetString(r.GetOrdinal("id")),
        UserId = r.GetString(r.GetOrdinal("user_id")),
        Namespace = r.GetString(r.GetOrdinal("namespace")),
        Kind = r.GetString(r.GetOrdinal("kind")),
        Label = r.GetString(r.GetOrdinal("label")),
        EncryptedValue = r.GetString(r.GetOrdinal("encrypted_value")),
        DisplayHint = r.GetString(r.GetOrdinal("display_hint")),
        Metadata = r.GetNullableString(r.GetOrdinal("metadata")),
        CreatedAt = r.GetString(r.GetOrdinal("created_at")),
        UpdatedAt = r.GetString(r.GetOrdinal("updated_at")),
    };
}
