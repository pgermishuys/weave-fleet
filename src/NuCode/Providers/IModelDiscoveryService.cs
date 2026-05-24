namespace NuCode.Providers;

/// <summary>
/// Discovers available models from a provider's API.
/// The implementation is provided by the host (e.g. Fleet Infrastructure)
/// since it requires HTTP access.
/// </summary>
public interface IModelDiscoveryService
{
    /// <summary>
    /// Queries the provider's API to discover available models.
    /// </summary>
    /// <param name="provider">The provider definition.</param>
    /// <param name="credentials">Resolved credentials (fieldKey → decrypted value).</param>
    /// <param name="options">Optional provider-level config overrides (e.g. "baseUrl").</param>
    /// <param name="ct">Cancellation token.</param>
    /// <returns>List of discovered models, or empty if discovery fails or is unsupported.</returns>
    Task<IReadOnlyList<DiscoveredModel>> DiscoverModelsAsync(
        ProviderDefinition provider,
        IReadOnlyDictionary<string, string> credentials,
        IReadOnlyDictionary<string, string>? options = null,
        CancellationToken ct = default);
}

/// <summary>
/// A model discovered from a provider's API.
/// </summary>
/// <param name="Id">The model identifier (e.g. "gpt-4o", "claude-sonnet-4-20250514").</param>
/// <param name="Name">Optional human-readable display name.</param>
public sealed record DiscoveredModel(string Id, string? Name = null);
