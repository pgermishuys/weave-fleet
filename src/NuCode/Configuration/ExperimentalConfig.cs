using System.Text.Json;
using System.Text.Json.Serialization;

namespace NuCode.Configuration;

/// <summary>
/// Configuration for experimental NuCode features.
/// Supports arbitrary extension data for forward compatibility.
/// </summary>
public sealed class ExperimentalConfig
{
    [JsonPropertyName("batchTool")]
    public bool? BatchTool { get; init; }

    [JsonPropertyName("openTelemetry")]
    public bool? OpenTelemetry { get; init; }

    [JsonPropertyName("primaryTools")]
    public List<string>? PrimaryTools { get; init; }

    [JsonPropertyName("continueLoopOnDeny")]
    public bool? ContinueLoopOnDeny { get; init; }

    [JsonPropertyName("mcpTimeout")]
    public int? McpTimeout { get; init; }

    [JsonExtensionData]
    public Dictionary<string, JsonElement>? ExtensionData { get; init; }
}
