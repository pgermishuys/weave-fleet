using WeaveFleet.Domain.Harnesses;

namespace WeaveFleet.Application.Services;

/// <summary>
/// Durable persistence entry-point for harness events. Writes SQLite rows and, through the
/// existing outbox, fans events out to WebSocket clients.
/// </summary>
public interface IHarnessEventPersister
{
    /// <summary>Persist a durable event. No-op for events not classified as durable.</summary>
    Task HandleAsync(string fleetSessionId, string ownerUserId, HarnessEvent evt, CancellationToken ct);

    /// <summary>Buffer a text delta so it can be merged into the next message.updated write.</summary>
    void BufferTextDelta(string fleetSessionId, HarnessEvent evt);

    /// <summary>
    /// Flush any unflushed text deltas for <paramref name="fleetSessionId"/> as a synthetic
    /// <c>message.updated</c> persistence call. Called when a pump disconnects to preserve
    /// partial streaming content.
    /// </summary>
    Task FlushBufferedDeltasAsync(string fleetSessionId, string ownerUserId, CancellationToken ct);
}
