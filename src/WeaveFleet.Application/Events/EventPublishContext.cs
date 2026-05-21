using WeaveFleet.Domain.Events;

namespace WeaveFleet.Application.Events;

/// <summary>
/// Per-event context required by <see cref="IEventPublisher"/> to construct subjects and headers.
/// Populated by the caller (typically <c>HarnessEventRelay</c>) from repository data.
/// <para>
/// <see cref="InternalPumpDedupKey"/> is an internal per-pump monotonic counter owned by the
/// publishing caller. It is used only as the publish-side/store dedup key
/// (<c>{sessionId}:{key}</c>) when no caller-supplied <see cref="CorrelationId"/> exists.
/// It is not a client-facing ordering cursor; public protocols use durable <c>eventId</c>.
/// </para>
/// </summary>
public readonly record struct EventPublishContext(
    string FleetSessionId,
    string? ProjectId,
    string? UserId,
    string? HarnessType,
    long InternalPumpDedupKey)
{
    /// <summary>
    /// Gets the translated domain event associated with the published raw harness event when one exists.
    /// </summary>
    public DomainEvent? DomainEvent { get; init; }

    /// <summary>
    /// Gets the caller-supplied idempotency key for client-originated events when one exists.
    /// </summary>
    public string? CorrelationId { get; init; }
}
