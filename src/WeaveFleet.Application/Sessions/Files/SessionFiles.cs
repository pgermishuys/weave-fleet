using Microsoft.Extensions.Logging;
using WeaveFleet.Application.Events;
using WeaveFleet.Application.Services;
using WeaveFleet.Domain.Common;
using WeaveFleet.Domain.Repositories;

namespace WeaveFleet.Application.Sessions.Files;

/// <summary>
/// The files in a session's folder, as the file browser, the editor and links in replies see them: browsing, reading,
/// finding, resolving paths, images and saving. Only the filesystem is read, so the session's harness stays asleep;
/// every path is kept inside the session's folder.
/// </summary>
public sealed partial class SessionFiles(
    ISessionRepository sessionRepository,
    IEventBroadcaster eventBroadcaster,
    ILogger<SessionFiles> logger)
{
    public async Task<Result<IReadOnlyList<string>>> FindSessionFilesAsync(
        string sessionId,
        string query,
        CancellationToken ct = default)
    {
        using var _ = logger.BeginSessionScope(sessionId);
        var sessionResult = await sessionRepository.GetSessionAsync(sessionId);
        if (sessionResult.IsFailure)
            return sessionResult.Error;

        // Only the filesystem is read, so the session's harness stays asleep.
        if (!Directory.Exists(sessionResult.Value.Directory))
            return Result.Success<IReadOnlyList<string>>(Array.Empty<string>());

        var matches = await WorkspaceFileSearch.FindAsync(sessionResult.Value.Directory, query, limit: 50, ct).ConfigureAwait(false);
        return Result.Success(matches);
    }

    public async Task<Result<BrowseDirectoryResult>> BrowseSessionDirectoryAsync(
        string sessionId,
        string? path,
        CancellationToken ct = default)
    {
        using var _ = logger.BeginSessionScope(sessionId);
        var sessionResult = await sessionRepository.GetSessionAsync(sessionId);
        if (sessionResult.IsFailure)
            return sessionResult.Error;

        var sessionDirectory = sessionResult.Value.Directory;
        if (!Directory.Exists(sessionDirectory))
            return FleetError.ValidationError("Session.Directory", "Session directory does not exist.");

        // Normalize path separators to support both forward and backslashes on all platforms
        var normalizedPath = string.IsNullOrWhiteSpace(path)
            ? null
            : path.Replace('\\', Path.DirectorySeparatorChar).Replace('/', Path.DirectorySeparatorChar);

        // Resolve target directory with path traversal protection
        var targetDirectory = string.IsNullOrWhiteSpace(normalizedPath)
            ? sessionDirectory
            : Path.GetFullPath(Path.Combine(sessionDirectory, normalizedPath));

        var sessionDirectoryFullPath = Path.GetFullPath(sessionDirectory);
        if (!IsSameOrChildPath(targetDirectory, sessionDirectoryFullPath))
            return FleetError.ValidationError("Session.Directory", "Path traversal is not allowed.");

        if (!Directory.Exists(targetDirectory))
            return FleetError.ValidationError("Session.Directory", "Directory does not exist.");

        // Enumerate entries
        var entries = Directory.EnumerateFileSystemEntries(targetDirectory, "*", SearchOption.TopDirectoryOnly)
            .Select(fullPath =>
            {
                var name = Path.GetFileName(fullPath);
                var isDirectory = Directory.Exists(fullPath);
                var relativePath = Path.GetRelativePath(sessionDirectoryFullPath, fullPath)
                    .Replace(Path.DirectorySeparatorChar, '/')
                    .Replace(Path.AltDirectorySeparatorChar, '/');
                return new BrowseEntry(name, relativePath, isDirectory);
            })
            .ToList();

        // Filter out .git directory
        var filteredEntries = entries.Where(e => !string.Equals(e.Name, ".git", StringComparison.Ordinal)).ToList();

        // Sort: directories first, then files, both alphabetical
        var sortedEntries = filteredEntries
            .OrderByDescending(e => e.IsDirectory)
            .ThenBy(e => e.Name, StringComparer.OrdinalIgnoreCase)
            .ToList();

        var currentPath = string.IsNullOrWhiteSpace(path)
            ? string.Empty
            : path.Replace(Path.DirectorySeparatorChar, '/').Replace(Path.AltDirectorySeparatorChar, '/').Trim('/');

        return new BrowseDirectoryResult(sortedEntries, currentPath);
    }

    public async Task<Result<ReadFileResult>> ReadSessionFileAsync(
        string sessionId,
        string? path,
        CancellationToken ct = default)
    {
        using var _ = logger.BeginSessionScope(sessionId);
        if (string.IsNullOrWhiteSpace(path))
            return FleetError.ValidationError("Session.File", "Path parameter is required.");

        var sessionResult = await sessionRepository.GetSessionAsync(sessionId);
        if (sessionResult.IsFailure)
            return sessionResult.Error;

        var sessionDirectory = sessionResult.Value.Directory;
        if (!Directory.Exists(sessionDirectory))
            return FleetError.ValidationError("Session.Directory", "Session directory does not exist.");

        // Normalize path separators to support both forward and backslashes on all platforms
        var normalizedPath = path.Replace('\\', Path.DirectorySeparatorChar).Replace('/', Path.DirectorySeparatorChar);

        // Resolve target file with path traversal protection
        // Path.GetFullPath will normalize separators (both / and \ work on all platforms)
        var targetFilePath = Path.GetFullPath(Path.Combine(sessionDirectory, normalizedPath));
        var sessionDirectoryFullPath = Path.GetFullPath(sessionDirectory);
        if (!IsSameOrChildPath(targetFilePath, sessionDirectoryFullPath))
            return FleetError.ValidationError("Session.File", "Path traversal is not allowed.");

        if (!File.Exists(targetFilePath))
            return FleetError.NotFoundFor("File", path);

        try
        {
            var fileInfo = new FileInfo(targetFilePath);
            if (fileInfo.Length > MaxEditableFileBytes)
            {
                return new ReadFileResult(path, Content: null, IsBinary: false, IsTruncated: true);
            }

            var bytes = await File.ReadAllBytesAsync(targetFilePath, ct).ConfigureAwait(false);
            var hash = HashFileBytes(bytes);
            var content = DecodeText(bytes);
            return content is null
                ? new ReadFileResult(path, Content: null, IsBinary: true, IsTruncated: false, hash)
                : new ReadFileResult(path, content, IsBinary: false, IsTruncated: false, hash);
        }
        catch (Exception ex) when (ex is ArgumentException or IOException or NotSupportedException or UnauthorizedAccessException)
        {
            return FleetError.NotFoundFor("File", path);
        }
    }

    /// <summary>The most paths one resolve looks at; a reply names far fewer.</summary>
    public const int MaxResolvedPaths = 200;

    /// <summary>
    /// The paths that are files in the session's folder, each with its path from the folder. Paths named in a reply
    /// come here so only real files become links. Relative and absolute paths both work, with either separator;
    /// anything outside the folder, missing, or not a file is left out.
    /// </summary>
    public async Task<Result<IReadOnlyList<ResolvedSessionFile>>> ResolveSessionFilesAsync(
        string sessionId,
        IReadOnlyList<string> paths,
        CancellationToken ct = default)
    {
        using var _ = logger.BeginSessionScope(sessionId);
        var sessionResult = await sessionRepository.GetSessionAsync(sessionId);
        if (sessionResult.IsFailure)
            return sessionResult.Error;

        var resolved = new List<ResolvedSessionFile>();
        var sessionDirectory = sessionResult.Value.Directory;
        if (!Directory.Exists(sessionDirectory))
            return Result.Success<IReadOnlyList<ResolvedSessionFile>>(resolved);

        var sessionDirectoryFullPath = Path.GetFullPath(sessionDirectory);
        foreach (var path in paths.Where(p => !string.IsNullOrWhiteSpace(p)).Distinct(StringComparer.Ordinal).Take(MaxResolvedPaths))
        {
            ct.ThrowIfCancellationRequested();
            try
            {
                var normalizedPath = path.Replace('\\', Path.DirectorySeparatorChar).Replace('/', Path.DirectorySeparatorChar);
                var targetFilePath = Path.GetFullPath(Path.Combine(sessionDirectoryFullPath, normalizedPath));
                if (!IsSameOrChildPath(targetFilePath, sessionDirectoryFullPath) || !File.Exists(targetFilePath))
                    continue;

                var relativePath = Path.GetRelativePath(sessionDirectoryFullPath, targetFilePath)
                    .Replace(Path.DirectorySeparatorChar, '/');
                resolved.Add(new ResolvedSessionFile(path, relativePath));
            }
            catch (Exception ex) when (ex is ArgumentException or NotSupportedException or PathTooLongException)
            {
                // Not a path this system can read, so not a file in the folder.
            }
        }

        return Result.Success<IReadOnlyList<ResolvedSessionFile>>(resolved);
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

/// <summary>Result of browsing a session directory.</summary>
public sealed record BrowseDirectoryResult(IReadOnlyList<BrowseEntry> Entries, string CurrentPath);

/// <summary>Represents a file or directory entry in a browsed directory.</summary>
public sealed record BrowseEntry(string Name, string RelativePath, bool IsDirectory);

/// <summary>A path as a reply named it, and the file it is in the session's folder.</summary>
/// <param name="RelativePath">From the session's folder, with <c>/</c> between names.</param>
public sealed record ResolvedSessionFile(string Path, string RelativePath);

/// <summary>Result of reading a session file.</summary>
/// <param name="Hash">SHA-256 of the file's bytes as lowercase hex; null when the file was too large to read.</param>
public sealed record ReadFileResult(string Path, string? Content, bool IsBinary, bool IsTruncated, string? Hash = null);
