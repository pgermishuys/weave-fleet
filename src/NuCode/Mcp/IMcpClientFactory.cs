using Microsoft.Extensions.Logging;

namespace NuCode.Mcp;

/// <summary>
/// Factory for creating MCP client connections. Extracted for testability.
/// </summary>
internal interface IMcpClientFactory
{
    /// <summary>
    /// Creates and connects an MCP client for the given server configuration.
    /// </summary>
    Task<IMcpClientWrapper> CreateAsync(McpServerConfig config, ILoggerFactory? loggerFactory, CancellationToken cancellationToken);
}
