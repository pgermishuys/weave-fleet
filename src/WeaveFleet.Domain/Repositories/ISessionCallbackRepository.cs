using WeaveFleet.Domain.Entities;

namespace WeaveFleet.Domain.Repositories;

public interface ISessionCallbackRepository
{
    Task InsertAsync(SessionCallback callback);

    /// <summary>Moves the source session's pending callbacks to started. Returns how many moved.</summary>
    Task<int> MarkSourceStartedAsync(string sourceSessionId);

    /// <summary>The current user's started callbacks, oldest first.</summary>
    Task<IReadOnlyList<SessionCallback>> GetStartedAsync();

    /// <summary>Marks a started callback fired. False when it wasn't started (already fired, or someone else's).</summary>
    Task<bool> MarkFiredAsync(string id);

    /// <summary>The users who have started callbacks, across all users. For the poll, which has no user of its own.</summary>
    Task<IReadOnlyList<string>> GetOwnersWithStartedCallbacksAsync();

    Task<int> DeleteForSessionAsync(string sessionId);
}
