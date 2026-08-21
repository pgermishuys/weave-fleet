using WeaveFleet.Domain.Tools;

namespace WeaveFleet.Application.Tools;

/// <summary>
/// Persistence abstraction for tool manifests.
/// Manages reading and writing tool installation metadata to durable storage.
/// </summary>
public interface IToolManifestStore
{
    /// <summary>
    /// Loads the tool manifest for the specified user and optional workspace.
    /// Returns an empty manifest if the file does not exist.
    /// </summary>
    /// <param name="userId">The user ID owning the manifest.</param>
    /// <param name="workspaceId">Optional workspace ID for workspace-scoped manifests.</param>
    /// <param name="ct">Cancellation token.</param>
    /// <returns>The loaded manifest, or an empty manifest if not found.</returns>
    Task<ToolManifest> LoadAsync(string userId, string? workspaceId = null, CancellationToken ct = default);

    /// <summary>
    /// Saves the complete tool manifest atomically.
    /// </summary>
    /// <param name="manifest">The manifest to save.</param>
    /// <param name="ct">Cancellation token.</param>
    Task SaveAsync(ToolManifest manifest, CancellationToken ct = default);

    /// <summary>
    /// Adds a new tool entry to the manifest.
    /// Updates the manifest's UpdatedAt timestamp.
    /// </summary>
    /// <param name="userId">The user ID owning the manifest.</param>
    /// <param name="workspaceId">Optional workspace ID for workspace-scoped manifests.</param>
    /// <param name="entry">The tool entry to add.</param>
    /// <param name="ct">Cancellation token.</param>
    Task AddEntryAsync(string userId, string? workspaceId, ToolManifestEntry entry, CancellationToken ct = default);

    /// <summary>
    /// Removes a tool entry from the manifest by name.
    /// Updates the manifest's UpdatedAt timestamp.
    /// </summary>
    /// <param name="userId">The user ID owning the manifest.</param>
    /// <param name="workspaceId">Optional workspace ID for workspace-scoped manifests.</param>
    /// <param name="toolName">The name of the tool to remove.</param>
    /// <param name="ct">Cancellation token.</param>
    Task RemoveEntryAsync(string userId, string? workspaceId, string toolName, CancellationToken ct = default);

    /// <summary>
    /// Updates an existing tool entry in the manifest.
    /// Updates the manifest's UpdatedAt timestamp.
    /// </summary>
    /// <param name="userId">The user ID owning the manifest.</param>
    /// <param name="workspaceId">Optional workspace ID for workspace-scoped manifests.</param>
    /// <param name="entry">The updated tool entry.</param>
    /// <param name="ct">Cancellation token.</param>
    Task UpdateEntryAsync(string userId, string? workspaceId, ToolManifestEntry entry, CancellationToken ct = default);
}
