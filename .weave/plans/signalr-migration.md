# SignalR Migration: WebSocket to SignalR Transport

## TL;DR
Replace Fleet's raw WebSocket transport (`/ws`) and custom v1/v2 protocol with a SignalR hub, porting Foundry's atomic snapshot-merge pattern to eliminate visual tearing and reconnect jank. Four phases: server hub, client migration, validation, cleanup.

## Context
Fleet currently uses a raw WebSocket endpoint (`/ws`) with a custom JSON v1/v2 protocol for real-time event delivery. This works but has a race condition during subscribe (visual tearing) and requires manual reconnect logic with REST-based gap-fill. Foundry solved these problems with SignalR and an atomic snapshot merge pattern.

The migration is additive through Phases 1-3: the existing WebSocket endpoint stays active and is the default. A transport toggle lets developers test SignalR in parallel. Phase 4 removes the old code after validation.

Key architectural constraint: `IEventBroadcaster` and `InProcessFanOutService` stay unchanged. The new hub is a thin transport layer that subscribes to the existing broadcaster.

Reference docs:
- `docs/signalr-migration/01-comparison.md` (architecture comparison)
- `docs/signalr-migration/02-migration-guide.md` (design decisions)
- `docs/signalr-migration/03-execution-plan.md` (phased plan)

## Scope
- In scope:
  - New `SessionEventsHub` SignalR hub with typed methods
  - Atomic snapshot merge on subscribe (port from Foundry)
  - `@microsoft/signalr` client with `WeaveSocketAPI` conformance
  - Transport toggle (localStorage / URL param)
  - Removal of `WebSocketEndpoints.cs`, `WebSocketV2Protocol.cs`, and related test files
- Out of scope:
  - Event sourcing migration (keep relational SQLite)
  - NATS integration
  - Changes to `IEventBroadcaster`, `InProcessFanOutService`, or `InProcessEventPublisher`
  - Changes to harness event relay or persistence projections
- Constraints / assumptions:
  - `bun` is the package manager (not npm)
  - SignalR is already available in ASP.NET Core (no extra NuGet package needed for server)
  - Both transports must coexist during Phases 1-3
  - Phase 4 is the point of no return

## Objectives
- Eliminate visual tearing on subscribe via atomic snapshot merge
- Remove manual reconnect/gap-fill complexity from the client
- Reduce maintenance burden by replacing custom protocol with SignalR's built-in hub protocol
- Net code reduction after cleanup

## Dependencies and Order
1. Phase 1 (server hub) must complete before Phase 2 (client) can connect to the hub.
2. Within Phase 1: tasks 1.1 (infra setup) -> 1.2 (hub) -> 1.3 (snapshot merge) -> 1.4 (tests). Task 1.1a (expose streaming state) and 1.1b (expose last event ID) can run in parallel, but must complete before 1.3.
3. Phase 2 depends on Phase 1 completion. Tasks 2.1 (install package) -> 2.2 (create composable) -> 2.3 (transport toggle) -> 2.4 (simplify reconnect) -> 2.5 (tests).
4. Phase 3 depends on Phase 2. All validation tasks can run in parallel except 3.5 (flip default) which requires 3.1-3.4 passing.
5. Phase 4 depends on Phase 3 validation and the decision to commit. All cleanup tasks can run in parallel.

## Tasks

### Phase 1: Server-Side Hub

- [x] 1.1 Create StreamingStateProvider that aggregates in-flight state
  - **What**: The snapshot merge needs two pieces of in-flight state that live in different singletons:
    1. **Activity status** — `SessionActivityTracker` tracks busy/idle per session (status, userId, updatedAt). It does NOT hold message content.
    2. **Buffered text deltas** — `TextDeltaBuffer` accumulates `message.part.delta` fragments keyed by `(sessionId, messageId, partId)`. Its `SnapshotSession(sessionId)` method returns all buffered `(messageId, partId) → text` entries for a session.
    
    Create a new `StreamingStateProvider` service (or similar) that composes both:
    - Reads `SessionActivityTracker.Get(sessionId)` for activity status
    - Reads `TextDeltaBuffer.SnapshotSession(sessionId)` for in-flight text deltas
    - Returns a `StreamingStateSnapshot` record containing: activity status, and a dictionary of `messageId → { partId → buffered text }` representing partial content being streamed right now
    
    This is what the hub's snapshot merge will use to overlay in-flight content onto persisted messages.
  - **Files**: New `src/WeaveFleet.Application/Services/StreamingStateProvider.cs`, references `SessionActivityTracker.cs` and `TextDeltaBuffer.cs`
  - **Depends on**: None
  - **Acceptance**:
    - `GetStreamingState(sessionId)` returns activity status + buffered text deltas for the session
    - Returns empty/idle state when no deltas are buffered
    - `SessionActivityTracker` and `TextDeltaBuffer` unchanged
    - Unit tests cover: no state, activity-only, deltas-only, both present

