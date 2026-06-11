using System.Collections.Concurrent;
using WeaveFleet.Application.Events;
using WeaveFleet.Domain.Harnesses;

namespace WeaveFleet.Testing.Fakes;

/// <summary>
/// Hand-crafted fake of <see cref="IEventPublisher"/> that records every call for assertion
/// in tests and never contacts a real broker.
/// Thread-safe: safe for concurrent use from multiple prompt tasks.
/// </summary>
public sealed class FakeEventPublisher : IEventPublisher
{
    private long _nextEventId;

    // ConcurrentDictionary provides thread-safe deduplication for concurrent prompt tests.
    private readonly ConcurrentDictionary<string, long> _correlationEventIds = new(StringComparer.Ordinal);

    public ConcurrentQueue<Published> Calls { get; } = new();

    /// <summary>When true, every call to <see cref="PublishAsync"/> throws — used to exercise
    /// the relay's publish-failure-swallow path.</summary>
    public bool ShouldFail { get; set; }

    public Task<PublishResult> PublishAsync(HarnessEvent evt, EventPublishContext context, CancellationToken ct)
    {
        Calls.Enqueue(new Published(evt, context));

        if (ShouldFail) throw new InvalidOperationException("FakeEventPublisher configured to fail.");

        if (string.IsNullOrWhiteSpace(context.CorrelationId))
        {
            var newEventId = Interlocked.Increment(ref _nextEventId);
            return Task.FromResult(new PublishResult(newEventId, IsDuplicate: false));
        }

        // Atomically assign an event ID for this correlation key.
        // GetOrAdd: if key exists, returns existing value (duplicate); if not, inserts and returns new value.
        var isNew = false;
        var assignedId = _correlationEventIds.GetOrAdd(
            context.CorrelationId,
            _ =>
            {
                isNew = true;
                return Interlocked.Increment(ref _nextEventId);
            });

        return Task.FromResult(new PublishResult(assignedId, IsDuplicate: !isNew));
    }

    public sealed record Published(HarnessEvent Event, EventPublishContext Context);
}
