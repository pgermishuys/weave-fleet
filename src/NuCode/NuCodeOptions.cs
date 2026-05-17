using NuCode.Configuration;
using NuCode.Mcp;

namespace NuCode;

/// <summary>
/// Configuration options for the NuCode library.
/// </summary>
public sealed class NuCodeOptions
{
    /// <summary>
    /// Gets or sets the working directory for tool execution.
    /// Defaults to <see cref="Directory.GetCurrentDirectory"/>.
    /// </summary>
    public string WorkingDirectory { get; set; } = Directory.GetCurrentDirectory();

    /// <summary>
    /// Gets the list of MCP server configurations.
    /// Add server configs to connect to MCP servers and make their tools available to agents.
    /// </summary>
    public List<McpServerConfig> McpServers { get; } = [];

    /// <summary>
    /// Gets or sets programmatic configuration overrides.
    /// These take the highest priority, overriding both global and project config files.
    /// </summary>
    public NuCodeConfig? Config { get; set; }
}
