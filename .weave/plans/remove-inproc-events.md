# Remove inproc_events Table and Projection Infrastructure

## TL;DR
Remove the `inproc_events` table and all dead projection infrastructure (`InProcessEventStore`, `InProcessProjectionHost`, `ProjectionRegistry`, `InProcessEventBusBuilder`, `IProjection<T>`, `ProjectionContext`). Simplify `InProcessEventPublisher.PublishDurable` to remove persistence and projection wake-up while keeping fan-out broadcast, automation channel write, and provisional ID counter.

## Context

The `inproc_events` table was part of a durable event pipeline that fed `MessagePersistenceProjection`. That projection was removed in the thin-proxy simplification (`.weave/plans/thin-proxy-simplification.md`, Task 8). The table and its infrastructure are now dead weight:

- `InProcessProjectionHost` reads from `inproc_events` and dispatches to zero registered projections (the `AddInProcessEventBus` call in `DependencyInjection.cs:195` registers no projections).
- `InProcessEventStore` writes to `inproc_events` on every durable event, adding unnecessary SQLite I/O.
- `InProcessEventPublisher.PublishDurable` persists events and signals `ProjectionWakeUp` — both are no-ops downstream.

### Key files
| File | Role |
|---|---|
| `src/WeaveFleet.Infrastructure/EventBus/InProcessEventStore.cs` | SQLite CRUD for `inproc_events` |
| `src/WeaveFleet.Infrastructure/EventBus/InProcessProjectionHost.cs` | BackgroundService dispatching to projections |
| `src/WeaveFleet.Infrastructure/EventBus/ProjectionRegistry.cs` | Registry of projection types + `ConsumerScope` enum |
| `src/WeaveFleet.Infrastructure/EventBus/InProcessEventBusBuilder.cs` | Fluent builder for registering projections |
| `src/WeaveFleet.Infrastructure/EventBus/InProcessChannels.cs` | Holds `ProjectionWakeUp`, `FanOut`, `AutomationEvents` channels |
| `src/WeaveFleet.Infrastructure/EventBus/InProcessEventPublisher.cs` | Publishes durable + ephemeral events |
| `src/WeaveFleet.Infrastructure/EventBus/InProcessServiceCollectionExtensions.cs` | DI wiring for event bus |
| `src/WeaveFleet.Application/Events/IEventStore.cs` | Interface for event store |
| `src/WeaveFleet.Application/Projections/IProjection.cs` | Projection interface |
| `src/WeaveFleet.Application/Projections/ProjectionContext.cs` | Projection context record |
| `src/WeaveFleet.Infrastructure/Migrations/028_drop_dead_tables.sql` | Migration that currently drops `messages` and `harness_events` only |
| `tests/WeaveFleet.Infrastructure.Tests/EventBus/InProcessTests.cs` | Tests for store, publisher, projection host |
| `tests/WeaveFleet.Infrastructure.Tests/Services/HarnessEventRelayTests.cs` | Creates `InProcessEventStore` + `InProcessEventPublisher` for relay tests |

### What stays intact
- `InProcessChannels.FanOut` and `InProcessChannels.AutomationEvents` channels
- Fan-out broadcast in publisher (already happens before persist)
- Automation channel write in publisher (already happens before persist)
- Provisional negative ID counter (in-memory, no SQLite dependency)
- `InProcessFanOutService` (broadcast-only, no store dependency)
- `InProcessMetrics`, `PipelineLatencyMetrics`
- Outbox infrastructure (`InProcessOutboxDispatcher`, `OutboxDispatchBackgroundService`, `OutboxCleanupBackgroundService`, `IOutboxDispatcher`) — still used by `SessionActivityWriteService`
- `IsDurable` classification — kept for metrics/logging (cosmetic)

## Scope
- In scope:
  - Delete `InProcessEventStore`, `IEventStore`, `InProcessProjectionHost`, `ProjectionRegistry`, `InProcessEventBusBuilder`
  - Delete `IProjection<T>`, `ProjectionContext`
  - Remove `ProjectionWakeUp` channel from `InProcessChannels`
  - Simplify `InProcessEventPublisher.PublishDurable` (remove persist + wake-up)
  - Simplify `InProcessServiceCollectionExtensions` (remove store, projection host, registry, builder)
  - Update migration 028 to also drop `inproc_events` table and index
  - Delete or update affected tests
