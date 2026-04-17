using System.Diagnostics;
using System.Text.Json;
using Microsoft.Extensions.Logging;
using NATS.Client.Core;
using NATS.Client.JetStream;
using WeaveFleet.Application.Configuration;
using WeaveFleet.Application.Events;
using WeaveFleet.Domain.Harnesses;

namespace WeaveFleet.Infrastructure.Nats;

/// <summary>
/// Publishes <see cref="HarnessEvent"/>s to NATS. Durable events (per
/// <see cref="EventTypeMetadata"/>) go to JetStream with an awaited PubAck; ephemeral events go
/// to core NATS fire-and-forget. Sets <c>Nats-Msg-Id</c>, <c>x-fleet-user-id</c>,
/// <c>x-fleet-harness-type</c>, and OTel <c>traceparent</c>/<c>tracestate</c> headers.
/// </summary>
public sealed class NatsEventPublisher : IEventPublisher
{
    private static readonly Action<ILogger, string, Exception?> LogUnknownEventType =
        LoggerMessage.Define<string>(LogLevel.Warning, new EventId(1, "NatsPublishUnknownEventType"),
            "Publish dropped for unclassified event type {EventType} — neither durable nor ephemeral-relay.");

    private readonly Lazy<INatsJSContext> _jsLazy;
    private readonly Lazy<INatsConnection> _connectionLazy;
    private readonly NatsNamingStrategy _naming;
    private readonly NatsMetrics _metrics;
    private readonly NatsOptions _options;
    private readonly ILogger<NatsEventPublisher> _logger;

    public NatsEventPublisher(
        Lazy<INatsJSContext> js,
        Lazy<INatsConnection> connection,
        NatsNamingStrategy naming,
        NatsMetrics metrics,
        NatsOptions options,
        ILogger<NatsEventPublisher> logger)
    {
        _jsLazy = js;
        _connectionLazy = connection;
        _naming = naming;
        _metrics = metrics;
        _options = options;
        _logger = logger;
    }

    public async Task PublishAsync(HarnessEvent evt, EventPublishContext context, CancellationToken ct)
    {
        var classification = EventTypeMetadata.Classify(evt.Type);

        if (classification.IsDurable)
        {
            await PublishDurableAsync(evt, context, ct).ConfigureAwait(false);
            return;
        }

        if (classification.IsEphemeralRelay)
        {
            await PublishEphemeralAsync(evt, context, ct).ConfigureAwait(false);
            return;
        }

        if (!classification.IsKnown)
            LogUnknownEventType(_logger, evt.Type, null);
        _metrics.RecordPublish(routing: "dropped", eventType: evt.Type, tenant: _options.TenantPrefix, result: "ok");
    }

    private async Task PublishDurableAsync(HarnessEvent evt, EventPublishContext context, CancellationToken ct)
    {
        var subject = _naming.DurableSubject(context.ProjectId, context.FleetSessionId, evt.Type);
        var payload = JsonSerializer.SerializeToUtf8Bytes(evt);
        var msgId = $"{context.FleetSessionId}:{context.Sequence}";
        var headers = new NatsHeaders
        {
            ["Nats-Msg-Id"] = msgId,
        };
        if (context.UserId is { Length: > 0 }) headers["x-fleet-user-id"] = context.UserId;
        if (context.HarnessType is { Length: > 0 }) headers["x-fleet-harness-type"] = context.HarnessType;
        InjectTraceContext(headers);

        var sw = Stopwatch.StartNew();
        string result = "ok";
        try
        {
            var ack = await _jsLazy.Value.PublishAsync(
                subject: subject,
                data: payload,
                headers: headers,
                opts: new NatsJSPubOpts { MsgId = msgId },
                cancellationToken: ct).ConfigureAwait(false);
            if (ack.Error is not null)
                throw new NatsJSApiException(ack.Error);
            if (ack.Duplicate)
                result = "duplicate";
        }
        catch (Exception)
        {
            result = "error";
            throw;
        }
        finally
        {
            sw.Stop();
            _metrics.RecordPublishDuration(sw.Elapsed.TotalMilliseconds, evt.Type, _options.TenantPrefix, result);
            _metrics.RecordPublish(routing: "durable", eventType: evt.Type, tenant: _options.TenantPrefix, result: result);
        }
    }

    private async Task PublishEphemeralAsync(HarnessEvent evt, EventPublishContext context, CancellationToken ct)
    {
        var subject = _naming.EphemeralSubject(context.ProjectId, context.FleetSessionId, evt.Type);
        var payload = JsonSerializer.SerializeToUtf8Bytes(evt);
        var headers = new NatsHeaders();
        if (context.UserId is { Length: > 0 }) headers["x-fleet-user-id"] = context.UserId;
        if (context.HarnessType is { Length: > 0 }) headers["x-fleet-harness-type"] = context.HarnessType;
        InjectTraceContext(headers);

        string result = "ok";
        try
        {
            await _connectionLazy.Value.PublishAsync(subject, payload, headers: headers, cancellationToken: ct).ConfigureAwait(false);
        }
        catch (Exception)
        {
            result = "error";
            throw;
        }
        finally
        {
            _metrics.RecordPublish(routing: "ephemeral", eventType: evt.Type, tenant: _options.TenantPrefix, result: result);
        }
    }

    private static void InjectTraceContext(NatsHeaders headers)
    {
        var activity = Activity.Current;
        if (activity is null) return;
        DistributedContextPropagator.Current.Inject(activity, headers, static (carrier, key, value) =>
        {
            if (carrier is NatsHeaders h) h[key] = value;
        });
    }
}
