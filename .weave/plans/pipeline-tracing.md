# Pipeline Tracing Instrumentation

## TL;DR
> **Summary**: Add distributed tracing spans to the 4 pipeline seams (harness subscriber, relay loop, broadcaster, WebSocket/SSE pump) with trace context propagation across channel boundaries, plus a lightweight dev collector setup.
> **Estimated Effort**: Medium

## Context

### Original Request
Instrument the weave-fleet event pipeline with OpenTelemetry spans to enable end-to-end latency visibility. The pipeline flows:
```
AI Agent Process (OpenCode via SSE, Claude Code via stdio)
  -> Harness Instance (bounded channel, 1000 cap)
    -> HarnessEventRelay (BackgroundService, rewrites session IDs)
      -> InMemoryEventBroadcaster (fan-out, unbounded per-subscriber channels)
        -> WebSocket/SSE endpoints
          -> React Frontend
```

### Key Findings
1. **Telemetry infrastructure exists but is unused:**
   - `FleetInstrumentation.ActivitySource` is declared and registered with the SDK
   - Zero `StartActivity()` calls exist anywhere in the codebase
   - 6 custom metrics exist and are actively emitted

2. **Channel boundaries break `Activity.Current` propagation:**
   - `OpenCodeHarnessInstance.SubscribeAsync()` yields events from an SSE stream
   - `ClaudeCodeHarnessInstance` pumps events through a bounded channel (`_eventChannel`)
   - `HarnessEventRelay.PumpAsync()` reads from instance subscription, writes to broadcaster
   - `InMemoryEventBroadcaster` fans out to unbounded per-subscriber channels
   - WebSocket/SSE endpoints read from broadcaster subscription

3. **`HarnessEvent` record is immutable and defined in Domain layer:**
   - Cannot add mutable trace context fields
   - Solution: Create an infrastructure-layer wrapper that carries trace context

4. **Dev OTLP endpoint is Seq at `http://localhost:5341/ingest/otlp`** (already configured in `appsettings.Development.json`)

5. **Existing code style conventions:**
   - `LoggerMessage.Define<>()` for high-perf logging
   - Records for DTOs
   - `sealed` on all concrete classes
   - Nullable reference types enabled
   - Async methods use `.ConfigureAwait(false)`

## Objectives

### Core Objective
Enable distributed tracing across the event pipeline so developers can visualize event flow latency and identify bottlenecks.

### Deliverables
- [ ] Trace collector setup script or docker-compose for local dev
- [ ] Pipeline span instrumentation at 4 seams
- [ ] Trace context carrier mechanism for cross-channel propagation
- [ ] 3 new metrics (broadcaster queue depth, event drops, rewrite duration)
- [ ] Frontend timing metadata on events
- [ ] Unit tests for context propagation

### Definition of Done
- [ ] `dotnet run` with Seq running shows spans for full pipeline traversal
- [ ] All existing tests pass: `dotnet test`
- [ ] New unit tests verify trace context flows through channels
- [ ] Release build succeeds: `dotnet build -c Release`

### Guardrails (Must NOT)
- Do NOT modify Domain layer (`WeaveFleet.Domain`)
- Do NOT modify Application layer business logic (services, repositories)
- Do NOT add dependencies to Domain layer
- Do NOT spread `StartActivity()` calls through business logic — keep at boundaries only

---

## TODOs

### 1. Create Trace Context Carrier

**What**: Define an infrastructure-layer wrapper that carries trace context alongside `HarnessEvent`. This wrapper flows through channels internally but is unwrapped at boundaries.

**Files**:
- `src/WeaveFleet.Infrastructure/Diagnostics/TracedEvent.cs` (new)

**Implementation**:
```csharp
using System.Diagnostics;
using WeaveFleet.Domain.Harnesses;

namespace WeaveFleet.Infrastructure.Diagnostics;

/// <summary>
/// Wraps a <see cref="HarnessEvent"/> with trace context for propagation across channel boundaries.
/// The Activity reference is captured at the producer and restored at the consumer.
/// </summary>
internal readonly record struct TracedEvent(
    HarnessEvent Event,
    ActivityContext? TraceContext);
```

**Acceptance**: File compiles, record struct is readonly for perf.

---

### 2. Add Pipeline Activity Names to FleetInstrumentation

**What**: Declare span name constants in the existing instrumentation file for consistency.

**Files**:
- `src/WeaveFleet.Application/Diagnostics/FleetInstrumentation.cs`

