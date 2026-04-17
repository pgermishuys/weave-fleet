# NATS Event Substrate — Implementation Plan

**Goal:** Route every `HarnessEvent` through NATS: JetStream for durable events (persisted to SQLite by a projection, fanned out to WebSocket clients by the existing outbox), core NATS for ephemeral events (fanned out by a dedicated subscriber).

**Architecture:**

```
IHarnessSession.SubscribeAsync()
           │
           ▼
 HarnessEventRelay (pump per instance)           ◄── publish-only: every event → IEventPublisher
   │                                                  with per-session monotonic sequence
   │
   ▼
 IEventPublisher → NatsEventPublisher
   │
   ├─► IsDurable ────► JetStream: tenant.{ws}.project.{pid}.session.{sid}.{type}
   │                         │
   │                         ▼  pre-created durable consumers
   │                   MessagePersistenceProjection
   │                         │
   │                         ▼
   │                   HarnessEventPersistenceService
   │                         │
   │                         ▼ (SQLite rows + outbox entries)
   │                   OutboxDispatchBackgroundService → IEventBroadcaster → WS clients
   │
   └─► IsEphemeralRelay ─► core NATS: tenant.{ws}.project.{pid}.live.{sid}.{type}
                               │
                               ▼  one subscriber per Fleet node
                         EphemeralEventRelayService
                               │
                               ├─ buffers message.part.delta → TextDeltaBuffer (shared singleton)
                               ├─ updates SessionActivityTracker on status events
                               ├─ forwards on "sessions" activity_status topic
                               └─ forwards to IEventBroadcaster on session:{sid} topic → WS clients
```

**Scope:** Harness-originated events only (the `HarnessEvent` stream emitted by `IHarnessSession.SubscribeAsync()` and routed through `HarnessEventRelay`). `DelegationService` / `SessionOrchestrator` broadcasts stay on their existing direct `IEventBroadcaster` call path — they're a future iteration.

**Design reference:** `docs/nats-event-substrate-design.md`.

> **Greenfield simplification note.** The original design included a multi-phase shadow-table + dual-write + cutover rollout intended for migrating a running system. Fleet is a new project with no production data to protect, so we skipped all of it: NATS is the sole event path from day one. No shadow tables, no diff harness, no compat window, no phased cutover. Sections 1–3 below are what actually shipped.

---

## 1. Published + consumed substrate (Phase 1 equivalent)

### 1.1 Configuration + publish abstractions

**Application layer:**
- `src/WeaveFleet.Application/Configuration/NatsOptions.cs` — `ExternalUrl`, `CredsFile`, `DataDirectory`, `StreamName`, `MaxAge`, `MaxBytes`, `MaxPayloadBytes`, `TenantPrefix`, `NodeId`, `ProjectionRetryBudget`. `FleetOptions.Nats` exposes it.
- `src/WeaveFleet.Application/Events/IEventPublisher.cs` — the publish abstraction.
- `src/WeaveFleet.Application/Events/EventPublishContext.cs` — per-event context: `FleetSessionId`, `ProjectId`, `UserId`, `HarnessType`, `Sequence` (per-pump monotonic counter for `{sessionId}:{seq}` `Nats-Msg-Id`).
- `src/WeaveFleet.Application/Projections/IProjection.cs` + `ProjectionContext.cs` — consumer abstraction.

**Infrastructure layer:**
- `src/WeaveFleet.Infrastructure/Nats/NatsNamingStrategy.cs` — subject + consumer naming. Validates segment IDs reject `.`, `*`, `>`, whitespace. Stream filter `tenant.*.project.*.session.*.>` and ephemeral filter `tenant.*.project.*.live.*.>` are static. `ClusterConsumerName` vs `PerNodeConsumerName` selects based on projection scope.
- `src/WeaveFleet.Infrastructure/Nats/NatsMetrics.cs` — OTel metrics on the existing `FleetInstrumentation.Meter`: `weave_fleet.nats.{publish.count, publish.duration, projection.ack.count, projection.handler.duration, reconnect.count}`.
- `src/WeaveFleet.Infrastructure/Nats/NatsEventPublisher.cs` — routes by `EventTypeMetadata.Classify`; durable → JetStream with awaited `PubAckResponse.Error` check + `{sessionId}:{seq}` Nats-Msg-Id + `x-fleet-user-id` / `x-fleet-harness-type` / `traceparent` / `tracestate` headers; ephemeral → core NATS fire-and-forget. Unknown event types are logged at warn level and dropped.

### 1.2 Embedded nats-server + stream + consumer initialization

