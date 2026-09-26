using System.Text;
using System.Text.RegularExpressions;
using Microsoft.Extensions.Logging;
using WeaveFleet.Application.Configuration;
using WeaveFleet.Domain.Common;

namespace WeaveFleet.Application.Services;

/// <summary>
/// Makes a folder to start a session in, from the new-session Folder menu: an empty folder, a git
/// repository with a first commit, or a clone. A folder outside the workspace roots is added to them,
/// so a session can use it at once.
/// </summary>
/// <param name="gitHubTokens">
/// The GitHub token the person connected in Fleet, used for one clone from github.com so private
/// repositories work. Null (or no token) leaves git to the person's own credentials.
/// </param>
public sealed partial class NewFolderService(
    WorkspaceRootService workspaceRootService,
    IUserContext userContext,
    FleetOptions options,
    ILogger<NewFolderService> logger,
    IGitHubTokenSource? gitHubTokens = null)
{
    /// <summary>The message of the empty first commit, which gives New worktree something to branch from.</summary>
    public const string FirstCommitMessage = "Initial commit";

    private static readonly TimeSpan InitTimeout = TimeSpan.FromSeconds(30);

    // Long enough for a signing prompt (commit.gpgsign) to be answered on this machine.
    private static readonly TimeSpan CommitTimeout = TimeSpan.FromSeconds(60);

    /// <summary>Creates the folder at <paramref name="path"/>, and parent folders it needs.</summary>
    /// <param name="git">Also start a git repository in it, with an empty first commit.</param>
    public async Task<Result<NewFolder>> CreateAsync(string path, bool git, CancellationToken ct = default)
    {
        var target = await ValidateTargetAsync(path).ConfigureAwait(false);
        if (target.IsFailure)
            return target.Error;

        var (fullPath, isWithinRoots) = target.Value;
        try
        {
            Directory.CreateDirectory(fullPath);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or NotSupportedException)
        {
            LogCreateFailed(ex, fullPath);
            return FleetError.ValidationError("Path", $"Couldn't create {fullPath}: {ex.Message}");
        }

        string? warning = null;
        if (git)
        {
            try
            {
                await GitCommand.RunAsync(fullPath, InitTimeout, "init").ConfigureAwait(false);
            }
            catch (GitCommandException ex)
            {
                return FleetError.ValidationError("Git", $"Created the folder, but git init failed: {ex.GitMessage}");
            }

            try
            {
                await GitCommand.RunAsync(fullPath, CommitTimeout, "commit", "--allow-empty", "-m", FirstCommitMessage)
                    .ConfigureAwait(false);
            }
            catch (GitCommandException ex)
            {
                // Most often git doesn't know who the person is yet. The repository is still fine to work in.
                warning = $"Git couldn't make the first commit: {ex.GitMessage} New worktree works once the repository has a commit.";
            }
        }

        var added = await AddToRootsAsync(fullPath, isWithinRoots).ConfigureAwait(false);
        if (added.IsFailure)
            return added.Error;

        return new NewFolder(fullPath, git, added.Value, warning);
    }

    /// <summary>
    /// Clones <paramref name="repository"/> (<c>owner/repo</c> on GitHub, or an https or ssh address) into
    /// <paramref name="path"/>, reporting git's progress. A failed or cancelled clone leaves nothing behind.
    /// </summary>
    public async Task<Result<NewFolder>> CloneAsync(
        string repository,
        string path,
        IProgress<CloneProgress>? progress,
        CancellationToken ct = default)
    {
        var url = ResolveCloneUrl(repository);
        if (url.IsFailure)
            return url.Error;

        var target = await ValidateTargetAsync(path).ConfigureAwait(false);
        if (target.IsFailure)
            return target.Error;

        var (fullPath, isWithinRoots) = target.Value;
        var environment = await GitHubAuthEnvironmentAsync(url.Value, ct).ConfigureAwait(false);
        var cloned = await CloneFromUrlAsync(url.Value, fullPath, environment, progress, ct).ConfigureAwait(false);
        if (cloned.IsFailure)
            return cloned.Error;

        var added = await AddToRootsAsync(fullPath, isWithinRoots).ConfigureAwait(false);
        if (added.IsFailure)
            return added.Error;

        return new NewFolder(fullPath, IsGitRepo: true, AddedToFleet: added.Value, Warning: null);
    }

    /// <summary>
    /// The address git clones for what the person typed: <c>owner/repo</c> and <c>github.com/owner/repo</c>
    /// become GitHub https addresses; https and ssh addresses are kept. Local paths and other transports are refused.
    /// </summary>
    public static Result<string> ResolveCloneUrl(string repository)
    {
        var value = repository.Trim().TrimEnd('/');
        if (OwnerRepoPattern().Match(value) is { Success: true } shorthand)
            return $"https://github.com/{shorthand.Groups["owner"].Value}/{WithGitSuffix(shorthand.Groups["repo"].Value)}";

        if (value.StartsWith("github.com/", StringComparison.OrdinalIgnoreCase))
            value = "https://" + value;

        if (Uri.TryCreate(value, UriKind.Absolute, out var uri)
            && (uri.Scheme is "https" or "ssh")
            && !string.IsNullOrEmpty(uri.Host)
            && uri.AbsolutePath.Trim('/').Length > 0)
        {
            return uri.Host.Equals("github.com", StringComparison.OrdinalIgnoreCase)
                && GitHubPathPattern().Match(uri.AbsolutePath) is { Success: true } github
                ? $"https://github.com/{github.Groups["owner"].Value}/{WithGitSuffix(github.Groups["repo"].Value)}"
                : value;
        }

        if (ScpLikePattern().IsMatch(value))
            return value;

        return FleetError.ValidationError("Repository", "Enter owner/repo, or the https or ssh address of a git repository.");
    }

    /// <summary>The folder name a clone of <paramref name="url"/> gets by default: the repository's name.</summary>
    public static string FolderNameFor(string url)
    {
        var last = url.TrimEnd('/').Split('/', ':').Last();
        return last.EndsWith(".git", StringComparison.OrdinalIgnoreCase) ? last[..^4] : last;
    }

    internal async Task<Result<Unit>> CloneFromUrlAsync(
        string url,
        string fullPath,
        IReadOnlyDictionary<string, string>? environment,
        IProgress<CloneProgress>? progress,
        CancellationToken ct)
    {
        var parent = Path.GetDirectoryName(fullPath)!;
        try
        {
            Directory.CreateDirectory(parent);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or NotSupportedException)
        {
            return FleetError.ValidationError("Path", $"Couldn't create {parent}: {ex.Message}");
        }

        CloneProgress? last = null;
        void OnProgress(string line)
        {
            if (progress is null || ParseProgress(line) is not { } parsed || parsed == last)
                return;
            last = parsed;
            progress.Report(parsed);
        }

        try
        {
            await GitCommand.RunWithProgressAsync(parent, environment, OnProgress, ct, "clone", "--progress", "--", url, fullPath)
                .ConfigureAwait(false);
            return Unit.Value;
        }
        catch (GitCommandException ex)
        {
            DeletePartialClone(fullPath);
            return FleetError.ValidationError("Clone", $"Couldn't clone {url}: {ex.GitMessage}");
        }
        catch (OperationCanceledException)
        {
            DeletePartialClone(fullPath);
            throw;
        }
    }

    /// <summary>Reads one of git's progress lines, e.g. <c>Receiving objects:  38% (380/1000)</c>.</summary>
    internal static CloneProgress? ParseProgress(string line)
    {
        var match = ProgressPattern().Match(line);
        return match.Success
            ? new CloneProgress(match.Groups["phase"].Value, int.Parse(match.Groups["percent"].Value, System.Globalization.CultureInfo.InvariantCulture))
            : null;
    }

    private async Task<Result<(string FullPath, bool IsWithinRoots)>> ValidateTargetAsync(string path)
    {
        // Every session in cloud mode starts in a fresh folder of its own, so a folder made here would never be used.
        if (options.Cloud.Enabled)
            return FleetError.ValidationError("Path", "New folders aren't available in cloud mode: every session there starts in a folder of its own.");

        if (string.IsNullOrWhiteSpace(path))
            return FleetError.ValidationError("Path", "Path is required.");

        string fullPath;
        try
        {
            fullPath = WorkspaceRootService.CanonicalizePath(WorkspaceRootService.ExpandHome(path.Trim()));
        }
        catch (Exception ex) when (ex is ArgumentException or IOException or UnauthorizedAccessException or NotSupportedException)
        {
            return FleetError.ValidationError("Path", $"{path} isn't a folder path.");
        }

        if (string.IsNullOrEmpty(Path.GetFileName(fullPath)))
            return FleetError.ValidationError("Path", $"{fullPath} isn't a folder Fleet can create.");

        if (Directory.Exists(fullPath) || File.Exists(fullPath))
            return new FleetError("General.Conflict", $"{fullPath} already exists.");

        var allowedRoots = await workspaceRootService.GetAllowedRootsAsync().ConfigureAwait(false);
        return (fullPath, WorkspaceRootService.IsPathWithinRoots(fullPath, allowedRoots));
    }

    /// <summary>Adds a folder outside the workspace roots to them; true when it was added.</summary>
    private async Task<Result<bool>> AddToRootsAsync(string fullPath, bool isWithinRoots)
    {
        if (isWithinRoots)
            return false;

        var added = await workspaceRootService.AddRootAsync(fullPath).ConfigureAwait(false);
        return added.IsSuccess ? true : added.Error;
    }

    /// <summary>
    /// For a clone from github.com, the person's GitHub token as a one-off header for this git process
    /// only. It never reaches the repository's config.
    /// </summary>
    private async Task<IReadOnlyDictionary<string, string>?> GitHubAuthEnvironmentAsync(string url, CancellationToken ct)
    {
        if (gitHubTokens is null || !url.StartsWith("https://github.com/", StringComparison.OrdinalIgnoreCase))
            return null;

        var token = await gitHubTokens.GetTokenAsync(userContext.UserId, ct).ConfigureAwait(false);
        if (string.IsNullOrEmpty(token))
            return null;

        var basic = Convert.ToBase64String(Encoding.UTF8.GetBytes($"x-access-token:{token}"));
        return new Dictionary<string, string>
        {
            ["GIT_CONFIG_COUNT"] = "1",
            ["GIT_CONFIG_KEY_0"] = "http.https://github.com/.extraheader",
            ["GIT_CONFIG_VALUE_0"] = $"Authorization: Basic {basic}",
        };
    }

    private void DeletePartialClone(string fullPath)
    {
        try
        {
            if (Directory.Exists(fullPath))
            {
                // Git makes pack files read-only, which Windows refuses to delete.
                foreach (var file in Directory.EnumerateFiles(fullPath, "*", SearchOption.AllDirectories))
                    File.SetAttributes(file, FileAttributes.Normal);
                Directory.Delete(fullPath, recursive: true);
            }
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            LogCleanupFailed(ex, fullPath);
        }
    }

    private static string WithGitSuffix(string repo) =>
        repo.EndsWith(".git", StringComparison.OrdinalIgnoreCase) ? repo : repo + ".git";

    [GeneratedRegex(@"^(?<owner>[A-Za-z0-9][A-Za-z0-9-]*)/(?<repo>[A-Za-z0-9._-]+)$")]
    private static partial Regex OwnerRepoPattern();

    /// <summary>A repository's page on github.com, or a page inside it (<c>/tree/main</c>, <c>/pull/12</c>).</summary>
    [GeneratedRegex(@"^/(?<owner>[A-Za-z0-9][A-Za-z0-9-]*)/(?<repo>[A-Za-z0-9._-]+?)(?:\.git)?(?:/.*)?$")]
    private static partial Regex GitHubPathPattern();

    /// <summary>ssh in scp form: <c>git@github.com:owner/repo.git</c>.</summary>
    [GeneratedRegex(@"^[A-Za-z0-9._-]+@[A-Za-z0-9.-]+:[A-Za-z0-9._/-]+$")]
    private static partial Regex ScpLikePattern();

    [GeneratedRegex(@"^(?<phase>Receiving objects|Resolving deltas|Updating files):\s+(?<percent>\d{1,3})%")]
    private static partial Regex ProgressPattern();

    [LoggerMessage(Level = LogLevel.Warning, Message = "Couldn't create folder {Path}")]
    private partial void LogCreateFailed(Exception ex, string path);

    [LoggerMessage(Level = LogLevel.Warning, Message = "Couldn't remove the partial clone at {Path}")]
    private partial void LogCleanupFailed(Exception ex, string path);
}

/// <summary>A folder Fleet just made.</summary>
/// <param name="AddedToFleet">It was outside the workspace roots and has been added to them.</param>
/// <param name="Warning">Something the person should know, such as a missing first commit.</param>
public sealed record NewFolder(string Path, bool IsGitRepo, bool AddedToFleet, string? Warning);

/// <summary>How far a clone has got, in git's own words: a phase and a percentage.</summary>
public sealed record CloneProgress(string Phase, int Percent);

/// <summary>The GitHub token a person connected in Fleet, if any.</summary>
public interface IGitHubTokenSource
{
    Task<string?> GetTokenAsync(string userId, CancellationToken ct = default);
}
