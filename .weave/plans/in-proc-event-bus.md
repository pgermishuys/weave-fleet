# In-Process Event Bus

## TL;DR
> **Summary**: Add a pure in-process event bus using `System.Threading.Channels` as an alternative to NATS, selectable via config. Reuses existing `IEventPublisher`/`IProjection<T>` abstractions.
> **Estimated Effort**: Medium

## Context
### Original Request
Replace the embedded NATS child-process event bus with a pure in-process alternative for single-node deployments. NATS remains for cross-process scenarios — the two must be pluggable via configuration.

### Key Findings
- **Abstractions are clean**: `IEventPublisher` and `IProjection<T>` are transport-agnostic. `HarnessEventRelay` only depends on `IEventPublisher` — it works with either transport unchanged.
- **Builder pattern**: `NatsStreamBuilder` + `ProjectionRegistry` + `ProjectionRegistryEntry` handle projection registration. These types live in `Nats/Configuration/` but are transport-neutral. They should be **reused as-is** (or moved to a shared location).
- **WebSocket fan-out**: `WebSocketFanOutSubscriber` is NATS-coupled (subscribes to core NATS). The in-proc alternative must replicate its side-channel duties: broadcasting to `IEventBroadcaster`, updating `SessionActivityTracker`, buffering text deltas.
- **`EventTypeMetadata.Classify()`** already classifies durable vs ephemeral — reuse directly.
- **`ProjectionContext`** needs `StreamSequence` and `PublishSequence` — in-proc can use an auto-increment store sequence and the publish-side sequence from `EventPublishContext`.
- **`ConsumerScope`** enum and `ProjectionRegistryEntry` record are in `Nats/Configuration/NatsStreamBuilder.cs` — need to be accessible from the new in-proc code. Simplest: reference across namespaces (same assembly).
- **DI entry point**: `DependencyInjection.cs:138-152` gates on `options.Nats.Enabled`. New code adds an `else` branch (or a transport enum check).

## Objectives
### Core Objective
Ship a working in-process event bus that is the **default** transport, with NATS opt-in for multi-node.

### Deliverables
- [ ] `InProcessEventPublisher` — implements `IEventPublisher`, writes durable events to a Channel + store, ephemeral events to Channel only
- [ ] `InProcessProjectionHost` — `BackgroundService` that reads from the durable Channel and dispatches to registered projections with 1-retry semantics
- [ ] `InProcessFanOutService` — `BackgroundService` that reads from a fan-out Channel (all events) and replicates `WebSocketFanOutSubscriber`'s broadcaster/activity-tracker/text-delta duties
- [ ] `InProcessEventStore` — thin SQLite-backed append-only log for durable events (replay support)
- [ ] `InProcessMetrics` — equivalent OTel counters/histograms
- [ ] `InProcessServiceCollectionExtensions` — DI wiring via same builder pattern
- [ ] Configuration plumbing — transport enum, updated `DependencyInjection.cs`
- [ ] Tests

### Definition of Done
- [ ] `dotnet build` succeeds with no warnings
- [ ] `dotnet test` passes — existing tests unaffected, new tests cover in-proc path
- [ ] Default launch profile uses in-process transport; NATS profile still works
- [ ] Events published by `HarnessEventRelay` reach `MessagePersistenceProjection` and WebSocket clients via the in-proc bus

### Guardrails (Must NOT)
- Must NOT modify any existing NATS files
- Must NOT change `IEventPublisher`, `IProjection<T>`, `ProjectionContext`, or `EventPublishContext` signatures
- Must NOT introduce external dependencies (pure BCL + existing SQLite)

## TODOs

