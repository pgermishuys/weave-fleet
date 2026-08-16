namespace WeaveFleet.Application.Skills;

/// <summary>
/// Result of syncing a single skill to a single harness.
/// </summary>
public sealed record SkillSyncResult
{
    /// <summary>The skill name.</summary>
    public required string SkillName { get; init; }

    /// <summary>The harness identifier (e.g., "opencode", "claude-code").</summary>
    public required string Harness { get; init; }

    /// <summary>Whether the sync succeeded.</summary>
    public required bool Success { get; init; }

    /// <summary>Whether the sync was skipped (e.g., user-managed folder exists).</summary>
    public required bool Skipped { get; init; }

    /// <summary>Optional error message if sync failed.</summary>
    public string? ErrorMessage { get; init; }

    /// <summary>The target path where the skill was deployed.</summary>
    public string? TargetPath { get; init; }
}

/// <summary>
/// Synchronizes skills from the manifest to harness discovery paths.
/// </summary>
public interface ISkillSyncEngine
{
    /// <summary>
    /// Synchronizes all skills in the manifest to their target harnesses.
    /// </summary>
    /// <param name="cancellationToken">Cancellation token.</param>
    /// <returns>A collection of sync results for each skill-harness pair.</returns>
    Task<IReadOnlyList<SkillSyncResult>> SyncAllAsync(CancellationToken cancellationToken = default);

    /// <summary>
    /// Synchronizes a single skill by name to its target harnesses.
    /// </summary>
    /// <param name="skillName">The name of the skill to sync.</param>
    /// <param name="cancellationToken">Cancellation token.</param>
    /// <returns>A collection of sync results for each harness the skill targets.</returns>
    Task<IReadOnlyList<SkillSyncResult>> SyncSkillAsync(string skillName, CancellationToken cancellationToken = default);
}
