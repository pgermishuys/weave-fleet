using System.Threading.Channels;
using WeaveFleet.Infrastructure.Services;

namespace WeaveFleet.Infrastructure.EventBus;

/// <summary>
/// Holds the two <see cref="Channel{T}"/> instances shared between the publisher and the
/// background consumer services. Registered as a singleton so both services share the
/// same channel instances without multiple DI registrations.
/// </summary>
internal sealed class InProcessChannels
{
    /// <summary>
    /// Carries every event (durable + ephemeral) to <see cref="InProcessFanOutService"/> for
    /// immediate WebSocket broadcast. Not persisted — events are dropped if the service is
    /// not running (e.g. during startup replay).
    /// </summary>
    internal Channel<InProcessEnvelope> FanOut { get; } =
        Channel.CreateUnbounded<InProcessEnvelope>(new UnboundedChannelOptions
        {
            SingleReader = true,
            SingleWriter = false,
            AllowSynchronousContinuations = false,
        });

    /// <summary>
    /// Carries every event (durable + ephemeral) to <see cref="AutomationEventDispatcherService"/>
    /// for automation trigger matching. Not persisted — events are dropped if the service is
    /// not running.
    /// </summary>
    internal Channel<AutomationEventNotification> AutomationEvents { get; } =
        Channel.CreateUnbounded<AutomationEventNotification>(new UnboundedChannelOptions
        {
            SingleReader = true,
            SingleWriter = false,
            AllowSynchronousContinuations = false,
        });
}
