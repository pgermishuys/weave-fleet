using System.Threading.Channels;

namespace WeaveFleet.Infrastructure.EventBus;

/// <summary>
/// Holds the two <see cref="Channel{T}"/> instances shared between the publisher and the
/// background consumer services. Registered as a singleton so all three services share the
/// same channel instances without multiple DI registrations.
/// </summary>
internal sealed class InProcessChannels
{
    /// <summary>
    /// Receives a signal (null) every time a new durable event is appended to the store, waking
    /// up <see cref="InProcessProjectionHost"/> to query the DB for new rows.
    /// </summary>
    internal Channel<object?> ProjectionWakeUp { get; } =
        Channel.CreateUnbounded<object?>(new UnboundedChannelOptions
        {
            SingleReader = true,
            SingleWriter = false,
            AllowSynchronousContinuations = false,
        });

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
}
