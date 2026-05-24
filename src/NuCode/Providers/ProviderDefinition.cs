namespace NuCode.Providers;

/// <summary>
/// Static description of an LLM provider supported by NuCode.
/// A <see cref="ProviderDefinition"/> is immutable data — it describes what a provider is,
/// not the runtime state of a user's connection to it.
/// </summary>
public sealed record ProviderDefinition
{
    /// <summary>
    /// Stable machine-readable identifier (e.g. "anthropic", "openai", "copilot", "amazon-bedrock").
    /// Used as the key in credential storage.
    /// </summary>
    public required string Id { get; init; }

    /// <summary>Human-readable display name (e.g. "Anthropic", "GitHub Copilot").</summary>
    public required string DisplayName { get; init; }

    /// <summary>Optional short description shown in the provider picker UI.</summary>
    public string? Description { get; init; }

    /// <summary>How this provider authenticates users.</summary>
    public required AuthMechanism AuthMechanism { get; init; }

    /// <summary>
    /// Credential fields required (or optional) for this provider.
    /// For API-key providers this is typically a single "apiKey" field.
    /// For complex providers (AWS, Azure) there may be additional config fields.
    /// </summary>
    public IReadOnlyList<CredentialField> CredentialFields { get; init; } = [];

    /// <summary>
    /// Default API endpoint URL. Null for providers that don't use a fixed endpoint
    /// (e.g. AWS Bedrock uses regional endpoints).
    /// </summary>
    public string? DefaultEndpoint { get; init; }

    /// <summary>
    /// Whether the user can override the endpoint with a custom base URL.
    /// True for most OpenAI-compatible providers.
    /// </summary>
    public bool SupportsCustomBaseUrl { get; init; }

    /// <summary>
    /// Whether this provider exposes an OpenAI-compatible chat completions API.
    /// When true, the standard OpenAI SDK client can be used with a custom endpoint.
    /// </summary>
    public bool IsOpenAiCompatible { get; init; } = true;

    /// <summary>
    /// Model ID prefixes used to infer this provider from a model name.
    /// E.g. ["claude"] for Anthropic, ["gpt", "o1", "o3", "o4"] for OpenAI.
    /// </summary>
    public IReadOnlyList<string> ModelPrefixes { get; init; } = [];

    /// <summary>
    /// Whether the API key / credential is optional (e.g. local models like Ollama).
    /// </summary>
    public bool CredentialOptional { get; init; }
}
