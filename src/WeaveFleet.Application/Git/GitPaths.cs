namespace WeaveFleet.Application.Git;

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

    /// <summary>
    /// The main checkout of the repository <paramref name="directory"/> is in, or <see langword="null"/> when it isn't in
    /// one. A folder inside a checkout gives the checkout, and a linked worktree gives the checkout it was made from, so
    /// every worktree of a repository has the same answer. A submodule or a separate git dir gives its own checkout.
    /// </summary>
    public static string? MainCheckoutOf(string directory)
    {
        string? current;
        try
        {
            current = Path.GetFullPath(directory);
        }
        catch (Exception ex) when (ex is ArgumentException or NotSupportedException or PathTooLongException)
        {
            return null;
        }

        for (; current is not null; current = Path.GetDirectoryName(current))
        {
            var dotGit = Path.Combine(current, ".git");
            if (Directory.Exists(dotGit))
                return current;

            if (ReadGitDirPointer(dotGit) is not { } gitDir)
                continue;

            // A worktree's .git file points at {main}/.git/worktrees/{name}.
            var target = Path.GetFullPath(Path.IsPathRooted(gitDir) ? gitDir : Path.Combine(current, gitDir));
            var worktrees = Path.GetDirectoryName(target.TrimEnd('/', '\\'));
            var mainGit = worktrees is null ? null : Path.GetDirectoryName(worktrees);
            return worktrees is not null
                   && string.Equals(Path.GetFileName(worktrees), "worktrees", StringComparison.Ordinal)
                   && mainGit is not null
                   && string.Equals(Path.GetFileName(mainGit), ".git", StringComparison.Ordinal)
                   && Path.GetDirectoryName(mainGit) is { } main
                ? main
                : current;
        }

        return null;
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
