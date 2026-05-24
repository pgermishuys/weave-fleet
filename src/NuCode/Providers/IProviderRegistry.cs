namespace NuCode.Providers;

/// <summary>
/// Registry of all known LLM providers supported by NuCode.
/// </summary>
public interface IProviderRegistry
{
    /// <summary>Returns all registered provider definitions.</summary>
    IReadOnlyList<ProviderDefinition> GetAll();

    /// <summary>
    /// Returns the provider definition for the given ID, or null if not found.
    /// </summary>
    ProviderDefinition? GetById(string providerId);

    /// <summary>
    /// Infers the provider ID from a model ID.
    /// Handles explicit prefix notation (e.g. "copilot/claude-sonnet-4-20250514")
    /// and model name prefix matching (e.g. "claude-*" → "anthropic").
    /// Returns the default provider ID if no match is found.
    /// </summary>
    string InferFromModelId(string modelId);

    /// <summary>
    /// Registers a custom provider definition, overriding any existing entry with the same ID.
    /// </summary>
    void Register(ProviderDefinition definition);
}
