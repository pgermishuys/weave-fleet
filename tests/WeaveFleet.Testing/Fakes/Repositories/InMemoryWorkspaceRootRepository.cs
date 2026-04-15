using WeaveFleet.Domain.Entities;
using WeaveFleet.Domain.Repositories;

namespace WeaveFleet.Testing.Fakes.Repositories;

public sealed class InMemoryWorkspaceRootRepository : IWorkspaceRootRepository
{
    private readonly Dictionary<string, WorkspaceRoot> _store = new();

    // ── Seeding API ──────────────────────────────────────────────────────────

    public void Seed(WorkspaceRoot root) => _store[root.Id] = root;

    public void Seed(params WorkspaceRoot[] roots)
    {
        foreach (var r in roots)
            Seed(r);
    }

    public void Clear() => _store.Clear();

    // ── Inspection API ───────────────────────────────────────────────────────

    public IReadOnlyList<WorkspaceRoot> All => [.. _store.Values];

    // ── IWorkspaceRootRepository ─────────────────────────────────────────────

    public Task InsertAsync(WorkspaceRoot root)
    {
        _store[root.Id] = root;
        return Task.CompletedTask;
    }

    public Task<IReadOnlyList<WorkspaceRoot>> ListAsync()
    {
        IReadOnlyList<WorkspaceRoot> result = [.. _store.Values];
        return Task.FromResult(result);
    }

    public Task<bool> DeleteAsync(string id)
    {
        var removed = _store.Remove(id);
        return Task.FromResult(removed);
    }

    public Task<WorkspaceRoot?> GetByPathAsync(string path)
        => Task.FromResult(_store.Values.FirstOrDefault(r => r.Path == path));
}
