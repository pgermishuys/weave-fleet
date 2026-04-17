# Unified Fan-Out — Single NATS-Driven Delivery Path for Harness Events

## Status

Draft. Supersedes the dual-path (durable-via-outbox + ephemeral-via-core-NATS) section of
`docs/nats-event-substrate-design.md`.

## Context

Today Weave Fleet delivers harness events to WebSocket clients via two independent
paths that converge at `IEventBroadcaster`:

1. **Durable events** (`message.*` lifecycle + `session.updated/error/compacted/deleted`)
   — `NatsEventPublisher` publishes to JetStream with an awaited `PubAck`.
   `MessagePersistenceProjection` consumes the stream and delegates to
   `HarnessEventPersistenceService`, which in one DB transaction upserts
   messages/sessions **and** appends a row to the `outbox` table.
   `InProcessOutboxDispatcher` polls the outbox and forwards rows to the broadcaster.
2. **Ephemeral events** (`message.part.delta`, `session.status`, `session.idle`,
   `error`, `permission.*`) — `NatsEventPublisher` fire-and-forgets to core NATS.
   `EphemeralEventRelayService` subscribes to `.live.>`, buffers delta text into
   `TextDeltaBuffer`, parses activity-status, and forwards to the broadcaster.

Two problems motivate this redesign:

- **No cross-path ordering.** Each path is ordered per-session, but the two are
  independent — a trailing `message.part.delta` can race a `message.updated`
  snapshot and produce tearing at the client.
- **Durable fan-out is slow.** Outbox polling (`Outbox.PollIntervalMilliseconds`)
  plus DB round-trips add latency for events that should feel instant. Naïvely
  collapsing onto a single memory-backed JetStream would fix ordering and speed but
  puts the delta firehose (~60 msg/s/session × thousands of sessions at hosted
  scale) through JetStream storage and ACK bookkeeping — untenable.

The goal is **one ordered delivery path from harness to client**, without putting
ephemeral firehose traffic through JetStream, while preserving durable
at-least-once-to-SQLite for the events that need it.

## Constitution Alignment

- **Local-first, cloud-ready** — one topology works in both embedded-NATS
  single-node and multi-node cloud deployments. No mode switch.
- **Invisible infrastructure** — collapses two fan-out components into one. One
  backpressure surface to reason about.
- **Real-time by default** — durable events reach clients via the fast core NATS
  fan-out instead of the DB-polled outbox. Latency drops from hundreds of
  milliseconds to sub-millisecond.
- **Data is a first-class output** — SQLite persistence guarantees unchanged.
  Durable events still land in SQLite via the `MessagePersistenceProjection`
  consumer; reconnecting clients still rehydrate from there.

## Target Architecture

**One publish per harness event. Two independent observers on the NATS side.**

```
 HarnessEventRelay.PumpAsync
        │ 1. classifies via EventTypeMetadata, assigns Sequence
        │ 2. applies reasoning filter (RequiresReasoningFilter == true)
        ▼
 NatsEventPublisher.PublishAsync  (unchanged branch logic)
        │
        ├─ durable → js.PublishAsync(subject, ..., MsgId=…)    ── PubAck awaited
        └─ ephemeral → connection.PublishAsync(subject, ...)   ── fire-and-forget
                │
                ▼
         (single core NATS subject tree: tenant.*.project.*.session.*.>)
                │
                ├─ core NATS subscription ──► WebSocketFanOutSubscriber
                │                              (delta-buffer + activity-status + broadcaster)
                │
                └─ JetStream stream FLEET_EVENTS
                   (Subjects = durable leaf types only)
                   │
                   └─► MessagePersistenceProjection → SQLite
                                                    (no outbox write for harness events)
```

**Why this delivers the "single ordered stream" guarantee:**

A `js.PublishAsync` is a core NATS publish *plus* stream capture — any core NATS
subscriber on that subject still sees the message. `HarnessEventRelay.PumpAsync`
is a single-writer loop per harness session, so publishes are serialised on the
publisher's NATS connection. NATS preserves per-publisher-connection delivery
order on the subscriber side. The unified fan-out subscriber therefore sees every
event for a given session in the exact order the relay emitted them — regardless
of whether each event took the durable or ephemeral publish branch inside
`NatsEventPublisher`.

The client's existing reducers (`client/src/lib/event-state.ts`) treat
`message.updated` snapshots as authoritative; combined with the new end-to-end
ordering guarantee, the delta→snapshot→normal-state transition is tear-free.

## Component-by-Component

### `NatsNamingStrategy` (`src/WeaveFleet.Infrastructure/Nats/NatsNamingStrategy.cs`)

- Rename `DurableSubject` → `Subject`. Single subject tree:
  `tenant.{ws}.project.{pid}.session.{sid}.{type}`.
- Delete `EphemeralSubject` and its `.live.` infix.
- Rename `DurableStreamFilter` → `FanOutSubscriptionFilter`
  (`tenant.*.project.*.session.*.>`).
