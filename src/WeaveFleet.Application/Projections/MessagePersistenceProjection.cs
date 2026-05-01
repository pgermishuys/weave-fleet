using System.Text.Json;
using WeaveFleet.Application.Services;
using WeaveFleet.Domain.Entities;
using WeaveFleet.Domain.Harnesses;
using WeaveFleet.Domain.Repositories;

namespace WeaveFleet.Application.Projections;

/// <summary>
/// Durable projection that writes every harness event to the SQLite read model via
/// <see cref="IHarnessEventPersister"/>. Also appends a row to the per-session
/// <c>harness_events</c> log so the <c>/api/sessions/{id}/committed-events</c> gap-fill API
/// has a queryable source after the unified-fan-out refactor moved harness events off the
/// outbox dispatcher path.
/// </summary>
public sealed class MessagePersistenceProjection : IProjection<HarnessEvent>
{
    private readonly IHarnessEventPersister _persister;
    private readonly IHarnessEventLogRepository _logRepository;

    public MessagePersistenceProjection(
        IHarnessEventPersister persister,
        IHarnessEventLogRepository logRepository)
    {
        _persister = persister;
        _logRepository = logRepository;
    }

    public string Name => "message-persistence";

    public async Task HandleAsync(HarnessEvent evt, ProjectionContext ctx, CancellationToken ct)
    {
        if (ctx.UserId is null) return;

        await _persister.HandleAsync(ctx.FleetSessionId, ctx.UserId, evt, ct).ConfigureAwait(false);

        // Log every durable event regardless of whether the persister wrote a row.
        // Idempotent on (session_id, sequence_number) so JetStream redelivery is safe.
        if (ctx.PublishSequence > 0)
        {
            var payloadJson = evt.Payload.HasValue
                ? evt.Payload.Value.GetRawText()
                : "{}";

            await _logRepository.AppendAsync(new HarnessEventLogEntry
            {
                SessionId = ctx.FleetSessionId,
                SequenceNumber = ctx.PublishSequence,
                Type = evt.Type,
                Payload = payloadJson,
                UserId = ctx.UserId,
                CreatedAt = DateTimeOffset.UtcNow.ToString("O"),
            }).ConfigureAwait(false);
        }
    }
}
