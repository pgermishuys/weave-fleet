using Microsoft.Extensions.AI;
using ModelContextProtocol.Client;

namespace NuCode.Mcp;

/// <summary>
/// Thin wrapper around <see cref="McpClient"/> to enable test doubles.
/// </summary>
internal interface IMcpClientWrapper : IAsyncDisposable
{
    /// <summary>
    /// Pings the MCP server to check connectivity.
    /// </summary>
    Task PingAsync(CancellationToken cancellationToken);

    /// <summary>
    /// Lists available tools from the MCP server.
    /// </summary>
    Task<IList<McpClientTool>> ListToolsAsync(CancellationToken cancellationToken);
}
