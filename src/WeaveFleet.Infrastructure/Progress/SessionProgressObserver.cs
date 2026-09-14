using System.Threading.Channels;
using WeaveFleet.Application.Progress;
using WeaveFleet.Domain.Events;

namespace WeaveFleet.Infrastructure.Progress;

/// <summary>An event that can change a session's progress, as the relay saw it.</summary>
public sealed record ObservedProgressEvent(string SessionId, string UserId, DomainEvent Event, DateTimeOffset At);

/// <summary>
/// Picks out the Fleet events that change session progress as they pass through the relay and queues them
/// for <see cref="SessionProgressService"/>. Never blocks the relay: when the queue is full the oldest event
/// is dropped. Each todo event carries the whole list and plans are read again when a turn ends, so later
/// events catch up.
/// </summary>
public sealed class SessionProgressObserver : ISessionProgressObserver
{
    private readonly Channel<ObservedProgressEvent> _channel = Channel.CreateBounded<ObservedProgressEvent>(
        new BoundedChannelOptions(1_000) { FullMode = BoundedChannelFullMode.DropOldest, SingleReader = true });

    public ChannelReader<ObservedProgressEvent> Reader => _channel.Reader;

    public void Observe(string sessionId, string? userId, DomainEvent? domainEvent)
    {
        if (string.IsNullOrEmpty(userId) || string.IsNullOrEmpty(sessionId))
            return;

        // Todo lists; files the agent wrote that might be plans; and turn ends, when plans are read again.
        var relevant = domainEvent switch
        {
            TodosReported => true,
            FilesWritten written => written.Payload.Paths.Any(PlanFileReader.IsMarkdown),
            SessionIdled => true,
            DelegationCreated or DelegationUpdated or DelegationCompleted => true,
            _ => false,
        };
        if (!relevant)
            return;

        _channel.Writer.TryWrite(new ObservedProgressEvent(sessionId, userId, domainEvent!, DateTimeOffset.UtcNow));
    }
}