- Out of scope:
  - Removing `IsDurable` classification or `EventTypeMetadata.Classify` (cosmetic, no functional impact)
  - Removing outbox infrastructure (still actively used)
  - Changing ephemeral event path (already correct)
  - Removing `InProcessEnvelope` or `InProcessMetrics` (still used by fan-out)
- Constraints / assumptions:
  - `InProcessEventPublisher` constructor signature changes (removes `InProcessEventStore` param), which affects all tests that instantiate it
  - The `PublishResult.EventId` for durable events will now always be the provisional negative ID (no SQLite rowid). Callers that check `IsDuplicate` need review — currently only `InProcessEventPublisher` itself uses it internally.

## Objectives
1. Remove all SQLite I/O on the durable event hot path (eliminate `inproc_events` writes)
2. Remove the idle `InProcessProjectionHost` background service
3. Drop the `inproc_events` table via migration
4. Keep fan-out broadcast, automation dispatch, and metrics intact

## Dependencies and Order

The migration has 3 phases:
1. **Phase 1: Simplify publisher and channels** — Remove store dependency from publisher, remove `ProjectionWakeUp` from channels. System still works; projection host becomes unreachable.
2. **Phase 2: Delete dead infrastructure** — Remove store, projection host, registry, builder, projection interfaces, and DI wiring.
3. **Phase 3: Migration and test cleanup** — Update migration 028, delete/update tests.

Each phase leaves the system compilable.

## Tasks

### Phase 1: Simplify publisher and channels

- [x] 1. Simplify `InProcessEventPublisher.PublishDurable`
  - **What**: Remove the `InProcessEventStore` dependency from the constructor. In `PublishDurable`: remove the `_store.AppendIdempotent` call (step 3) and `_channels.ProjectionWakeUp.Writer.TryWrite` call (step 4). Keep the provisional ID counter, fan-out channel write, automation channel write, and metrics. The method now returns `new PublishResult(provisionalId, IsDuplicate: false)` always (no duplicate detection without the store — duplicates were only meaningful for the persistence layer). Update the XML doc comment to reflect the simplified flow. Remove the `_store` field.
  - **Files**:
    - `src/WeaveFleet.Infrastructure/EventBus/InProcessEventPublisher.cs`
  - **Depends on**: None
  - **Acceptance**:
    - Constructor no longer takes `InProcessEventStore`
    - `PublishDurable` does not reference `_store` or `ProjectionWakeUp`
    - Fan-out and automation channel writes are preserved
    - Provisional negative ID assignment is preserved
    - Metrics recording (`_metrics`, `_pipelineMetrics`) is preserved
    - File compiles in isolation (no missing references)

- [x] 2. Remove `ProjectionWakeUp` channel from `InProcessChannels`
  - **What**: Delete the `ProjectionWakeUp` property and its XML doc comment from `InProcessChannels`. Remove the `using System.Threading.Channels;` if no longer needed (it is still needed for `FanOut` and `AutomationEvents`). Update the class XML doc to say "two" instead of implying three channels.
  - **Files**:
    - `src/WeaveFleet.Infrastructure/EventBus/InProcessChannels.cs`
  - **Depends on**: Task 1 (publisher no longer writes to `ProjectionWakeUp`)
  - **Acceptance**:
    - `ProjectionWakeUp` property is gone
    - `FanOut` and `AutomationEvents` channels are untouched
    - File compiles

### Phase 2: Delete dead infrastructure

- [x] 3. Delete `InProcessEventStore` and `IEventStore`
  - **What**: Delete both files. These are no longer referenced after Task 1.
  - **Files**:
    - `src/WeaveFleet.Infrastructure/EventBus/InProcessEventStore.cs` (delete)
    - `src/WeaveFleet.Application/Events/IEventStore.cs` (delete)
  - **Depends on**: Task 1
  - **Acceptance**:
    - Files deleted
    - No remaining references to `InProcessEventStore` or `IEventStore` in `src/`

