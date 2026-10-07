using System.Globalization;
using System.Text;

namespace WeaveFleet.Application.Services;

public sealed class GitDiffService
{
    private const int MaxFileContentBytes = 512 * 1024;
    private const int MaxConcurrentContentFetches = 8;

    private readonly IGitDiffCommandRunner _commandRunner;

    public GitDiffService()
        : this(new GitDiffProcessCommandRunner())
    {
    }

    public GitDiffService(IGitDiffCommandRunner commandRunner)
    {
        _commandRunner = commandRunner;
    }

    public Task<GitBaselineCapture?> CaptureBaselineAsync(string workDir, CancellationToken ct)
    {
        var baselineId = Guid.NewGuid().ToString("N", CultureInfo.InvariantCulture);
        return CaptureBaselineAsync(workDir, baselineId, ct);
    }

    public async Task<GitBaselineCapture?> CaptureBaselineAsync(string workDir, string baselineId, CancellationToken ct)
    {
        if (string.IsNullOrWhiteSpace(workDir))
            return null;

        var repoRoot = await FindRepoRootAsync(workDir, ct).ConfigureAwait(false);
        if (repoRoot is null)
            return null;

        var stashResult = await RunGitAsync(repoRoot, ["stash", "create", $"fleet baseline {baselineId}"], ct).ConfigureAwait(false);
        if (!stashResult.IsSuccess)
            return null;

        var baselineObject = stashResult.StandardOutput.Trim();
        if (string.IsNullOrWhiteSpace(baselineObject))
        {
            var headResult = await RunGitAsync(repoRoot, ["rev-parse", "HEAD"], ct).ConfigureAwait(false);
            if (!headResult.IsSuccess)
                return null;

            baselineObject = headResult.StandardOutput.Trim();
            if (string.IsNullOrWhiteSpace(baselineObject))
                return null;
        }

        var refName = $"refs/fleet/baselines/{ToRefComponent(baselineId)}";
        var updateResult = await RunGitAsync(repoRoot, ["update-ref", refName, baselineObject], ct).ConfigureAwait(false);
        if (!updateResult.IsSuccess)
            return null;

        return new GitBaselineCapture(refName, repoRoot);
    }

    /// <summary>The root of the git repository <paramref name="directory"/> is in, or null when it isn't in one.</summary>
    public async Task<string?> FindRepoRootAsync(string directory, CancellationToken ct)
    {
        if (string.IsNullOrWhiteSpace(directory))
            return null;

        var result = await RunGitAsync(directory, ["rev-parse", "--show-toplevel"], ct).ConfigureAwait(false);
        var repoRoot = result.StandardOutput.Trim();
        return result.IsSuccess && !string.IsNullOrWhiteSpace(repoRoot) ? repoRoot : null;
    }

    /// <summary>
    /// What a session's changes are compared with. On a branch other than the repository's main branch it's where
    /// the branch left main, as a pull request shows it: rebasing onto a newer main, pulling it, or switching
    /// branches then doesn't list other people's work as the session's. On main itself or a detached HEAD there's
    /// no branch to compare, so it's the baseline taken when the session started. Null when there's neither.
    /// </summary>
    public async Task<GitDiffBase?> ResolveDiffBaseAsync(string repoRoot, string? sessionBaselineRef, CancellationToken ct)
    {
        if (string.IsNullOrWhiteSpace(repoRoot))
            return null;

        var branchBase = await ResolveBranchBaseAsync(repoRoot, ct).ConfigureAwait(false);
        if (branchBase is not null)
            return branchBase;

        return string.IsNullOrWhiteSpace(sessionBaselineRef)
            ? null
            : new GitDiffBase(sessionBaselineRef, GitDiffBaseKind.SessionStart, MainBranch: null);
    }

    private async Task<GitDiffBase?> ResolveBranchBaseAsync(string repoRoot, CancellationToken ct)
    {
        var branchResult = await RunGitAsync(repoRoot, ["symbolic-ref", "--quiet", "--short", "HEAD"], ct).ConfigureAwait(false);
        var branch = branchResult.StandardOutput.Trim();
        if (!branchResult.IsSuccess || branch.Length == 0)
            return null;

        var main = await FindMainBranchAsync(repoRoot, ct).ConfigureAwait(false);
        if (main is null || string.Equals(branch, main.Value.Name, StringComparison.Ordinal))
            return null;

        var mergeBaseResult = await RunGitAsync(repoRoot, ["merge-base", "HEAD", main.Value.Ref], ct).ConfigureAwait(false);
        var mergeBase = mergeBaseResult.StandardOutput.Trim();
        if (!mergeBaseResult.IsSuccess || mergeBase.Length == 0)
            return null;

        return new GitDiffBase(mergeBase, GitDiffBaseKind.Branch, main.Value.Name);
    }

