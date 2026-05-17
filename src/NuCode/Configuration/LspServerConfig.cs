using System.Text.Json.Serialization;

namespace NuCode.Configuration;

/// <summary>
/// Configuration for an LSP server. Matches OpenCode's LSP config shape.
/// </summary>
public sealed class LspServerConfig
{
    /// <summary>
    /// The command to start the LSP server (e.g., ["typescript-language-server", "--stdio"]).
    /// </summary>
    [JsonPropertyName("command")]
    public List<string>? Command { get; init; }

    /// <summary>
    /// File extensions this LSP server handles (e.g., [".ts", ".tsx"]).
    /// </summary>
    [JsonPropertyName("extensions")]
    public List<string>? Extensions { get; init; }

    /// <summary>
    /// Environment variables to set when starting the server.
    /// </summary>
    [JsonPropertyName("env")]
    public Dictionary<string, string>? Env { get; init; }

    /// <summary>
    /// Initialization options to send to the LSP server during initialize.
    /// </summary>
    [JsonPropertyName("initialization")]
    public Dictionary<string, object?>? Initialization { get; init; }

    /// <summary>
    /// Set to true to disable this LSP server.
    /// </summary>
    [JsonPropertyName("disabled")]
    public bool? Disabled { get; init; }
}
