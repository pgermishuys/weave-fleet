namespace WeaveFleet.Domain.Skills;

/// <summary>
/// A single skill entry in the manifest, describing its source and installation metadata.
/// </summary>
public sealed record SkillManifestEntry
{
    /// <summary>The unique name of the skill.</summary>
    public required string Name { get; init; }

    /// <summary>The source type of the skill.</summary>
    public required SkillSource Source { get; init; }

    /// <summary>GitHub repository URL (required when Source is GitHub).</summary>
    public string? RepoUrl { get; init; }

    /// <summary>Git ref or branch (optional, defaults to main/master when Source is GitHub).</summary>
    public string? Ref { get; init; }

    /// <summary>Subdirectory path within the repository (optional, for skills in subdirectories).</summary>
    public string? SubPath { get; init; }

    /// <summary>Local filesystem path (required when Source is Local).</summary>
    public string? LocalPath { get; init; }

    /// <summary>List of harness types this skill targets (e.g., "opencode", "aider").</summary>
    public IReadOnlyList<string> TargetHarnesses { get; init; } = [];

    /// <summary>Timestamp when the skill was first installed.</summary>
    public required DateTimeOffset InstalledAt { get; init; }

    /// <summary>Timestamp when the skill was last updated.</summary>
    public required DateTimeOffset UpdatedAt { get; init; }
}

/// <summary>
/// The complete manifest of installed skills for a user or workspace.
/// </summary>
public sealed record SkillManifest
{
    /// <summary>The unique identifier for this manifest.</summary>
    public required string Id { get; init; }

    /// <summary>The user ID this manifest belongs to.</summary>
    public required string UserId { get; init; }

    /// <summary>Optional workspace ID if this manifest is workspace-scoped.</summary>
    public string? WorkspaceId { get; init; }

    /// <summary>The list of installed skills.</summary>
    public IReadOnlyList<SkillManifestEntry> Skills { get; init; } = [];

    /// <summary>Timestamp when the manifest was created.</summary>
    public required DateTimeOffset CreatedAt { get; init; }

    /// <summary>Timestamp when the manifest was last modified.</summary>
    public required DateTimeOffset UpdatedAt { get; init; }
}
