using System.Collections.Concurrent;
using WeaveFleet.Application.Events;
using WeaveFleet.Domain.Harnesses;

namespace WeaveFleet.Testing.Fakes;

/// <summary>
/// Hand-crafted fake of <see cref="IEventPublisher"/> that records every call for assertion
/// in tests and never contacts a real broker.
/// </summary>
public sealed class FakeEventPublisher : IEventPublisher
{
    private long _nextEventId;
    private readonly Dictionary<string, long> _correlationEventIds = new(StringComparer.Ordinal);

    public ConcurrentQueue<Published> Calls { get; } = new();

    /// <summary>When true, every call to <see cref="PublishAsync"/> throws — used to exercise
    /// the relay's publish-failure-swallow path.</summary>
    public bool ShouldFail { get; set; }

    public Task<PublishResult> PublishAsync(HarnessEvent evt, EventPublishContext context, CancellationToken ct)
    {
        Calls.Enqueue(new Published(evt, context));

        if (ShouldFail) throw new InvalidOperationException("FakeEventPublisher configured to fail.");

        if (!string.IsNullOrWhiteSpace(context.CorrelationId)
            && _correlationEventIds.TryGetValue(context.CorrelationId, out var existingEventId))
        {
            return Task.FromResult(new PublishResult(existingEventId, IsDuplicate: true));
        }

        var eventId = Interlocked.Increment(ref _nextEventId);
        if (!string.IsNullOrWhiteSpace(context.CorrelationId))
            _correlationEventIds[context.CorrelationId] = eventId;

        return Task.FromResult(new PublishResult(eventId, IsDuplicate: false));
    }

    public sealed record Published(HarnessEvent Event, EventPublishContext Context);
}
