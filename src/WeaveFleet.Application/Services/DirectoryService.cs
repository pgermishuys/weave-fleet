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
                    IsGitRepo: Directory.Exists(Path.Combine(r, ".git")),
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
                    IsGitRepo: Directory.Exists(Path.Combine(d, ".git")),
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
    /// Lists subdirectories at the given path without restricting to workspace roots.
    /// If <paramref name="path"/> is null/empty, returns filesystem drive roots.
    /// Used by the workspace settings UI when adding a new root.
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
                .Select(d => new DirectoryEntry(
                    Name: d.Name.TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar),
                    FullPath: d.RootDirectory.FullName,
                    IsGitRepo: false,
                    IsRoot: true))
                .ToList();

            return Task.FromResult(new DirectoryListingResult(
                Entries: drives,
                CurrentPath: null,
                ParentPath: null,
                Roots: []));
        }

        var normalised = Path.GetFullPath(path);

        if (!Directory.Exists(normalised))
        {
            return Task.FromResult(new DirectoryListingResult(
                Entries: [],
                CurrentPath: normalised,
                ParentPath: GetParent(normalised),
                Roots: []));
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
                    IsGitRepo: Directory.Exists(Path.Combine(d, ".git")),
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
public sealed record DirectoryListingResult(
    IReadOnlyList<DirectoryEntry> Entries,
    string? CurrentPath,
    string? ParentPath,
    IReadOnlyList<string> Roots);

/// <summary>A single directory entry.</summary>
public sealed record DirectoryEntry(
    string Name,
    string FullPath,
    bool IsGitRepo,
    bool IsRoot);
