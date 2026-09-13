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
                switch (strategy)
                {
                    case "worktree":
                        (workingDirectory, branch) = await CreateWorktreeAsync(sourceDirectory, branch);
                        break;
                    case "clone":
                        workingDirectory = await CreateCloneAsync(sourceDirectory, branch);
                        break;
                    default:
                        workingDirectory = sourceDirectory;
                        break;
                }
            }
            catch (GitCommandException ex)
            {
                LogWorkspaceCreateFailed(ex, strategy, sourceDirectory);
                return FleetError.ValidationError("Workspace", $"Couldn't create the {strategy}: {ex.GitMessage}");
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

    private static readonly TimeSpan _baseFetchTimeout = TimeSpan.FromSeconds(20);

    /// <summary>
    /// Creates a worktree next to the repository and returns its directory and branch.
    /// A requested branch that exists and isn't checked out anywhere is checked out as is.
    /// Otherwise a new branch starts from the repository's default branch, never from
    /// whatever the main checkout happens to have checked out. When the branch is checked
    /// out elsewhere, or the folder is taken, a numeric suffix is added.
    /// </summary>
    private async Task<(string Directory, string Branch)> CreateWorktreeAsync(string sourceDir, string? branch)
    {
        var requestedBranch = branch ?? $"weave-session-{Guid.NewGuid().ToString("N")[..8]}";

        // Place worktree under a dedicated sibling folder to avoid polluting the parent.
        // Naming: {repo-name}-worktrees/{hyphenated-branch-name}
        // e.g. source "C:\repos\my-project" + branch "feature/auth"
        //   → "C:\repos\my-project-worktrees\feature-auth"
        var repoName = Path.GetFileName(sourceDir);
        var parentDir = Path.GetFullPath(Path.GetDirectoryName(sourceDir) ?? sourceDir);
        var worktreesRoot = Path.GetFullPath(Path.Combine(parentDir, $"{repoName}-worktrees"));
        if (!worktreesRoot.StartsWith(parentDir + Path.DirectorySeparatorChar, StringComparison.Ordinal))
            throw new InvalidOperationException($"Worktree root escapes parent directory: {worktreesRoot}");

        // A worktree folder deleted without `git worktree remove` keeps its branch "checked out"
        // until pruned, which would make `git worktree add` refuse it.
        await TryRunGitAsync(sourceDir, "worktree", "prune");

        var checkedOutBranches = await GetCheckedOutBranchesAsync(sourceDir);
        var reuseExistingBranch = !checkedOutBranches.Contains(requestedBranch)
            && await RefExistsAsync(sourceDir, $"refs/heads/{requestedBranch}");

        var branchName = requestedBranch;
        if (!reuseExistingBranch)
        {
            for (var n = 2; checkedOutBranches.Contains(branchName) || await RefExistsAsync(sourceDir, $"refs/heads/{branchName}"); n++)
                branchName = $"{requestedBranch}-{n}";
        }

        var worktreeDir = ResolveFreeWorktreeDirectory(worktreesRoot, branchName);
        Directory.CreateDirectory(worktreesRoot);

        if (reuseExistingBranch)
        {
            await RunGitAsync(sourceDir, "worktree", "add", worktreeDir, branchName);
        }
        else
        {
            var baseRef = await ResolveBaseRefAsync(sourceDir);
            // --no-track: origin/main is where the branch starts, not where it gets pushed.
            string[] args = baseRef is null
                ? ["worktree", "add", "-b", branchName, worktreeDir]
                : ["worktree", "add", "--no-track", "-b", branchName, worktreeDir, baseRef];
            await RunGitAsync(sourceDir, args);
        }

        return (worktreeDir, branchName);
    }

    private static string ResolveFreeWorktreeDirectory(string worktreesRoot, string branchName)
    {
        var hyphenatedBranch = branchName.Replace('/', '-').Replace('\\', '-');
        var candidate = Path.GetFullPath(Path.Combine(worktreesRoot, hyphenatedBranch));
        if (!candidate.StartsWith(worktreesRoot + Path.DirectorySeparatorChar, StringComparison.Ordinal))
            throw new InvalidOperationException($"Invalid branch name results in path outside worktree root: {branchName}");

        var directory = candidate;
        for (var n = 2; Directory.Exists(directory) || File.Exists(directory); n++)
            directory = $"{candidate}-{n}";

        return directory;
    }

    /// <summary>
    /// The ref new worktree branches start from: <c>origin/&lt;default&gt;</c>, fetched first
    /// (best effort), when the repository has an origin; otherwise a local <c>main</c> or
    /// <c>master</c>; otherwise null, meaning the current HEAD.
    /// </summary>
    private async Task<string?> ResolveBaseRefAsync(string sourceDir)
    {
        var remotes = await TryRunGitAsync(sourceDir, "remote");
        if (remotes is not null && ParseLines(remotes).Contains("origin"))
        {
            var defaultBranch = await ResolveOriginDefaultBranchAsync(sourceDir);
            if (defaultBranch is not null)
            {
                if (await TryRunGitAsync(sourceDir, _baseFetchTimeout, "fetch", "--quiet", "--no-tags", "origin", defaultBranch) is null)
                    LogBaseFetchFailed(sourceDir, defaultBranch);

                if (await RefExistsAsync(sourceDir, $"refs/remotes/origin/{defaultBranch}"))
                    return $"origin/{defaultBranch}";
            }
        }

        foreach (var candidate in _defaultBranchCandidates)
        {
            if (await RefExistsAsync(sourceDir, $"refs/heads/{candidate}"))
                return candidate;
        }

        return null;
    }

    private static readonly string[] _defaultBranchCandidates = ["main", "master"];

    private static async Task<string?> ResolveOriginDefaultBranchAsync(string sourceDir)
    {
        var originHead = (await TryRunGitAsync(sourceDir, "symbolic-ref", "--quiet", "--short", "refs/remotes/origin/HEAD"))?.Trim();
        if (originHead is not null && originHead.StartsWith("origin/", StringComparison.Ordinal))
            return originHead["origin/".Length..];

        foreach (var candidate in _defaultBranchCandidates)
        {
            if (await RefExistsAsync(sourceDir, $"refs/remotes/origin/{candidate}"))
                return candidate;
        }

        return null;
    }

    private static async Task<HashSet<string>> GetCheckedOutBranchesAsync(string sourceDir)
    {
        const string branchPrefix = "branch refs/heads/";
        var porcelain = await RunGitAsync(sourceDir, "worktree", "list", "--porcelain");
        return ParseLines(porcelain)
            .Where(line => line.StartsWith(branchPrefix, StringComparison.Ordinal))
            .Select(line => line[branchPrefix.Length..])
            .ToHashSet(StringComparer.Ordinal);
    }

    private static async Task<bool> RefExistsAsync(string sourceDir, string refName) =>
        await TryRunGitAsync(sourceDir, "show-ref", "--verify", "--quiet", refName) is not null;

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

    private static Task<string> RunGitAsync(string workingDir, params string[] args) =>
        RunGitAsync(workingDir, timeout: null, args);

    private static async Task<string?> TryRunGitAsync(string workingDir, params string[] args)
    {
        try { return await RunGitAsync(workingDir, timeout: null, args); }
        catch (GitCommandException) { return null; }
    }

    private static async Task<string?> TryRunGitAsync(string workingDir, TimeSpan timeout, params string[] args)
    {
        try { return await RunGitAsync(workingDir, timeout, args); }
        catch (GitCommandException) { return null; }
    }

    private static async Task<string> RunGitAsync(string workingDir, TimeSpan? timeout, string[] args)
    {
        using var process = new System.Diagnostics.Process();
        process.StartInfo = new System.Diagnostics.ProcessStartInfo
        {
            FileName = "git",
            WorkingDirectory = workingDir,
            RedirectStandardInput = true,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            UseShellExecute = false
        };
        foreach (var arg in args)
            process.StartInfo.ArgumentList.Add(arg);

        // Never wait on a credential prompt nobody can see.
        process.StartInfo.Environment["GIT_TERMINAL_PROMPT"] = "0";

        process.Start();
        process.StandardInput.Close();
        var stdoutTask = process.StandardOutput.ReadToEndAsync();
        var stderrTask = process.StandardError.ReadToEndAsync();

        using var timeoutCts = timeout is { } limit ? new CancellationTokenSource(limit) : new CancellationTokenSource();
        try
        {
            await process.WaitForExitAsync(timeoutCts.Token);
        }
        catch (OperationCanceledException)
        {
            try { process.Kill(entireProcessTree: true); }
            catch (InvalidOperationException) { /* already exited */ }
            throw new GitCommandException(string.Join(' ', args), $"timed out after {timeout!.Value.TotalSeconds:0} seconds");
        }

        var stdout = await stdoutTask;
        var stderr = await stderrTask;
        if (process.ExitCode != 0)
            throw new GitCommandException(string.Join(' ', args), CleanGitMessage(stderr));

        return stdout;
    }

    /// <summary>
    /// Turns git's stderr into one readable sentence. Git also writes progress there
    /// ("Preparing worktree …"), so when it marks lines with "fatal:" or "error:", only those count.
    /// </summary>
    private static string CleanGitMessage(string stderr)
    {
        string[] prefixes = ["fatal: ", "error: "];
        var lines = ParseLines(stderr).Where(line => !line.StartsWith("hint:", StringComparison.Ordinal)).ToArray();
        var marked = lines
            .Where(line => prefixes.Any(prefix => line.StartsWith(prefix, StringComparison.Ordinal)))
            .Select(line => line[(line.IndexOf(": ", StringComparison.Ordinal) + 2)..])
            .ToArray();
        var message = marked.Length > 0 ? marked : lines;
        return message.Length > 0 ? string.Join(' ', message) : "git exited with an error";
    }

    private static string[] ParseLines(string output) =>
        output.Split('\n', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);

    /// <summary>A git command that exited non-zero; <see cref="GitMessage"/> is fit to show the user.</summary>
    private sealed class GitCommandException(string command, string gitMessage)
        : Exception($"git {command} failed: {gitMessage}")
    {
        public string GitMessage { get; } = gitMessage;
    }

    [LoggerMessage(Level = LogLevel.Error, Message = "Failed to create workspace with strategy {Strategy} for {Dir}")]
    private partial void LogWorkspaceCreateFailed(Exception ex, string strategy, string dir);

    [LoggerMessage(Level = LogLevel.Warning, Message = "Failed to delete clone/managed directory: {Dir}")]
    private partial void LogCloneDeleteFailed(Exception ex, string dir);

    [LoggerMessage(Level = LogLevel.Warning, Message = "Couldn't fetch origin/{Branch} in {Dir}; starting the worktree from the last fetched copy")]
    private partial void LogBaseFetchFailed(string dir, string branch);
}
