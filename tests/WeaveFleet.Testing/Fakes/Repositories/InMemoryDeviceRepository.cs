using System.Collections.Concurrent;
using WeaveFleet.Domain.Entities;
using WeaveFleet.Domain.Repositories;

namespace WeaveFleet.Testing.Fakes.Repositories;

/// <summary>Paired devices in memory. Counts reads and touches so tests can see what the cache saved.</summary>
public sealed class InMemoryDeviceRepository : IDeviceRepository
{
    private readonly ConcurrentDictionary<string, Device> _store = new(StringComparer.Ordinal);

    private int _reads;
    private int _touches;

    public int Reads => _reads;
    public int Touches => _touches;

    public IReadOnlyList<Device> All => [.. _store.Values];

    public Task<Device?> GetAsync(string id)
    {
        Interlocked.Increment(ref _reads);
        return Task.FromResult(_store.GetValueOrDefault(id));
    }

    public Task<IReadOnlyList<Device>> ListActiveAsync() =>
        Task.FromResult<IReadOnlyList<Device>>(_store.Values.Where(d => d.RevokedAt is null).OrderBy(d => d.CreatedAt).ToList());

    public Task InsertAsync(Device device)
    {
        _store[device.Id] = device;
        return Task.CompletedTask;
    }

    public Task TouchAsync(string id, DateTimeOffset lastUsedAt)
    {
        Interlocked.Increment(ref _touches);
        if (_store.TryGetValue(id, out var device) && device.RevokedAt is null)
            _store[id] = device with { LastUsedAt = lastUsedAt };
        return Task.CompletedTask;
    }

    public Task<bool> RevokeAsync(string id, DateTimeOffset at)
    {
        if (!_store.TryGetValue(id, out var device) || device.RevokedAt is not null)
            return Task.FromResult(false);
        _store[id] = device with { RevokedAt = at };
        return Task.FromResult(true);
    }
}
