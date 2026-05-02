using System.Data;
using WeaveFleet.Domain.Entities;

namespace WeaveFleet.Domain.Repositories;

public interface ISessionRepository
{
    Task InsertAsync(Session session);
    Task InsertAsync(IDbConnection connection, IDbTransaction? transaction, Session session);
    Task<Session?> GetByIdAsync(string id);
    Task<Session?> GetByHarnessIdAsync(string harnessSessionId);
    Task<IReadOnlyList<Session>> ListAsync(int limit = 100, int offset = 0, IReadOnlyList<string>? statuses = null, string? projectId = null);
    Task<IReadOnlyList<Session>> ListAsync(int limit, int offset, IReadOnlyList<string>? statuses, string? projectId, IReadOnlyList<string>? retentionStatuses);
    Task DeleteByProjectIdAsync(string projectId);
    Task<int> CountAsync(IReadOnlyList<string>? statuses = null);
    Task<int> CountAsync(IReadOnlyList<string>? statuses, IReadOnlyList<string>? retentionStatuses);
    Task<(int Active, int Idle)> GetStatusCountsAsync();
    Task<IReadOnlyList<Session>> ListActiveAsync();
    Task<IReadOnlyList<Session>> ListActiveAsync(IReadOnlyList<string>? retentionStatuses);
    Task UpdateStatusAsync(string id, string status, string? stoppedAt = null);
    Task UpdateStatusAsync(IDbConnection connection, IDbTransaction? transaction, string id, string status, string? stoppedAt);
    Task ArchiveAsync(string id, string archivedAt);
    Task ArchiveAsync(IDbConnection connection, IDbTransaction? transaction, string id, string archivedAt);
    Task UnarchiveAsync(string id);
    Task UnarchiveAsync(IDbConnection connection, IDbTransaction? transaction, string id);
    Task<IReadOnlyList<Session>> GetForInstanceAsync(string instanceId);
    Task<Session?> GetAnyForInstanceAsync(string instanceId);
    Task<IReadOnlyList<Session>> GetNonTerminalForInstanceAsync(string instanceId);
    Task UpdateTitleAsync(string id, string title);
    Task UpdateForResumeAsync(string id, string instanceId);
    Task UpdateResumeTokenAsync(string id, string resumeToken);
    Task<IReadOnlyList<Session>> GetActiveChildrenAsync(string parentDbId);
    Task<IReadOnlySet<string>> GetIdsWithActiveChildrenAsync();
    Task<IReadOnlyList<Session>> GetForWorkspaceAsync(string workspaceId);
    Task<IReadOnlyList<Session>> GetForWorkspaceAsync(string workspaceId, IReadOnlyList<string>? retentionStatuses);
    Task<bool> DeleteAsync(string id);
    Task<bool> DeleteAsync(IDbConnection connection, IDbTransaction? transaction, string id);
    Task<(int TotalTokens, double TotalCost)?> IncrementTokensAsync(string id, int tokens, double cost);
    Task<(int TotalTokens, double TotalCost)> GetFleetTokenTotalsAsync();
    Task<int> MarkAllNonTerminalStoppedAsync(string stoppedAt);
    Task UpdateProjectAsync(string id, string? projectId);
    /// <summary>
    /// Persist the most-recent model selection used on this session, so a SPA refresh
    /// (which loses local state) can fall back to it on the next prompt.
    /// </summary>
    Task UpdateSelectedModelAsync(string id, string providerId, string modelId);
}
