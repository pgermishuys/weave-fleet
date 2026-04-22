using WeaveFleet.Domain.Entities;
using WeaveFleet.Domain.Repositories;

namespace WeaveFleet.Testing.Fakes.Repositories;

public sealed class InMemorySessionSourceUsageRepository : ISessionSourceUsageRepository
{
    private readonly List<SessionSourceUsage> _store = [];

    // ── Seeding API ──────────────────────────────────────────────────────────

    public void Seed(SessionSourceUsage usage) => _store.Add(usage);

    // ── Inspection API ───────────────────────────────────────────────────────

    public IReadOnlyList<SessionSourceUsage> All => [.. _store];

    // ── ISessionSourceUsageRepository ────────────────────────────────────────

    public Task InsertAsync(SessionSourceUsage usage)
    {
        _store.Add(usage);
        return Task.CompletedTask;
    }

    public Task<SessionSourceUsage?> GetPrimaryBySessionIdAsync(string sessionId)
    {
        var result = _store
            .Where(usage => usage.SessionId == sessionId && usage.ActionId == "start-session")
            .OrderBy(usage => usage.CreatedAt)
            .FirstOrDefault();

        return Task.FromResult(result);
    }

    public Task<IReadOnlyDictionary<string, SessionSourceUsage>> GetPrimaryBySessionIdsAsync(IReadOnlyCollection<string> sessionIds)
    {
        IReadOnlyDictionary<string, SessionSourceUsage> result = _store
            .Where(usage => sessionIds.Contains(usage.SessionId) && usage.ActionId == "start-session")
            .OrderBy(usage => usage.CreatedAt)
            .GroupBy(usage => usage.SessionId)
            .ToDictionary(group => group.Key, group => group.First());

        return Task.FromResult(result);
    }

    public Task<IReadOnlyList<SessionSourceUsage>> ListBySessionIdAsync(string sessionId)
    {
        IReadOnlyList<SessionSourceUsage> result = [.. _store.Where(u => u.SessionId == sessionId)];
        return Task.FromResult(result);
    }
}