- [x] 1. **Extract shared builder types to a transport-neutral location**
  **What**: Move `ConsumerScope`, `ProjectionRegistryEntry`, `ProjectionRegistry`, and the builder base pattern out of the `Nats.Configuration` namespace so both transports can reference them. Simplest approach: create `src/WeaveFleet.Infrastructure/EventBus/EventBusBuilder.cs` with these types (or type aliases). Keep the Nats versions as thin wrappers or update `NatsStreamBuilder` to inherit/delegate. Alternatively, since it's the same assembly, the in-proc code can just reference `Nats.Configuration` types directly — evaluate which is cleaner. **Decision**: reference `Nats.Configuration` types directly for now (same assembly, no extraction needed). Only extract if it creates a circular feel.
  **Files**: No files if reusing directly. If extracting: `src/WeaveFleet.Infrastructure/EventBus/ProjectionRegistry.cs`
  **Acceptance**: In-proc code can reference `ProjectionRegistry`, `ProjectionRegistryEntry`, `ConsumerScope` without depending on NATS NuGet packages.

- [x] 2. **Create `InProcessEventStore` — SQLite durable event log**
  **What**: Append-only table `InProcessEvents (Id INTEGER PRIMARY KEY, MessageId TEXT UNIQUE, Subject TEXT, Payload BLOB, Tenant TEXT, ProjectId TEXT, SessionId TEXT, EventType TEXT, UserId TEXT, HarnessType TEXT, Sequence INTEGER, CreatedAt TEXT)`. Methods: `AppendAsync(...)` returns store sequence, `ReadFromAsync(long afterSequence, CancellationToken)` returns `IAsyncEnumerable` for replay. Use existing SQLite connection pattern from the project. Dedup via `MessageId UNIQUE` constraint (INSERT OR IGNORE).
  **Files**: `src/WeaveFleet.Infrastructure/EventBus/InProcessEventStore.cs`
  **Acceptance**: Can append and read back events; duplicate MessageId is silently ignored.

- [x] 3. **Create `InProcessMetrics`**
  **What**: Mirror `NatsMetrics` but with `weave_fleet.inproc.*` metric names. Same counters: publish count, publish duration, projection ack count, projection handler duration. No reconnect counter (not applicable).
  **Files**: `src/WeaveFleet.Infrastructure/EventBus/InProcessMetrics.cs`
  **Acceptance**: Metrics emitted under `weave_fleet.inproc.*` namespace.

- [x] 4. **Create `InProcessEventPublisher`**
  **What**: Implements `IEventPublisher`. On `PublishAsync`:
  - Classify event via `EventTypeMetadata.Classify()`.
  - **Durable**: persist to `InProcessEventStore`, then write to a `Channel<InProcessEnvelope>` (projection channel) AND a fan-out channel. Dedup via message ID `{sessionId}:{sequence}`.
  - **Ephemeral**: write to fan-out channel only (no persistence).
  - **Unknown**: log warning, record metric, skip.
  - `InProcessEnvelope`: internal record holding `HarnessEvent`, metadata (tenant, projectId, sessionId, eventType, userId, harnessType, sequence), and store sequence.
  **Files**: `src/WeaveFleet.Infrastructure/EventBus/InProcessEventPublisher.cs`, `src/WeaveFleet.Infrastructure/EventBus/InProcessEnvelope.cs`
  **Acceptance**: Durable events land in store + both channels; ephemeral events land in fan-out channel only.

- [x] 5. **Create `InProcessProjectionHost` — BackgroundService**
  **What**: Reads from the durable projection channel. For each envelope, resolves each registered projection from DI scope, calls `HandleAsync` with a populated `ProjectionContext`. On failure: 1 retry, then log + skip. Records metrics. Similar to `ProjectionListener` but simpler (no ACK/NAK/TERM, no JetStream consumer binding).
  **Files**: `src/WeaveFleet.Infrastructure/EventBus/InProcessProjectionHost.cs`
  **Acceptance**: `MessagePersistenceProjection.HandleAsync` is invoked for every durable event. Failures retried once.