- [x] 1.2 Expose last event ID from InProcessEventStore
  - **What**: Add a `GetLastEventId(string sessionId)` method to `InProcessEventStore` that returns the highest `id` from `inproc_events` for a given session. This is used by the snapshot merge to set the dedup watermark. The store is `internal sealed`, so consider whether to expose via a new interface or make the method `internal` and accessible to the hub via the same assembly or `InternalsVisibleTo`.
  - **Files**: `src/WeaveFleet.Infrastructure/EventBus/InProcessEventStore.cs`
  - **Depends on**: None
  - **Acceptance**:
    - `GetLastEventId(sessionId)` returns the max event ID for that session, or 0 if none
    - Existing append/read methods unchanged
    - New unit test in `tests/WeaveFleet.Infrastructure.Tests/EventBus/InProcessTests.cs`

- [x] 1.3 Add SignalR infrastructure to API project
  - **What**: Register SignalR services and map the hub endpoint. Add origin validation middleware for the `/hubs` path prefix (port logic from `WebSocketEndpoints.IsOriginAllowed()`).
    - In `Program.cs` (~line 538 area where `app.UseWebSockets()` lives): add `builder.Services.AddSignalR()` with JSON protocol options, add origin validation middleware before `MapHub`, call `app.MapHub<SessionEventsHub>("/hubs/session-events")`.
    - In `EndpointExtensions.cs`: the hub mapping should respect the same auth group (`RequireAuthorization("FleetUser")`) that wraps other endpoints when auth is enabled.
    - No new NuGet package needed; `Microsoft.AspNetCore.SignalR` is part of the ASP.NET Core shared framework (referenced via `<FrameworkReference Include="Microsoft.AspNetCore.App" />`). Do NOT add a separate NuGet package reference.
  - **Files**: `src/WeaveFleet.Api/Program.cs`, `src/WeaveFleet.Api/Endpoints/EndpointExtensions.cs`
  - **Depends on**: None
  - **Acceptance**:
    - `builder.Services.AddSignalR()` is called in DI setup
    - Hub is mapped at `/hubs/session-events`
    - Origin validation middleware rejects disallowed origins with 403 for `/hubs` paths
    - Hub respects the same auth requirements as other API endpoints
    - Existing WebSocket endpoint still works (no regressions)

