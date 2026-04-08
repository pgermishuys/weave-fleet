using WeaveFleet.Domain.Entities;

namespace WeaveFleet.Domain.Repositories;

public interface IDelegationRepository
{
    Task InsertAsync(Delegation delegation);
    Task<Delegation?> GetByIdAsync(string id);
    Task<IReadOnlyList<Delegation>> GetByParentSessionIdAsync(string parentSessionId);
    Task<Delegation?> GetByChildSessionIdAsync(string childSessionId);
    Task<Delegation?> GetByParentToolCallIdAsync(string parentSessionId, string toolCallId);
    Task UpdateStatusAsync(string id, string status, string updatedAt, string? completedAt);
    Task UpdateChildSessionIdAsync(string id, string childSessionId, string updatedAt);
}
