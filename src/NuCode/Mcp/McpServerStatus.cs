namespace NuCode.Mcp;

/// <summary>
/// Connection status of an MCP server.
/// </summary>
public enum McpServerStatus
{
    /// <summary>
    /// Server is connected and operational.
    /// </summary>
    Connected,

    /// <summary>
    /// Server is disabled (either by configuration or explicit disconnect).
    /// </summary>
    Disabled,

    /// <summary>
    /// Connection attempt failed.
    /// </summary>
    Failed,

    /// <summary>
    /// Server disconnected and is attempting to reconnect.
    /// </summary>
    Reconnecting,
}
