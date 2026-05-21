using WeaveFleet.Domain.Events;
using WeaveFleet.Domain.Harnesses;

namespace WeaveFleet.Infrastructure.EventBus;

/// <summary>
/// Carries a single <see cref="HarnessEvent"/> and its routing metadata through the in-process
/// event bus channels. Created by <see cref="InProcessEventPublisher"/> for both durable and
/// ephemeral events.
/// </summary>
internal sealed class InProcessEnvelope
{
    public InProcessEnvelope(
        HarnessEvent @event,
        string messageId,
        string tenant,
        string projectId,
        string sessionId,
        string eventType,
        string? userId,
        string? harnessType,
        long internalPumpDedupKey,
        bool isDurable)
    {
        Event = @event;
        MessageId = messageId;
        Tenant = tenant;
        ProjectId = projectId;
        SessionId = sessionId;
        EventType = eventType;
        UserId = userId;
        HarnessType = harnessType;
        InternalPumpDedupKey = internalPumpDedupKey;
        IsDurable = isDurable;
    }

    public HarnessEvent Event { get; }
    public string MessageId { get; }
    public string Tenant { get; }
    public string ProjectId { get; }
    public string SessionId { get; }
    public string EventType { get; }
    public string? UserId { get; }
    public string? HarnessType { get; }

    /// <summary>
    /// Internal publish-side dedup key persisted to the in-process store for idempotency only.
    /// Do not use for client ordering, replay cursors, or public protocol sequence numbers.
    /// </summary>
    public long InternalPumpDedupKey { get; }

    public bool IsDurable { get; }

    public long? EventId { get; set; }

    public DomainEvent? DomainEvent { get; init; }
}
