using WeaveFleet.Application.Services;

namespace WeaveFleet.Application.Workflows;

/// <summary>Whether the files a step declares are in the run's worktree, and committing them once they are.</summary>
public interface IWorkflowFiles
{
    /// <summary>The declared files that aren't there, in the order given.</summary>
    /// <param name="worktree">The run's worktree; null when it doesn't exist yet, so nothing is there.</param>
    /// <param name="files">Paths relative to the worktree.</param>
    IReadOnlyList<string> Missing(string? worktree, IReadOnlyList<string> files);

    /// <summary>
    /// Commits the declared files that are new or changed, and only those, on the worktree's branch. Nothing to commit
    /// (the agent committed them, or git ignores them) does nothing. Never throws: a failure comes back as its error.
    /// </summary>
    /// <param name="title">The step's title, for the commit message.</param>
    Task<WorkflowFilesCommit> CommitAsync(string? worktree, IReadOnlyList<string> files, string title, CancellationToken ct);
}

/// <summary>What committing a step's declared files did.</summary>
/// <param name="Commit">The commit's short SHA; null when there was nothing to commit, or it failed.</param>
/// <param name="Files">The files it committed.</param>
/// <param name="Error">Why it failed, in git's words; null when it didn't.</param>
public sealed record WorkflowFilesCommit(string? Commit, IReadOnlyList<string> Files, string? Error)
{
    public static readonly WorkflowFilesCommit Nothing = new(null, [], null);

    public static WorkflowFilesCommit Failed(string error) => new(null, [], error);
}

/// <summary>Looks on disk, and commits with git. A path that would leave the worktree counts as missing.</summary>
public sealed class WorkflowFiles : IWorkflowFiles
{
    /// <summary>Long enough for a commit hook; short enough that a signing prompt nobody sees can't hold the run.</summary>
    private static readonly TimeSpan GitTimeout = TimeSpan.FromSeconds(30);

    /// <summary>Past this, the subject names the step only and the files are in the body.</summary>
    private const int SubjectLimit = 72;

    public IReadOnlyList<string> Missing(string? worktree, IReadOnlyList<string> files)
    {
        if (string.IsNullOrWhiteSpace(worktree))
            return files;

        var root = Path.TrimEndingDirectorySeparator(Path.GetFullPath(worktree)) + Path.DirectorySeparatorChar;
        return files.Where(file =>
        {
            var full = Path.GetFullPath(Path.Combine(root, file));
            return !full.StartsWith(root, StringComparison.Ordinal) || !File.Exists(full);
        }).ToList();
    }

    public async Task<WorkflowFilesCommit> CommitAsync(string? worktree, IReadOnlyList<string> files, string title, CancellationToken ct)
    {
        if (string.IsNullOrWhiteSpace(worktree) || files.Count == 0)
            return WorkflowFilesCommit.Nothing;

        try
        {
            // New or changed among the declared paths. A path git ignores isn't listed, and Fleet never force-adds it:
            // that's how a workflow keeps its notes out of the pull request.
            var status = await Git(worktree, ["status", "--porcelain", "-z", "--untracked-files=all", "--", .. files]).ConfigureAwait(false);
            var changed = Changed(status, files);
            if (changed.Count == 0)
                return WorkflowFilesCommit.Nothing;

            await Git(worktree, ["add", "--", .. changed]).ConfigureAwait(false);

            // With paths, commit takes only those, whatever else is staged. The repo's identity and hooks apply.
            await Git(worktree, ["commit", "--quiet", "-m", Message(title, changed), "--", .. changed]).ConfigureAwait(false);
            var sha = (await Git(worktree, ["rev-parse", "--short", "HEAD"]).ConfigureAwait(false)).Trim();
            return new WorkflowFilesCommit(sha, changed, null);
        }
        catch (GitCommandException ex)
        {
            return WorkflowFilesCommit.Failed(ex.GitMessage);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            // No git at all, or the worktree is gone.
            return WorkflowFilesCommit.Failed(ex.Message);
        }
    }

    /// <summary>
    /// A conventional commit, so a repo's commit-message hook takes it: <c>docs: design (x.md, x.html)</c>, then each
    /// path on its own line.
    /// </summary>
    internal static string Message(string title, IReadOnlyList<string> files)
    {
        var step = title.Length > 0 ? char.ToLowerInvariant(title[0]) + title[1..] : "workflow step";
        var subject = $"docs: {step} ({string.Join(", ", files.Select(Path.GetFileName))})";
        if (subject.Length > SubjectLimit)
            subject = $"docs: {step}";
        return $"{subject}\n\n{string.Join('\n', files)}";
    }

    /// <summary>The declared paths <c>git status --porcelain -z</c> lists, in the order they were declared.</summary>
    internal static IReadOnlyList<string> Changed(string status, IReadOnlyList<string> files)
    {
        var listed = new HashSet<string>(StringComparer.Ordinal);
        var entries = status.Split('\0');
        for (var i = 0; i < entries.Length; i++)
        {
            var entry = entries[i];
            if (entry.Length < 4)
                continue;

            listed.Add(entry[3..]);

            // A rename or a copy is followed by the path it came from.
            if (entry[0] is 'R' or 'C')
                i++;
        }

        return files.Where(listed.Contains).ToList();
    }

    private static Task<string> Git(string worktree, string[] args)
        => GitCommand.RunAsync(worktree, GitTimeout, ["--literal-pathspecs", .. args]);
}