    // The branch pull requests go to: origin's default branch, else main or master. Origin's copy comes first,
    // because that's what a branch is rebased onto.
    private static readonly string[] MainBranchCandidates =
        ["refs/remotes/origin/main", "refs/remotes/origin/master", "refs/heads/main", "refs/heads/master"];

    private async Task<(string Ref, string Name)?> FindMainBranchAsync(string repoRoot, CancellationToken ct)
    {
        const string OriginHead = "refs/remotes/origin/HEAD";
        var result = await RunGitAsync(
            repoRoot,
            ["for-each-ref", "--format=%(refname)%09%(symref)", OriginHead, .. MainBranchCandidates],
            ct).ConfigureAwait(false);
        if (!result.IsSuccess)
            return null;

        var refs = new Dictionary<string, string>(StringComparer.Ordinal);
        foreach (var line in SplitLines(result.StandardOutput))
        {
            var columns = line.Split('\t');
            refs[columns[0]] = columns.Length > 1 ? columns[1] : string.Empty;
        }

        var found = refs.TryGetValue(OriginHead, out var originDefault) && originDefault.Length > 0
            ? originDefault
            : MainBranchCandidates.FirstOrDefault(refs.ContainsKey);
        if (found is null)
            return null;

        var name = found.StartsWith("refs/remotes/origin/", StringComparison.Ordinal)
            ? found["refs/remotes/origin/".Length..]
            : found.StartsWith("refs/heads/", StringComparison.Ordinal) ? found["refs/heads/".Length..] : found;
        return (found, name);
    }

    public async Task<IReadOnlyList<FileDiffSummary>> ComputeDiffsAsync(
        string repoRoot,
        string baselineRef,
        string workspacePrefix,
        CancellationToken ct)
    {
        var result = await ComputeDiffsWithAvailabilityAsync(repoRoot, baselineRef, workspacePrefix, ct).ConfigureAwait(false);
        return result.Diffs;
    }

    public async Task<IReadOnlyList<FileDiffContent>> ComputeDiffsWithContentAsync(
        string repoRoot,
        string baselineRef,
        string workspacePrefix,
        CancellationToken ct)
    {
        var result = await ComputeDiffsWithAvailabilityAsync(repoRoot, baselineRef, workspacePrefix, ct).ConfigureAwait(false);
        if (!result.Available || result.Diffs.Count == 0)
            return [];

        var content = new FileDiffContent[result.Diffs.Count];
        await Parallel.ForEachAsync(
            Enumerable.Range(0, result.Diffs.Count),
            new ParallelOptions
            {
                CancellationToken = ct,
                MaxDegreeOfParallelism = MaxConcurrentContentFetches
            },
            async (index, cancellationToken) =>
            {
                var summary = result.Diffs[index];
                content[index] = await ToFileDiffContentAsync(repoRoot, baselineRef, summary, cancellationToken).ConfigureAwait(false);
            }).ConfigureAwait(false);

        return content;
    }

    /// <summary>
    /// One changed file with its baseline and working-tree contents, or null when <paramref name="path"/> isn't
    /// among the session's changes.
    /// </summary>
    public async Task<FileDiffContent?> ComputeFileDiffWithContentAsync(
        string repoRoot,
        string baselineRef,
        string workspacePrefix,
        string path,
        CancellationToken ct)
    {
        var result = await ComputeDiffsWithAvailabilityAsync(repoRoot, baselineRef, workspacePrefix, ct).ConfigureAwait(false);
        var summary = result.Diffs.FirstOrDefault(diff => string.Equals(diff.Path, path, StringComparison.Ordinal));
        return summary is null
            ? null
            : await ToFileDiffContentAsync(repoRoot, baselineRef, summary, ct).ConfigureAwait(false);
    }

    public Task<string?> GetFileContentAsync(string repoRoot, string @ref, string path) =>
        GetFileContentAsync(repoRoot, @ref, path, CancellationToken.None);