- [x] 6. **Create `InProcessFanOutService` — BackgroundService**
  **What**: Reads from the fan-out channel. Replicates `WebSocketFanOutSubscriber` logic:
  - Broadcast to `IEventBroadcaster` on `session:{sessionId}` topic.
  - Parse activity status from `session.status`/`session.idle`, update `SessionActivityTracker`, broadcast on `sessions` topic.
  - Buffer `message.part.delta` via `IHarnessEventPersister.BufferTextDelta`.
  - Propagate parent session activity (call the static helper from `WebSocketFanOutSubscriber` or extract it).
  **Files**: `src/WeaveFleet.Infrastructure/EventBus/InProcessFanOutService.cs`
  **Acceptance**: WebSocket clients receive events identically to the NATS path.

- [x] 7. **Create `InProcessServiceCollectionExtensions`**
  **What**: `AddInProcessEventBus(this IServiceCollection, Action<InProcessEventBusBuilder>)` method. Registers: `InProcessEventStore`, `InProcessMetrics`, `InProcessEventPublisher` as `IEventPublisher`, channels as singletons, `InProcessProjectionHost` as hosted service, `InProcessFanOutService` as hosted service. `InProcessEventBusBuilder` wraps the same `ProjectionRegistryEntry` list pattern as `NatsStreamBuilder`.
  **Files**: `src/WeaveFleet.Infrastructure/EventBus/InProcessServiceCollectionExtensions.cs`, `src/WeaveFleet.Infrastructure/EventBus/InProcessEventBusBuilder.cs`
  **Acceptance**: `services.AddInProcessEventBus(b => b.AddProjection<MessagePersistenceProjection>())` wires everything.

- [x] 8. **Update configuration and DI wiring**
  **What**: 
  - Add a `TransportKind` enum (`InProcess`, `Nats`) to `Application/Configuration` or `Infrastructure/EventBus`.
  - Add `Transport` property (default `InProcess`) to the existing options model (or a new top-level `EventBusOptions`).
  - Update `DependencyInjection.cs` to branch: if `Transport == Nats` use existing NATS wiring; if `Transport == InProcess` call `AddInProcessEventBus`. `HarnessEventRelay` registration is shared (it only depends on `IEventPublisher`).
  **Files**: `src/WeaveFleet.Infrastructure/EventBus/TransportKind.cs`, `src/WeaveFleet.Infrastructure/DependencyInjection.cs`, `src/WeaveFleet.Application/Configuration/NatsOptions.cs` (add `Transport` property or sibling)
  **Acceptance**: Default config launches with in-process transport. Setting `"Transport": "Nats"` uses NATS.

- [x] 9. **Update launchSettings.json**
  **What**: Ensure the default profile has `"Transport": "InProcess"` (or omits it since it's the default). Add/keep a `"Nats"` profile with `"Transport": "Nats"` + existing NATS env vars.
  **Files**: `src/WeaveFleet.WebApi/Properties/launchSettings.json` (or wherever the host project lives)
  **Acceptance**: Both profiles launchable; default doesn't spawn nats-server.

- [x] 10. **Write tests**
  **What**: 
  - `InProcessEventStoreTests` — append, read, dedup.
  - `InProcessEventPublisherTests` — durable routes to store+channels, ephemeral to fan-out only, unknown dropped.
  - `InProcessProjectionHostTests` — events dispatched to projection, retry on failure, skip after retry exhausted.
  - Integration: end-to-end publish → projection → broadcaster (may reuse existing test infra).
  **Files**: `tests/WeaveFleet.Infrastructure.Tests/EventBus/InProcessEventStoreTests.cs`, `tests/WeaveFleet.Infrastructure.Tests/EventBus/InProcessEventPublisherTests.cs`, `tests/WeaveFleet.Infrastructure.Tests/EventBus/InProcessProjectionHostTests.cs`
  **Acceptance**: All new tests green. Existing tests unaffected.

## Verification
- [x] `dotnet build src/WeaveFleet.sln` — no errors, no new warnings
- [x] `dotnet test` — all tests pass
- [x] Default launch profile starts without spawning nats-server process
- [x] Publish a harness event → observe it reaching `MessagePersistenceProjection` (durable) and WebSocket broadcast (all events)
- [x] Switch to Nats profile → existing NATS behavior unchanged
