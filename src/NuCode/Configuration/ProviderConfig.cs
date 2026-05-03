using System.Text.Json;
using System.Text.Json.Serialization;

namespace NuCode.Configuration;

/// <summary>
/// LLM provider configuration entry.
/// </summary>
public sealed class ProviderConfig
{
    [JsonPropertyName("options")]
    public ProviderOptions? Options { get; init; }

    [JsonPropertyName("whitelist")]
    public List<string>? Whitelist { get; init; }

    [JsonPropertyName("blacklist")]
    public List<string>? Blacklist { get; init; }
}

/// <summary>
/// Options for an LLM provider. Supports well-known fields plus arbitrary extension data.
/// </summary>
public sealed class ProviderOptions
{
    [JsonPropertyName("apiKey")]
    public string? ApiKey { get; init; }

    [JsonPropertyName("baseUrl")]
    public string? BaseUrl { get; init; }

    [JsonPropertyName("timeout")]
    public int? Timeout { get; init; }

    [JsonExtensionData]
    public Dictionary<string, JsonElement>? ExtensionData { get; init; }
}