    public async Task<string?> GetFileContentAsync(string repoRoot, string? @ref, string path, CancellationToken ct)
    {
        var result = await GetFileContentResultAsync(repoRoot, @ref, path, ct).ConfigureAwait(false);
        return result.State == FileContentReadState.Available ? result.Content : null;
    }

    private async Task<FileContentReadResult> GetFileContentResultAsync(string repoRoot, string? @ref, string path, CancellationToken ct)
    {
        if (string.IsNullOrWhiteSpace(repoRoot) || string.IsNullOrWhiteSpace(path))
            return FileContentReadResult.Missing;

        var normalizedPath = NormalizeRepositoryRelativePath(repoRoot, path);
        if (normalizedPath is null)
            return FileContentReadResult.Missing;

        if (string.IsNullOrWhiteSpace(@ref))
            return await ReadWorkingTreeFileContentAsync(repoRoot, normalizedPath, ct).ConfigureAwait(false);

        var result = await RunGitAsync(repoRoot, ["show", $"{@ref}:{normalizedPath}"], ct).ConfigureAwait(false);
        if (!result.IsSuccess)
            return FileContentReadResult.Missing;

        return ToTextContent(result.StandardOutput);
    }

    public async Task<GitDiffComputationResult> ComputeDiffsWithAvailabilityAsync(
        string repoRoot,
        string baselineRef,
        string workspacePrefix,
        CancellationToken ct)
    {
        if (string.IsNullOrWhiteSpace(repoRoot) || string.IsNullOrWhiteSpace(baselineRef))
            return GitDiffComputationResult.Unavailable;

        var prefix = NormalizeWorkspacePrefix(workspacePrefix);
        var diffArgs = BuildPathspecArguments(["diff", "--numstat", baselineRef, "--"], prefix);
        var diffResult = await RunGitAsync(repoRoot, diffArgs, ct).ConfigureAwait(false);
        if (!diffResult.IsSuccess)
            return GitDiffComputationResult.Unavailable;

        var statusArgs = BuildPathspecArguments(["diff", "--name-status", baselineRef, "--"], prefix);
        var statusResult = await RunGitAsync(repoRoot, statusArgs, ct).ConfigureAwait(false);
        if (!statusResult.IsSuccess)
            return GitDiffComputationResult.Unavailable;

        var untrackedArgs = BuildPathspecArguments(["ls-files", "--others", "--exclude-standard", "--"], prefix);
        var untrackedResult = await RunGitAsync(repoRoot, untrackedArgs, ct).ConfigureAwait(false);
        if (!untrackedResult.IsSuccess)
            return GitDiffComputationResult.Unavailable;

        var untrackedLineCounts = CountUntrackedLines(repoRoot, untrackedResult.StandardOutput);

        return new GitDiffComputationResult(
            ParseDiffs(diffResult.StandardOutput, statusResult.StandardOutput, untrackedResult.StandardOutput, untrackedLineCounts),
            Available: true);
    }

    private async Task<FileDiffContent> ToFileDiffContentAsync(
        string repoRoot,
        string baselineRef,
        FileDiffSummary summary,
        CancellationToken ct)
    {
        var status = summary.Status ?? (summary.IsUntracked ? "added" : "modified");
        FileContentReadResult? beforeResult = null;
        FileContentReadResult? afterResult = null;

        if (!summary.IsBinary)
        {
            if (status != "added" && !summary.IsUntracked)
                beforeResult = await GetFileContentResultAsync(repoRoot, baselineRef, summary.Path, ct).ConfigureAwait(false);

            if (status != "deleted")
                afterResult = await GetFileContentResultAsync(repoRoot, null, summary.Path, ct).ConfigureAwait(false);
        }

        var isBinary = summary.IsBinary
            || beforeResult?.State == FileContentReadState.Binary
            || afterResult?.State == FileContentReadState.Binary;
        var isTruncated = beforeResult?.State == FileContentReadState.Truncated
            || afterResult?.State == FileContentReadState.Truncated;

        var before = ResolveContentSide(status == "added" || summary.IsUntracked, beforeResult, isBinary, isTruncated);
        var after = ResolveContentSide(status == "deleted", afterResult, isBinary, isTruncated);

        return new FileDiffContent(
            summary.Path,
            before,
            after,
            isBinary,
            isTruncated,
            summary.AddedLines ?? 0,
            summary.DeletedLines ?? 0,
            status);
    }