- [x] 1.4 Create SessionEventsHub
  - **What**: Create the SignalR hub with three client-callable methods and a per-connection event pump.
  
    **Hub methods:**
    - `SubscribeToSessionAsync(string sessionId)` -> adds connection to group, builds merged snapshot, returns `SessionSnapshot`
    - `UnsubscribeFromSessionAsync(string sessionId)` -> removes from group
    - `LoadHistoryAsync(string sessionId, string? cursor)` -> returns `HistoryPage`
    
    **Server-to-client methods (called by the hub on client proxies):**
    - `Event(string topic, long eventId, DomainEvent data)` — streamed events
    
    **Event pump design (addresses B2 — how broadcaster events reach SignalR clients):**
    
    The existing `IEventBroadcaster` is a per-subscriber channel model — each subscriber gets its own `Channel<T>` and consumes events via `SubscribeAsync()`. The current `WebSocketEndpoints.PumpEventsAsync()` (line 293) does exactly this: one subscriber per WebSocket connection, pumping events in a loop.
    
    The SignalR hub replicates this pattern:
    1. `OnConnectedAsync()` — call `IEventBroadcaster.SubscribeAsync(["*"], userId)` to get a per-connection event channel. Start a background task that reads from this channel and calls `Clients.Caller.SendAsync("Event", topic, eventId, data)` for each event.
    2. The background pump task filters events by the connection's subscribed topics (tracked in a `ConcurrentDictionary<string, byte>` per connection, updated by `SubscribeToSessionAsync`/`UnsubscribeFromSessionAsync`).
    3. `OnDisconnectedAsync()` — cancel the pump task, dispose the broadcaster subscription.
    
    This means `IEventBroadcaster` is NOT replaced by SignalR groups. Groups are used only for the subscribe/unsubscribe bookkeeping (knowing which sessions a connection cares about). The actual event delivery uses the same per-connection broadcaster subscription that the raw WebSocket uses today.
    
    Reference `WebSocketEndpoints.cs` lines 293-327 for the existing pump pattern.
  - **Files**: New `src/WeaveFleet.Api/Hubs/SessionEventsHub.cs`
  - **Depends on**: 1.3 (SignalR infrastructure registered)
  - **Acceptance**:
    - Hub compiles and is reachable at `/hubs/session-events`
    - Each connection gets its own broadcaster subscription on connect
    - Events from broadcaster are forwarded to caller via `Clients.Caller.SendAsync("Event", ...)`
    - Events are filtered by the connection's subscribed topics
    - `SubscribeToSessionAsync` adds topic to connection's filter set and returns a snapshot
    - `UnsubscribeFromSessionAsync` removes topic from filter set
    - `OnDisconnectedAsync` cancels pump and disposes broadcaster subscription

- [x] 1.5 Implement atomic snapshot merge
  - **What**: Port Foundry's snapshot merge into a partial class or helper method. When `SubscribeToSessionAsync` is called:
    1. Add connection to SignalR group (events start flowing)
    2. Read in-flight streaming state from `StreamingStateProvider.GetStreamingState()`
    3. Load persisted messages from the session repository (same data source as `WebSocketV2Protocol.BuildSnapshotAsync`)
    4. Merge: in-flight messages take precedence over persisted (by message ID)
    5. Return merged snapshot with `lastEventId` from `InProcessEventStore.GetLastEventId()`
    - Merge rules: build dictionary of in-flight messages by ID, iterate persisted messages skipping those in-flight, append all in-flight, sort chronologically.
  - **Files**: New `src/WeaveFleet.Api/Hubs/SessionEventsHub.Snapshot.cs` (partial class), or inline in `SessionEventsHub.cs`
  - **Depends on**: 1.1 (streaming state), 1.2 (last event ID), 1.4 (hub exists)
  - **Acceptance**:
    - Snapshot merge returns persisted messages with in-flight state overlaid
    - `lastEventId` is set to the highest event ID for the session
    - Messages are sorted chronologically
    - In-flight messages win over persisted when IDs collide

- [x] 1.6 Write server-side tests
  - **What**: Unit and integration tests for the hub and snapshot merge.
    - Unit tests: snapshot merge logic (in-flight vs persisted precedence, empty states, ordering)
    - Integration tests: connect to hub, subscribe, receive snapshot; subscribe then trigger harness event and receive via hub; origin validation rejects bad origins
    - Follow existing test patterns in `tests/WeaveFleet.Api.Tests/`
  - **Files**: New `tests/WeaveFleet.Api.Tests/Hubs/SessionEventsHubTests.cs`, new `tests/WeaveFleet.Api.Tests/Hubs/SnapshotMergeTests.cs`
  - **Depends on**: 1.4, 1.5
  - **Acceptance**:
    - Snapshot merge unit tests cover: empty session, persisted-only, in-flight-only, merge with collisions, chronological ordering
    - Integration test confirms hub delivers events from broadcaster
    - All new tests pass; all existing tests still pass

### Phase 2: Client-Side Migration

- [x] 2.1 Install @microsoft/signalr
  - **What**: Add the SignalR client package using bun.
    ```bash
    cd client && bun add @microsoft/signalr
    ```
  - **Files**: `client/package.json`
  - **Depends on**: None (can start in parallel with Phase 1)
  - **Acceptance**:
    - `@microsoft/signalr` appears in `client/package.json` dependencies
    - `bun install` succeeds

