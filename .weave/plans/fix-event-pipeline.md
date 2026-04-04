# Fix Event Pipeline: OpenCode to Frontend

## TL;DR
> **Summary**: Three bugs break real-time events from OpenCode harness instances to the Fleet UI: no event bridge service exists, the WebSocket pump only subscribes to hardcoded topics, and the WebSocket message format does not match what the frontend expects.
> **Estimated Effort**: Medium

## Context

### Original Request
After migrating the API layer from Next.js to .NET, the event pipeline from OpenCode to Frontend is completely broken. Three bugs need fixing to restore real-time streaming of harness events (message.updated, session.status, etc.) from running OpenCode instances to the React frontend over WebSocket.

### Key Findings

**Architecture overview (current broken state):**

    OpenCode SSE stream
      -> OpenCodeHarnessInstance.SubscribeAsync()    // yields HarnessEvent objects
      -> ??? (NOTHING calls SubscribeAsync)          // BUG 1: no relay service
      -> IEventBroadcaster.BroadcastAsync()          // only called for session_created/session_stopped
      -> InMemoryEventBroadcaster                    // fan-out via Channels
      -> WebSocketEndpoints.PumpEventsAsync()        // subscribes to ["sessions", "instances", "activity"]
      -> WebSocket -> Frontend                       // sends {type, topic, payload, timestamp}

    Frontend expects:
      WebSocket -> { type: "event", topic: "session:{id}", data: { type: "message.updated", properties: {...} } }

**Architecture (desired working state):**

    OpenCode SSE -> OpenCodeHarnessInstance.SubscribeAsync()
                         |
                   HarnessEventRelay (BackgroundService)  <-- NEW: subscribes to each live instance
                         |
                   IEventBroadcaster.BroadcastAsync("session:{sessionId}", ...)
                         |
                   InMemoryEventBroadcaster  <-- FIXED: supports wildcard/pass-all subscription
                         |
                   WebSocketEndpoints.PumpEventsAsync  <-- FIXED: uses wildcard subscription
                         |
                   WebSocket -> Frontend  <-- FIXED: wraps as { type: "event", topic, data: { type, properties } }

**Instance lifecycle (from SessionOrchestrator):**
- **Create**: CreateSessionAsync() -> spawns instance -> instanceTracker.Register(instanceId, instance) (line 93)
- **Resume**: ResumeSessionAsync() -> spawns instance -> instanceTracker.Register(instanceId, instance) (line 178)
- **Delete**: DeleteSessionAsync() -> SafeStopAsync() -> instanceTracker.Remove(instanceId) (line 261)
- InstanceTracker is a ConcurrentDictionary singleton with Register(), Get(), Remove(), GetAll()
- No events/callbacks are fired when instances are registered or removed -- the tracker is passive

**Session to Instance mapping:**
- Session.Id is the fleet session ID (a GUID set at line 61 of SessionOrchestrator)
- Session.InstanceId is the harness instance ID
- ISessionRepository.GetAnyForInstanceAsync(instanceId) returns the session for an instance
- HarnessEvent.SessionId from OpenCodeMapper.ToHarnessEvent() is the *OpenCode* session ID (from _openCodeSessionId), NOT the fleet session ID
- The frontend subscribes to session:{fleetSessionId} (use-session-events.ts:257)
- The relay must look up fleet session ID via instanceId using ISessionRepository.GetAnyForInstanceAsync()

**DI registration (DependencyInjection.cs):**
- InstanceTracker is singleton (line 77)
- IEventBroadcaster -> InMemoryEventBroadcaster (singleton, line 80)
- ISessionRepository is scoped (line 44) -- the relay must use IServiceScopeFactory
- No IHostedService or BackgroundService exists in the project yet

**Race condition:** `instanceTracker.Register()` fires at line 93 of SessionOrchestrator but `sessionRepository.InsertAsync()` happens at line 109. The relay may receive the InstanceRegistered event before the session record exists. Needs retry logic.

**InMemoryEventBroadcaster topic filtering:**
- BroadcastAsync() line 30: `if (!sub.Topics.Contains(topic)) continue;`
- Events broadcast on session:abc never reach subscribers of ["sessions", "instances", "activity"]

**WebSocket message format mismatch:**
- Server sends: `{ type: "message.updated", topic: "session:abc", payload: {...}, timestamp: 12345 }`
- Frontend expects: `msg.type === "event"` and reads `msg.data` (use-weave-socket.ts:116)
- Frontend handleEvent() receives SSEEvent { type, properties } (use-session-events.ts:361)

