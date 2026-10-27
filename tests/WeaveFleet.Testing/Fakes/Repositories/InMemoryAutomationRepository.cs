using WeaveFleet.Domain.Entities;
using WeaveFleet.Domain.Repositories;

namespace WeaveFleet.Testing.Fakes.Repositories;

public sealed class InMemoryAutomationRepository : IAutomationRepository
{
    private readonly Dictionary<string, Automation> _store = new();

    public void Seed(Automation automation) => _store[automation.Id] = automation;

    public IReadOnlyList<Automation> All => [.. _store.Values];

    public Task InsertAsync(Automation automation)
    {
        _store[automation.Id] = automation;
        return Task.CompletedTask;
    }

    public Task UpdateAsync(Automation automation)
    {
        _store[automation.Id] = automation;
        return Task.CompletedTask;
    }

    public Task<Automation?> GetByIdAsync(string id) => Task.FromResult(_store.GetValueOrDefault(id));

    public Task<Automation?> GetBuiltinAsync(string builtin)
        => Task.FromResult(_store.Values.Where(a => a.Builtin == builtin && !a.IsDeleted).OrderByDescending(a => a.CreatedAt, StringComparer.Ordinal).FirstOrDefault());

    public Task<IReadOnlyList<Automation>> ListAsync(string? workspaceId = null)
        => Task.FromResult<IReadOnlyList<Automation>>([.. _store.Values.Where(a => !a.IsDeleted && (workspaceId is null || a.WorkspaceId == workspaceId))]);

    public Task<IReadOnlyList<Automation>> ListEnabledByTriggerTypeAsync(string triggerType)
        => Task.FromResult<IReadOnlyList<Automation>>([.. _store.Values.Where(a => a.IsEnabled && !a.IsDeleted && a.TriggerType == triggerType)]);

    public Task DeleteAsync(string id)
    {
        if (_store.TryGetValue(id, out var automation))
            automation.IsDeleted = true;
        return Task.CompletedTask;
    }

    public Task SetEnabledAsync(string id, bool enabled)
    {
        if (_store.TryGetValue(id, out var automation))
            automation.IsEnabled = enabled;
        return Task.CompletedTask;
    }
}
