using WeaveFleet.Domain.Entities;

namespace WeaveFleet.Application.Services;

/// <summary>
/// Where a session's changes are read and what they're compared with: the Changes tab's diff, and the walkthrough's.
/// <see cref="WorkspacePrefix"/> is the session's folder within the repository ("" at its root), so a session in a
/// subfolder sees only that folder's changes.
/// </summary>
public sealed record SessionDiffScope(string RepoRoot, GitDiffBase Base, string WorkspacePrefix)
{
    /// <summary>
    /// The session's scope, or null when its folder isn't in a git repository or there's nothing to compare with. A
    /// session Fleet took no baseline for (a delegated child, say) still has its folder's branch to compare with.
    /// </summary>
    public static async Task<SessionDiffScope?> ResolveAsync(Session session, GitDiffService git, CancellationToken ct)
    {
        var repoRoot = string.IsNullOrWhiteSpace(session.GitRepoRoot)
            ? await git.FindRepoRootAsync(session.Directory, ct).ConfigureAwait(false)
            : session.GitRepoRoot;
        if (repoRoot is null || ComputeWorkspacePrefix(repoRoot, session.Directory) is not { } prefix)
            return null;

        var diffBase = await git.ResolveDiffBaseAsync(repoRoot, session.GitBaselineRef, ct).ConfigureAwait(false);
        return diffBase is null ? null : new SessionDiffScope(repoRoot, diffBase, prefix);
    }

    /// <summary>The session's folder relative to the repository, with forward slashes; null when it's outside it.</summary>
    public static string? ComputeWorkspacePrefix(string repoRoot, string sessionDirectory)
    {
        if (string.IsNullOrWhiteSpace(repoRoot))
            return null;

        if (string.IsNullOrWhiteSpace(sessionDirectory))
            return string.Empty;

        try
        {
            var repoRootFullPath = Path.GetFullPath(repoRoot);
            var sessionDirectoryFullPath = Path.GetFullPath(sessionDirectory);

            if (!IsSameOrChildPath(sessionDirectoryFullPath, repoRootFullPath))
                return null;

            if (PathsEqual(sessionDirectoryFullPath, repoRootFullPath))
                return string.Empty;

            return Path.GetRelativePath(repoRootFullPath, sessionDirectoryFullPath)
                .Replace(Path.DirectorySeparatorChar, '/')
                .Replace(Path.AltDirectorySeparatorChar, '/')
                .Trim('/');
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
        if (PathsEqual(candidate, root))
            return true;

        return candidate.StartsWith(EnsureEndingDirectorySeparator(root), PathStringComparison);
    }

    private static bool PathsEqual(string left, string right) =>
        string.Equals(
            TrimEndingDirectorySeparator(left),
            TrimEndingDirectorySeparator(right),
            PathStringComparison);

    private static string EnsureEndingDirectorySeparator(string path) =>
        Path.EndsInDirectorySeparator(path) ? path : path + Path.DirectorySeparatorChar;

    private static string TrimEndingDirectorySeparator(string path) =>
        Path.GetPathRoot(path) == path
            ? path
            : path.TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);

    private static StringComparison PathStringComparison =>
        OperatingSystem.IsWindows() ? StringComparison.OrdinalIgnoreCase : StringComparison.Ordinal;
}
