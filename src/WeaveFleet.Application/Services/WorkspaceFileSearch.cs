using System.Collections.Concurrent;
using System.Diagnostics;
using System.IO.Enumeration;

namespace WeaveFleet.Application.Services;

/// <summary>
/// Finds files and folders in a session's directory for the composer's <c>@</c> references. It lists
/// what git would (tracked and untracked, minus ignored), so build output and dependencies stay out;
/// outside a git repository it walks the tree and skips the usual noise folders. Paths use <c>/</c>
/// and folders end in <c>/</c>. Listings are cached briefly because the composer asks as you type.
/// </summary>
public static class WorkspaceFileSearch
{
    private static readonly TimeSpan CacheTtl = TimeSpan.FromSeconds(10);
    private static readonly TimeSpan GitTimeout = TimeSpan.FromSeconds(10);
    private const int MaxWalkEntries = 50_000;

    private static readonly HashSet<string> SkippedFolders = new(StringComparer.OrdinalIgnoreCase)
    {
        ".git", "node_modules", "bin", "obj", "dist", "build", "out", "target", "coverage",
        ".venv", "venv", "__pycache__", ".next", ".nuxt", ".turbo", ".cache", ".gradle", ".idea", ".vs",
    };

    private static readonly ConcurrentDictionary<string, Listing> Cache = new(StringComparer.Ordinal);

    /// <summary>
    /// An empty query, or one ending in <c>/</c>, lists that folder's children, folders first.
    /// Anything else returns matching paths, best first: name is the query, name starts with it,
    /// name contains it, path contains it; then fuzzy matches, where the query's characters appear
    /// in order in the name, then in the path (so "fcanv" finds FileCanvas.vue); then shallower
    /// paths, then alphabetical.
    /// </summary>
    public static async Task<IReadOnlyList<string>> FindAsync(string directory, string query, int limit, CancellationToken ct = default)
    {
        var entries = await ListAsync(Path.GetFullPath(directory), ct).ConfigureAwait(false);
        var normalized = query.Trim().Replace('\\', '/').TrimStart('/');

        return normalized.Length == 0 || normalized.EndsWith('/')
            ? Children(entries, normalized, limit)
            : Search(entries, normalized, limit);
    }

    private static List<string> Children(IReadOnlyList<string> entries, string folder, int limit) =>
        entries
            .Where(entry => entry.Length > folder.Length
                && entry.StartsWith(folder, StringComparison.OrdinalIgnoreCase)
                && entry.IndexOf('/', folder.Length) is var slash
                && (slash == -1 || slash == entry.Length - 1))
            .OrderByDescending(entry => entry.EndsWith('/'))
            .ThenBy(entry => entry, StringComparer.OrdinalIgnoreCase)
            .Take(limit)
            .ToList();

    private static List<string> Search(IReadOnlyList<string> entries, string query, int limit)
    {
        var matchesPathOnly = query.Contains('/');

        return entries
            .Select(entry => (Entry: entry, Rank: Rank(entry, query, matchesPathOnly)))
            .Where(candidate => candidate.Rank >= 0)
            .OrderBy(candidate => candidate.Rank)
            .ThenBy(candidate => Depth(candidate.Entry))
            .ThenByDescending(candidate => candidate.Entry.EndsWith('/'))
            .ThenBy(candidate => candidate.Entry, StringComparer.OrdinalIgnoreCase)
            .Take(limit)
            .Select(candidate => candidate.Entry)
            .ToList();
    }

    /// <summary>Lower is better; -1 means no match.</summary>
    private static int Rank(string entry, string query, bool matchesPathOnly)
    {
        var path = entry.TrimEnd('/');
        if (matchesPathOnly)
        {
            if (path.StartsWith(query, StringComparison.OrdinalIgnoreCase)) return 0;
            if (path.Contains(query, StringComparison.OrdinalIgnoreCase)) return 3;
            return InOrder(path, query) ? 5 : -1;
        }

        var name = path[(path.LastIndexOf('/') + 1)..];
        if (name.Equals(query, StringComparison.OrdinalIgnoreCase)) return 0;
        if (name.StartsWith(query, StringComparison.OrdinalIgnoreCase)) return 1;
        if (name.Contains(query, StringComparison.OrdinalIgnoreCase)) return 2;
        if (path.Contains(query, StringComparison.OrdinalIgnoreCase)) return 3;
        if (InOrder(name, query)) return 4;
        return InOrder(path, query) ? 5 : -1;
    }

