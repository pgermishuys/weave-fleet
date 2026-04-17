# NATS Event Substrate — Design

## Context

Weave Fleet today delivers real-time harness events to WebSocket clients via an in-process `InMemoryEventBroadcaster` (`System.Threading.Channels`) and persists durable message/session state to SQLite through `HarnessEventPersistenceService` (called from the relay pump). Events are lost on process restart, there is no archive, and there is no seam for downstream consumers (read-model projections, external integrations, analytics, multi-node fan-out).

This design introduces **NATS (core + JetStream)** as the underlying event substrate. Durable events flow through JetStream and feed projections that write the SQLite read model and any future consumers (archive, analytics, external integrations). Ephemeral events (live status signals, token-level deltas, permission prompts) flow through core NATS for live fan-out without durability overhead. The result: a pluggable event substrate, durable history with replay-based consumer addition, and a clean home for cross-node delivery once the managed cloud runs multiple Fleet nodes.

## Constitution Alignment

This design is accountable to `/.weave/CONSTITUTION.md`. Key intersections:

- **Sessions are the universal primitive** — Events are session-scoped; subject paths center on the session. Per-session ordering is preserved end-to-end.
- **Projects are the organizing unit** — Project identity is a first-class subject dimension, so consumers can subscribe to "all activity in project X" without joining. Sessions without an explicit project use the sentinel `project.scratch`.
- **Harnesses define the experience** — The substrate is harness-agnostic. `HarnessEvent` is the on-wire record; harnesses never see NATS. `HarnessType` is carried in message headers so projections can categorize without joining against `Session`.
- **Local-first, cloud-ready** — One binary, one codebase, three modes. Local and self-hosted get embedded `nats-server`; managed cloud points at an external broker. Zero configuration on first run.
- **Authentication is a first-class citizen** — User-facing auth is Fleet's existing OIDC/cookie story and is unchanged by this design. NATS is never exposed to end users; it is internal plumbing with exactly one client (Fleet). Fleet-to-NATS auth is a single deployment-level credential (comparable to a database connection string): local uses loopback with no creds, self-hosted external and managed cloud use a static creds file in `NatsOptions`. No new auth abstraction is needed, because there is no new auth boundary. Tenant isolation is enforced by application-layer authz at WebSocket subscribe + subject construction, not by per-tenant NATS users.
- **Invisible infrastructure** — NATS is hidden behind `IEventPublisher` and the projection surface. Users never see it. The bundled binary is transparent.
- **UI/UX is not a layer on top** — WebSocket endpoints and `IEventBroadcaster` are unchanged; UX contracts are preserved through the swap.
- **Data is a first-class output** — Every durable event carries the dimensions required for categorization without a runtime join: tenant/project/session/event-type live in the subject, user/harness-type live in headers, the event itself lives in the payload. No metadata is duplicated across layers. "We don't discard data" is satisfied by the SQLite read model being the canonical durable store; NATS interest-based retention deletes only what has been projected out, and an archive projection can be added at any time to persist the raw event log before ack.
- **Observable from day one** — Publish rate, consumer lag per projection, projection handler duration, ack/nack rates, and reconnect events are surfaced as structured metrics and traces from the first phase.
- **Real-time by default** — Ephemeral events cross nodes via core NATS; durable events cross via JetStream. Both paths feed the existing `IEventBroadcaster` so the frontend transport (WebSocket today, SignalR in the constitution's tenet — either works behind the same abstraction) is unaffected.
- **Storage abstraction** — Projections write through the existing repository interfaces; SQLite today, Postgres-swappable in cloud later without touching projection logic.

## Current State

Recent upstream work has already reshaped the relay path in a direction that aligns with this design:

- **`HarnessEventPersistenceService`** (`src/WeaveFleet.Infrastructure/Services/HarnessEventPersistenceService.cs`) extracts durable persistence out of `IHarnessSession` implementations and is called from `HarnessEventRelay.PumpAsync` for every event. It owns per-pump text-delta buffering and flushes on disconnect.
- **`EventTypes`** (`src/WeaveFleet.Domain/Harnesses/EventTypes.cs`) — constants for every known harness event type.
- **`EventTypeMetadata`** (`src/WeaveFleet.Domain/Harnesses/EventTypeMetadata.cs`) — central registry classifying every event as durable, ephemeral-relay, requiring reasoning filter, and/or an activity signal. Replaces scattered `if (evt.Type == "…")` checks with a single lookup.
- **`HarnessEventRelay`** already routes by classification: durable events go to the persistence service and are **not** directly relayed; ephemeral events skip persistence and broadcast via `IEventBroadcaster`. Text deltas are buffered and merged into durable `message.updated` writes.

The current durable/ephemeral split (from `EventTypeMetadata`):

| Durable                        | Ephemeral                |
| ------------------------------ | ------------------------ |
| `message.created`              | `session.status`         |
| `message.updated`              | `session.idle`           |
| `message.part.updated`         | `message.part.delta`     |
| `message.removed`              | `error`                  |
| `message.part.removed`         | `permission.*`           |
| `session.updated`              |                          |
| `session.error`                |                          |
| `session.compacted`            |                          |
| `session.deleted`              |                          |

`HarnessEvent` (`src/WeaveFleet.Domain/Harnesses/HarnessTypes.cs`) remains the single on-wire record: `Type`, `SessionId`, `FleetSessionId?`, `Timestamp`, `Payload`.

Hosting modes today (`src/WeaveFleet.Application/Configuration/FleetOptions.cs`): Local dev, Self-hosted, Managed cloud (OIDC + per-user workspace isolation).

## Architecture At A Glance

```
IHarnessSession.SubscribeAsync()      (in-process IAsyncEnumerable<HarnessEvent>)
           │
           ▼
 HarnessEventRelay (pump per instance)
   ├─ buffers message.part.delta (text) in-process
   ├─ uses EventTypeMetadata.Classify(evt.Type) to route
   │
   ├─► IsDurable   ───► IEventPublisher  ───► JetStream stream: fleet-sessions
   │                                           subject: tenant.{ws}.project.{pid}.session.{sid}.{type}
   │                                              │
   │                                              ▼  durable consumers
   │                       ┌────────────────────────────────────────────────┐
   │                       │ MessagePersistenceProjection  → SQLite         │
   │                       │ (future) ArchiveProjection, AnalyticsProj, …  │
   │                       └────────────────────────────────────────────────┘
   │
   └─► IsEphemeralRelay ─► IEventPublisher ─► Core NATS (no JetStream)
                                               subject: tenant.{ws}.project.{pid}.live.{sid}.{type}
                                                 │
                                                 ▼  one core NATS subscriber per Fleet node
                                        WebSocketFanOutConsumer → IEventBroadcaster → WS clients
```

Durable events drive read models and any cross-node/external consumer. Ephemeral events cross nodes without incurring JetStream's storage overhead, and are not persisted.

## Decisions

1. **Scope** — Dual-write + projections for durable events. Durable events go through NATS first; a projection writes the SQLite read model. SQLite continues to serve queries but is derived from the event log. Ephemeral events do not change the persistence model. **This iteration covers harness-originated events only** — i.e. the `HarnessEvent` stream emitted by `IHarnessSession.SubscribeAsync()` and routed through `HarnessEventRelay`. Fleet-orchestrator-originated broadcasts (`DelegationService` delegation lifecycle events keyed on `parentSessionId`; `SessionOrchestrator` `sessions/session_created`, `session_stopped`, etc.) remain on their existing direct `IEventBroadcaster` call path and are out of scope here. A future iteration can route them through `IEventPublisher` with a subject shape designed at that time (delegation events in particular need a subject that reaches parent-session subscribers — plausibly `tenant.{ws}.project.{pid}.delegation.{parentSessionId}.{type}` — but that decision belongs with the work that brings them onto the substrate, not here).

2. **Event routing by classification** — Use the existing `EventTypeMetadata.Classify(evt.Type)` result to decide routing. `IsDurable` ⇒ JetStream. `IsEphemeralRelay` ⇒ core NATS. `RequiresReasoningFilter` is applied in-relay before publish, matching current behaviour. `IsActivitySignal` continues to feed `SessionActivityTracker` locally.

3. **Wire-format typing** — Keep the existing `HarnessEvent` envelope as-is (`Type` string + `Payload` JsonElement). No aggregate hierarchy, no generated event catalog. Projections deserialize into `HarnessEvent` and dispatch on `evt.Type` via the shared `EventTypes` constants. Per-payload record types may be defined locally inside a projection if it wants compile-time shape checks.

4. **Publish ownership** — `HarnessEventRelay` remains the sole component that publishes to NATS. Harness implementations stay NATS-agnostic. The publisher is behind `IEventPublisher` so the substrate is pluggable (NATS impl is the default; an in-memory impl stays viable for tests). Text-delta buffering continues to live in the relay pump — deltas are merged into durable payloads before JetStream publish; the raw `message.part.delta` events flow only through the ephemeral core NATS subject.

5. **Ordering discipline** — Per-session serial publish with awaited acks on the JetStream path. `await foreach (var evt in instance.SubscribeAsync(ct)) { await _publisher.PublishAsync(...); }` ⇒ TCP FIFO + single-pump-per-session + awaited `PubAck` preserves strict per-session ordering. `Nats-Msg-Id` header set per publish for JetStream dedup within its ~2-minute window. Core NATS ephemeral publishes are at-most-once and ordered per connection (no ack to await).

6. **Embedded NATS delivery** — Bundle `nats-server` per-RID at build time. A `NatsServerHostedService` launches it on startup, bound to loopback on an ephemeral port, with JetStream file storage under the app data directory. A config setting `Fleet:Nats:ExternalUrl` skips the embedded launch and points at an external broker instead. Apache-2.0-licensed redistribution is fine.

7. **Cloud tenant isolation** — Subject-prefix routing, single NATS credential. NATS is never exposed to end users; Fleet is the sole client. Per-tenant subject prefix (`tenant.{workspaceId}.>`) is used by Fleet for routing and filtering, and **tenant isolation is enforced in the application layer**: authz at WebSocket subscribe checks session ownership against the authenticated user, and subject construction uses the already-authenticated tenant context. Fleet connects to NATS with a single deployment-level credential that has access to the full `tenant.*.>` subject space. Unit of tenancy today = per-user workspace (current `FleetOptions.Cloud` model). If a future SLA demands defense-in-depth at the broker layer (e.g. protecting against a Fleet code bug leaking events cross-tenant), we can add per-tenant NATS users with subject-scoped permissions, or migrate to account-per-tenant — neither requires touching the application-layer design.

8. **Client read path** — `IEventBroadcaster` survives as the in-process last-mile fan-out abstraction. Each Fleet node runs:
   - One `WebSocketFanOutProjection` (JetStream consumer, name `fleet-ws-fanout-durable-{nodeId}`) on `tenant.*.project.*.session.*.>`, forwarding durable events to `IEventBroadcaster`.
   - One `WebSocketFanOutConsumer` (core NATS subscriber) on `tenant.*.project.*.live.*.>`, forwarding ephemeral events to the same broadcaster.
   WebSocket endpoints stay unchanged — they consume from `IEventBroadcaster` as today. Horizontal scale-out = more Fleet nodes, each with its own consumer pair. Sufficient at expected volume (~100–200 durable events/sec aggregate; delta firehose much higher but also much lighter per message and ephemeral-only). Partitioned-by-session-hash workers can be added within a node later if a single node ever can't keep up, without changing the WebSocket-facing abstraction.

9. **Migration** — No backfill. NATS starts empty at cutover; historical SQLite messages stay where they are and remain queryable. New durable events flow NATS → projection → SQLite. SQLite is not purely rebuildable from the event log for pre-cutover data — acceptable given no current use case.

10. **Retention policy** — Interest-based retention on the durable JetStream stream with a `MaxAge` safety net. Once every registered projection (persistence + future archive + any other) has acked a message, JetStream garbage-collects it. `MaxAge` protects against a stalled or missing projection causing unbounded growth. Reconciled with the constitution's "We don't discard data": the canonical durable record of every interaction lives in SQLite (constitution's storage abstraction); NATS is the event substrate, not the system of record. Interest-based deletion only removes events after every registered projection — including any archival projection — has processed them. If full replayability of the event log itself becomes a requirement (e.g. to populate a brand-new projection from the start of history), we extend `MaxAge` long enough to span the backfill window, add a parallel long-retention `Retention = Limits` stream for archive, or run an explicit replay from SQLite into a seeded stream. Defaults per hosting mode (overridable via `NatsOptions`): local dev `MaxAge = 24h`, self-hosted `7d`, managed cloud `30d`. Retention, max-age, max-bytes, and storage backend are all first-class options in the fluent builder.

    **Ephemeral events and "we don't discard data".** Core NATS subjects carry no retention. This is *not* a carve-out from the constitution; it is an application of what the constitution actually promises. "We don't discard data" commits us to preserving **interactions** — the prompt/response record, the cost/token telemetry, the lifecycle transitions that make a session a session. The ephemeral subjects carry *derived signals* that exist to drive live UI and are either redundant with a durable counterpart or are not themselves the interaction record:

    | Ephemeral event        | Why no durable record is lost                                                                                                                           |
    | ---------------------- | ------------------------------------------------------------------------------------------------------------------------------------------------------- |
    | `session.status` / `session.idle` | Live liveness signals. Authoritative session state is reconstructable from the durable `session.*` stream and session row. |
    | `message.part.delta`   | Token-level stream fragments. The *committed* text is preserved by the merged `message.updated` snapshot on the durable path. |
    | `error`                | Transient UI-facing error notifications. Terminal/session-level failures are captured by durable `session.error`. |
    | `permission.*`         | In-flight permission prompts. The *decision* becomes part of the durable message stream (the resulting `message.updated` reflects grant/deny outcomes). |

    If a future audit or compliance requirement asks us to preserve one of these signals verbatim (e.g. every individual permission prompt decision, separately from the resulting message change), the fix is to add an audit projection that subscribes to the ephemeral subject and persists what it sees — or to promote the specific event type to `IsDurable` in `EventTypeMetadata`. Neither requires redesigning the substrate. Today no such requirement exists, so ephemeral stays ephemeral.

11. **Fleet-to-NATS credentials are static config, not an abstraction** — NATS is internal plumbing with a single client (Fleet). Connection auth is a deployment-level concern handled by `NatsOptions`: `ExternalUrl` (null ⇒ use embedded loopback, no creds) and optional `CredsFile` (path to a NATS creds file, used only when talking to an external broker). Rotating creds is a file-swap + `SIGHUP`-style reload, not a pluggable provider. Constitution's "auth is a first-class citizen" tenet continues to be satisfied by Fleet's existing user-facing auth (OIDC/cookie for cloud, bypass for local) — that boundary is unchanged by this design. Tenant isolation is enforced at the application layer (see decision 7), not at the NATS auth layer.

## Subject Scheme

Primary organizational axes from the constitution — tenant → project → session — are reflected directly in the subject hierarchy so consumers can subscribe to any slice (all of a tenant, all of a project, one session) with a single wildcard.

**Durable (JetStream):**
```
tenant.{workspaceId}.project.{projectId}.session.{sessionId}.{evt.Type}
   ↳ message.created / message.updated / message.part.updated
   ↳ message.removed / message.part.removed
   ↳ session.updated / session.error / session.compacted / session.deleted
```
Stream `fleet-sessions` binds to `tenant.*.project.*.session.*.>` with file storage.

**Ephemeral (core NATS):**
```
tenant.{workspaceId}.project.{projectId}.live.{sessionId}.{evt.Type}
   ↳ session.status / session.idle
   ↳ message.part.delta
   ↳ error
   ↳ permission.*
```
No stream — core NATS publish/subscribe, one subscription per Fleet node.

Sessions without an explicit project use the sentinel `project.scratch`, matching the constitution's implicit-Scratch-project rule. For **local dev** and **self-hosted** modes the `tenant.{workspaceId}` prefix is a static placeholder (`tenant.default`). `NatsNamingStrategy` centralizes prefix construction.

**What goes where.** Hierarchical identifiers live in the subject and are extracted by parsing — duplicating them as headers would be storage/bandwidth waste with no benefit (NATS doesn't filter on headers). The payload is `HarnessEvent` as-is. Headers carry only what is **not** in the subject or payload, plus protocol/observability necessities:

| Header           | Why it's a header                                                  |
| ---------------- | ------------------------------------------------------------------ |
| `x-fleet-user-id`     | Lives on `Session.UserId`, not on `HarnessEvent`. Needed at consumer time for application-layer authz on the WebSocket fan-out. |
| `x-fleet-harness-type`| Lives on `Session.HarnessType`, not on `HarnessEvent`. Wanted by future analytics/archive projections so they can categorize without joining `Session`. |
| `Nats-Msg-Id`         | JetStream dedup within its ~2-min window. Format `{sessionId}:{seq}`. Durable path only. |
| `traceparent` / `tracestate` | OpenTelemetry trace context propagation across the publish/consume boundary. |

A projection that needs tenant/project/session reads them off the subject (parse once, cache on the consume context). A projection that needs the event itself deserializes the payload into `HarnessEvent`. A projection that needs user or harness type reads the corresponding header. No piece of metadata is in two places.

## Hosting-Mode Mechanics

### Local dev
- **NATS**: embedded `nats-server` subprocess on loopback, ephemeral port, JetStream storage under `./data/nats/`.
- **Auth**: none (loopback-only).
- **Tenant prefix**: `tenant.default`.
- **Retention**: `MaxAge = 24h`.
- **Scale-out**: single Fleet node.

### Self-hosted
- **NATS default**: embedded subprocess (same as local); storage under `/data/nats/`.
- **NATS override**: operators set `Fleet:Nats:ExternalUrl` (+ optional creds file) to use their own broker.
- **Auth**: none embedded; creds file if external.
- **Tenant prefix**: `tenant.default` (single-tenant deployment).
- **Retention**: `MaxAge = 7d`.
- **Scale-out**: single node is the expected deployment; multi-node works if operator provides clustered external NATS.

### Managed cloud
- **NATS**: external clustered JetStream we operate. `Fleet:Nats:ExternalUrl` always set; embedded service never spins up.
- **Auth**: a single deployment-level NATS credential (per Decision 11) with access to the full `tenant.*.>` subject space. Tenant isolation is enforced at the application layer (Decision 7), not at the NATS auth boundary. Per-tenant NATS users are a future defence-in-depth option and are not part of the initial managed-cloud rollout.
- **Tenant prefix**: per workspace, resolved at publish + subscribe time from the authenticated request context.
- **Retention**: `MaxAge = 30d`.
- **Scale-out**: multiple Fleet nodes, each with its own durable + ephemeral consumer pair; HTTP layer load-balances clients to any node. Per-node durable consumer names for the WS fan-out projection carry a `{nodeId}` suffix (`fleet-ws-fanout-durable-{nodeId}`) so each node receives its own copy of the stream; the persistence projection uses a single stream-wide consumer (one writer to SQLite).

## Framework To Build

A small fluent DI surface wraps the NATS client and provides stream configuration, publisher, consumer host, and projection listeners.

**Core pieces:**
- `NatsServerHostedService` — launches the bundled `nats-server` subprocess when no external URL is configured; no-op otherwise.
- `NatsStreamInitializer` — hosted service that idempotently creates the durable stream on startup (catch "already exists" and continue). Also pre-creates the durable consumer for every registered projection before publishes are accepted — interest-based retention requires every consumer to be bound so GC cannot run ahead of a late-starting listener.
- `IEventPublisher` + `NatsEventPublisher` — publish abstraction; internally routes each `HarnessEvent` to JetStream or core NATS based on `EventTypeMetadata.Classify`. Validates subject segments (project id, session id) against NATS structural characters (`.`, `*`, `>`, whitespace) before interpolating into a subject.
- `ProjectionListener` — one per registered projection; binds to a pre-created durable JetStream consumer (`AckPolicy.Explicit`, `DeliverPolicy.All`), pumps deserialized `HarnessEvent`s to the projection's `HandleAsync`, acks on success. On handler exception it NAKs up to a bounded retry budget; past that it TERMs the message as poison and records a metric. Deserialization failures are TERM'd immediately.
- `ProjectionHostService` — hosted service that spins up one `ProjectionListener` per registered projection.
- `NatsNamingStrategy` — prepends tenant/isolation prefix to streams/consumers/subjects; resolves the prefix from request/session context. Durable consumer names for fan-out-style projections (WS fan-out) are suffixed with the node id; storage-writer projections (persistence) use a stream-wide consumer.
- `IProjection<HarnessEvent>` — single generic interface; each projection implements `HandleAsync(HarnessEvent, ProjectionContext, CancellationToken)` and dispatches internally on `evt.Type`.
- `EphemeralEventRelayService` — core NATS subscriber for `tenant.*.project.*.live.*.>`. In addition to forwarding events to `IEventBroadcaster`, it hosts the activity-status handler that updates `SessionActivityTracker` and emits `activity_status` broadcasts on the global `"sessions"` topic. This responsibility lived inline in `HarnessEventRelay` before cutover; moving it to the ephemeral subscriber preserves the "initial snapshot on WebSocket subscribe" contract.

**Fluent DI shape:**
```csharp
services.AddEventStore(nats =>
{
    nats.Stream("fleet-sessions")
        .Subjects("tenant.*.project.*.session.*.>")
        .Storage(StreamConfigStorage.File)
        .Retention(StreamConfigRetention.Interest)      // delete once all projections ack
        .MaxAge(TimeSpan.FromDays(7))                   // upper-bound safety net
        .AddProjection<MessagePersistenceProjection>()
        .AddProjection<WebSocketFanOutProjection>();
});
```

All of `Retention`, `MaxAge`, `MaxBytes`, `Storage` are configurable per stream and overridable per hosting mode via `NatsOptions`. Ephemeral core NATS subjects need no stream configuration.

## Cutover Timing and Snapshot Ordering

Moving SQLite writes out of `HarnessEventRelay.PumpAsync` and into `MessagePersistenceProjection` changes the timing relationship between three events that today happen in a fixed local order:

1. `HarnessEventPersistenceService` writes the durable row to SQLite.
2. `IEventBroadcaster.BroadcastAsync(...)` pushes the event to WebSocket clients.
3. A client, on receiving the event, may refetch session state from SQLite.

Today (1) happens strictly before (2) inside the same relay pump, so (3) always observes the row. After cutover, (1) runs in `MessagePersistenceProjection` and (2) runs in `WebSocketFanOutProjection` — both are JetStream consumers of the same durable event, delivered independently. This matters for one specific learning from `conversation-history-fidelity-gaps.md` Task 2: committed `message.updated` payloads are authoritative snapshots the client merge logic depends on. The design must not regress that contract.

**Decisions:**

- **The published durable payload is the final merged snapshot.** `HarnessEventRelay` continues to own text-delta buffering and merges deltas into `message.updated` *before* calling `_publisher.PublishAsync(...)`. The JetStream payload that both `MessagePersistenceProjection` and `WebSocketFanOutProjection` receive is the same byte-for-byte snapshot — the snapshot is not constructed a second time in either projection. Client merge semantics (text-part replacement from the committed snapshot) are unchanged.

- **WS may arrive before SQLite is updated — and that is acceptable.** Without an explicit two-phase commit we cannot guarantee projection-ordering between the SQLite write and the WS broadcast, and we will not introduce one. Instead, the design asserts that no current WS-triggered code path assumes the SQLite row exists by the time the WS event fires. This is verified, not assumed — see the "Dependency audit" item in the verification section below. If a future consumer needs "row exists by event delivery", the correct fix is to make that consumer idempotent-on-refetch (poll-until-present with a small retry budget) rather than to serialize projections.

- **Per-session ordering across projections.** Within a single JetStream consumer, messages for a given session arrive in publish order (guaranteed by per-session serial publish + awaited `PubAck`, decision 5). Across the two projections, each projection sees its own copy of the stream in order; the projections do not coordinate with each other. This is sufficient: a client reconnecting after missing events will catch up from SQLite (populated by `MessagePersistenceProjection`) and then observe new events via WS (delivered by `WebSocketFanOutProjection`) — both paths preserve per-session ordering independently, which is what every current consumer actually relies on.

**Concurrency audit of `HarnessEventPersistenceService`.** Today one instance exists per relay pump (one pump per live harness instance), so per-session state is implicitly serialized. After cutover, one instance services all sessions from the projection callback. Before merging the cutover, audit the service for any state currently relying on per-pump isolation:

- **Text-delta buffer** — already called out: stays in the relay, merged into `message.updated` before publish. Not affected by the service move.
- **In-flight dedup / write-ordering per session** — `MessagePersistenceProjection` must deliver events to the service in per-session order. The projection listener uses a single consumer with `AckPolicy.Explicit` and processes messages serially; per-session order is preserved because publish is per-session serial. If we ever parallelize the projection (partitioned-by-session-hash workers, mentioned in decision 8), each worker must own a disjoint session set to preserve this property.
- **Any instance-scoped caches** (e.g. last-seen message id per session) — these become cross-session state inside one service instance. Audit existing fields and either promote to keyed-by-sessionId maps or verify they are stateless.

This audit is an explicit deliverable of Phase 4 (persistence cutover), not an assumption.

## Observability

Per the constitution's "observable from day one" tenet, the NATS layer is instrumented from Phase 1:

- **Metrics** (OpenTelemetry / `System.Diagnostics.Metrics`) — emitted on the existing `FleetInstrumentation.Meter` (service name `weave_fleet`, see `src/WeaveFleet.Application/Diagnostics/FleetInstrumentation.cs`), matching the `weave_fleet.*` / `snake_case` convention already used for token/cost/message metrics:
  - `weave_fleet.nats.publish.count{routing=durable|ephemeral, event_type, tenant, result=ok|error}`
  - `weave_fleet.nats.publish.duration` (histogram, durable path only — time to `PubAck`)
  - `weave_fleet.nats.consumer.pending{projection, stream}` — JetStream pending-message count per projection
  - `weave_fleet.nats.projection.handler.duration{projection, event_type, result}`
  - `weave_fleet.nats.projection.ack.count{projection, result=ack|nak|term}`
  - `weave_fleet.nats.reconnect.count` — NATS client reconnects
  - `weave_fleet.nats.server.embedded.status` — embedded subprocess health gauge
- **Tracing** — publish and consume spans carry `session.id`, `project.id`, `tenant.id`, `event.type`, `harness.type` attributes. Publish spans link to consumer spans via the `Nats-Msg-Id` header. Relay → publish → projection → repository is one continuous trace.
- **Structured logs** — existing `LoggerMessage` patterns (see `HarnessEventRelay`) extended to cover consumer failures, stream creation, embedded server stdout/stderr at warn+, and projection handler exceptions.

## Critical Files

**New**
- `src/WeaveFleet.Application/Events/IEventPublisher.cs` — publish abstraction, routes by `EventTypeMetadata`.
- `src/WeaveFleet.Application/Projections/IProjection.cs` — `IProjection<HarnessEvent>`.
- `src/WeaveFleet.Application/Projections/MessagePersistenceProjection.cs` — consumes durable events from JetStream; delegates to `HarnessEventPersistenceService` for the actual SQLite writes. Replaces the direct in-relay call to `HarnessEventPersistenceService.TryHandleDurableEventAsync`.
- `src/WeaveFleet.Application/Projections/WebSocketFanOutProjection.cs` — JetStream consumer for durable events, forwards to `IEventBroadcaster`.
- `src/WeaveFleet.Application/Services/EphemeralEventRelayService.cs` — core NATS subscriber for ephemeral events, forwards to `IEventBroadcaster` (counterpart to the durable projection).
- `src/WeaveFleet.Application/Configuration/NatsOptions.cs` — `ExternalUrl`, `CredsFile`, data dir, retention defaults.
- `src/WeaveFleet.Infrastructure/Nats/NatsServerHostedService.cs` — launches bundled `nats-server`.
- `src/WeaveFleet.Infrastructure/Nats/NatsMetrics.cs` — OTel metric definitions.
- `src/WeaveFleet.Infrastructure/Nats/NatsStreamInitializer.cs` — idempotent stream creation.
- `src/WeaveFleet.Infrastructure/Nats/NatsEventPublisher.cs` — `IEventPublisher` impl; routes via `EventTypeMetadata`; builds subjects from `NatsNamingStrategy`; sets `Nats-Msg-Id`; awaits `PubAck` on JetStream path.
- `src/WeaveFleet.Infrastructure/Nats/ProjectionListener.cs` / `ProjectionHostService.cs` — durable consumer pump + lifecycle.
- `src/WeaveFleet.Infrastructure/Nats/NatsNamingStrategy.cs` — subject/stream/consumer prefix helper.
- `src/WeaveFleet.Infrastructure/Nats/Configuration/NatsStreamBuilder.cs` + `NatsServiceCollectionExtensions.cs` — fluent DI.

**Modify**
- `src/WeaveFleet.Infrastructure/Services/HarnessEventRelay.cs` — replace the direct call to `_broadcaster.BroadcastAsync(...)` for ephemeral events and the in-line call to `persistenceService.TryHandleDurableEventAsync(...)` with `await _publisher.PublishAsync(evt, ct)`. The publisher routes internally. Text-delta buffering and merge-on-durable still happen here before publishing durable events.
- `src/WeaveFleet.Infrastructure/Services/HarnessEventPersistenceService.cs` — no longer called from the relay directly; invoked from `MessagePersistenceProjection` instead. Logic unchanged; concurrency model shifts slightly (one service instance per Fleet node servicing all sessions via the projection, instead of one per pump). The text-delta buffer stays in the relay since it needs upstream event timing context.
- `src/WeaveFleet.Api/Program.cs` — register `AddEventStore`, `NatsServerHostedService`, `NatsStreamInitializer`, `ProjectionHostService`.
- `src/WeaveFleet.Application/Configuration/FleetOptions.cs` — nest `NatsOptions`.

**Unchanged (intentional)**
- `src/WeaveFleet.Infrastructure/Services/InMemoryEventBroadcaster.cs` — remains the last-mile in-process fan-out.
- `src/WeaveFleet.Api/Endpoints/WebSocketEndpoints.cs` — consumes `IEventBroadcaster` as today.
- `src/WeaveFleet.Domain/Harnesses/HarnessTypes.cs`, `EventTypes.cs`, `EventTypeMetadata.cs` — already shaped correctly for this design.

## Phased Rollout

1. **NATS plumbing and embedded server.** Land `NatsOptions` (`ExternalUrl`, `CredsFile`, retention defaults), `NatsServerHostedService`, `NatsStreamInitializer`, `IEventPublisher`, `NatsEventPublisher`, subject + header construction via `NatsNamingStrategy`, `NatsMetrics`, and the fluent `AddEventStore` surface. No relay changes yet. Verify `nats-server` launches, stream is created idempotently, publishes from a test-only endpoint land on disk with correct subjects + headers, and metrics/traces emit.

2. **Projection host.** Land `ProjectionListener` + `ProjectionHostService` + `IProjection<HarnessEvent>` + a `NoOpProjection` that logs received events. Proves consumer wiring end-to-end.

3. **Dual-write compatibility window.** Modify `HarnessEventRelay` to *additionally* publish durable events to JetStream and ephemeral events to core NATS, without yet removing the direct persistence call or broadcaster call. Wire the consumer-side pieces (`MessagePersistenceProjection`, `WebSocketFanOutProjection`, `EphemeralEventRelayService`) so they are running during this window, but route their writes to a *shadow* SQLite table and a separate in-memory broadcaster instance used only by a verification harness — the existing production tables and the production `IEventBroadcaster` remain the single source of truth for users. Divergence between the legacy path and the projection path is surfaced by a test-time diff harness that consumes both outputs and asserts equality. This is **not** a runtime feature flag gating user-visible behavior (per constitution/session-source-architecture learning #9, the repo does not use runtime flags for capability rollout): both paths run unconditionally during the window; only the verification outputs are gated, and the window is closed by the cutover phases below, not by flipping a flag.

4. **Persistence cutover.** Once shadow outputs match, remove the direct `HarnessEventPersistenceService` call from the relay. `MessagePersistenceProjection` becomes the sole SQLite writer for durable events.

5. **Broadcast cutover.** Remove the direct `IEventBroadcaster.BroadcastAsync` call from the relay for ephemeral events. `EphemeralEventRelayService` + `WebSocketFanOutProjection` feed the broadcaster exclusively.

6. **Managed-cloud tenant wiring.** Subject-prefix construction becomes per-workspace (currently `tenant.default`). Application-layer authz at WebSocket subscribe asserts session ownership before forwarding events. Fleet's NATS creds file is provisioned with the deployment. Gated until managed-cloud rollout begins.

## Verification

**Unit tests**
- `NatsEventPublisher` routes to JetStream vs core NATS based on `EventTypeMetadata`; subject correctly derived; `Nats-Msg-Id` set on durable path.
- `HarnessEventRelay` pumps events with awaited `PubAck` on durable path (mock publisher, assert order + serial awaits).
- `MessagePersistenceProjection` delegates to `HarnessEventPersistenceService` and produces SQLite rows matching existing schema.
- `WebSocketFanOutProjection` and `EphemeralEventRelayService` forward to `IEventBroadcaster` preserving topic, userId, payload.
- `NatsServerHostedService` no-ops when `ExternalUrl` is set; launches subprocess otherwise.
- `NatsStreamInitializer` is idempotent on repeated boots.

**Integration tests** (embedded NATS in the test host)
- End-to-end: fake harness emits the full `EventTypes` range; durable events land in SQLite via projection; ephemeral events reach a WebSocket test client via broadcaster.
- Mid-stream restart: pending JetStream messages redeliver on restart; projection resumes from last ack without duplicates (dedup header honoured).
- Tenant-prefix isolation: two synthetic tenants' events don't leak across scoped subscribe contexts.
- Retention: messages disappear from the stream once all projections have acked; disappear after `MaxAge` even if a projection stalls.

**Manual verification**
- **Local dev**: launch Fleet with no extra config. Confirm `nats-server` subprocess starts, stream is created, a real Claude Code / OpenCode session produces events that land in SQLite via the projection and appear at a WebSocket client via the broadcaster.
- **Self-hosted**: same, plus one run with `Fleet:Nats:ExternalUrl` pointing at a separately-launched NATS container. Confirm embedded path is skipped and the stream is created on the external broker.
- **Storage sanity**: shut down Fleet, inspect `./data/nats/jetstream/`, restart, confirm stream persists and re-creation is idempotent.

**Dependency audit (pre-Phase-4 deliverable)**
- Enumerate every code path that reacts to a durable harness event on the WebSocket client and then reads from SQLite (directly or via refetch). For each, confirm it tolerates "SQLite row not yet written" — either because it only reads from the event payload, or because the refetch is retry-tolerant. This is the concrete check that backs the "WS may arrive before SQLite" decision in the Cutover Timing section. Blocking any path that assumes strict ordering is a precondition for Phase 4.
- Audit `HarnessEventPersistenceService` for instance-scoped state that relied on per-pump isolation (see the concurrency audit bullet in the Cutover Timing section). Record findings alongside the Phase 4 plan before touching the relay.

## Pre-flight Checklist (implementation-plan deliverables, not design changes)

These items belong in the implementation plan that follows this design. They are collected here as a single checklist so the plan author does not rediscover them from the learnings folder:

- **Verify every entry in "Critical Files → Modify" exists at the referenced path** before the plan locks scope (`HarnessEventRelay`, `HarnessEventPersistenceService`, `Program.cs`, `FleetOptions.cs`). Past learnings (`session-source-architecture`, `session-archive-delete-lifecycle`, `shouldly-assertion-style-adoption`) repeatedly show plans referencing files as "new" that exist or vice versa.
- **`ApiWebApplicationFactory` is `sealed`** (tenant-isolation-analytics learning). Any integration test needing a host must subclass `WebApplicationFactory<Program>` directly, not `ApiWebApplicationFactory`.
- **CSRF + antiforgery on mutating HTTP endpoints.** If any test-only publish/diagnostic endpoint is introduced in Phase 1, tests must set `HandleCookies = true`, GET first to seed the antiforgery state, and send the `.WeaveFleet.CSRF` value as `X-CSRF-Token` on non-GET requests. See `UserAuthEndpointTests.UpdateConfig_WithCsrfToken_Succeeds` for the reference pattern.
- **New test/host projects under `tests/`** that are not test projects (none currently planned — embedded NATS is a subprocess, not a .NET project — but noted for completeness): `tests/Directory.Build.props` applies a global `<Using Include="Shouldly" />`. A non-test web project under `tests/` must add a local `Directory.Build.props` that imports the parent and removes the Shouldly using (pattern from `duende-idp-auth-e2e-pipeline` learning, Task 1).
- **Async-disposable test classes** may trip CA1001 even when `IAsyncLifetime.DisposeAsync` correctly handles disposal. `#pragma warning disable CA1001` around the class declaration is the established workaround.
- **Parallel `dotnet test` runs on shared build outputs** can cause transient `.deps.json` file locks (shouldly-assertion-style-adoption learning, Task 6). Verification steps that run multiple .NET test filters must run sequentially, not in parallel.
- **Baseline pre-existing test failures** before starting (e.g. `SkillEndpointPathTraversalTests.GetSkill_ReturnsBadRequestOrNotRouted_ForEncodedTraversal` has been flagged pre-existing in multiple prior plans). Record the baseline so NATS-introduced regressions are distinguishable from pre-existing noise.
- **Manual UX validation stays separate from automated verification.** The plan's completion criteria must keep the manual verification checklist distinct from test-pass assertions (task-delegation-card-v3 verification-sweep learning).
