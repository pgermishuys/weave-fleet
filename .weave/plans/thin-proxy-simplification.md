# Thin Proxy Simplification

## TL;DR
Strip Fleet's message persistence, snapshot merge, and streaming state buffering. Make Fleet a thin proxy that forwards opencode SSE events via SignalR and fetches message history by proxying opencode's REST API. Analytics pipeline stays intact.

## Context

Fleet currently duplicates opencode's message store in its own SQLite `messages` table, maintains in-flight streaming state (TextDeltaBuffer, MessagePartBuffer, MessageSnapshotBuffer, StreamingStateProvider), performs complex snapshot merges on SignalR subscribe, and logs every event to `harness_events` and `inproc_events`. This has caused persistent bugs: stale snapshots, cross-session state bleed, delta buffer data loss, dedup watermark issues.

OpenCode already stores full session and message history in its own SQLite database and exposes REST APIs:
- `GET /session/{id}/message?directory={dir}&limit=N&before=cursor` (paginated messages)
- `GET /session/{id}?directory={dir}` (session detail)
- `GET /session?directory={dir}` (session list)
- `GET /session/status?directory={dir}` (activity status per session)

These are already used by `OpenCodeHttpClient` in the codebase.

**Key architectural insight**: Analytics collection (`AcceptTokenEvent`) happens inside `OpenCodeHarnessSession.SubscribeAsync()`, not downstream. This means the analytics pipeline is unaffected by removing the persistence projection and fan-out buffering.

### Components Being Removed
| Component | Location | Role |
|---|---|---|
| `messages` table | Migration 002 | Stores duplicated messages |
| `harness_events` table | Migration 017 | Event log for gap-fill API |
| `inproc_events` table | Migration 018 | In-process event store for dedup |
| `outbox_messages` table | Migration 013 | Transactional outbox |
| `MessagePersistenceProjection` | Application/Projections | Writes events to messages + harness_events |
| `HarnessEventPersistenceService` | Infrastructure/Services | Message upsert logic |
| `MessagePersistenceService` | Application/Services | Conversion between HarnessMessage and PersistedMessage |
| `TextDeltaBuffer` | Application/Services | Buffers text deltas for snapshot merge |
| `MessagePartBuffer` | Application/Services | Buffers part updates for snapshot merge |
| `MessageSnapshotBuffer` | Application/Services | Buffers full message snapshots |
| `StreamingStateProvider` | Application/Services | Composes buffers into unified snapshot |
| `SessionSnapshotBuilder` | Infrastructure/Events | Reads messages from SQLite for snapshots |
| `IMessageRepository` / `MessageRepository` | Domain + Infrastructure | CRUD for messages table |
| `IOutboxRepository` / `OutboxRepository` | Domain + Infrastructure | CRUD for outbox table |
| `IHarnessEventLogRepository` / `HarnessEventLogRepository` | Domain + Infrastructure | CRUD for harness_events |
| `InProcessEventStore` | Infrastructure/EventBus | Reads/writes inproc_events |
| `InProcessOutboxDispatcher` | Infrastructure | Dispatches outbox messages |
| `OutboxDispatchBackgroundService` | Infrastructure | Background outbox poller |
| `OutboxCleanupBackgroundService` | Infrastructure | Background outbox cleanup |

### Components Being Simplified
| Component | Change |
|---|---|
| `HarnessEventRelay` | Remove `FlushBufferedDeltasAsync` call in finally block |
| `InProcessFanOutService` | Remove all buffering logic (delta, part, message snapshot); keep broadcast only |
| `SessionEventsHub` | Replace `BuildAtomicSnapshotAsync` with proxy call to opencode; remove merge methods |
| `SessionOrchestrator.GetSessionMessagesAsync` | Proxy to opencode instead of reading from messages table |
| `InProcessEventPublisher` | Remove durable event persistence; keep ephemeral channel write |

### Components Staying Intact
- Session registry (sessions table, ISessionRepository)
- InstanceTracker, SessionActivityTracker
- Analytics pipeline (AnalyticsCollector, AnalyticsWriterService, token_events, session_snapshots, daily_rollups)
- Delegations table and DelegationService
- HarnessEventRelay event pump (SSE subscription, activity status tracking, user echo suppression)
- IEventBroadcaster / InMemoryEventBroadcaster
- All harness runtimes (OpenCode, ClaudeCode, NuCode, Pi)
- Projects, workspaces, users, credentials, automations, boards