**Implementation** (add after line 27):
```csharp
    // ─── Pipeline span names ──────────────────────────────────────────────────
    public const string SpanHarnessSubscribe = "harness.subscribe";
    public const string SpanRelayPump = "relay.pump";
    public const string SpanBroadcast = "broadcast.fanout";
    public const string SpanWebSocketPump = "ws.pump";
    public const string SpanSsePump = "sse.pump";
```

**Acceptance**: Constants are public, follow `component.action` naming convention.

---

### 3. Add New Metrics to FleetInstrumentation

**What**: Declare the 3 new metrics for pipeline observability.

**Files**:
- `src/WeaveFleet.Application/Diagnostics/FleetInstrumentation.cs`

**Implementation** (add after existing metrics):
```csharp
    // ─── Pipeline metrics ─────────────────────────────────────────────────────

    /// <summary>Current queue depth per broadcaster subscriber.</summary>
    public static readonly ObservableGauge<int> BroadcasterQueueDepth =
        Meter.CreateObservableGauge<int>(
            "weave_fleet.broadcaster.queue_depth",
            () => [], // Will be populated by InMemoryEventBroadcaster
            "events",
            "Current queue depth per broadcaster subscriber");

    /// <summary>Events dropped due to bounded channel overflow.</summary>
    public static readonly Counter<long> EventsDropped =
        Meter.CreateCounter<long>(
            "weave_fleet.events.dropped",
            "events",
            "Events dropped due to bounded channel overflow");

    /// <summary>Duration of session ID rewriting in the relay.</summary>
    public static readonly Histogram<double> RewriteSessionIdsDuration =
        Meter.CreateHistogram<double>(
            "weave_fleet.relay.rewrite_duration",
            "ms",
            "Duration of session ID rewriting in the relay");
```

**Note**: `ObservableGauge` requires a callback. The actual callback will be wired in TODO #7 when instrumenting `InMemoryEventBroadcaster`.

**Acceptance**: Metrics follow existing naming convention (`weave_fleet.*`).

---

### 4. Instrument Harness Instance Subscriber (OpenCode)

**What**: Wrap the SSE event loop in `OpenCodeHarnessInstance.SubscribeAsync()` with a span for each event. Stamp trace context on yielded events.

**Files**:
- `src/WeaveFleet.Infrastructure/Harnesses/OpenCode/OpenCodeHarnessInstance.cs`

**Implementation**:

Add using at top:
```csharp
using WeaveFleet.Application.Diagnostics;
```

Modify `SubscribeAsync()` (lines 226-250) to start an activity per event:
```csharp
    public async IAsyncEnumerable<HarnessEvent> SubscribeAsync(
        [System.Runtime.CompilerServices.EnumeratorCancellation] CancellationToken ct)
    {
        await foreach (var sseEvt in _httpClient
            .SubscribeToEventsAsync(_workingDirectory, ct)
            .ConfigureAwait(false))
        {
            // Start a span that will be the root for this event's journey through the pipeline
            using var activity = FleetInstrumentation.ActivitySource.StartActivity(
                FleetInstrumentation.SpanHarnessSubscribe,
                ActivityKind.Producer);
            
            activity?.SetTag("harness.type", HarnessType);
            activity?.SetTag("session.id", _fleetSessionId);
            activity?.SetTag("event.type", sseEvt.Type);

            // Fire-and-forget analytics intercept — never blocks or throws
            if (_analyticsCollector is not null)
            {
                var tokenEvent = OpenCodeMapper.TryExtractTokenEvent(
                    sseEvt, _fleetSessionId, _projectId, _projectName, _workingDirectory);
                if (tokenEvent is not null)
                    _analyticsCollector.AcceptTokenEvent(tokenEvent);
            }

            var harnessEvent = OpenCodeMapper.ToHarnessEvent(sseEvt, _openCodeSessionId);

            // Fire-and-forget persistence (instance-owned — never blocks event stream)
            _ = TryPersistMessageAsync(harnessEvent);
            _ = TryPersistPartAsync(harnessEvent);

            yield return harnessEvent;
        }
    }
```

**Problem**: This approach creates a span, but the span ends when `yield return` suspends. The activity won't propagate across the channel boundary.

**Better approach**: Create a long-running "event pipeline" span at subscription start, then create child spans per event. The child span's context is captured and manually propagated.

