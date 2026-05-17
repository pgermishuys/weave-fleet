namespace WeaveFleet.Domain.Events;

/// <summary>
/// Raised when a delegation has been created.
/// </summary>
public sealed record DelegationCreated : DomainEvent
{
    /// <summary>
    /// Gets the strongly typed payload for the delegation-created event.
    /// </summary>
    public required DelegationCreatedPayload Payload { get; init; }
}

/// <summary>
/// Raised when a delegation has been updated.
/// </summary>
public sealed record DelegationUpdated : DomainEvent
{
    /// <summary>
    /// Gets the strongly typed payload for the delegation-updated event.
    /// </summary>
    public required DelegationUpdatedPayload Payload { get; init; }
}

/// <summary>
/// Raised when a delegation has completed.
/// </summary>
public sealed record DelegationCompleted : DomainEvent
{
    /// <summary>
    /// Gets the strongly typed payload for the delegation-completed event.
    /// </summary>
    public required DelegationCompletedPayload Payload { get; init; }
}

/// <summary>
/// Payload describing a created delegation.
/// </summary>
public sealed record DelegationCreatedPayload
{
    /// <summary>
    /// Gets the delegation identifier.
    /// </summary>
    public required string DelegationId { get; init; }

    /// <summary>
    /// Gets the parent Fleet session identifier.
    /// </summary>
    public required string ParentSessionId { get; init; }

    /// <summary>
    /// Gets the parent tool call identifier that created the delegation.
    /// </summary>
    public string? ParentToolCallId { get; init; }

    /// <summary>
    /// Gets the child Fleet session identifier when linked.
    /// </summary>
    public string? ChildSessionId { get; init; }

    /// <summary>
    /// Gets the human-readable delegation title.
    /// </summary>
    public required string Title { get; init; }

    /// <summary>
    /// Gets the current delegation status.
    /// </summary>
    public required string Status { get; init; }

    /// <summary>
    /// Gets the ISO-8601 timestamp when the delegation was created.
    /// </summary>
    public required string CreatedAt { get; init; }
}

/// <summary>
/// Payload describing an updated delegation.
/// </summary>
public sealed record DelegationUpdatedPayload
{
    /// <summary>
    /// Gets the delegation identifier.
    /// </summary>
    public required string DelegationId { get; init; }

    /// <summary>
    /// Gets the parent Fleet session identifier.
    /// </summary>
    public required string ParentSessionId { get; init; }

    /// <summary>
    /// Gets the parent tool call identifier that created the delegation.
    /// </summary>
    public string? ParentToolCallId { get; init; }

    /// <summary>
    /// Gets the child Fleet session identifier when linked.
    /// </summary>
    public string? ChildSessionId { get; init; }

    /// <summary>
    /// Gets the human-readable delegation title.
    /// </summary>
    public required string Title { get; init; }

    /// <summary>
    /// Gets the current delegation status.
    /// </summary>
    public required string Status { get; init; }

    /// <summary>
    /// Gets the ISO-8601 timestamp when the delegation was created.
    /// </summary>
    public required string CreatedAt { get; init; }
}

/// <summary>
/// Payload describing a completed delegation.
/// </summary>
public sealed record DelegationCompletedPayload
{
    /// <summary>
    /// Gets the delegation identifier.
    /// </summary>
    public required string DelegationId { get; init; }

    /// <summary>
    /// Gets the parent Fleet session identifier.
    /// </summary>
    public required string ParentSessionId { get; init; }

    /// <summary>
    /// Gets the parent tool call identifier that created the delegation.
    /// </summary>
    public string? ParentToolCallId { get; init; }

    /// <summary>
    /// Gets the child Fleet session identifier when linked.
    /// </summary>
    public string? ChildSessionId { get; init; }

    /// <summary>
    /// Gets the human-readable delegation title.
    /// </summary>
    public required string Title { get; init; }

    /// <summary>
    /// Gets the terminal delegation status.
    /// </summary>
    public required string Status { get; init; }

    /// <summary>
    /// Gets the ISO-8601 timestamp when the delegation was created.
    /// </summary>
    public required string CreatedAt { get; init; }

    /// <summary>
    /// Gets the ISO-8601 timestamp when the delegation completed.
    /// </summary>
    public required string CompletedAt { get; init; }
}
