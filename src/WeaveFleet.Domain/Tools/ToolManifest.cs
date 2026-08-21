using WeaveFleet.Domain.Skills;

namespace WeaveFleet.Domain.Tools;

/// <summary>
/// A single tool entry in the manifest, describing its source and installation metadata.
/// </summary>
public sealed record ToolManifestEntry
{
    /// <summary>The unique name of the tool.</summary>
    public required string Name { get; init; }

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

    /// <summary>Local filesystem path (required when Source is Local).</summary>
    public string? LocalPath { get; init; }

    /// <summary>Timestamp when the tool was first installed.</summary>
    public required DateTimeOffset InstalledAt { get; init; }

    /// <summary>Timestamp when the tool was last updated.</summary>
    public required DateTimeOffset UpdatedAt { get; init; }
}

/// <summary>
/// The complete manifest of installed tools for a user or workspace.
/// </summary>
public sealed record ToolManifest
{
    /// <summary>The unique identifier for this manifest.</summary>
    public required string Id { get; init; }

    /// <summary>The user ID this manifest belongs to.</summary>
    public required string UserId { get; init; }

    /// <summary>Optional workspace ID if this manifest is workspace-scoped.</summary>
    public string? WorkspaceId { get; init; }

    /// <summary>The list of installed tools.</summary>
    public IReadOnlyList<ToolManifestEntry> Tools { get; init; } = [];

    /// <summary>Timestamp when the manifest was created.</summary>
    public required DateTimeOffset CreatedAt { get; init; }

    /// <summary>Timestamp when the manifest was last modified.</summary>
    public required DateTimeOffset UpdatedAt { get; init; }
}
