using System.Text.Json.Serialization;

namespace NuCode.Configuration;

/// <summary>
/// Configuration for tool execution timeouts.
/// </summary>
public sealed class TimeoutConfig
{
    /// <summary>
    /// Default timeout in milliseconds for any tool execution. Defaults to 30000 (30 seconds).
    /// </summary>
    [JsonPropertyName("defaultMs")]
    public int? DefaultMs { get; init; }

    /// <summary>
    /// Per-tool timeout overrides in milliseconds, keyed by tool name (e.g. "bash", "lsp", "task").
    /// When set, overrides <see cref="DefaultMs"/> for the named tool.
    /// </summary>
    [JsonPropertyName("toolOverrides")]
    public Dictionary<string, int>? ToolOverrides { get; init; }
}
