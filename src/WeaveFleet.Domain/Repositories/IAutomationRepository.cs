using WeaveFleet.Domain.Entities;

namespace WeaveFleet.Domain.Repositories;

public interface IAutomationRepository
{
    Task InsertAsync(Automation automation);
    Task UpdateAsync(Automation automation);
    Task<Automation?> GetByIdAsync(string id);
    /// <summary>The current user's built-in automation of that name (<see cref="Automation.Builtin"/>), unless deleted.</summary>
    Task<Automation?> GetBuiltinAsync(string builtin);
    Task<IReadOnlyList<Automation>> ListAsync(string? workspaceId = null);
    Task<IReadOnlyList<Automation>> ListEnabledByTriggerTypeAsync(string triggerType);
    Task DeleteAsync(string id);
    Task SetEnabledAsync(string id, bool enabled);
}