    private static string? ResolveContentSide(
        bool isEmptyForStatus,
        FileContentReadResult? contentResult,
        bool isBinary,
        bool isTruncated)
    {
        if (isBinary || isTruncated || isEmptyForStatus)
            return string.Empty;

        return contentResult?.State == FileContentReadState.Available ? contentResult.Content : null;
    }

    internal static IReadOnlyList<FileDiffSummary> ParseDiffs(string numstatOutput, string untrackedOutput) =>
        ParseDiffs(numstatOutput, string.Empty, untrackedOutput);

    internal static IReadOnlyList<FileDiffSummary> ParseDiffs(string numstatOutput, string nameStatusOutput, string untrackedOutput)
    {
        return ParseDiffs(numstatOutput, nameStatusOutput, untrackedOutput, new Dictionary<string, int?>(StringComparer.Ordinal));
    }

    private static IReadOnlyList<FileDiffSummary> ParseDiffs(
        string numstatOutput,
        string nameStatusOutput,
        string untrackedOutput,
        IReadOnlyDictionary<string, int?> untrackedLineCounts)
    {
        var statuses = ParseNameStatuses(nameStatusOutput);
        var summaries = new Dictionary<string, FileDiffSummary>(StringComparer.Ordinal);

        foreach (var rawLine in SplitLines(numstatOutput))
        {
            var columns = rawLine.Split('\t');
            if (columns.Length < 3)
                continue;

            var path = columns[^1].Trim();
            if (string.IsNullOrWhiteSpace(path))
                continue;

            var isBinary = columns[0] == "-" || columns[1] == "-";
            var addedLines = ParseNullableLineCount(columns[0]);
            var deletedLines = ParseNullableLineCount(columns[1]);
            var summary = new FileDiffSummary(path, addedLines, deletedLines, isBinary, IsUntracked: false)
            {
                Status = statuses.GetValueOrDefault(path)
            };
            summaries[path] = summary;
        }

        foreach (var rawLine in SplitLines(untrackedOutput))
        {
            var path = rawLine.Trim();
            if (string.IsNullOrWhiteSpace(path) || summaries.ContainsKey(path))
                continue;

            summaries[path] = new FileDiffSummary(path, untrackedLineCounts.GetValueOrDefault(path), DeletedLines: 0, IsBinary: false, IsUntracked: true)
            {
                Status = "added"
            };
        }

        return [.. summaries.Values.OrderBy(summary => summary.Path, StringComparer.Ordinal)];
    }

    private static Dictionary<string, int?> CountUntrackedLines(string repoRoot, string untrackedOutput)
    {
        var lineCounts = new Dictionary<string, int?>(StringComparer.Ordinal);
        foreach (var rawLine in SplitLines(untrackedOutput))
        {
            var path = rawLine.Trim();
            if (string.IsNullOrWhiteSpace(path))
                continue;

            lineCounts[path] = TryCountTextLines(repoRoot, path);
        }

        return lineCounts;
    }

    private static int? TryCountTextLines(string repoRoot, string relativePath)
    {
        try
        {
            var repoRootFullPath = Path.GetFullPath(repoRoot);
            var fullPath = Path.GetFullPath(Path.Combine(repoRootFullPath, relativePath));
            if (!IsSameOrChildPath(fullPath, repoRootFullPath) || !File.Exists(fullPath))
                return null;

            return CountTextLines(fullPath);
        }
        catch (Exception ex) when (ex is ArgumentException or IOException or NotSupportedException or UnauthorizedAccessException)
        {
            return null;
        }
    }

    private static int? CountTextLines(string fullPath)
    {
        using var stream = File.OpenRead(fullPath);
        var buffer = new byte[8192];
        var lines = 0;
        var hasBytes = false;
        byte lastByte = 0;

        while (true)
        {
            var bytesRead = stream.Read(buffer, 0, buffer.Length);
            if (bytesRead == 0)
                break;

            hasBytes = true;
            for (var index = 0; index < bytesRead; index++)
            {
                var value = buffer[index];
                if (value == 0)
                    return null;

                if (value == '\n')
                    lines++;

                lastByte = value;
            }
        }

        if (hasBytes && lastByte != '\n')
            lines++;

        return lines;
    }

