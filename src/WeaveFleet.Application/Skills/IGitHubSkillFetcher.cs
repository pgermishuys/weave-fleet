using WeaveFleet.Domain.Common;
using WeaveFleet.Domain.Skills;

namespace WeaveFleet.Application.Skills;

/// <summary>
/// Service for cloning and updating skills from GitHub repositories.
/// </summary>
public interface IGitHubSkillFetcher
{
    /// <summary>
    /// Clones or updates a skill repository to the local filesystem.
    /// </summary>
    /// <param name="repoUrl">The GitHub repository URL (HTTPS format).</param>
    /// <param name="skillName">The name of the skill (used for the local directory name).</param>
    /// <param name="gitRef">Optional git ref (branch, tag, or commit SHA). Defaults to HEAD if not specified.</param>
    /// <param name="subPath">Optional subdirectory path within the repository for sparse checkout.</param>
    /// <param name="cancellationToken">Cancellation token.</param>
    /// <returns>Result containing the local path where the skill was cloned/updated.</returns>
    Task<Result<string>> CloneOrUpdateAsync(
        string repoUrl,
        string skillName,
        string? gitRef = null,
        string? subPath = null,
        CancellationToken cancellationToken = default);

    /// <summary>
    /// Checks if an update is available for an installed skill.
    /// Compares the local HEAD with the remote ref without performing a full fetch.
    /// </summary>
    /// <param name="entry">The skill manifest entry to check for updates.</param>
    /// <param name="cancellationToken">Cancellation token.</param>
    /// <returns>Result containing update availability status and the remote ref SHA if available.</returns>
    Task<Result<UpdateCheckResult>> CheckForUpdateAsync(
        SkillManifestEntry entry,
        CancellationToken cancellationToken = default);
}

/// <summary>
/// Result of checking for skill updates.
/// </summary>
/// <param name="UpdateAvailable">True if a newer version is available on the remote.</param>
/// <param name="RemoteRef">The remote ref SHA if available, otherwise null.</param>
/// <param name="LocalRef">The current local ref SHA if available, otherwise null.</param>
public sealed record UpdateCheckResult(
    bool UpdateAvailable,
    string? RemoteRef,
    string? LocalRef);
