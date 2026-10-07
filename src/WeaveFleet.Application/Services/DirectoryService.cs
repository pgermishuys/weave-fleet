using Microsoft.Extensions.Logging;
using WeaveFleet.Application.Services;

namespace WeaveFleet.Application.Services;

/// <summary>
/// Provides filesystem directory listing for the UI's folder picker, constrained to allowed workspace roots.
/// </summary>
public sealed partial class DirectoryService(
    WorkspaceRootService workspaceRootService,
    ILogger<DirectoryService> logger)
{
    /// <summary>
    /// Lists subdirectories at the given path.
    /// If <paramref name="path"/> is null/empty, returns the workspace roots as top-level entries.
    /// </summary>
    public async Task<DirectoryListingResult> ListDirectoryAsync(
        string? path,
        CancellationToken ct = default)
    {
        var allowedRoots = await workspaceRootService.GetAllowedRootsAsync().ConfigureAwait(false);

        // If no path given, return roots as the listing
        if (string.IsNullOrEmpty(path))
        {
            var rootEntries = allowedRoots
                .Select(r => new DirectoryEntry(
                    Name: Path.GetFileName(r.TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar))
                         ?? r,
                    FullPath: r,
                    IsGitRepo: GitPaths.IsRepository(r),
                    IsRoot: true))
                .ToList();

            return new DirectoryListingResult(
                Entries: rootEntries,
                CurrentPath: null,
                ParentPath: null,
                Roots: allowedRoots);
        }

        // Normalise + security check
        var normalised = Path.GetFullPath(path);
        if (!IsUnderAllowedRoot(normalised, allowedRoots))
        {
            LogPathDenied(normalised);
            return new DirectoryListingResult(
                Entries: [],
                CurrentPath: normalised,
                ParentPath: null,
                Roots: allowedRoots);
        }

        if (!Directory.Exists(normalised))
        {
            return new DirectoryListingResult(
                Entries: [],
                CurrentPath: normalised,
                ParentPath: GetParent(normalised),
                Roots: allowedRoots);
        }

        // If this path is exactly a workspace root, parent should be null (back to root list)
        var isRoot = allowedRoots.Any(r =>
            normalised.Equals(Path.GetFullPath(r), StringComparison.OrdinalIgnoreCase));
        var parent = isRoot ? null : GetParent(normalised);

        List<DirectoryEntry> entries;
        try
        {
            entries = Directory.EnumerateDirectories(normalised)
                .OrderBy(d => d, StringComparer.OrdinalIgnoreCase)
                .Select(d => new DirectoryEntry(
                    Name: Path.GetFileName(d),
                    FullPath: d,
                    IsGitRepo: GitPaths.IsRepository(d),
                    IsRoot: false))
                .ToList();
        }
        catch (UnauthorizedAccessException ex)
        {
            LogAccessDenied(ex, normalised);
            entries = [];
        }

        return new DirectoryListingResult(
            Entries: entries,
            CurrentPath: normalised,
            ParentPath: parent,
            Roots: allowedRoots);
    }

    /// <summary>
    /// Lists subdirectories at the given path without restricting to workspace roots; <c>~</c> is the home folder.
    /// If <paramref name="path"/> is null/empty, returns filesystem drive roots.
    /// Used by Settings → Folders when adding a location, and by the new-session folder box as you type.
    /// </summary>
    public Task<DirectoryListingResult> ListDirectoryUnconstrainedAsync(
        string? path,
        CancellationToken ct = default)
    {
        // If no path given, return filesystem drives as the listing
        if (string.IsNullOrEmpty(path))
        {
            var drives = DriveInfo.GetDrives()
                .Where(d => d.IsReady)
                .Select(d =>
                {
                    var name = d.Name.TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);
                    if (string.IsNullOrEmpty(name))
                        name = d.RootDirectory.FullName; // e.g. "/" on Linux/macOS
                    return new DirectoryEntry(
                        Name: name,
                        FullPath: d.RootDirectory.FullName,
                        IsGitRepo: false,
                        IsRoot: true);
                })
                .ToList();

            return Task.FromResult(new DirectoryListingResult(
                Entries: drives,
                CurrentPath: null,
                ParentPath: null,
                Roots: []));
        }

        string normalised;
        try
        {
            normalised = Path.GetFullPath(WorkspaceRootService.ExpandHome(path.Trim()));
        }
        catch (Exception ex) when (ex is ArgumentException or IOException or NotSupportedException)
        {
            return Task.FromResult(new DirectoryListingResult(
                Entries: [],
                CurrentPath: path,
                ParentPath: null,
                Roots: [],
                Exists: false));
        }

        if (!Directory.Exists(normalised))
        {
            return Task.FromResult(new DirectoryListingResult(
                Entries: [],
                CurrentPath: normalised,
                ParentPath: GetParent(normalised),
                Roots: [],
                Exists: false,
                NearestExisting: NearestExistingFolder(normalised)));
        }

        var parent = GetParent(normalised);

        List<DirectoryEntry> entries;
        try
        {
            entries = Directory.EnumerateDirectories(normalised)
                .OrderBy(d => d, StringComparer.OrdinalIgnoreCase)
                .Select(d => new DirectoryEntry(
                    Name: Path.GetFileName(d),
                    FullPath: d,
                    IsGitRepo: GitPaths.IsRepository(d),
                    IsRoot: false))
                .ToList();
        }
        catch (UnauthorizedAccessException ex)
        {
            LogAccessDenied(ex, normalised);
            entries = [];
        }

        return Task.FromResult(new DirectoryListingResult(
            Entries: entries,
            CurrentPath: normalised,
            ParentPath: parent,
            Roots: []));
    }

    /// <summary>
    /// Describes one folder for the new-session folder picker: whether it exists, whether it is a
    /// git repository, and whether it is inside the workspace roots (so a session can use it).
    /// </summary>
    public async Task<FolderInspection> InspectFolderAsync(string path, CancellationToken ct = default)
    {
        string normalised;
        try
        {
            normalised = WorkspaceRootService.CanonicalizePath(WorkspaceRootService.ExpandHome(path.Trim()));
        }
        catch (Exception ex) when (ex is ArgumentException or IOException or UnauthorizedAccessException or NotSupportedException)
        {
            return new FolderInspection(path, Exists: false, IsGitRepo: false, IsWithinRoots: false);
        }

        if (!Directory.Exists(normalised))
            return new FolderInspection(normalised, Exists: false, IsGitRepo: false, IsWithinRoots: false);

        var allowedRoots = await workspaceRootService.GetAllowedRootsAsync().ConfigureAwait(false);
        return new FolderInspection(
            normalised,
            Exists: true,
            IsGitRepo: GitPaths.IsRepository(normalised),
            IsWithinRoots: WorkspaceRootService.IsPathWithinRoots(normalised, allowedRoots));
    }

    // ── Helpers ────────────────────────────────────────────────────────────────

    private static bool IsUnderAllowedRoot(string path, IReadOnlyList<string> roots)
    {
        foreach (var root in roots)
        {
            var normRoot = Path.GetFullPath(root);
            if (path.Equals(normRoot, StringComparison.OrdinalIgnoreCase))
                return true;
            var rootWithSep = normRoot.EndsWith(Path.DirectorySeparatorChar)
                ? normRoot
                : normRoot + Path.DirectorySeparatorChar;
            if (path.StartsWith(rootWithSep, StringComparison.OrdinalIgnoreCase))
                return true;
        }
        return false;
    }

    /// <summary>The deepest folder above <paramref name="path"/> that's there, so the picker can show what a create would add.</summary>
    private static string? NearestExistingFolder(string path)
    {
        for (var candidate = GetParent(path); candidate is not null; candidate = GetParent(candidate))
        {
            if (Directory.Exists(candidate))
                return candidate;
        }
        return null;
    }

    private static string? GetParent(string path)
    {
        var parent = Path.GetDirectoryName(path);
        return string.IsNullOrEmpty(parent) ? null : parent;
    }

    [LoggerMessage(Level = LogLevel.Warning, Message = "Directory listing denied for path outside allowed roots: {Path}")]
    private partial void LogPathDenied(string path);

    [LoggerMessage(Level = LogLevel.Warning, Message = "Access denied listing directory {Path}")]
    private partial void LogAccessDenied(Exception ex, string path);
}

/// <summary>Result of a directory listing.</summary>
/// <param name="Exists">Whether <paramref name="CurrentPath"/> is a folder that's there.</param>
/// <param name="NearestExisting">When it isn't, the deepest folder above it that is.</param>
public sealed record DirectoryListingResult(
    IReadOnlyList<DirectoryEntry> Entries,
    string? CurrentPath,
    string? ParentPath,
    IReadOnlyList<string> Roots,
    bool Exists = true,
    string? NearestExisting = null);

/// <summary>What the folder picker needs to know about one folder.</summary>
public sealed record FolderInspection(
    string Path,
    bool Exists,
    bool IsGitRepo,
    bool IsWithinRoots);

/// <summary>A single directory entry.</summary>
public sealed record DirectoryEntry(
    string Name,
    string FullPath,
    bool IsGitRepo,
    bool IsRoot);