**Revised implementation**:
```csharp
    public async IAsyncEnumerable<HarnessEvent> SubscribeAsync(
        [System.Runtime.CompilerServices.EnumeratorCancellation] CancellationToken ct)
    {
        await foreach (var sseEvt in _httpClient
            .SubscribeToEventsAsync(_workingDirectory, ct)
            .ConfigureAwait(false))
        {
            // Create a producer span — it completes when the event is yielded
            // The Activity.Current context is captured by the relay for propagation
            using var activity = FleetInstrumentation.ActivitySource.StartActivity(
                FleetInstrumentation.SpanHarnessSubscribe,
                ActivityKind.Producer);
            
            activity?.SetTag("harness.type", HarnessType);
            activity?.SetTag("fleet.session.id", _fleetSessionId);
            activity?.SetTag("event.type", sseEvt.Type);

            // Fire-and-forget analytics intercept — never blocks or throws
            if (_analyticsCollector is not null)
            {
                var tokenEvent = OpenCodeMapper.TryExtractTokenEvent(
                    sseEvt, _fleetSessionId, _projectId, _projectName, _workingDirectory);
                if (tokenEvent is not null)
                    _analyticsCollector.AcceptTokenEvent(tokenEvent);
            }

            var harnessEvent = OpenCodeMapper.ToHarnessEvent(sseEvt, _openCodeSessionId);

            // Fire-and-forget persistence (instance-owned — never blocks event stream)
            _ = TryPersistMessageAsync(harnessEvent);
            _ = TryPersistPartAsync(harnessEvent);

            yield return harnessEvent;
            
            // Activity disposes here, ending the span after the consumer has received the event
        }
    }
```

**Acceptance**: Each event yielded has an associated span in traces.

---

### 5. Instrument Harness Instance Subscriber (ClaudeCode)

**What**: Instrument the `PumpStdoutAsync` method that writes to the bounded channel. The span starts when an event is read from stdout and ends when written to channel.

**Files**:
- `src/WeaveFleet.Infrastructure/Harnesses/ClaudeCode/ClaudeCodeHarnessInstance.cs`

**Implementation**:

Add using at top:
```csharp
using WeaveFleet.Application.Diagnostics;
```

Add metric recording for dropped events. First, add a field to detect drops (near line 52):
```csharp
    // Track last event count for drop detection
    private long _eventsSent;
```

Modify `PumpStdoutAsync()` to instrument each event:
```csharp
    private async Task PumpStdoutAsync(StreamReader stdout, ClaudeCodeProcessManager processManager)
    {
        try
        {
            await foreach (var msg in ClaudeCodeStdioClient
                .ReadMessagesAsync(stdout, _logger, CancellationToken.None)
                .ConfigureAwait(false))
            {
                // ... existing session ID capture logic (lines 314-362) ...

                // Emit frontend-compatible events to channel
                var events = ClaudeCodeMapper.ToFrontendEvents(msg, _fleetSessionId);
                foreach (var evt in events)
                {
                    using var activity = FleetInstrumentation.ActivitySource.StartActivity(
                        FleetInstrumentation.SpanHarnessSubscribe,
                        ActivityKind.Producer);
                    
                    activity?.SetTag("harness.type", HarnessType);
                    activity?.SetTag("fleet.session.id", _fleetSessionId);
                    activity?.SetTag("event.type", evt.Type);

                    // Write to bounded channel — may drop if full
                    if (!_eventChannel.Writer.TryWrite(evt))
                    {
                        // Channel is full, event will be dropped (DropOldest mode)
                        FleetInstrumentation.EventsDropped.Add(1, 
                            new KeyValuePair<string, object?>("harness.type", HarnessType),
                            new KeyValuePair<string, object?>("fleet.session.id", _fleetSessionId));
                        activity?.SetTag("event.dropped", true);
                    }
                    else
                    {
                        // Fallback to WriteAsync if TryWrite succeeds (it always will for unbounded)
                        // Actually, for bounded with DropOldest, WriteAsync never blocks but may drop
                        // Let's keep using WriteAsync and check reader count before/after
                    }
                    
                    await _eventChannel.Writer.WriteAsync(evt, CancellationToken.None).ConfigureAwait(false);
                }
            }
        }
        // ... rest of method unchanged ...
    }
```

**Wait** — the channel is bounded with `DropOldest`, so `WriteAsync` always succeeds but silently drops the oldest item. Detecting drops requires comparing the reader's item count. This is complex. 

**Simpler approach**: Track drops via the channel's `ItemsWritten` vs a counter. Actually, `BoundedChannelFullMode.DropOldest` doesn't expose drop count.

