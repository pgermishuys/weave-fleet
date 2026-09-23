namespace WeaveFleet.Domain.Events;

/// <summary>
/// Represents the fully materialized state of a session at a point in time.
/// </summary>
public sealed record SessionSnapshot
{
    /// <summary>
    /// Gets the current session metadata.
    /// </summary>
    public required SessionSnapshotSession Session { get; init; }

    /// <summary>
    /// Gets the most recent page of materialized messages for the session.
    /// </summary>
    public IReadOnlyList<MessageLifecyclePayload> Messages { get; init; } = [];

    /// <summary>
    /// Gets the current materialized delegations for the session.
    /// </summary>
    public IReadOnlyList<SessionSnapshotDelegation> Delegations { get; init; } = [];

    /// <summary>
    /// Gets the effective current activity status for the session.
    /// Expected values are <c>"idle"</c>, <c>"busy"</c> and <c>"retry"</c> (still working, waiting to retry a model error).
    /// </summary>
    public required string ActivityStatus { get; init; }

    /// <summary>
    /// Gets the recap Fleet wrote while you were away, until you send your next prompt.
    /// </summary>
    public SessionRecapPayload? Recap { get; init; }

    private readonly long? _lastEventId;

    /// <summary>
    /// Gets the highest committed durable event id included in the snapshot.
    /// Live events should resume after this watermark.
    /// </summary>
    public long? LastEventId
    {
        get => _lastEventId;
        init => _lastEventId = value;
    }

    /// <summary>
    /// Deprecated compatibility alias for <see cref="LastEventId"/>.
    /// </summary>
    public long? LastSequenceNumber
    {
        get => _lastEventId;
        init => _lastEventId = value;
    }

    /// <summary>
    /// Gets a value indicating whether older messages exist before the current page.
    /// </summary>
    public bool HasMore { get; init; }

    /// <summary>
    /// Gets the cursor for loading older messages.
    /// When present, this should identify the oldest included message.
    /// </summary>
    public string? Cursor { get; init; }

    /// <summary>
    /// Gets a value indicating whether this snapshot is partial due to unavailability of the live harness.
    /// When <c>true</c>, messages may be incomplete or stale, and the client should show a degraded state indicator.
    /// </summary>
    public bool IsPartial { get; init; }
}

/// <summary>
/// Session metadata included in a <see cref="SessionSnapshot"/>.
/// </summary>
public sealed record SessionSnapshotSession
{
    /// <summary>
    /// Gets the Fleet session identifier.
    /// </summary>
    public required string Id { get; init; }

    /// <summary>
    /// Gets the current session title.
    /// </summary>
    public required string Title { get; init; }

    /// <summary>
    /// Gets the current persisted session status.
    /// This is the session lifecycle/status value stored by Fleet.
    /// </summary>
    public required string Status { get; init; }
}

/// <summary>
/// A materialized delegation entry included in a <see cref="SessionSnapshot"/>.
/// </summary>
public sealed record SessionSnapshotDelegation
{
    /// <summary>
    /// Gets the delegation identifier.
    /// </summary>
    public required string DelegationId { get; init; }

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

    /// <summary>
    /// Gets what the child session shows now (<c>"busy"</c>, <c>"waiting_input"</c>, …), or <c>null</c> when Fleet
    /// isn't tracking it. A parent opened while its subagent waits on a question shows that on the subagent's row;
    /// live changes arrive on the <c>sessions</c> topic's <c>activity_status</c>.
    /// </summary>
    public string? ChildActivityStatus { get; init; }

    /// <summary>
    /// Gets whether the call that started the delegation returned while its child carries on working (<c>true</c>),
    /// or <c>null</c>. Such a child's work isn't its parent's: the parent reads idle, and the subagent's row says it's
    /// running in the background.
    /// </summary>
    public bool? Background { get; init; }
}
