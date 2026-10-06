using WeaveFleet.Domain.Entities;

namespace WeaveFleet.Domain.Repositories;

public interface ISessionContextRepository
{
    /// <summary>Returns the current user's record of a session's context, or <c>null</c> when there is none.</summary>
    Task<SessionContext?> GetAsync(string sessionId, CancellationToken ct);

    /// <summary>
    /// Returns a session's context for the given owner. Not scoped to the current user: for background work that
    /// already knows the session's owner.
    /// </summary>
    Task<SessionContext?> GetForOwnerAsync(string sessionId, string userId, CancellationToken ct);

    /// <summary>
    /// Stores a session's context, guarded by the session's owner (<see cref="SessionContext.UserId"/>). Returns
    /// <c>false</c> when the session doesn't exist or belongs to someone else.
    /// </summary>
    Task<bool> UpsertAsync(SessionContext context, CancellationToken ct);
}
