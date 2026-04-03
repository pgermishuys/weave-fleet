using WeaveFleet.Domain.Entities;

namespace WeaveFleet.Domain.Repositories;

public interface ISessionRepository
{
    Task InsertAsync(Session session);
    Task<Session?> GetByIdAsync(string id);
    Task<Session?> GetByHarnessIdAsync(string harnessSessionId);
    Task<IReadOnlyList<Session>> ListAsync(int limit = 100, int offset = 0, IReadOnlyList<string>? statuses = null, string? projectId = null);
    Task DeleteByProjectIdAsync(string projectId);
    Task<int> CountAsync(IReadOnlyList<string>? statuses = null);
    Task<(int Active, int Idle)> GetStatusCountsAsync();
    Task<IReadOnlyList<Session>> ListActiveAsync();
    Task UpdateStatusAsync(string id, string status, string? stoppedAt = null);
    Task<IReadOnlyList<Session>> GetForInstanceAsync(string instanceId);
    Task<Session?> GetAnyForInstanceAsync(string instanceId);
    Task<IReadOnlyList<Session>> GetNonTerminalForInstanceAsync(string instanceId);
    Task UpdateTitleAsync(string id, string title);
    Task UpdateForResumeAsync(string id, string instanceId);
    Task<IReadOnlyList<Session>> GetActiveChildrenAsync(string parentDbId);
    Task<IReadOnlySet<string>> GetIdsWithActiveChildrenAsync();
    Task<IReadOnlyList<Session>> GetForWorkspaceAsync(string workspaceId);
    Task<bool> DeleteAsync(string id);
    Task<(int TotalTokens, double TotalCost)?> IncrementTokensAsync(string id, int tokens, double cost);
    Task<(int TotalTokens, double TotalCost)> GetFleetTokenTotalsAsync();
    Task<int> MarkAllNonTerminalStoppedAsync(string stoppedAt);
    Task UpdateProjectAsync(string id, string? projectId);
}
