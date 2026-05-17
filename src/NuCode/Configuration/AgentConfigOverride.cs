using System.Text.Json;
using System.Text.Json.Serialization;

namespace NuCode.Configuration;

/// <summary>
/// Per-agent configuration overrides. Each property is nullable — only set values override defaults.
/// </summary>
public sealed class AgentConfigOverride
{
    [JsonPropertyName("name")]
    public string? Name { get; init; }

    [JsonPropertyName("description")]
    public string? Description { get; init; }

    [JsonPropertyName("model")]
    public string? Model { get; init; }

    [JsonPropertyName("prompt")]
    public string? Prompt { get; init; }

    [JsonPropertyName("temperature")]
    public float? Temperature { get; init; }

    [JsonPropertyName("topP")]
    public float? TopP { get; init; }

    [JsonPropertyName("steps")]
    public int? Steps { get; init; }

    [JsonPropertyName("hidden")]
    public bool? Hidden { get; init; }

    [JsonPropertyName("disable")]
    public bool? Disable { get; init; }

    [JsonPropertyName("mode")]
    public string? Mode { get; init; }

    [JsonPropertyName("permission")]
    public PermissionConfig? Permission { get; init; }

    [JsonPropertyName("options")]
    public Dictionary<string, JsonElement>? Options { get; init; }
}
