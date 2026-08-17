namespace WeaveFleet.Domain.Skills;

/// <summary>
/// A skill entry in the public or private catalog, representing a discoverable skill.
/// </summary>
public sealed record CatalogEntry
{
    /// <summary>The unique name of the skill.</summary>
    public required string Name { get; init; }

    /// <summary>Human-readable display name.</summary>
    public string? DisplayName { get; init; }

    /// <summary>Short description of what the skill does.</summary>
    public string? Description { get; init; }

    /// <summary>The source type of the skill.</summary>
    public required SkillSource Source { get; init; }

    /// <summary>GitHub repository URL (required when Source is GitHub).</summary>
    public string? RepoUrl { get; init; }

    /// <summary>Default Git ref or branch (optional, defaults to main/master when Source is GitHub).</summary>
    public string? Ref { get; init; }

    /// <summary>Subdirectory within the repository (optional, GitHub only).</summary>
    public string? SubPath { get; init; }

    /// <summary>Local filesystem path (required when Source is Local).</summary>
    public string? LocalPath { get; init; }

    /// <summary>List of harness types this skill targets (e.g., "opencode", "aider").</summary>
    public IReadOnlyList<string> TargetHarnesses { get; init; } = [];

    /// <summary>Author or maintainer of the skill.</summary>
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