- [x] 4. Delete `InProcessProjectionHost`
  - **What**: Delete the file. The background service dispatches to zero projections and reads from a channel (`ProjectionWakeUp`) that no longer exists after Task 2.
  - **Files**:
    - `src/WeaveFleet.Infrastructure/EventBus/InProcessProjectionHost.cs` (delete)
  - **Depends on**: Tasks 2, 3
  - **Acceptance**:
    - File deleted
    - No remaining references to `InProcessProjectionHost` in `src/`

- [x] 5. Delete `ProjectionRegistry`, `InProcessEventBusBuilder`, and projection interfaces
  - **What**: Delete all four files:
    - `ProjectionRegistry.cs` (includes `ProjectionRegistry`, `ProjectionRegistryEntry`, `ConsumerScope`)
    - `InProcessEventBusBuilder.cs`
    - `IProjection.cs`
    - `ProjectionContext.cs`
  - **Files**:
    - `src/WeaveFleet.Infrastructure/EventBus/ProjectionRegistry.cs` (delete)
    - `src/WeaveFleet.Infrastructure/EventBus/InProcessEventBusBuilder.cs` (delete)
    - `src/WeaveFleet.Application/Projections/IProjection.cs` (delete)
    - `src/WeaveFleet.Application/Projections/ProjectionContext.cs` (delete)
  - **Depends on**: Task 4
  - **Acceptance**:
    - All four files deleted
    - No remaining references to `ProjectionRegistry`, `InProcessEventBusBuilder`, `IProjection`, `ProjectionContext`, or `ConsumerScope` in `src/`

- [x] 6. Simplify `InProcessServiceCollectionExtensions`
  - **What**: The `AddInProcessEventBus` method currently: creates a builder, runs the configure callback, registers `ProjectionRegistry`, `InProcessChannels`, `InProcessEventStore`, `IEventStore`, `InProcessMetrics`, `PipelineLatencyMetrics`, `IEventPublisher`, `InProcessProjectionHost`, and `InProcessFanOutService`. After this change: remove the `Action<InProcessEventBusBuilder> configure` parameter entirely. Remove registrations for `ProjectionRegistry`, `InProcessEventStore`, `IEventStore`, and `InProcessProjectionHost`. Keep registrations for `InProcessChannels`, `InProcessMetrics`, `PipelineLatencyMetrics`, `IEventPublisher` (as `InProcessEventPublisher`), `InProcessFanOutService`, and the automation events channel exposure. Remove the `InProcessEventBusBuilder` usage. Update the method signature to `AddInProcessEventBus(this IServiceCollection services)` (no configure callback).
  - **Files**:
    - `src/WeaveFleet.Infrastructure/EventBus/InProcessServiceCollectionExtensions.cs`
  - **Depends on**: Tasks 3, 4, 5
  - **Acceptance**:
    - Method signature is `AddInProcessEventBus(this IServiceCollection services)`
    - No references to deleted types
    - `InProcessEventPublisher` registration updated (no longer needs `InProcessEventStore` from DI)
    - File compiles

- [x] 7. Update DI registration call site
  - **What**: In `DependencyInjection.cs`, change `services.AddInProcessEventBus(bus => { })` to `services.AddInProcessEventBus()` (no callback). The empty lambda is now unnecessary since the method no longer accepts a configure callback.
  - **Files**:
    - `src/WeaveFleet.Infrastructure/DependencyInjection.cs`
  - **Depends on**: Task 6
  - **Acceptance**:
    - Call site updated to parameterless `AddInProcessEventBus()`
    - Solution compiles: `dotnet build WeaveFleet.slnx`

### Phase 3: Migration and test cleanup

- [x] 8. Update migration 028 to drop `inproc_events`
  - **What**: Add `DROP INDEX IF EXISTS idx_inproc_events_message_id;` and `DROP TABLE IF EXISTS inproc_events;` to the existing `028_drop_dead_tables.sql` migration. Update the comment header to reflect that `inproc_events` is now also dead. Remove the "NOT dropping" comment about `inproc_events`.
  - **Files**:
    - `src/WeaveFleet.Infrastructure/Migrations/028_drop_dead_tables.sql`
  - **Depends on**: Task 7
  - **Acceptance**:
    - Migration drops `inproc_events` table and its index
    - Existing `messages` and `harness_events` drops are preserved
    - Application starts cleanly on fresh and existing databases

