using WeaveFleet.Domain.Skills;

namespace WeaveFleet.Application.Skills;

/// <summary>
/// Persistence abstraction for skill manifests.
/// Manages reading and writing skill installation metadata to durable storage.
/// </summary>
public interface ISkillManifestStore
{
    /// <summary>
    /// Loads the skill manifest for the specified user and optional workspace.
    /// Returns an empty manifest if the file does not exist.
    /// </summary>
    /// <param name="userId">The user ID owning the manifest.</param>
    /// <param name="workspaceId">Optional workspace ID for workspace-scoped manifests.</param>
    /// <param name="ct">Cancellation token.</param>
    /// <returns>The loaded manifest, or an empty manifest if not found.</returns>
    Task<SkillManifest> LoadAsync(string userId, string? workspaceId = null, CancellationToken ct = default);

    /// <summary>
    /// Saves the complete skill manifest atomically.
    /// </summary>
    /// <param name="manifest">The manifest to save.</param>
    /// <param name="ct">Cancellation token.</param>
    Task SaveAsync(SkillManifest manifest, CancellationToken ct = default);

    /// <summary>
    /// Adds a new skill entry to the manifest.
    /// Updates the manifest's UpdatedAt timestamp.
    /// </summary>
    /// <param name="userId">The user ID owning the manifest.</param>
    /// <param name="workspaceId">Optional workspace ID for workspace-scoped manifests.</param>
    /// <param name="entry">The skill entry to add.</param>
    /// <param name="ct">Cancellation token.</param>
    Task AddEntryAsync(string userId, string? workspaceId, SkillManifestEntry entry, CancellationToken ct = default);

    /// <summary>
    /// Removes a skill entry from the manifest by name.
    /// Updates the manifest's UpdatedAt timestamp.
    /// </summary>
    /// <param name="userId">The user ID owning the manifest.</param>
    /// <param name="workspaceId">Optional workspace ID for workspace-scoped manifests.</param>
    /// <param name="skillName">The name of the skill to remove.</param>
    /// <param name="ct">Cancellation token.</param>
    Task RemoveEntryAsync(string userId, string? workspaceId, string skillName, CancellationToken ct = default);

    /// <summary>
    /// Updates an existing skill entry in the manifest.
    /// Updates the manifest's UpdatedAt timestamp.
    /// </summary>
    /// <param name="userId">The user ID owning the manifest.</param>
    /// <param name="workspaceId">Optional workspace ID for workspace-scoped manifests.</param>
    /// <param name="entry">The updated skill entry.</param>
    /// <param name="ct">Cancellation token.</param>
    Task UpdateEntryAsync(string userId, string? workspaceId, SkillManifestEntry entry, CancellationToken ct = default);
}
