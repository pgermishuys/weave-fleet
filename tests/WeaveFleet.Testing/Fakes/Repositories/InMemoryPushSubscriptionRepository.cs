using System.Collections.Concurrent;
using WeaveFleet.Domain.Entities;
using WeaveFleet.Domain.Repositories;

namespace WeaveFleet.Testing.Fakes.Repositories;

/// <summary>Push subscriptions in memory, keyed by endpoint.</summary>
public sealed class InMemoryPushSubscriptionRepository : IPushSubscriptionRepository
{
    private readonly ConcurrentDictionary<string, PushSubscriptionRecord> _store = new(StringComparer.Ordinal);

    public IReadOnlyList<PushSubscriptionRecord> All => [.. _store.Values.OrderBy(s => s.CreatedAt)];

    public Task<IReadOnlyList<PushSubscriptionRecord>> ListAsync() => Task.FromResult(All);

    public Task<PushSubscriptionRecord?> GetByEndpointAsync(string endpoint) => Task.FromResult(_store.GetValueOrDefault(endpoint));

    public Task<PushSubscriptionRecord> UpsertAsync(PushSubscriptionRecord subscription)
    {
        var saved = _store.AddOrUpdate(
            subscription.Endpoint,
            subscription,
            (_, existing) => subscription with { Id = existing.Id, CreatedAt = existing.CreatedAt, FailureCount = 0 });
        return Task.FromResult(saved);
    }

    public Task<bool> DeleteByEndpointAsync(string endpoint) => Task.FromResult(_store.TryRemove(endpoint, out _));

    public Task<int> DeleteByDeviceAsync(string deviceId)
    {
        var gone = _store.Values.Where(s => s.DeviceId == deviceId).ToList();
        foreach (var subscription in gone)
            _store.TryRemove(subscription.Endpoint, out _);
        return Task.FromResult(gone.Count);
    }

    public Task RecordSuccessAsync(string id, DateTimeOffset at)
    {
        Update(id, s => s with { LastSuccessAt = at, FailureCount = 0 });
        return Task.CompletedTask;
    }

    public Task<int> RecordFailureAsync(string id)
    {
        var updated = Update(id, s => s with { FailureCount = s.FailureCount + 1 });
        return Task.FromResult(updated?.FailureCount ?? 0);
    }

    private PushSubscriptionRecord? Update(string id, Func<PushSubscriptionRecord, PushSubscriptionRecord> change)
    {
        var current = _store.Values.FirstOrDefault(s => s.Id == id);
        if (current is null)
            return null;
        var next = change(current);
        _store[current.Endpoint] = next;
        return next;
    }
}
