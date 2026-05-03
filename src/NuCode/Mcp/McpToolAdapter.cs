using Microsoft.Extensions.AI;
using ModelContextProtocol.Client;

namespace NuCode.Mcp;

/// <summary>
/// Adapts a <see cref="McpClientTool"/> (from an MCP server) to the <see cref="INuCodeTool"/> interface,
/// allowing MCP tools to be registered in the tool registry and participate in the permission system.
/// </summary>
internal sealed class McpToolAdapter : INuCodeTool
{
    private readonly McpClientTool _mcpTool;

    public McpToolAdapter(McpClientTool mcpTool, string serverName)
    {
        _mcpTool = mcpTool;
        ServerName = serverName;
        Name = $"{serverName}_{mcpTool.Name}";
        Description = string.IsNullOrEmpty(mcpTool.Description)
            ? $"MCP tool '{mcpTool.Name}' from server '{serverName}'."
            : mcpTool.Description;
    }

    /// <summary>
    /// Gets the MCP server name this tool belongs to.
    /// </summary>
    public string ServerName { get; }

    /// <inheritdoc />
    public string Name { get; }

    /// <inheritdoc />
    public string Description { get; }

    /// <inheritdoc />
    public AIFunction ToAIFunction()
    {
        // McpClientTool already IS an AIFunction — but we need to rename it
        // to include the server prefix for namespacing.
        return _mcpTool.WithName(Name);
    }
}
