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

    public Task<IReadOnlyList<SessionCallback>> GetPendingForSessionAsync(string sourceSessionId)
    {
        IReadOnlyList<SessionCallback> result = [.. _store.Values.Where(c => c.SourceSessionId == sourceSessionId && c.Status == "pending")];
        return Task.FromResult(result);
    }

    public Task MarkFiredAsync(string id)
    {
        if (_store.TryGetValue(id, out var callback))
        {
            callback.Status = "fired";
            callback.FiredAt = DateTimeOffset.UtcNow.ToString("O");
        }
        return Task.CompletedTask;
    }

    public Task<bool> ClaimPendingAsync(string id)
    {
        if (_store.TryGetValue(id, out var callback) && callback.Status == "pending")
        {
            callback.Status = "claimed";
            return Task.FromResult(true);
        }
        return Task.FromResult(false);
    }

    public Task<IReadOnlyList<SessionCallback>> GetAllPendingAsync()
    {
        IReadOnlyList<SessionCallback> result = [.. _store.Values.Where(c => c.Status == "pending")];
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