- [x] 2.2 Create use-signalr-socket.ts
  - **What**: New composable implementing the full public API surface of `use-weave-socket.ts` using SignalR's `HubConnection`. This includes:
  
    **`WeaveSocketAPI` interface (lines 18-22):**
    - `subscribe(topics, callback)` — v1 topic subscription. Map to the hub's event pump: register the callback locally, send a subscribe message to the hub for each topic. Events received via `connection.on("Event", ...)` are dispatched to matching topic callbacks. Used by `use-session-events.ts` and `use-activity-stream.ts`.
    - `subscribeV2(topic, onSnapshot, onEvent, onHistory)` — v2 subscription. Call `connection.invoke("SubscribeToSessionAsync", sessionId)` for snapshot, register `connection.on("Event", ...)` for events.
    - `sendV2(message)` — map to appropriate hub invocations
    
    **Module-level exports (lines 372-392):**
    - `isWeaveSocketConnected()` — return `connection.state === HubConnectionState.Connected`
    - `onReconnect(callback)` — register callback on `connection.onreconnected()`, return unsubscribe function
    - `onDisconnect(callback)` — register callback on `connection.onclose()`, return unsubscribe function
    
    These module-level exports are imported by `use-session-events.ts` (line 28) and must work identically to the raw WebSocket versions.
    
    **Connection lifecycle:**
    - `connect()` -> `new HubConnectionBuilder().withUrl("/hubs/session-events").withAutomaticReconnect([1000, 2000, 5000, 10000]).build()` then `connection.start()`
    - On reconnect (`connection.onreconnected`): re-subscribe to all active sessions (snapshot merge handles consistency), fire all registered reconnect callbacks
    - On disconnect (`connection.onclose`): fire all registered disconnect callbacks
    
    **Test API (`WeaveSocketTestAPI`, lines 24-32):**
    - Implement `__WEAVE_SOCKET_TEST_API` with equivalent suspend/resume/status methods backed by SignalR connection state
    
    Reference `use-weave-socket.ts` for the complete API surface and callback types.
  - **Files**: New `client/src/composables/use-signalr-socket.ts`
  - **Depends on**: 2.1 (package installed), Phase 1 complete (hub exists)
  - **Acceptance**:
    - Implements full `WeaveSocketAPI` interface (both v1 `subscribe` and v2 `subscribeV2`)
    - Exports `isWeaveSocketConnected()`, `onReconnect()`, `onDisconnect()` at module level
    - Exports `useWeaveSocket()` composable with same lifecycle (onMounted/onUnmounted subscriber counting)
    - Auto-reconnect configured with exponential intervals
    - On reconnect, re-subscribes to all active topics and fires reconnect callbacks
    - On disconnect, fires disconnect callbacks
    - `__WEAVE_SOCKET_TEST_API` populated for E2E tests
    - Event callbacks match the same shape as the raw WebSocket implementation

- [x] 2.3 Add transport toggle to use-weave-socket.ts
  - **What**: Modify `use-weave-socket.ts` to check for a transport flag and delegate to either the existing raw WebSocket implementation or the new SignalR composable.
    - Check `localStorage.getItem("fleet:transport")` and URL parameter `?transport=signalr`
    - Default to `"websocket"` (zero risk to existing users)
    - Export the same `WeaveSocketAPI` regardless of transport
    - Consumers (`use-session-events.ts`, `use-activity-stream.ts`, `use-diffs.ts`, `use-session-stream.ts`) should not need changes
  - **Files**: `client/src/composables/use-weave-socket.ts`
  - **Depends on**: 2.2 (SignalR composable exists)
  - **Acceptance**:
    - Default transport is `"websocket"` (existing behavior unchanged)
    - Setting `localStorage.setItem("fleet:transport", "signalr")` switches to SignalR
    - URL param `?transport=signalr` also works
    - All consumers compile without changes
    - `isWeaveSocketConnected`, `onDisconnect`, `onReconnect` exports still work for both transports

