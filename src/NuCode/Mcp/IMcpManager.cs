using Microsoft.Extensions.AI;

namespace NuCode.Mcp;

/// <summary>
/// Manages MCP server connections and provides tools from connected servers.
/// </summary>
public interface IMcpManager : IAsyncDisposable
{
    /// <summary>
    /// Connects to all configured MCP servers.
    /// </summary>
    Task ConnectAllAsync(CancellationToken cancellationToken);

    /// <summary>
    /// Connects (or reconnects) a specific MCP server by name.
    /// </summary>
    /// <param name="name">The server name.</param>
    /// <param name="cancellationToken">Cancellation token.</param>
    Task ConnectAsync(string name, CancellationToken cancellationToken);

    /// <summary>
    /// Disconnects a specific MCP server.
    /// </summary>
    /// <param name="name">The server name.</param>
    /// <param name="cancellationToken">Cancellation token.</param>
    Task DisconnectAsync(string name, CancellationToken cancellationToken);

    /// <summary>
    /// Gets the connection status of all configured MCP servers.
    /// </summary>
    IReadOnlyDictionary<string, McpServerState> GetStatus();

    /// <summary>
    /// Gets all tools from all connected MCP servers as flat <see cref="AITool"/> list.
    /// Since <c>McpClientTool</c> inherits from <see cref="AIFunction"/>, they can be used directly as <see cref="AITool"/>.
    /// </summary>
    /// <param name="cancellationToken">Cancellation token.</param>
    Task<IReadOnlyList<AITool>> GetToolsAsync(CancellationToken cancellationToken);

    /// <summary>
    /// Gets tools from all connected MCP servers, grouped by server name.
    /// Each entry maps the server name to its list of tools.
    /// </summary>
    /// <param name="cancellationToken">Cancellation token.</param>
    Task<IReadOnlyDictionary<string, IReadOnlyList<AITool>>> GetToolsByServerAsync(CancellationToken cancellationToken);

    /// <summary>
    /// Adds a new MCP server configuration dynamically (not from initial config).
    /// Connects automatically if the config is enabled.
    /// </summary>
    /// <param name="config">The server configuration to add.</param>
    /// <param name="cancellationToken">Cancellation token.</param>
    Task AddAsync(McpServerConfig config, CancellationToken cancellationToken);

    /// <summary>
    /// Raised when a server's connection state changes.
    /// </summary>
    event Action<McpServerState>? ServerStateChanged;

    /// <summary>
    /// Checks the health of a specific MCP server by pinging it.
    /// If the server was connected but the ping fails, triggers the reconnect flow.
    /// </summary>
    /// <param name="name">The server name.</param>
    /// <param name="cancellationToken">Cancellation token.</param>
    Task<McpServerState> CheckHealthAsync(string name, CancellationToken cancellationToken);
}
