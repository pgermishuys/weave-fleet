#pragma warning disable CA1848, CA1873 // Temporary diagnostic logging
using System.Diagnostics;
using System.Globalization;
using Microsoft.Extensions.Logging;
using WeaveFleet.Application.Diagnostics;
using WeaveFleet.Application.Events;
using WeaveFleet.Domain.Harnesses;
using WeaveFleet.Infrastructure.Services;

namespace WeaveFleet.Infrastructure.EventBus;

/// <summary>
/// In-process implementation of <see cref="IEventPublisher"/>.
/// <list type="bullet">
///   <item><b>Durable events</b> — assigned a provisional negative ID and broadcast immediately
///     to the fan-out channel and automation channel. No persistence or duplicate detection.</item>
///   <item><b>Ephemeral events</b> — written to the fan-out channel only (no persistence).</item>
///   <item><b>Unknown events</b> — logged and counted as "dropped"; no channel write.</item>
/// </list>
/// </summary>
internal sealed class InProcessEventPublisher : IEventPublisher
{
    private static readonly Action<ILogger, string, Exception?> LogUnknownEventType =
        LoggerMessage.Define<string>(LogLevel.Warning, new EventId(1, "InProcPublishUnknownEventType"),
            "Publish dropped for unclassified event type {EventType} — neither durable nor ephemeral-relay.");

    private readonly InProcessChannels _channels;
    private readonly InProcessMetrics _metrics;
    private readonly PipelineLatencyMetrics _pipelineMetrics;
    private readonly ILogger<InProcessEventPublisher> _logger;

    /// <summary>
    /// Monotonic counter for provisional event IDs used during broadcast before SQLite persistence.
    /// Negative IDs ensure no collision with SQLite rowids (which are always positive).
    /// </summary>
    private long _nextProvisionalId = -1;

    public InProcessEventPublisher(
        InProcessChannels channels,
        InProcessMetrics metrics,
        PipelineLatencyMetrics pipelineMetrics,
        ILogger<InProcessEventPublisher> logger)
    {
        _channels = channels;
        _metrics = metrics;
        _pipelineMetrics = pipelineMetrics;
        _logger = logger;
    }

    public Task<PublishResult> PublishAsync(HarnessEvent evt, EventPublishContext context, CancellationToken ct)
    {
        using var activity = FleetInstrumentation.ActivitySource.StartActivity(
            "fleet.publish",
            ActivityKind.Internal,
            context.TraceContext ?? default);
        activity?.SetTag(FleetInstrumentation.SessionIdTag, context.FleetSessionId);
        activity?.SetTag("event.type", evt.Type);

        var classification = EventTypeMetadata.Classify(evt.Type);
        activity?.SetTag("event.routing", classification.IsDurable ? "durable" : classification.IsEphemeralRelay ? "ephemeral" : "dropped");

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
            DomainEvent = context.DomainEvent,
            SourceReference = context.SourceReference,
            TraceContext = context.TraceContext
        };

        var sw = Stopwatch.StartNew();
        string result = "ok";
        try
        {
            // 1. Assign provisional negative ID for immediate broadcast
            //    (negative IDs never collide with positive SQLite rowids)
            var provisionalId = Interlocked.Decrement(ref _nextProvisionalId);
            envelope.EventId = provisionalId;

            // 2. Broadcast to clients IMMEDIATELY (non-blocking channel write)
            var broadcastStart = sw.Elapsed.TotalMilliseconds;
            _channels.FanOut.Writer.TryWrite(envelope);
            var broadcastEnd = sw.Elapsed.TotalMilliseconds;

            // 3. Send to automation dispatcher
            WriteToAutomationChannel(envelope);

            _logger.LogDebug(
                "[Publisher:Durable] type={Type} broadcast={BroadcastMs:F1}ms total={TotalMs:F1}ms provisionalId={ProvisionalId}",
                evt.Type, broadcastEnd - broadcastStart, sw.Elapsed.TotalMilliseconds, provisionalId);

            return new PublishResult(provisionalId, IsDuplicate: false);
        }
        catch
        {
            result = "error";
            throw;
        }
        finally
        {
            sw.Stop();
            _pipelineMetrics.RecordPublishHop(sw.Elapsed.TotalMilliseconds, "total", evt.Type);
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
            DomainEvent = context.DomainEvent,
            SourceReference = context.SourceReference,
            TraceContext = context.TraceContext
        };

        string result = "ok";
        try
        {
            _channels.FanOut.Writer.TryWrite(envelope);
            WriteToAutomationChannel(envelope);
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

    private void WriteToAutomationChannel(InProcessEnvelope envelope)
    {
        // Map envelope to AutomationEventNotification
        // EventId: use the envelope's EventId if available (durable), otherwise generate from messageId
        var eventId = envelope.EventId?.ToString(CultureInfo.InvariantCulture) ?? envelope.MessageId;

        // EventSummary: use domain event type if available, otherwise harness event type
        var eventSummary = envelope.DomainEvent?.GetType().Name ?? envelope.EventType;

        var notification = new AutomationEventNotification(
            EventType: envelope.EventType,
            EventId: eventId,
            SessionId: envelope.SessionId,
            SessionSourceReference: envelope.SourceReference,
            EventSummary: eventSummary);

        _channels.AutomationEvents.Writer.TryWrite(notification);
    }
}
