using WeaveFleet.Domain.Entities;

namespace WeaveFleet.Application.Services;

/// <summary>
/// Application-layer service for storing and retrieving user credentials.
/// Pure storage concern — no env-var building, no runtime dictionaries, no credential interpretation.
/// The store manages CRUD + encryption at rest; harnesses own all interpretation.
/// </summary>
public interface ICredentialStore
{
    /// <summary>
    /// List the current user's stored credentials with metadata.
    /// Values are NEVER included in the returned records — only metadata (label, namespace, kind, displayHint, etc.).
    /// </summary>
    Task<IReadOnlyList<CredentialSummary>> ListCredentialsAsync();

    /// <summary>
    /// Store a new credential for the current user. Encrypts the value before persisting.
    /// If a credential with the same label already exists for the user, it is updated.
    /// </summary>
    Task StoreCredentialAsync(
        string label,
        string credentialNamespace,
        string kind,
        string value,
        string? metadata = null);

    /// <summary>
    /// Delete a credential by its stable ID. User-scoped — silently no-ops if not owned by current user.
    /// </summary>
    Task DeleteCredentialAsync(string id);

    /// <summary>
    /// Returns all decrypted <see cref="UserCredential"/> records for the specified user.
    /// Used by <c>SessionOrchestrator</c> to pass a credential bag to <c>IHarness.PrepareRuntimeAsync()</c>.
    /// The returned records include decrypted values — callers must not log them.
    /// </summary>
    Task<IReadOnlyList<UserCredential>> GetDecryptedCredentialsAsync(string userId);
}

/// <summary>Metadata-only view of a stored credential — safe to return in API responses.</summary>
public sealed record CredentialSummary(
    string Id,
    string Label,
    string Namespace,
    string Kind,
    string DisplayHint,
    string? Metadata,
    string CreatedAt,
    string UpdatedAt);
