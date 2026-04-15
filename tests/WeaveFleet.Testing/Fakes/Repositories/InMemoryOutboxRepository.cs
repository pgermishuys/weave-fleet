using System.Data;
using WeaveFleet.Domain.Entities;
using WeaveFleet.Domain.Repositories;

namespace WeaveFleet.Testing.Fakes.Repositories;

public sealed class InMemoryOutboxRepository : IOutboxRepository
{
    private readonly Dictionary<long, OutboxMessage> _store = new();
    private long _nextId = 1;

    // ── Seeding API ──────────────────────────────────────────────────────────

    public void Seed(OutboxMessage message)
    {
        if (message.Id == 0)
            message.Id = _nextId++;
        _store[message.Id] = message;
    }

    // ── Inspection API ───────────────────────────────────────────────────────

    public IReadOnlyList<OutboxMessage> All => [.. _store.Values];

    // ── IOutboxRepository ────────────────────────────────────────────────────

    public Task<long> EnqueueAsync(OutboxMessage message)
    {
        var id = _nextId++;
        message.Id = id;
        _store[id] = message;
        return Task.FromResult(id);
    }

    public Task<long> EnqueueAsync(IDbConnection connection, IDbTransaction? transaction, OutboxMessage message)
        => EnqueueAsync(message);

    public Task<IReadOnlyList<OutboxMessage>> GetUndispatchedAsync(int limit)
    {
        IReadOnlyList<OutboxMessage> result = [.. _store.Values.Where(m => m.DispatchedAt is null).Take(limit)];
        return Task.FromResult(result);
    }

    public Task<IReadOnlyList<OutboxMessage>> GetByTopicAfterAsync(string topic, long sequenceNumber, int limit)
    {
        IReadOnlyList<OutboxMessage> result = [.. _store.Values
            .Where(m => m.Topic == topic && m.Id > sequenceNumber)
            .OrderBy(m => m.Id)
            .Take(limit)];
        return Task.FromResult(result);
    }

    public Task MarkDispatchedAsync(IReadOnlyList<long> ids, string dispatchedAt)
    {
        foreach (var id in ids)
        {
            if (_store.TryGetValue(id, out var message))
                message.DispatchedAt = dispatchedAt;
        }
        return Task.CompletedTask;
    }

    public Task<int> DeleteDispatchedBeforeAsync(string dispatchedBefore, int limit)
    {
        var toDelete = _store.Values
            .Where(m => m.DispatchedAt is not null && string.Compare(m.DispatchedAt, dispatchedBefore, StringComparison.Ordinal) < 0)
            .Take(limit)
            .Select(m => m.Id)
            .ToList();
        foreach (var id in toDelete)
            _store.Remove(id);
        return Task.FromResult(toDelete.Count);
    }
}
