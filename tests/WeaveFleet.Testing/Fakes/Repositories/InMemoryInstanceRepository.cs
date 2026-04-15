using WeaveFleet.Domain.Entities;
using WeaveFleet.Domain.Repositories;

namespace WeaveFleet.Testing.Fakes.Repositories;

public sealed class InMemoryInstanceRepository : IInstanceRepository
{
    private readonly Dictionary<string, Instance> _store = new();

    // ── Seeding API ──────────────────────────────────────────────────────────

    public void Seed(Instance instance) => _store[instance.Id] = instance;

    // ── Inspection API ───────────────────────────────────────────────────────

    public IReadOnlyList<Instance> All => [.. _store.Values];

    // ── Configurable behaviors ───────────────────────────────────────────────

    /// <summary>
    /// Optional override for <see cref="GetByIdAsync"/>. When set, called instead of the in-memory store.
    /// Supports dynamic return values based on the id argument.
    /// </summary>
    public Func<string, Task<Instance?>>? GetByIdBehavior { get; set; }

    // ── IInstanceRepository ──────────────────────────────────────────────────

    public Task InsertAsync(Instance instance)
    {
        _store[instance.Id] = instance;
        return Task.CompletedTask;
    }

    public Task<Instance?> GetByIdAsync(string id)
    {
        if (GetByIdBehavior is not null)
            return GetByIdBehavior(id);
        return Task.FromResult(_store.GetValueOrDefault(id));
    }

    public Task<Instance?> GetByDirectoryAsync(string directory)
        => Task.FromResult(_store.Values.FirstOrDefault(i => i.Directory == directory));

    public Task<IReadOnlyList<Instance>> ListAsync()
    {
        IReadOnlyList<Instance> result = [.. _store.Values];
        return Task.FromResult(result);
    }

    public Task UpdateStatusAsync(string id, string status, string? stoppedAt = null)
    {
        if (_store.TryGetValue(id, out var instance))
        {
            instance.Status = status;
            if (stoppedAt is not null)
                instance.StoppedAt = stoppedAt;
        }
        return Task.CompletedTask;
    }

    public Task<IReadOnlyList<Instance>> GetRunningAsync()
    {
        IReadOnlyList<Instance> result = [.. _store.Values.Where(i => i.Status == "running")];
        return Task.FromResult(result);
    }

    public Task<int> MarkAllStoppedAsync(string stoppedAt)
    {
        var count = 0;
        foreach (var instance in _store.Values.Where(i => i.Status == "running"))
        {
            instance.Status = "stopped";
            instance.StoppedAt = stoppedAt;
            count++;
        }
        return Task.FromResult(count);
    }
}
