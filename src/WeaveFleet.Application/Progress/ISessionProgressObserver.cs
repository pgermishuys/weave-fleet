using WeaveFleet.Domain.Events;

namespace WeaveFleet.Application.Progress;

/// <summary>
/// Takes the Fleet events that change a session's progress. The relay hands it translated harness events;
/// <c>DelegationService</c> hands it subagent lifecycle events, which don't pass through the relay. Never blocks.
/// </summary>
public interface ISessionProgressObserver
{
    void Observe(string sessionId, string? userId, DomainEvent? domainEvent);
}