**Payload serialization concern:**
- HarnessEvent.Payload is `JsonElement?`
- BroadcastAsync takes `object` and calls `JsonSerializer.SerializeToElement(payload)`
- Passing a `JsonElement` as `object` to `SerializeToElement` produces the correct JSON (not double-encoded) because System.Text.Json handles JsonElement specially

## Objectives

### Core Objective
Restore the real-time event pipeline so that harness events (message.updated, session.status, message.part.updated, message.part.delta, etc.) flow from OpenCode instances through the .NET backend to the React frontend over WebSocket.

### Deliverables
- [x] A HarnessEventRelay background service that subscribes to all live harness instances and broadcasts their events via IEventBroadcaster
- [x] A wildcard subscription mechanism in InMemoryEventBroadcaster so the WebSocket pump receives events on any topic
- [x] Corrected WebSocket message envelope format to match the frontend protocol
- [x] Unit tests for the new and modified components

### Definition of Done
- [x] When an OpenCode instance emits message.updated, the frontend handleEvent() receives it as `{ type: "message.updated", properties: {...} }`
- [x] The WebSocket pump delivers events for dynamically-created session:{id} topics
- [x] Existing sessions topic events (session_created, session_stopped) still work
- [x] All existing tests pass: `dotnet test`
- [x] New unit tests pass for HarnessEventRelay, InMemoryEventBroadcaster wildcard, and WebSocket envelope format

### Guardrails (Must NOT)
- Must NOT modify the frontend React code -- the .NET server must conform to the existing frontend protocol
- Must NOT change the IHarnessInstance interface
- Must NOT change the HarnessEvent domain type
- Must NOT introduce polling -- use async enumerable subscriptions
- Must NOT break the existing session_created/session_stopped events broadcast on the "sessions" topic

## TODOs

### Bug 2 fix: Wildcard subscription in InMemoryEventBroadcaster

- [x] 1. Add wildcard topic support to InMemoryEventBroadcaster

  **What**: Modify the topic filter in BroadcastAsync() so that a subscriber with a `"*"` topic receives ALL events regardless of topic. This enables the WebSocket pump to receive events on dynamically-created `session:{id}` topics without knowing them upfront.

  **Files**: `src/WeaveFleet.Infrastructure/Services/InMemoryEventBroadcaster.cs`

  **Changes**:
  In BroadcastAsync() (line 30), change the topic filter from:
  ```csharp
  if (!sub.Topics.Contains(topic))
      continue;
  ```
  to:
  ```csharp
  if (!sub.Topics.Contains("*") && !sub.Topics.Contains(topic))
      continue;
  ```
  This is a one-line change. A subscriber with `["*"]` gets everything. Existing topic-specific subscriptions are unaffected.

  **Acceptance**: A subscriber with topics `["*"]` receives events broadcast on any topic including "session:xyz".

- [x] 2. Update WebSocket pump to use wildcard subscription

  **What**: Change PumpEventsAsync() to subscribe with `["*"]` instead of the hardcoded `["sessions", "instances", "activity"]`. The pump already filters by the per-connection subscribedTopics list (line 156-158) before sending to the client, so per-client topic scoping is preserved.

  **Files**: `src/WeaveFleet.Api/Endpoints/WebSocketEndpoints.cs`

  **Changes**:
  Line 151: Replace:
  ```csharp
  var allTopics = new[] { "sessions", "instances", "activity" };
  ```
  with:
  ```csharp
  var allTopics = new[] { "*" };
  ```
  The inScope check at line 156-157 (`subscribedTopics.Count == 0 || subscribedTopics.Contains(evt.Topic)`) already correctly filters per-client.

  **Acceptance**: Events broadcast on "session:abc" are delivered to a WebSocket client that sent `{ "type": "subscribe", "topics": ["session:abc"] }`.

### Bug 3 fix: WebSocket message envelope format

