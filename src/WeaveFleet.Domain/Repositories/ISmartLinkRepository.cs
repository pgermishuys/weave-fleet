using System.Data;
using WeaveFleet.Domain.Entities;

namespace WeaveFleet.Domain.Repositories;

public interface ISmartLinkRepository
{
    Task<IReadOnlyList<SmartLink>> ListBySessionIdAsync(string sessionId);
    Task<IReadOnlyList<SmartLink>> ListActiveBySessionIdAsync(string sessionId);
    Task DismissAsync(string id);

    /// <summary>
    /// Sets the relationship of a link owned by the current user. Returns <c>false</c> when no such link exists.
    /// </summary>
    Task<bool> SetRelationshipAsync(string id, string relationship);

    /// <summary>
    /// Clears the last-checked time on the current user's links for a session so the watcher refreshes them next.
    /// </summary>
    Task MarkSessionDueAsync(string sessionId);

    /// <summary>
    /// Inserts a newly found link, or upgrades the relationship of an existing one when the new relationship
    /// ranks higher. Ownership comes from <see cref="SmartLink.UserId"/>, guarded by the session's owner.
    /// When <paramref name="restoreDismissed"/> is set, a dismissed link is shown again.
    /// Returns the stored row when something changed, or <c>null</c> when the link was already known.
    /// </summary>
    Task<SmartLink?> InsertDetectedAsync(SmartLink link, bool restoreDismissed, CancellationToken ct);

    /// <summary>
    /// Adds links for GitHub sources recorded on sessions (start and add-to-session) that have no smart
    /// link yet: for <paramref name="sessionId"/> when given, otherwise for running sessions. Returns the
    /// number of links added or upgraded.
    /// </summary>
    Task<int> InsertMissingSourceLinksAsync(string? sessionId, CancellationToken ct);

    /// <summary>
    /// Returns links the watcher should check now: links still waiting for details, and open links on
    /// running sessions last checked before <paramref name="checkedBefore"/>. Not user-scoped.
    /// </summary>
    Task<IReadOnlyList<SmartLink>> ListDueForEnrichmentAsync(string checkedBefore, int limit, CancellationToken ct);

    /// <summary>Writes the watcher's results for a link. Not user-scoped.</summary>
    Task UpdateEnrichmentAsync(SmartLink link, CancellationToken ct);

    /// <summary>Returns running sessions whose workspace has a branch, for finding the session's pull request.</summary>
    Task<IReadOnlyList<SmartLinkBranchTarget>> ListBranchTargetsAsync(CancellationToken ct);

    /// <summary>
    /// Deletes all smart links belonging to a session.
    /// </summary>
    Task DeleteBySessionIdAsync(string sessionId);

    /// <summary>
    /// Deletes all smart links belonging to a session (transactional overload).
    /// </summary>
    Task DeleteBySessionIdAsync(IDbConnection connection, IDbTransaction? transaction, string sessionId);

    /// <summary>
    /// Deletes orphaned smart links whose sessions no longer exist.
    /// Used at startup for reconciliation.
    /// </summary>
    Task DeleteOrphanedAsync(CancellationToken ct);
}

/// <summary>A running session with a branch, used to look up the pull request opened from that branch.</summary>
public sealed record SmartLinkBranchTarget(
    string SessionId,
    string UserId,
    string Branch,
    string Directory,
    string? SourceDirectory);