    /// <summary>Whether every character of <paramref name="query"/> appears in <paramref name="text"/>, in order.</summary>
    private static bool InOrder(string text, string query)
    {
        var next = 0;
        foreach (var c in text)
        {
            if (next < query.Length && char.ToLowerInvariant(c) == char.ToLowerInvariant(query[next]))
                next++;
        }
        return next == query.Length;
    }

    private static int Depth(string entry) => entry.TrimEnd('/').Count(c => c == '/');

    private static async Task<IReadOnlyList<string>> ListAsync(string root, CancellationToken ct)
    {
        var now = DateTime.UtcNow;
        foreach (var (key, stale) in Cache)
        {
            if (now - stale.CreatedAt >= CacheTtl)
                Cache.TryRemove(key, out _);
        }

        if (Cache.TryGetValue(root, out var cached))
            return cached.Entries;

        var files = await ListWithGitAsync(root, ct).ConfigureAwait(false);
        var entries = files is null ? Walk(root) : WithFolders(files);
        Cache[root] = new Listing(entries, now);
        return entries;
    }

    /// <summary>Files git knows about or would add, relative to <paramref name="root"/>; null when git can't say.</summary>
    private static async Task<IReadOnlyList<string>?> ListWithGitAsync(string root, CancellationToken ct)
    {
        using var process = new Process();
        process.StartInfo = new ProcessStartInfo("git")
        {
            WorkingDirectory = root,
            UseShellExecute = false,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            CreateNoWindow = true,
        };
        foreach (var argument in new[] { "ls-files", "-z", "--cached", "--others", "--exclude-standard" })
            process.StartInfo.ArgumentList.Add(argument);

        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(ct);
        timeout.CancelAfter(GitTimeout);

        try
        {
            if (!process.Start())
                return null;

            var output = process.StandardOutput.ReadToEndAsync(timeout.Token);
            var errors = process.StandardError.ReadToEndAsync(timeout.Token);
            await process.WaitForExitAsync(timeout.Token).ConfigureAwait(false);
            await errors.ConfigureAwait(false);

            if (process.ExitCode != 0)
                return null;

            return (await output.ConfigureAwait(false)).Split('\0', StringSplitOptions.RemoveEmptyEntries);
        }
        catch (OperationCanceledException) when (!ct.IsCancellationRequested)
        {
            Kill(process);
            return null;
        }
        catch (OperationCanceledException)
        {
            Kill(process);
            throw;
        }
        catch (Exception exception) when (exception is System.ComponentModel.Win32Exception or InvalidOperationException or IOException)
        {
            return null;
        }
    }

    private static void Kill(Process process)
    {
        try { process.Kill(entireProcessTree: true); } catch (InvalidOperationException) { }
    }

    /// <summary>The files plus every folder above them.</summary>
    private static List<string> WithFolders(IReadOnlyList<string> files)
    {
        var entries = new HashSet<string>(StringComparer.Ordinal);
        foreach (var file in files)
        {
            entries.Add(file);
            for (var slash = file.IndexOf('/'); slash != -1; slash = file.IndexOf('/', slash + 1))
                entries.Add(file[..(slash + 1)]);
        }

        return [.. entries];
    }

    private static List<string> Walk(string root)
    {
        var options = new EnumerationOptions
        {
            RecurseSubdirectories = true,
            IgnoreInaccessible = true,
            AttributesToSkip = 0,
        };

        var walk = new FileSystemEnumerable<string>(
            root,
            (ref FileSystemEntry entry) =>
            {
                var relative = Path.GetRelativePath(root, entry.ToFullPath()).Replace('\\', '/');
                return entry.IsDirectory ? relative + "/" : relative;
            },
            options)
        {
            ShouldIncludePredicate = (ref FileSystemEntry entry) =>
                !(entry.IsDirectory && SkippedFolders.Contains(entry.FileName.ToString())),
            ShouldRecursePredicate = (ref FileSystemEntry entry) =>
                !SkippedFolders.Contains(entry.FileName.ToString())
                && (entry.Attributes & FileAttributes.ReparsePoint) == 0,
        };

        try
        {
            return walk.Take(MaxWalkEntries).ToList();
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
        {
            return [];
        }
    }

    private sealed record Listing(IReadOnlyList<string> Entries, DateTime CreatedAt);
}