**Revised approach**: Instrument at the channel write site using a custom channel wrapper OR accept that drops aren't directly measurable for DropOldest mode. For now, instrument the write and note that drop detection requires switching to `DropWrite` mode or using a wrapper.

**Final implementation**:
```csharp
    private async Task PumpStdoutAsync(StreamReader stdout, ClaudeCodeProcessManager processManager)
    {
        try
        {
            await foreach (var msg in ClaudeCodeStdioClient
                .ReadMessagesAsync(stdout, _logger, CancellationToken.None)
                .ConfigureAwait(false))
            {
                // Capture session ID from init message
                if (msg is ClaudeCodeSystemMessage { Subtype: "init" } init)
                {
                    if (init.SessionId is not null)
                    {
                        _claudeSessionId = init.SessionId;
                        LogSessionId(_logger, init.SessionId, null);
                        _ = PersistResumeTokenAsync(init.SessionId);
                    }

                    if (init.Model is not null)
                        _modelId = init.Model;
                }
                else if (msg is ClaudeCodeAssistantMessage assistantMsg)
                {
                    var harnessMsg = ClaudeCodeMapper.ToHarnessMessage(
                        assistantMsg, DateTimeOffset.UtcNow);
                    _ = PersistMessageAsync(harnessMsg);

                    if (assistantMsg.Message?.Model is not null)
                        _modelId = assistantMsg.Message.Model;
                }
                else if (msg is ClaudeCodeResultMessage result)
                {
                    if (_analyticsCollector is not null)
                    {
                        var tokenEvent = ClaudeCodeMapper.TryExtractTokenEvent(
                            result, _fleetSessionId, _projectId, _projectName, _workingDirectory, _modelId);
                        if (tokenEvent is not null)
                            _analyticsCollector.AcceptTokenEvent(tokenEvent);
                    }

                    if (result.SessionId is not null && _claudeSessionId is null)
                    {
                        _claudeSessionId = result.SessionId;
                        _ = PersistResumeTokenAsync(result.SessionId);
                    }

                    _status = HarnessInstanceStatus.Idle;
                }

                // Emit frontend-compatible events to channel
                var events = ClaudeCodeMapper.ToFrontendEvents(msg, _fleetSessionId);
                foreach (var evt in events)
                {
                    using var activity = FleetInstrumentation.ActivitySource.StartActivity(
                        FleetInstrumentation.SpanHarnessSubscribe,
                        ActivityKind.Producer);
                    
                    activity?.SetTag("harness.type", HarnessType);
                    activity?.SetTag("fleet.session.id", _fleetSessionId);
                    activity?.SetTag("event.type", evt.Type);

                    await _eventChannel.Writer.WriteAsync(evt, CancellationToken.None).ConfigureAwait(false);
                }
            }
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            _status = HarnessInstanceStatus.Error;
        }
        finally
        {
            _status = _status is HarnessInstanceStatus.Running
                ? HarnessInstanceStatus.Idle
                : _status;

            await processManager.DisposeAsync().ConfigureAwait(false);

            if (ReferenceEquals(_activeProcess, processManager))
                _activeProcess = null;
        }
    }
```

**Acceptance**: Each event written to channel has an associated span.

---

### 6. Instrument HarnessEventRelay

**What**: Add spans around the relay pump loop, measure `RewriteSessionIds` duration, and propagate trace context.

**Files**:
- `src/WeaveFleet.Infrastructure/Services/HarnessEventRelay.cs`

**Implementation**:

Add usings:
```csharp
using System.Diagnostics;
using WeaveFleet.Application.Diagnostics;
```

