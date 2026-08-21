namespace WeaveFleet.Domain.Tools;

/// <summary>
/// The type of tool implementation.
/// </summary>
public enum ToolType
{
    /// <summary>Native tool built into the application.</summary>
    Native,

    /// <summary>Tool provided via Model Context Protocol (MCP).</summary>
    Mcp
}
