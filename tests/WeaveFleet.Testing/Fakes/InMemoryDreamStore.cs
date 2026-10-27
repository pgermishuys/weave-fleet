using WeaveFleet.Application.Memory.Dreaming;

namespace WeaveFleet.Testing.Fakes;

public sealed class InMemoryDreamStore : IDreamStore
{
    private readonly Dictionary<string, Dream> _dreams = new();
    private readonly Lock _gate = new();

    public IReadOnlyList<Dream> All
    {
        get
        {
            lock (_gate)
                return [.. _dreams.Values];
        }
    }

    public Task<IReadOnlyList<Dream>> ListAsync(string userId, string repository, int limit, CancellationToken ct = default)
        => Task.FromResult<IReadOnlyList<Dream>>([.. All.Where(d => d.Repository == repository).OrderByDescending(d => d.Started).Take(limit)]);

    public Task<Dream?> FindAsync(string userId, string id, CancellationToken ct = default)
    {
        lock (_gate)
            return Task.FromResult(_dreams.GetValueOrDefault(id));
    }

    public Task SaveAsync(string userId, Dream dream, CancellationToken ct = default)
    {
        lock (_gate)
            _dreams[dream.Id] = dream;
        return Task.CompletedTask;
    }

    public Task<IReadOnlyList<Dream>> ListSinceAsync(string userId, DateTimeOffset since, int limit, CancellationToken ct = default)
        => Task.FromResult<IReadOnlyList<Dream>>([.. All.Where(d => d.Started >= since).OrderByDescending(d => d.Started).Take(limit)]);
}
