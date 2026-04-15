using WeaveFleet.Domain.Entities;
using WeaveFleet.Domain.Repositories;

namespace WeaveFleet.Testing.Fakes.Repositories;

public sealed class InMemoryProjectRepository : IProjectRepository
{
    private readonly Dictionary<string, Project> _store = new();
    private readonly Dictionary<string, int> _sessionCounts = new();

    // ── Seeding API ──────────────────────────────────────────────────────────

    public void Seed(Project project) => _store[project.Id] = project;

    public void Seed(params Project[] projects)
    {
        foreach (var p in projects)
            Seed(p);
    }

    public void SeedSessionCount(string projectId, int count) => _sessionCounts[projectId] = count;

    // ── Inspection API ───────────────────────────────────────────────────────

    public IReadOnlyList<Project> All => [.. _store.Values];

    // ── IProjectRepository ───────────────────────────────────────────────────

    public Task<Project?> GetByIdAsync(string id)
        => Task.FromResult(_store.GetValueOrDefault(id));

    public Task<Project?> GetScratchProjectAsync()
        => Task.FromResult(_store.Values.FirstOrDefault(p => p.Type == "scratch"));

    public Task<IReadOnlyList<Project>> ListAsync()
    {
        IReadOnlyList<Project> result = [.. _store.Values.OrderBy(p => p.Position)];
        return Task.FromResult(result);
    }

    public Task InsertAsync(Project project)
    {
        _store[project.Id] = project;
        return Task.CompletedTask;
    }

    public Task UpdateAsync(Project project)
    {
        _store[project.Id] = project;
        return Task.CompletedTask;
    }

    public Task DeleteAsync(string id)
    {
        _store.Remove(id);
        return Task.CompletedTask;
    }

    public Task ReorderAsync(string id, int newPosition)
    {
        if (_store.TryGetValue(id, out var project))
            project.Position = newPosition;
        return Task.CompletedTask;
    }

    public Task<int> GetSessionCountAsync(string projectId)
    {
        _sessionCounts.TryGetValue(projectId, out var count);
        return Task.FromResult(count);
    }

    public Task MoveSessionsToProjectAsync(string fromProjectId, string toProjectId)
    {
        // In-memory: no sessions tracked here; this is a no-op for the fake
        return Task.CompletedTask;
    }
}
