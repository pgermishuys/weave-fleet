using WeaveFleet.Domain.Entities;

namespace WeaveFleet.Domain.Repositories;

/// <summary>
/// A session's queued messages (<see cref="QueuedPrompt"/>), in order. Every call is scoped to the current user's
/// sessions: another user's session reads as having none, and can't be added to.
/// </summary>
public interface IQueuedPromptRepository
{
    /// <summary>The session's queue, first to go first.</summary>
    Task<IReadOnlyList<QueuedPrompt>> ListAsync(string sessionId);

    /// <summary>Adds <paramref name="item"/> at the end of its session's queue. False when the session isn't the user's.</summary>
    Task<bool> AddAsync(QueuedPrompt item);

    /// <summary>Puts <paramref name="item"/> back at the front of its session's queue, after sending it failed.</summary>
    Task<bool> ReturnToFrontAsync(QueuedPrompt item);

    /// <summary>Takes one item out of the queue and returns it; <see langword="null"/> when it isn't there (any more).</summary>
    Task<QueuedPrompt?> TakeAsync(string sessionId, string itemId);

    /// <summary>
    /// Takes the first item out of the session's queue and returns it; <see langword="null"/> when the queue is empty.
    /// Only one caller gets each item.
    /// </summary>
    Task<QueuedPrompt?> TakeFirstAsync(string sessionId);
}
