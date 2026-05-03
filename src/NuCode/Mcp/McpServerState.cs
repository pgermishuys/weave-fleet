namespace NuCode.Mcp;

/// <summary>
/// Snapshot of an MCP server's connection state.
/// </summary>
/// <param name="Name">Server name.</param>
/// <param name="Status">Current connection status.</param>
/// <param name="Error">Error message when <paramref name="Status"/> is <see cref="McpServerStatus.Failed"/>.</param>
public sealed record McpServerState(
    string Name,
    McpServerStatus Status,
    string? Error = null,
    int RestartCount = 0,
    int MaxRestarts = 3);