    private static async Task<FileContentReadResult> ReadWorkingTreeFileContentAsync(string repoRoot, string relativePath, CancellationToken ct)
    {
        try
        {
            var repoRootFullPath = Path.GetFullPath(repoRoot);
            var fullPath = Path.GetFullPath(Path.Combine(repoRootFullPath, relativePath));
            if (!IsSameOrChildPath(fullPath, repoRootFullPath) || !File.Exists(fullPath))
                return FileContentReadResult.Missing;

            var fileInfo = new FileInfo(fullPath);
            if (fileInfo.Length > MaxFileContentBytes)
                return FileContentReadResult.Truncated;

            var bytes = await File.ReadAllBytesAsync(fullPath, ct).ConfigureAwait(false);
            return ToTextContent(bytes);
        }
        catch (Exception ex) when (ex is ArgumentException or IOException or NotSupportedException or UnauthorizedAccessException)
        {
            return FileContentReadResult.Missing;
        }
    }

    private static FileContentReadResult ToTextContent(string content)
    {
        if (Encoding.UTF8.GetByteCount(content) > MaxFileContentBytes)
            return FileContentReadResult.Truncated;

        if (content.Contains('\0', StringComparison.Ordinal))
            return FileContentReadResult.Binary;

        return FileContentReadResult.Available(content);
    }

    private static FileContentReadResult ToTextContent(byte[] bytes)
    {
        if (bytes.Length > MaxFileContentBytes)
            return FileContentReadResult.Truncated;

        if (bytes.Contains((byte)0))
            return FileContentReadResult.Binary;

        try
        {
            return FileContentReadResult.Available(new UTF8Encoding(encoderShouldEmitUTF8Identifier: false, throwOnInvalidBytes: true).GetString(bytes));
        }
        catch (DecoderFallbackException)
        {
            return FileContentReadResult.Binary;
        }
    }

    private static string? NormalizeRepositoryRelativePath(string repoRoot, string relativePath)
    {
        try
        {
            var repoRootFullPath = Path.GetFullPath(repoRoot);
            var fullPath = Path.GetFullPath(Path.Combine(repoRootFullPath, relativePath));
            if (!IsSameOrChildPath(fullPath, repoRootFullPath))
                return null;

            return Path.GetRelativePath(repoRootFullPath, fullPath).Replace(Path.DirectorySeparatorChar, '/');
        }
        catch (Exception ex) when (ex is ArgumentException or IOException or NotSupportedException or UnauthorizedAccessException)
        {
            return null;
        }
    }

    private static bool IsSameOrChildPath(string candidatePath, string rootPath)
    {
        var root = TrimEndingDirectorySeparator(Path.GetFullPath(rootPath));
        var candidate = TrimEndingDirectorySeparator(Path.GetFullPath(candidatePath));
        if (string.Equals(candidate, root, PathStringComparison))
            return true;

        return candidate.StartsWith(EnsureEndingDirectorySeparator(root), PathStringComparison);
    }

    private static string EnsureEndingDirectorySeparator(string path) =>
        Path.EndsInDirectorySeparator(path) ? path : path + Path.DirectorySeparatorChar;

