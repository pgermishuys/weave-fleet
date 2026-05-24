using Microsoft.Extensions.AI;

namespace NuCode.Providers;

/// <summary>
/// Creates <see cref="IChatClient"/> instances for a given provider and model.
/// The implementation is provided by the host (e.g. Fleet Infrastructure).
/// </summary>
public interface IChatClientFactory
{
    /// <summary>
    /// Creates a configured <see cref="IChatClient"/> for the specified provider and model.
    /// </summary>
    /// <param name="provider">The provider definition.</param>
    /// <param name="modelId">The model identifier (e.g. "claude-sonnet-4-20250514").</param>
    /// <param name="credentials">
    /// Resolved credentials for the provider (fieldKey → decrypted value).
    /// For Copilot this contains the short-lived Copilot API token under key "copilotToken".
    /// </param>
    /// <param name="options">
    /// Optional provider-level config overrides (e.g. "baseUrl", "resourceName").
    /// </param>
    /// <returns>A configured <see cref="IChatClient"/>.</returns>
    IChatClient Create(
        ProviderDefinition provider,
        string modelId,
        IReadOnlyDictionary<string, string> credentials,
        IReadOnlyDictionary<string, string>? options = null);
}
