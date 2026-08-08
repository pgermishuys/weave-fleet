# SignalR Migration: Phased Execution Plan

## Status

**COMPLETED** — All phases executed successfully. Migration finished 2026-07-30.  
This document is retained for historical reference. Draft produced 2026-07-30.

## Phases Overview

```
Phase 1 ──── Phase 2 ──── Phase 3 ──── Phase 4
Server Hub    Client        Validate     Cleanup
(~2 weeks)    (~1 week)     (~1 week)    (~3 days)
```

Total estimated effort: 4–5 weeks.

---

## Phase 1: Server-Side Hub (Week 1–2)

### Goal

Stand up a SignalR hub alongside the existing WebSocket endpoint. Both
transports active; no client changes yet.

### Tasks

#### 1.1 Add SignalR to the API project

- Add `Microsoft.AspNetCore.SignalR` package reference
- Register in `Program.cs`: `builder.Services.AddSignalR()` with JSON options
- Map hub: `app.MapHub<SessionEventsHub>("/hubs/session-events")`
- Add origin validation middleware for `/hubs` path

**Files:** `Program.cs`, `WeaveFleet.Api.csproj`

#### 1.2 Create SessionEventsHub

Implement hub with methods:

- `SubscribeToSessionAsync(string sessionId)` → returns `SessionSnapshot`
- `UnsubscribeFromSessionAsync(string sessionId)` → removes from group
- `LoadHistoryAsync(string sessionId, string? cursor)` → returns `HistoryPage`

On connect: subscribe to `IEventBroadcaster`, pump events to client via
`Clients.Caller.SendAsync("Event", ...)`.

On disconnect: unsubscribe from broadcaster, clean up group memberships.

**Files:** New `Hubs/SessionEventsHub.cs`

#### 1.3 Implement atomic snapshot merge

Port Foundry's merge pattern:

1. Add `GetStreamingState(sessionId)` to `SessionActivityTracker`
2. Add `GetLastEventId(sessionId)` to `InProcessEventStore`
3. In `SubscribeToSessionAsync`: merge streaming state with persisted messages
4. Return merged snapshot with `lastEventId`

**Files:** `SessionEventsHub.cs`, `SessionActivityTracker.cs`,
`InProcessEventStore.cs`

#### 1.4 Write server-side tests

- Unit tests for snapshot merge logic
- Integration test: connect to hub, subscribe, receive snapshot
- Integration test: subscribe, trigger harness event, receive via hub

**Files:** New test files in `tests/`

#### 1.5 Validate with manual testing

- Connect to `/hubs/session-events` via a test HTML page or Postman
- Subscribe to a session, verify snapshot
- Trigger harness events, verify delivery
- Kill connection, reconnect, verify no data loss

### Phase 1 exit criteria

- Hub accepts connections and delivers events
- Snapshot merge returns consistent state
- Existing WebSocket endpoint unchanged and unaffected

---

## Phase 2: Client-Side Migration (Week 3)

### Goal

Add SignalR client alongside raw WebSocket. Transport toggle selects which
is active.

### Tasks

#### 2.1 Install @microsoft/signalr

```bash
bun add @microsoft/signalr
```

**Files:** `package.json`

#### 2.2 Create use-signalr-socket.ts

Implement `WeaveSocketAPI` interface using SignalR `HubConnection`:

- `connect()` → `connection.start()`
- `subscribeV2(topics)` → `connection.invoke("SubscribeToSessionAsync", sessionId)`
- `unsubscribe(topics)` → `connection.invoke("UnsubscribeFromSessionAsync", sessionId)`
- Event dispatch: `connection.on("Event", ...)` → same callback interface

Configure auto-reconnect: `withAutomaticReconnect([1000, 2000, 5000, 10000])`.

On reconnect: re-subscribe to all active sessions (snapshot merge handles
consistency).

**Files:** New `composables/use-signalr-socket.ts`

#### 2.3 Add transport toggle to use-weave-socket.ts

```typescript
const transport = localStorage.getItem("fleet:transport") ?? "websocket";

export function useWeaveSocket() {
    return transport === "signalr" ? useSignalRSocket() : useRawWebSocket();
}
```

**Files:** `use-weave-socket.ts`

#### 2.4 Simplify reconnect in use-session-events.ts

When SignalR transport is active:
- Remove manual gap-fill REST call (snapshot merge handles it)
- On reconnect event: re-subscribe → hydrate from fresh snapshot
- Keep sequence ID dedup for events arriving during re-subscribe

**Files:** `use-session-events.ts`

#### 2.5 Write client-side tests