    private static string TrimEndingDirectorySeparator(string path) =>
        Path.GetPathRoot(path) == path
            ? path
            : path.TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);

    private static StringComparison PathStringComparison =>
        OperatingSystem.IsWindows() ? StringComparison.OrdinalIgnoreCase : StringComparison.Ordinal;

    private static Dictionary<string, string> ParseNameStatuses(string nameStatusOutput)
    {
        var statuses = new Dictionary<string, string>(StringComparer.Ordinal);
        foreach (var rawLine in SplitLines(nameStatusOutput))
        {
            var columns = rawLine.Split('\t');
            if (columns.Length < 2)
                continue;

            var path = columns[^1].Trim();
            if (string.IsNullOrWhiteSpace(path))
                continue;

            statuses[path] = ToClientStatus(columns[0]);
        }

        return statuses;
    }

    private static string ToClientStatus(string gitStatus) =>
        gitStatus switch
        {
            var status when status.StartsWith('A') => "added",
            var status when status.StartsWith('D') => "deleted",
            _ => "modified"
        };

    private async Task<GitCommandResult> RunGitAsync(string workingDirectory, IReadOnlyList<string> arguments, CancellationToken ct)
    {
        try
        {
            return await _commandRunner.RunAsync(workingDirectory, arguments, ct).ConfigureAwait(false);
        }
        catch (Exception ex) when (ex is InvalidOperationException or IOException or UnauthorizedAccessException)
        {
            return new GitCommandResult(ExitCode: -1, StandardOutput: string.Empty, StandardError: ex.Message);
        }
    }

    private static IReadOnlyList<string> BuildPathspecArguments(IReadOnlyList<string> baseArguments, string prefix)
    {
        if (string.IsNullOrEmpty(prefix))
            return baseArguments;

        return [.. baseArguments, prefix];
    }

    private static string NormalizeWorkspacePrefix(string workspacePrefix)
    {
        if (string.IsNullOrWhiteSpace(workspacePrefix))
            return string.Empty;

        return workspacePrefix.Replace('\\', '/').Trim('/');
    }

    private static string ToRefComponent(string baselineId)
    {
        var builder = new StringBuilder(baselineId.Length);
        foreach (var ch in baselineId)
        {
            if (char.IsAsciiLetterOrDigit(ch) || ch is '-' or '_')
                builder.Append(ch);
            else
                builder.Append('-');
        }

        var component = builder.ToString().Trim('-');
        return string.IsNullOrWhiteSpace(component) ? Guid.NewGuid().ToString("N", CultureInfo.InvariantCulture) : component;
    }

    private static int? ParseNullableLineCount(string value) =>
        int.TryParse(value, NumberStyles.None, CultureInfo.InvariantCulture, out var count) ? count : null;

    private static string[] SplitLines(string output) =>
        output.Split(['\r', '\n'], StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
}

public interface IGitDiffCommandRunner
{
    Task<GitCommandResult> RunAsync(string workingDirectory, IReadOnlyList<string> arguments, CancellationToken ct);
}

public sealed class GitDiffProcessCommandRunner : IGitDiffCommandRunner
{
    /// <summary>A diff of a big change set takes seconds; a git that takes this long is stuck, and is stopped.</summary>
    private static readonly TimeSpan Timeout = TimeSpan.FromMinutes(1);

    public async Task<GitCommandResult> RunAsync(string workingDirectory, IReadOnlyList<string> arguments, CancellationToken ct)
    {
        try
        {
            return await GitCommand.ExecAsync(workingDirectory, arguments, Timeout, ct).ConfigureAwait(false);
        }
        catch (Exception ex) when (ex is InvalidOperationException or System.ComponentModel.Win32Exception)
        {
            return new GitCommandResult(ExitCode: -1, StandardOutput: string.Empty, StandardError: ex.Message);
        }
        catch (GitCommandException ex)
        {
            return new GitCommandResult(ExitCode: -1, StandardOutput: string.Empty, StandardError: ex.Message);
        }
    }
}

public sealed record GitCommandResult(int ExitCode, string StandardOutput, string StandardError)
{
    public bool IsSuccess => ExitCode == 0;
}

public sealed record GitBaselineCapture(string RefName, string RepoRoot);

public enum GitDiffBaseKind
{
    /// <summary>The baseline taken when the session started.</summary>
    SessionStart,

    /// <summary>Where the branch left the repository's main branch; <see cref="GitDiffBase.Ref"/> is that commit.</summary>
    Branch
}

public sealed record GitDiffBase(string Ref, GitDiffBaseKind Kind, string? MainBranch);

public sealed record GitDiffComputationResult(IReadOnlyList<FileDiffSummary> Diffs, bool Available)
{
    public static GitDiffComputationResult Unavailable { get; } = new([], Available: false);
}

public sealed record FileDiffSummary(
    string Path,
    int? AddedLines,
    int? DeletedLines,
    bool IsBinary,
    bool IsUntracked)
{
    public string? Status { get; init; }
}

public sealed record FileDiffContent(
    string Path,
    string? Before,
    string? After,
    bool IsBinary,
    bool IsTruncated,
    int Additions,
    int Deletions,
    string Status);

internal enum FileContentReadState
{
    Available,
    Missing,
    Binary,
    Truncated
}

internal sealed record FileContentReadResult(FileContentReadState State, string? Content)
{
    public static FileContentReadResult Missing { get; } = new(FileContentReadState.Missing, Content: null);

    public static FileContentReadResult Binary { get; } = new(FileContentReadState.Binary, Content: null);

    public static FileContentReadResult Truncated { get; } = new(FileContentReadState.Truncated, Content: null);

    public static FileContentReadResult Available(string content) => new(FileContentReadState.Available, content);
}
