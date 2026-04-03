using Microsoft.Extensions.Logging;
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
    ILogger<WorkspaceService> logger)
{
    /// <summary>
    /// Creates a new workspace, applying the specified isolation strategy.
    /// </summary>
    public async Task<Result<Workspace>> CreateWorkspaceAsync(
        string sourceDirectory,
        string strategy = "existing",
        string? branch = null)
    {
        string workingDirectory;

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

        var workspace = new Workspace
        {
            Id = Guid.NewGuid().ToString(),
            Directory = workingDirectory,
            SourceDirectory = strategy == "existing" ? null : sourceDirectory,
            IsolationStrategy = strategy,
            Branch = branch,
            CreatedAt = DateTime.UtcNow.ToString("O")
        };

        await workspaceRepository.InsertAsync(workspace);
        return workspace;
    }

    public async Task<Result<Unit>> CleanupWorkspaceAsync(string id)
    {
        var workspace = await workspaceRepository.GetByIdAsync(id);
        if (workspace is null)
            return FleetError.NotFoundFor(nameof(Workspace), id);

        if (workspace.IsolationStrategy == "worktree" && workspace.Directory is not null)
        {
            await RunGitAsync(workspace.SourceDirectory ?? workspace.Directory,
                "worktree", "remove", "--force", workspace.Directory);
        }
        else if (workspace.IsolationStrategy == "clone" && workspace.Directory is not null)
        {
            try
            {
                Directory.Delete(workspace.Directory, recursive: true);
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
        var branchName = branch ?? $"fleet-{Guid.NewGuid().ToString("N")[..8]}";
        var worktreeDir = Path.Combine(
            Path.GetDirectoryName(sourceDir) ?? sourceDir,
            $".fleet-worktree-{Guid.NewGuid().ToString("N")[..8]}");

        await RunGitAsync(sourceDir, "worktree", "add", "-b", branchName, worktreeDir);
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

    [LoggerMessage(Level = LogLevel.Warning, Message = "Failed to delete clone directory: {Dir}")]
    private partial void LogCloneDeleteFailed(Exception ex, string dir);
}
