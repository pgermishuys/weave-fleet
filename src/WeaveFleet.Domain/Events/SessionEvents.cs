namespace WeaveFleet.Domain.Events;

/// <summary>
/// Raised when a Fleet session has been created and is ready for use.
/// </summary>
public sealed record SessionStarted : DomainEvent
{
    /// <summary>
    /// Gets the strongly typed payload for the session-started event.
    /// </summary>
    public required SessionStartedPayload Payload { get; init; }
}

/// <summary>
/// Raised when a Fleet session has become idle.
/// </summary>
public sealed record SessionIdled : DomainEvent
{
    /// <summary>
    /// Gets the strongly typed payload for the session-idled event.
    /// </summary>
    public required SessionIdledPayload Payload { get; init; }
}

/// <summary>
/// Raised when a Fleet session has been deleted.
/// </summary>
public sealed record SessionDeleted : DomainEvent
{
    /// <summary>
    /// Gets the strongly typed payload for the session-deleted event.
    /// </summary>
    public required SessionDeletedPayload Payload { get; init; }
}

/// <summary>
/// Raised when a Fleet session has been archived.
/// </summary>
public sealed record SessionArchived : DomainEvent
{
    /// <summary>
    /// Gets the strongly typed payload for the session-archived event.
    /// </summary>
    public required SessionArchivedPayload Payload { get; init; }
}

/// <summary>
/// Raised when Fleet writes a recap for a session you stepped away from, or clears it when you send
/// your next prompt.
/// </summary>
public sealed record SessionRecapUpdated : DomainEvent
{
    /// <summary>
    /// Gets the strongly typed payload for the session-recap event.
    /// </summary>
    public required SessionRecapPayload Payload { get; init; }
}

/// <summary>
/// Raised when one session's agent sends another session a message with <c>fleet_message</c>. Published on
/// the receiving session's topic.
/// </summary>
public sealed record SessionMessaged : DomainEvent
{
    /// <summary>
    /// Gets the strongly typed payload for the session-messaged event.
    /// </summary>
    public required SessionMessagedPayload Payload { get; init; }
}

/// <summary>
/// Why Fleet is telling you about a session you weren't looking at.
/// </summary>
public static class SessionNotificationReasons
{
    /// <summary>The agent stopped on a question only you can answer.</summary>
    public const string NeedsYou = "needs_you";

    /// <summary>The turn ended.</summary>
    public const string Finished = "finished";
}

/// <summary>
/// One thing worth interrupting you for: a session that needed you, or finished, while you were looking
/// somewhere else. Sent on the global sessions topic; the browser turns it into a desktop notification.
/// </summary>
public sealed record SessionNotificationPayload
{
    /// <summary>
    /// Gets the Fleet session identifier.
    /// </summary>
    public required string SessionId { get; init; }

    /// <summary>
    /// Gets why the session is worth interrupting for: see <see cref="SessionNotificationReasons"/>.
    /// </summary>
    public required string Reason { get; init; }

    /// <summary>
    /// Gets the session's title, for the notification's heading.
    /// </summary>
    public required string Title { get; init; }

    /// <summary>
    /// Gets the one line under the heading.
    /// </summary>
    public required string Body { get; init; }
}

/// <summary>
/// A session's recap: one or two sentences on the goal, the current task and the next action.
/// </summary>
public sealed record SessionRecapPayload
{
    /// <summary>
    /// Gets the Fleet session identifier.
    /// </summary>
    public required string SessionId { get; init; }

    /// <summary>
    /// Gets the recap text, or null when the recap was cleared.
    /// </summary>
    public string? Text { get; init; }

    /// <summary>
    /// Gets when Fleet wrote the recap (ISO 8601), or null when it was cleared.
    /// </summary>
    public string? WrittenAt { get; init; }
}

/// <summary>
/// One session messaged another. The sender is the session whose agent called the tool, as Fleet resolved it,
/// not something the agent wrote.
/// </summary>
public sealed record SessionMessagedPayload
{
    /// <summary>
    /// Gets the session that sent the message.
    /// </summary>
    public required string FromSessionId { get; init; }

    /// <summary>
    /// Gets the session that received it.
    /// </summary>
    public required string ToSessionId { get; init; }

    /// <summary>
    /// Gets the receipt's event id for the prompt that carried it, when there is one.
    /// </summary>
    public long? EventId { get; init; }

    /// <summary>
    /// Gets the correlation id of the prompt that carried it.
    /// </summary>
    public required string CorrelationId { get; init; }
}

/// <summary>
/// Payload describing a started session.
/// </summary>
public sealed record SessionStartedPayload
{
    /// <summary>
    /// Gets the Fleet session identifier.
    /// </summary>
    public required string SessionId { get; init; }

    /// <summary>
    /// Gets the backing harness instance identifier.
    /// </summary>
    public string? InstanceId { get; init; }

    /// <summary>
    /// Gets the Fleet workspace identifier.
    /// </summary>
    public string? WorkspaceId { get; init; }

    /// <summary>
    /// Gets the session title.
    /// </summary>
    public string? Title { get; init; }

    /// <summary>
    /// Gets the Fleet project identifier associated with the session.
    /// </summary>
    public string? ProjectId { get; init; }

    /// <summary>
    /// Gets the parent Fleet session identifier when the session was delegated or forked.
    /// </summary>
    public string? ParentSessionId { get; init; }

    /// <summary>
    /// Gets a value indicating whether the session is hidden from normal lists.
    /// </summary>
    public bool? IsHidden { get; init; }
}

/// <summary>
/// Payload describing a session that has become idle.
/// </summary>
public sealed record SessionIdledPayload
{
    /// <summary>
    /// Gets the Fleet session identifier.
    /// </summary>
    public required string SessionId { get; init; }
}

/// <summary>
/// Payload describing a deleted session.
/// </summary>
public sealed record SessionDeletedPayload
{
    /// <summary>
    /// Gets the Fleet session identifier.
    /// </summary>
    public required string SessionId { get; init; }
}

/// <summary>
/// Payload describing an archived session.
/// </summary>
public sealed record SessionArchivedPayload
{
    /// <summary>
    /// Gets the Fleet session identifier.
    /// </summary>
    public required string SessionId { get; init; }

    /// <summary>
    /// Gets the ISO-8601 timestamp when the session was archived.
    /// </summary>
    public required string ArchivedAt { get; init; }
}
