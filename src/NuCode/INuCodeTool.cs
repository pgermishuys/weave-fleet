using Microsoft.Extensions.AI;

namespace NuCode;

/// <summary>
/// Defines a NuCode tool that can be registered with the tool registry and invoked by agents.
/// </summary>
public interface INuCodeTool
{
    /// <summary>
    /// Gets the unique identifier for this tool.
    /// </summary>
    string Name { get; }

    /// <summary>
    /// Gets the human-readable description of what this tool does.
    /// </summary>
    string Description { get; }

    /// <summary>
    /// Converts this tool to an <see cref="AIFunction"/> for use with the Microsoft Agent Framework.
    /// </summary>
    AIFunction ToAIFunction();
}
