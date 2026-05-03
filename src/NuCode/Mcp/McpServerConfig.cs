using System.Collections.Immutable;

namespace NuCode.Mcp;

/// <summary>
/// Configuration for an MCP server connection.
/// </summary>
public sealed class McpServerConfig
{
    /// <summary>
    /// Gets or sets the server name (used as the identifier).
    /// </summary>
    public required string Name { get; init; }

    /// <summary>
    /// Gets or sets the transport type.
    /// </summary>
    public required McpTransport Transport { get; init; }

    /// <summary>
    /// Gets or sets the command to execute (for stdio transport).
    /// First element is the executable, rest are arguments.
    /// </summary>
    public ImmutableArray<string> Command { get; init; } = [];

    /// <summary>
    /// Gets or sets the URL (for HTTP transport).
    /// </summary>
    public string? Url { get; init; }

    /// <summary>
    /// Gets or sets environment variables for the server process (stdio transport).
    /// </summary>
    public ImmutableDictionary<string, string>? Environment { get; init; }

    /// <summary>
    /// Gets or sets custom HTTP headers (HTTP transport).
    /// </summary>
    public ImmutableDictionary<string, string>? Headers { get; init; }

    /// <summary>
    /// Gets or sets whether the server is enabled. Defaults to <c>true</c>.
    /// </summary>
    public bool Enabled { get; init; } = true;

    /// <summary>
    /// Gets or sets the request timeout in milliseconds. Defaults to 30000 (30 seconds).
    /// </summary>
    public int TimeoutMs { get; init; } = 30_000;

    /// <summary>
    /// Gets or sets whether the server should automatically reconnect on failure. Defaults to <c>true</c>.
    /// </summary>
    public bool AutoReconnect { get; init; } = true;

    /// <summary>
    /// Gets or sets the maximum number of automatic reconnect attempts. Defaults to 3.
    /// </summary>
    public int MaxRestarts { get; init; } = 3;
}