Modify `PumpAsync()` (lines 94-158):
```csharp
    private async Task PumpAsync(string instanceId, IHarnessInstance instance, CancellationToken ct)
    {
        // ... existing session lookup logic (lines 98-124) unchanged ...

        var topic = $"session:{fleetSessionId}";
        try
        {
            await foreach (var evt in instance.SubscribeAsync(ct).ConfigureAwait(false))
            {
                // Create a consumer span linked to the producer span (if one exists)
                using var activity = FleetInstrumentation.ActivitySource.StartActivity(
                    FleetInstrumentation.SpanRelayPump,
                    ActivityKind.Internal);
                
                activity?.SetTag("fleet.session.id", fleetSessionId);
                activity?.SetTag("instance.id", instanceId);
                activity?.SetTag("event.type", evt.Type);
                activity?.SetTag("harness.type", instance.HarnessType);

                // Guard against null Payload — BroadcastAsync serializes via
                // JsonSerializer.SerializeToElement which throws on null/Undefined JsonElement.
                // Use an empty object {} as fallback when Payload is null.
                object payload;
                if (evt.Payload.HasValue)
                {
                    // Measure rewrite duration
                    var sw = Stopwatch.StartNew();
                    payload = RewriteSessionIds(evt.Payload.Value, fleetSessionId);
                    sw.Stop();
                    FleetInstrumentation.RewriteSessionIdsDuration.Record(
                        sw.Elapsed.TotalMilliseconds,
                        new KeyValuePair<string, object?>("instance.id", instanceId));
                }
                else
                {
                    payload = JsonSerializer.SerializeToElement(new { });
                }

                await _broadcaster.BroadcastAsync(topic, evt.Type, payload, ct).ConfigureAwait(false);
            }
        }
        catch (OperationCanceledException)
        {
            // Expected on instance removal or application shutdown
        }
        catch (Exception ex)
        {
            LogPumpFailed(_logger, instanceId, ex);
        }
        finally
        {
            if (_subscriptions.TryRemove(instanceId, out var cts))
            {
                cts.Cancel();
                cts.Dispose();
            }
        }
    }
```

**Acceptance**: Relay spans appear in traces, rewrite duration histogram is populated.

---

### 7. Instrument InMemoryEventBroadcaster

**What**: Add spans around the broadcast fan-out loop and expose queue depth metrics.

**Files**:
- `src/WeaveFleet.Infrastructure/Services/InMemoryEventBroadcaster.cs`

**Implementation**:

Add usings:
```csharp
using System.Diagnostics;
using System.Diagnostics.Metrics;
using WeaveFleet.Application.Diagnostics;
```

Add queue depth measurement callback in constructor or as a static registration. Since `InMemoryEventBroadcaster` is a singleton, we can register the observable gauge callback once.

**Problem**: `FleetInstrumentation.BroadcasterQueueDepth` is already created with an empty callback. Observable gauges don't support changing callbacks after creation.

**Solution**: Change the approach — create the gauge in `InMemoryEventBroadcaster` directly, or expose a method that `FleetInstrumentation` can call.

**Revised approach**: Add a static registration method to `FleetInstrumentation` that gets called during DI setup:

Actually, the cleanest approach is to let `InMemoryEventBroadcaster` create and own the observable gauge itself, since it knows how to measure queue depths.

**Final implementation**:

