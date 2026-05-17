namespace NuCode.Mcp;

/// <summary>
/// Transport type for an MCP server connection.
/// </summary>
public enum McpTransport
{
    /// <summary>
    /// Standard I/O transport — spawns a local process and communicates via stdin/stdout.
    /// </summary>
    Stdio,

    /// <summary>
    /// HTTP-based transport using Streamable HTTP or SSE.
    /// </summary>
    Http,
}
