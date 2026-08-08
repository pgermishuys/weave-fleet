using System.Collections.Concurrent;
using System.Diagnostics;
using System.Diagnostics.Metrics;
using WeaveFleet.Application.Diagnostics;

namespace WeaveFleet.Infrastructure.EventBus;

/// <summary>
/// OpenTelemetry metric surface for pipeline latency telemetry.
/// Exposes metrics under <c>weave_fleet.pipeline.*</c> names.
/// Uses the shared <see cref="FleetInstrumentation.Meter"/> so existing exporters pick them up.
/// </summary>
internal sealed class PipelineLatencyMetrics
{
    private readonly Histogram<double> _promptToFirstToken;
    private readonly Histogram<double> _relayToBroadcast;
    private readonly Histogram<double> _publishHop;
    private readonly Histogram<double> _fanoutHop;

    private readonly ConcurrentDictionary<string, long> _pendingPromptTimestamps = new();

    public PipelineLatencyMetrics()
    {
        var meter = FleetInstrumentation.Meter;
        _promptToFirstToken = meter.CreateHistogram<double>(
            "weave_fleet.pipeline.prompt_to_first_token", unit: "ms",
            description: "Time from POST /api/sessions/{id}/prompt arriving to the first TextDelta event being broadcast.");
        _relayToBroadcast = meter.CreateHistogram<double>(
            "weave_fleet.pipeline.relay_to_broadcast", unit: "ms",
            description: "Time from HarnessEventRelay receiving an SSE event to InMemoryEventBroadcaster broadcasting it.");
        _publishHop = meter.CreateHistogram<double>(
            "weave_fleet.pipeline.publish_hop", unit: "ms",
            description: "Time spent in InProcessEventPublisher.PublishAsync (persist vs enqueue vs total).");
        _fanoutHop = meter.CreateHistogram<double>(
            "weave_fleet.pipeline.fanout_hop", unit: "ms",
            description: "Time from event entering FanOut channel to broadcast completing.");
    }

    /// <summary>
    /// Records the timestamp when a prompt arrives at the API endpoint.
    /// Call this from SessionEndpoints.cs prompt handler.
    /// </summary>
    public void RecordPromptArrival(string sessionId)
    {
        _pendingPromptTimestamps[sessionId] = Stopwatch.GetTimestamp();
    }

    /// <summary>
    /// Records the time from prompt arrival to first TextDelta broadcast.
    /// Call this when a TextDelta event is broadcast for a session that has a pending prompt timestamp.
    /// </summary>
    public void RecordPromptToFirstToken(string sessionId)
    {
        if (_pendingPromptTimestamps.TryRemove(sessionId, out var startTimestamp))
        {
            var elapsed = Stopwatch.GetElapsedTime(startTimestamp);
            _promptToFirstToken.Record(elapsed.TotalMilliseconds,
                new KeyValuePair<string, object?>("session_id", sessionId));
        }
    }

    /// <summary>
    /// Records the time from relay receiving an event to broadcaster broadcasting it.
    /// </summary>
    public void RecordRelayToBroadcast(double milliseconds, string eventType, bool isDurable)
        => _relayToBroadcast.Record(milliseconds,
            new KeyValuePair<string, object?>("event_type", eventType),
            new KeyValuePair<string, object?>("is_durable", isDurable));

    /// <summary>
    /// Records the time spent in a specific phase of the publish hop.
    /// </summary>
    public void RecordPublishHop(double milliseconds, string phase, string eventType)
        => _publishHop.Record(milliseconds,
            new KeyValuePair<string, object?>("phase", phase),
            new KeyValuePair<string, object?>("event_type", eventType));

    /// <summary>
    /// Records the time from event entering FanOut channel to broadcast completing.
    /// </summary>
    public void RecordFanoutHop(double milliseconds, string eventType)
        => _fanoutHop.Record(milliseconds,
            new KeyValuePair<string, object?>("event_type", eventType));
}
