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
    public ConcurrentQueue<Published> Calls { get; } = new();

    /// <summary>When true, every call to <see cref="PublishAsync"/> throws — used to exercise
    /// the relay's publish-failure-swallow path.</summary>
    public bool ShouldFail { get; set; }

    public Task PublishAsync(HarnessEvent evt, EventPublishContext context, CancellationToken ct)
    {
        Calls.Enqueue(new Published(evt, context));
        if (ShouldFail) throw new InvalidOperationException("FakeEventPublisher configured to fail.");
        return Task.CompletedTask;
    }

    public sealed record Published(HarnessEvent Event, EventPublishContext Context);
}