- [x] 2.4 Simplify reconnect in use-session-events.ts for SignalR path
  - **What**: When SignalR transport is active, the manual gap-fill REST call (`GET /api/sessions/{id}/committed-events`) is unnecessary because the snapshot merge handles consistency on re-subscribe. Add a conditional path:
    - If SignalR: on reconnect, re-subscribe to session -> hydrate from fresh snapshot -> use `lastEventId` for dedup
    - If WebSocket: keep existing gap-fill logic unchanged
    - Keep sequence ID dedup for events arriving during re-subscribe (both transports)
  - **Files**: `client/src/composables/use-session-events.ts`
  - **Depends on**: 2.3 (toggle exists)
  - **Acceptance**:
    - SignalR reconnect does not call the gap-fill REST endpoint
    - WebSocket reconnect behavior unchanged
    - No visual tearing on reconnect with SignalR transport
    - Existing tests in `client/src/composables/__tests__/use-session-events.test.ts` still pass

- [x] 2.5 Write client-side tests
  - **What**: Tests for the new SignalR composable and transport toggle.
    - Test SignalR connection lifecycle (connect, disconnect, reconnect)
    - Test snapshot hydration from `SubscribeToSessionAsync` return value
    - Test event dispatch and dedup by `lastEventId`
    - Test transport toggle switches correctly between implementations
  - **Files**: New `client/src/composables/__tests__/use-signalr-socket.test.ts`, update `client/src/composables/__tests__/use-session-events.test.ts`
  - **Depends on**: 2.2, 2.3, 2.4
  - **Acceptance**:
    - SignalR composable tests cover connect/disconnect/reconnect lifecycle
    - Transport toggle tests confirm correct implementation is selected
    - All existing client tests still pass

### Phase 3: Validation and Hardening

- [ ] 3.1 Side-by-side comparison testing
  - **What**: Manual testing with two browser tabs (one WebSocket, one SignalR) connected to the same session. Compare event ordering, completeness, and timing. Specifically test scenarios that currently exhibit visual tearing (subscribe during active streaming, rapid message updates).
  - **Depends on**: Phase 2 complete
  - **Acceptance**:
    - Both transports receive identical event sequences
    - SignalR path shows no visual tearing on subscribe during streaming
    - No missing or duplicate messages

- [ ] 3.2 Network failure and reconnect testing
  - **What**: Test resilience scenarios:
    - Kill server mid-stream -> verify SignalR auto-reconnect and consistent re-hydration
    - Throttle network -> verify no message loss
    - Sleep/resume -> verify clean recovery
    - Disconnect during active streaming -> reconnect -> no duplicate or missing content
  - **Depends on**: Phase 2 complete
  - **Acceptance**:
    - All reconnect scenarios recover to consistent state
    - No manual intervention needed (auto-reconnect handles it)
    - Sequence ID dedup prevents duplicates

- [ ] 3.3 Load testing (50+ concurrent connections)
  - **What**: Stress test with 50+ concurrent connections to the same session, sustained streaming for 10+ minutes. Monitor memory, CPU, and connection stability. Compare resource usage between WebSocket and SignalR transports.
  - **Depends on**: Phase 2 complete
  - **Acceptance**:
    - 50+ connections stable for 10+ minutes
    - No memory leaks or connection drops under load
    - SignalR event delivery latency within 2× of raw WebSocket (measured p50 and p99)
    - Memory usage within 1.5× of raw WebSocket baseline under same load

- [x] 3.4 E2E test suite with SignalR transport
  - **What**: Run the full E2E test suite (`tests/WeaveFleet.E2E/`) with `fleet:transport=signalr` active. All existing tests must pass. Add SignalR-specific E2E tests for reconnect scenarios.
  - **Files**: `tests/WeaveFleet.E2E/Tests/` (new or updated test files)
  - **Depends on**: Phase 2 complete
  - **Acceptance**:
    - All existing E2E tests pass with SignalR transport
    - New reconnect E2E tests pass
    - No regressions

- [x] 3.5 Flip default transport to SignalR
  - **What**: Change the default transport from `"websocket"` to `"signalr"` in `use-weave-socket.ts`. Users can still override back to `"websocket"` via localStorage if needed.
  - **Files**: `client/src/composables/use-weave-socket.ts`
  - **Depends on**: 3.1, 3.2, 3.3, 3.4 all passing
  - **Acceptance**:
    - Default transport is now `"signalr"`
    - `localStorage.setItem("fleet:transport", "websocket")` still works as escape hatch
    - Full E2E suite passes with the new default

### Phase 4: Cleanup