- Test SignalR connection lifecycle (connect, disconnect, reconnect)
- Test snapshot hydration
- Test event dispatch and dedup
- Test transport toggle switches correctly

**Files:** New/updated test files

### Phase 2 exit criteria

- `?transport=signalr` or `localStorage` flag switches to SignalR
- Full message flow works end-to-end via SignalR
- Default remains `websocket` (no risk to existing users)

---

## Phase 3: Validation & Hardening (Week 4)

### Goal

Prove SignalR transport is at least as reliable as raw WebSocket. Flip default.

### Tasks

#### 3.1 Side-by-side comparison testing

- Open two browser tabs: one WebSocket, one SignalR
- Same session, same harness events
- Compare: event ordering, completeness, timing
- Specifically test the scenarios that currently exhibit tearing

#### 3.2 Network failure testing

- Kill server mid-stream → verify SignalR auto-reconnect
- Throttle network → verify no message loss
- Sleep laptop → resume → verify clean recovery

#### 3.3 Load testing

- 50+ concurrent connections to same session
- Sustained streaming for 10+ minutes
- Monitor memory, CPU, connection stability

#### 3.4 E2E test suite

- Run full E2E suite with `fleet:transport=signalr`
- All existing tests must pass
- Add SignalR-specific E2E tests for reconnect scenarios

#### 3.5 Flip default transport

Change default from `"websocket"` to `"signalr"`.

### Phase 3 exit criteria

- No visual tearing or gap-fill artifacts with SignalR
- All E2E tests pass
- Performance equal or better than raw WebSocket
- Default transport is SignalR

---

## Phase 4: Cleanup (Week 5, ~3 days)

### Goal

Remove dead code. Single transport.

### Tasks

#### 4.1 Remove raw WebSocket endpoint

- Delete `WebSocketEndpoints.cs` (379 LOC)
- Delete `WebSocketV2Protocol.cs` (942 LOC)
- Remove `app.UseWebSockets()` from `Program.cs`
- Remove `MapWebSocketEndpoints()` registration

#### 4.2 Remove transport toggle

- Delete `useRawWebSocket()` from `use-weave-socket.ts`
- Inline SignalR implementation (or rename)
- Remove toggle flag logic

#### 4.3 Remove gap-fill REST endpoint (if unused)

- If no other consumers use `GET /api/sessions/{id}/committed-events`,
  remove it from `SessionEndpoints.cs`
- Keep if needed for external tooling

#### 4.4 Remove related tests

- Delete WebSocket-specific unit tests
- Delete WebSocket E2E tests
- Keep/update SignalR equivalents

#### 4.5 Update documentation

- Update `docs/unified-fanout-design.md` to reference SignalR
- Archive this migration guide as completed

### Phase 4 exit criteria

- No WebSocket code remains
- ~1,600 LOC net reduction
- All tests pass
- Documentation updated

---

## File Impact Summary

### New files (Phases 1–2)

| File | Phase | Purpose |
|------|-------|---------|
| `Hubs/SessionEventsHub.cs` | 1 | SignalR hub |
| `Hubs/SessionEventsHub.Snapshot.cs` | 1 | Snapshot merge (partial class) |
| `composables/use-signalr-socket.ts` | 2 | SignalR client connection |
| Test files | 1–3 | Hub + client tests |

### Modified files

| File | Phase | Change |
|------|-------|--------|
| `Program.cs` | 1 | Add SignalR registration + middleware |
| `WeaveFleet.Api.csproj` | 1 | Add SignalR package |
| `SessionActivityTracker.cs` | 1 | Expose streaming state |
| `InProcessEventStore.cs` | 1 | Expose last event ID |
| `use-weave-socket.ts` | 2 | Transport toggle |
| `use-session-events.ts` | 2 | Simplify reconnect path |
| `package.json` | 2 | Add @microsoft/signalr |

### Deleted files (Phase 4)

| File | LOC |
|------|-----|
| `WebSocketEndpoints.cs` | 379 |
| `WebSocketV2Protocol.cs` | 942 |
| WebSocket test files | ~800 |
| **Total removed** | **~2,100** |

---

## Dependencies & Prerequisites

- `Microsoft.AspNetCore.SignalR` — already available in ASP.NET Core (no extra NuGet needed)
- `@microsoft/signalr` — npm package for client (~50KB gzipped)
- No infrastructure changes (no new services, databases, or message brokers)

---

## Rollback Plan

At any point during Phases 1–3, the raw WebSocket endpoint remains active and
is the default transport. Rollback = remove the SignalR hub and client code.
No data migration, no state to clean up.

Phase 4 (cleanup) is the point of no return. Only proceed after Phase 3
validation is complete.
