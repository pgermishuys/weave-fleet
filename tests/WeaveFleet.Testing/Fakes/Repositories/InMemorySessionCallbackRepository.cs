using WeaveFleet.Domain.Entities;
using WeaveFleet.Domain.Repositories;

namespace WeaveFleet.Testing.Fakes.Repositories;

public sealed class InMemorySessionCallbackRepository : ISessionCallbackRepository
{
    private readonly Dictionary<string, SessionCallback> _store = new();

    // ── Seeding API ──────────────────────────────────────────────────────────

    public void Seed(SessionCallback callback) => _store[callback.Id] = callback;

    // ── Inspection API ───────────────────────────────────────────────────────

    public IReadOnlyList<SessionCallback> All => [.. _store.Values];

    // ── ISessionCallbackRepository ───────────────────────────────────────────

    public Task InsertAsync(SessionCallback callback)
    {
        _store[callback.Id] = callback;
        return Task.CompletedTask;
    }

    public Task<int> MarkSourceStartedAsync(string sourceSessionId)
    {
        var moved = 0;
        foreach (var callback in _store.Values.Where(c => c.SourceSessionId == sourceSessionId && c.Status == SessionCallbackStatuses.Pending))
        {
            callback.Status = SessionCallbackStatuses.Started;
            moved++;
        }
        return Task.FromResult(moved);
    }

    public Task<IReadOnlyList<SessionCallback>> GetStartedAsync()
    {
        IReadOnlyList<SessionCallback> result = [.. _store.Values.Where(c => c.Status == SessionCallbackStatuses.Started).OrderBy(c => c.CreatedAt, StringComparer.Ordinal)];
        return Task.FromResult(result);
    }

    public Task<bool> MarkFiredAsync(string id)
    {
        if (!_store.TryGetValue(id, out var callback) || callback.Status != SessionCallbackStatuses.Started)
            return Task.FromResult(false);

        callback.Status = SessionCallbackStatuses.Fired;
        callback.FiredAt = DateTimeOffset.UtcNow.ToString("O");
        return Task.FromResult(true);
    }

    /// <summary>The owners the poll should act for. The fake doesn't know sessions' owners, so tests set them.</summary>
    public List<string> Owners { get; } = [];

    public Task<IReadOnlyList<string>> GetOwnersWithStartedCallbacksAsync()
    {
        IReadOnlyList<string> result = [.. Owners];
        return Task.FromResult(result);
    }

    public Task<int> DeleteForSessionAsync(string sessionId)
    {
        var ids = _store.Values
            .Where(c => c.SourceSessionId == sessionId || c.TargetSessionId == sessionId)
            .Select(c => c.Id)
            .ToList();
        foreach (var id in ids)
            _store.Remove(id);
        return Task.FromResult(ids.Count);
    }
}
