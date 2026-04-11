using WeaveFleet.Domain.Entities;

namespace WeaveFleet.Domain.Repositories;

/// <summary>
/// Repository for user-scoped <see cref="UserCredential"/> records.
/// All queries are scoped to the requesting user.
/// </summary>
public interface IUserCredentialRepository
{
    /// <summary>Get a credential by its stable ID. Returns null when not found or when the credential does not belong to the current user.</summary>
    Task<UserCredential?> GetByIdAsync(string id);

    /// <summary>List all credentials belonging to the current user.</summary>
    Task<IReadOnlyList<UserCredential>> ListByUserAsync();

    /// <summary>List all credentials for the current user with the given namespace.</summary>
    Task<IReadOnlyList<UserCredential>> ListByUserAndNamespaceAsync(string credentialNamespace);

    /// <summary>List all credentials for the current user with the given namespace and kind.</summary>
    Task<IReadOnlyList<UserCredential>> ListByUserNamespaceAndKindAsync(string credentialNamespace, string kind);

    /// <summary>
    /// Insert or update a credential record.
    /// On conflict by (user_id, label), updates the encrypted value, display hint, metadata, and updated_at timestamp.
    /// </summary>
    Task UpsertAsync(UserCredential credential);

    /// <summary>Delete a credential by its stable ID. User-scoped — silently no-ops if the credential does not belong to the current user.</summary>
    Task DeleteAsync(string id);
}
