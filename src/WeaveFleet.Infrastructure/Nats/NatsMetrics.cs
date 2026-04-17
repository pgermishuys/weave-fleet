using System.Diagnostics.Metrics;
using WeaveFleet.Application.Diagnostics;

namespace WeaveFleet.Infrastructure.Nats;

/// <summary>
/// OpenTelemetry metric surface for the NATS event substrate. Uses the existing
/// <see cref="FleetInstrumentation.Meter"/> so exporters already shipping metrics do not need
/// reconfiguration.
/// </summary>
/// <remarks>
/// The <c>tenant</c> dimension is low-cardinality (=1) under local/self-hosted (always
/// <c>tenant.default</c>). Under managed-cloud rollout it becomes per-workspace; replace with a
/// <c>tenant_class</c> bucket or hash before GA to keep cardinality bounded.
/// </remarks>
public sealed class NatsMetrics
{
    private readonly Counter<long> _publishCount;
    private readonly Histogram<double> _publishDuration;
    private readonly Counter<long> _projectionAckCount;
    private readonly Histogram<double> _projectionHandlerDuration;
    private readonly Counter<long> _reconnectCount;

    public NatsMetrics()
    {
        var meter = FleetInstrumentation.Meter;
        _publishCount = meter.CreateCounter<long>(
            "weave_fleet.nats.publish.count", unit: "events", description: "NATS publish attempts.");
        _publishDuration = meter.CreateHistogram<double>(
            "weave_fleet.nats.publish.duration", unit: "ms", description: "Time from publish to PubAck (durable path only).");
        _projectionAckCount = meter.CreateCounter<long>(
            "weave_fleet.nats.projection.ack.count", unit: "acks", description: "Projection ack/nak/term outcomes.");
        _projectionHandlerDuration = meter.CreateHistogram<double>(
            "weave_fleet.nats.projection.handler.duration", unit: "ms", description: "Projection handler duration.");
        _reconnectCount = meter.CreateCounter<long>(
            "weave_fleet.nats.reconnect.count", unit: "reconnects", description: "NATS client reconnects.");
    }

    public void RecordPublish(string routing, string eventType, string tenant, string result)
        => _publishCount.Add(1,
            new KeyValuePair<string, object?>("routing", routing),
            new KeyValuePair<string, object?>("event_type", eventType),
            new KeyValuePair<string, object?>("tenant", tenant),
            new KeyValuePair<string, object?>("result", result));

    public void RecordPublishDuration(double milliseconds, string eventType, string tenant, string result)
        => _publishDuration.Record(milliseconds,
            new KeyValuePair<string, object?>("event_type", eventType),
            new KeyValuePair<string, object?>("tenant", tenant),
            new KeyValuePair<string, object?>("result", result));

    public void RecordProjectionAck(string projection, string result)
        => _projectionAckCount.Add(1,
            new KeyValuePair<string, object?>("projection", projection),
            new KeyValuePair<string, object?>("result", result));

    public void RecordProjectionHandler(double milliseconds, string projection, string eventType, string result)
        => _projectionHandlerDuration.Record(milliseconds,
            new KeyValuePair<string, object?>("projection", projection),
            new KeyValuePair<string, object?>("event_type", eventType),
            new KeyValuePair<string, object?>("result", result));

    public void RecordReconnect() => _reconnectCount.Add(1);
}
