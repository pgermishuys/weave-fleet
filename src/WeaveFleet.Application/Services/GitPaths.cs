namespace WeaveFleet.Application.Services;

/// <summary>
/// Recognises git checkouts on disk without running git.
/// </summary>
public static class GitPaths
{
    private const string GitDirPrefix = "gitdir:";

    /// <summary>
    /// True when <paramref name="path"/> is the top of a git checkout: a <c>.git</c> directory, or a
    /// <c>.git</c> file pointing at one elsewhere (a linked worktree, a submodule, a separate git dir).
    /// </summary>
    public static bool IsRepository(string path)
    {
        var dotGit = Path.Combine(path, ".git");
        return Directory.Exists(dotGit) || ReadGitDirPointer(dotGit) is not null;
    }

    /// <summary>
    /// True when <paramref name="path"/> is a linked worktree made by <c>git worktree add</c>: its
    /// <c>.git</c> file points into the main repository's <c>.git/worktrees</c> folder.
    /// </summary>
    public static bool IsLinkedWorktree(string path)
    {
        var gitDir = ReadGitDirPointer(Path.Combine(path, ".git"));
        if (gitDir is null)
            return false;

        var parent = Path.GetDirectoryName(gitDir.TrimEnd('/', '\\'));
        return parent is not null
            && string.Equals(Path.GetFileName(parent), "worktrees", StringComparison.Ordinal);
    }

    /// <summary>
    /// The folder Fleet creates a repository's new worktrees in: <c>{repo}-worktrees</c> next to it.
    /// </summary>
    public static string WorktreesFolderFor(string repositoryPath)
    {
        var trimmed = repositoryPath.TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);
        var parent = Path.GetDirectoryName(trimmed) ?? trimmed;
        return Path.Combine(parent, $"{Path.GetFileName(trimmed)}-worktrees");
    }

    private static string? ReadGitDirPointer(string dotGitFile)
    {
        if (!File.Exists(dotGitFile))
            return null;

        try
        {
            using var reader = new StreamReader(dotGitFile);
            var firstLine = reader.ReadLine()?.Trim();
            if (firstLine is null || !firstLine.StartsWith(GitDirPrefix, StringComparison.Ordinal))
                return null;

            var target = firstLine[GitDirPrefix.Length..].Trim();
            return target.Length == 0 ? null : target;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            return null;
        }
    }
}
