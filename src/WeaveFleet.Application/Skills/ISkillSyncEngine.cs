using WeaveFleet.Domain.Skills;

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
/// Copies skills into harness skill folders (<c>~/.config/opencode/skills</c>, <c>&lt;repo&gt;/.opencode/skills</c>, …)
/// and removes them again. Only replaces or deletes folders Fleet wrote.
/// </summary>
public interface ISkillSyncEngine
{
    /// <summary>
    /// Re-copies every skill in the local user's manifest and records where each one landed.
    /// </summary>
    /// <param name="cancellationToken">Cancellation token.</param>
    /// <returns>A collection of sync results for each skill-harness pair.</returns>
    Task<IReadOnlyList<SkillSyncResult>> SyncAllAsync(CancellationToken cancellationToken = default);

    /// <summary>
    /// Copies one skill into the skill folder of each of its harnesses, at the entry's install target.
    /// Doesn't touch the manifest; use <see cref="SkillManifestEntryExtensions.WithSyncedPaths"/> to record the result.
    /// </summary>
    /// <param name="skill">The skill to copy.</param>
    /// <param name="cancellationToken">Cancellation token.</param>
    /// <returns>A result per harness; successful results carry the folder written.</returns>
    Task<IReadOnlyList<SkillSyncResult>> SyncSkillAsync(SkillManifestEntry skill, CancellationToken cancellationToken = default);

    /// <summary>
    /// Deletes the skill folders Fleet wrote for this entry. Folders Fleet didn't write are left alone.
    /// </summary>
    /// <param name="skill">The skill to remove.</param>
    /// <param name="cancellationToken">Cancellation token.</param>
    /// <returns>A result per folder that was (or failed to be) deleted.</returns>
    Task<IReadOnlyList<SkillSyncResult>> RemoveSkillAsync(SkillManifestEntry skill, CancellationToken cancellationToken = default);
}

public static class SkillManifestEntryExtensions
{
    /// <summary>
    /// Adds the folders a sync wrote to the entry's installed paths. Folders from earlier syncs stay:
    /// Fleet still owns them even when this sync couldn't refresh them.
    /// </summary>
    public static SkillManifestEntry WithSyncedPaths(this SkillManifestEntry entry, IEnumerable<SkillSyncResult> results) =>
        entry with
        {
            InstalledPaths = entry.InstalledPaths
                .Union(results.Where(r => r.Success && r.TargetPath is not null).Select(r => r.TargetPath!))
                .ToArray()
        };
}
