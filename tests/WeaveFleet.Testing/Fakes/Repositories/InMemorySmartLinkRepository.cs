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

    public Task DismissAsync(string id)
    {
        var link = _store.FirstOrDefault(l => l.Id == id);
        if (link is not null)
            link.IsDismissed = true;
        return Task.CompletedTask;
    }

    public Task<bool> SetRelationshipAsync(string id, string relationship)
    {
        var link = _store.FirstOrDefault(l => l.Id == id);
        if (link is null)
            return Task.FromResult(false);
        link.Relationship = relationship;
        return Task.FromResult(true);
    }

    public Task MarkSessionDueAsync(string sessionId)
    {
        foreach (var link in _store.Where(l => l.SessionId == sessionId))
            link.LastCheckedAt = null;
        return Task.CompletedTask;
    }

    public Task<SmartLink?> InsertDetectedAsync(SmartLink link, bool restoreDismissed, CancellationToken ct)
    {
        var existing = _store.FirstOrDefault(l => l.SessionId == link.SessionId
            && (l.Url == link.Url || string.Equals(l.ResourceId, link.ResourceId, StringComparison.OrdinalIgnoreCase)));
        if (existing is null)
        {
            _store.Add(link);
            return Task.FromResult<SmartLink?>(link);
        }

        var upgrade = SmartLinkRelationships.Rank(link.Relationship) > SmartLinkRelationships.Rank(existing.Relationship);
        var restore = restoreDismissed && existing.IsDismissed;
        if (!upgrade && !restore)
            return Task.FromResult<SmartLink?>(null);

        if (upgrade)
            existing.Relationship = link.Relationship;
        if (restore)
            existing.IsDismissed = false;
        return Task.FromResult<SmartLink?>(existing);
    }

    public Task<int> InsertMissingSourceLinksAsync(string? sessionId, CancellationToken ct) => Task.FromResult(0);

    public Task<IReadOnlyList<SmartLink>> ListDueForEnrichmentAsync(string checkedBefore, int limit, CancellationToken ct)
    {
        IReadOnlyList<SmartLink> result = [.. _store
            .Where(l => !l.IsDismissed
                && (l.EnrichmentStatus is SmartLinkEnrichmentStatuses.Pending or SmartLinkEnrichmentStatuses.NotConnected
                    || l.LastCheckedAt is null
                    || (!l.IsTerminal && string.CompareOrdinal(l.LastCheckedAt, checkedBefore) < 0)))
            .Take(limit)];
        return Task.FromResult(result);
    }

    public Task UpdateEnrichmentAsync(SmartLink link, CancellationToken ct)
    {
        var index = _store.FindIndex(l => l.Id == link.Id);
        if (index >= 0)
            _store[index] = link;
        return Task.CompletedTask;
    }

    public Task<IReadOnlyList<SmartLinkBranchTarget>> ListBranchTargetsAsync(CancellationToken ct)
        => Task.FromResult<IReadOnlyList<SmartLinkBranchTarget>>([]);

    public Task DeleteBySessionIdAsync(string sessionId)
    {
        _store.RemoveAll(l => l.SessionId == sessionId);
        return Task.CompletedTask;
    }

    public Task DeleteBySessionIdAsync(IDbConnection connection, IDbTransaction? transaction, string sessionId)
        => DeleteBySessionIdAsync(sessionId);

    public Task DeleteOrphanedAsync(CancellationToken ct) => Task.CompletedTask;
}
