namespace NuCode.Providers;

/// <summary>
/// NuCode's credential storage abstraction.
/// The host (e.g. Fleet) implements this interface; NuCode only depends on it.
/// All values are decrypted when returned — encryption at rest is the host's concern.
/// </summary>
public interface INuCodeCredentialStore
{
    /// <summary>
    /// Retrieves a single credential field for a provider, or null if not stored.
    /// </summary>
    Task<StoredCredential?> GetAsync(string providerId, string fieldKey, CancellationToken ct = default);

    /// <summary>
    /// Retrieves all stored credential fields for a provider.
    /// Returns an empty list if no credentials are stored for the provider.
    /// </summary>
    Task<IReadOnlyList<StoredCredential>> GetAllForProviderAsync(string providerId, CancellationToken ct = default);

    /// <summary>
    /// Stores (or replaces) a credential field for a provider.
    /// </summary>
    Task SetAsync(
        string providerId,
        string fieldKey,
        string value,
        DateTimeOffset? expiresAt = null,
        CancellationToken ct = default);

    /// <summary>
    /// Deletes a single credential field for a provider.
    /// No-op if the credential does not exist.
    /// </summary>
    Task DeleteAsync(string providerId, string fieldKey, CancellationToken ct = default);

    /// <summary>
    /// Deletes all stored credentials for a provider (i.e. disconnects the provider).
    /// No-op if no credentials are stored.
    /// </summary>
    Task DeleteAllForProviderAsync(string providerId, CancellationToken ct = default);

    /// <summary>
    /// Returns the IDs of all providers that have at least one stored credential.
    /// </summary>
    Task<IReadOnlyList<string>> ListConfiguredProviderIdsAsync(CancellationToken ct = default);
}
