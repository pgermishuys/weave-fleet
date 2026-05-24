#pragma warning disable CA1848, CA1873 // Temporary diagnostic logging
using System.Diagnostics;
using Microsoft.Extensions.Logging;
using WeaveFleet.Application.Events;
using WeaveFleet.Domain.Harnesses;

namespace WeaveFleet.Infrastructure.EventBus;

/// <summary>
/// In-process implementation of <see cref="IEventPublisher"/>.
/// <list type="bullet">
///   <item><b>Durable events</b> — persisted to <c>inproc_events</c> via
///     <see cref="InProcessEventStore"/>, then written to both the projection wake-up channel
///     and the fan-out channel. Duplicate <c>message_id</c> values are silently dropped.</item>
///   <item><b>Ephemeral events</b> — written to the fan-out channel only (no persistence).</item>
///   <item><b>Unknown events</b> — logged and counted as "dropped"; no channel write.</item>
/// </list>
/// </summary>
internal sealed class InProcessEventPublisher : IEventPublisher
{
    private static readonly Action<ILogger, string, Exception?> LogUnknownEventType =
        LoggerMessage.Define<string>(LogLevel.Warning, new EventId(1, "InProcPublishUnknownEventType"),
            "Publish dropped for unclassified event type {EventType} — neither durable nor ephemeral-relay.");

    private readonly InProcessEventStore _store;
    private readonly InProcessChannels _channels;
    private readonly InProcessMetrics _metrics;
    private readonly ILogger<InProcessEventPublisher> _logger;

    public InProcessEventPublisher(
        InProcessEventStore store,
        InProcessChannels channels,
        InProcessMetrics metrics,
        ILogger<InProcessEventPublisher> logger)
    {
        _store = store;
        _channels = channels;
        _metrics = metrics;
        _logger = logger;
    }

    public Task<PublishResult> PublishAsync(HarnessEvent evt, EventPublishContext context, CancellationToken ct)
    {
        var classification = EventTypeMetadata.Classify(evt.Type);

        _logger.LogDebug("[Publisher] type={Type} session={Session} isDurable={IsDurable} isEphemeral={IsEphemeral} isKnown={IsKnown}",
            evt.Type, context.FleetSessionId, classification.IsDurable, classification.IsEphemeralRelay, classification.IsKnown);

        if (classification.IsDurable)
        {
            return Task.FromResult(PublishDurable(evt, context));
        }

        if (classification.IsEphemeralRelay)
        {
            PublishEphemeral(evt, context);
            return Task.FromResult(new PublishResult(EventId: null, IsDuplicate: false));
        }

        if (!classification.IsKnown)
            LogUnknownEventType(_logger, evt.Type, null);
        _metrics.RecordPublish(routing: "dropped", eventType: evt.Type, result: "ok");
        return Task.FromResult(new PublishResult(EventId: null, IsDuplicate: false));
    }

    private PublishResult PublishDurable(HarnessEvent evt, EventPublishContext context)
    {
        var projectId = context.ProjectId ?? "scratch";
        var tenant    = "tenant.default";
        var messageId = string.IsNullOrWhiteSpace(context.CorrelationId)
            ? $"{context.FleetSessionId}:{context.InternalPumpDedupKey}"
            : $"{context.FleetSessionId}:correlation:{context.CorrelationId}";

        var envelope = new InProcessEnvelope(
            @event:               evt,
            messageId:            messageId,
            tenant:               tenant,
            projectId:            projectId,
            sessionId:            context.FleetSessionId,
            eventType:            evt.Type,
            userId:               context.UserId,
            harnessType:          context.HarnessType,
            internalPumpDedupKey: context.InternalPumpDedupKey,
            isDurable:            true)
        {
            DomainEvent = context.DomainEvent
        };

        var sw = Stopwatch.StartNew();
        string result = "ok";
        try
        {
            var appendResult = _store.AppendIdempotent(envelope);
            if (appendResult.IsDuplicate)
            {
                result = "duplicate";
                _metrics.RecordPublish(routing: "durable", eventType: evt.Type, result: result);
                return new PublishResult(appendResult.EventId, IsDuplicate: true);
            }

            envelope.EventId = appendResult.EventId;

            // Wake up the projection host to process this event.
            _channels.ProjectionWakeUp.Writer.TryWrite(null!);

            // Forward to the fan-out channel so WebSocket clients receive it immediately.
            _channels.FanOut.Writer.TryWrite(envelope);
            return new PublishResult(appendResult.EventId, IsDuplicate: false);
        }
        catch
        {
            result = "error";
            throw;
        }
        finally
        {
            sw.Stop();
            _metrics.RecordPublishDuration(sw.Elapsed.TotalMilliseconds, evt.Type, result);
            _metrics.RecordPublish(routing: "durable", eventType: evt.Type, result: result);
        }
    }

    private void PublishEphemeral(HarnessEvent evt, EventPublishContext context)
    {
        var projectId = context.ProjectId ?? "scratch";
        var tenant    = "tenant.default";

        var envelope = new InProcessEnvelope(
            @event:               evt,
            messageId:            $"{context.FleetSessionId}:{context.InternalPumpDedupKey}",
            tenant:               tenant,
            projectId:            projectId,
            sessionId:            context.FleetSessionId,
            eventType:            evt.Type,
            userId:               context.UserId,
            harnessType:          context.HarnessType,
            internalPumpDedupKey: context.InternalPumpDedupKey,
            isDurable:            false)
        {
            DomainEvent = context.DomainEvent
        };

        string result = "ok";
        try
        {
            _channels.FanOut.Writer.TryWrite(envelope);
        }
        catch
        {
            result = "error";
            throw;
        }
        finally
        {
            _metrics.RecordPublish(routing: "ephemeral", eventType: evt.Type, result: result);
        }
    }
}
