using WeaveFleet.Domain.Common;

namespace WeaveFleet.Application.Tools;

/// <summary>
/// Service for installing tools to the local filesystem or configuration.
/// Replaces both IGitHubSkillFetcher and ISkillSyncEngine since tool installation
/// is simpler (copy file or write config).
/// </summary>
public interface IToolInstaller
{
    /// <summary>
    /// Installs a native tool by copying it from a source path to the target location.
    /// The scope (user or workspace) is determined by the workspaceId parameter.
    /// </summary>
    /// <param name="name">The unique name of the tool.</param>
    /// <param name="sourcePath">The source path to copy the tool from (file or directory).</param>
    /// <param name="userId">The user ID owning the installation.</param>
    /// <param name="workspaceId">Optional workspace ID for workspace-scoped installations.</param>
    /// <param name="cancellationToken">Cancellation token.</param>
    /// <returns>Result containing the installation path where the tool was installed.</returns>
    Task<Result<string>> InstallNativeAsync(
        string name,
        string sourcePath,
        string userId,
        string? workspaceId = null,
        CancellationToken cancellationToken = default);

    /// <summary>
    /// Installs an MCP tool by writing its configuration to the appropriate config file.
    /// The scope (user or workspace) is determined by the workspaceId parameter.
    /// </summary>
    /// <param name="name">The unique name of the tool.</param>
    /// <param name="command">The command to execute for the MCP tool.</param>
    /// <param name="args">Optional command-line arguments.</param>
    /// <param name="env">Optional environment variables.</param>
    /// <param name="userId">The user ID owning the installation.</param>
    /// <param name="workspaceId">Optional workspace ID for workspace-scoped installations.</param>
    /// <param name="cancellationToken">Cancellation token.</param>
    /// <returns>Result containing the configuration path where the tool was registered.</returns>
    Task<Result<string>> InstallMcpAsync(
        string name,
        string command,
        IReadOnlyList<string>? args,
        IReadOnlyDictionary<string, string>? env,
        string userId,
        string? workspaceId = null,
        CancellationToken cancellationToken = default);
}
