namespace WeaveFleet.Application.Events;

/// <summary>
/// Per-event context required by <see cref="IEventPublisher"/> to construct subjects and headers.
/// Populated by the caller (typically <c>HarnessEventRelay</c>) from repository data.
/// <para>
/// <see cref="Sequence"/> is a per-session monotonic counter owned by the publishing caller.
/// It is used as the publish-side half of the <c>Nats-Msg-Id</c> header (<c>{sessionId}:{seq}</c>)
/// so JetStream dedup (~2-minute window) can collapse retries of the same logical publish.
/// </para>
/// </summary>
public readonly record struct EventPublishContext(
    string FleetSessionId,
    string? ProjectId,
    string? UserId,
    string? HarnessType,
    long Sequence);
