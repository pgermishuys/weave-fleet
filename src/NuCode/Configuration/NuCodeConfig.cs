using System.Text.Json.Serialization;

namespace NuCode.Configuration;

/// <summary>
/// Root configuration model for NuCode. Represents the deserialized shape of a nucode.jsonc config file.
/// </summary>
public sealed class NuCodeConfig
{
    [JsonPropertyName("agents")]
    public Dictionary<string, AgentConfigOverride>? Agents { get; init; }

    [JsonPropertyName("permission")]
    public PermissionConfig? Permission { get; init; }

    [JsonPropertyName("mcp")]
    public Dictionary<string, McpServerConfigEntry>? Mcp { get; init; }

    [JsonPropertyName("provider")]
    public Dictionary<string, ProviderConfig>? Provider { get; init; }

    [JsonPropertyName("enabledProviders")]
    public List<string>? EnabledProviders { get; init; }

    [JsonPropertyName("disabledProviders")]
    public List<string>? DisabledProviders { get; init; }

    [JsonPropertyName("model")]
    public string? Model { get; init; }

    [JsonPropertyName("smallModel")]
    public string? SmallModel { get; init; }

    [JsonPropertyName("defaultAgent")]
    public string? DefaultAgent { get; init; }

    [JsonPropertyName("plugin")]
    public List<string>? Plugins { get; init; }

    [JsonPropertyName("instructions")]
    public List<string>? Instructions { get; init; }

    [JsonPropertyName("snapshot")]
    public bool? Snapshot { get; init; }

    [JsonPropertyName("compaction")]
    public CompactionConfig? Compaction { get; init; }

    [JsonPropertyName("experimental")]
    public ExperimentalConfig? Experimental { get; init; }

    [JsonPropertyName("skills")]
    public Dictionary<string, SkillConfig>? Skills { get; init; }

    [JsonPropertyName("lsp")]
    public Dictionary<string, LspServerConfig>? Lsp { get; init; }

    /// <summary>
    /// When true (default), auto-detect common LSP servers on PATH.
    /// Set to false to disable auto-detection entirely.
    /// </summary>
    [JsonPropertyName("lspAutoDetect")]
    public bool? LspAutoDetect { get; init; }

    /// <summary>
    /// Configuration for tool execution timeouts (global default and per-tool overrides).
    /// </summary>
    [JsonPropertyName("timeout")]
    public TimeoutConfig? Timeout { get; init; }

    [JsonPropertyName("logLevel")]
    public string? LogLevel { get; init; }
}
