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

        if (evt.Type == EventTypes.UserPromptCommitted)
            return;

        if (IsUserMessageEcho(evt))
            return;

        await _persister.HandleAsync(ctx.FleetSessionId, ctx.UserId, evt, ct).ConfigureAwait(false);

        // Log every durable event regardless of whether the persister wrote a row.
        // event_id is the public replay cursor; sequence_number remains a compatibility alias
        // for event_id and must not use the relay pump's internal dedup key.
        if (ctx.StreamSequence > 0)
        {
            var payloadJson = evt.Payload.HasValue
                ? evt.Payload.Value.GetRawText()
                : "{}";

            await _logRepository.AppendAsync(new HarnessEventLogEntry
            {
                SessionId = ctx.FleetSessionId,
                EventId = ctx.StreamSequence,
                SequenceNumber = ctx.StreamSequence,
                Type = evt.Type,
                Payload = payloadJson,
                UserId = ctx.UserId,
                CreatedAt = DateTimeOffset.UtcNow.ToString("O"),
            }).ConfigureAwait(false);
        }
    }

    private static bool IsUserMessageEcho(HarnessEvent evt)
    {
        if (evt.Type is EventTypes.MessageCreated or EventTypes.MessageUpdated)
            return HasUserRole(evt.Payload);

        return false;
    }

    private static bool HasUserRole(JsonElement? payload)
    {
        if (!payload.HasValue
            || payload.Value.ValueKind != JsonValueKind.Object
            || !payload.Value.TryGetProperty("info", out var info)
            || info.ValueKind != JsonValueKind.Object
            || !info.TryGetProperty("role", out var role))
        {
            return false;
        }

        return role.GetString() is "user";
    }

}
