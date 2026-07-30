# Fleet vs Foundry: Real-Time Message Pipeline Comparison

## Status

**COMPLETED** — Migration to SignalR finished 2026-07-30.  
This document is retained for historical reference. Produced 2026-07-30.

## Purpose

Side-by-side comparison of how real-time messages flow from harness/agent to
frontend in Fleet and Foundry, identifying the architectural differences that
make Foundry feel snappier.

---

## Architecture at a Glance

| Dimension              | Fleet                                    | Foundry                                  |
|------------------------|------------------------------------------|------------------------------------------|
| Backend                | ASP.NET Core + in-process channels       | ASP.NET Core 10 + in-process channels    |
| Transport to client    | Raw WebSocket (`/ws`)                    | SignalR (`/hubs/foundry`)                |
| Persistence            | SQLite (relational, outbox pattern)      | SQLite (event-sourced)                   |
| Event classification   | Durable vs Ephemeral                     | Streaming vs Domain                      |
| Protocol               | Custom JSON v1/v2 over WebSocket frames  | SignalR JSON hub protocol                |
| Reconnect              | Manual backoff + REST gap-fill           | SignalR auto-reconnect + sequence dedup  |
| Snapshot delivery      | Async snapshot → buffered-event drain    | Atomic hub invocation returns merged snapshot |

---

## Fleet Message Pipeline

```
Harness (OpenCode/NuCode/Pi)
    │  IHarnessSession.SubscribeAsync()
    ▼
HarnessEventRelay.PumpAsync()
    │  • Resolves Fleet session (up to 10 retries × 500ms)
    │  • Applies reasoning filter
    │  • Translates via DomainEventTranslator
    ▼
InProcessEventPublisher.PublishAsync()
    │
    ├─ DURABLE events (message.*, session.updated/error/compacted/deleted)
    │  ├─ Append to InProcessEventStore (in-memory, idempotent)
    │  ├─ Signal ProjectionWakeUp channel → MessagePersistenceProjection → SQLite
    │  └─ Write to FanOut channel
    │
    └─ EPHEMERAL events (message.part.delta, session.status, session.idle)
       └─ Write to FanOut channel only (no persistence)
    │
    ▼
InProcessFanOutService
    │  • Reads FanOut channel
    │  • Broadcasts to IEventBroadcaster
    │  • Updates SessionActivityTracker
    │  • Buffers text deltas in TextDeltaBuffer
    ▼
InMemoryEventBroadcaster
    │  • Per-subscriber channels, topic-filtered, user-scoped
    ▼
WebSocketEndpoints (/ws)
    │  • PumpEventsAsync: reads from broadcaster
    │  • Serializes to JSON, sends via WebSocket frame
    │  • Send-lock serializes writes per connection
    ▼
Frontend (use-weave-socket.ts)
    │  • Parses JSON frames
    │  • Dispatches snapshot/event_v2 to subscribers
    ▼
use-session-events.ts
    │  • Event reducer: mergeMessageUpdate, applyTextDelta
    │  • Gap-fill on reconnect: GET /api/sessions/{id}/committed-events
    ▼
Vue reactive state → render
```

### Key files

| File                                          | LOC  | Role                                    |
|-----------------------------------------------|------|-----------------------------------------|
| `WebSocketEndpoints.cs`                       | 379  | WebSocket handler, v1/v2 protocol       |
| `WebSocketV2Protocol.cs`                      | 942  | Snapshot building, event translation     |
| `InMemoryEventBroadcaster.cs`                 | 151  | Pub/sub, topic filtering                 |
| `InProcessFanOutService.cs`                   | 225  | Fan-out orchestration                    |
| `InProcessEventPublisher.cs`                  | 158  | Durable vs ephemeral routing             |
| `HarnessEventRelay.cs`                        | ~290 | Harness→event translation                |
| `client/src/composables/use-weave-socket.ts`  | 464  | WebSocket connection, reconnect          |
| `client/src/composables/use-session-events.ts`| 1035 | Event state machine, gap-fill            |

---

## Foundry Message Pipeline

```
User POSTs /conversations/{id}/messages
    │  • Save user message to DB (sync)
    │  • Notify clients: UserMessage via SignalR (async)
    │  • Return 201 Created immediately
    │  • Fire-and-forget: Task.Run → DispatchAsync()
    ▼
AgentDispatchService.DispatchAsync()
    │  • Connect to OpenCode session (SSE)
    │  • Stream parts from harness
    │
    ├─ TurnStartPart
    │  ├─ Save to DB (sync)
    │  └─ SignalR: NotifyTurnStartedAsync → conversation group
    │
    ├─ TextPartDelta (repeated)
    │  ├─ Append to StreamingStateStore (in-memory only)
    │  └─ SignalR: NotifyTextDeltaAsync → conversation group
    │
    └─ TurnEndPart
       ├─ Finalize message with all accumulated parts
       ├─ Save to DB (sync, event-sourced)
       ├─ Dispatch to projections (async)
       └─ SignalR: NotifyTurnEndedAsync → conversation group
    │
    ▼
StreamingNotifier
    │  • Calls IHubContext<FoundryHub>.Clients.Group(name).SendAsync()
    │  • Updates StreamingStateStore (in-memory)
    │  • Increments sequence ID per event
    ▼
Frontend (useFoundryHub.ts)
    │  • SignalR HubConnection with auto-reconnect
    │  • Typed event handlers: TextDelta, TurnStarted, etc.
    ▼
useStreamingSession.ts
    │  • Hydrates from SessionSnapshot on subscribe
    │  • Applies deltas with sequence-ID dedup
    │  • Drops out-of-order events (sequenceId <= lastSequenceId)
    ▼
Vue reactive state → render
```

