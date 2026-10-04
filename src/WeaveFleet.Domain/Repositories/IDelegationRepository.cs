using System.Data;
using WeaveFleet.Domain.Entities;

namespace WeaveFleet.Domain.Repositories;

public interface IDelegationRepository
{
    Task InsertAsync(Delegation delegation);
    Task InsertAsync(IDbConnection connection, IDbTransaction? transaction, Delegation delegation);
    Task<Delegation?> GetByIdAsync(string id);
    Task<IReadOnlyList<Delegation>> GetByParentSessionIdAsync(string parentSessionId);
    Task<Delegation?> GetByChildSessionIdAsync(string childSessionId);
    Task<Delegation?> GetByParentToolCallIdAsync(string parentSessionId, string toolCallId);

    /// <summary>The work session <paramref name="parentSessionId"/> runs under the harness's handle <paramref name="workId"/>.</summary>
    Task<Delegation?> GetByWorkIdAsync(string parentSessionId, string workId);

    /// <summary>The current user's work still running, in every session, oldest first.</summary>
    Task<IReadOnlyList<Delegation>> ListRunningAsync();

    /// <summary>How much work still runs in each of <paramref name="parentSessionIds"/>; sessions with none are left out.</summary>
    Task<IReadOnlyDictionary<string, int>> CountRunningAsync(IReadOnlyCollection<string> parentSessionIds);

    /// <summary>Saves what a harness said about the work: its kind, title, label, and what can be done with it.</summary>
    Task UpdateWorkAsync(Delegation delegation);

    /// <summary>Records that the work ended: its status, <paramref name="endedReason"/> and <paramref name="detail"/>.</summary>
    Task EndAsync(string id, string status, string endedReason, string? detail, string endedAt);
    Task UpdateStatusAsync(string id, string status, string updatedAt, string? completedAt);
    Task UpdateStatusAsync(IDbConnection connection, IDbTransaction? transaction, string id, string status, string updatedAt, string? completedAt);
    Task UpdateChildSessionIdAsync(string id, string? childSessionId, string updatedAt);
    Task UpdateChildSessionIdAsync(IDbConnection connection, IDbTransaction? transaction, string id, string? childSessionId, string updatedAt);
    Task DeleteByParentSessionIdAsync(string parentSessionId);

    /// <summary>
    /// Recovery, for every user: ends the work still pending or running as cancelled, lost
    /// (<see cref="WeaveFleet.Domain.Harnesses.WorkEndedReasons.Lost"/>). Called at startup, when no turn or background
    /// work from the previous run can still be going. Returns how many were ended.
    /// </summary>
    Task<int> CancelAllUnfinishedAsync(string completedAt);

    /// <summary>
    /// Session <paramref name="parentSessionId"/>'s work that ended lost (<see cref="WeaveFleet.Domain.Harnesses.WorkEndedReasons.Lost"/>)
    /// that its agent hasn't been told about yet, oldest first.
    /// </summary>
    Task<IReadOnlyList<Delegation>> GetUnreportedLostAsync(string parentSessionId);

    /// <summary>Records that the agent was told work <paramref name="ids"/> was lost, so it isn't told again.</summary>
    Task MarkLostReportedAsync(IReadOnlyCollection<string> ids, string reportedAt);
    Task DeleteByParentSessionIdAsync(IDbConnection connection, IDbTransaction? transaction, string parentSessionId);
}
