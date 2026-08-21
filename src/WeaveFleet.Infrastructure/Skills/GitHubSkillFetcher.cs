using System.Diagnostics;
using System.Text;
using Microsoft.Extensions.Logging;
using WeaveFleet.Application.Skills;
using WeaveFleet.Domain.Common;
using WeaveFleet.Domain.Skills;

namespace WeaveFleet.Infrastructure.Skills;

/// <summary>
/// GitHub skill fetcher using git CLI for cloning and updating skills.
/// Supports public repositories only (v1).
/// </summary>
public sealed partial class GitHubSkillFetcher(ILogger<GitHubSkillFetcher> logger) : IGitHubSkillFetcher
{
    private static readonly string _skillsBasePath = Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.UserProfile),
        ".weave",
        "skills");

    /// <inheritdoc />
    public async Task<Result<string>> CloneOrUpdateAsync(
        string repoUrl,
        string skillName,
        string? gitRef = null,
        string? subPath = null,
        CancellationToken cancellationToken = default)
    {
        if (string.IsNullOrWhiteSpace(repoUrl))
        {
            return FleetError.ValidationError("RepoUrl", "Repository URL cannot be empty.");
        }

        if (string.IsNullOrWhiteSpace(skillName))
        {
            return FleetError.ValidationError("SkillName", "Skill name cannot be empty.");
        }

        // Ensure base directory exists
        Directory.CreateDirectory(_skillsBasePath);

        var localPath = Path.Combine(_skillsBasePath, skillName);

        try
        {
            if (Directory.Exists(localPath) && Directory.Exists(Path.Combine(localPath, ".git")))
            {
                // When subPath is specified, we cannot do an in-place update because the
                // existing directory contains a full git repo structure. The update path
                // would leave the full repo structure with subPath nested inside.
                // Instead, delete and re-clone to extract only the subPath contents.
                if (!string.IsNullOrWhiteSpace(subPath))
                {
                    ForceDeleteDirectory(localPath);
                }
                else
                {
                    // Repository exists, update it
                    LogUpdatingRepository(localPath);
                    var updateResult = await UpdateRepositoryAsync(localPath, gitRef, subPath, cancellationToken).ConfigureAwait(false);
                    if (!updateResult.IsSuccess)
                    {
                        return updateResult.Error;
                    }

                    return Result.Success(localPath);
                }
            }

            // Clone new repository (or re-clone after deleting for subPath case)
            LogCloningRepository(repoUrl, localPath);
            var cloneResult = await CloneRepositoryAsync(repoUrl, localPath, gitRef, subPath, cancellationToken).ConfigureAwait(false);
            if (!cloneResult.IsSuccess)
            {
                return cloneResult.Error;
            }

            return Result.Success(localPath);
        }
        catch (Exception ex)
        {
            LogCloneOrUpdateFailed(ex, repoUrl);
            return new FleetError("GitHubSkillFetcher.CloneOrUpdate", $"Failed to clone or update repository: {ex.Message}");
        }
    }

    /// <inheritdoc />
    public async Task<Result<UpdateCheckResult>> CheckForUpdateAsync(
        SkillManifestEntry entry,
        CancellationToken cancellationToken = default)
    {
        if (entry.Source != SkillSource.GitHub)
        {
            return FleetError.ValidationError("SkillSource", "Skill is not from GitHub.");
        }

        if (string.IsNullOrWhiteSpace(entry.RepoUrl))
        {
            return FleetError.ValidationError("RepoUrl", "Skill manifest entry is missing repository URL.");
        }

        // Derive local path from skill name when not explicitly set (e.g. older manifest entries)
        var localPath = entry.LocalPath;
        if (string.IsNullOrWhiteSpace(localPath))
        {
            localPath = Path.Combine(_skillsBasePath, entry.Name);
        }

        if (!Directory.Exists(localPath))
        {
            return FleetError.ValidationError("LocalPath", "Skill is not installed locally.");
        }

        // For subPath skills, the main directory has no .git — use the sparse-temp clone if available
        var gitPath = localPath;
        if (!Directory.Exists(Path.Combine(localPath, ".git")))
        {
            var sparseTemp = localPath + ".sparse-temp";
            var sparseTempGit = Path.Combine(sparseTemp, ".git");

            // Verify the sparse-temp is a functional git repo (has HEAD file)
            if (Directory.Exists(sparseTempGit) && File.Exists(Path.Combine(sparseTempGit, "HEAD")))
            {
                gitPath = sparseTemp;
            }
            else
            {
                // No usable git history available locally; do a remote-only check
                var remoteRefResult = await GetRemoteRefShaAsync(
                    entry.RepoUrl,
                    entry.Ref ?? "HEAD",
                    cancellationToken).ConfigureAwait(false);

                if (!remoteRefResult.IsSuccess)
                {
                    LogRemoteCheckFailed(entry.Name, remoteRefResult.Error);
                    return Result.Success(new UpdateCheckResult(
                        UpdateAvailable: false,
                        RemoteRef: null,
                        LocalRef: null));
                }

                // Without local SHA we can't compare — report as potentially outdated
                return Result.Success(new UpdateCheckResult(
                    UpdateAvailable: true,
                    RemoteRef: remoteRefResult.Value,
                    LocalRef: null));
            }
        }

        try
        {
            // Get local HEAD SHA
            var localRefResult = await GetLocalHeadShaAsync(gitPath, cancellationToken).ConfigureAwait(false);
            if (!localRefResult.IsSuccess)
            {
                return localRefResult.Error;
            }

            var localRef = localRefResult.Value;

            // Get remote ref SHA (without full fetch)
            var remoteRefResult = await GetRemoteRefShaAsync(
                entry.RepoUrl,
                entry.Ref ?? "HEAD",
                cancellationToken).ConfigureAwait(false);

            if (!remoteRefResult.IsSuccess)
            {
                // If remote check fails (e.g., network issue, auth failure), return gracefully
                LogRemoteCheckFailed(entry.Name, remoteRefResult.Error);
                return Result.Success(new UpdateCheckResult(
                    UpdateAvailable: false,
                    RemoteRef: null,
                    LocalRef: localRef));
            }

            var remoteRef = remoteRefResult.Value;
            var updateAvailable = !string.Equals(localRef, remoteRef, StringComparison.OrdinalIgnoreCase);

            return Result.Success(new UpdateCheckResult(
                UpdateAvailable: updateAvailable,
                RemoteRef: remoteRef,
                LocalRef: localRef));
        }
        catch (Exception ex)
        {
            LogCheckForUpdateFailed(ex, entry.Name);
            return new FleetError("GitHubSkillFetcher.CheckForUpdate", $"Failed to check for updates: {ex.Message}");
        }
    }

    // ── Private Helpers ────────────────────────────────────────────────────────

    private async Task<Result<Unit>> CloneRepositoryAsync(
        string repoUrl,
        string localPath,
        string? gitRef,
        string? subPath,
        CancellationToken cancellationToken)
    {
        if (string.IsNullOrWhiteSpace(subPath))
        {
            // Standard shallow clone for full repository
            var args = new List<string> { "clone", "--depth", "1" };

            if (!string.IsNullOrWhiteSpace(gitRef))
            {
                args.Add("--branch");
                args.Add(gitRef);
            }

            args.Add(repoUrl);
            args.Add(localPath);

            var result = await RunGitCommandAsync(
                args.ToArray(),
                workingDirectory: null,
                cancellationToken).ConfigureAwait(false);

            return result.IsSuccess
                ? Result.Success(Unit.Value)
                : new FleetError("GitHubSkillFetcher.Clone", $"Git clone failed: {result.Error}");
        }

        // Sparse checkout for subdirectory
        var tempClonePath = localPath + ".sparse-temp";
        try
        {
            // Clean up if exists from a previous failed attempt
            if (Directory.Exists(tempClonePath))
            {
                ForceDeleteDirectory(tempClonePath);
            }

            // Clone with no checkout
            var cloneArgs = new List<string> { "clone", "--depth", "1", "--no-checkout", "--filter=blob:none" };
            if (!string.IsNullOrWhiteSpace(gitRef))
            {
                cloneArgs.Add("--branch");
                cloneArgs.Add(gitRef);
            }
            cloneArgs.Add(repoUrl);
            cloneArgs.Add(tempClonePath);

            var cloneResult = await RunGitCommandAsync(
                cloneArgs.ToArray(),
                workingDirectory: null,
                cancellationToken).ConfigureAwait(false);

            if (!cloneResult.IsSuccess)
            {
                return new FleetError("GitHubSkillFetcher.Clone", $"Git clone failed: {cloneResult.Error}");
            }

            // Set sparse checkout
            var sparseInitResult = await RunGitCommandAsync(
                ["sparse-checkout", "set", subPath],
                tempClonePath,
                cancellationToken).ConfigureAwait(false);

            if (!sparseInitResult.IsSuccess)
            {
                return new FleetError("GitHubSkillFetcher.SparseCheckout", $"Sparse checkout setup failed: {sparseInitResult.Error}");
            }

            // Checkout
            var checkoutResult = await RunGitCommandAsync(
                ["checkout"],
                tempClonePath,
                cancellationToken).ConfigureAwait(false);

            if (!checkoutResult.IsSuccess)
            {
                return new FleetError("GitHubSkillFetcher.Checkout", $"Checkout failed: {checkoutResult.Error}");
            }

            // Move subdirectory contents to the target path
            var subDirPath = Path.Combine(tempClonePath, subPath.Replace('/', Path.DirectorySeparatorChar));
            if (!Directory.Exists(subDirPath))
            {
                return new FleetError("GitHubSkillFetcher.SubPath", $"Subdirectory '{subPath}' not found in repository.");
            }

            // Ensure target directory exists and move contents
            if (Directory.Exists(localPath))
            {
                ForceDeleteDirectory(localPath);
            }

            Directory.Move(subDirPath, localPath);

            return Result.Success(Unit.Value);
        }
        finally
        {
            // Clean up temp clone
            if (Directory.Exists(tempClonePath))
            {
                try
                {
                    Directory.Delete(tempClonePath, recursive: true);
                }
                catch
                {
                    // Best effort cleanup
                }
            }
        }
    }

    private async Task<Result<Unit>> UpdateRepositoryAsync(
        string localPath,
        string? gitRef,
        string? subPath,
        CancellationToken cancellationToken)
    {
        // For sparse checkouts, we need to ensure sparse-checkout is still configured
        if (!string.IsNullOrWhiteSpace(subPath))
        {
            var sparseConfigResult = await RunGitCommandAsync(
                ["sparse-checkout", "set", subPath],
                localPath,
                cancellationToken).ConfigureAwait(false);

            if (!sparseConfigResult.IsSuccess)
            {
                return new FleetError("GitHubSkillFetcher.SparseCheckout", $"Sparse checkout reconfiguration failed: {sparseConfigResult.Error}");
            }
        }

        // Fetch latest changes
        var fetchResult = await RunGitCommandAsync(
            ["fetch", "origin"],
            localPath,
            cancellationToken).ConfigureAwait(false);

        if (!fetchResult.IsSuccess)
        {
            return new FleetError("GitHubSkillFetcher.Fetch", $"Git fetch failed: {fetchResult.Error}");
        }

        // Determine target ref
        var targetRef = string.IsNullOrWhiteSpace(gitRef) ? "origin/HEAD" : $"origin/{gitRef}";

        // Reset to target ref
        var resetResult = await RunGitCommandAsync(
            ["reset", "--hard", targetRef],
            localPath,
            cancellationToken).ConfigureAwait(false);

        return resetResult.IsSuccess
            ? Result.Success(Unit.Value)
            : new FleetError("GitHubSkillFetcher.Reset", $"Git reset failed: {resetResult.Error}");
    }

    private async Task<Result<string>> GetLocalHeadShaAsync(
        string localPath,
        CancellationToken cancellationToken)
    {
        var result = await RunGitCommandAsync(
            ["rev-parse", "HEAD"],
            localPath,
            cancellationToken).ConfigureAwait(false);

        return result.IsSuccess
            ? Result.Success(result.Value!.Trim())
            : new FleetError("GitHubSkillFetcher.GetLocalHead", $"Failed to get local HEAD: {result.Error}");
    }

    private async Task<Result<string>> GetRemoteRefShaAsync(
        string repoUrl,
        string gitRef,
        CancellationToken cancellationToken)
    {
        // Use ls-remote to get remote ref without cloning/fetching
        var result = await RunGitCommandAsync(
            ["ls-remote", repoUrl, gitRef],
            workingDirectory: null,
            cancellationToken).ConfigureAwait(false);

        if (!result.IsSuccess)
        {
            return new FleetError("GitHubSkillFetcher.GetRemoteRef", $"Failed to query remote ref: {result.Error}");
        }

        var output = result.Value!.Trim();
        if (string.IsNullOrWhiteSpace(output))
        {
            return new FleetError("GitHubSkillFetcher.GetRemoteRef", $"Remote ref '{gitRef}' not found.");
        }

        // Output format: "<sha>\t<ref>"
        var parts = output.Split('\t', '\n');
        if (parts.Length == 0 || string.IsNullOrWhiteSpace(parts[0]))
        {
            return new FleetError("GitHubSkillFetcher.GetRemoteRef", "Failed to parse remote ref SHA.");
        }

        return Result.Success(parts[0].Trim());
    }

    private async Task<Result<string>> RunGitCommandAsync(
        string[] args,
        string? workingDirectory,
        CancellationToken cancellationToken)
    {
        var startInfo = new ProcessStartInfo
        {
            FileName = "git",
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            RedirectStandardInput = true,
            UseShellExecute = false,
            CreateNoWindow = true,
        };

        // Prevent git from opening an interactive terminal prompt for credentials.
        // This ensures the process fails fast with an auth error instead of hanging.
        startInfo.Environment["GIT_TERMINAL_PROMPT"] = "0";
        startInfo.Environment["GCM_INTERACTIVE"] = "never";

        foreach (var arg in args)
        {
            startInfo.ArgumentList.Add(arg);
        }

        if (!string.IsNullOrWhiteSpace(workingDirectory))
        {
            startInfo.WorkingDirectory = workingDirectory;
        }

        var outputBuilder = new StringBuilder();
        var errorBuilder = new StringBuilder();

        try
        {
            using var process = new Process { StartInfo = startInfo };

            process.OutputDataReceived += (_, e) =>
            {
                if (e.Data is not null)
                {
                    outputBuilder.AppendLine(e.Data);
                }
            };

            process.ErrorDataReceived += (_, e) =>
            {
                if (e.Data is not null)
                {
                    errorBuilder.AppendLine(e.Data);
                }
            };

            process.Start();
            process.BeginOutputReadLine();
            process.BeginErrorReadLine();

            await process.WaitForExitAsync(cancellationToken).ConfigureAwait(false);

            var output = outputBuilder.ToString();
            var error = errorBuilder.ToString();

            if (process.ExitCode != 0)
            {
                var errorMessage = !string.IsNullOrWhiteSpace(error) ? error : output;
                LogGitCommandFailed(process.ExitCode, errorMessage);

                // Check for common auth failures
                if (errorMessage.Contains("Authentication failed", StringComparison.OrdinalIgnoreCase) ||
                    errorMessage.Contains("could not read Username", StringComparison.OrdinalIgnoreCase) ||
                    errorMessage.Contains("terminal prompts disabled", StringComparison.OrdinalIgnoreCase))
                {
                    return new FleetError("GitHubSkillFetcher.Auth",
                        "Authentication required. Ensure the GitHub CLI is authenticated (run 'gh auth login') " +
                        "or configure a git credential helper for this repository.");
                }

                return new FleetError("GitHubSkillFetcher.Git", errorMessage.Trim());
            }

            return Result.Success(output);
        }
        catch (Exception ex)
        {
            LogGitExecutionFailed(ex, string.Join(" ", args));
            return new FleetError("GitHubSkillFetcher.Git", $"Failed to execute git: {ex.Message}");
        }
    }

    // ── Logging ────────────────────────────────────────────────────────────────

    /// <summary>
    /// Recursively deletes a directory after clearing read-only attributes on all files.
    /// Git repositories contain read-only pack/object files that <see cref="Directory.Delete"/>
    /// cannot remove on Windows without first clearing the attribute.
    /// </summary>
    private static void ForceDeleteDirectory(string path)
    {
        var directory = new DirectoryInfo(path);
        if (!directory.Exists)
        {
            return;
        }

        foreach (var file in directory.GetFiles("*", SearchOption.AllDirectories))
        {
            file.Attributes = FileAttributes.Normal;
        }

        directory.Delete(recursive: true);
    }

    // ── Log Messages ──────────────────────────────────────────────────────────

    [LoggerMessage(Level = LogLevel.Information, Message = "Updating existing skill repository at {LocalPath}")]
    private partial void LogUpdatingRepository(string localPath);

    [LoggerMessage(Level = LogLevel.Information, Message = "Cloning skill repository from {RepoUrl} to {LocalPath}")]
    private partial void LogCloningRepository(string repoUrl, string localPath);

    [LoggerMessage(Level = LogLevel.Error, Message = "Failed to clone or update skill repository {RepoUrl}")]
    private partial void LogCloneOrUpdateFailed(Exception ex, string repoUrl);

    [LoggerMessage(Level = LogLevel.Warning, Message = "Failed to check remote ref for {SkillName}: {Error}")]
    private partial void LogRemoteCheckFailed(string skillName, FleetError error);

    [LoggerMessage(Level = LogLevel.Error, Message = "Failed to check for updates for skill {SkillName}")]
    private partial void LogCheckForUpdateFailed(Exception ex, string skillName);

    [LoggerMessage(Level = LogLevel.Warning, Message = "Git command failed with exit code {ExitCode}: {Error}")]
    private partial void LogGitCommandFailed(int exitCode, string error);

    [LoggerMessage(Level = LogLevel.Error, Message = "Failed to execute git command: {Command}")]
    private partial void LogGitExecutionFailed(Exception ex, string command);
}