### Key files

| File                                            | LOC  | Role                                  |
|-------------------------------------------------|------|---------------------------------------|
| `FoundryHub.cs`                                 | ~170 | SignalR hub, subscribe, snapshot merge |
| `StreamingNotifier.cs`                          | ~200 | Streaming event broadcaster           |
| `StreamingStateStore.cs`                        | ~300 | In-memory buffer for in-flight state  |
| `AgentDispatchService.cs`                       | ~370 | Agent orchestration, SSE consumption  |
| `SqliteEventStore.cs`                           | ~240 | Event-sourced persistence             |
| `src/Foundry.Web/src/composables/useFoundryHub.ts`       | ~255 | SignalR connection lifecycle  |
| `src/Foundry.Web/src/composables/useStreamingSession.ts` | ~507 | Streaming state, delta accumulation |

---

## Why Foundry Feels Snappier

### 1. Fire-and-forget dispatch

Foundry returns `201 Created` immediately. Agent dispatch runs in a background
`Task.Run`. The user message is echoed to all clients via SignalR before the
agent even starts processing.

Fleet's `HarnessEventRelay.PumpAsync()` must first resolve session metadata via
`ISessionRepository.GetAnyForInstanceAsync()` — with up to 10 retries at 500ms
intervals if there's a race condition. This is a potential 5-second latency
cliff before events flow.

### 2. Zero DB writes during streaming

Foundry writes to the database only at turn boundaries (start + end). Every
`TextPartDelta` goes straight to `StreamingStateStore` (in-memory) → SignalR →
client. No event store, no projection wake-up, no channel indirection.

Fleet's ephemeral path is similarly lightweight (no DB write), but the durable
events that frame them (`message.created`, `message.updated`) go through the
full InProcessEventStore → ProjectionWakeUp pipeline. The interleaving of
durable and ephemeral events on the same fan-out channel can cause ordering
artifacts visible to the client.

### 3. Atomic snapshot merge on subscribe

Foundry's `SubscribeToConversation` hub method:

1. Adds connection to SignalR group (atomic)
2. Reads in-memory streaming state from `StreamingStateStore`
3. Loads historical messages from DB
4. Merges them (in-flight takes precedence)
5. Returns one `SessionSnapshot` with `lastSequenceId`

The client hydrates once. Events arriving during snapshot delivery are received
via the group and deduplicated by sequence ID. **No gap-fill REST call needed.**

Fleet's subscribe path:

1. Client sends `subscribe_v2` via WebSocket
2. Server builds snapshot from event store
3. While snapshot is being built/sent, new events arrive
4. Buffered events are drained after snapshot delivery
5. If the snapshot is stale or events arrive in the gap → visual tearing

Gap-fill requires a separate `GET /api/sessions/{id}/committed-events` REST
call — an extra HTTP round-trip adding 30–50ms of perceived jank.

### 4. SignalR auto-reconnect

SignalR's `withAutomaticReconnect()` handles connection drops transparently.
On reconnect, the client re-subscribes and gets a fresh snapshot. Sequence ID
dedup prevents duplicates.

Fleet implements manual reconnect with exponential backoff in
`use-weave-socket.ts` (~100 LOC of custom logic) plus REST-based gap-fill.
This is more fragile and harder to test.

---

## What Fleet Already Does Well

- **Fan-out is already decoupled from persistence.** The `InProcessFanOutService`
  broadcasts immediately via channels; persistence runs on a separate projection
  path. This is not a bottleneck.
- **Event classification** (durable vs ephemeral) is sound architecture. The
  problem is ordering across the two classes, not the classification itself.
- **Custom WebSocket v2 protocol** is well-designed — it just carries a
  maintenance burden that SignalR eliminates.

---

## Latency Comparison

### Fleet

| Path             | Latency estimate | Notes                                   |
|------------------|------------------|-----------------------------------------|
| Ephemeral event  | ~3–4ms           | Harness → relay → fan-out → WS → browser |
| Durable event    | ~3–4ms to client | Same path; +10–30ms for DB write (async) |
| Reconnect gap-fill | ~30–50ms       | REST call + SQLite query + JSON serialize |
| Session resolution | 0–5000ms       | Up to 10 retries × 500ms on first connect |

### Foundry

| Path             | Latency estimate | Notes                                   |
|------------------|------------------|-----------------------------------------|
| Text delta       | ~1–5ms           | In-memory → SignalR → browser            |
| Turn start/end   | ~5–20ms          | Includes DB write (event-sourced)        |
| Subscribe        | ~10–20ms         | Single hub invocation, snapshot merge    |
| Reconnect        | ~10–20ms         | Auto-reconnect + re-subscribe            |
