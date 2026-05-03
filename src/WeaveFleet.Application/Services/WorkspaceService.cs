using System.Text.RegularExpressions;
using Microsoft.Extensions.Logging;
using WeaveFleet.Application.Configuration;
using WeaveFleet.Application.SessionSources;
using WeaveFleet.Domain.Common;
using WeaveFleet.Domain.Entities;
using WeaveFleet.Domain.Repositories;

namespace WeaveFleet.Application.Services;

/// <summary>
/// Manages workspace lifecycle — creation with isolation strategies, cleanup, and metadata updates.
/// Mirrors the TypeScript workspace-manager.ts logic.
/// </summary>
public sealed partial class WorkspaceService(
    IWorkspaceRepository workspaceRepository,
    IUserContext userContext,
    FleetOptions options,
    ILogger<WorkspaceService> logger)
{
    /// <summary>
    /// Creates a new workspace, applying the specified isolation strategy.
    /// In cloud mode, the strategy is overridden to "managed" and the directory is computed
    /// under <see cref="CloudOptions.WorkspaceRoot"/>.
    /// </summary>
    public async Task<Result<Workspace>> CreateWorkspaceAsync(
        string sourceDirectory,
        string strategy = "existing",
        string? branch = null)
        => await CreateWorkspaceAsync(sourceDirectory, strategy, branch, provenance: null);

    public async Task<Result<Workspace>> CreateWorkspaceAsync(
        string sourceDirectory,
        string strategy,
        string? branch,
        ProvenanceRecord? provenance)
    {
        string workingDirectory;

        // Cloud mode: override strategy to "managed" and derive path under WorkspaceRoot
        if (options.Cloud.Enabled)
        {
            var managedResult = CreateManagedWorkspacePath();
            if (managedResult.IsFailure)
                return managedResult.Error;

            workingDirectory = managedResult.Value;
            strategy = "managed";

            try
            {
                System.IO.Directory.CreateDirectory(workingDirectory);
            }
            catch (Exception ex)
            {
                LogWorkspaceCreateFailed(ex, strategy, workingDirectory);
                return FleetError.Unexpected;
            }
        }
        else
        {
            try
            {
                workingDirectory = strategy switch
                {
                    "worktree" => await CreateWorktreeAsync(sourceDirectory, branch),
                    "clone" => await CreateCloneAsync(sourceDirectory, branch),
                    _ => sourceDirectory
                };
            }
            catch (Exception ex)
            {
                LogWorkspaceCreateFailed(ex, strategy, sourceDirectory);
                return FleetError.Unexpected;
            }
        }

        var workspace = new Workspace
        {
            Id = Guid.NewGuid().ToString(),
            Directory = workingDirectory,
            SourceDirectory = strategy == "existing" ? null : sourceDirectory,
            IsolationStrategy = strategy,
            Branch = branch,
            CreatedAt = DateTime.UtcNow.ToString("O"),
            SourceProviderId = provenance?.ProviderId,
            SourceType = provenance?.SourceType,
            SourceResourceId = provenance?.ResourceId,
            SourceResourceUrl = provenance?.ResourceUrl,
            SourceTitle = provenance?.Title,
            SourceSummary = provenance?.Summary,
            SourceResolvedAt = provenance?.ResolvedAt,
            UserId = userContext.UserId
        };

        await workspaceRepository.InsertAsync(workspace);
        return workspace;
    }

    /// <summary>
    /// Derives a fully-qualified managed workspace path under the configured <see cref="CloudOptions.WorkspaceRoot"/>.
    /// Path: <c>{WorkspaceRoot}/{userStorageKey}/{workspaceId}</c>
    /// The <c>userStorageKey</c> is a filesystem-safe representation of the user ID.
    /// The path is canonicalized and verified to be under the workspace root.
    /// </summary>
    private Result<string> CreateManagedWorkspacePath()
    {
        var workspaceRoot = options.Cloud.WorkspaceRoot;
        if (string.IsNullOrWhiteSpace(workspaceRoot))
            return FleetError.ValidationError("Cloud.WorkspaceRoot", "Cloud.WorkspaceRoot must be configured when Cloud.Enabled is true.");

        var userStorageKey = ToPathSafeKey(userContext.UserId);
        var workspaceId = Guid.NewGuid().ToString("N");
        var candidatePath = Path.Combine(workspaceRoot, userStorageKey, workspaceId);
        var canonicalPath = Path.GetFullPath(candidatePath);
        var canonicalRoot = Path.GetFullPath(workspaceRoot);

        // Guard: ensure the resolved path stays under the workspace root
        if (!canonicalPath.StartsWith(canonicalRoot + Path.DirectorySeparatorChar, StringComparison.Ordinal)
            && !canonicalPath.StartsWith(canonicalRoot + Path.AltDirectorySeparatorChar, StringComparison.Ordinal)
            && canonicalPath != canonicalRoot)
        {
            return FleetError.ValidationError("WorkspacePath",
                "Resolved workspace path escapes the configured workspace root.");
        }

        return canonicalPath;
    }

    /// <summary>
    /// Converts a user ID to a filesystem-safe storage key.
    /// Strips unsafe characters; uses first 64 chars to bound length.
    /// Example: "user_abc|org:123" → "user_abcorg123"
    /// </summary>
    private static string ToPathSafeKey(string userId)
    {
        // Allow alphanumeric, hyphen, underscore only
        var safe = PathUnsafeChars().Replace(userId, "");
        if (string.IsNullOrEmpty(safe))
            safe = "user";
        return safe.Length > 64 ? safe[..64] : safe;
    }

    [GeneratedRegex(@"[^a-zA-Z0-9_\-]")]
    private static partial Regex PathUnsafeChars();

    public async Task<Result<Unit>> CleanupWorkspaceAsync(string id)
    {
        var workspace = await workspaceRepository.GetByIdAsync(id);
        if (workspace is null)
            return FleetError.NotFoundFor(nameof(Workspace), id);

        if (workspace.IsolationStrategy == "worktree" && workspace.Directory is not null)
        {
            try
            {
                await RunGitAsync(workspace.SourceDirectory ?? workspace.Directory,
                    "worktree", "remove", "--force", workspace.Directory);
            }
            catch (Exception ex)
            {
                // Fall back to manual directory removal if git worktree remove fails
                LogCloneDeleteFailed(ex, workspace.Directory);
                try { System.IO.Directory.Delete(workspace.Directory, recursive: true); }
                catch { /* best effort */ }
            }

            // Remove the -worktrees parent folder if it is now empty
            var worktreesRoot = Path.GetDirectoryName(workspace.Directory);
            if (worktreesRoot is not null
                && worktreesRoot.EndsWith("-worktrees", StringComparison.Ordinal)
                && System.IO.Directory.Exists(worktreesRoot)
                && System.IO.Directory.GetFileSystemEntries(worktreesRoot).Length == 0)
            {
                try { System.IO.Directory.Delete(worktreesRoot); }
                catch { /* best effort */ }
            }
        }
        else if (workspace.IsolationStrategy is "clone" or "managed" && workspace.Directory is not null)
        {
            try
            {
                System.IO.Directory.Delete(workspace.Directory, recursive: true);
            }
            catch (Exception ex)
            {
                LogCloneDeleteFailed(ex, workspace.Directory);
            }
        }

        await workspaceRepository.MarkCleanedAsync(id);
        return Unit.Value;
    }

    public async Task<Result<string>> GetWorkspaceDirectoryAsync(string id)
    {
        var workspace = await workspaceRepository.GetByIdAsync(id);
        if (workspace is null)
            return FleetError.NotFoundFor(nameof(Workspace), id);
        return workspace.Directory;
    }

    public async Task<Result<Workspace>> GetWorkspaceAsync(string id)
    {
        var workspace = await workspaceRepository.GetByIdAsync(id);
        if (workspace is null)
            return FleetError.NotFoundFor(nameof(Workspace), id);
        return workspace;
    }

    public async Task<Result<IReadOnlyList<Workspace>>> ListWorkspacesAsync()
    {
        var workspaces = await workspaceRepository.ListAsync();
        return Result.Success(workspaces);
    }

    public async Task<Result<Unit>> UpdateDisplayNameAsync(string id, string displayName)
    {
        var workspace = await workspaceRepository.GetByIdAsync(id);
        if (workspace is null)
            return FleetError.NotFoundFor(nameof(Workspace), id);

        await workspaceRepository.UpdateDisplayNameAsync(id, displayName);
        return Unit.Value;
    }

    private static async Task<string> CreateWorktreeAsync(string sourceDir, string? branch)
    {
        var branchName = branch ?? $"weave-session-{Guid.NewGuid().ToString("N")[..8]}";

        // Place worktree under a dedicated sibling folder to avoid polluting the parent.
        // Naming: {repo-name}-worktrees/{hyphenated-branch-name}
        // e.g. source "C:\repos\my-project" + branch "feature/auth"
        //   → "C:\repos\my-project-worktrees\feature-auth"
        var repoName = Path.GetFileName(sourceDir);
        var hyphenatedBranch = branchName.Replace('/', '-').Replace('\\', '-');
        var parentDir = Path.GetFullPath(Path.GetDirectoryName(sourceDir) ?? sourceDir);
        var worktreesRoot = Path.Combine(parentDir, $"{repoName}-worktrees");
        var worktreeDir = Path.Combine(worktreesRoot, hyphenatedBranch);

        // Guard against path traversal
        var resolvedWorktreesRoot = Path.GetFullPath(worktreesRoot);
        var resolvedWorktreeDir = Path.GetFullPath(worktreeDir);
        if (!resolvedWorktreesRoot.StartsWith(parentDir + Path.DirectorySeparatorChar, StringComparison.Ordinal))
            throw new InvalidOperationException($"Worktree root escapes parent directory: {resolvedWorktreesRoot}");
        if (!resolvedWorktreeDir.StartsWith(resolvedWorktreesRoot + Path.DirectorySeparatorChar, StringComparison.Ordinal))
            throw new InvalidOperationException($"Invalid branch name results in path outside worktree root: {branchName}");

        Directory.CreateDirectory(worktreesRoot);

        // Check if the branch already exists; if so, check it out rather than creating.
        var branchExists = false;
        try
        {
            await RunGitAsync(sourceDir, "rev-parse", "--verify", branchName);
            branchExists = true;
        }
        catch
        {
            // Branch doesn't exist locally — will be created
        }

        if (branchExists)
            await RunGitAsync(sourceDir, "worktree", "add", worktreeDir, branchName);
        else
            await RunGitAsync(sourceDir, "worktree", "add", worktreeDir, "-b", branchName);

        return worktreeDir;
    }

    private static async Task<string> CreateCloneAsync(string sourceDir, string? branch)
    {
        var cloneDir = Path.Combine(
            Path.GetDirectoryName(sourceDir) ?? sourceDir,
            $".fleet-clone-{Guid.NewGuid().ToString("N")[..8]}");

        var args = branch is not null
            ? new[] { "clone", "--branch", branch, sourceDir, cloneDir }
            : new[] { "clone", sourceDir, cloneDir };

        await RunGitAsync(sourceDir, args);
        return cloneDir;
    }

    private static async Task RunGitAsync(string workingDir, params string[] args)
    {
        using var process = new System.Diagnostics.Process();
        process.StartInfo = new System.Diagnostics.ProcessStartInfo
        {
            FileName = "git",
            WorkingDirectory = workingDir,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            UseShellExecute = false
        };
        foreach (var arg in args)
            process.StartInfo.ArgumentList.Add(arg);

        process.Start();
        var stderr = await process.StandardError.ReadToEndAsync();
        await process.WaitForExitAsync();

        if (process.ExitCode != 0)
            throw new InvalidOperationException($"git {string.Join(' ', args)} failed: {stderr}");
    }

    [LoggerMessage(Level = LogLevel.Error, Message = "Failed to create workspace with strategy {Strategy} for {Dir}")]
    private partial void LogWorkspaceCreateFailed(Exception ex, string strategy, string dir);

    [LoggerMessage(Level = LogLevel.Warning, Message = "Failed to delete clone/managed directory: {Dir}")]
    private partial void LogCloneDeleteFailed(Exception ex, string dir);
}
