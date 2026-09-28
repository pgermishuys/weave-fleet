using System.Collections.Concurrent;
using System.Text.RegularExpressions;
using Microsoft.Extensions.Logging;
using WeaveFleet.Application.Pages;

namespace WeaveFleet.Infrastructure.Pages;

/// <summary>
/// Pages under <c>{root}/{sessionId}/{pageId}/</c>, with the page's file name in <see cref="EntryFile"/>. A new copy
/// is built next to the page and swapped in, so the address serves the old page or the new one. Page ids are
/// Fleet's own and checked before they touch the disk; so is every path a request names.
/// </summary>
internal sealed partial class PageStore(string root, ILogger<PageStore> logger) : IPageStore
{
    /// <summary>Holds the page's file name. A dot file, so the address never serves it.</summary>
    internal const string EntryFile = ".entry";

    private const string BuildingPrefix = ".building-";
    private const string RetiredPrefix = ".retired-";

    /// <summary>Page id → session folder, filled as pages are copied and found.</summary>
    private readonly ConcurrentDictionary<string, string> _sessionOf = new(StringComparer.Ordinal);

    public async Task<PageCopyResult> CopyAsync(string sessionId, string pageId, string entryFile, CancellationToken ct = default)
    {
        if (!IsSafe(sessionId) || !PageIds.IsValid(pageId))
            return new PageCopyResult(null, "Fleet couldn't keep a copy of this page.");

        var source = Path.GetDirectoryName(Path.GetFullPath(entryFile))!;
        var entry = Path.GetFileName(entryFile);
        var listed = List(source);
        if (listed.Problem is not null)
            return new PageCopyResult(null, listed.Problem);
        if (!listed.Files.Contains(entry, StringComparer.Ordinal))
            return new PageCopyResult(null, $"Fleet can't copy {entryFile}: it's a link or a hidden file. Show the file itself.");

        var sessionFolder = Path.Combine(root, sessionId);
        var building = Path.Combine(sessionFolder, BuildingPrefix + Guid.NewGuid().ToString("N"));
        try
        {
            long bytes = 0;
            foreach (var relative in listed.Files)
            {
                ct.ThrowIfCancellationRequested();
                var target = Path.Combine(building, relative);
                Directory.CreateDirectory(Path.GetDirectoryName(target)!);
                File.Copy(Path.Combine(source, relative), target);
                bytes += new FileInfo(target).Length;
                if (bytes > PageRules.MaxBytes)
                    return new PageCopyResult(null, TooBig(source));
            }

            await File.WriteAllTextAsync(Path.Combine(building, EntryFile), entry, ct).ConfigureAwait(false);

            var page = Path.Combine(sessionFolder, pageId);
            Swap(building, page);
            _sessionOf[pageId] = sessionId;
            return new PageCopyResult(new PageCopy(pageId, entry, listed.Files.Count, bytes), null);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            LogCopyFailed(ex, source);
            return new PageCopyResult(null, $"Fleet couldn't copy the page from {source}: {ex.Message}");
        }
        finally
        {
            TryDelete(building);
        }
    }

    public string? Resolve(string pageId, string path)
    {
        if (!PageIds.IsValid(pageId) || FindPage(pageId) is not { } page)
            return null;

        if (path.Length == 0)
        {
            var entry = ReadEntry(page);
            return entry is null ? null : Resolve(pageId, entry);
        }

        var segments = path.Split('/');
        if (segments.Any(segment => segment.Length == 0 || segment.StartsWith('.') || segment.Contains('\\') || segment.Contains(':')))
            return null;

        var full = Path.GetFullPath(Path.Combine([page, .. segments]));
        return full.StartsWith(page + Path.DirectorySeparatorChar, StringComparison.Ordinal) && File.Exists(full) ? full : null;
    }

    public Task DeleteAsync(string sessionId, string pageId, CancellationToken ct = default)
    {
        if (IsSafe(sessionId) && PageIds.IsValid(pageId))
        {
            _sessionOf.TryRemove(pageId, out _);
            TryDelete(Path.Combine(root, sessionId, pageId));
        }
        return Task.CompletedTask;
    }