- `src/WeaveFleet.Infrastructure/Nats/NatsServerHostedService.cs` — launches bundled `nats-server` on loopback with JetStream when no `Fleet:Nats:ExternalUrl` is configured. No-op for external brokers.
- `src/WeaveFleet.Infrastructure/Nats/EmbeddedNatsServer/NatsServerBinaryResolver.cs` — picks the right RID-bundled binary.
- `src/WeaveFleet.Infrastructure/Nats/EmbeddedNatsServer/Binaries/win-x64/nats-server.exe` — bundled via Git LFS; other RIDs to follow when targeting them.
- `src/WeaveFleet.Infrastructure/Nats/NatsStreamInitializer.cs` — hosted service that idempotently creates the durable JetStream stream AND pre-creates every registered projection's durable consumer before any publish. Pre-creation closes the interest-retention race (a publish that lands before the consumer would otherwise be GC'd if `MaxAge` expires first).

### 1.3 Projection host

- `src/WeaveFleet.Infrastructure/Nats/ProjectionListener.cs` — binds to a pre-created durable consumer, extracts OTel trace context from headers, invokes `IProjection<HarnessEvent>.HandleAsync`. Oversize payloads (`> NatsOptions.MaxPayloadBytes`) and malformed subjects/payloads are TERM'd immediately. Handler failures are NAK'd up to `ProjectionRetryBudget` (`MaxDeliver` on the consumer), with an explicit TERM + poison-message warning on the final attempt.
- `src/WeaveFleet.Infrastructure/Nats/ProjectionHostService.cs` — spins up one `ProjectionListener` per registered projection.

### 1.4 DI surface

