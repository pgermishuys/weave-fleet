namespace WeaveFleet.Application.Workflows;

/// <summary>Whether the files a step declares are in the run's worktree.</summary>
public interface IWorkflowFiles
{
    /// <summary>The declared files that aren't there, in the order given.</summary>
    /// <param name="worktree">The run's worktree; null when it doesn't exist yet, so nothing is there.</param>
    /// <param name="files">Paths relative to the worktree.</param>
    IReadOnlyList<string> Missing(string? worktree, IReadOnlyList<string> files);
}

/// <summary>Looks on disk. A path that would leave the worktree counts as missing.</summary>
public sealed class WorkflowFiles : IWorkflowFiles
{
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
}