- Delete `EphemeralSubscriptionFilter`.
- Add `DurableStreamSubjects` — enumerated list of durable leaf subject patterns
  for `StreamConfig.Subjects`:
  `tenant.*.project.*.session.*.message.created`,
  `...message.updated`, `...message.part.updated`, `...message.removed`,
  `...message.part.removed`, `...session.updated`, `...session.error`,
  `...session.compacted`, `...session.deleted`.
- Rename `ParseDurableSubject` → `ParseSubject`; shape is identical.

### `NatsStreamInitializer` (`src/WeaveFleet.Infrastructure/Nats/NatsStreamInitializer.cs`)

- `StreamConfig(_options.StreamName, NatsNamingStrategy.DurableStreamSubjects)` —
  pass the enumerated list rather than the wildcard.
- `ConsumerConfig.FilterSubject` — set to the stream's subject wildcard
  (`tenant.*.project.*.session.*.>`). Redundant with the narrowed `Subjects` list
  but keeps consumer config self-describing.
- Everything else unchanged (file storage, interest retention, MaxAge/MaxBytes,
  pre-created consumers).

### `NatsEventPublisher` (`src/WeaveFleet.Infrastructure/Nats/NatsEventPublisher.cs`)

- Subject construction moves to the renamed `Subject` method.
- Branch logic unchanged: durable → `js.PublishAsync` with `PubAck`; ephemeral →
  `connection.PublishAsync`.

### `HarnessEventRelay.PumpAsync`

- **New step before publish:** if
  `EventTypeMetadata.Classify(evt.Type).RequiresReasoningFilter`, sanitize
  `evt.Payload` via `MessagePersistenceService.SanitizeDurableEventPayload` and
  replace the payload on the event record. Today that filter runs only on the
  outbox path; after the refactor the outbox is no longer involved for harness
  events, so the sanitization must move up.
- Existing sequence assignment, dispose flush, and activity-idle broadcast remain
  unchanged.

### `EphemeralEventRelayService` → `WebSocketFanOutSubscriber`

Rename and move to
`src/WeaveFleet.Infrastructure/Nats/WebSocketFanOutSubscriber.cs`.

- **Subscription filter:** `NatsNamingStrategy.FanOutSubscriptionFilter`
  (`tenant.*.project.*.session.*.>`) — whole tree, not just `.live.`.
- **Subject parsing:** use `NatsNamingStrategy.ParseSubject` (the renamed helper).
- **Existing behaviours retained verbatim:**
  - Oversize / malformed-payload guards.
  - `IEventBroadcaster.BroadcastAsync($"session:{sid}", …)`.
  - `SessionActivityTracker.Update(...)` + `activity_status` broadcast on
    `sessions` topic.
  - `IHarnessEventPersister.BufferTextDelta` for `message.part.delta`.
- **New behaviour:** handles durable events too. Same code paths apply — the
  activity-status branch and delta-buffer branch short-circuit for types that
  don't match, so adding durable events to the subscription doesn't change
  branching semantics.

### `HarnessEventPersistenceService`

- **Stop writing outbox rows for harness events.** Keep all SQLite writes
  (message/session upserts, deletes, title updates). Callers of
  `_sessionActivityWriteService.WriteAsync` drop the `OutboxMessages` array for
  harness events.
- `EmitOutboxOnlyAsync` / `EmitOutboxEventAsync` — delete (no remaining callers
  after the conversion).
- `WriteDurableEventAsync` — still persists the merged message but no longer
  includes an `OutboxMessages` entry.
- Reasoning-filter call site inside this service (`SanitizeDurableEventPayload`
  during outbox build) goes away — filter now runs upstream in the relay.
- Text-delta buffering and `FlushBufferedDeltasAsync` unchanged, except the
  flush-time `OutboxMessages` entry is dropped; the flushed `message.updated` is
  now published naturally on the NATS path at next session activity.
  - *Open question:* does the flush-time outbox write serve a separate purpose
    (e.g. re-broadcasting on client reconnect before the next natural update)?
    Current behaviour: the flushed merged message goes to the outbox so any
    connected client sees the final text even after the harness crashed. Under
    the new design: after a crash, no further NATS events fire for that session.
    Clients reconnecting will refetch the flushed merged text from SQLite on
    their initial page load (standard reconnect path). Dropping the outbox write
    is acceptable — the merged text is still in the DB.

### `MessagePersistenceProjection` (`src/WeaveFleet.Application/Projections/MessagePersistenceProjection.cs`)

- **Unchanged.** Still a JetStream consumer, still delegates to
  `IHarnessEventPersister`. Because the stream's narrowed `Subjects` list only
  contains durable leaf types, the projection only sees durable events. The
  existing `EventTypeMetadata.Classify(evt.Type).IsDurable` check in the
  persister becomes cheap defence in depth.

### Outbox infrastructure (`IOutboxRepository`, `InProcessOutboxDispatcher`, `OutboxDispatchBackgroundService`, `OutboxCleanupBackgroundService`)