- `src/WeaveFleet.Infrastructure/Nats/Configuration/NatsStreamBuilder.cs` — `ConsumerScope.Cluster` vs `ConsumerScope.PerNode`; fluent `AddProjection<T>(scope)`.
- `src/WeaveFleet.Infrastructure/Nats/Configuration/NatsServiceCollectionExtensions.cs` — `AddEventStore(options, configure)`. Registers the bundled server, a lazy `INatsConnection` / `INatsJSContext` factory (so DI-time construction of hosted services doesn't race the embedded-server startup), the stream initializer, the publisher, the projection host, and every projection.

---

## 2. The sole durable path — MessagePersistenceProjection

- `src/WeaveFleet.Application/Projections/MessagePersistenceProjection.cs` — single-writer cluster-scoped projection. Reads every durable `HarnessEvent` off JetStream and delegates to `IHarnessEventPersister.HandleAsync(fleetSessionId, ownerUserId, evt, ct)`.
- `src/WeaveFleet.Application/Services/IHarnessEventPersister.cs` — `HandleAsync`, `BufferTextDelta`, `FlushBufferedDeltasAsync`. Hides the infrastructure-layer persister behind an application-layer interface so the projection lives in Application without a downstream reference.
- `src/WeaveFleet.Infrastructure/Services/HarnessEventPersistenceService.cs` — now `public`, implements `IHarnessEventPersister`. Writes messages + sessions via the existing repositories, writes outbox entries via `SessionActivityWriteService`. The existing `OutboxDispatchBackgroundService` fans durable events out to WebSocket clients — no separate `WebSocketFanOutProjection` is needed because the outbox already does the fan-out. `ownerUserId` is a per-call argument; the legacy four-string ctor + `TryHandleDurableEventAsync(sid, evt)` remain as thin wrappers so older persistence unit tests compile.

---

## 3. The sole ephemeral path — EphemeralEventRelayService

- `src/WeaveFleet.Infrastructure/Nats/EphemeralEventRelayService.cs` — `BackgroundService`. Subscribes core NATS on the ephemeral filter, then per message:
  1. Guards against oversize + malformed payloads.
  2. Forwards to `IEventBroadcaster.BroadcastAsync(session:{sid}, type, payload, userId, ct)` — the WebSocket topic.
  3. On `session.status` / `session.idle`: updates `SessionActivityTracker` and emits the `activity_status` broadcast on the global `"sessions"` topic (what the sidebar activity stream consumes).
  4. On `message.part.delta`: scopes an `IHarnessEventPersister` and calls `BufferTextDelta` so the text fragment is accumulated for the next durable `message.updated` write.

**TextDeltaBuffer** (`src/WeaveFleet.Application/Services/TextDeltaBuffer.cs`) is a singleton — fragments buffered on the ephemeral side survive across the scoped projection invocation on the durable side. `HarnessEventPersistenceService` reads/clears via `SnapshotSession` / `ClearMessage` / `ClearPart`.

---

## 4. Relay — publish-only

`src/WeaveFleet.Infrastructure/Services/HarnessEventRelay.cs` is now publish-only. Each pump:

1. Resolves `(fleetSessionId, ownerUserId, projectId, harnessType)` from `ISessionRepository` (with retry for the insert-vs-register race).
2. `await foreach` over `instance.SubscribeAsync(ct)`:
   - Increments a local `publishSequence` counter.
   - `await _publisher.PublishAsync(evt, new EventPublishContext(…, Sequence: seq), ct)`.
   - Publish failures are logged + swallowed so one bad publish doesn't tear down the pump.
3. On pump exit (disconnect / cancellation):
   - Resolves `IHarnessEventPersister` from a fresh scope and calls `FlushBufferedDeltasAsync(sessionId, ownerUserId, ct)` so partial streaming content is committed.
   - Clears `SessionActivityTracker` for the session and broadcasts one `activity_status=idle` on the `"sessions"` topic (the last thing the sidebar sees when a harness disconnects).

No direct persistence call, no direct broadcaster call for ephemeral events.

---

## 5. Tests

Unit + integration, all run under the baseline `dotnet test` (no separate suites or gating):

- **`NatsNamingStrategyTests`** — subject construction, rejection of unsafe segments (`.`, `*`, `>`, whitespace, empty), scratch sentinel, round-trip `ParseDurableSubject`, per-node vs cluster consumer naming.
- **`NatsOptionsTests`** — defaults, `FleetOptions.Nats` wiring.
- **`TextDeltaBufferTests`** — append / concatenate / scope-to-session / clear-message / clear-part.
- **`NatsEventPublisherTests`** (embedded nats-server fixture) — durable publish lands with `{sessionId}:{seq}` header + subject + headers; ephemeral publish reaches core NATS; unknown event types drop; subject-injection IDs throw.
- **`NatsServerHostedServiceTests`** — external URL is a no-op; default config launches subprocess on loopback.
- **`NatsStreamInitializerTests`** (embedded nats-server fixture) — idempotent on restart; pre-creates every registered projection's consumer.
- **`ProjectionListenerTests`** (embedded nats-server fixture) — durable message reaches the projection, subject parses into `ProjectionContext`, headers flow through.
- **`MessagePersistenceProjectionTests`** — delegates to the persister with the right owner/session; no-op when `UserId` is missing.
- **`HarnessEventRelayTests`** — publishes every event, monotonic sequence, session metadata pass-through, survives publish failures, emits idle on disconnect, retries session lookup, handles pre-existing registrations.
- **`NatsEventSubstrateEndToEndTests`** (API host via `WebApplicationFactory<Program>`) — full DI graph: embedded server starts, stream initializes, publish through `IEventPublisher` lands on JetStream.

Embedded `nats-server` is launched per test-class fixture (`EmbeddedNatsTestFixture`) on a random loopback port with a temp storage directory.

---

## 6. Current state

Shipped on branch `nats` (commits `4e01a0f`, `8ff4dc4`, `754ab96`, `82f5c0e`):

| Suite | Passing | Skipped |
| --- | --- | --- |
| `WeaveFleet.Domain.Tests` | 50 | 0 |
| `WeaveFleet.Application.Tests` | 173 | 0 |
| `WeaveFleet.Infrastructure.Tests` | 359 | 1 (pre-existing ClaudeCode availability skip) |
| `WeaveFleet.Api.Tests` | 73 | 0 |
| `WeaveFleet.TestHarness` | 29 | 0 |
| **Total** | **684** | **1** |

---

## 7. Follow-ups not done

These would be nice-to-haves but aren't load-bearing for Fleet today. Pick up when the triggering scenario arrives:

- **Additional RID binaries.** Only `win-x64` is bundled right now (primary dev platform). Download `linux-x64` / `osx-x64` / `osx-arm64` from <https://github.com/nats-io/nats-server/releases> and drop into `src/WeaveFleet.Infrastructure/Nats/EmbeddedNatsServer/Binaries/{rid}/nats-server`. LFS filter is already set in `.gitattributes`.
- **SHA-256 integrity verification** for the bundled binaries (`build/nats-server-checksums.txt` + a pre-build MSBuild target). Needed before shipping embedded binaries broadly.
- **Per-mode `MaxAge` defaults** (local 24h, self-hosted 7d, managed cloud 30d). Add a `NatsOptionsConfigurator : IPostConfigureOptions<NatsOptions>`. Not needed until self-hosted / managed cloud actually roll.
- **WebSocket authz test** — forged `x-fleet-user-id` doesn't cross subscriber boundaries. Add when managed-cloud tenant wiring begins (Decision 7 / 11 of the design).
- **`tenant` metric cardinality** — under managed cloud this becomes per-workspace. Replace with a `tenant_class` bucket before managed-cloud GA.
- **Retire the outbox** — since `MessagePersistenceProjection` is the sole SQLite writer and NATS replay covers durability, the outbox is strictly speaking redundant for this flow. Keeping it for now because it also services `DelegationService` / `SessionOrchestrator` broadcasts. Revisit when those move onto the substrate.
- **Merge-before-publish** — today `HarnessEventPersistenceService` merges buffered text deltas with the existing DB row at persist time (same behaviour as pre-NATS). The design's "merge into the payload before publishing" alternative ships final snapshots over the wire. Not load-bearing because the persister still does the right thing; swap if downstream analytics or a non-SQLite projection ever needs the wire payload to be canonical.

Saved to `.weave/plans/nats-event-substrate.md`.