## Scope
- In scope:
  - Remove message persistence pipeline (MessagePersistenceProjection, HarnessEventPersistenceService, MessagePersistenceService)
  - Remove streaming state buffering (TextDeltaBuffer, MessagePartBuffer, MessageSnapshotBuffer, StreamingStateProvider)
  - Remove snapshot merge logic from SessionEventsHub
  - Replace snapshot with opencode REST proxy on SignalR subscribe
  - Replace /api/sessions/{id}/messages with opencode proxy
  - Simplify InProcessFanOutService to broadcast-only
  - Remove or deprecate messages, harness_events, inproc_events, outbox_messages tables
  - Update/delete affected tests
- Out of scope:
  - Multi-harness support (ClaudeCode, NuCode proxy APIs differ; keep existing behavior for non-opencode sessions for now)
  - Removing the IEventPublisher/InProcessEventPublisher entirely (still needed for ephemeral broadcast routing)
  - Frontend domain event reducer changes (events still arrive in the same DomainEvent shape)
  - Analytics pipeline changes
  - Auth/multi-tenancy changes
- Constraints / assumptions:
  - OpenCode must be running for message history to be available. When unavailable, the subscribe snapshot returns empty messages with an error indicator, and the UI shows a degraded state.
  - Only OpenCode harness sessions get proxy behavior. Non-opencode sessions (NuCode, ClaudeCode) retain existing behavior initially, but this is tracked as a separate concern.
  - Delegations stay in Fleet's database (they're Fleet-level orchestration, not opencode state).
  - The `inproc_events` table is still needed short-term for the ephemeral event ID sequence used by the broadcaster. Can be simplified to a counter in a later phase.

## Objectives
1. Eliminate Fleet's duplicate message store and all bugs caused by snapshot merge complexity
2. Make SignalR subscribe fast (single opencode REST call instead of SQLite query + streaming state merge)
3. Keep analytics pipeline functioning unchanged
4. Maintain working system at every incremental step

## Dependencies and Order

The migration has 4 phases:
1. **Phase 1: Add opencode proxy infrastructure** (new code, no removals). System works exactly as before.
2. **Phase 2: Switch snapshot + message history to proxy** (swap implementations). Old code still exists but is bypassed.
3. **Phase 3: Remove buffering from fan-out** (simplify InProcessFanOutService). Snapshot merge no longer needed.
4. **Phase 4: Delete dead code and tables** (cleanup). Remove all unused persistence code.

Each phase leaves the system fully functional.

## Tasks

### Phase 1: Add opencode proxy infrastructure

- [x] 1. Create `ISessionMessageProxy` interface and opencode implementation
  - **What**: Define an interface `ISessionMessageProxy` with two methods: `GetSnapshotAsync(fleetSessionId)` returning messages + activity status + delegations, and `GetMessagesAsync(fleetSessionId, limit, before)` returning paginated messages. Implement it for opencode by looking up the session's `instance_id` in `InstanceTracker`, getting the `OpenCodeHttpClient`, and calling `GetMessagesAsync`. For the snapshot method, call opencode's messages API and combine with Fleet's delegation query and `SessionActivityTracker` for activity status. Handle opencode unavailability by returning empty messages with a flag. For non-opencode sessions, fall back to existing behavior (read from messages table).
  - **Files**:
    - `src/WeaveFleet.Application/Services/ISessionMessageProxy.cs` (new)
    - `src/WeaveFleet.Infrastructure/Services/OpenCodeSessionMessageProxy.cs` (new)
    - `src/WeaveFleet.Infrastructure/DependencyInjection.cs` (register)
  - **Depends on**: None
  - **Acceptance**:
    - Interface defines `GetSnapshotAsync` and `GetMessagesAsync`
    - Implementation resolves opencode HTTP client from InstanceTracker/session metadata
    - Gracefully returns empty result when opencode is unavailable (HTTP timeout/connection refused)
    - Falls back to persisted messages for non-opencode harness types
    - Unit test covers the happy path and unavailability scenario