- **Unchanged.** Still serves non-harness domain events published via
  `DelegationService` and any other `SessionActivityWriteService` callers that
  pass an `OutboxMessages` array. The `outbox` table continues to exist and be
  polled; it just carries a narrower set of event types.

### Client (`client/src/lib/event-state.ts`, `client/src/hooks/use-session-events.ts`)

- **Unchanged.** Receives the same events on the same WebSocket topic. Ordering
  is now reliable end-to-end, so the reducers' implicit per-session ordering
  assumption is upheld by the transport instead of being coincidence.

## Trade-Offs and Things to Watch

- **SQLite writes no longer gate client delivery.** Today the durable event
  reaches the client only after `HarnessEventPersistenceService` has committed the
  transaction. Under the new design, the client sees the event as soon as the
  core NATS subscriber forwards it, which is typically before
  `MessagePersistenceProjection` has acked the JetStream message. Any client code
  that immediately re-queries SQLite after a WS event could see stale state
  briefly; the design doc already asserts "no current WS-triggered code path
  assumes the SQLite row exists by the time the WS event fires" — re-verify
  before merging.
- **Outbox dedup is gone for harness events.** The outbox table's `id` column
  provided at-least-once dedup to clients. Under the new design, if the fan-out
  subscriber re-delivers on reconnect, clients would see duplicates — but
  core NATS doesn't redeliver (subscription is fresh each time) and the client
  reducer already handles replayed snapshots idempotently via
  `mergeMessageUpdate`. Low risk.
- **Backpressure concentrates on one subscriber.** If the fan-out subscriber
  lags, all client delivery lags. Operationally simpler than today's two
  independent queues; worth a metric on broadcaster queue depth.
- **Durable `PubAck` still gates the relay pump.** Today an ephemeral publish is
  fire-and-forget; it remains so. Durable `PubAck` serialises the next pump
  iteration as today. Net latency is equal or better because deltas no longer
  wait for a slow outbox poll.
- **Reasoning filter is now load-bearing at publish time.** If the filter is
  skipped (or added later to a new event type without the classification flag),
  raw reasoning content could reach clients. Add a test that asserts a reasoning
  payload is stripped before `IEventPublisher.PublishAsync` is called.

## Verification

### Unit

- `NatsEventPublisher`: existing tests updated for the single-subject-tree name.
- `NatsStreamInitializer`: test that the stream is configured with the enumerated
  `DurableStreamSubjects` list.
- `HarnessEventRelay`: new test — an event whose classification requires reasoning
  filtering has its payload sanitized before it reaches the publisher.
- `WebSocketFanOutSubscriber`: given a publisher emitting mixed durable + ephemeral
  events for one session, the subscriber receives them in publish order and
  broadcasts each on `session:{sid}`. Delta-buffer and activity-status side
  channels still fire for their respective event types.
- `HarnessEventPersistenceService`: writes to message/session repositories as
  today; no call to the `OutboxMessages` path for harness event types.

### Integration

- Existing NATS integration tests (publisher → stream → projection) unchanged in
  shape; assertions update for the narrowed `Subjects` list and the new
  "fan-out subscriber receives durable events too" behaviour.

### End-to-end

- `tests/WeaveFleet.E2E/` streaming scenario: WebSocket client receives
  `delta × N` then `message.updated` then no further deltas for that message, in
  that order. Final merged text matches the snapshot.
- Auth E2E: tearing/ordering assertions around the delta→snapshot transition,
  which were previously flaky-by-design, should now pass deterministically.

### Performance sanity (not a gate)

- Drive ~100 concurrent streaming sessions (~6k msg/s). JetStream publish count
  reflects only durable rate (~100–500 msg/s). Outbox table row count for harness
  events stays at zero. Broadcaster queue depth stays near zero.

## Files Touched (Summary)

- Modify: `src/WeaveFleet.Infrastructure/Nats/NatsNamingStrategy.cs`
- Modify: `src/WeaveFleet.Infrastructure/Nats/NatsStreamInitializer.cs`
- Modify: `src/WeaveFleet.Infrastructure/Nats/NatsEventPublisher.cs`
- Modify: `src/WeaveFleet.Infrastructure/Services/HarnessEventRelay.cs`
- Rename + expand: `src/WeaveFleet.Infrastructure/Nats/EphemeralEventRelayService.cs`
  → `WebSocketFanOutSubscriber.cs`
- Modify: `src/WeaveFleet.Infrastructure/Services/HarnessEventPersistenceService.cs`
- Modify: `src/WeaveFleet.Infrastructure/DependencyInjection.cs` (registration name
  only; behaviour unchanged)
- Update: `docs/nats-event-substrate-design.md` (cross-reference this document,
  strike the dual-path description).
- Update: associated tests under `tests/WeaveFleet.Infrastructure.Tests/` and
  `tests/WeaveFleet.E2E/`.

## Out of Scope

- Memory-backed JetStream for ephemerals (rejected per hosted-scale volume).
- Client-side sequence-based merge buffering (not needed once server ordering
  holds).
- Outbox schema / dispatcher changes for non-harness domain events.
- `SessionActivityTracker` behaviour.
