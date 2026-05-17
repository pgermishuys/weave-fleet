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
/// Raised when a Fleet session has been stopped.
/// </summary>
public sealed record SessionStopped : DomainEvent
{
    /// <summary>
    /// Gets the strongly typed payload for the session-stopped event.
    /// </summary>
    public required SessionStoppedPayload Payload { get; init; }
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
/// Payload describing a session that has stopped.
/// </summary>
public sealed record SessionStoppedPayload
{
    /// <summary>
    /// Gets the Fleet session identifier.
    /// </summary>
    public required string SessionId { get; init; }

    /// <summary>
    /// Gets the ISO-8601 timestamp when the session stopped.
    /// </summary>
    public required string StoppedAt { get; init; }
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