- [x] 2. Map opencode message format to Fleet's `SessionSnapshot` shape
  - **What**: The opencode `GET /session/{id}/message` endpoint returns `OpenCodeMessageWithParts[]`. Fleet's `SessionSnapshot` expects `MessageLifecyclePayload[]`. Create a mapper in the proxy implementation that converts between these formats. The existing `OpenCodeMessageDeserializer` and `SessionSnapshotBuilder.ToMessageLifecyclePayload` provide the pattern to follow. The mapper must handle: text parts, tool use parts, file parts, step finish parts, and reasoning parts (filtered per existing `ReasoningFilter`).
  - **Files**:
    - `src/WeaveFleet.Infrastructure/Services/OpenCodeSessionMessageProxy.cs` (extend)
  - **Depends on**: Task 1
  - **Acceptance**:
    - All part types map correctly to `MessageEventPart` subtypes
    - Reasoning parts are filtered using existing `ReasoningFilter`
    - Unit test verifies mapping for a message with mixed part types

### Phase 2: Switch to proxy

- [x] 3. Replace `SessionEventsHub.BuildAtomicSnapshotAsync` with proxy
  - **What**: Replace the body of `BuildAtomicSnapshotAsync` (currently: load persisted messages, read streaming state, merge deltas/parts/buffered messages) with a single call to `ISessionMessageProxy.GetSnapshotAsync`. The hub constructor gains `ISessionMessageProxy` and loses `ISessionSnapshotBuilder`, `StreamingStateProvider`, and `IEventStore`. The `ApplyStreamingDeltas`, `ApplyBufferedParts`, `ApplyBufferedMessages` methods become dead code (leave them for Phase 4 deletion). Update the `SubscribeToSessionAsync` method accordingly.
  - **Files**:
    - `src/WeaveFleet.Api/Hubs/SessionEventsHub.cs`
  - **Depends on**: Tasks 1, 2
  - **Acceptance**:
    - `SubscribeToSessionAsync` returns a snapshot built from opencode's API
    - Delegations still come from Fleet's database
    - Activity status comes from `SessionActivityTracker`
    - `lastEventId` is no longer needed in the snapshot (set to null); client dedup watermark is removed
    - Hub compiles and integration tests pass (SignalR contract tests)

- [x] 4. Replace `SessionOrchestrator.GetSessionMessagesAsync` with proxy
  - **What**: Replace `GetPersistedMessagesAsync` (reads from `IMessageRepository`) with a call to `ISessionMessageProxy.GetMessagesAsync`. The orchestrator no longer depends on `IMessageRepository` for message reads. The `/api/sessions/{id}/messages` endpoint now proxies to opencode.
  - **Files**:
    - `src/WeaveFleet.Application/Services/SessionOrchestrator.cs`
  - **Depends on**: Tasks 1, 2
  - **Acceptance**:
    - GET `/api/sessions/{id}/messages` returns messages from opencode
    - Pagination (limit, before cursor) works correctly
    - Returns 503 or empty result when opencode is unavailable

- [x] 5. Stop persisting user prompt messages in `SessionOrchestrator`
  - **What**: The orchestrator currently calls `messageRepository.UpsertAsync` to persist user prompts before sending them to opencode. This is no longer needed since messages come from opencode. Remove the `UpsertAsync` call but keep the `BroadcastPersistedUserMessageAsync` for the optimistic UI update. The broadcast needs to change to use a lightweight format that doesn't depend on `PersistedMessage`. Also update `PublishUserPromptEventAsync` to stop creating a `HarnessEvent` with `EventTypes.UserPromptCommitted` since there's no longer a persistence layer to write to. Keep the correlation ID mechanism for optimistic reconciliation.
  - **Files**:
    - `src/WeaveFleet.Application/Services/SessionOrchestrator.cs`
  - **Depends on**: Task 4
  - **Acceptance**:
    - Sending a prompt no longer writes to the messages table
    - Optimistic UI update still broadcasts the user message to SignalR subscribers
    - Existing E2E prompt tests still pass

### Phase 3: Remove buffering from fan-out

