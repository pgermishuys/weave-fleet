using System.Security.Cryptography;
using System.Text.RegularExpressions;

namespace WeaveFleet.Application.Pages;

/// <summary>A page Fleet copied: how much it took. <see cref="Entry"/> is the page's file name within the copy.</summary>
public sealed record PageCopy(string PageId, string Entry, int Files, long Bytes);

/// <summary>The copy, or why there isn't one (text for the agent to read).</summary>
public sealed record PageCopyResult(PageCopy? Page, string? Problem);

/// <summary>
/// Fleet's copies of the pages agents show (<c>fleet_page_show</c>), served at <c>/pages/{pageId}/…</c>. A page is
/// copied, never served from the agent's folder, so the address only ever serves what the agent showed.
/// </summary>
public interface IPageStore
{
    /// <summary>
    /// Copies <paramref name="entryFile"/> and the web files in its folder (<see cref="PageRules"/>) to the page
    /// <paramref name="pageId"/>, replacing what it held. A reader sees the old copy or the new one, never half.
    /// </summary>
    Task<PageCopyResult> CopyAsync(string sessionId, string pageId, string entryFile, CancellationToken ct = default);

    /// <summary>
    /// The file <paramref name="path"/> names in the page (empty for the page itself), or null when there's no
    /// such page or file. A path that leaves the page, or names a dot file, is null too.
    /// </summary>
    string? Resolve(string pageId, string path);

    /// <summary>Deletes one page, e.g. when its tab moved to another file.</summary>
    Task DeleteAsync(string sessionId, string pageId, CancellationToken ct = default);

    /// <summary>Deletes every page a session showed, once the session is gone.</summary>
    Task DeleteSessionAsync(string sessionId, CancellationToken ct = default);
}

public static partial class PageIds
{
    public const string Prefix = "pg_";

    /// <summary>A new page id: 128 random bits, since the address is all it takes to open the page.</summary>
    public static string New() => Prefix + Convert.ToHexStringLower(RandomNumberGenerator.GetBytes(16));

    public static bool IsValid(string? id) => id is not null && Pattern().IsMatch(id);

    [GeneratedRegex("^pg_[0-9a-f]{32}$")]
    private static partial Regex Pattern();
}
