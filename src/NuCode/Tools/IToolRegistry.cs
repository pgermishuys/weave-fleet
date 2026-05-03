using NuCode.Agents;

namespace NuCode.Tools;

/// <summary>
/// Registry for NuCode tools. Provides tool registration and lookup.
/// </summary>
public interface IToolRegistry
{
    /// <summary>
    /// Registers a tool.
    /// </summary>
    /// <param name="tool">The tool to register.</param>
    void Register(INuCodeTool tool);

    /// <summary>
    /// Gets all registered tools.
    /// </summary>
    IReadOnlyList<INuCodeTool> GetAll();

    /// <summary>
    /// Gets tools filtered for a specific agent profile (applying AllowedTools/DeniedTools).
    /// </summary>
    /// <param name="profile">The agent profile to filter tools for.</param>
    IReadOnlyList<INuCodeTool> GetForProfile(AgentProfile profile);

    /// <summary>
    /// Gets a tool by its ID.
    /// </summary>
    /// <param name="id">The tool ID.</param>
    /// <returns>The tool, or <c>null</c> if not found.</returns>
    INuCodeTool? Get(string id);
}
