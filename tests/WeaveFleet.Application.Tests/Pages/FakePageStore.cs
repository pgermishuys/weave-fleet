using WeaveFleet.Application.Pages;

namespace WeaveFleet.Application.Tests.Pages;

/// <summary>Copies nothing: it records what it was asked to copy and delete, and says each copy took two files.</summary>
internal sealed class FakePageStore : IPageStore
{
    public List<(string SessionId, string PageId, string EntryFile)> Copies { get; } = [];

    public List<string> Deleted { get; } = [];

    /// <summary>Set to fail the next copy, as a folder too big to be a page would.</summary>
    public string? NextProblem { get; set; }

    public Task<PageCopyResult> CopyAsync(string sessionId, string pageId, string entryFile, CancellationToken ct = default)
    {
        if (NextProblem is { } problem)
        {
            NextProblem = null;
            return Task.FromResult(new PageCopyResult(null, problem));
        }

        Copies.Add((sessionId, pageId, entryFile));
        return Task.FromResult(new PageCopyResult(new PageCopy(pageId, Path.GetFileName(entryFile), 2, 38 * 1024), null));
    }

    public string? Resolve(string pageId, string path) => null;

    public Task DeleteAsync(string sessionId, string pageId, CancellationToken ct = default)
    {
        Deleted.Add(pageId);
        return Task.CompletedTask;
    }

    public Task DeleteSessionAsync(string sessionId, CancellationToken ct = default) => Task.CompletedTask;
}
