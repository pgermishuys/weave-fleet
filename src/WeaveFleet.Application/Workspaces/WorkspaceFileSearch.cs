using System.Collections.Concurrent;
using System.Diagnostics;
using System.IO.Enumeration;

namespace WeaveFleet.Application.Workspaces;

/// <summary>
/// Finds files and folders in a session's directory for the composer's <c>@</c> references. It lists
/// what git would (tracked and untracked, minus ignored), so build output and dependencies stay out;
/// outside a git repository it walks the tree and skips the usual noise folders. Paths use <c>/</c>
/// and folders end in <c>/</c>. Listings are cached because the composer asks as you type: a listing
/// older than <see cref="FreshFor"/> still answers at once while a new one is made in the background,
/// so only the first ask for a folder waits on git.
/// </summary>
public static class WorkspaceFileSearch
{
    private static readonly TimeSpan FreshFor = TimeSpan.FromSeconds(10);
    private static readonly TimeSpan KeptFor = TimeSpan.FromMinutes(10);
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
    public static Task<IReadOnlyList<string>> FindAsync(string directory, string query, int limit, CancellationToken ct = default) =>
        FindAsync(directory, query, limit, TimeProvider.System, ct);

    internal static async Task<IReadOnlyList<string>> FindAsync(string directory, string query, int limit, TimeProvider clock, CancellationToken ct = default)
    {
        var entries = await ListAsync(Path.GetFullPath(directory), clock, ct).ConfigureAwait(false);
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

    private static async Task<IReadOnlyList<string>> ListAsync(string root, TimeProvider clock, CancellationToken ct)
    {
        var now = clock.GetUtcNow();
        foreach (var entry in Cache)
        {
            if (now - entry.Value.UsedAt >= KeptFor)
                Cache.TryRemove(entry);
        }

        var listing = Cache.GetOrAdd(root, static (folder, madeAt) => new Listing(folder, madeAt), now);
        listing.UsedAt = now;

        Task<IReadOnlyList<string>> entries = listing.Entries;
        try
        {
            var result = await entries.WaitAsync(ct).ConfigureAwait(false);
            if (now - listing.MadeAt >= FreshFor)
                Remake(root, listing, clock);
            return result;
        }
        catch (Exception) when (entries.IsFaulted)
        {
            Cache.TryRemove(KeyValuePair.Create(root, listing));
            throw;
        }
    }

    /// <summary>Makes a new listing for <paramref name="root"/> in the background; the old one answers until it's done.</summary>
    private static void Remake(string root, Listing stale, TimeProvider clock)
    {
        if (Interlocked.Exchange(ref stale.Remaking, 1) == 1)
            return;

        var next = new Listing(root, clock.GetUtcNow()) { UsedAt = stale.UsedAt };
        _ = next.Entries.ContinueWith(
            made =>
            {
                if (made.IsCompletedSuccessfully)
                    Cache.TryUpdate(root, next, stale);
                else
                    Volatile.Write(ref stale.Remaking, 0);
            },
            CancellationToken.None,
            TaskContinuationOptions.ExecuteSynchronously,
            TaskScheduler.Default);
    }

    /// <summary>What git lists, or the walked tree outside git; made off the request so a cancelled one doesn't stop it.</summary>
    private static async Task<IReadOnlyList<string>> MakeListingAsync(string root)
    {
        var files = await ListWithGitAsync(root, CancellationToken.None).ConfigureAwait(false);
        return files is null ? Walk(root) : WithFolders(files);
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

    /// <summary>A folder's listing: started when made, shared by every request while it's being made.</summary>
    private sealed class Listing(string root, DateTimeOffset madeAt)
    {
        private readonly Lazy<Task<IReadOnlyList<string>>> _entries = new(() => Task.Run(() => MakeListingAsync(root)));

        public Task<IReadOnlyList<string>> Entries => _entries.Value;
        public DateTimeOffset MadeAt { get; } = madeAt;
        public DateTimeOffset UsedAt { get; set; } = madeAt;
        public int Remaking;
    }
}