- [x] 6. Simplify `InProcessFanOutService` to broadcast-only
  - **What**: Remove all buffering logic from `ForwardAsync`: the `BufferTextDelta` call for `message.part.delta`, the `MessagePartBuffer.Set` call for `message.part.updated`, and the `MessageSnapshotBuffer.Set` call for `message.created/message.updated`. Keep only the `IEventBroadcaster.BroadcastAsync` call and the `EnrichSessionStatusPayloadAsync` logic. The service no longer depends on `IHarnessEventPersister`, `MessagePartBuffer`, or `MessageSnapshotBuffer`.
  - **Files**:
    - `src/WeaveFleet.Infrastructure/EventBus/InProcessFanOutService.cs`
  - **Depends on**: Task 3 (snapshot no longer reads buffered state)
  - **Acceptance**:
    - `InProcessFanOutService` only broadcasts; no buffering calls
    - Constructor no longer requires `IServiceScopeFactory` for persister/buffer resolution (if no other use remains)
    - Existing event flow tests pass (events still reach SignalR clients)

- [x] 7. Remove `FlushBufferedDeltasAsync` from `HarnessEventRelay`
  - **What**: In the `PumpAsync` finally block, remove the `persister.FlushBufferedDeltasAsync` call. This was needed to persist partial streaming content on disconnect. Since messages are no longer persisted by Fleet, this is dead code.
  - **Files**:
    - `src/WeaveFleet.Infrastructure/Services/HarnessEventRelay.cs`
  - **Depends on**: Task 6
  - **Acceptance**:
    - `PumpAsync` finally block no longer references `IHarnessEventPersister`
    - Relay still broadcasts idle status on disconnect
    - Relay tests pass

