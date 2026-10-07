namespace WeaveFleet.Domain.Entities;

/// <summary>
/// A turn a model provider's limit stopped, which Fleet tries again by itself: when the limit resets, or after a wait
/// when the provider didn't say. One per session. It's kept in Fleet's database, so a restart doesn't lose it.
/// </summary>
public sealed record ScheduledRetry
{
    public required string SessionId { get; init; }

    /// <summary>The session's owner: the retry is sent as them.</summary>
    public required string UserId { get; init; }

    /// <summary>When Fleet sends it.</summary>
    public required DateTimeOffset DueAt { get; init; }

    /// <summary>How many times in a row a limit stopped the session's turn: 1 for the first.</summary>
    public required int Attempt { get; init; }

    /// <summary>The limit, one of <see cref="Events.TurnErrorKinds"/>.</summary>
    public required string Kind { get; init; }

    /// <summary>What the provider said, as the failure card shows it.</summary>
    public required string Reason { get; init; }

    /// <summary>Whether the provider said when (<see langword="true"/>), or Fleet picked the wait.</summary>
    public bool ProviderSaid { get; init; }

    /// <summary><see cref="ScheduledRetryStates.Waiting"/> until it's sent, then <see cref="ScheduledRetryStates.Sent"/>.</summary>
    public required string State { get; init; }

    public DateTimeOffset CreatedAt { get; init; }
}

/// <summary>Where a <see cref="ScheduledRetry"/> is.</summary>
public static class ScheduledRetryStates
{
    /// <summary>Waiting for <see cref="ScheduledRetry.DueAt"/>. The session's queue waits with it.</summary>
    public const string Waiting = "waiting";

    /// <summary>
    /// Sent, and its turn not over yet. Kept so the next failure counts as the next attempt; gone once a turn ends
    /// without a limit stopping it.
    /// </summary>
    public const string Sent = "sent";
}
