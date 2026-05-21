namespace WeaveFleet.Domain.Harnesses;

/// <summary>
/// Describes the processing characteristics of a harness event type.
/// </summary>
public readonly struct EventClassification
{
    /// <summary>Whether the event type is recognised by the registry. Unknown types default to <c>false</c> so callers can distinguish genuinely unclassified events from known-but-unrouted ones (e.g. server control events).</summary>
    public bool IsKnown { get; init; }

    /// <summary>Whether the event should be persisted to durable storage.</summary>
    public bool IsDurable { get; init; }

    /// <summary>Whether the event is ephemeral and should be relayed directly to clients without persistence.</summary>
    public bool IsEphemeralRelay { get; init; }

    /// <summary>
    /// Whether the event is advisory only. Advisory events are not part of committed session state:
    /// they carry no durable <c>eventId</c>, may be dropped during reconnect/replay, and must not
    /// be used by clients or projections to advance committed state.
    /// </summary>
    public bool IsAdvisory { get; init; }

    /// <summary>Whether the event payload may contain reasoning content that must be filtered before delivery or storage.</summary>
    public bool RequiresReasoningFilter { get; init; }

    /// <summary>Whether the event carries session activity status information.</summary>
    public bool IsActivitySignal { get; init; }
}

/// <summary>
/// Central registry that classifies every known harness event type.
/// Replaces scattered <c>if (evt.Type == "...")</c> checks with a single lookup.
/// </summary>
public static class EventTypeMetadata
{
    /// <summary>
    /// Returns the <see cref="EventClassification"/> for the given event type.
    /// Unknown event types default to non-durable, non-ephemeral.
    /// </summary>
    public static EventClassification Classify(string eventType)
    {
        ArgumentNullException.ThrowIfNull(eventType);
        return eventType switch
        {
        EventTypes.MessageCreated => new EventClassification
        {
            IsKnown = true,
            IsDurable = true,
            IsEphemeralRelay = false,
            IsAdvisory = false,
            RequiresReasoningFilter = true,
            IsActivitySignal = false,
        },
        EventTypes.MessageUpdated => new EventClassification
        {
            IsKnown = true,
            IsDurable = true,
            IsEphemeralRelay = false,
            IsAdvisory = false,
            RequiresReasoningFilter = true,
            IsActivitySignal = false,
        },
        EventTypes.MessagePartUpdated => new EventClassification
        {
            IsKnown = true,
            IsDurable = true,
            IsEphemeralRelay = false,
            IsAdvisory = false,
            RequiresReasoningFilter = true,
            IsActivitySignal = false,
        },
        EventTypes.MessageRemoved => new EventClassification
        {
            IsKnown = true,
            IsDurable = true,
            IsEphemeralRelay = false,
            IsAdvisory = false,
            RequiresReasoningFilter = false,
            IsActivitySignal = false,
        },
        EventTypes.MessagePartRemoved => new EventClassification
        {
            IsKnown = true,
            IsDurable = true,
            IsEphemeralRelay = false,
            IsAdvisory = false,
            RequiresReasoningFilter = false,
            IsActivitySignal = false,
        },
        EventTypes.UserPromptCommitted => new EventClassification
        {
            IsKnown = true,
            IsDurable = true,
            IsEphemeralRelay = false,
            IsAdvisory = false,
            RequiresReasoningFilter = false,
            IsActivitySignal = false,
        },
        EventTypes.SessionCreated => new EventClassification
        {
            IsKnown = true,
            IsDurable = false,
            IsEphemeralRelay = true,
            IsAdvisory = true,
            RequiresReasoningFilter = false,
            IsActivitySignal = false,
        },
        EventTypes.SessionUpdated => new EventClassification
        {
            IsKnown = true,
            IsDurable = true,
            IsEphemeralRelay = false,
            IsAdvisory = false,
            RequiresReasoningFilter = false,
            IsActivitySignal = false,
        },
        EventTypes.SessionError => new EventClassification
        {
            IsKnown = true,
            IsDurable = true,
            IsEphemeralRelay = false,
            IsAdvisory = false,
            RequiresReasoningFilter = false,
            IsActivitySignal = false,
        },
        EventTypes.SessionCompacted => new EventClassification
        {
            IsKnown = true,
            IsDurable = true,
            IsEphemeralRelay = false,
            IsAdvisory = false,
            RequiresReasoningFilter = false,
            IsActivitySignal = false,
        },
        EventTypes.SessionDeleted => new EventClassification
        {
            IsKnown = true,
            IsDurable = true,
            IsEphemeralRelay = false,
            IsAdvisory = false,
            RequiresReasoningFilter = false,
            IsActivitySignal = false,
        },
        EventTypes.SessionStatus => new EventClassification
        {
            IsKnown = true,
            IsDurable = false,
            IsEphemeralRelay = true,
            IsAdvisory = true,
            RequiresReasoningFilter = false,
            IsActivitySignal = true,
        },
        EventTypes.SessionIdle => new EventClassification
        {
            IsKnown = true,
            IsDurable = false,
            IsEphemeralRelay = true,
            IsAdvisory = true,
            RequiresReasoningFilter = false,
            IsActivitySignal = true,
        },
        EventTypes.MessagePartDelta => new EventClassification
        {
            IsKnown = true,
            IsDurable = false,
            IsEphemeralRelay = true,
            IsAdvisory = true,
            RequiresReasoningFilter = false,
            IsActivitySignal = false,
        },
        EventTypes.Error => new EventClassification
        {
            IsKnown = true,
            IsDurable = false,
            IsEphemeralRelay = true,
            IsAdvisory = true,
            RequiresReasoningFilter = false,
            IsActivitySignal = false,
        },
        EventTypes.ServerHeartbeat => new EventClassification
        {
            IsKnown = true,
            IsDurable = false,
            IsEphemeralRelay = false,
            IsAdvisory = false,
            RequiresReasoningFilter = false,
            IsActivitySignal = false,
        },
        EventTypes.ServerConnected => new EventClassification
        {
            IsKnown = true,
            IsDurable = false,
            IsEphemeralRelay = false,
            IsAdvisory = false,
            RequiresReasoningFilter = false,
            IsActivitySignal = false,
        },
        EventTypes.SessionDiff => new EventClassification
        {
            IsKnown = true,
            IsDurable = false,
            IsEphemeralRelay = false,
            IsAdvisory = false,
            RequiresReasoningFilter = false,
            IsActivitySignal = false,
        },
        _ when EventTypes.IsPermissionEvent(eventType) => new EventClassification
        {
            IsKnown = true,
            IsDurable = false,
            IsEphemeralRelay = true,
            IsAdvisory = true,
            RequiresReasoningFilter = false,
            IsActivitySignal = false,
        },
        _ => default,
    };
    }
}
