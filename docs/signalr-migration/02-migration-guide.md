# SignalR Migration Guide: Fleet WebSocket → SignalR

## Status

**COMPLETED** — Migration finished 2026-07-30.  
This document is retained for historical reference. Draft produced 2026-07-30.

## Goal

Replace Fleet's raw WebSocket transport and custom v1/v2 protocol with SignalR,
adopting Foundry's atomic snapshot-merge pattern to eliminate visual tearing
and reconnect jank.

---

## Scope

### In scope

- New `SessionEventsHub` (SignalR hub) with typed hub methods
- Atomic snapshot merge on subscribe (port Foundry's pattern)
- Client migration to `@microsoft/signalr`
- Client-side transport toggle for parallel development
- Removal of raw WebSocket endpoint and custom protocol code

### Out of scope

- Event sourcing migration (keep relational SQLite)
- NATS integration (see `docs/unified-fanout-design.md`)
- Changes to `IEventBroadcaster` or `InProcessFanOutService`
- Changes to harness event relay or persistence projections

---

## Design Decisions

### D1: Keep IEventBroadcaster unchanged

The hub does NOT use SignalR groups for event delivery. Each hub connection
subscribes to `IEventBroadcaster` and gets its own event channel (same
per-connection pump pattern as `WebSocketEndpoints.PumpEventsAsync()`). A
background task reads from the channel and calls
`Clients.Caller.SendAsync("Event", ...)`, filtering by subscribed topics.

SignalR groups are used only for bookkeeping (tracking which sessions a
connection cares about). The streaming state for snapshot merge comes from
`StreamingStateProvider`, which composes `SessionActivityTracker` (busy/idle)
and `TextDeltaBuffer` (in-flight text deltas).

```csharp
public class SessionEventsHub : Hub
{
    public override async Task OnConnectedAsync()
    {
        // Per-connection broadcaster subscription + background pump
        _ = PumpEventsAsync(Context.ConnectionId, Context.ConnectionAborted);
        await base.OnConnectedAsync();
    }
}
```

### D2: Hub methods replace custom protocol messages

| Current WebSocket message              | SignalR hub method                                     |
|----------------------------------------|--------------------------------------------------------|
| `{ type: "subscribe_v2", topics: [] }` | `Task<SessionSnapshot> SubscribeToSessionAsync(string sessionId)` |
| `{ type: "unsubscribe", topics: [] }`  | `Task UnsubscribeFromSessionAsync(string sessionId)`   |
| `{ type: "load_history", topic, cursor }` | `Task<HistoryPage> LoadHistoryAsync(string sessionId, string? cursor)` |

Server-to-client events become named methods:

| Current frame type | SignalR client method |
|--------------------|----------------------|
| `{ type: "snapshot", ... }` | `Snapshot(string topic, SessionSnapshot data)` |
| `{ type: "event_v2", ... }` | `Event(string topic, long eventId, DomainEvent data)` |

### D3: Atomic snapshot merge on subscribe

Port Foundry's `SubscribeToConversation` pattern:

```csharp
public async Task<SessionSnapshot> SubscribeToSessionAsync(string sessionId)
{
    // 1. Add connection to SignalR group (events start flowing)
    await Groups.AddToGroupAsync(Context.ConnectionId, $"session:{sessionId}");

    // 2. Get in-flight streaming state from StreamingStateProvider
    //    (composes SessionActivityTracker for busy/idle + TextDeltaBuffer for partial content)
    var streamingState = _streamingStateProvider.GetStreamingState(sessionId);

    // 3. Load persisted messages from DB
    var persisted = await _sessionRepository.GetMessagesAsync(sessionId);

    // 4. Merge: in-flight takes precedence over persisted
    var merged = MergeSnapshots(persisted, streamingState);

    // 5. Return snapshot with lastEventId for dedup
    return new SessionSnapshot
    {
        Session = ...,
        Messages = merged,
        LastEventId = _eventStore.GetLastEventId(sessionId)
    };
}
```

The client hydrates from this snapshot, then deduplicates subsequent events
using `lastEventId`. No gap-fill REST call needed.

### D4: Client-side transport toggle

During development, both transports coexist. The client selects via a flag:

```typescript
// use-weave-socket.ts
const transport = localStorage.getItem("fleet:transport") ?? "websocket";

export function useWeaveSocket() {
    if (transport === "signalr") {
        return useSignalRSocket();
    }
    return useRawWebSocket();
}
```

Both implementations conform to the same `WeaveSocketAPI` interface. Consumers
(`use-session-events.ts`, components) are unaware of the transport.

Toggle methods during development:
- `localStorage.setItem("fleet:transport", "signalr")` in browser console
- URL parameter: `?transport=signalr`
- Default: `"websocket"` (current behavior, zero risk)

### D5: Origin validation via middleware

Current origin check in `WebSocketEndpoints.IsOriginAllowed()` moves to
middleware that runs before `MapHub`:

```csharp
app.Use(async (context, next) =>
{
    if (context.Request.Path.StartsWithSegments("/hubs"))
    {
        var options = context.RequestServices.GetRequiredService<IOptions<FleetOptions>>().Value;
        var origin = context.Request.Headers.Origin.FirstOrDefault();
        if (!IsOriginAllowed(origin, options.Auth.AllowedOrigins))
        {
            context.Response.StatusCode = 403;
            return;
        }
    }
    await next();
});
```

---

## Server-Side Changes

### New files

| File | Purpose |
|------|---------|
| `src/WeaveFleet.Api/Hubs/SessionEventsHub.cs` | SignalR hub: subscribe, unsubscribe, history, event pump |
| `src/WeaveFleet.Api/Hubs/SessionEventsHub.Snapshot.cs` | Snapshot merge logic (partial class) |

### Modified files

| File | Change |
|------|--------|
| `Program.cs` | Add `builder.Services.AddSignalR()`, `app.MapHub<SessionEventsHub>("/hubs/session-events")` |
| `DependencyInjection.cs` | No changes — `IEventBroadcaster` stays singleton |
| `InProcessFanOutService.cs` | No changes — hub subscribes to broadcaster |
| `SessionActivityTracker.cs` | Expose `GetStreamingState(sessionId)` for snapshot merge |
| `InProcessEventStore.cs` | Expose `GetLastEventId(sessionId)` for snapshot merge |

### Removed files (Phase 4)

| File | LOC | Reason |
|------|-----|--------|
| `WebSocketEndpoints.cs` | 379 | Replaced by hub |
| `WebSocketV2Protocol.cs` | 942 | Replaced by typed hub methods |
| Related test files | ~800 | Replaced by hub tests |

### Estimated delta

- **Added:** ~400–500 LOC (hub + snapshot merge)
- **Removed:** ~2,100 LOC (WebSocket endpoints + v2 protocol + tests)
- **Net:** ~1,600 LOC reduction

---

## Client-Side Changes

### New files

| File | Purpose |
|------|---------|
| `client/src/composables/use-signalr-socket.ts` | SignalR connection, subscribe, reconnect |

### Modified files

| File | Change |
|------|--------|
| `use-weave-socket.ts` | Add transport toggle; delegate to SignalR or raw WS |
| `use-session-events.ts` | Simplify reconnect path (SignalR auto-reconnect handles it) |
| `package.json` | Add `@microsoft/signalr` dependency |

### Removed files (Phase 4)

Raw WebSocket connection code in `use-weave-socket.ts` (manual reconnect,
frame parsing, ~200 LOC).

---

## Snapshot Merge: Solving Visual Tearing

### Current problem

Fleet's subscribe flow has a race condition window:

```
T=0    Client sends subscribe_v2
T=1    Server starts building snapshot from event store
T=2    New event arrives → buffered in subscription state
T=3    Server sends snapshot to client
T=4    Server drains buffered events to client
```

If events arrive between T=1 and T=3 that modify messages already in the
snapshot, the client sees stale data followed by a partial update — manifesting
as "front of message missing" or content popping in.

### Foundry's solution (to be ported)

```
T=0    Client calls hub.invoke("SubscribeToSession", sessionId)
T=0    Server adds connection to group (events start flowing to connection)
T=1    Server reads in-memory streaming state (latest partial content)
T=2    Server reads persisted messages from DB
T=3    Server merges: in-flight state wins over persisted state
T=4    Server returns merged snapshot with lastEventId
T=5    Client hydrates from snapshot
T=6    Client receives events from group, drops any with eventId <= lastEventId
```

The merge is atomic because it happens inside a single hub method invocation.
Events that arrive during merge are queued by SignalR and delivered after the
method returns — the client deduplicates them.

### Merge rules

1. Build a dictionary of in-flight messages by ID from streaming state
2. Iterate persisted messages; skip any that exist in-flight
3. Append all in-flight messages
4. Sort chronologically
5. Set `lastEventId` to the highest event ID seen in the store

---

## Auth & Security

### Origin validation

Moves to middleware (see D5 above). Equivalent behavior, different location.

### User scoping

`IEventBroadcaster` already filters by `subscriberUserId`. The hub passes
`Context.User` identity to the broadcaster subscription. No change needed.

### Connection authentication

SignalR inherits the ASP.NET Core auth pipeline. If the current WebSocket
upgrade path uses cookie or bearer auth, SignalR picks it up automatically
via `HttpContext.User`.

---

## Performance Considerations

### SignalR overhead vs raw WebSocket

| Metric | Raw WebSocket | SignalR JSON protocol |
|--------|---------------|----------------------|
| Frame overhead | ~2 bytes | ~30 bytes (JSON envelope) |
| Serialization | 1× JSON.stringify | 1× JSON.stringify (same) |
| Keep-alive | Manual (if any) | Built-in ping/pong |
| Compression | None | Optional per-message deflate |

**Verdict:** Negligible difference. The JSON envelope adds ~30 bytes per message.
SignalR's built-in keep-alive is a net positive for connection reliability.

### Snapshot merge cost

Reading streaming state and merging with persisted messages adds ~1–5ms to
the subscribe call. This replaces a separate gap-fill REST round-trip that
costs 30–50ms. Net improvement.

---

## Testing Strategy

### Unit tests

- `SessionEventsHub` subscribe/unsubscribe logic
- Snapshot merge (in-flight vs persisted precedence)
- Event dedup by sequence ID

### Integration tests

- Hub connection + subscribe → receive snapshot
- Hub connection + subscribe → receive streaming events
- Reconnect → re-subscribe → no duplicate/missing events
- Origin validation rejects disallowed origins

### E2E tests

- Full message flow: harness event → hub → frontend render
- Concurrent connections to same session
- Connection drop during active streaming → reconnect → consistent state

### Comparison tests (during parallel phase)

- Same harness events sent to both transports
- Assert identical event sequences received by client
- Timing comparison (WebSocket vs SignalR latency)

---

## Risks & Mitigations

| Risk | Severity | Mitigation |
|------|----------|------------|
| Visual regression during migration | Medium | Transport toggle; default to WebSocket until validated |
| SignalR reconnect differs from manual backoff | Low | Test reconnect scenarios; configure retry policy |
| Snapshot merge race condition | Low | Sequence ID dedup (proven in Foundry) |
| Origin validation gap | Low | Middleware runs before hub negotiation |
| Breaking change for external consumers | Low | No external WebSocket consumers known |
| `IEventBroadcaster` contention with hub subscribers | Low | Already handles multiple subscribers; hub adds one more |
