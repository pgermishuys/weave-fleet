using WeaveFleet.Domain.Skills;

namespace WeaveFleet.Domain.Tools;

/// <summary>
/// A tool entry in the public or private catalog, representing a discoverable tool.
/// </summary>
public sealed record ToolCatalogEntry
{
    /// <summary>The unique name of the tool.</summary>
    public required string Name { get; init; }

    /// <summary>Human-readable display name.</summary>
    public string? DisplayName { get; init; }

    /// <summary>Short description of what the tool does.</summary>
    public string? Description { get; init; }

    /// <summary>The type of tool implementation.</summary>
    public required ToolType ToolType { get; init; }

    /// <summary>The source type of the tool.</summary>
    public required SkillSource Source { get; init; }

    /// <summary>Command to execute for MCP tools (required when ToolType is Mcp).</summary>
    public string? Command { get; init; }

    /// <summary>Command-line arguments for MCP tools (optional).</summary>
    public IReadOnlyList<string>? Args { get; init; }

    /// <summary>Environment variables for MCP tools (optional).</summary>
    public IReadOnlyDictionary<string, string>? Env { get; init; }

    /// <summary>GitHub repository URL (required when Source is GitHub).</summary>
    public string? RepoUrl { get; init; }

    /// <summary>Default Git ref or branch (optional, defaults to main/master when Source is GitHub).</summary>
    public string? Ref { get; init; }

    /// <summary>Subdirectory within the repository (optional, GitHub only).</summary>
    public string? SubPath { get; init; }

    /// <summary>Local filesystem path (required when Source is Local).</summary>
    public string? LocalPath { get; init; }

    /// <summary>Author or maintainer of the tool.</summary>
    public string? Author { get; init; }

    /// <summary>Version string (e.g., "1.0.0").</summary>
    public string? Version { get; init; }

    /// <summary>Tags for categorization and search.</summary>
    public IReadOnlyList<string> Tags { get; init; } = [];

    /// <summary>Timestamp when the catalog entry was created.</summary>
    public DateTimeOffset? CreatedAt { get; init; }

    /// <summary>Timestamp when the catalog entry was last updated.</summary>
    public DateTimeOffset? UpdatedAt { get; init; }
}
