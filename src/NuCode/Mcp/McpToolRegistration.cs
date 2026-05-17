using Microsoft.Extensions.Logging;
using ModelContextProtocol.Client;
using NuCode.Tools;

namespace NuCode.Mcp;

/// <summary>
/// Connects to configured MCP servers and registers their tools in the tool registry.
/// Call <see cref="RegisterToolsAsync"/> during application startup to make MCP tools
/// available to agents.
/// </summary>
internal sealed class McpToolRegistration(
    IMcpManager mcpManager,
    IToolRegistry toolRegistry,
    ILoggerFactory? loggerFactory)
{
    private readonly ILogger? _logger = loggerFactory?.CreateLogger<McpToolRegistration>();

    /// <summary>
    /// Connects all configured MCP servers and registers their tools in the tool registry.
    /// Tools are namespaced as "{serverName}_{toolName}" to avoid conflicts.
    /// Servers that fail to connect are skipped (logged as warnings).
    /// </summary>
    /// <param name="cancellationToken">Cancellation token.</param>
    /// <returns>The number of tools registered.</returns>
    public async Task<int> RegisterToolsAsync(CancellationToken cancellationToken)
    {
        await mcpManager.ConnectAllAsync(cancellationToken);

        var toolsByServer = await mcpManager.GetToolsByServerAsync(cancellationToken);
        var registered = 0;

        foreach (var (serverName, tools) in toolsByServer)
        {
            foreach (var tool in tools)
            {
                if (tool is McpClientTool mcpTool)
                {
                    var adapter = new McpToolAdapter(mcpTool, serverName);

                    try
                    {
                        toolRegistry.Register(adapter);
                        registered++;
                        _logger?.LogDebug("Registered MCP tool '{ToolName}' from server '{ServerName}'", adapter.Name, serverName);
                    }
                    catch (InvalidOperationException ex)
                    {
                        _logger?.LogWarning(ex, "Failed to register MCP tool '{ToolName}' (duplicate?)", adapter.Name);
                    }
                }
            }
        }

        _logger?.LogInformation("Registered {Count} MCP tools from connected servers", registered);
        return registered;
    }

    /// <summary>
    /// Refreshes tools from a specific MCP server. Useful after reconnection or dynamic server addition.
    /// </summary>
    /// <param name="serverName">The MCP server name.</param>
    /// <param name="cancellationToken">Cancellation token.</param>
    /// <returns>The number of new tools registered.</returns>
    public async Task<int> RefreshServerToolsAsync(string serverName, CancellationToken cancellationToken)
    {
        var status = mcpManager.GetStatus();
        if (!status.TryGetValue(serverName, out var state) || state.Status != McpServerStatus.Connected)
        {
            _logger?.LogWarning("Cannot refresh tools for MCP server '{ServerName}': not connected (status: {Status})",
                serverName, state?.Status.ToString() ?? "unknown");
            return 0;
        }

        var toolsByServer = await mcpManager.GetToolsByServerAsync(cancellationToken);
        if (!toolsByServer.TryGetValue(serverName, out var tools))
        {
            return 0;
        }

        var registered = 0;

        foreach (var tool in tools)
        {
            if (tool is McpClientTool mcpTool)
            {
                var adapter = new McpToolAdapter(mcpTool, serverName);
                var existing = toolRegistry.Get(adapter.Name);
                if (existing is null)
                {
                    try
                    {
                        toolRegistry.Register(adapter);
                        registered++;
                    }
                    catch (InvalidOperationException)
                    {
                        // Race condition — tool was registered between Get and Register
                    }
                }
            }
        }

        return registered;
    }
}