- [x] 8. Remove `MessagePersistenceProjection` from event bus registration
  - **What**: Remove the `bus.AddProjection<MessagePersistenceProjection>(ConsumerScope.Cluster)` line from `DependencyInjection.cs`. This stops the durable event pipeline from writing to `messages` and `harness_events`. The `InProcessProjectionHost` no longer has any projections to dispatch to (it becomes a no-op or can be removed if it's the only projection consumer).
  - **Files**:
    - `src/WeaveFleet.Infrastructure/DependencyInjection.cs`
  - **Depends on**: Tasks 5, 6, 7
  - **Acceptance**:
    - No projections registered in the event bus
    - `messages` and `harness_events` tables stop receiving writes
    - Application starts and runs without errors

### Phase 4: Delete dead code and tables

- [x] 9. Remove persistence service classes
  - **What**: Delete the following files and their DI registrations:
    - `MessagePersistenceProjection` (Application/Projections)
    - `HarnessEventPersistenceService` (Infrastructure/Services)
    - `MessagePersistenceService` (Application/Services): **Partial removal**. The `SanitizeDurableEventPayload` method is still used by `HarnessEventRelay` for reasoning filtering. Either keep this method or move it to `ReasoningFilter`. The `ToPersistedMessage`, `ToHarnessMessage`, `MergePart*`, `BuildCommittedMessagePayload` methods are all dead.
    - `IHarnessEventPersister` (Application/Services)
    - `TextDeltaBuffer` (Application/Services)
    - `MessagePartBuffer` (Application/Services)
    - `MessageSnapshotBuffer` (Application/Services)
    - `StreamingStateProvider` (Application/Services)
    - `SessionSnapshotBuilder` (Infrastructure/Events)
    - `ISessionSnapshotBuilder` (Application/Events)
    - `InProcessEventStore` (Infrastructure/EventBus)
    - `IEventStore` (Application/Events)
    - `InProcessOutboxDispatcher` (Infrastructure)
    - `IOutboxDispatcher` (Application)
    - `OutboxDispatchBackgroundService` (Infrastructure)
    - `OutboxCleanupBackgroundService` (Infrastructure)
  - **Files**:
    - All files listed above (delete)
    - `src/WeaveFleet.Infrastructure/DependencyInjection.cs` (remove registrations)
  - **Depends on**: Task 8
  - **Acceptance**:
    - All listed files deleted
    - DI registrations removed
    - Solution compiles with no errors
    - No runtime DI resolution failures

- [x] 10. Remove repository classes for dead tables
  - **What**: Delete:
    - `IMessageRepository` / `MessageRepository`
    - `IOutboxRepository` / `OutboxRepository`
    - `IHarnessEventLogRepository` / `HarnessEventLogRepository`
    - `PersistedMessage` entity (Domain/Entities)
    - `HarnessEventLogEntry` entity (Domain/Entities)
    Remove all references from `DependencyInjection.cs`.
  - **Files**:
    - `src/WeaveFleet.Domain/Repositories/IMessageRepository.cs` (delete)
    - `src/WeaveFleet.Domain/Repositories/IOutboxRepository.cs` (delete)
    - `src/WeaveFleet.Domain/Repositories/IHarnessEventLogRepository.cs` (delete)
    - `src/WeaveFleet.Infrastructure/Data/Repositories/MessageRepository.cs` (delete)
    - `src/WeaveFleet.Infrastructure/Data/Repositories/OutboxRepository.cs` (delete)
    - `src/WeaveFleet.Infrastructure/Data/Repositories/HarnessEventLogRepository.cs` (delete)
    - `src/WeaveFleet.Domain/Entities/PersistedMessage.cs` (delete)
    - `src/WeaveFleet.Infrastructure/DependencyInjection.cs` (remove registrations)
  - **Depends on**: Task 9
  - **Acceptance**:
    - All listed files deleted
    - Solution compiles
    - No remaining references to deleted types

- [x] 11. Add migration to drop dead tables
  - **What**: Create a new SQL migration that drops the `messages`, `harness_events`, `inproc_events`, and `outbox_messages` tables. This reclaims disk space and signals intent. The migration number should follow the current highest (027).
  - **Files**:
    - `src/WeaveFleet.Infrastructure/Migrations/028_drop_message_persistence_tables.sql` (new)
  - **Depends on**: Tasks 9, 10
  - **Acceptance**:
    - Migration drops all four tables with `DROP TABLE IF EXISTS`
    - Migration drops associated indexes
    - Application starts cleanly on a fresh database
    - Application starts cleanly on an existing database (tables dropped)

- [x] 12. Remove `ApplyStreamingDeltas`, `ApplyBufferedParts`, `ApplyBufferedMessages` from `SessionEventsHub`
  - **What**: Delete the three static merge methods that are now dead code. Also remove the `LoadHistoryAsync` placeholder method if it's not used by the client.
  - **Files**:
    - `src/WeaveFleet.Api/Hubs/SessionEventsHub.cs`
  - **Depends on**: Task 3
  - **Acceptance**:
    - Methods deleted
    - Hub compiles

### Phase 5: Test cleanup

- [x] 13. Delete or update affected tests
  - **What**: The following test files are directly testing removed functionality and must be deleted or substantially rewritten:
    - `tests/WeaveFleet.Api.Tests/Hubs/SnapshotMergeTests.cs` (delete; tests merge logic)
    - `tests/WeaveFleet.Application.Tests/Projections/MessagePersistenceProjectionTests.cs` (delete)
    - `tests/WeaveFleet.Application.Tests/Services/MessagePersistenceServiceTests.cs` (delete or trim to remaining methods)
    - `tests/WeaveFleet.Application.Tests/Services/MessageSnapshotBufferTests.cs` (delete)
    - `tests/WeaveFleet.E2E/Tests/MessagePersistenceTests.cs` (delete or rewrite as proxy test)
    - `tests/WeaveFleet.Infrastructure.Tests/Events/SessionSnapshotBuilderTests.cs` (delete)
    - `tests/WeaveFleet.Infrastructure.Tests/Services/HarnessEventRelayTests.cs` (update: remove assertions about FlushBufferedDeltasAsync)
    - `tests/WeaveFleet.Infrastructure.Tests/EventBus/InProcessTests.cs` (update: remove InProcessFanOutServiceTests that assert buffering behavior)
    - `tests/WeaveFleet.Api.Tests/Hubs/SessionEventsHubTests.cs` (update: change FakeSessionSnapshotBuilder to use new proxy interface)
    - `tests/WeaveFleet.IntegrationTests/Sessions/AutoActivationTests.cs` (update: remove MessagePersistenceProjection usage)
  - **Files**: All listed above
  - **Depends on**: Tasks 9, 10, 12
  - **Acceptance**:
    - `dotnet test` passes for all test projects
    - No compilation errors referencing deleted types

- [x] 14. Add proxy integration test
  - **What**: Add an integration test that verifies the subscribe-to-session flow works end-to-end: start a test session, connect via SignalR, subscribe, verify the snapshot contains messages from opencode's API (not from Fleet's database). This replaces the deleted snapshot merge tests with a simpler proxy-based test.
  - **Files**:
    - `tests/WeaveFleet.IntegrationTests/Sessions/SessionProxySnapshotTests.cs` (new)
  - **Depends on**: Tasks 3, 13
  - **Acceptance**:
    - Test boots real Kestrel + opencode test harness
    - SignalR client subscribes and receives snapshot with messages
    - Snapshot messages match what opencode's API returns