- [x] 9. Delete `InProcessEventStoreTests` and `InProcessProjectionHostTests`
  - **What**: Delete the `InProcessEventStoreTests` class (lines 16-204) and `InProcessProjectionHostTests` class (lines 406-480) from `InProcessTests.cs`. Keep `InProcessEventPublisherTests` and `InProcessFanOutServiceTests`.
  - **Files**:
    - `tests/WeaveFleet.Infrastructure.Tests/EventBus/InProcessTests.cs`
  - **Depends on**: Tasks 3, 4, 5
  - **Acceptance**:
    - `InProcessEventStoreTests` and `InProcessProjectionHostTests` classes removed
    - `InProcessEventPublisherTests` and `InProcessFanOutServiceTests` retained

- [x] 10. Update `InProcessEventPublisherTests`
  - **What**: The publisher tests currently instantiate `InProcessEventStore` and pass it to the publisher constructor. After Task 1, the publisher no longer takes a store. Update all test methods to:
    - Remove `InProcessEventStore` creation
    - Remove `TestDbHelper.CreateSharedDbAsync()` calls (no longer needed for publisher tests — no SQLite dependency)
    - Remove assertions about `store.ReadPending` (no store)
    - Remove assertions about `channels.ProjectionWakeUp` (no wake-up channel)
    - Update `duplicate_durable_event_is_dropped_silently`: durable events are no longer deduplicated by the publisher (no store). This test should be deleted or rewritten to verify that two publishes with the same context both produce fan-out writes.
    - Update `duplicate_correlation_id_is_idempotent`: same — no dedup. Delete or rewrite.
    - Keep assertions about `channels.FanOut` (fan-out still works)
    - Keep assertions about provisional negative IDs
  - **Files**:
    - `tests/WeaveFleet.Infrastructure.Tests/EventBus/InProcessTests.cs`
  - **Depends on**: Task 1
  - **Acceptance**:
    - Publisher tests compile without `InProcessEventStore`
    - No references to `ProjectionWakeUp` in tests
    - Tests pass

- [x] 11. Update `HarnessEventRelayTests`
  - **What**: The relay tests at lines 128 and 205 create `InProcessEventStore` to pass to `InProcessEventPublisher`. After Task 1, the publisher no longer takes a store. Remove `InProcessEventStore` creation and `TestDbHelper.CreateSharedDbAsync()` calls from these test methods. The relay tests should still work since they test event relay behavior, not persistence.
  - **Files**:
    - `tests/WeaveFleet.Infrastructure.Tests/Services/HarnessEventRelayTests.cs`
  - **Depends on**: Task 1
  - **Acceptance**:
    - No references to `InProcessEventStore` in relay tests
    - Relay tests compile and pass

- [x] 12. Verify build and tests
  - **Depends on**: All previous tasks
  - **Acceptance**:
    - `dotnet build WeaveFleet.slnx` succeeds with no errors
    - `dotnet test WeaveFleet.slnx --filter "Category!=E2E&Category!=Benchmark"` passes
    - `grep -r "InProcessEventStore\|InProcessProjectionHost\|ProjectionRegistry\|InProcessEventBusBuilder\|IProjection<\|ProjectionContext\|ProjectionWakeUp" src/ tests/` returns no matches

## Verification

After all tasks are complete:

```bash
# Solution compiles
dotnet build WeaveFleet.slnx

# All non-E2E tests pass
dotnet test WeaveFleet.slnx --filter "Category!=E2E&Category!=Benchmark"

# No references to deleted types remain
grep -rn "InProcessEventStore\|InProcessProjectionHost\|ProjectionRegistry\|InProcessEventBusBuilder\|IProjection<\|ProjectionContext\|ProjectionWakeUp\|inproc_events" src/ tests/ --include="*.cs"

# Migration applies cleanly (start the app against a fresh + existing database)
```

Passing output: all commands exit 0, grep returns no matches.
