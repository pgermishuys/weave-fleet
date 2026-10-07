using WeaveFleet.Domain.Entities;

namespace WeaveFleet.Domain.Repositories;

/// <summary>
/// Sessions Fleet will try again (<see cref="ScheduledRetry"/>), one per session. The calls that name a session are
/// scoped to the current user's sessions; <see cref="ListWaitingAsync"/> is every user's, for the scheduler.
/// </summary>
public interface IScheduledRetryRepository
{
    /// <summary>The session's retry, or <see langword="null"/>.</summary>
    Task<ScheduledRetry?> GetAsync(string sessionId);

    /// <summary>The current user's waiting retries, for the sessions list.</summary>
    Task<IReadOnlyList<ScheduledRetry>> ListForUserAsync();

    /// <summary>Every user's waiting retries, for the scheduler to keep in memory.</summary>
    Task<IReadOnlyList<ScheduledRetry>> ListWaitingAsync();

    /// <summary>Saves the session's retry, replacing any it had. False when the session isn't the user's.</summary>
    Task<bool> SaveAsync(ScheduledRetry retry);

    /// <summary>
    /// Marks the session's waiting retry sent and returns it; <see langword="null"/> when it has none waiting (it was
    /// cancelled, or already sent). Only one caller gets it.
    /// </summary>
    Task<ScheduledRetry?> TakeWaitingAsync(string sessionId);

    /// <summary>Removes the session's retry, in whatever state, and returns it; <see langword="null"/> when it had none.</summary>
    Task<ScheduledRetry?> RemoveAsync(string sessionId);
}
