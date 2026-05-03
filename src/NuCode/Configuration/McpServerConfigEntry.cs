using System.Text.Json.Serialization;

namespace NuCode.Configuration;

/// <summary>
/// Config-file form of an MCP server entry. Distinct from the runtime <c>McpServerConfig</c>.
/// </summary>
public sealed class McpServerConfigEntry
{
    /// <summary>
    /// Transport type: "local" (stdio) or "remote" (HTTP/SSE).
    /// </summary>
    [JsonPropertyName("type")]
    public string? Type { get; init; }

    /// <summary>
    /// Command to execute for local (stdio) servers.
    /// First element is the executable; remainder are arguments.
    /// </summary>
    [JsonPropertyName("command")]
    public List<string>? Command { get; init; }

    [JsonPropertyName("url")]
    public string? Url { get; init; }

    [JsonPropertyName("environment")]
    public Dictionary<string, string>? Environment { get; init; }

    [JsonPropertyName("headers")]
    public Dictionary<string, string>? Headers { get; init; }

    [JsonPropertyName("enabled")]
    public bool? Enabled { get; init; }

    [JsonPropertyName("timeout")]
    public int? Timeout { get; init; }
}
