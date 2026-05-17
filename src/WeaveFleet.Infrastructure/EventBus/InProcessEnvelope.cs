using WeaveFleet.Domain.Events;
using WeaveFleet.Domain.Harnesses;

namespace WeaveFleet.Infrastructure.EventBus;

/// <summary>
/// Carries a single <see cref="HarnessEvent"/> and its routing metadata through the in-process
/// event bus channels. Created by <see cref="InProcessEventPublisher"/> for both durable and
/// ephemeral events.
/// </summary>
internal sealed record InProcessEnvelope(
    HarnessEvent Event,
    string MessageId,
    string Tenant,
    string ProjectId,
    string SessionId,
    string EventType,
    string? UserId,
    string? HarnessType,
    long Sequence,
    bool IsDurable)
{
    public DomainEvent? DomainEvent { get; init; }
}