### Considerations

- [x] 15. Handle opencode unavailability gracefully
  - **What**: When opencode is temporarily unavailable (restart, crash), the proxy should:
    - `GetSnapshotAsync`: Return a snapshot with empty messages, the session's last known activity status, and delegations from Fleet's database. Add an `isPartial` flag to the snapshot so the UI can show a degraded state indicator.
    - `GetMessagesAsync`: Return 503 Service Unavailable.
    - The SignalR connection stays open; when opencode comes back, the client can re-subscribe to get fresh data.
    This behavior is implemented in Task 1 but called out here for explicit verification.
  - **Files**:
    - `src/WeaveFleet.Infrastructure/Services/OpenCodeSessionMessageProxy.cs`
    - `client/src/composables/use-session-stream.ts` (handle `isPartial` flag in snapshot)
  - **Depends on**: Task 1
  - **Acceptance**:
    - When opencode is down, subscribe returns partial snapshot (no crash, no hang)
    - When opencode comes back, re-subscribe returns full data
    - UI shows degraded state indicator when snapshot is partial

- [x] 16. Verify delegation flow works with proxy model
  - **What**: Delegations (child sessions) are created by `OpenCodeHarnessSession` when it detects a delegation tool call. The child session is a separate opencode session. In the proxy model: (a) delegations table stays in Fleet; (b) child session messages come from opencode's API via the same proxy; (c) auto-subscribe in `SessionEventsHub.PumpEventsAsync` for `delegation.updated` events still works since the broadcaster pipeline is unchanged. Verify this works by running the existing delegation E2E test.
  - **Depends on**: Tasks 3, 4
  - **Acceptance**:
    - Parent session subscribe returns delegations from Fleet's database
    - Child session auto-subscribe works (SignalR client receives child events)
    - Child session message history loads via proxy

- [x] 17. Move `SanitizeDurableEventPayload` to `ReasoningFilter`
  - **What**: `MessagePersistenceService.SanitizeDurableEventPayload` is still called by `HarnessEventRelay` for reasoning content filtering before broadcast. Move this method to `ReasoningFilter` (which already has related methods like `FilterMessageEventPayload`, `IsReasoningPartEvent`, `FilterDurableParts`). This unblocks the full deletion of `MessagePersistenceService`.
  - **Files**:
    - `src/WeaveFleet.Application/Services/ReasoningFilter.cs`
    - `src/WeaveFleet.Infrastructure/Services/HarnessEventRelay.cs` (update call site)
  - **Depends on**: None (can be done early)
  - **Acceptance**:
    - `ReasoningFilter.SanitizeEventPayload` replaces `MessagePersistenceService.SanitizeDurableEventPayload`
    - HarnessEventRelay reasoning filtering still works
    - Existing reasoning filter tests pass

## Verification

After all tasks are complete:

```bash
# Backend compiles
dotnet build WeaveFleet.slnx -c Debug

# All tests pass
dotnet test WeaveFleet.slnx -c Debug

# E2E tests pass (requires frontend build)
cd client && bun install && bun run build && cd ..
dotnet test tests/WeaveFleet.E2E -c Debug --filter "Category=E2E"

# Verify messages table no longer exists in a fresh database
# (start the app, check SQLite schema)

# Verify SignalR subscribe returns messages from opencode
# (use devtools: window.__WEAVE_SOCKET_TEST_API.hasV2Snapshot("session:{id}"))
```

Passing output: all `dotnet test` commands exit 0, no compilation warnings referencing deleted types, E2E tests demonstrate message history loads correctly through the proxy path.
