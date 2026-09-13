using WeaveFleet.Domain.Entities;

namespace WeaveFleet.Domain.Repositories;

public interface ISessionProgressRepository
{
    /// <summary>Returns the current user's progress for a session, or <c>null</c> when there is none.</summary>
    Task<SessionProgress?> GetAsync(string sessionId, CancellationToken ct);

    /// <summary>Returns the current user's progress for whichever of <paramref name="sessionIds"/> have some.</summary>
    Task<IReadOnlyDictionary<string, SessionProgress>> GetManyAsync(IReadOnlyCollection<string> sessionIds, CancellationToken ct);

    /// <summary>
    /// Returns a session's progress for the given owner. Not scoped to the current user: for background work
    /// that already knows the session's owner.
    /// </summary>
    Task<SessionProgress?> GetForOwnerAsync(string sessionId, string userId, CancellationToken ct);

    /// <summary>
    /// Stores progress, guarded by the session's owner (<see cref="SessionProgress.UserId"/>). Returns <c>false</c>
    /// when the session doesn't exist or belongs to someone else.
    /// </summary>
    Task<bool> UpsertAsync(SessionProgress progress, CancellationToken ct);
}
