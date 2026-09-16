namespace WeaveFleet.Domain.Harnesses;

/// <summary>
/// What a session is doing right now, as reported by its harness in the <c>type</c> of a
/// <see cref="EventTypes.SessionStatus"/> event. Ephemeral: it lives in the activity tracker, never the database.
/// </summary>
public static class ActivityStatuses
{
    /// <summary>Between turns, waiting for a prompt.</summary>
    public const string Idle = "idle";

    /// <summary>In a turn.</summary>
    public const string Busy = "busy";

    /// <summary>In a turn, waiting out a model error (e.g. a rate limit) before trying again.</summary>
    public const string Retry = "retry";

    /// <summary>In a turn, waiting on a subagent.</summary>
    public const string Delegating = "delegating";

    /// <summary>
    /// In a turn, but stopped on a question only the user can answer. The turn goes on the moment they do,
    /// so this is neither working nor finished: it needs them.
    /// </summary>
    public const string WaitingInput = "waiting_input";
}
