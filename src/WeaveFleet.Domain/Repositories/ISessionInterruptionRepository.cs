using WeaveFleet.Domain.Entities;

namespace WeaveFleet.Domain.Repositories;

/// <summary>
/// Which sessions were mid-turn when Fleet stopped (<c>sessions.turn_started_at</c>) and which a restart cut off
/// (<see cref="SessionInterruption"/>). The calls that name a session are scoped to the current user's sessions; the
/// flag calls are made as the session's owner by the relay, and <see cref="ListFlaggedAsync"/> is every user's, for startup.
/// </summary>
public interface ISessionInterruptionRepository
{
    /// <summary>The session's turn started (or is waiting on the user): flags it.</summary>
    Task FlagTurnAsync(string sessionId, DateTimeOffset startedAt, bool waiting);

    /// <summary>Marks only whether the flagged turn waits on the user.</summary>
    Task SetWaitingAsync(string sessionId, bool waiting);

    /// <summary>The turn ended: clears the flag.</summary>
    Task ClearTurnAsync(string sessionId);

    /// <summary>Every session flagged as mid-turn, for every user.</summary>
    Task<IReadOnlyList<FlaggedTurn>> ListFlaggedAsync();

    Task<SessionInterruption?> GetAsync(string sessionId);

    /// <summary>The current user's interruptions, for the sessions list.</summary>
    Task<IReadOnlyList<SessionInterruption>> ListForUserAsync();

    Task SaveAsync(SessionInterruption interruption);

    /// <summary>Removes the session's interruption and returns it; <see langword="null"/> when it had none. Only one caller gets it.</summary>
    Task<SessionInterruption?> RemoveAsync(string sessionId);
}
