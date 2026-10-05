using System.Text.Json;
using WeaveFleet.Application.Services;
using WeaveFleet.Domain.Events;

namespace WeaveFleet.Application.Sessions;

/// <summary>
/// Sends every session notification to the open tabs on the global sessions topic. The browser decides whether to
/// show it (Settings → Features → Desktop notifications), so the server sends it either way: another machine's
/// home server listens on this topic to push to phones, whatever this machine's desktop setting is.
/// </summary>
public sealed class BroadcastNotificationSink(IEventBroadcaster broadcaster) : ISessionNotificationSink
{
    public Task HandleAsync(SessionNotificationPayload payload, string userId, CancellationToken cancellationToken) =>
        broadcaster.BroadcastAsync(
            "sessions",
            SessionNotifier.EventType,
            JsonSerializer.SerializeToElement(payload, ApplicationJsonContext.Default.SessionNotificationPayload),
            userId,
            cancellationToken);
}
