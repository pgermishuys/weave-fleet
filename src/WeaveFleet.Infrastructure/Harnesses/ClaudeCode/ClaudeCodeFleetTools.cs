using WeaveFleet.Application.FleetTools;

namespace WeaveFleet.Infrastructure.Harnesses.ClaudeCode;

/// <summary>
/// How a claude process gets Fleet's own tools: Fleet's MCP server (<see cref="FleetToolCatalog"/>), at <c>/mcp</c> under
/// the process's <c>FLEET_URL</c>, whose bridge token tells Fleet which session is calling.
/// </summary>
internal static class ClaudeCodeFleetTools
{
    /// <summary>
    /// The server, for <c>--mcp-config</c>. Claude Code fills in <c>${FLEET_URL}</c> from the process's environment, so
    /// the bridge token stays off the command line, where any user on the machine could read it. Claude Code cuts a call
    /// to it after 60 s unless the server says otherwise (<c>timeout</c>, ms); Fleet's calls can be held much longer: a
    /// page waits for the user's pick for up to a day, and <c>fleet_app_start</c> waits minutes for the app.
    /// </summary>
    internal const string McpConfig = "{\"mcpServers\":{\"" + ClaudeCodeTools.FleetServer + "\":{\"type\":\"http\",\"url\":\"${FLEET_URL}/mcp\",\"timeout\":"
                                      + CallTimeoutMs + "}}}";

    /// <summary>How long Claude Code lets a call to Fleet's server run: a day, as long as a page waits for a pick.</summary>
    internal const string CallTimeoutMs = "86400000";

    /// <summary>
    /// Claude Code's names for the tools the process gets that never ask (<c>--allowedTools</c>): all but
    /// <c>fleet_app_start</c>, which asks as a shell command does.
    /// </summary>
    internal static IEnumerable<string> AllowedWithoutAsking(FleetToolSwitches switches)
        => FleetToolCatalog.For(switches)
            .Select(tool => ClaudeCodeTools.ClaudeName(tool.Name))
            .Where(name => ClaudeCodeTools.PermissionName(name) == ClaudeCodeTools.FleetTool(name));
}
