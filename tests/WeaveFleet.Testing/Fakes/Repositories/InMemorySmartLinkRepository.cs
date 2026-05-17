using System.Data;
using WeaveFleet.Domain.Entities;
using WeaveFleet.Domain.Repositories;

namespace WeaveFleet.Testing.Fakes.Repositories;

public sealed class InMemorySmartLinkRepository : ISmartLinkRepository
{
    private readonly List<SmartLink> _store = [];

    public void Seed(SmartLink smartLink) => _store.Add(smartLink);

    public IReadOnlyList<SmartLink> All => [.. _store];

    public Task<IReadOnlyList<SmartLink>> ListBySessionIdAsync(string sessionId)
    {
        IReadOnlyList<SmartLink> result = [.. _store.Where(l => l.SessionId == sessionId)];
        return Task.FromResult(result);
    }

    public Task<IReadOnlyList<SmartLink>> ListActiveBySessionIdAsync(string sessionId)
    {
        IReadOnlyList<SmartLink> result = [.. _store.Where(l => l.SessionId == sessionId && !l.IsDismissed)];
        return Task.FromResult(result);
    }

    public Task<SmartLink?> GetBySessionIdAndUrlAsync(string sessionId, string url)
    {
        var result = _store.FirstOrDefault(l => l.SessionId == sessionId && l.Url == url);
        return Task.FromResult(result);
    }

    public Task UpsertAsync(SmartLink smartLink)
    {
        var existing = _store.FindIndex(l => l.SessionId == smartLink.SessionId && l.Url == smartLink.Url);
        if (existing >= 0)
            _store[existing] = smartLink;
        else
            _store.Add(smartLink);
        return Task.CompletedTask;
    }

    public Task DismissAsync(string id)
    {
        var link = _store.FirstOrDefault(l => l.Id == id);
        if (link is not null)
            link.IsDismissed = true;
        return Task.CompletedTask;
    }

    public Task DeleteBySessionIdAsync(string sessionId)
    {
        _store.RemoveAll(l => l.SessionId == sessionId);
        return Task.CompletedTask;
    }

    public Task DeleteBySessionIdAsync(IDbConnection connection, IDbTransaction? transaction, string sessionId)
        => DeleteBySessionIdAsync(sessionId);

    public Task DeleteOrphanedAsync(CancellationToken ct) => Task.CompletedTask;

    public Task<IReadOnlyList<SmartLink>> ListNonTerminalPrLinksAsync(CancellationToken ct)
    {
        IReadOnlyList<SmartLink> result = [.. _store.Where(l =>
            l.ResourceType == "pull_request" && !l.IsTerminal && !l.IsDismissed)];
        return Task.FromResult(result);
    }

    public Task UpdateMetadataAsync(string id, string metadataJson, CancellationToken ct)
    {
        var link = _store.FirstOrDefault(l => l.Id == id);
        if (link is not null)
            link.MetadataJson = metadataJson;
        return Task.CompletedTask;
    }
}
