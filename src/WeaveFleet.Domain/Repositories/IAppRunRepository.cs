using WeaveFleet.Domain.Entities;

namespace WeaveFleet.Domain.Repositories;

/// <summary>
/// Stores the runs Fleet starts for sessions. Queries are scoped to the current user, except the two
/// startup methods, which clean up after a previous Fleet for every user.
/// </summary>
public interface IAppRunRepository
{
    Task<AppRun?> GetByIdAsync(string sessionId, string appId);

    /// <summary>Lists a session's runs, oldest first.</summary>
    Task<IReadOnlyList<AppRun>> ListBySessionIdAsync(string sessionId);

    /// <summary>
    /// Inserts the run, or updates everything but its session, owner and creation time. Returns <c>false</c>
    /// if the session doesn't exist or belongs to another user.
    /// </summary>
    Task<bool> UpsertAsync(AppRun run);

    /// <summary>Every user's runs that may still have a process: the ones not yet marked exited or stopped.</summary>
    Task<IReadOnlyList<AppRun>> ListUnfinishedForAllUsersAsync();

    /// <summary>Marks runs stopped and forgets their process, for every user.</summary>
    Task MarkStoppedForAllUsersAsync(IReadOnlyCollection<string> appIds, string updatedAt);
}
