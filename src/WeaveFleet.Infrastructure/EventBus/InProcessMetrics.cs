using System.Diagnostics.Metrics;
using WeaveFleet.Application.Diagnostics;

namespace WeaveFleet.Infrastructure.EventBus;

/// <summary>
/// OpenTelemetry metric surface for the in-process event bus.
/// Exposes metrics under <c>weave_fleet.inproc.*</c> names.
/// Uses the shared <see cref="FleetInstrumentation.Meter"/> so existing exporters pick them up.
/// </summary>
internal sealed class InProcessMetrics
{
    private readonly Counter<long> _publishCount;
    private readonly Histogram<double> _publishDuration;
    private readonly Counter<long> _projectionDispatchCount;
    private readonly Histogram<double> _projectionHandlerDuration;

    public InProcessMetrics()
    {
        var meter = FleetInstrumentation.Meter;
        _publishCount = meter.CreateCounter<long>(
            "weave_fleet.inproc.publish.count", unit: "events",
            description: "In-process bus publish attempts.");
        _publishDuration = meter.CreateHistogram<double>(
            "weave_fleet.inproc.publish.duration", unit: "ms",
            description: "Time to persist and enqueue a durable event.");
        _projectionDispatchCount = meter.CreateCounter<long>(
            "weave_fleet.inproc.projection.dispatch.count", unit: "dispatches",
            description: "In-process projection dispatch outcomes (ok/skip/retry).");
        _projectionHandlerDuration = meter.CreateHistogram<double>(
            "weave_fleet.inproc.projection.handler.duration", unit: "ms",
            description: "In-process projection handler duration.");
    }

    public void RecordPublish(string routing, string eventType, string result)
        => _publishCount.Add(1,
            new KeyValuePair<string, object?>("routing", routing),
            new KeyValuePair<string, object?>("event_type", eventType),
            new KeyValuePair<string, object?>("result", result));

    public void RecordPublishDuration(double milliseconds, string eventType, string result)
        => _publishDuration.Record(milliseconds,
            new KeyValuePair<string, object?>("event_type", eventType),
            new KeyValuePair<string, object?>("result", result));

    public void RecordProjectionDispatch(string projection, string result)
        => _projectionDispatchCount.Add(1,
            new KeyValuePair<string, object?>("projection", projection),
            new KeyValuePair<string, object?>("result", result));

    public void RecordProjectionHandler(double milliseconds, string projection, string eventType, string result)
        => _projectionHandlerDuration.Record(milliseconds,
            new KeyValuePair<string, object?>("projection", projection),
            new KeyValuePair<string, object?>("event_type", eventType),
            new KeyValuePair<string, object?>("result", result));
}
