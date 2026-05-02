namespace WeaveFleet.Application.Events;

/// <summary>
/// Per-event context required by <see cref="IEventPublisher"/> to construct subjects and headers.
/// Populated by the caller (typically <c>HarnessEventRelay</c>) from repository data.
/// <para>
/// <see cref="Sequence"/> is a per-session monotonic counter owned by the publishing caller.
/// It is used as the publish-side dedup key (<c>{sessionId}:{seq}</c>).
/// </para>
/// </summary>
public readonly record struct EventPublishContext(
    string FleetSessionId,
    string? ProjectId,
    string? UserId,
    string? HarnessType,
    long Sequence);