    public Task DeleteSessionAsync(string sessionId, CancellationToken ct = default)
    {
        if (!IsSafe(sessionId))
            return Task.CompletedTask;

        foreach (var (pageId, owner) in _sessionOf)
        {
            if (owner == sessionId)
                _sessionOf.TryRemove(pageId, out _);
        }
        TryDelete(Path.Combine(root, sessionId));
        return Task.CompletedTask;
    }

    /// <summary>
    /// The web files in <paramref name="folder"/> and its subfolders, as paths relative to it. Dot entries,
    /// <c>node_modules</c> and links are skipped. Stops with a problem when the folder is too big to be a page.
    /// </summary>
    private static (List<string> Files, string? Problem) List(string folder)
    {
        var files = new List<string>();
        var visited = 0;
        long bytes = 0;
        var pending = new Stack<DirectoryInfo>([new DirectoryInfo(folder)]);
        while (pending.Count > 0)
        {
            var directory = pending.Pop();
            foreach (var item in directory.EnumerateFileSystemInfos())
            {
                if (++visited > PageRules.MaxEntriesVisited)
                    return ([], TooBig(folder));
                if (item.Name.StartsWith('.') || item.LinkTarget is not null || item.Attributes.HasFlag(FileAttributes.ReparsePoint))
                    continue;

                if (item is DirectoryInfo child)
                {
                    if (!child.Name.Equals("node_modules", StringComparison.OrdinalIgnoreCase))
                        pending.Push(child);
                    continue;
                }

                if (item is not FileInfo file || !PageRules.IsWebFile(file.Name))
                    continue;

                bytes += file.Length;
                files.Add(Path.GetRelativePath(folder, file.FullName));
                if (files.Count > PageRules.MaxFiles || bytes > PageRules.MaxBytes)
                    return ([], TooBig(folder));
            }
        }

        return (files, null);
    }

    private static string TooBig(string folder)
        => $"{folder} holds more than a page: Fleet copies at most {PageRules.MaxFiles} web files and "
           + $"{PageRules.MaxBytes / (1024 * 1024)} MB from the page's folder. Put the page in a folder of its own, with only the files it uses.";

    /// <summary>Moves <paramref name="building"/> into <paramref name="page"/>'s place, retiring the old copy first.</summary>
    private void Swap(string building, string page)
    {
        if (!Directory.Exists(page))
        {
            Directory.Move(building, page);
            return;
        }

        var retired = Path.Combine(Path.GetDirectoryName(page)!, RetiredPrefix + Guid.NewGuid().ToString("N"));
        Directory.Move(page, retired);
        Directory.Move(building, page);
        TryDelete(retired);
    }

    private string? FindPage(string pageId)
    {
        if (_sessionOf.TryGetValue(pageId, out var known))
        {
            var page = Path.Combine(root, known, pageId);
            if (Directory.Exists(page))
                return page;
            _sessionOf.TryRemove(pageId, out _);
        }

        if (!Directory.Exists(root))
            return null;

        foreach (var session in Directory.EnumerateDirectories(root))
        {
            var page = Path.Combine(session, pageId);
            if (Directory.Exists(page))
            {
                _sessionOf[pageId] = Path.GetFileName(session);
                return page;
            }
        }

        return null;
    }

    private static string? ReadEntry(string page)
    {
        try
        {
            var entry = File.ReadAllText(Path.Combine(page, EntryFile)).Trim();
            return entry.Length > 0 ? entry : null;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            return null;
        }
    }

    private void TryDelete(string folder)
    {
        try
        {
            if (Directory.Exists(folder))
                Directory.Delete(folder, recursive: true);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            LogDeleteFailed(ex, folder);
        }
    }

    private static bool IsSafe(string? id) => !string.IsNullOrEmpty(id) && SafeId().IsMatch(id) && id is not ("." or "..");

    [GeneratedRegex("^[A-Za-z0-9_.-]{1,128}$")]
    private static partial Regex SafeId();

    [LoggerMessage(Level = LogLevel.Warning, Message = "Couldn't copy the page in {Folder}")]
    private partial void LogCopyFailed(Exception ex, string folder);

    [LoggerMessage(Level = LogLevel.Warning, Message = "Couldn't delete the page folder {Folder}")]
    private partial void LogDeleteFailed(Exception ex, string folder);
}