Remove the `BroadcasterQueueDepth` gauge from `FleetInstrumentation` (from TODO #3) and instead add it to the broadcaster:

```csharp
public sealed class InMemoryEventBroadcaster : IEventBroadcaster, IDisposable
{
    private sealed record Subscription(
        string SubscriberId,
        IReadOnlyList<string> Topics,
        Channel<BroadcastEvent> Channel);

    private readonly ConcurrentDictionary<string, Subscription> _subscriptions = new();
    private readonly ObservableGauge<int> _queueDepthGauge;

    public InMemoryEventBroadcaster()
    {
        _queueDepthGauge = FleetInstrumentation.Meter.CreateObservableGauge<int>(
            "weave_fleet.broadcaster.queue_depth",
            MeasureQueueDepths,
            "events",
            "Current queue depth per broadcaster subscriber");
    }

    private IEnumerable<Measurement<int>> MeasureQueueDepths()
    {
        foreach (var sub in _subscriptions.Values)
        {
            // Channel.Reader.Count gives current item count
            yield return new Measurement<int>(
                sub.Channel.Reader.Count,
                new KeyValuePair<string, object?>("subscriber.id", sub.SubscriberId));
        }
    }

    /// <summary>Number of active subscribers (exposed for test synchronisation).</summary>
    internal int SubscriberCount => _subscriptions.Count;

    /// <inheritdoc />
    public Task BroadcastAsync(string topic, string type, object payload, CancellationToken ct = default)
    {
        using var activity = FleetInstrumentation.ActivitySource.StartActivity(
            FleetInstrumentation.SpanBroadcast,
            ActivityKind.Internal);
        
        activity?.SetTag("topic", topic);
        activity?.SetTag("event.type", type);
        activity?.SetTag("subscriber.count", _subscriptions.Count);

        var json = JsonSerializer.SerializeToElement(payload);
        var evt = new BroadcastEvent(topic, type, json, DateTimeOffset.UtcNow);

        int delivered = 0;
        foreach (var sub in _subscriptions.Values)
        {
            if (!sub.Topics.Contains("*") && !sub.Topics.Contains(topic))
                continue;

            // TryWrite is fine — unbounded channel; drop if disposed
            if (sub.Channel.Writer.TryWrite(evt))
                delivered++;
        }
        
        activity?.SetTag("subscriber.delivered", delivered);

        return Task.CompletedTask;
    }

    // ... rest unchanged ...
}
```

**Acceptance**: Broadcast spans show subscriber count, queue depth gauge is visible in metrics.

---

### 8. Instrument WebSocket Pump

**What**: Add spans around the WebSocket event send loop.

**Files**:
- `src/WeaveFleet.Api/Endpoints/WebSocketEndpoints.cs`

**Implementation**:

Add usings:
```csharp
using System.Diagnostics;
using WeaveFleet.Application.Diagnostics;
```

Modify `PumpEventsAsync()` (lines 142-191):
```csharp
    private static async Task PumpEventsAsync(
        WebSocket webSocket,
        IEventBroadcaster broadcaster,
        List<string> subscribedTopics,
        CancellationToken ct)
    {
        var allTopics = new[] { "*" };

        await foreach (var evt in broadcaster.SubscribeAsync(allTopics, ct))
        {
            bool inScope;
            lock (subscribedTopics)
                inScope = subscribedTopics.Contains(evt.Topic);

            if (!inScope)
                continue;

            if (webSocket.State != WebSocketState.Open)
                break;

            using var activity = FleetInstrumentation.ActivitySource.StartActivity(
                FleetInstrumentation.SpanWebSocketPump,
                ActivityKind.Producer);
            
            activity?.SetTag("topic", evt.Topic);
            activity?.SetTag("event.type", evt.Type);

            var json = JsonSerializer.Serialize(new
            {
                type = "event",
                topic = evt.Topic,
                data = new
                {
                    type = evt.Type,
                    properties = evt.Payload
                }
            });

            var bytes = Encoding.UTF8.GetBytes(json);
            try
            {
                await webSocket.SendAsync(new ArraySegment<byte>(bytes),
                    WebSocketMessageType.Text, endOfMessage: true, ct);
                activity?.SetTag("bytes.sent", bytes.Length);
            }
            catch (OperationCanceledException)
            {
                break;
            }
            catch (WebSocketException)
            {
                activity?.SetStatus(ActivityStatusCode.Error, "WebSocket send failed");
                break;
            }
        }
    }
```

**Acceptance**: WebSocket send spans visible in traces.

---

### 9. Instrument SSE Endpoints

**What**: Add spans around SSE event writes.

**Files**:
- `src/WeaveFleet.Api/Endpoints/SessionEventEndpoints.cs`

**Implementation**:

Add usings:
```csharp
using System.Diagnostics;
using WeaveFleet.Application.Diagnostics;
```

Modify the session events endpoint (lines 17-56):
```csharp
        app.MapGet("/api/sessions/{id}/events", async (
            string id,
            ISessionRepository sessionRepo,
            InstanceTracker tracker,
            HttpContext context,
            CancellationToken ct) =>
        {
            var session = await sessionRepo.GetByIdAsync(id);
            if (session is null)
                return Results.NotFound(new { error = $"Session {id} not found." });

            context.Response.Headers.ContentType = "text/event-stream";
            context.Response.Headers.CacheControl = "no-cache";
            context.Response.Headers.Connection = "keep-alive";

            var instance = tracker.Get(session.InstanceId);
            if (instance is null)
            {
                var stoppedData = JsonSerializer.Serialize(new { sessionId = id, status = "stopped" });
                await WriteSseEventAsync(context.Response, "session_status", stoppedData, ct);
                return Results.Empty;
            }

            await foreach (var evt in instance.SubscribeAsync(ct))
            {
                using var activity = FleetInstrumentation.ActivitySource.StartActivity(
                    FleetInstrumentation.SpanSsePump,
                    ActivityKind.Producer);
                
                activity?.SetTag("session.id", id);
                activity?.SetTag("event.type", evt.Type);

                var data = JsonSerializer.Serialize(new
                {
                    sessionId = evt.SessionId,
                    type = evt.Type,
                    payload = evt.Payload,
                    timestamp = evt.Timestamp.ToUnixTimeMilliseconds()
                });
                await WriteSseEventAsync(context.Response, evt.Type, data, ct);
            }

            return Results.Empty;
        })
```

Similarly modify the activity-stream endpoint (lines 58-84):
```csharp
        app.MapGet("/api/activity-stream", async (
            IEventBroadcaster broadcaster,
            HttpContext context,
            CancellationToken ct) =>
        {
            context.Response.Headers.ContentType = "text/event-stream";
            context.Response.Headers.CacheControl = "no-cache";
            context.Response.Headers.Connection = "keep-alive";

            var topics = ActivityStreamTopics;
            await foreach (var evt in broadcaster.SubscribeAsync(topics, ct))
            {
                using var activity = FleetInstrumentation.ActivitySource.StartActivity(
                    FleetInstrumentation.SpanSsePump,
                    ActivityKind.Producer);
                
                activity?.SetTag("topic", evt.Topic);
                activity?.SetTag("event.type", evt.Type);

                var data = JsonSerializer.Serialize(new
                {
                    topic = evt.Topic,
                    type = evt.Type,
                    payload = evt.Payload,
                    timestamp = evt.Timestamp.ToUnixTimeMilliseconds()
                });
                await WriteSseEventAsync(context.Response, evt.Type, data, ct);
            }

            return Results.Empty;
        })
```

**Acceptance**: SSE write spans visible in traces.

---

### 10. Add Trace ID to Frontend Events (Optional)

**What**: Stamp the current trace ID on outgoing events so the frontend can correlate end-to-end latency.

**Files**:
- `src/WeaveFleet.Api/Endpoints/WebSocketEndpoints.cs`
- `src/WeaveFleet.Api/Endpoints/SessionEventEndpoints.cs`

**Implementation**:

In the WebSocket JSON payload, add `traceId`:
```csharp
var json = JsonSerializer.Serialize(new
{
    type = "event",
    topic = evt.Topic,
    traceId = activity?.TraceId.ToString(), // null if no activity
    data = new
    {
        type = evt.Type,
        properties = evt.Payload
    }
});
```

In SSE payloads:
```csharp
var data = JsonSerializer.Serialize(new
{
    sessionId = evt.SessionId,
    type = evt.Type,
    payload = evt.Payload,
    timestamp = evt.Timestamp.ToUnixTimeMilliseconds(),
    traceId = activity?.TraceId.ToString()
});
```

**Acceptance**: Frontend can log/display trace IDs for debugging.

---

### 11. Create Dev Trace Collector Script

**What**: Provide a simple script to start Seq with OTLP ingestion for local trace visualization.

**Files**:
- `scripts/start-seq.ps1` (new)

**Implementation**:
```powershell
#!/usr/bin/env pwsh
# Starts Seq for local OpenTelemetry trace visualization.
# Access UI at http://localhost:5341
# OTLP endpoint: http://localhost:5341/ingest/otlp

$containerName = "weave-fleet-seq"

# Check if container exists
$existing = docker ps -a --filter "name=$containerName" --format "{{.Names}}"
if ($existing -eq $containerName) {
    Write-Host "Starting existing Seq container..."
    docker start $containerName
} else {
    Write-Host "Creating new Seq container..."
    docker run -d `
        --name $containerName `
        -e ACCEPT_EULA=Y `
        -p 5341:80 `
        datalust/seq:latest
}

Write-Host ""
Write-Host "Seq is running:"
Write-Host "  UI: http://localhost:5341"
Write-Host "  OTLP: http://localhost:5341/ingest/otlp"
Write-Host ""
Write-Host "appsettings.Development.json is already configured for this endpoint."
```

**Alternative** — docker-compose for more options:

**Files**:
- `docker-compose.dev.yml` (new)

```yaml
# Development services for weave-fleet
# Usage: docker-compose -f docker-compose.dev.yml up -d

services:
  seq:
    image: datalust/seq:latest
    container_name: weave-fleet-seq
    environment:
      - ACCEPT_EULA=Y
    ports:
      - "5341:80"   # UI + OTLP ingestion
    restart: unless-stopped
```

**Acceptance**: Running `scripts/start-seq.ps1` or `docker-compose -f docker-compose.dev.yml up -d` starts Seq.

---

### 12. Add Unit Tests for Instrumentation

**What**: Verify spans are created at each pipeline seam using in-memory ActivityListener.

**Files**:
- `tests/WeaveFleet.Infrastructure.Tests/Diagnostics/PipelineTracingTests.cs` (new)

**Implementation**:
```csharp
using System.Diagnostics;
using WeaveFleet.Application.Diagnostics;
using WeaveFleet.Infrastructure.Services;

namespace WeaveFleet.Infrastructure.Tests.Diagnostics;

public sealed class PipelineTracingTests : IDisposable
{
    private readonly ActivityListener _listener;
    private readonly List<Activity> _capturedActivities = [];

    public PipelineTracingTests()
    {
        _listener = new ActivityListener
        {
            ShouldListenTo = source => source.Name == FleetInstrumentation.ServiceName,
            Sample = (ref ActivityCreationOptions<ActivityContext> _) => ActivitySamplingResult.AllData,
            ActivityStarted = activity => _capturedActivities.Add(activity),
        };
        ActivitySource.AddActivityListener(_listener);
    }

    public void Dispose()
    {
        _listener.Dispose();
    }

    [Fact]
    public async Task Broadcaster_BroadcastAsync_CreatesSpan()
    {
        var broadcaster = new InMemoryEventBroadcaster();
        
        await broadcaster.BroadcastAsync("test-topic", "test-type", new { foo = "bar" });

        var span = _capturedActivities.SingleOrDefault(a => 
            a.OperationName == FleetInstrumentation.SpanBroadcast);
        
        Assert.NotNull(span);
        Assert.Equal("test-topic", span.GetTagItem("topic"));
        Assert.Equal("test-type", span.GetTagItem("event.type"));
    }

    // Additional tests for relay, WebSocket, SSE...
}
```

**Acceptance**: Tests pass, verify span creation and tags.

---

### 13. Update FleetInstrumentation Metrics (Revised)

**What**: Based on TODO #7 revision, don't create `BroadcasterQueueDepth` in `FleetInstrumentation` — let the broadcaster own it. Only add `EventsDropped` and `RewriteSessionIdsDuration` to `FleetInstrumentation`.

**Files**:
- `src/WeaveFleet.Application/Diagnostics/FleetInstrumentation.cs`

**Implementation** (add after line 59):
```csharp
    // ─── Pipeline metrics ─────────────────────────────────────────────────────

    /// <summary>Events dropped due to bounded channel overflow.</summary>
    public static readonly Counter<long> EventsDropped =
        Meter.CreateCounter<long>(
            "weave_fleet.events.dropped",
            "events",
            "Events dropped due to bounded channel overflow");

    /// <summary>Duration of session ID rewriting in the relay.</summary>
    public static readonly Histogram<double> RewriteSessionIdsDuration =
        Meter.CreateHistogram<double>(
            "weave_fleet.relay.rewrite_duration",
            "ms",
            "Duration of session ID rewriting in the relay");
```

**Acceptance**: Metrics declared, used by instrumented code.

---

## Verification

- [ ] `scripts/start-seq.ps1` starts Seq container
- [ ] `dotnet run` with Seq shows spans for: `harness.subscribe`, `relay.pump`, `broadcast.fanout`, `ws.pump`, `sse.pump`
- [ ] Spans have expected tags (`fleet.session.id`, `event.type`, `harness.type`)
- [ ] `weave_fleet.relay.rewrite_duration` histogram has data
- [ ] `weave_fleet.broadcaster.queue_depth` gauge shows per-subscriber counts
- [ ] All existing tests pass: `dotnet test`
- [ ] New tracing unit tests pass
- [ ] Release build succeeds: `dotnet build -c Release`

---

## Implementation Notes

### Trace Context Propagation Limitation

The current design creates **independent spans at each pipeline stage** rather than linked spans. This is because:

1. `Activity.Current` flows via `AsyncLocal<T>`, which breaks when crossing channel boundaries
2. The `HarnessEvent` record is in the Domain layer and cannot carry trace context
3. Adding trace context propagation would require either:
   - A wrapper type (`TracedEvent`) that flows through channels
   - Modifying `HarnessEvent` (violates domain purity constraint)
   - Using baggage/W3C trace context headers in event payloads

For **full end-to-end trace correlation**, a future enhancement could:
1. Create `TracedEvent` wrapper in Infrastructure layer
2. Modify `HarnessEventRelay` to wrap events with captured `Activity.Context`
3. Restore context when reading from broadcaster

For now, the independent spans provide **per-stage latency visibility** which addresses the primary use case.

### Metric Naming

Metrics follow the existing convention: `weave_fleet.<component>.<measurement>`

- `weave_fleet.broadcaster.queue_depth` — gauge
- `weave_fleet.events.dropped` — counter
- `weave_fleet.relay.rewrite_duration` — histogram (milliseconds)
