using WeaveFleet.Domain.Events;

namespace WeaveFleet.Application.Sessions;

/// <summary>
/// Somewhere a session notification goes once <see cref="SessionNotifier"/> decides it's worth sending: the open
/// browser tabs (<see cref="BroadcastNotificationSink"/>), and phones (<c>PushNotificationDispatcher</c>).
/// A sink must return quickly: it's called off the relay's pump but in line with the other sinks.
/// </summary>
public interface ISessionNotificationSink
{
    Task HandleAsync(SessionNotificationPayload payload, string userId, CancellationToken cancellationToken);
}