- [x] 3. Fix WebSocket event envelope to match frontend protocol

  **What**: The frontend expects `{ type: "event", topic: "...", data: { type: "...", properties: {...} } }` but the server sends `{ type: evt.Type, topic, payload, timestamp }`. Fix the serialization in PumpEventsAsync().

  **Files**: `src/WeaveFleet.Api/Endpoints/WebSocketEndpoints.cs`

  **Changes**:
  Replace the JSON serialization and byte-encoding block at lines 165-173:
  ```csharp
  var json = JsonSerializer.Serialize(new
  {
      type = evt.Type,
      topic = evt.Topic,
      payload = evt.Payload,
      timestamp = evt.Timestamp.ToUnixTimeMilliseconds()
  });

  var bytes = Encoding.UTF8.GetBytes(json);
  ```
  with:
  ```csharp
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
  ```

  **Why**: The frontend WebSocket handler (use-weave-socket.ts:116) checks `msg.type === "event"` and passes `msg.data` to topic listeners. The session event handler (use-session-events.ts:361) then reads `event.type` and `event.properties` from that data object. The `properties` field carries the HarnessEvent.Payload (a JsonElement), which maps correctly since System.Text.Json serializes JsonElement passthrough without double-encoding.

  **Acceptance**: A WebSocket client receives `{"type":"event","topic":"session:abc","data":{"type":"message.updated","properties":{...}}}` when a message.updated event is broadcast.

### Bug 1 fix: HarnessEventRelay BackgroundService

- [x] 4. Add InstanceRegistered and InstanceRemoved events to InstanceTracker

  **What**: The InstanceTracker is currently a passive dictionary. The HarnessEventRelay needs to know when instances come and go. Add two events.

  **Files**: `src/WeaveFleet.Application/Services/InstanceTracker.cs`

  **Changes**:
  Add two events and fire them from Register() and Remove():
  ```csharp
  public event Action<string, IHarnessInstance>? InstanceRegistered;
  public event Action<string>? InstanceRemoved;
  ```
  In Register() (after the dictionary add): fire `InstanceRegistered?.Invoke(instanceId, instance);`
  In Remove() (after the dictionary remove, only if removal succeeded): fire `InstanceRemoved?.Invoke(instanceId);`

  **Acceptance**: After calling Register("foo", instance), the InstanceRegistered event fires with ("foo", instance). After calling Remove("foo"), the InstanceRemoved event fires with "foo".

