using System.Data;
using WeaveFleet.Domain.Entities;
using WeaveFleet.Domain.Repositories;

namespace WeaveFleet.Testing.Fakes.Repositories;

public sealed class InMemoryHarnessEventLogRepository : IHarnessEventLogRepository
{
    private readonly Dictionary<(string SessionId, long SequenceNumber), HarnessEventLogEntry> _store = new();
    private long _nextId = 1;

    public IReadOnlyList<HarnessEventLogEntry> All => [.. _store.Values];

    public Task<long> AppendAsync(HarnessEventLogEntry entry)
        => AppendAsync(connection: null!, transaction: null, entry);

    public Task<long> AppendAsync(IDbConnection connection, IDbTransaction? transaction, HarnessEventLogEntry entry)
    {
        var key = (entry.SessionId, entry.SequenceNumber);
        if (_store.TryGetValue(key, out var existing))
            return Task.FromResult(existing.Id);

        entry.Id = _nextId++;
        _store[key] = entry;
        return Task.FromResult(entry.Id);
    }

    public Task<IReadOnlyList<HarnessEventLogEntry>> GetBySessionAfterAsync(string sessionId, long afterSequenceNumber, int limit)
    {
        IReadOnlyList<HarnessEventLogEntry> result = [.. _store.Values
            .Where(e => e.SessionId == sessionId && e.SequenceNumber > afterSequenceNumber)
            .OrderBy(e => e.SequenceNumber)
            .Take(limit)];
        return Task.FromResult(result);
    }
}
