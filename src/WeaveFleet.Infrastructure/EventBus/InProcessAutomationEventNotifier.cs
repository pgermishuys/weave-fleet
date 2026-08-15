using System.Threading.Channels;
using WeaveFleet.Application.Services;
using WeaveFleet.Infrastructure.Services;

namespace WeaveFleet.Infrastructure.EventBus;

/// <summary>
/// In-process implementation of <see cref="IAutomationEventNotifier"/> that writes
/// notifications to the <see cref="InProcessChannels.AutomationEvents"/> channel.
/// </summary>
internal sealed class InProcessAutomationEventNotifier : IAutomationEventNotifier
{
    private readonly Channel<AutomationEventNotification> _channel;

    public InProcessAutomationEventNotifier(Channel<AutomationEventNotification> channel)
    {
        _channel = channel;
    }

    public async Task NotifyAsync(
        string eventType,
        string eventId,
        string? sessionId,
        string? sessionSourceReference,
        string? eventSummary,
        CancellationToken ct)
    {
        var notification = new AutomationEventNotification(
            EventType: eventType,
            EventId: eventId,
            SessionId: sessionId,
            SessionSourceReference: sessionSourceReference,
            EventSummary: eventSummary);

        await _channel.Writer.WriteAsync(notification, ct).ConfigureAwait(false);
    }
}
