using System.Data;
using WeaveFleet.Domain.Entities;

namespace WeaveFleet.Domain.Repositories;

public interface ISessionRepository
{
    Task InsertAsync(Session session);
    Task InsertAsync(IDbConnection connection, IDbTransaction? transaction, Session session);
    Task<Session?> GetByIdAsync(string id);
    Task<Session?> GetByHarnessIdAsync(string harnessSessionId);

    /// <summary>The side conversation open on session <paramref name="sessionId"/> (<c>/btw</c>), if there is one.</summary>
    Task<Session?> GetSideConversationAsync(string sessionId);

    /// <summary>The side conversation of session <paramref name="sessionId"/> discarded most recently, still in its undo window.</summary>
    Task<Session?> GetDiscardedSideConversationAsync(string sessionId);

    /// <summary>Every side conversation of session <paramref name="sessionId"/>, discarded ones included.</summary>
    Task<IReadOnlyList<Session>> ListSideConversationsAsync(string sessionId);

    /// <summary>Every owner's side conversations discarded before <paramref name="cutoff"/> (ISO 8601): past their undo window.</summary>
    Task<IReadOnlyList<Session>> ListSideConversationsDiscardedBeforeAsync(string cutoff);

    /// <summary>Sets whether side conversation <paramref name="id"/> is minimized, and when it was discarded (null: it wasn't).</summary>
    Task SetSideConversationStateAsync(string id, bool minimized, string? discardedAt);

    /// <summary>
    /// Makes side conversation <paramref name="id"/> a session of its own, listed like any other, in workspace
    /// <paramref name="workspaceId"/>.
    /// </summary>
    Task KeepSideConversationAsync(string id, string workspaceId);
    Task<IReadOnlyList<Session>> ListAsync(int limit = 100, int offset = 0, IReadOnlyList<string>? statuses = null, string? projectId = null);
    Task<IReadOnlyList<Session>> ListAsync(int limit, int offset, IReadOnlyList<string>? statuses, string? projectId, IReadOnlyList<string>? retentionStatuses);
    Task<IReadOnlyList<Session>> ListAsync(int limit, int offset, IReadOnlyList<string>? statuses, string? projectId, IReadOnlyList<string>? retentionStatuses, IReadOnlyList<string>? tags);
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
    /// <summary>Maps active child sessions to their parents, leaving out children whose delegations have all finished.</summary>
    Task<IReadOnlyDictionary<string, string>> GetActiveChildToParentMappingAsync();
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
    /// <summary>
    /// Record the agent a session was prompted with by name, so prompts that name none go to it too.
    /// </summary>
    Task UpdateSelectedAgentAsync(string id, string agent);
    /// <summary>
    /// Update the tags for a session.
    /// </summary>
    Task UpdateTagsAsync(string id, List<string> tags);
}