- [x] 5. Create HarnessEventRelay BackgroundService

  **What**: A new BackgroundService that bridges harness instance events to IEventBroadcaster. On startup, subscribes to InstanceTracker.InstanceRegistered/InstanceRemoved. For each registered instance, starts a background loop that calls SubscribeAsync() and forwards each HarnessEvent to IEventBroadcaster.BroadcastAsync().

  **Files**: Create `src/WeaveFleet.Infrastructure/Services/HarnessEventRelay.cs`

  **Design**:
  ```csharp
  public class HarnessEventRelay : BackgroundService
  {
      private readonly InstanceTracker _tracker;
      private readonly IEventBroadcaster _broadcaster;
      private readonly IServiceScopeFactory _scopeFactory;
      private readonly ILogger<HarnessEventRelay> _logger;
      private readonly ConcurrentDictionary<string, CancellationTokenSource> _subscriptions = new();
      private CancellationToken _stoppingToken;

      public HarnessEventRelay(
          InstanceTracker tracker,
          IEventBroadcaster broadcaster,
          IServiceScopeFactory scopeFactory,
          ILogger<HarnessEventRelay> logger) { ... }

      protected override Task ExecuteAsync(CancellationToken stoppingToken)
      {
          _stoppingToken = stoppingToken;

          _tracker.InstanceRegistered += OnInstanceRegistered;
          _tracker.InstanceRemoved += OnInstanceRemoved;

          // Subscribe to any already-running instances (in case service restarts)
          foreach (var (id, instance) in _tracker.GetAll())
              StartSubscription(id, instance);

          // Keep alive until shutdown
          return Task.Delay(Timeout.Infinite, stoppingToken)
              .ContinueWith(_ =>
              {
                  _tracker.InstanceRegistered -= OnInstanceRegistered;
                  _tracker.InstanceRemoved -= OnInstanceRemoved;
              }, TaskScheduler.Default);
      }

      private void OnInstanceRegistered(string instanceId, IHarnessInstance instance)
      {
          StartSubscription(instanceId, instance);
      }

      private void OnInstanceRemoved(string instanceId)
      {
          if (_subscriptions.TryRemove(instanceId, out var cts))
              cts.Cancel();
      }

      private void StartSubscription(string instanceId, IHarnessInstance instance)
      {
          var cts = CancellationTokenSource.CreateLinkedTokenSource(_stoppingToken);
          if (!_subscriptions.TryAdd(instanceId, cts))
              return; // already subscribed

          _ = Task.Run(() => PumpAsync(instanceId, instance, cts.Token), cts.Token);
      }

      private async Task PumpAsync(string instanceId, IHarnessInstance instance, CancellationToken ct)
      {
          // Look up fleet session ID with retry (race condition with SessionOrchestrator)
          string? fleetSessionId = null;
          for (int attempt = 0; attempt < 10 && !ct.IsCancellationRequested; attempt++)
          {
              using var scope = _scopeFactory.CreateScope();
              var repo = scope.ServiceProvider.GetRequiredService<ISessionRepository>();
              var session = await repo.GetAnyForInstanceAsync(instanceId);
              if (session is not null)
              {
                  fleetSessionId = session.Id;
                  break;
              }
              await Task.Delay(500, ct);  // retry after 500ms
          }
          if (fleetSessionId is null)
          {
              _logger.LogWarning("Could not resolve fleet session for instance {InstanceId}", instanceId);
              return;
          }

          var topic = $"session:{fleetSessionId}";
          try
          {
              await foreach (var evt in instance.SubscribeAsync(ct))
              {
                  // Guard against null Payload — BroadcastAsync calls
                  // JsonSerializer.SerializeToElement(payload) which throws on null.
                  object payload = evt.Payload is JsonElement je ? je : default(JsonElement);
                  await _broadcaster.BroadcastAsync(topic, evt.Type, payload);
              }
          }
          catch (OperationCanceledException) { /* expected on shutdown/removal */ }
          catch (Exception ex)
          {
              _logger.LogError(ex, "Event pump failed for instance {InstanceId}", instanceId);
          }
          finally
          {
              _subscriptions.TryRemove(instanceId, out _);
          }
      }
  }
  ```

  **Key decisions**:
  - Uses IServiceScopeFactory to create scoped ISessionRepository lookups (because ISessionRepository is scoped DI)
  - Retries session lookup up to 10 times with 500ms delay to handle the race condition where Register() fires before InsertAsync()
  - Uses CancellationTokenSource per instance so removal cancels just that subscription
  - Catches OperationCanceledException silently (expected on instance removal or app shutdown)
  - Subscribes to already-running instances on startup (handles service restart scenario)
  - Stores `stoppingToken` in a `_stoppingToken` field so the `OnInstanceRegistered` event handler can access it (event handlers can't receive method parameters from `ExecuteAsync`)
  - Guards null `Payload` with `evt.Payload is JsonElement je ? je : default(JsonElement)` to avoid `ArgumentNullException` from `JsonSerializer.SerializeToElement(null)`
  - Cleans up event subscriptions in the `ContinueWith` callback on shutdown

  **Acceptance**: When an OpenCode instance emits an event via SubscribeAsync(), it appears in IEventBroadcaster on topic "session:{fleetSessionId}".

- [x] 6. Register HarnessEventRelay in DI

  **What**: Add the hosted service registration so the relay starts automatically.

  **Files**: `src/WeaveFleet.Infrastructure/DependencyInjection.cs`

  **Changes**:
  Add after the existing singleton registrations (around line 80):
  ```csharp
  services.AddHostedService<HarnessEventRelay>();
  ```
  Add the using statement if needed:
  ```csharp
  using WeaveFleet.Infrastructure.Services;
  ```

  **Acceptance**: The HarnessEventRelay is instantiated and ExecuteAsync() is called on application startup.

### Verification and hardening

- [x] 7. Verify JsonElement payload serialization (null-safety already handled in Task 5)

  **What**: Confirm that passing a `JsonElement` (from HarnessEvent.Payload) as the `object payload` parameter to `BroadcastAsync` does not cause double-encoding. BroadcastAsync calls `JsonSerializer.SerializeToElement(payload)` -- when payload is already a JsonElement, System.Text.Json produces the correct JSON.

  **Null-safety**: `HarnessEvent.Payload` is `JsonElement?` (nullable). The relay in Task 5 already guards this: `object payload = evt.Payload is JsonElement je ? je : default(JsonElement);`. When Payload is null, `default(JsonElement)` produces an `Undefined` kind JsonElement, which serializes to `null` in JSON via `SerializeToElement`. Verify this works correctly by including it in the unit tests (Task 9).

  **Files**: No code changes needed — the null guard is already in Task 5's relay code. If verification reveals `default(JsonElement)` doesn't serialize cleanly, change the guard to `evt.Payload ?? JsonSerializer.SerializeToElement(new { })` instead.

  **Acceptance**: A BroadcastEvent created from a null Payload does not throw and produces either `null` or `{}` in the final JSON. A non-null JsonElement payload passes through without double-encoding.

### Tests

- [x] 8. Unit tests for InMemoryEventBroadcaster wildcard

  **What**: Test that a `["*"]` subscriber receives events on arbitrary topics, while a `["sessions"]` subscriber only receives events on "sessions".

  **Files**: Create `tests/WeaveFleet.Infrastructure.Tests/Services/InMemoryEventBroadcasterTests.cs` (or add to existing test file if one exists)

  **Test cases**:
  - Wildcard subscriber receives event on "session:abc"
  - Wildcard subscriber receives event on "sessions"
  - Specific-topic subscriber ["sessions"] does NOT receive event on "session:abc"
  - Specific-topic subscriber ["sessions"] receives event on "sessions"

  **Acceptance**: `dotnet test --filter InMemoryEventBroadcasterTests` passes.

- [x] 9. Unit tests for HarnessEventRelay

  **What**: Test the relay using mocked InstanceTracker, IEventBroadcaster, and IHarnessInstance. Verify that when an instance is registered and yields events, they are broadcast on the correct topic.

  **Files**: Create `tests/WeaveFleet.Infrastructure.Tests/Services/HarnessEventRelayTests.cs`

  **Test cases**:
  - When instance is registered and emits events, they are broadcast on "session:{fleetSessionId}"
  - When instance is removed, its subscription is cancelled
  - When session lookup fails after retries, a warning is logged and no events are broadcast
  - Already-running instances at startup are subscribed to

  **Acceptance**: `dotnet test --filter HarnessEventRelayTests` passes.

- [x] 10. Unit tests for WebSocket message envelope format

  **What**: Test that the PumpEventsAsync serialization produces the correct envelope format.

  **Files**: Create `tests/WeaveFleet.Api.Tests/Endpoints/WebSocketMessageFormatTests.cs`

  **Test cases**:
  - Serialized message has `type: "event"` (not the event type like "message.updated")
  - Serialized message has `data.type` equal to the original event type
  - Serialized message has `data.properties` equal to the original payload
  - Serialized message has `topic` equal to the broadcast topic

  **Acceptance**: `dotnet test --filter WebSocketMessageFormatTests` passes.

## Implementation Order

Tasks are numbered by dependency order:

1. **Task 1** (wildcard in broadcaster) -- no dependencies, foundational
2. **Task 2** (pump uses wildcard) -- depends on Task 1
3. **Task 3** (envelope format) -- independent of Tasks 1-2, but logically related
4. **Task 4** (InstanceTracker events) -- independent, needed for Task 5
5. **Task 5** (HarnessEventRelay) -- depends on Tasks 1 and 4
6. **Task 6** (DI registration) -- depends on Task 5
7. **Task 7** (payload verification) -- can run anytime, ideally before Task 5
8. **Tasks 8-10** (tests) -- after their respective implementation tasks

Recommended execution batches:
- **Batch 1**: Tasks 1, 3, 4 (all independent, can be done in parallel)
- **Batch 2**: Tasks 2, 7 (depend on Batch 1)
- **Batch 3**: Task 5 (depends on Tasks 1, 4)
- **Batch 4**: Task 6 (depends on Task 5)
- **Batch 5**: Tasks 8, 9, 10 (tests, after all implementation)

## Files Summary

| Action | File | Task |
|--------|------|------|
| Modify | `src/WeaveFleet.Infrastructure/Services/InMemoryEventBroadcaster.cs` | 1 |
| Modify | `src/WeaveFleet.Api/Endpoints/WebSocketEndpoints.cs` | 2, 3 |
| Modify | `src/WeaveFleet.Application/Services/InstanceTracker.cs` | 4 |
| Create | `src/WeaveFleet.Infrastructure/Services/HarnessEventRelay.cs` | 5 |
| Modify | `src/WeaveFleet.Infrastructure/DependencyInjection.cs` | 6 |
| Create | `tests/WeaveFleet.Infrastructure.Tests/Services/InMemoryEventBroadcasterTests.cs` | 8 |
| Create | `tests/WeaveFleet.Infrastructure.Tests/Services/HarnessEventRelayTests.cs` | 9 |
| Create | `tests/WeaveFleet.Api.Tests/Endpoints/WebSocketMessageFormatTests.cs` | 10 |

## Verification

- [x] `dotnet build` succeeds with no errors
- [x] `dotnet test` -- all existing tests pass (no regressions)
- [x] `dotnet test --filter "InMemoryEventBroadcasterTests|HarnessEventRelayTests|WebSocketMessageFormatTests"` -- all new tests pass
- [ ] Manual smoke test: start a session, send a message, verify that the frontend receives real-time events via the browser DevTools Network/WS tab