- [x] 4.1 Remove WebSocket endpoint and protocol files
  - **What**: Delete the raw WebSocket implementation and remove its registration.
    - Delete `WebSocketEndpoints.cs`
    - Delete `WebSocketV2Protocol.cs`
    - Remove `apiScope.MapWebSocketEndpoints()` from `EndpointExtensions.cs` (line 62)
    - Remove `app.UseWebSockets()` from `Program.cs` (line 538) if no other code depends on it
  - **Files**: Delete `src/WeaveFleet.Api/Endpoints/WebSocketEndpoints.cs`, delete `src/WeaveFleet.Api/Endpoints/WebSocketV2Protocol.cs`, modify `src/WeaveFleet.Api/Endpoints/EndpointExtensions.cs`, modify `src/WeaveFleet.Api/Program.cs`
  - **Depends on**: 3.5 (default flipped to SignalR)
  - **Acceptance**:
    - No WebSocket endpoint code remains
    - Application compiles and starts
    - `/ws` endpoint no longer responds

- [x] 4.2 Remove transport toggle code
  - **What**: Remove the toggle logic from `use-weave-socket.ts`. Either inline the SignalR implementation or rename `use-signalr-socket.ts` to be the sole implementation. Remove the raw WebSocket connection code.
  - **Files**: `client/src/composables/use-weave-socket.ts`, potentially delete or rename `client/src/composables/use-signalr-socket.ts`
  - **Depends on**: 4.1
  - **Acceptance**:
    - No transport toggle code remains
    - `WeaveSocketAPI` is backed solely by SignalR
    - All consumers still work without changes

- [x] 4.3 Remove gap-fill REST endpoint if unused
  - **What**: Check whether `GET /api/sessions/{id}/committed-events` (in `SessionEndpoints.cs` ~line 320) has any remaining consumers. If not, remove it. Known references: `use-session-events.ts` (gap-fill on reconnect, removed in 2.4), `MessagePersistenceTests.cs` (E2E test). If the E2E test is the only remaining consumer, update or remove that test.
  - **Files**: `src/WeaveFleet.Api/Endpoints/SessionEndpoints.cs`, `tests/WeaveFleet.E2E/Tests/MessagePersistenceTests.cs`
  - **Depends on**: 4.2
  - **Acceptance**:
    - If no consumers remain: endpoint removed, related tests updated
    - If external consumers exist: endpoint preserved, documented as legacy

- [x] 4.4 Remove WebSocket-specific tests
  - **What**: Delete test files that only test the removed WebSocket code:
    - `tests/WeaveFleet.Api.Tests/Endpoints/WebSocketV2ProtocolTests.cs`
    - `tests/WeaveFleet.Api.Tests/Endpoints/WebSocketV2SubscriptionStateTests.cs`
    - `tests/WeaveFleet.Api.Tests/Endpoints/WebSocketMessageFormatTests.cs`
    - `tests/WeaveFleet.E2E/Tests/WebSocketV2ProtocolTests.cs`
    - `tests/WeaveFleet.E2E/Tests/WebSocketResilienceTests.cs`
  - **Files**: Delete the five test files listed above
  - **Depends on**: 4.1
  - **Acceptance**:
    - No WebSocket-specific test files remain
    - All remaining tests pass
    - SignalR hub tests (from 1.6) and E2E tests (from 3.4) provide equivalent coverage

- [x] 4.5 Update documentation
  - **What**: Update `docs/unified-fanout-design.md` to reference SignalR instead of raw WebSocket. Mark the migration docs as completed. Remove any WebSocket-specific developer guidance.
  - **Files**: `docs/unified-fanout-design.md`, `docs/signalr-migration/` (mark status as completed)
  - **Depends on**: 4.1, 4.2, 4.3, 4.4
  - **Acceptance**:
    - No documentation references raw WebSocket as the active transport
    - Migration docs marked as completed

## Verification
After all four phases:
1. `dotnet build` from repo root compiles without errors
2. `dotnet test` from repo root passes all tests
3. `cd client && bun run build` succeeds
4. `cd client && bun run test` passes all client tests
5. Application starts and serves the frontend; real-time events flow via SignalR
6. No files named `WebSocket*.cs` remain in `src/WeaveFleet.Api/Endpoints/`
7. No WebSocket-specific test files remain in `tests/`
8. `grep -r "UseWebSockets\|MapWebSocketEndpoints" src/` returns no matches
