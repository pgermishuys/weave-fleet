# NATS Event Substrate — Implementation Plan

> **For agentic workers:** REQUIRED SUB-SKILL: Use superpowers:subagent-driven-development (recommended) or superpowers:executing-plans to implement this plan task-by-task. Steps use checkbox (`- [ ]`) syntax for tracking.

**Goal:** Introduce NATS (core + JetStream) as the event substrate behind `HarnessEventRelay`, moving durable-event SQLite writes and ephemeral-event WebSocket fan-out off the in-process path without regressing any user-visible contract.

**Architecture:** Durable `HarnessEvent`s publish to JetStream stream `fleet-sessions` on `tenant.{ws}.project.{pid}.session.{sid}.{type}`; a `MessagePersistenceProjection` consumes the stream and delegates to the existing `HarnessEventPersistenceService` for SQLite writes; a `WebSocketFanOutProjection` forwards the same events to `IEventBroadcaster`. Ephemeral events publish to core NATS on `tenant.{ws}.project.{pid}.live.{sid}.{type}`; a per-node `EphemeralEventRelayService` forwards them to `IEventBroadcaster`. Rollout is gated by a dual-write compatibility window with a test-only shadow diff harness — no runtime feature flags (constitution: no runtime flags for capability rollout).

**Tech Stack:** .NET 10 / C# 14, NATS.Net 2.x (`NATS.Net` + `NATS.Extensions.Microsoft.DependencyInjection`), bundled `nats-server` binaries per-RID, OpenTelemetry via `FleetInstrumentation.Meter`, xUnit + Shouldly (project policy forbids mocking libraries; use hand-crafted fakes under `WeaveFleet.Testing`).

**Design Reference:** `docs/nats-event-substrate-design.md` is the source of truth for design rationale, subject scheme, and constitution alignment. This plan executes that design.

---

## Orientation

**Current state snapshot (verified):**

- `HarnessEventRelay` (`src/WeaveFleet.Infrastructure/Services/HarnessEventRelay.cs:19-286`) is a `BackgroundService` that starts one `PumpAsync` task per live `IHarnessSession`. The pump (lines 117-262):
  1. Resolves `fleetSessionId` from `ISessionRepository` with retry.
  2. Creates a per-pump scope that owns a `HarnessEventPersistenceService` instance.
  3. `await foreach` over `instance.SubscribeAsync(ct)`:
     - Calls `persistenceService.BufferTextDelta(...)` (line 179) for delta events.
     - Calls `persistenceService.TryHandleDurableEventAsync(...)` (line 184). Returns `true` for durable events (it writes both the DB row and an `OutboxMessage` via `SessionActivityWriteService`) ⇒ relay `continue`s.
     - For ephemeral events (classification `IsEphemeralRelay`), calls `_broadcaster.BroadcastAsync(session:{id}, type, payload, userId, ct)` (line 201).
     - Parses `activity_status` and broadcasts on `"sessions"` topic if present (lines 206-216).
  4. On pump exit: flushes buffered deltas, broadcasts idle (lines 227-262).
- `HarnessEventPersistenceService` (`src/WeaveFleet.Infrastructure/Services/HarnessEventPersistenceService.cs:17-560`) is `internal sealed`. Public surface used by the relay:
  - `TryHandleDurableEventAsync(string fleetSessionId, HarnessEvent evt) → Task<bool>`
  - `BufferTextDelta(string fleetSessionId, HarnessEvent evt) → void`
  - `FlushBufferedDeltasAsync(string fleetSessionId) → Task`

  It writes durable rows via `_messageRepository`/`_sessionRepository` AND writes `OutboxMessage` entries via `SessionActivityWriteService.WriteAsync(...)`. The outbox is dispatched by `OutboxDispatchBackgroundService` → `InProcessOutboxDispatcher` → `IEventBroadcaster.BroadcastAsync`. This plan's persistence cutover moves the *in-relay invocation* to a NATS projection; it does **not** remove the outbox (which still serves as a crash-recovery buffer for the broadcast fan-out of durable events). **After Phase 5** we revisit the outbox (see Task 5.5).

- `IEventBroadcaster` (`src/WeaveFleet.Application/Services/IEventBroadcaster.cs:7-25`) exposes two `BroadcastAsync` overloads and one `SubscribeAsync` returning `IAsyncEnumerable<BroadcastEvent>`. WebSocket endpoints consume via `SubscribeAsync` (`src/WeaveFleet.Api/Endpoints/WebSocketEndpoints.cs:212`).
- `EventTypeMetadata.Classify(string)` (`src/WeaveFleet.Domain/Harnesses/EventTypeMetadata.cs:31-150`) returns `EventClassification { IsDurable, IsEphemeralRelay, RequiresReasoningFilter, IsActivitySignal }`. Already used by the relay.
- `HarnessEvent` (`src/WeaveFleet.Domain/Harnesses/HarnessTypes.cs:35-42`) is the on-wire record. Stays unchanged.
- `FleetInstrumentation.Meter` (`src/WeaveFleet.Application/Diagnostics/FleetInstrumentation.cs`) service name `weave-fleet`, metric convention `weave_fleet.<category>.<metric>` with snake_case.
- DI bulk registration in `src/WeaveFleet.Infrastructure/DependencyInjection.cs:40-184` (`AddFleetInfrastructure`). Hosted services go via `AddHostedService<T>()` (e.g. line 127).
- `ApiWebApplicationFactory` is `sealed` (`tests/WeaveFleet.Api.Tests/Infrastructure/ApiWebApplicationFactory.cs:14`). Integration tests needing a custom host must subclass `WebApplicationFactory<Program>` directly.
- Test policy (`tests/Directory.Build.props`): Shouldly is globally imported; mocking libraries are banned; fakes live in `WeaveFleet.Testing`.
- `TargetFramework = net10.0`, `LangVersion = 14`, `TreatWarningsAsErrors = true`, `AnalysisLevel = latest-recommended` (`Directory.Build.props`).

**Out of scope (explicit):**

- `DelegationService` / `SessionOrchestrator` broadcasts remain on their existing direct `IEventBroadcaster` call path (design Decision 1).
- The existing outbox / `OutboxDispatchBackgroundService` stays in place throughout the plan. Retiring it is a follow-up (see Task 5.5).
- Multi-node deployment, per-tenant NATS accounts, archive projections — all future work.

---

## File Structure

### New files (Infrastructure)

- `src/WeaveFleet.Infrastructure/Nats/NatsServerHostedService.cs` — launches bundled `nats-server` subprocess, no-op when `Fleet:Nats:ExternalUrl` is set.
- `src/WeaveFleet.Infrastructure/Nats/NatsStreamInitializer.cs` — idempotent JetStream stream creation on startup.
- `src/WeaveFleet.Infrastructure/Nats/NatsEventPublisher.cs` — `IEventPublisher` implementation; routes by `EventTypeMetadata.Classify`; sets `Nats-Msg-Id` and headers; awaits `PubAck`.
- `src/WeaveFleet.Infrastructure/Nats/NatsNamingStrategy.cs` — subject/stream/consumer prefix helper (tenant/project/session resolution).
- `src/WeaveFleet.Infrastructure/Nats/NatsMetrics.cs` — OpenTelemetry metric + counter definitions.
- `src/WeaveFleet.Infrastructure/Nats/ProjectionListener.cs` — generic JetStream durable-consumer pump.
- `src/WeaveFleet.Infrastructure/Nats/ProjectionHostService.cs` — hosted service that spins up one `ProjectionListener` per registered projection.
- `src/WeaveFleet.Infrastructure/Nats/EmbeddedNatsServer/NatsServerBinaryResolver.cs` — picks the right bundled binary for the current RID.
- `src/WeaveFleet.Infrastructure/Nats/Configuration/NatsStreamBuilder.cs` — fluent DI surface for stream config + projection registration.
- `src/WeaveFleet.Infrastructure/Nats/Configuration/NatsServiceCollectionExtensions.cs` — `AddEventStore(...)` extension.
- `src/WeaveFleet.Infrastructure/Nats/EphemeralEventRelayService.cs` — core NATS subscriber; forwards ephemeral events to `IEventBroadcaster`.

### New files (Application)

- `src/WeaveFleet.Application/Events/IEventPublisher.cs` — publish abstraction.
- `src/WeaveFleet.Application/Projections/IProjection.cs` — `IProjection<T>` with `HandleAsync(T evt, ProjectionContext ctx, CancellationToken ct)`.
- `src/WeaveFleet.Application/Projections/ProjectionContext.cs` — parsed subject parts + headers handed to projections.
- `src/WeaveFleet.Application/Projections/MessagePersistenceProjection.cs` — delegates to `HarnessEventPersistenceService` for SQLite writes.
- `src/WeaveFleet.Application/Projections/WebSocketFanOutProjection.cs` — forwards durable events to `IEventBroadcaster`.
- `src/WeaveFleet.Application/Configuration/NatsOptions.cs` — `ExternalUrl`, `CredsFile`, data dir, retention defaults.

### Modified files

- `src/WeaveFleet.Application/Configuration/FleetOptions.cs` — add `public NatsOptions Nats { get; set; } = new();`.
- `src/WeaveFleet.Infrastructure/WeaveFleet.Infrastructure.csproj` — add `NATS.Net` + `NATS.Extensions.Microsoft.DependencyInjection` PackageReference; add `EmbeddedNatsServer\Binaries\*` as `<None>` with `CopyToOutputDirectory` per RID.
- `src/WeaveFleet.Infrastructure/Services/HarnessEventRelay.cs` — inject `IEventPublisher`; in the pump, call `_publisher.PublishAsync(evt, sessionContext, ct)` *in addition to* existing behaviour during Phase 3; remove in-relay persistence call in Phase 4; remove in-relay broadcaster call for ephemeral in Phase 5.
- `src/WeaveFleet.Infrastructure/Services/HarnessEventPersistenceService.cs` — relax `internal` to `public` (still `sealed`); expose the service so `MessagePersistenceProjection` (Application-layer) can call it. (Alternative: introduce a public `IHarnessEventPersister` interface — see Task 1.0c for the decision.)
- `src/WeaveFleet.Infrastructure/DependencyInjection.cs` — register `HarnessEventPersistenceService` as scoped service, register `IEventPublisher → NatsEventPublisher` singleton, register `IProjection` implementations, register new hosted services.
- `src/WeaveFleet.Api/Program.cs` — call `builder.Services.AddEventStore(nats => { ... })` inside `AddFleetInfrastructure` or alongside it; ensure `NatsServerHostedService` starts before `NatsStreamInitializer` which starts before `ProjectionHostService` / `EphemeralEventRelayService`.

### Test files (new)

- `tests/WeaveFleet.Infrastructure.Tests/Nats/NatsEventPublisherTests.cs`
- `tests/WeaveFleet.Infrastructure.Tests/Nats/NatsNamingStrategyTests.cs`
- `tests/WeaveFleet.Infrastructure.Tests/Nats/NatsStreamInitializerTests.cs`
- `tests/WeaveFleet.Infrastructure.Tests/Nats/ProjectionListenerTests.cs`
- `tests/WeaveFleet.Infrastructure.Tests/Nats/NatsServerHostedServiceTests.cs`
- `tests/WeaveFleet.Application.Tests/Projections/MessagePersistenceProjectionTests.cs`
- `tests/WeaveFleet.Application.Tests/Projections/WebSocketFanOutProjectionTests.cs`
- `tests/WeaveFleet.Api.Tests/Nats/NatsEventSubstrateEndToEndTests.cs`
- `tests/WeaveFleet.Api.Tests/Nats/ShadowDiffHarnessTests.cs`
- `tests/WeaveFleet.Testing/Nats/EmbeddedNatsTestFixture.cs` — shared xUnit fixture that launches embedded `nats-server` on a random port for integration tests.
- `tests/WeaveFleet.Testing/Nats/FakeEventPublisher.cs` — hand-crafted fake for unit tests that do not need a real broker.

---

## Phase 0 — Pre-flight

### Task 0.1: Baseline build & test state

**Files:** none modified.

- [ ] **Step 1: Run the full test suite and capture baseline.**

Run:
```bash
cd D:/personal/weave-fleet
dotnet test --nologo --no-restore 2>&1 | tee /tmp/baseline-tests.txt
```
Expected: `SkillEndpointPathTraversalTests.GetSkill_ReturnsBadRequestOrNotRouted_ForEncodedTraversal` may fail — **pre-existing**, per design doc's pre-flight checklist. Record the exact set of failing tests.

- [ ] **Step 2: Record the baseline.** Write the pre-existing failing test list to `.weave/plans/nats-event-substrate-baseline.md` so later regressions are distinguishable:

```markdown
# NATS Substrate — Pre-existing test baseline

Captured: {date}, commit {short-sha}
Running `dotnet test --nologo --no-restore` on main shows the following pre-existing failures, which are NOT regressions from this plan:

- {list verbatim test IDs from /tmp/baseline-tests.txt}
```

- [ ] **Step 3: Commit the baseline file.**

```bash
git add .weave/plans/nats-event-substrate-baseline.md
git commit -m "docs: capture pre-existing test baseline for nats substrate plan"
```

### Task 0.2: Dependency audit — post-event WS → SQLite paths

**Files:** none modified; produces an audit record.

Design Decision: "WS may arrive before SQLite is updated — and that is acceptable." Must be verified before Phase 4.

- [ ] **Step 1: Enumerate every durable-event consumer on the client side.** Grep the frontend for WebSocket event handlers:

```bash
Grep pattern: "message.updated|message.created|message.part.updated|session.updated|session.error|session.compacted|session.deleted|message.removed|message.part.removed" glob: "frontend/**/*.{ts,tsx,vue,js}"
```

For each match, inspect whether the handler subsequently fetches from the backend (via SQLite-backed endpoint) and assumes the row exists. Record findings.

- [ ] **Step 2: Enumerate every backend post-event read path.** Grep backend for `refetch`/`reload` triggered by event types:

```bash
Grep pattern: "session:.*|activity_status" glob: "src/**/*.cs"
```

- [ ] **Step 3: Write the audit.** Create `.weave/learnings/nats-substrate-dependency-audit.md` listing every path and its conclusion: "tolerant of missing row" or "requires row" (and the fix needed).

```markdown
# NATS Substrate — Dependency Audit (Phase 4 pre-req)

**Summary:** After cutover, `MessagePersistenceProjection` and `WebSocketFanOutProjection` are independent JetStream consumers. The WebSocket broadcast may arrive before the SQLite row is written. Every post-event read path must tolerate the "row not yet present" case via retry or payload-only reads.

## Frontend handlers

| File:line | Event type | Reads SQLite? | Tolerant of missing row? | Fix if needed |
| --------- | ---------- | ------------- | ------------------------ | ------------- |
| ... | ... | ... | ... | ... |

## Backend handlers

| File:line | Event type | Reads SQLite? | Tolerant of missing row? | Fix if needed |
| --------- | ---------- | ------------- | ------------------------ | ------------- |
| ... | ... | ... | ... | ... |

**Blocking any non-tolerant path is a Phase 4 pre-requisite.** Either make the consumer payload-only, or add bounded-retry refetch (poll-until-present with a small budget).
```

- [ ] **Step 4: Commit the audit.**

```bash
git add .weave/learnings/nats-substrate-dependency-audit.md
git commit -m "docs: dependency audit for nats substrate persistence cutover"
```

### Task 0.3: Concurrency audit of HarnessEventPersistenceService

**Files:** none modified; produces an audit record appended to Task 0.2's file.

Today one `HarnessEventPersistenceService` instance lives per relay pump — per-session state is implicitly serialized. After Phase 4 one instance services all sessions via the projection callback.

- [ ] **Step 1: Enumerate instance-scoped state.** Inspect `HarnessEventPersistenceService` fields (`src/WeaveFleet.Infrastructure/Services/HarnessEventPersistenceService.cs:19-23`):
  - `_messageRepository`, `_sessionRepository`, `_sessionActivityWriteService`, `_ownerUserId` — currently one-per-pump.
  - `_bufferedTextDeltas: ConcurrentDictionary<(string MessageKey, string PartId), string>` — already keyed by session-scoped `MessageKey`, so cross-session safe *in the ConcurrentDictionary sense*, but the design keeps the buffer in the relay regardless (Decision 4).

- [ ] **Step 2: Document the change.** Append to `.weave/learnings/nats-substrate-dependency-audit.md`:

```markdown
## Concurrency — HarnessEventPersistenceService after Phase 4

- Instance fields `_messageRepository`, `_sessionRepository`, `_sessionActivityWriteService`: repository calls take `fleetSessionId` as an arg; no instance-held per-session state.
- `_ownerUserId`: today this is the pump's session owner. After cutover, this field is no longer safe as a singleton — the owner varies per event. **Action:** remove the field and resolve the owner per call from the session (see Task 4.0a).
- `_bufferedTextDeltas`: stays in the relay per design Decision 4. Not moved.

Projection-side serialization requirement: `MessagePersistenceProjection` must deliver events to the service in per-session order. A single `ProjectionListener` with `AckPolicy.Explicit` and serial-per-session consumer receives events in publish order (per-session publish is serial, design Decision 5). If we ever parallelize (partitioned-by-session-hash workers, design Decision 8), each worker must own a disjoint session set.
```

- [ ] **Step 3: Commit.**

```bash
git add .weave/learnings/nats-substrate-dependency-audit.md
git commit -m "docs: concurrency audit for persistence service cutover"
```

---

## Phase 1 — NATS plumbing and embedded server

Goal: Fleet can launch `nats-server`, create the JetStream stream, and publish events via `IEventPublisher`. No relay changes; no consumers yet. Verified by an integration test that publishes synthetic events and asserts stream contents + metrics.

### Task 1.0a: Add NATS NuGet packages

**Files:**
- Modify: `src/WeaveFleet.Infrastructure/WeaveFleet.Infrastructure.csproj`

- [ ] **Step 1: Add PackageReferences.**

Edit `src/WeaveFleet.Infrastructure/WeaveFleet.Infrastructure.csproj` — add a new `<ItemGroup>`:

```xml
  <ItemGroup>
    <PackageReference Include="NATS.Net" Version="2.5.*" />
    <PackageReference Include="NATS.Extensions.Microsoft.DependencyInjection" Version="2.5.*" />
  </ItemGroup>
```

If the repo pins package versions centrally (check for `Directory.Packages.props`), add them there instead and use `<PackageReference Include="NATS.Net" />` without a `Version`.

- [ ] **Step 2: Restore and compile.**

```bash
dotnet restore
dotnet build --nologo
```
Expected: build succeeds, no new warnings.

- [ ] **Step 3: Commit.**

```bash
git add src/WeaveFleet.Infrastructure/WeaveFleet.Infrastructure.csproj Directory.Packages.props 2>/dev/null
git commit -m "build: add NATS.Net client packages"
```

### Task 1.0b: Bundle `nats-server` binaries

**Files:**
- Create directory: `src/WeaveFleet.Infrastructure/Nats/EmbeddedNatsServer/Binaries/{rid}/nats-server[.exe]`
- Modify: `src/WeaveFleet.Infrastructure/WeaveFleet.Infrastructure.csproj`

- [ ] **Step 1: Download `nats-server` binaries per RID.** From <https://github.com/nats-io/nats-server/releases> pick a recent LTS (e.g. v2.11.x). Required RIDs for first release: `win-x64`, `linux-x64`, `osx-x64`, `osx-arm64`. Place them at:

```
src/WeaveFleet.Infrastructure/Nats/EmbeddedNatsServer/Binaries/win-x64/nats-server.exe
src/WeaveFleet.Infrastructure/Nats/EmbeddedNatsServer/Binaries/linux-x64/nats-server
src/WeaveFleet.Infrastructure/Nats/EmbeddedNatsServer/Binaries/osx-x64/nats-server
src/WeaveFleet.Infrastructure/Nats/EmbeddedNatsServer/Binaries/osx-arm64/nats-server
```

Licence: Apache-2.0, redistribution is fine. Set Unix executable bits where applicable:

```bash
chmod +x src/WeaveFleet.Infrastructure/Nats/EmbeddedNatsServer/Binaries/linux-x64/nats-server
chmod +x src/WeaveFleet.Infrastructure/Nats/EmbeddedNatsServer/Binaries/osx-x64/nats-server
chmod +x src/WeaveFleet.Infrastructure/Nats/EmbeddedNatsServer/Binaries/osx-arm64/nats-server
```

- [ ] **Step 2: Wire binaries into the build.**

Edit `src/WeaveFleet.Infrastructure/WeaveFleet.Infrastructure.csproj` — add:

```xml
  <ItemGroup>
    <None Include="Nats\EmbeddedNatsServer\Binaries\**\*" CopyToOutputDirectory="PreserveNewest" />
  </ItemGroup>
```

- [ ] **Step 3: Verify at build time.**

```bash
dotnet build --nologo
ls src/WeaveFleet.Api/bin/Debug/net10.0/Nats/EmbeddedNatsServer/Binaries/
```
Expected: binaries present under the API's output directory.

- [ ] **Step 4: Commit (binary files + csproj).**

```bash
git add src/WeaveFleet.Infrastructure/Nats/EmbeddedNatsServer/Binaries src/WeaveFleet.Infrastructure/WeaveFleet.Infrastructure.csproj
git commit -m "build: bundle nats-server binaries per RID"
```

### Task 1.0c: Expose `HarnessEventPersistenceService` for projection use

**Files:**
- Modify: `src/WeaveFleet.Infrastructure/Services/HarnessEventPersistenceService.cs`
- Modify: `src/WeaveFleet.Infrastructure/DependencyInjection.cs`

**Decision:** The projection will be in `WeaveFleet.Application` and must call the service. Two options: (a) introduce `IHarnessEventPersister` in Application and implement it in Infrastructure, (b) lift the class to `public` and have the projection depend on the infrastructure type. The codebase already exposes several infrastructure types via DI to Application (`InMemoryEventBroadcaster`, `SessionActivityWriteService`). Prefer **(a)** for clean-layering (constitution: "No layer reaches down"). The interface lives in Application, the class stays in Infrastructure.

- [ ] **Step 1: Create the interface.**

Create `src/WeaveFleet.Application/Services/IHarnessEventPersister.cs`:

```csharp
using WeaveFleet.Domain.Harnesses;

namespace WeaveFleet.Application.Services;

/// <summary>
/// Handles durable persistence of harness events (message lifecycle, session lifecycle).
/// The Infrastructure implementation writes SQLite rows and emits outbox events.
/// </summary>
public interface IHarnessEventPersister
{
    /// <summary>
    /// Persist a durable harness event. Caller must ensure <paramref name="fleetSessionId"/> is
    /// the resolved Fleet session id for <paramref name="evt"/>.
    /// </summary>
    Task HandleAsync(string fleetSessionId, string ownerUserId, HarnessEvent evt, CancellationToken ct);
}
```

- [ ] **Step 2: Relax visibility + add `IHarnessEventPersister` implementation.**

Edit `src/WeaveFleet.Infrastructure/Services/HarnessEventPersistenceService.cs`:

- Change line 17: `internal sealed class HarnessEventPersistenceService` → `public sealed class HarnessEventPersistenceService : IHarnessEventPersister`.
- Change the `internal` constructor signature (line 25) to `public`.
- Add `using WeaveFleet.Application.Services;` at the top.
- **Remove the `_ownerUserId` field** (line 22) and drop it from the constructor — per Task 0.3, it is no longer a singleton-scope property. All private helpers that reference `_ownerUserId` must accept an `ownerUserId` parameter instead.
- Add a new public method that implements the interface:

```csharp
    public async Task HandleAsync(string fleetSessionId, string ownerUserId, HarnessEvent evt, CancellationToken ct)
    {
        // The existing TryHandleDurableEventAsync already routes by event type.
        // Keep the legacy internal method for backward compat during Phase 3 (dual-write).
        await TryHandleDurableEventAsync(fleetSessionId, ownerUserId, evt).ConfigureAwait(false);
    }
```

- Rewrite each internal `Try…` method to accept `string ownerUserId` rather than reading from `_ownerUserId`. Propagate through `BackgroundUserContext.BeginScope(ownerUserId)` calls.
- Update `TryHandleDurableEventAsync(string fleetSessionId, HarnessEvent evt)` → `TryHandleDurableEventAsync(string fleetSessionId, string ownerUserId, HarnessEvent evt)`.

- [ ] **Step 3: Update `HarnessEventRelay` call sites.**

Edit `src/WeaveFleet.Infrastructure/Services/HarnessEventRelay.cs` around lines 157-184:

- Line 163-167: Drop the `ownerUserId` constructor arg:
  ```csharp
  persistenceService = new HarnessEventPersistenceService(
      messageRepo,
      sessionRepo,
      activityWriteService);
  ```
- Line 184: `await persistenceService.TryHandleDurableEventAsync(targetFleetSessionId, sessionUserId, evt)`.

- [ ] **Step 4: Register the persister in DI.**

Edit `src/WeaveFleet.Infrastructure/DependencyInjection.cs` after line 122:

```csharp
        // Harness event persister — scoped so message/session repositories flow through correctly.
        services.AddScoped<HarnessEventPersistenceService>();
        services.AddScoped<IHarnessEventPersister>(sp => sp.GetRequiredService<HarnessEventPersistenceService>());
```

- [ ] **Step 5: Build and run existing tests.**

```bash
dotnet build --nologo
dotnet test tests/WeaveFleet.Infrastructure.Tests --nologo
dotnet test tests/WeaveFleet.Application.Tests --nologo
```
Expected: all previously-passing tests still pass.

- [ ] **Step 6: Commit.**

```bash
git add src/WeaveFleet.Application/Services/IHarnessEventPersister.cs src/WeaveFleet.Infrastructure/Services/HarnessEventPersistenceService.cs src/WeaveFleet.Infrastructure/Services/HarnessEventRelay.cs src/WeaveFleet.Infrastructure/DependencyInjection.cs
git commit -m "refactor: expose HarnessEventPersistenceService via IHarnessEventPersister for projection use"
```

### Task 1.1: Add `NatsOptions` to configuration

**Files:**
- Create: `src/WeaveFleet.Application/Configuration/NatsOptions.cs`
- Modify: `src/WeaveFleet.Application/Configuration/FleetOptions.cs`

- [ ] **Step 1: Write the failing test.**

Create `tests/WeaveFleet.Application.Tests/Configuration/NatsOptionsTests.cs`:

```csharp
using WeaveFleet.Application.Configuration;

namespace WeaveFleet.Application.Tests.Configuration;

public sealed class NatsOptionsTests
{
    [Fact]
    public void Defaults_matchLocalDevMode()
    {
        var options = new NatsOptions();

        options.ExternalUrl.ShouldBeNull();
        options.CredsFile.ShouldBeNull();
        options.DataDirectory.ShouldBe("./data/nats");
        options.StreamName.ShouldBe("fleet-sessions");
        options.MaxAge.ShouldBe(TimeSpan.FromHours(24));
        options.MaxBytes.ShouldBe(-1);
        options.TenantPrefix.ShouldBe("tenant.default");
    }

    [Fact]
    public void FleetOptions_exposesNatsSection()
    {
        var options = new FleetOptions();

        options.Nats.ShouldNotBeNull();
        options.Nats.StreamName.ShouldBe("fleet-sessions");
    }
}
```

- [ ] **Step 2: Run the failing test.**

```bash
dotnet test tests/WeaveFleet.Application.Tests --nologo --filter FullyQualifiedName~NatsOptionsTests
```
Expected: FAIL — `NatsOptions` type does not exist.

- [ ] **Step 3: Create `NatsOptions`.**

Create `src/WeaveFleet.Application/Configuration/NatsOptions.cs`:

```csharp
namespace WeaveFleet.Application.Configuration;

/// <summary>
/// NATS event-substrate configuration. See docs/nats-event-substrate-design.md.
/// </summary>
public sealed class NatsOptions
{
    /// <summary>URL of an external NATS broker. When null, Fleet launches its bundled nats-server.</summary>
    public string? ExternalUrl { get; set; }

    /// <summary>Path to a NATS creds file. Only honoured when <see cref="ExternalUrl"/> is set.</summary>
    public string? CredsFile { get; set; }

    /// <summary>Directory for embedded nats-server JetStream file storage. Default: ./data/nats.</summary>
    public string DataDirectory { get; set; } = "./data/nats";

    /// <summary>Durable JetStream stream name. Default: fleet-sessions.</summary>
    public string StreamName { get; set; } = "fleet-sessions";

    /// <summary>MaxAge safety net for stream retention. Default: 24h (local dev).</summary>
    public TimeSpan MaxAge { get; set; } = TimeSpan.FromHours(24);

    /// <summary>MaxBytes for the durable stream. -1 = unlimited.</summary>
    public long MaxBytes { get; set; } = -1;

    /// <summary>Tenant/prefix string for subject construction. Default: tenant.default.</summary>
    public string TenantPrefix { get; set; } = "tenant.default";
}
```

- [ ] **Step 4: Expose it from `FleetOptions`.**

Edit `src/WeaveFleet.Application/Configuration/FleetOptions.cs`. After line 100 (the `public OutboxOptions Outbox` property), add:

```csharp
    /// <summary>NATS event substrate configuration.</summary>
    public NatsOptions Nats { get; set; } = new();
```

- [ ] **Step 5: Run tests.**

```bash
dotnet test tests/WeaveFleet.Application.Tests --nologo --filter FullyQualifiedName~NatsOptionsTests
```
Expected: PASS.

- [ ] **Step 6: Commit.**

```bash
git add src/WeaveFleet.Application/Configuration/NatsOptions.cs src/WeaveFleet.Application/Configuration/FleetOptions.cs tests/WeaveFleet.Application.Tests/Configuration/NatsOptionsTests.cs
git commit -m "feat: add NatsOptions configuration"
```

### Task 1.2: Create `NatsMetrics`

**Files:**
- Create: `src/WeaveFleet.Infrastructure/Nats/NatsMetrics.cs`

Metric names follow the existing `weave_fleet.<category>.<metric>` convention (see `FleetInstrumentation.cs`).

- [ ] **Step 1: Write the failing test.**

Create `tests/WeaveFleet.Infrastructure.Tests/Nats/NatsMetricsTests.cs`:

```csharp
using System.Diagnostics.Metrics;
using WeaveFleet.Application.Diagnostics;
using WeaveFleet.Infrastructure.Nats;

namespace WeaveFleet.Infrastructure.Tests.Nats;

public sealed class NatsMetricsTests
{
    [Fact]
    public void PublishCount_increments_withRoutingTag()
    {
        using var listener = new MeterListener();
        var recorded = new List<(string Name, long Value, IDictionary<string, object?> Tags)>();
        listener.InstrumentPublished = (inst, l) =>
        {
            if (inst.Meter == FleetInstrumentation.Meter) l.EnableMeasurementEvents(inst);
        };
        listener.SetMeasurementEventCallback<long>((inst, value, tags, _) =>
        {
            var dict = new Dictionary<string, object?>();
            foreach (var kv in tags) dict[kv.Key] = kv.Value;
            recorded.Add((inst.Name, value, dict));
        });
        listener.Start();

        var metrics = new NatsMetrics();
        metrics.RecordPublish(routing: "durable", eventType: "message.created", tenant: "tenant.default", result: "ok");

        recorded.ShouldContain(r => r.Name == "weave_fleet.nats.publish.count"
            && (string)r.Tags["routing"]! == "durable"
            && (string)r.Tags["event_type"]! == "message.created"
            && (string)r.Tags["result"]! == "ok");
    }
}
```

- [ ] **Step 2: Run the failing test.**

```bash
dotnet test tests/WeaveFleet.Infrastructure.Tests --nologo --filter FullyQualifiedName~NatsMetricsTests
```
Expected: FAIL — `NatsMetrics` type does not exist.

- [ ] **Step 3: Implement `NatsMetrics`.**

Create `src/WeaveFleet.Infrastructure/Nats/NatsMetrics.cs`:

```csharp
using System.Diagnostics.Metrics;
using WeaveFleet.Application.Diagnostics;

namespace WeaveFleet.Infrastructure.Nats;

/// <summary>
/// OpenTelemetry metric surface for the NATS event substrate. Uses the existing
/// FleetInstrumentation.Meter so exporters that already ship metrics do not need reconfiguration.
/// </summary>
public sealed class NatsMetrics
{
    private readonly Counter<long> _publishCount;
    private readonly Histogram<double> _publishDuration;
    private readonly Counter<long> _projectionAckCount;
    private readonly Histogram<double> _projectionHandlerDuration;
    private readonly Counter<long> _reconnectCount;

    public NatsMetrics()
    {
        var meter = FleetInstrumentation.Meter;
        _publishCount = meter.CreateCounter<long>(
            "weave_fleet.nats.publish.count", unit: "events", description: "NATS publish attempts.");
        _publishDuration = meter.CreateHistogram<double>(
            "weave_fleet.nats.publish.duration", unit: "ms", description: "Time from publish to PubAck (durable path only).");
        _projectionAckCount = meter.CreateCounter<long>(
            "weave_fleet.nats.projection.ack.count", unit: "acks", description: "Projection ack/nak/term outcomes.");
        _projectionHandlerDuration = meter.CreateHistogram<double>(
            "weave_fleet.nats.projection.handler.duration", unit: "ms", description: "Projection handler duration.");
        _reconnectCount = meter.CreateCounter<long>(
            "weave_fleet.nats.reconnect.count", unit: "reconnects", description: "NATS client reconnects.");
    }

    public void RecordPublish(string routing, string eventType, string tenant, string result)
        => _publishCount.Add(1,
            new KeyValuePair<string, object?>("routing", routing),
            new KeyValuePair<string, object?>("event_type", eventType),
            new KeyValuePair<string, object?>("tenant", tenant),
            new KeyValuePair<string, object?>("result", result));

    public void RecordPublishDuration(double milliseconds, string eventType, string tenant, string result)
        => _publishDuration.Record(milliseconds,
            new KeyValuePair<string, object?>("event_type", eventType),
            new KeyValuePair<string, object?>("tenant", tenant),
            new KeyValuePair<string, object?>("result", result));

    public void RecordProjectionAck(string projection, string result)
        => _projectionAckCount.Add(1,
            new KeyValuePair<string, object?>("projection", projection),
            new KeyValuePair<string, object?>("result", result));

    public void RecordProjectionHandler(double milliseconds, string projection, string eventType, string result)
        => _projectionHandlerDuration.Record(milliseconds,
            new KeyValuePair<string, object?>("projection", projection),
            new KeyValuePair<string, object?>("event_type", eventType),
            new KeyValuePair<string, object?>("result", result));

    public void RecordReconnect() => _reconnectCount.Add(1);
}
```

- [ ] **Step 4: Run the test.**

```bash
dotnet test tests/WeaveFleet.Infrastructure.Tests --nologo --filter FullyQualifiedName~NatsMetricsTests
```
Expected: PASS.

- [ ] **Step 5: Commit.**

```bash
git add src/WeaveFleet.Infrastructure/Nats/NatsMetrics.cs tests/WeaveFleet.Infrastructure.Tests/Nats/NatsMetricsTests.cs
git commit -m "feat: add NatsMetrics with weave_fleet.nats.* counters and histograms"
```

### Task 1.3: Create `NatsNamingStrategy`

**Files:**
- Create: `src/WeaveFleet.Infrastructure/Nats/NatsNamingStrategy.cs`
- Create: `tests/WeaveFleet.Infrastructure.Tests/Nats/NatsNamingStrategyTests.cs`

- [ ] **Step 1: Write the failing tests.**

Create `tests/WeaveFleet.Infrastructure.Tests/Nats/NatsNamingStrategyTests.cs`:

```csharp
using WeaveFleet.Application.Configuration;
using WeaveFleet.Infrastructure.Nats;

namespace WeaveFleet.Infrastructure.Tests.Nats;

public sealed class NatsNamingStrategyTests
{
    private readonly NatsNamingStrategy _sut = new(new NatsOptions());

    [Fact]
    public void DurableSubject_includesTenantProjectSessionAndType()
    {
        var subject = _sut.DurableSubject(projectId: "proj-1", sessionId: "sess-1", eventType: "message.created");
        subject.ShouldBe("tenant.default.project.proj-1.session.sess-1.message.created");
    }

    [Fact]
    public void EphemeralSubject_usesLiveSegment()
    {
        var subject = _sut.EphemeralSubject(projectId: "proj-1", sessionId: "sess-1", eventType: "message.part.delta");
        subject.ShouldBe("tenant.default.project.proj-1.live.sess-1.message.part.delta");
    }

    [Fact]
    public void ScratchSentinel_appliedForMissingProjectId()
    {
        var subject = _sut.DurableSubject(projectId: null, sessionId: "sess-1", eventType: "message.updated");
        subject.ShouldBe("tenant.default.project.scratch.session.sess-1.message.updated");
    }

    [Fact]
    public void DurableStreamFilter_coversAllSessions()
    {
        _sut.DurableStreamFilter.ShouldBe("tenant.*.project.*.session.*.>");
    }

    [Fact]
    public void EphemeralSubscriptionFilter_coversAllLiveSubjects()
    {
        _sut.EphemeralSubscriptionFilter.ShouldBe("tenant.*.project.*.live.*.>");
    }

    [Fact]
    public void ParseDurableSubject_extractsTenantProjectSessionAndEventType()
    {
        var parsed = NatsNamingStrategy.ParseDurableSubject("tenant.default.project.proj-1.session.sess-1.message.part.updated");
        parsed.ShouldNotBeNull();
        parsed.Value.Tenant.ShouldBe("default");
        parsed.Value.ProjectId.ShouldBe("proj-1");
        parsed.Value.SessionId.ShouldBe("sess-1");
        parsed.Value.EventType.ShouldBe("message.part.updated");
    }

    [Fact]
    public void ParseDurableSubject_returnsNullForMalformedInput()
    {
        NatsNamingStrategy.ParseDurableSubject("garbage.subject").ShouldBeNull();
    }
}
```

- [ ] **Step 2: Run the failing tests.**

```bash
dotnet test tests/WeaveFleet.Infrastructure.Tests --nologo --filter FullyQualifiedName~NatsNamingStrategyTests
```
Expected: FAIL — type missing.

- [ ] **Step 3: Implement `NatsNamingStrategy`.**

Create `src/WeaveFleet.Infrastructure/Nats/NatsNamingStrategy.cs`:

```csharp
using WeaveFleet.Application.Configuration;

namespace WeaveFleet.Infrastructure.Nats;

/// <summary>
/// Subject / stream / consumer name construction for the NATS event substrate.
/// Keeps tenant/project/session hierarchy out of every other component.
/// </summary>
public sealed class NatsNamingStrategy
{
    public const string ScratchProjectSentinel = "scratch";

    private readonly NatsOptions _options;

    public NatsNamingStrategy(NatsOptions options) => _options = options;

    public string DurableSubject(string? projectId, string sessionId, string eventType)
        => $"{_options.TenantPrefix}.project.{projectId ?? ScratchProjectSentinel}.session.{sessionId}.{eventType}";

    public string EphemeralSubject(string? projectId, string sessionId, string eventType)
        => $"{_options.TenantPrefix}.project.{projectId ?? ScratchProjectSentinel}.live.{sessionId}.{eventType}";

    public string DurableStreamFilter => "tenant.*.project.*.session.*.>";
    public string EphemeralSubscriptionFilter => "tenant.*.project.*.live.*.>";

    public string StreamName => _options.StreamName;

    public string DurableConsumerName(string projection) => $"{_options.StreamName}-{projection}";

    public readonly record struct ParsedDurableSubject(string Tenant, string ProjectId, string SessionId, string EventType);

    /// <summary>
    /// Parse `tenant.{ws}.project.{pid}.session.{sid}.{evt.Type}` back to its parts.
    /// The event type is the remainder after the "session.{sid}." segment (may contain dots).
    /// Returns null for malformed input.
    /// </summary>
    public static ParsedDurableSubject? ParseDurableSubject(string subject)
    {
        var parts = subject.Split('.');
        // Minimum: tenant.{ws}.project.{pid}.session.{sid}.{type} = 7 tokens
        if (parts.Length < 7) return null;
        if (parts[0] != "tenant" || parts[2] != "project" || parts[4] != "session") return null;
        var type = string.Join('.', parts[6..]);
        return new ParsedDurableSubject(parts[1], parts[3], parts[5], type);
    }
}
```

- [ ] **Step 4: Run the tests.**

```bash
dotnet test tests/WeaveFleet.Infrastructure.Tests --nologo --filter FullyQualifiedName~NatsNamingStrategyTests
```
Expected: PASS.

- [ ] **Step 5: Commit.**

```bash
git add src/WeaveFleet.Infrastructure/Nats/NatsNamingStrategy.cs tests/WeaveFleet.Infrastructure.Tests/Nats/NatsNamingStrategyTests.cs
git commit -m "feat: add NatsNamingStrategy for subject/stream/consumer naming"
```

### Task 1.4: Define `IEventPublisher` and publish context types

**Files:**
- Create: `src/WeaveFleet.Application/Events/IEventPublisher.cs`
- Create: `src/WeaveFleet.Application/Events/EventPublishContext.cs`

- [ ] **Step 1: Create `EventPublishContext`.**

Create `src/WeaveFleet.Application/Events/EventPublishContext.cs`:

```csharp
namespace WeaveFleet.Application.Events;

/// <summary>
/// Per-event context required by <see cref="IEventPublisher"/> to construct subjects and headers.
/// Populated by the caller (typically <c>HarnessEventRelay</c>) from repository data.
/// </summary>
public readonly record struct EventPublishContext(
    string FleetSessionId,
    string? ProjectId,
    string? UserId,
    string? HarnessType);
```

- [ ] **Step 2: Create `IEventPublisher`.**

Create `src/WeaveFleet.Application/Events/IEventPublisher.cs`:

```csharp
using WeaveFleet.Domain.Harnesses;

namespace WeaveFleet.Application.Events;

/// <summary>
/// Publishes HarnessEvents to the event substrate. Implementations route durable and ephemeral
/// events to the appropriate transport (JetStream vs core NATS today).
/// </summary>
public interface IEventPublisher
{
    /// <summary>
    /// Publish a single event. For durable events the call completes once the broker has
    /// acknowledged receipt; for ephemeral events it completes when the publish has been
    /// handed to the client library. Caller-side ordering is preserved when called serially.
    /// </summary>
    Task PublishAsync(HarnessEvent evt, EventPublishContext context, CancellationToken ct);
}
```

- [ ] **Step 3: Build & commit.**

```bash
dotnet build --nologo
git add src/WeaveFleet.Application/Events
git commit -m "feat: add IEventPublisher and EventPublishContext abstractions"
```

### Task 1.5: Implement `NatsEventPublisher`

**Files:**
- Create: `src/WeaveFleet.Infrastructure/Nats/NatsEventPublisher.cs`
- Create: `tests/WeaveFleet.Infrastructure.Tests/Nats/NatsEventPublisherTests.cs`

The implementation routes by `EventTypeMetadata.Classify`, constructs subjects via `NatsNamingStrategy`, sets `Nats-Msg-Id` on durable publishes, awaits `PubAck`, and records metrics.

- [ ] **Step 1: Write the failing test using `EmbeddedNatsTestFixture`.**

First, create the shared fixture. Create `tests/WeaveFleet.Testing/Nats/EmbeddedNatsTestFixture.cs`:

```csharp
using System.Diagnostics;
using System.Net.Sockets;
using NATS.Client.Core;

namespace WeaveFleet.Testing.Nats;

/// <summary>
/// xUnit fixture that launches a bundled nats-server on a random loopback port with JetStream
/// enabled in a temporary storage directory. Shared across a test class so setup cost is paid once.
/// </summary>
public sealed class EmbeddedNatsTestFixture : IAsyncLifetime
{
    public string Url { get; private set; } = "";
    private Process? _process;
    private string? _storageDir;

    public async ValueTask InitializeAsync()
    {
        var port = GetFreePort();
        _storageDir = Path.Combine(Path.GetTempPath(), $"nats-test-{Guid.NewGuid():N}");
        Directory.CreateDirectory(_storageDir);
        var binary = FindBinary();
        _process = Process.Start(new ProcessStartInfo
        {
            FileName = binary,
            Arguments = $"-p {port} -js -sd \"{_storageDir}\" --cluster_name test",
            RedirectStandardError = true,
            RedirectStandardOutput = true,
            UseShellExecute = false,
        }) ?? throw new InvalidOperationException("Failed to start nats-server");

        Url = $"nats://127.0.0.1:{port}";

        // Wait for the server to accept TCP connections
        for (int i = 0; i < 100; i++)
        {
            try
            {
                using var tcp = new TcpClient();
                await tcp.ConnectAsync("127.0.0.1", port);
                return;
            }
            catch { await Task.Delay(100); }
        }
        throw new TimeoutException($"nats-server on port {port} did not become ready.");
    }

    public async ValueTask DisposeAsync()
    {
        try { _process?.Kill(entireProcessTree: true); } catch { }
        try { _process?.WaitForExit(5000); } catch { }
        _process?.Dispose();
        if (_storageDir is not null && Directory.Exists(_storageDir))
        {
            try { Directory.Delete(_storageDir, recursive: true); } catch { }
        }
        await ValueTask.CompletedTask;
    }

    private static int GetFreePort()
    {
        using var socket = new TcpListener(System.Net.IPAddress.Loopback, 0);
        socket.Start();
        var port = ((System.Net.IPEndPoint)socket.LocalEndpoint).Port;
        socket.Stop();
        return port;
    }

    private static string FindBinary()
    {
        // Located relative to the test's bin output via the WeaveFleet.Infrastructure copy.
        var rid = System.Runtime.InteropServices.RuntimeInformation.RuntimeIdentifier;
        // Normalize platform RID (e.g., "win10-x64" -> "win-x64")
        rid = rid.StartsWith("win") ? "win-x64"
            : rid.StartsWith("osx") ? (System.Runtime.InteropServices.RuntimeInformation.OSArchitecture.ToString() == "Arm64" ? "osx-arm64" : "osx-x64")
            : "linux-x64";
        var exe = rid.StartsWith("win") ? "nats-server.exe" : "nats-server";
        var candidate = Path.Combine(AppContext.BaseDirectory, "Nats", "EmbeddedNatsServer", "Binaries", rid, exe);
        if (!File.Exists(candidate))
            throw new FileNotFoundException($"Bundled nats-server binary not found for RID '{rid}' at {candidate}. Ensure WeaveFleet.Infrastructure is referenced by this test project.");
        return candidate;
    }
}
```

Add `<ProjectReference Include="..\..\src\WeaveFleet.Infrastructure\WeaveFleet.Infrastructure.csproj" />` to `tests/WeaveFleet.Testing/WeaveFleet.Testing.csproj` if not already present, and add `<PackageReference Include="NATS.Net" />`.

Now create the test. Create `tests/WeaveFleet.Infrastructure.Tests/Nats/NatsEventPublisherTests.cs`:

```csharp
using System.Text.Json;
using NATS.Client.Core;
using NATS.Client.JetStream;
using NATS.Client.JetStream.Models;
using WeaveFleet.Application.Configuration;
using WeaveFleet.Application.Events;
using WeaveFleet.Domain.Harnesses;
using WeaveFleet.Infrastructure.Nats;
using WeaveFleet.Testing.Nats;

namespace WeaveFleet.Infrastructure.Tests.Nats;

public sealed class NatsEventPublisherTests(EmbeddedNatsTestFixture fixture)
    : IClassFixture<EmbeddedNatsTestFixture>
{
    private readonly EmbeddedNatsTestFixture _fixture = fixture;

    [Fact]
    public async Task DurableEvent_publishesToJetStreamWithMsgIdHeader()
    {
        var options = new NatsOptions();
        await using var connection = new NatsConnection(new NatsOpts { Url = _fixture.Url });
        var js = new NatsJSContext(connection);

        // Create the stream ahead of publish
        await js.CreateStreamAsync(new StreamConfig("fleet-sessions", ["tenant.*.project.*.session.*.>"]));

        var publisher = new NatsEventPublisher(
            js, connection, new NatsNamingStrategy(options), new NatsMetrics(), options);

        var evt = new HarnessEvent
        {
            Type = EventTypes.MessageCreated,
            SessionId = "sess-1",
            Timestamp = DateTimeOffset.UtcNow,
            Payload = JsonSerializer.SerializeToElement(new { info = new { role = "assistant" } })
        };

        await publisher.PublishAsync(
            evt,
            new EventPublishContext("sess-1", ProjectId: "proj-1", UserId: "user-1", HarnessType: "opencode"),
            CancellationToken.None);

        var stream = await js.GetStreamAsync("fleet-sessions");
        (await stream.GetInfoAsync()).State.Messages.ShouldBe(1UL);

        // Verify message id header is present
        var consumer = await stream.CreateOrderedConsumerAsync();
        await foreach (var msg in consumer.ConsumeAsync<byte[]>().Take(1))
        {
            msg.Headers.ShouldNotBeNull();
            msg.Headers!["Nats-Msg-Id"].ToString().ShouldBe("sess-1:" + msg.Metadata!.Value.Sequence.Stream);
            msg.Headers["x-fleet-user-id"].ToString().ShouldBe("user-1");
            msg.Headers["x-fleet-harness-type"].ToString().ShouldBe("opencode");
            msg.Subject.ShouldBe("tenant.default.project.proj-1.session.sess-1.message.created");
            break;
        }
    }

    [Fact]
    public async Task EphemeralEvent_publishesToCoreNats()
    {
        var options = new NatsOptions();
        await using var connection = new NatsConnection(new NatsOpts { Url = _fixture.Url });
        var publisher = new NatsEventPublisher(
            new NatsJSContext(connection), connection, new NatsNamingStrategy(options), new NatsMetrics(), options);

        // Subscribe first to catch the ephemeral publish
        var task = Task.Run(async () =>
        {
            await foreach (var msg in connection.SubscribeAsync<byte[]>("tenant.default.project.proj-1.live.sess-1.>").Take(1))
            {
                return msg;
            }
            throw new InvalidOperationException("no message");
        });

        var evt = new HarnessEvent
        {
            Type = EventTypes.MessagePartDelta,
            SessionId = "sess-1",
            Timestamp = DateTimeOffset.UtcNow,
            Payload = JsonSerializer.SerializeToElement(new { delta = "hi" })
        };
        await publisher.PublishAsync(evt,
            new EventPublishContext("sess-1", "proj-1", "user-1", "opencode"), CancellationToken.None);

        var received = await task.WaitAsync(TimeSpan.FromSeconds(5));
        received.Subject.ShouldBe("tenant.default.project.proj-1.live.sess-1.message.part.delta");
    }

    [Fact]
    public async Task UnknownClassification_isDroppedSilently()
    {
        // Any event type EventTypeMetadata doesn't classify as durable OR ephemeral relay is a no-op.
        var options = new NatsOptions();
        await using var connection = new NatsConnection(new NatsOpts { Url = _fixture.Url });
        var js = new NatsJSContext(connection);
        try { await js.DeleteStreamAsync("fleet-sessions"); } catch { /* may not exist */ }
        await js.CreateStreamAsync(new StreamConfig("fleet-sessions", ["tenant.*.project.*.session.*.>"]));

        var publisher = new NatsEventPublisher(js, connection, new NatsNamingStrategy(options), new NatsMetrics(), options);

        var evt = new HarnessEvent
        {
            Type = "totally.unknown.type",
            SessionId = "sess-1",
            Timestamp = DateTimeOffset.UtcNow
        };
        await publisher.PublishAsync(evt,
            new EventPublishContext("sess-1", "proj-1", "user-1", "opencode"), CancellationToken.None);

        var stream = await js.GetStreamAsync("fleet-sessions");
        (await stream.GetInfoAsync()).State.Messages.ShouldBe(0UL);
    }
}
```

- [ ] **Step 2: Run the failing tests.**

```bash
dotnet test tests/WeaveFleet.Infrastructure.Tests --nologo --filter FullyQualifiedName~NatsEventPublisherTests
```
Expected: FAIL — `NatsEventPublisher` does not exist.

- [ ] **Step 3: Implement `NatsEventPublisher`.**

Create `src/WeaveFleet.Infrastructure/Nats/NatsEventPublisher.cs`:

```csharp
using System.Diagnostics;
using System.Text.Json;
using NATS.Client.Core;
using NATS.Client.JetStream;
using WeaveFleet.Application.Configuration;
using WeaveFleet.Application.Events;
using WeaveFleet.Domain.Harnesses;

namespace WeaveFleet.Infrastructure.Nats;

/// <summary>
/// Publishes HarnessEvents to NATS. Durable events (per <see cref="EventTypeMetadata"/>) go to
/// JetStream with an awaited PubAck; ephemeral events go to core NATS fire-and-forget.
/// </summary>
public sealed class NatsEventPublisher : IEventPublisher
{
    private readonly INatsJSContext _js;
    private readonly INatsConnection _connection;
    private readonly NatsNamingStrategy _naming;
    private readonly NatsMetrics _metrics;
    private readonly NatsOptions _options;

    public NatsEventPublisher(
        INatsJSContext js,
        INatsConnection connection,
        NatsNamingStrategy naming,
        NatsMetrics metrics,
        NatsOptions options)
    {
        _js = js;
        _connection = connection;
        _naming = naming;
        _metrics = metrics;
        _options = options;
    }

    public async Task PublishAsync(HarnessEvent evt, EventPublishContext context, CancellationToken ct)
    {
        var classification = EventTypeMetadata.Classify(evt.Type);

        if (classification.IsDurable)
        {
            await PublishDurableAsync(evt, context, ct).ConfigureAwait(false);
            return;
        }

        if (classification.IsEphemeralRelay)
        {
            await PublishEphemeralAsync(evt, context, ct).ConfigureAwait(false);
            return;
        }

        // Unknown classification — drop silently. Matches current relay behaviour
        // (the relay also skips such events). Recorded as metric for visibility.
        _metrics.RecordPublish(routing: "dropped", eventType: evt.Type, tenant: _options.TenantPrefix, result: "ok");
    }

    private async Task PublishDurableAsync(HarnessEvent evt, EventPublishContext context, CancellationToken ct)
    {
        var subject = _naming.DurableSubject(context.ProjectId, context.FleetSessionId, evt.Type);
        var payload = JsonSerializer.SerializeToUtf8Bytes(evt);
        var msgId = $"{context.FleetSessionId}:{evt.Timestamp.ToUnixTimeMilliseconds()}:{Guid.NewGuid():N}";
        var headers = new NatsHeaders
        {
            ["Nats-Msg-Id"] = msgId,
        };
        if (context.UserId is { Length: > 0 }) headers["x-fleet-user-id"] = context.UserId;
        if (context.HarnessType is { Length: > 0 }) headers["x-fleet-harness-type"] = context.HarnessType;

        var sw = Stopwatch.StartNew();
        string result = "ok";
        try
        {
            var ack = await _js.PublishAsync(
                subject: subject,
                data: payload,
                headers: headers,
                opts: new NatsJSPubOpts { MsgId = msgId },
                cancellationToken: ct).ConfigureAwait(false);
            ack.EnsureSuccess();
        }
        catch (Exception)
        {
            result = "error";
            throw;
        }
        finally
        {
            sw.Stop();
            _metrics.RecordPublishDuration(sw.Elapsed.TotalMilliseconds, evt.Type, _options.TenantPrefix, result);
            _metrics.RecordPublish(routing: "durable", eventType: evt.Type, tenant: _options.TenantPrefix, result: result);
        }
    }

    private async Task PublishEphemeralAsync(HarnessEvent evt, EventPublishContext context, CancellationToken ct)
    {
        var subject = _naming.EphemeralSubject(context.ProjectId, context.FleetSessionId, evt.Type);
        var payload = JsonSerializer.SerializeToUtf8Bytes(evt);
        var headers = new NatsHeaders();
        if (context.UserId is { Length: > 0 }) headers["x-fleet-user-id"] = context.UserId;
        if (context.HarnessType is { Length: > 0 }) headers["x-fleet-harness-type"] = context.HarnessType;

        string result = "ok";
        try
        {
            await _connection.PublishAsync(subject, payload, headers: headers, cancellationToken: ct).ConfigureAwait(false);
        }
        catch (Exception)
        {
            result = "error";
            throw;
        }
        finally
        {
            _metrics.RecordPublish(routing: "ephemeral", eventType: evt.Type, tenant: _options.TenantPrefix, result: result);
        }
    }
}
```

- [ ] **Step 4: Run tests.**

```bash
dotnet test tests/WeaveFleet.Infrastructure.Tests --nologo --filter FullyQualifiedName~NatsEventPublisherTests
```
Expected: PASS (all three tests).

- [ ] **Step 5: Commit.**

```bash
git add src/WeaveFleet.Infrastructure/Nats/NatsEventPublisher.cs tests/WeaveFleet.Infrastructure.Tests/Nats/NatsEventPublisherTests.cs tests/WeaveFleet.Testing/Nats/EmbeddedNatsTestFixture.cs tests/WeaveFleet.Testing/WeaveFleet.Testing.csproj
git commit -m "feat: add NatsEventPublisher with JetStream + core routing"
```

### Task 1.6: Implement `NatsServerHostedService`

**Files:**
- Create: `src/WeaveFleet.Infrastructure/Nats/EmbeddedNatsServer/NatsServerBinaryResolver.cs`
- Create: `src/WeaveFleet.Infrastructure/Nats/NatsServerHostedService.cs`
- Create: `tests/WeaveFleet.Infrastructure.Tests/Nats/NatsServerHostedServiceTests.cs`

- [ ] **Step 1: Create `NatsServerBinaryResolver`.**

Create `src/WeaveFleet.Infrastructure/Nats/EmbeddedNatsServer/NatsServerBinaryResolver.cs`:

```csharp
using System.Runtime.InteropServices;

namespace WeaveFleet.Infrastructure.Nats.EmbeddedNatsServer;

internal static class NatsServerBinaryResolver
{
    public static string Resolve()
    {
        var rid = ResolveRid();
        var exe = OperatingSystem.IsWindows() ? "nats-server.exe" : "nats-server";
        var candidate = Path.Combine(AppContext.BaseDirectory, "Nats", "EmbeddedNatsServer", "Binaries", rid, exe);
        if (!File.Exists(candidate))
            throw new FileNotFoundException($"Bundled nats-server not found for RID '{rid}' at {candidate}. Ensure the runtime is supported.");
        return candidate;
    }

    private static string ResolveRid()
    {
        if (OperatingSystem.IsWindows()) return "win-x64";
        if (OperatingSystem.IsMacOS()) return RuntimeInformation.OSArchitecture == Architecture.Arm64 ? "osx-arm64" : "osx-x64";
        if (OperatingSystem.IsLinux()) return "linux-x64";
        throw new PlatformNotSupportedException($"No bundled nats-server for OS {RuntimeInformation.OSDescription}.");
    }
}
```

- [ ] **Step 2: Write the failing test.**

Create `tests/WeaveFleet.Infrastructure.Tests/Nats/NatsServerHostedServiceTests.cs`:

```csharp
using Microsoft.Extensions.Logging.Abstractions;
using WeaveFleet.Application.Configuration;
using WeaveFleet.Infrastructure.Nats;

namespace WeaveFleet.Infrastructure.Tests.Nats;

public sealed class NatsServerHostedServiceTests
{
    [Fact]
    public async Task ExternalUrlSet_doesNotLaunchSubprocess()
    {
        var options = new NatsOptions { ExternalUrl = "nats://localhost:4222" };
        var sut = new NatsServerHostedService(options, NullLogger<NatsServerHostedService>.Instance);

        await sut.StartAsync(CancellationToken.None);
        sut.IsEmbeddedRunning.ShouldBeFalse();
        sut.ResolvedUrl.ShouldBe("nats://localhost:4222");
        await sut.StopAsync(CancellationToken.None);
    }

    [Fact]
    public async Task NoExternalUrl_launchesSubprocessOnLoopback()
    {
        var dir = Path.Combine(Path.GetTempPath(), "nats-hosted-test-" + Guid.NewGuid().ToString("N"));
        try
        {
            var options = new NatsOptions { DataDirectory = dir };
            var sut = new NatsServerHostedService(options, NullLogger<NatsServerHostedService>.Instance);

            await sut.StartAsync(CancellationToken.None);
            try
            {
                sut.IsEmbeddedRunning.ShouldBeTrue();
                sut.ResolvedUrl.ShouldStartWith("nats://127.0.0.1:");
            }
            finally
            {
                await sut.StopAsync(CancellationToken.None);
            }
        }
        finally
        {
            if (Directory.Exists(dir)) Directory.Delete(dir, recursive: true);
        }
    }
}
```

- [ ] **Step 3: Run the failing test.**

```bash
dotnet test tests/WeaveFleet.Infrastructure.Tests --nologo --filter FullyQualifiedName~NatsServerHostedServiceTests
```
Expected: FAIL — type missing.

- [ ] **Step 4: Implement `NatsServerHostedService`.**

Create `src/WeaveFleet.Infrastructure/Nats/NatsServerHostedService.cs`:

```csharp
using System.Diagnostics;
using System.Net.Sockets;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using WeaveFleet.Application.Configuration;
using WeaveFleet.Infrastructure.Nats.EmbeddedNatsServer;

namespace WeaveFleet.Infrastructure.Nats;

/// <summary>
/// Launches the bundled nats-server subprocess on loopback when no external broker URL is
/// configured. No-op when <see cref="NatsOptions.ExternalUrl"/> is set.
/// Exposes <see cref="ResolvedUrl"/> so the NATS client registration can consume it after Start.
/// </summary>
public sealed class NatsServerHostedService : IHostedService, IAsyncDisposable
{
    private static readonly Action<ILogger, string, Exception?> LogLaunched =
        LoggerMessage.Define<string>(LogLevel.Information, new EventId(1, "NatsServerLaunched"),
            "Embedded nats-server launched at {Url}");
    private static readonly Action<ILogger, Exception?> LogSkipped =
        LoggerMessage.Define(LogLevel.Information, new EventId(2, "NatsServerSkipped"),
            "Embedded nats-server skipped (ExternalUrl configured)");

    private readonly NatsOptions _options;
    private readonly ILogger<NatsServerHostedService> _logger;
    private Process? _process;

    public string ResolvedUrl { get; private set; } = "";
    public bool IsEmbeddedRunning => _process is { HasExited: false };

    public NatsServerHostedService(NatsOptions options, ILogger<NatsServerHostedService> logger)
    {
        _options = options;
        _logger = logger;
    }

    public async Task StartAsync(CancellationToken cancellationToken)
    {
        if (!string.IsNullOrWhiteSpace(_options.ExternalUrl))
        {
            ResolvedUrl = _options.ExternalUrl!;
            LogSkipped(_logger, null);
            return;
        }

        Directory.CreateDirectory(_options.DataDirectory);
        var port = GetFreeLoopbackPort();
        var binary = NatsServerBinaryResolver.Resolve();
        var args = $"-a 127.0.0.1 -p {port} -js -sd \"{Path.GetFullPath(_options.DataDirectory)}\"";

        _process = Process.Start(new ProcessStartInfo
        {
            FileName = binary,
            Arguments = args,
            RedirectStandardError = true,
            RedirectStandardOutput = true,
            UseShellExecute = false,
            WorkingDirectory = Environment.CurrentDirectory,
        }) ?? throw new InvalidOperationException("Failed to start embedded nats-server.");

        ResolvedUrl = $"nats://127.0.0.1:{port}";
        await WaitForReadyAsync("127.0.0.1", port, cancellationToken).ConfigureAwait(false);
        LogLaunched(_logger, ResolvedUrl, null);
    }

    public async Task StopAsync(CancellationToken cancellationToken)
    {
        if (_process is null) return;
        try
        {
            if (!_process.HasExited) _process.Kill(entireProcessTree: true);
            await _process.WaitForExitAsync(cancellationToken).ConfigureAwait(false);
        }
        catch (Exception) { /* best effort */ }
    }

    public async ValueTask DisposeAsync()
    {
        await StopAsync(CancellationToken.None).ConfigureAwait(false);
        _process?.Dispose();
    }

    private static int GetFreeLoopbackPort()
    {
        using var listener = new TcpListener(System.Net.IPAddress.Loopback, 0);
        listener.Start();
        var port = ((System.Net.IPEndPoint)listener.LocalEndpoint).Port;
        listener.Stop();
        return port;
    }

    private static async Task WaitForReadyAsync(string host, int port, CancellationToken ct)
    {
        for (int i = 0; i < 100; i++)
        {
            ct.ThrowIfCancellationRequested();
            try
            {
                using var client = new TcpClient();
                await client.ConnectAsync(host, port, ct).ConfigureAwait(false);
                return;
            }
            catch (SocketException) { await Task.Delay(100, ct).ConfigureAwait(false); }
        }
        throw new TimeoutException($"Embedded nats-server did not become ready on {host}:{port}.");
    }
}
```

- [ ] **Step 5: Run the tests.**

```bash
dotnet test tests/WeaveFleet.Infrastructure.Tests --nologo --filter FullyQualifiedName~NatsServerHostedServiceTests
```
Expected: PASS.

- [ ] **Step 6: Commit.**

```bash
git add src/WeaveFleet.Infrastructure/Nats/EmbeddedNatsServer/NatsServerBinaryResolver.cs src/WeaveFleet.Infrastructure/Nats/NatsServerHostedService.cs tests/WeaveFleet.Infrastructure.Tests/Nats/NatsServerHostedServiceTests.cs
git commit -m "feat: add NatsServerHostedService to manage bundled nats-server subprocess"
```

### Task 1.7: Implement `NatsStreamInitializer`

**Files:**
- Create: `src/WeaveFleet.Infrastructure/Nats/NatsStreamInitializer.cs`
- Create: `tests/WeaveFleet.Infrastructure.Tests/Nats/NatsStreamInitializerTests.cs`

- [ ] **Step 1: Write the failing test.**

Create `tests/WeaveFleet.Infrastructure.Tests/Nats/NatsStreamInitializerTests.cs`:

```csharp
using Microsoft.Extensions.Logging.Abstractions;
using NATS.Client.Core;
using NATS.Client.JetStream;
using WeaveFleet.Application.Configuration;
using WeaveFleet.Infrastructure.Nats;
using WeaveFleet.Testing.Nats;

namespace WeaveFleet.Infrastructure.Tests.Nats;

public sealed class NatsStreamInitializerTests(EmbeddedNatsTestFixture fixture) : IClassFixture<EmbeddedNatsTestFixture>
{
    private readonly EmbeddedNatsTestFixture _fixture = fixture;

    [Fact]
    public async Task Start_createsStreamIdempotently()
    {
        var options = new NatsOptions();
        await using var conn = new NatsConnection(new NatsOpts { Url = _fixture.Url });
        var js = new NatsJSContext(conn);
        var sut = new NatsStreamInitializer(js, new NatsNamingStrategy(options), options, NullLogger<NatsStreamInitializer>.Instance);

        await sut.StartAsync(CancellationToken.None);
        await sut.StartAsync(CancellationToken.None); // idempotent on repeat

        var stream = await js.GetStreamAsync(options.StreamName);
        (await stream.GetInfoAsync()).Config.Subjects.ShouldContain("tenant.*.project.*.session.*.>");
    }
}
```

- [ ] **Step 2: Run the failing test.**

```bash
dotnet test tests/WeaveFleet.Infrastructure.Tests --nologo --filter FullyQualifiedName~NatsStreamInitializerTests
```
Expected: FAIL — type missing.

- [ ] **Step 3: Implement `NatsStreamInitializer`.**

Create `src/WeaveFleet.Infrastructure/Nats/NatsStreamInitializer.cs`:

```csharp
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using NATS.Client.JetStream;
using NATS.Client.JetStream.Models;
using WeaveFleet.Application.Configuration;

namespace WeaveFleet.Infrastructure.Nats;

/// <summary>
/// Hosted service that creates the durable JetStream stream on startup if it does not exist.
/// Idempotent — catches the "already exists" error and continues. Retention / MaxAge / MaxBytes
/// come from <see cref="NatsOptions"/>; subjects come from <see cref="NatsNamingStrategy"/>.
/// </summary>
public sealed class NatsStreamInitializer : IHostedService
{
    private static readonly Action<ILogger, string, Exception?> LogStreamReady =
        LoggerMessage.Define<string>(LogLevel.Information, new EventId(1, "NatsStreamReady"),
            "JetStream stream {StreamName} ready.");

    private readonly INatsJSContext _js;
    private readonly NatsNamingStrategy _naming;
    private readonly NatsOptions _options;
    private readonly ILogger<NatsStreamInitializer> _logger;

    public NatsStreamInitializer(
        INatsJSContext js,
        NatsNamingStrategy naming,
        NatsOptions options,
        ILogger<NatsStreamInitializer> logger)
    {
        _js = js;
        _naming = naming;
        _options = options;
        _logger = logger;
    }

    public async Task StartAsync(CancellationToken cancellationToken)
    {
        var config = new StreamConfig(_options.StreamName, [_naming.DurableStreamFilter])
        {
            Retention = StreamConfigRetention.Interest,
            Storage = StreamConfigStorage.File,
            MaxAge = _options.MaxAge,
            MaxBytes = _options.MaxBytes,
            Duplicates = TimeSpan.FromMinutes(2),
        };

        try
        {
            await _js.CreateStreamAsync(config, cancellationToken).ConfigureAwait(false);
        }
        catch (NatsJSApiException ex) when (ex.Error.Code == 400 || ex.Error.ErrCode == 10058 /* stream already in use */)
        {
            // Already exists; update to ensure configured subjects and retention match.
            await _js.UpdateStreamAsync(config, cancellationToken).ConfigureAwait(false);
        }

        LogStreamReady(_logger, _options.StreamName, null);
    }

    public Task StopAsync(CancellationToken cancellationToken) => Task.CompletedTask;
}
```

- [ ] **Step 4: Run the test.**

```bash
dotnet test tests/WeaveFleet.Infrastructure.Tests --nologo --filter FullyQualifiedName~NatsStreamInitializerTests
```
Expected: PASS.

- [ ] **Step 5: Commit.**

```bash
git add src/WeaveFleet.Infrastructure/Nats/NatsStreamInitializer.cs tests/WeaveFleet.Infrastructure.Tests/Nats/NatsStreamInitializerTests.cs
git commit -m "feat: add NatsStreamInitializer (idempotent stream creation)"
```

### Task 1.8: Fluent DI surface

**Files:**
- Create: `src/WeaveFleet.Infrastructure/Nats/Configuration/NatsStreamBuilder.cs`
- Create: `src/WeaveFleet.Infrastructure/Nats/Configuration/NatsServiceCollectionExtensions.cs`

- [ ] **Step 1: Implement the fluent builder.**

Create `src/WeaveFleet.Infrastructure/Nats/Configuration/NatsStreamBuilder.cs`:

```csharp
using Microsoft.Extensions.DependencyInjection;
using WeaveFleet.Application.Configuration;

namespace WeaveFleet.Infrastructure.Nats.Configuration;

public sealed class NatsStreamBuilder
{
    private readonly IServiceCollection _services;
    internal NatsStreamBuilder(IServiceCollection services) => _services = services;

    internal readonly List<Type> ProjectionTypes = new();

    public NatsStreamBuilder AddProjection<TProjection>()
        where TProjection : class
    {
        _services.AddScoped<TProjection>();
        ProjectionTypes.Add(typeof(TProjection));
        return this;
    }
}
```

Create `src/WeaveFleet.Infrastructure/Nats/Configuration/NatsServiceCollectionExtensions.cs`:

```csharp
using Microsoft.Extensions.DependencyInjection;
using NATS.Client.Core;
using NATS.Client.Hosting;
using NATS.Client.JetStream;
using WeaveFleet.Application.Configuration;
using WeaveFleet.Application.Events;

namespace WeaveFleet.Infrastructure.Nats.Configuration;

public static class NatsServiceCollectionExtensions
{
    /// <summary>
    /// Register the NATS event substrate: embedded server (if applicable), stream initializer,
    /// publisher, projection host, and any projections declared via the fluent builder.
    /// </summary>
    public static IServiceCollection AddEventStore(
        this IServiceCollection services,
        NatsOptions options,
        Action<NatsStreamBuilder> configure)
    {
        services.AddSingleton(options);
        services.AddSingleton<NatsMetrics>();
        services.AddSingleton<NatsNamingStrategy>();
        services.AddSingleton<NatsServerHostedService>();
        services.AddHostedService(sp => sp.GetRequiredService<NatsServerHostedService>());

        // Register NATS client — Url is resolved from NatsServerHostedService after Start,
        // but DI needs a value up front. For embedded mode we rely on a lazy factory that
        // resolves the URL on first use (see below). For simplicity in Phase 1, callers of the
        // connection must ensure the hosted service has started before requesting the connection.
        services.AddNats(poolSize: 1, opts => opts with
        {
            // Populated post-start. The factory below swaps in the resolved URL.
        });
        services.AddSingleton<INatsConnection>(sp =>
        {
            var server = sp.GetRequiredService<NatsServerHostedService>();
            if (string.IsNullOrWhiteSpace(server.ResolvedUrl))
                throw new InvalidOperationException("NatsServerHostedService must start before the NATS connection is requested.");
            var creds = options.CredsFile is { Length: > 0 } ? NatsAuthOpts.Default with { CredsFile = options.CredsFile } : NatsAuthOpts.Default;
            return new NatsConnection(new NatsOpts { Url = server.ResolvedUrl, AuthOpts = creds });
        });
        services.AddSingleton<INatsJSContext>(sp => new NatsJSContext(sp.GetRequiredService<INatsConnection>()));

        services.AddHostedService<NatsStreamInitializer>();

        services.AddSingleton<IEventPublisher, NatsEventPublisher>();

        var builder = new NatsStreamBuilder(services);
        configure(builder);

        // Register the projection host with the ordered list of projection types.
        services.AddSingleton(new ProjectionRegistry(builder.ProjectionTypes));
        services.AddHostedService<ProjectionHostService>();
        services.AddHostedService<EphemeralEventRelayService>();

        return services;
    }
}

public sealed record ProjectionRegistry(IReadOnlyList<Type> ProjectionTypes);
```

- [ ] **Step 2: Build.**

```bash
dotnet build --nologo
```
Expected: **build fails** because `ProjectionHostService` and `EphemeralEventRelayService` don't exist yet (next tasks). That is expected — skip straight to Task 1.9 and then return to commit this.

### Task 1.9: Wire into `Program.cs` (delayed until projection host lands)

Deferred. Registration happens in Task 2.3 once `ProjectionHostService` exists, and in Task 3.3 when `EphemeralEventRelayService` lands. Phase 1 verification (Task 1.10) uses a direct `NatsEventPublisher` instantiation in the test — not the DI graph.

---

## Phase 2 — Projection host

### Task 2.1: Define `IProjection<T>` and `ProjectionContext`

**Files:**
- Create: `src/WeaveFleet.Application/Projections/ProjectionContext.cs`
- Create: `src/WeaveFleet.Application/Projections/IProjection.cs`

- [ ] **Step 1: Create `ProjectionContext`.**

Create `src/WeaveFleet.Application/Projections/ProjectionContext.cs`:

```csharp
namespace WeaveFleet.Application.Projections;

/// <summary>
/// Metadata extracted from a NATS message for consumption by a projection.
/// Subject parts come from <c>NatsNamingStrategy.ParseDurableSubject</c>; header values are
/// read from the message's headers collection.
/// </summary>
public readonly record struct ProjectionContext(
    string Tenant,
    string ProjectId,
    string FleetSessionId,
    string EventType,
    string? UserId,
    string? HarnessType,
    long StreamSequence);
```

- [ ] **Step 2: Create `IProjection<T>`.**

Create `src/WeaveFleet.Application/Projections/IProjection.cs`:

```csharp
namespace WeaveFleet.Application.Projections;

/// <summary>
/// A projection consumes events of type <typeparamref name="T"/> from the event substrate.
/// Implementations should be idempotent — JetStream may redeliver a message on transient failure.
/// </summary>
public interface IProjection<T>
{
    /// <summary>Stable projection name. Used for consumer durable name and metric labels.</summary>
    string Name { get; }

    Task HandleAsync(T evt, ProjectionContext ctx, CancellationToken ct);
}
```

- [ ] **Step 3: Build & commit.**

```bash
dotnet build --nologo
git add src/WeaveFleet.Application/Projections
git commit -m "feat: add IProjection<T> and ProjectionContext for durable consumers"
```

### Task 2.2: Implement `ProjectionListener`

**Files:**
- Create: `src/WeaveFleet.Infrastructure/Nats/ProjectionListener.cs`
- Create: `tests/WeaveFleet.Infrastructure.Tests/Nats/ProjectionListenerTests.cs`

- [ ] **Step 1: Write the failing test.**

Create `tests/WeaveFleet.Infrastructure.Tests/Nats/ProjectionListenerTests.cs`:

```csharp
using System.Text.Json;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;
using NATS.Client.Core;
using NATS.Client.JetStream;
using NATS.Client.JetStream.Models;
using WeaveFleet.Application.Configuration;
using WeaveFleet.Application.Projections;
using WeaveFleet.Domain.Harnesses;
using WeaveFleet.Infrastructure.Nats;
using WeaveFleet.Testing.Nats;

namespace WeaveFleet.Infrastructure.Tests.Nats;

public sealed class ProjectionListenerTests(EmbeddedNatsTestFixture fixture) : IClassFixture<EmbeddedNatsTestFixture>
{
    private readonly EmbeddedNatsTestFixture _fixture = fixture;

    [Fact]
    public async Task DeliversEvent_parsesSubject_andAcks()
    {
        await using var conn = new NatsConnection(new NatsOpts { Url = _fixture.Url });
        var js = new NatsJSContext(conn);
        try { await js.DeleteStreamAsync("fleet-sessions"); } catch { }
        await js.CreateStreamAsync(new StreamConfig("fleet-sessions", ["tenant.*.project.*.session.*.>"]));

        // Publish a synthetic event
        var evt = new HarnessEvent
        {
            Type = EventTypes.MessageCreated,
            SessionId = "sess-x",
            Timestamp = DateTimeOffset.UtcNow,
            Payload = JsonSerializer.SerializeToElement(new { info = new { role = "assistant" } }),
        };
        var headers = new NatsHeaders { ["x-fleet-user-id"] = "user-1", ["x-fleet-harness-type"] = "opencode" };
        await js.PublishAsync(
            subject: "tenant.default.project.proj-1.session.sess-x.message.created",
            data: JsonSerializer.SerializeToUtf8Bytes(evt),
            headers: headers,
            opts: new NatsJSPubOpts { MsgId = "m1" });

        // Arrange listener
        var received = new TaskCompletionSource<(HarnessEvent Evt, ProjectionContext Ctx)>();
        var projection = new RecordingProjection(received);
        var services = new ServiceCollection();
        services.AddScoped<RecordingProjection>(_ => projection);
        var sp = services.BuildServiceProvider();

        var listener = new ProjectionListener(
            typeof(RecordingProjection),
            js,
            sp,
            new NatsNamingStrategy(new NatsOptions()),
            new NatsMetrics(),
            NullLogger<ProjectionListener>.Instance);

        using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(10));
        var runTask = Task.Run(() => listener.RunAsync(cts.Token), cts.Token);

        var (receivedEvt, receivedCtx) = await received.Task.WaitAsync(TimeSpan.FromSeconds(8));
        receivedEvt.Type.ShouldBe(EventTypes.MessageCreated);
        receivedCtx.ProjectId.ShouldBe("proj-1");
        receivedCtx.FleetSessionId.ShouldBe("sess-x");
        receivedCtx.UserId.ShouldBe("user-1");
        receivedCtx.HarnessType.ShouldBe("opencode");

        cts.Cancel();
        try { await runTask; } catch (OperationCanceledException) { }
    }

    private sealed class RecordingProjection : IProjection<HarnessEvent>
    {
        private readonly TaskCompletionSource<(HarnessEvent, ProjectionContext)> _tcs;
        public RecordingProjection(TaskCompletionSource<(HarnessEvent, ProjectionContext)> tcs) => _tcs = tcs;
        public string Name => "recording";
        public Task HandleAsync(HarnessEvent evt, ProjectionContext ctx, CancellationToken ct)
        {
            _tcs.TrySetResult((evt, ctx));
            return Task.CompletedTask;
        }
    }
}
```

- [ ] **Step 2: Run the failing test.**

```bash
dotnet test tests/WeaveFleet.Infrastructure.Tests --nologo --filter FullyQualifiedName~ProjectionListenerTests
```
Expected: FAIL — `ProjectionListener` does not exist.

- [ ] **Step 3: Implement `ProjectionListener`.**

Create `src/WeaveFleet.Infrastructure/Nats/ProjectionListener.cs`:

```csharp
using System.Diagnostics;
using System.Text.Json;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using NATS.Client.JetStream;
using NATS.Client.JetStream.Models;
using WeaveFleet.Application.Projections;
using WeaveFleet.Domain.Harnesses;

namespace WeaveFleet.Infrastructure.Nats;

/// <summary>
/// Pumps messages from a durable JetStream consumer into a single <see cref="IProjection{HarnessEvent}"/>.
/// Durable consumer name is derived from the projection's <see cref="IProjection{T}.Name"/>;
/// on transient handler failure the message is NAK'd for redelivery.
/// </summary>
public sealed class ProjectionListener
{
    private static readonly Action<ILogger, string, Exception?> LogHandlerFailed =
        LoggerMessage.Define<string>(LogLevel.Error, new EventId(1, "ProjectionHandlerFailed"),
            "Projection {Projection} handler threw");

    private readonly Type _projectionType;
    private readonly INatsJSContext _js;
    private readonly IServiceProvider _rootProvider;
    private readonly NatsNamingStrategy _naming;
    private readonly NatsMetrics _metrics;
    private readonly ILogger<ProjectionListener> _logger;

    public ProjectionListener(
        Type projectionType,
        INatsJSContext js,
        IServiceProvider rootProvider,
        NatsNamingStrategy naming,
        NatsMetrics metrics,
        ILogger<ProjectionListener> logger)
    {
        _projectionType = projectionType;
        _js = js;
        _rootProvider = rootProvider;
        _naming = naming;
        _metrics = metrics;
        _logger = logger;
    }

    public async Task RunAsync(CancellationToken ct)
    {
        // Resolve projection name by creating a one-shot scope
        string projectionName;
        using (var scope = _rootProvider.CreateScope())
        {
            var temp = (IProjection<HarnessEvent>)scope.ServiceProvider.GetRequiredService(_projectionType);
            projectionName = temp.Name;
        }

        var consumerConfig = new ConsumerConfig(_naming.DurableConsumerName(projectionName))
        {
            AckPolicy = ConsumerConfigAckPolicy.Explicit,
            DeliverPolicy = ConsumerConfigDeliverPolicy.All,
            FilterSubject = _naming.DurableStreamFilter,
        };
        var consumer = await _js.CreateOrUpdateConsumerAsync(_naming.StreamName, consumerConfig, ct).ConfigureAwait(false);

        await foreach (var msg in consumer.ConsumeAsync<byte[]>(cancellationToken: ct).ConfigureAwait(false))
        {
            var sw = Stopwatch.StartNew();
            string result = "ack";
            try
            {
                var parsed = NatsNamingStrategy.ParseDurableSubject(msg.Subject)
                    ?? throw new InvalidOperationException($"Subject did not parse: {msg.Subject}");
                var evt = JsonSerializer.Deserialize<HarnessEvent>(msg.Data!)
                    ?? throw new InvalidOperationException("Payload did not deserialize to HarnessEvent.");
                string? userId = msg.Headers?["x-fleet-user-id"].ToString();
                string? harnessType = msg.Headers?["x-fleet-harness-type"].ToString();
                var ctx = new ProjectionContext(
                    Tenant: parsed.Tenant,
                    ProjectId: parsed.ProjectId,
                    FleetSessionId: parsed.SessionId,
                    EventType: parsed.EventType,
                    UserId: string.IsNullOrEmpty(userId) ? null : userId,
                    HarnessType: string.IsNullOrEmpty(harnessType) ? null : harnessType,
                    StreamSequence: (long)(msg.Metadata?.Sequence.Stream ?? 0));

                using var scope = _rootProvider.CreateScope();
                var projection = (IProjection<HarnessEvent>)scope.ServiceProvider.GetRequiredService(_projectionType);

                await projection.HandleAsync(evt, ctx, ct).ConfigureAwait(false);
                await msg.AckAsync(cancellationToken: ct).ConfigureAwait(false);
            }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                result = "nak";
                LogHandlerFailed(_logger, projectionName, ex);
                try { await msg.NakAsync(cancellationToken: ct).ConfigureAwait(false); } catch { /* best effort */ }
            }
            finally
            {
                sw.Stop();
                _metrics.RecordProjectionHandler(sw.Elapsed.TotalMilliseconds, projectionName, msg.Subject, result);
                _metrics.RecordProjectionAck(projectionName, result);
            }
        }
    }
}
```

- [ ] **Step 4: Run the test.**

```bash
dotnet test tests/WeaveFleet.Infrastructure.Tests --nologo --filter FullyQualifiedName~ProjectionListenerTests
```
Expected: PASS.

- [ ] **Step 5: Commit.**

```bash
git add src/WeaveFleet.Infrastructure/Nats/ProjectionListener.cs tests/WeaveFleet.Infrastructure.Tests/Nats/ProjectionListenerTests.cs
git commit -m "feat: add ProjectionListener that pumps JetStream messages to IProjection handlers"
```

### Task 2.3: Implement `ProjectionHostService`

**Files:**
- Create: `src/WeaveFleet.Infrastructure/Nats/ProjectionHostService.cs`

- [ ] **Step 1: Implement the hosted service.**

Create `src/WeaveFleet.Infrastructure/Nats/ProjectionHostService.cs`:

```csharp
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using NATS.Client.JetStream;
using WeaveFleet.Infrastructure.Nats.Configuration;

namespace WeaveFleet.Infrastructure.Nats;

public sealed class ProjectionHostService : BackgroundService
{
    private readonly ProjectionRegistry _registry;
    private readonly INatsJSContext _js;
    private readonly IServiceProvider _rootProvider;
    private readonly NatsNamingStrategy _naming;
    private readonly NatsMetrics _metrics;
    private readonly ILoggerFactory _loggerFactory;

    public ProjectionHostService(
        ProjectionRegistry registry,
        INatsJSContext js,
        IServiceProvider rootProvider,
        NatsNamingStrategy naming,
        NatsMetrics metrics,
        ILoggerFactory loggerFactory)
    {
        _registry = registry;
        _js = js;
        _rootProvider = rootProvider;
        _naming = naming;
        _metrics = metrics;
        _loggerFactory = loggerFactory;
    }

    protected override Task ExecuteAsync(CancellationToken stoppingToken)
    {
        var tasks = _registry.ProjectionTypes.Select(type =>
        {
            var listener = new ProjectionListener(
                type, _js, _rootProvider, _naming, _metrics,
                _loggerFactory.CreateLogger<ProjectionListener>());
            return Task.Run(() => listener.RunAsync(stoppingToken), stoppingToken);
        }).ToArray();
        return Task.WhenAll(tasks);
    }
}
```

- [ ] **Step 2: Create a `NoOpProjection` for smoke verification.**

Create `src/WeaveFleet.Application/Projections/NoOpProjection.cs`:

```csharp
using Microsoft.Extensions.Logging;
using WeaveFleet.Domain.Harnesses;

namespace WeaveFleet.Application.Projections;

/// <summary>
/// Logs every received event. Used during rollout to prove consumer wiring
/// end-to-end before a real projection takes over.
/// </summary>
public sealed class NoOpProjection : IProjection<HarnessEvent>
{
    private static readonly Action<ILogger, string, string, Exception?> LogReceived =
        LoggerMessage.Define<string, string>(LogLevel.Debug, new EventId(1, "NoOpProjectionReceived"),
            "NoOpProjection received {EventType} on session {SessionId}");

    private readonly ILogger<NoOpProjection> _logger;
    public NoOpProjection(ILogger<NoOpProjection> logger) => _logger = logger;

    public string Name => "noop";

    public Task HandleAsync(HarnessEvent evt, ProjectionContext ctx, CancellationToken ct)
    {
        LogReceived(_logger, evt.Type, ctx.FleetSessionId, null);
        return Task.CompletedTask;
    }
}
```

- [ ] **Step 3: Wire initial `AddEventStore` into `AddFleetInfrastructure`.**

Edit `src/WeaveFleet.Infrastructure/DependencyInjection.cs`. Add a `using WeaveFleet.Infrastructure.Nats.Configuration;` and after line 122 (the `InMemoryEventBroadcaster` registration):

```csharp
        // NATS event substrate (Phase 1 registers publisher + NoOpProjection only)
        services.AddEventStore(options.Nats, nats =>
        {
            nats.AddProjection<WeaveFleet.Application.Projections.NoOpProjection>();
        });
```

- [ ] **Step 4: Build.**

```bash
dotnet build --nologo
```
Expected: build succeeds. `EphemeralEventRelayService` is referenced from `NatsServiceCollectionExtensions`; since it doesn't exist yet this step fails.

**Workaround for now:** In `NatsServiceCollectionExtensions.cs` (Task 1.8 Step 1), replace `services.AddHostedService<EphemeralEventRelayService>();` with a TODO comment:

```csharp
        // EphemeralEventRelayService registered in Phase 3 Task 3.4
```

Rerun build. Expected: PASS.

- [ ] **Step 5: Commit.**

```bash
git add src/WeaveFleet.Infrastructure/Nats/ProjectionHostService.cs src/WeaveFleet.Application/Projections/NoOpProjection.cs src/WeaveFleet.Infrastructure/Nats/Configuration/NatsServiceCollectionExtensions.cs src/WeaveFleet.Infrastructure/Nats/Configuration/NatsStreamBuilder.cs src/WeaveFleet.Infrastructure/DependencyInjection.cs
git commit -m "feat: add ProjectionHostService + NoOpProjection; wire AddEventStore into DI"
```

### Task 2.4: End-to-end smoke test — publish → stream → NoOpProjection

**Files:**
- Create: `tests/WeaveFleet.Api.Tests/Nats/NatsEventSubstrateEndToEndTests.cs`

- [ ] **Step 1: Write a minimal integration test.**

Create `tests/WeaveFleet.Api.Tests/Nats/NatsEventSubstrateEndToEndTests.cs`:

```csharp
using System.Text.Json;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.Extensions.DependencyInjection;
using NATS.Client.JetStream;
using WeaveFleet.Application.Configuration;
using WeaveFleet.Application.Events;
using WeaveFleet.Domain.Harnesses;
using WeaveFleet.Infrastructure.Nats;

namespace WeaveFleet.Api.Tests.Nats;

// Subclass WebApplicationFactory<Program> directly because ApiWebApplicationFactory is sealed.
public sealed class NatsEnabledFactory : WebApplicationFactory<Program>
{
    protected override void ConfigureWebHost(Microsoft.AspNetCore.Hosting.IWebHostBuilder builder)
    {
        // Overriding config so tests get a clean data dir per test class.
        var dir = Path.Combine(Path.GetTempPath(), $"fleet-nats-e2e-{Guid.NewGuid():N}");
        Directory.CreateDirectory(dir);
        builder.UseSetting("Fleet:Nats:DataDirectory", dir);
        builder.UseSetting("Fleet:DatabasePath", Path.Combine(dir, "fleet.db"));
        builder.UseSetting("Fleet:AnalyticsEnabled", "false"); // noise reduction
    }
}

public sealed class NatsEventSubstrateEndToEndTests : IClassFixture<NatsEnabledFactory>
{
    private readonly NatsEnabledFactory _factory;
    public NatsEventSubstrateEndToEndTests(NatsEnabledFactory factory) => _factory = factory;

    [Fact]
    public async Task PublishDurableEvent_landsOnStream()
    {
        using var client = _factory.CreateClient(); // forces host startup
        using var scope = _factory.Services.CreateScope();

        var publisher = scope.ServiceProvider.GetRequiredService<IEventPublisher>();
        var evt = new HarnessEvent
        {
            Type = EventTypes.MessageCreated,
            SessionId = "sess-e2e",
            Timestamp = DateTimeOffset.UtcNow,
            Payload = JsonSerializer.SerializeToElement(new { info = new { role = "assistant" } })
        };
        await publisher.PublishAsync(evt,
            new EventPublishContext("sess-e2e", "proj-e2e", "user-e2e", "opencode"), CancellationToken.None);

        var js = scope.ServiceProvider.GetRequiredService<INatsJSContext>();
        var stream = await js.GetStreamAsync("fleet-sessions");
        (await stream.GetInfoAsync()).State.Messages.ShouldBeGreaterThanOrEqualTo(1UL);
    }
}
```

- [ ] **Step 2: Run.**

```bash
dotnet test tests/WeaveFleet.Api.Tests --nologo --filter FullyQualifiedName~NatsEventSubstrateEndToEndTests
```
Expected: PASS.

- [ ] **Step 3: Commit.**

```bash
git add tests/WeaveFleet.Api.Tests/Nats/NatsEventSubstrateEndToEndTests.cs
git commit -m "test: e2e smoke — publisher + stream initializer wired via DI"
```

---

## Phase 3 — Dual-write compatibility window

Goal: `HarnessEventRelay` additionally publishes to NATS for every event; `MessagePersistenceProjection` and `WebSocketFanOutProjection` and `EphemeralEventRelayService` are running and writing to **shadow** tables / a shadow broadcaster. A diff harness verifies shadow output matches legacy output. Production users continue to see the legacy path exclusively.

### Task 3.0: Dual-write in `HarnessEventRelay` (additive)

**Files:**
- Modify: `src/WeaveFleet.Infrastructure/Services/HarnessEventRelay.cs`

- [ ] **Step 1: Inject `IEventPublisher`.**

Edit `src/WeaveFleet.Infrastructure/Services/HarnessEventRelay.cs`:

- Add `using WeaveFleet.Application.Events;` at the top.
- Add `IEventPublisher _publisher` field after line 33.
- Add `IEventPublisher publisher` to the constructor (line 57-69) and assign.

- [ ] **Step 2: Resolve `ProjectId` for the pump.**

The pump needs project id to construct the subject. Extend the session-resolution loop (lines 123-143) to grab `session.ProjectId`:

```csharp
        string? fleetSessionId = null;
        string? sessionUserId = null;
        string? projectId = null;
        string? harnessType = null;
        for (int attempt = 0; attempt < 10 && !ct.IsCancellationRequested; attempt++)
        {
            using var scope = _scopeFactory.CreateScope();
            var repo = scope.ServiceProvider.GetRequiredService<ISessionRepository>();
            var session = await repo.GetAnyForInstanceAsync(instanceId).ConfigureAwait(false);
            if (session is not null)
            {
                fleetSessionId = session.Id;
                sessionUserId = session.UserId;
                projectId = session.ProjectId;
                harnessType = session.HarnessType;
                break;
            }
            ...
        }
```

If `ISession` does not expose `ProjectId` or `HarnessType`, inspect `src/WeaveFleet.Domain/Entities/Session.cs` and confirm; they should exist per existing analytics work. If not, add the fields as a pre-req — but inspection required first.

- [ ] **Step 3: Publish alongside existing call.**

Inside the `await foreach` (line 172), after the existing delta-buffer / persistence / broadcast logic, add:

```csharp
                // Dual-write: publish to NATS so downstream projections observe every event.
                // Does not replace any existing behaviour during the compatibility window.
                try
                {
                    await _publisher.PublishAsync(evt,
                        new EventPublishContext(targetFleetSessionId, projectId, sessionUserId, harnessType),
                        ct).ConfigureAwait(false);
                }
                catch (Exception pubEx)
                {
                    // Publish failure during shadow window must not break the legacy path.
                    LogPublishFailed(_logger, instanceId, pubEx);
                }
```

And declare the new logger message near the existing ones at the top of the class:

```csharp
    private static readonly Action<ILogger, string, Exception?> LogPublishFailed =
        LoggerMessage.Define<string>(LogLevel.Error, new EventId(3, "EventPublishFailed"),
            "Event publish to NATS failed for instance {InstanceId}");
```

**Placement:** put the publish call *before* the `handledDurably` / ephemeral gating so the relay always publishes every event, regardless of legacy-path routing.

- [ ] **Step 4: Build & run existing tests.**

```bash
dotnet build --nologo
dotnet test --nologo
```
Expected: all previously-passing tests remain green; new `NatsEventSubstrateEndToEndTests` still passes.

- [ ] **Step 5: Commit.**

```bash
git add src/WeaveFleet.Infrastructure/Services/HarnessEventRelay.cs
git commit -m "feat: relay dual-writes every harness event to NATS during compatibility window"
```

### Task 3.1: `MessagePersistenceProjection` (shadow mode)

**Files:**
- Create: `src/WeaveFleet.Application/Projections/MessagePersistenceProjection.cs`
- Create: `tests/WeaveFleet.Application.Tests/Projections/MessagePersistenceProjectionTests.cs`

During shadow mode the projection must *not* write to the production `messages` / `sessions` tables — dual writes would conflict with the existing persistence call in the relay. Instead it writes to shadow tables (`messages_shadow`, `sessions_shadow`) populated exclusively by the projection, and a diff harness compares them to the production tables.

- [ ] **Step 1: Add shadow-table migration.**

Create `src/WeaveFleet.Infrastructure/Migrations/0NN_shadow_nats_tables.sql` (NN is next available):

```sql
CREATE TABLE IF NOT EXISTS messages_shadow AS SELECT * FROM messages WHERE 1=0;
CREATE TABLE IF NOT EXISTS sessions_shadow AS SELECT * FROM sessions WHERE 1=0;
```

Pick the right NN by listing `src/WeaveFleet.Infrastructure/Migrations/` and taking `max+1`.

- [ ] **Step 2: Introduce a shadow-mode flag on the persister.**

Add a boolean to `IHarnessEventPersister` is overkill. Instead, accept a shadow repository. Edit `src/WeaveFleet.Application/Services/IHarnessEventPersister.cs` to add:

```csharp
/// <summary>
/// Handle an event without writing outbox entries (shadow-mode for the NATS compat window).
/// </summary>
Task HandleShadowAsync(string fleetSessionId, string ownerUserId, HarnessEvent evt, CancellationToken ct);
```

Implement in `HarnessEventPersistenceService` by duplicating the routing switch but writing to shadow repositories. **Simpler alternative:** add `IShadowMessageRepository`/`IShadowSessionRepository` (thin Dapper wrappers over `messages_shadow`/`sessions_shadow`) and pass them in constructor; introduce a mode flag.

*Actual approach recommended:* create a parallel `HarnessEventShadowPersistenceService` that reuses the existing serialization helpers but writes to `messages_shadow` / `sessions_shadow` directly. It skips the outbox entirely (the WS fan-out projection writes to the shadow broadcaster instead). See Step 3.

- [ ] **Step 3: Create shadow repositories.**

Create `src/WeaveFleet.Domain/Repositories/IShadowMessageRepository.cs` and `IShadowSessionRepository.cs` mirroring the production interfaces but with table name `messages_shadow` / `sessions_shadow`.

Create Dapper implementations in `src/WeaveFleet.Infrastructure/Data/Repositories/DapperShadowMessageRepository.cs` and `DapperShadowSessionRepository.cs`. Register both as scoped in `DependencyInjection.cs`.

- [ ] **Step 4: Implement `MessagePersistenceProjection` (shadow).**

Create `src/WeaveFleet.Application/Projections/MessagePersistenceProjection.cs`:

```csharp
using WeaveFleet.Application.Services;
using WeaveFleet.Domain.Harnesses;
using WeaveFleet.Domain.Repositories;

namespace WeaveFleet.Application.Projections;

/// <summary>
/// Durable projection that writes harness events to the SQLite read model.
/// During the Phase 3 compatibility window, writes go to shadow tables
/// (messages_shadow / sessions_shadow). Phase 4 switches it to the production tables.
/// </summary>
public sealed class MessagePersistenceProjection : IProjection<HarnessEvent>
{
    private readonly IHarnessEventPersister _persister;
    public MessagePersistenceProjection(IHarnessEventPersister persister) => _persister = persister;

    public string Name => "message-persistence";

    public Task HandleAsync(HarnessEvent evt, ProjectionContext ctx, CancellationToken ct)
    {
        if (ctx.UserId is null) return Task.CompletedTask;
        return _persister.HandleShadowAsync(ctx.FleetSessionId, ctx.UserId, evt, ct);
    }
}
```

- [ ] **Step 5: Register the projection.**

Edit `src/WeaveFleet.Infrastructure/DependencyInjection.cs` to replace `NoOpProjection` with `MessagePersistenceProjection`:

```csharp
        services.AddEventStore(options.Nats, nats =>
        {
            nats.AddProjection<WeaveFleet.Application.Projections.MessagePersistenceProjection>();
        });
```

- [ ] **Step 6: Write a test asserting shadow writes.**

Create `tests/WeaveFleet.Application.Tests/Projections/MessagePersistenceProjectionTests.cs`:

```csharp
using System.Text.Json;
using WeaveFleet.Application.Projections;
using WeaveFleet.Application.Services;
using WeaveFleet.Domain.Harnesses;
using WeaveFleet.Testing.Fakes;

namespace WeaveFleet.Application.Tests.Projections;

public sealed class MessagePersistenceProjectionTests
{
    [Fact]
    public async Task Handle_delegatesToShadowPersister()
    {
        var persister = new FakeHarnessEventPersister();
        var sut = new MessagePersistenceProjection(persister);
        var evt = new HarnessEvent { Type = EventTypes.MessageCreated, SessionId = "s", Timestamp = DateTimeOffset.UtcNow };
        var ctx = new ProjectionContext("default", "proj", "sess", EventTypes.MessageCreated, "user-1", "opencode", 42);

        await sut.HandleAsync(evt, ctx, CancellationToken.None);

        persister.ShadowCalls.ShouldHaveSingleItem();
        var call = persister.ShadowCalls[0];
        call.FleetSessionId.ShouldBe("sess");
        call.OwnerUserId.ShouldBe("user-1");
        call.Event.Type.ShouldBe(EventTypes.MessageCreated);
    }

    [Fact]
    public async Task Handle_skipsWhenUserIdMissing()
    {
        var persister = new FakeHarnessEventPersister();
        var sut = new MessagePersistenceProjection(persister);
        var ctx = new ProjectionContext("default", "proj", "sess", EventTypes.MessageCreated, null, "opencode", 42);

        await sut.HandleAsync(new HarnessEvent { Type = EventTypes.MessageCreated, SessionId = "s", Timestamp = DateTimeOffset.UtcNow }, ctx, CancellationToken.None);

        persister.ShadowCalls.ShouldBeEmpty();
    }
}
```

Add `FakeHarnessEventPersister` to `tests/WeaveFleet.Testing/Fakes/FakeHarnessEventPersister.cs`:

```csharp
using WeaveFleet.Application.Services;
using WeaveFleet.Domain.Harnesses;

namespace WeaveFleet.Testing.Fakes;

public sealed class FakeHarnessEventPersister : IHarnessEventPersister
{
    public record Call(string FleetSessionId, string OwnerUserId, HarnessEvent Event);
    public List<Call> DurableCalls { get; } = new();
    public List<Call> ShadowCalls { get; } = new();

    public Task HandleAsync(string fleetSessionId, string ownerUserId, HarnessEvent evt, CancellationToken ct)
    {
        DurableCalls.Add(new Call(fleetSessionId, ownerUserId, evt));
        return Task.CompletedTask;
    }

    public Task HandleShadowAsync(string fleetSessionId, string ownerUserId, HarnessEvent evt, CancellationToken ct)
    {
        ShadowCalls.Add(new Call(fleetSessionId, ownerUserId, evt));
        return Task.CompletedTask;
    }
}
```

- [ ] **Step 7: Run tests.**

```bash
dotnet test tests/WeaveFleet.Application.Tests --nologo --filter FullyQualifiedName~MessagePersistenceProjectionTests
```
Expected: PASS.

- [ ] **Step 8: Commit.**

```bash
git add src/WeaveFleet.Application/Projections/MessagePersistenceProjection.cs src/WeaveFleet.Application/Services/IHarnessEventPersister.cs src/WeaveFleet.Domain/Repositories/IShadowMessageRepository.cs src/WeaveFleet.Domain/Repositories/IShadowSessionRepository.cs src/WeaveFleet.Infrastructure/Data/Repositories/DapperShadowMessageRepository.cs src/WeaveFleet.Infrastructure/Data/Repositories/DapperShadowSessionRepository.cs src/WeaveFleet.Infrastructure/Migrations src/WeaveFleet.Infrastructure/Services/HarnessEventPersistenceService.cs src/WeaveFleet.Infrastructure/DependencyInjection.cs tests/WeaveFleet.Application.Tests/Projections/MessagePersistenceProjectionTests.cs tests/WeaveFleet.Testing/Fakes/FakeHarnessEventPersister.cs
git commit -m "feat: MessagePersistenceProjection writes to shadow tables during compat window"
```

### Task 3.2: `WebSocketFanOutProjection` (shadow broadcaster)

**Files:**
- Create: `src/WeaveFleet.Application/Services/IShadowEventBroadcaster.cs`
- Create: `src/WeaveFleet.Infrastructure/Services/InMemoryShadowEventBroadcaster.cs`
- Create: `src/WeaveFleet.Application/Projections/WebSocketFanOutProjection.cs`
- Create: `tests/WeaveFleet.Application.Tests/Projections/WebSocketFanOutProjectionTests.cs`

- [ ] **Step 1: Define the shadow broadcaster interface (same shape, different lifetime).**

Create `src/WeaveFleet.Application/Services/IShadowEventBroadcaster.cs`:

```csharp
namespace WeaveFleet.Application.Services;

/// <summary>
/// Mirrors <see cref="IEventBroadcaster"/>. Exists only during the Phase 3 compat window so
/// the diff harness can compare what the production broadcaster receives (legacy path) with
/// what the NATS fan-out would have emitted.
/// </summary>
public interface IShadowEventBroadcaster : IEventBroadcaster { }
```

Create `src/WeaveFleet.Infrastructure/Services/InMemoryShadowEventBroadcaster.cs` mirroring `InMemoryEventBroadcaster` (copy its body, rename). Register as `AddSingleton<IShadowEventBroadcaster, InMemoryShadowEventBroadcaster>()`.

- [ ] **Step 2: Implement the projection.**

Create `src/WeaveFleet.Application/Projections/WebSocketFanOutProjection.cs`:

```csharp
using System.Text.Json;
using WeaveFleet.Application.Services;
using WeaveFleet.Domain.Harnesses;

namespace WeaveFleet.Application.Projections;

public sealed class WebSocketFanOutProjection : IProjection<HarnessEvent>
{
    private readonly IShadowEventBroadcaster _broadcaster;
    public WebSocketFanOutProjection(IShadowEventBroadcaster broadcaster) => _broadcaster = broadcaster;

    public string Name => "ws-fanout";

    public Task HandleAsync(HarnessEvent evt, ProjectionContext ctx, CancellationToken ct)
    {
        var topic = $"session:{ctx.FleetSessionId}";
        object payload = evt.Payload.HasValue
            ? evt.Payload.Value
            : JsonSerializer.SerializeToElement(new { });
        return _broadcaster.BroadcastAsync(topic, evt.Type, payload, ctx.UserId, ct);
    }
}
```

- [ ] **Step 3: Register it.**

Edit `src/WeaveFleet.Infrastructure/DependencyInjection.cs`:

```csharp
        services.AddSingleton<IShadowEventBroadcaster, InMemoryShadowEventBroadcaster>();

        services.AddEventStore(options.Nats, nats =>
        {
            nats.AddProjection<WeaveFleet.Application.Projections.MessagePersistenceProjection>();
            nats.AddProjection<WeaveFleet.Application.Projections.WebSocketFanOutProjection>();
        });
```

- [ ] **Step 4: Write a test.**

Create `tests/WeaveFleet.Application.Tests/Projections/WebSocketFanOutProjectionTests.cs`:

```csharp
using System.Text.Json;
using WeaveFleet.Application.Projections;
using WeaveFleet.Domain.Harnesses;
using WeaveFleet.Testing.Fakes;

namespace WeaveFleet.Application.Tests.Projections;

public sealed class WebSocketFanOutProjectionTests
{
    [Fact]
    public async Task Handle_forwardsToShadowBroadcaster_withSessionTopic()
    {
        var shadow = new FakeShadowEventBroadcaster();
        var sut = new WebSocketFanOutProjection(shadow);
        var evt = new HarnessEvent
        {
            Type = EventTypes.MessageUpdated,
            SessionId = "s",
            Timestamp = DateTimeOffset.UtcNow,
            Payload = JsonSerializer.SerializeToElement(new { id = "m1" })
        };
        var ctx = new ProjectionContext("default", "proj", "sess-1", EventTypes.MessageUpdated, "user-1", "opencode", 42);

        await sut.HandleAsync(evt, ctx, CancellationToken.None);

        shadow.Broadcasts.ShouldHaveSingleItem();
        shadow.Broadcasts[0].Topic.ShouldBe("session:sess-1");
        shadow.Broadcasts[0].Type.ShouldBe(EventTypes.MessageUpdated);
        shadow.Broadcasts[0].UserId.ShouldBe("user-1");
    }
}
```

And create `tests/WeaveFleet.Testing/Fakes/FakeShadowEventBroadcaster.cs` implementing `IShadowEventBroadcaster` and recording calls. (Follow the existing `WeaveFleet.Testing` fake patterns — no mocking libraries.)

- [ ] **Step 5: Run tests.**

```bash
dotnet test tests/WeaveFleet.Application.Tests --nologo --filter FullyQualifiedName~WebSocketFanOutProjectionTests
```
Expected: PASS.

- [ ] **Step 6: Commit.**

```bash
git add src/WeaveFleet.Application/Services/IShadowEventBroadcaster.cs src/WeaveFleet.Infrastructure/Services/InMemoryShadowEventBroadcaster.cs src/WeaveFleet.Application/Projections/WebSocketFanOutProjection.cs src/WeaveFleet.Infrastructure/DependencyInjection.cs tests/WeaveFleet.Application.Tests/Projections/WebSocketFanOutProjectionTests.cs tests/WeaveFleet.Testing/Fakes/FakeShadowEventBroadcaster.cs
git commit -m "feat: WebSocketFanOutProjection forwards durable events to shadow broadcaster"
```

### Task 3.3: `EphemeralEventRelayService` (core NATS subscriber → shadow broadcaster)

**Files:**
- Create: `src/WeaveFleet.Infrastructure/Nats/EphemeralEventRelayService.cs`

- [ ] **Step 1: Implement the subscriber.**

Create `src/WeaveFleet.Infrastructure/Nats/EphemeralEventRelayService.cs`:

```csharp
using System.Text.Json;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using NATS.Client.Core;
using WeaveFleet.Application.Services;
using WeaveFleet.Domain.Harnesses;

namespace WeaveFleet.Infrastructure.Nats;

/// <summary>
/// Core NATS subscriber for ephemeral events. During Phase 3 it forwards to
/// <see cref="IShadowEventBroadcaster"/>; Phase 5 switches the target to the production broadcaster.
/// </summary>
public sealed class EphemeralEventRelayService : BackgroundService
{
    private static readonly Action<ILogger, Exception?> LogFailed =
        LoggerMessage.Define(LogLevel.Warning, new EventId(1, "EphemeralForwardFailed"),
            "Failed to forward ephemeral NATS event to broadcaster");

    private readonly INatsConnection _connection;
    private readonly NatsNamingStrategy _naming;
    private readonly IShadowEventBroadcaster _shadow;
    private readonly ILogger<EphemeralEventRelayService> _logger;

    public EphemeralEventRelayService(
        INatsConnection connection,
        NatsNamingStrategy naming,
        IShadowEventBroadcaster shadow,
        ILogger<EphemeralEventRelayService> logger)
    {
        _connection = connection;
        _naming = naming;
        _shadow = shadow;
        _logger = logger;
    }

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        await foreach (var msg in _connection.SubscribeAsync<byte[]>(_naming.EphemeralSubscriptionFilter, cancellationToken: stoppingToken).ConfigureAwait(false))
        {
            try
            {
                // Subject format: tenant.{ws}.project.{pid}.live.{sid}.{type}
                var parts = msg.Subject.Split('.');
                if (parts.Length < 7 || parts[0] != "tenant" || parts[2] != "project" || parts[4] != "live")
                    continue;
                var sessionId = parts[5];
                var eventType = string.Join('.', parts[6..]);

                var evt = JsonSerializer.Deserialize<HarnessEvent>(msg.Data!);
                if (evt is null) continue;

                string? userId = msg.Headers?["x-fleet-user-id"].ToString();
                object payload = evt.Payload.HasValue ? evt.Payload.Value : JsonSerializer.SerializeToElement(new { });
                await _shadow.BroadcastAsync($"session:{sessionId}", eventType, payload, userId, stoppingToken).ConfigureAwait(false);
            }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                LogFailed(_logger, ex);
            }
        }
    }
}
```

- [ ] **Step 2: Register it in `NatsServiceCollectionExtensions`.**

Replace the earlier TODO stub with the real registration:

```csharp
        services.AddHostedService<EphemeralEventRelayService>();
```

- [ ] **Step 3: Build.**

```bash
dotnet build --nologo
```
Expected: PASS.

- [ ] **Step 4: Commit.**

```bash
git add src/WeaveFleet.Infrastructure/Nats/EphemeralEventRelayService.cs src/WeaveFleet.Infrastructure/Nats/Configuration/NatsServiceCollectionExtensions.cs
git commit -m "feat: EphemeralEventRelayService forwards core NATS ephemeral events to shadow broadcaster"
```

### Task 3.4: Shadow diff harness

**Files:**
- Create: `tests/WeaveFleet.Api.Tests/Nats/ShadowDiffHarnessTests.cs`

A harness that runs a fake harness session through the full pipeline and asserts the shadow output matches the production output.

- [ ] **Step 1: Write the harness test.**

Create `tests/WeaveFleet.Api.Tests/Nats/ShadowDiffHarnessTests.cs`:

```csharp
using System.Text.Json;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.Extensions.DependencyInjection;
using WeaveFleet.Application.Services;
using WeaveFleet.Domain.Harnesses;
using WeaveFleet.Domain.Repositories;

namespace WeaveFleet.Api.Tests.Nats;

public sealed class ShadowDiffHarnessTests : IClassFixture<NatsEnabledFactory>
{
    private readonly NatsEnabledFactory _factory;
    public ShadowDiffHarnessTests(NatsEnabledFactory factory) => _factory = factory;

    [Fact]
    public async Task FullHarnessFlow_shadowTablesMatchProductionTables()
    {
        // Arrange: spin up a fake harness that emits a canned script covering every EventType.
        using var scope = _factory.Services.CreateScope();
        var sp = scope.ServiceProvider;

        // ... drive the harness using the existing TestHarness project's fake harness machinery ...
        // Emit: message.created, message.part.updated, message.part.delta, message.updated,
        //       session.status, session.idle, session.error, session.compacted,
        //       message.part.removed, message.removed, session.updated, session.deleted

        // Act: wait for both sides to drain.
        await Task.Delay(TimeSpan.FromSeconds(2)); // naive; replace with deterministic wait on outbox/projection ack

        // Assert: production tables === shadow tables
        var prodMessages = sp.GetRequiredService<IMessageRepository>();
        var shadowMessages = sp.GetRequiredService<IShadowMessageRepository>();
        var prodSessions = sp.GetRequiredService<ISessionRepository>();
        var shadowSessions = sp.GetRequiredService<IShadowSessionRepository>();

        var prodMsgDump = await prodMessages.DumpAllForTestAsync();
        var shadowMsgDump = await shadowMessages.DumpAllForTestAsync();
        prodMsgDump.ShouldBe(shadowMsgDump);

        var prodSessDump = await prodSessions.DumpAllForTestAsync();
        var shadowSessDump = await shadowSessions.DumpAllForTestAsync();
        prodSessDump.ShouldBe(shadowSessDump);

        // Broadcaster diff
        var prodBroadcaster = (WeaveFleet.Testing.Fakes.RecordingEventBroadcaster)sp.GetRequiredService<IEventBroadcaster>();
        var shadowBroadcaster = (WeaveFleet.Testing.Fakes.RecordingShadowEventBroadcaster)sp.GetRequiredService<IShadowEventBroadcaster>();
        prodBroadcaster.EventsSnapshot.ShouldBe(shadowBroadcaster.EventsSnapshot);
    }
}
```

**Prerequisites not-yet-shown:** `IMessageRepository.DumpAllForTestAsync()`, `IShadowMessageRepository.DumpAllForTestAsync()`, `ISessionRepository.DumpAllForTestAsync()`, `IShadowSessionRepository.DumpAllForTestAsync()` — add these as test-only extension methods in `WeaveFleet.Testing` that run a simple `SELECT *` and return a deterministic ordered list. Also: `RecordingEventBroadcaster` + `RecordingShadowEventBroadcaster` installed in place of the default broadcaster when `NatsEnabledFactory.ConfigureWebHost` runs (wrap them so they still behave like the production broadcaster but record events). Build these as hand-crafted fakes in `WeaveFleet.Testing` per the project fakes-only policy.

- [ ] **Step 2: Implement the helpers.** Create the dump extension methods and recording broadcasters. Keep the fakes minimal.

- [ ] **Step 3: Run the harness.**

```bash
dotnet test tests/WeaveFleet.Api.Tests --nologo --filter FullyQualifiedName~ShadowDiffHarnessTests
```
Expected: PASS. If the diff fails, the delta points to the projection/relay divergence that must be fixed before Phase 4.

- [ ] **Step 4: Commit.**

```bash
git add tests/WeaveFleet.Api.Tests/Nats/ShadowDiffHarnessTests.cs tests/WeaveFleet.Testing/Fakes/RecordingEventBroadcaster.cs tests/WeaveFleet.Testing/Fakes/RecordingShadowEventBroadcaster.cs tests/WeaveFleet.Testing/Extensions/RepositoryDumpExtensions.cs
git commit -m "test: shadow diff harness asserts projection output matches legacy path"
```

---

## Phase 4 — Persistence cutover

**Precondition:** Task 0.2 audit shows every WS → SQLite path tolerates the "row not yet written" case. Task 0.3 concurrency audit resolved. Shadow diff harness passes under load.

### Task 4.1: Flip `MessagePersistenceProjection` to write production tables

**Files:**
- Modify: `src/WeaveFleet.Application/Projections/MessagePersistenceProjection.cs`

- [ ] **Step 1: Switch from shadow to production persister call.**

Edit `src/WeaveFleet.Application/Projections/MessagePersistenceProjection.cs`:

```csharp
    public Task HandleAsync(HarnessEvent evt, ProjectionContext ctx, CancellationToken ct)
    {
        if (ctx.UserId is null) return Task.CompletedTask;
        return _persister.HandleAsync(ctx.FleetSessionId, ctx.UserId, evt, ct);  // production path
    }
```

- [ ] **Step 2: Remove in-relay persistence call.**

Edit `src/WeaveFleet.Infrastructure/Services/HarnessEventRelay.cs`:

- Delete the `persistenceService?.TryHandleDurableEventAsync(...)` call at lines 183-184.
- Delete `if (handledDurably) continue;` at lines 186-187.
- Keep the `persistenceService?.BufferTextDelta(...)` (delta buffering still lives in the relay — design Decision 4).
- Keep the flush on disconnect (lines 237-247).

The pump now:
1. Buffers deltas for merge.
2. Publishes via `_publisher.PublishAsync(...)`.
3. Still broadcasts ephemeral events via `_broadcaster` (removed in Phase 5).
4. Flushes deltas on exit.

- [ ] **Step 3: Update text-delta merge to happen before durable publish.**

The design says the durable `message.updated` payload must include merged deltas (so downstream projections see the final snapshot). Currently the merge happens inside `HarnessEventPersistenceService.TryPersistMessageAsync` — which the relay will no longer invoke directly. The relay now needs to merge deltas *before* publishing durable events.

Implementation:

- Introduce a merge helper exposed on `IHarnessEventPersister` (or better: extract the merge logic to a static method on `MessagePersistenceService`, which already owns `MergeTextDeltaAndMetadata`).
- In the relay pump, when the event is `message.updated` (or `message.created`/`message.part.updated`), apply `BufferTextDelta` first, then on publish call an exposed `MergeBufferedDeltasIntoEvent(...)` that returns an updated `HarnessEvent` with the merged payload. Publish that merged event.

This is a non-trivial refactor. **Do not skip or hand-wave it.** The current text-delta buffer lives inside `HarnessEventPersistenceService` keyed by `(session::message::partId)` — expose a method `TryMergeBufferedDeltas(string fleetSessionId, HarnessEvent evt, out HarnessEvent mergedEvt)` on the persister interface, implemented by looking up buffered deltas and emitting a rebuilt `HarnessEvent` whose `Payload` includes the merged text parts.

Add the test in `tests/WeaveFleet.Infrastructure.Tests/Services/DeltaMergeBeforePublishTests.cs` that:
1. Calls `BufferTextDelta` with a series of partial deltas.
2. Passes a `message.updated` event to `TryMergeBufferedDeltas`.
3. Asserts the returned event's payload contains the merged text.

- [ ] **Step 4: Run the full integration test suite.**

```bash
dotnet test --nologo
```
Expected:
- All Phase-1–3 tests still pass.
- Shadow diff harness still runs (shadow tables still populated for now, until Task 4.3).
- `SkillEndpointPathTraversalTests.GetSkill_ReturnsBadRequestOrNotRouted_ForEncodedTraversal` remains on the known-failing list but is otherwise stable.

- [ ] **Step 5: Commit.**

```bash
git add src/WeaveFleet.Application/Projections/MessagePersistenceProjection.cs src/WeaveFleet.Infrastructure/Services/HarnessEventRelay.cs src/WeaveFleet.Infrastructure/Services/HarnessEventPersistenceService.cs src/WeaveFleet.Application/Services/IHarnessEventPersister.cs tests/WeaveFleet.Infrastructure.Tests/Services/DeltaMergeBeforePublishTests.cs
git commit -m "refactor: persistence cutover — projection writes production tables; relay publishes pre-merged durable payloads"
```

### Task 4.2: Retire shadow tables

**Files:**
- Create: `src/WeaveFleet.Infrastructure/Migrations/0NN_drop_shadow_nats_tables.sql`
- Modify/Delete: shadow repositories, `IShadowEventBroadcaster`, shadow projection path

- [ ] **Step 1: Drop shadow tables.**

Create `src/WeaveFleet.Infrastructure/Migrations/0NN_drop_shadow_nats_tables.sql`:

```sql
DROP TABLE IF EXISTS messages_shadow;
DROP TABLE IF EXISTS sessions_shadow;
```

- [ ] **Step 2: Delete the shadow repositories, shadow broadcaster, `HandleShadowAsync`, and `ShadowDiffHarnessTests`.** They served their purpose; keeping them risks rot.

- [ ] **Step 3: Verify build + tests.**

```bash
dotnet build --nologo
dotnet test --nologo
```
Expected: green.

- [ ] **Step 4: Commit.**

```bash
git rm src/WeaveFleet.Infrastructure/Data/Repositories/DapperShadowMessageRepository.cs src/WeaveFleet.Infrastructure/Data/Repositories/DapperShadowSessionRepository.cs src/WeaveFleet.Domain/Repositories/IShadowMessageRepository.cs src/WeaveFleet.Domain/Repositories/IShadowSessionRepository.cs src/WeaveFleet.Application/Services/IShadowEventBroadcaster.cs src/WeaveFleet.Infrastructure/Services/InMemoryShadowEventBroadcaster.cs tests/WeaveFleet.Api.Tests/Nats/ShadowDiffHarnessTests.cs tests/WeaveFleet.Testing/Fakes/RecordingShadowEventBroadcaster.cs tests/WeaveFleet.Testing/Fakes/FakeShadowEventBroadcaster.cs
git add src/WeaveFleet.Infrastructure/Migrations src/WeaveFleet.Application/Projections/WebSocketFanOutProjection.cs src/WeaveFleet.Application/Services/IHarnessEventPersister.cs
git commit -m "chore: retire shadow tables + shadow broadcaster after persistence cutover"
```

---

## Phase 5 — Broadcast cutover

### Task 5.1: Flip `WebSocketFanOutProjection` to production broadcaster

**Files:**
- Modify: `src/WeaveFleet.Application/Projections/WebSocketFanOutProjection.cs`

- [ ] **Step 1: Change constructor from `IShadowEventBroadcaster` to `IEventBroadcaster`.**

Edit `src/WeaveFleet.Application/Projections/WebSocketFanOutProjection.cs`:

```csharp
public sealed class WebSocketFanOutProjection : IProjection<HarnessEvent>
{
    private readonly IEventBroadcaster _broadcaster;
    public WebSocketFanOutProjection(IEventBroadcaster broadcaster) => _broadcaster = broadcaster;
    // ...
}
```

### Task 5.2: Flip `EphemeralEventRelayService` to production broadcaster

**Files:**
- Modify: `src/WeaveFleet.Infrastructure/Nats/EphemeralEventRelayService.cs`

- [ ] **Step 1: Change dependency from `IShadowEventBroadcaster` to `IEventBroadcaster`.**

### Task 5.3: Remove in-relay broadcaster call for ephemeral events

**Files:**
- Modify: `src/WeaveFleet.Infrastructure/Services/HarnessEventRelay.cs`

- [ ] **Step 1: Delete the `_broadcaster.BroadcastAsync(targetTopic, evt.Type, payload, sessionUserId, ct)` call at line 201 and the surrounding ephemeral gating (lines 189-201).**

- [ ] **Step 2: Decide the fate of activity-status broadcasting (lines 206-216).**

`ParseActivityStatus` emits on the `"sessions"` topic (not `"session:{id}"`) to drive the sidebar activity stream. Today this runs inline in the relay; after cutover, the right owner is a new `ActivityStatusProjection` that subscribes to ephemeral `session.status` / `session.idle` events. Move the logic there. Keep `SessionActivityTracker.Update` inside `EphemeralEventRelayService` (or the new projection) so the in-memory state for "initial snapshot on subscribe" still works.

Implementation sketch:

- Create `src/WeaveFleet.Infrastructure/Nats/ActivityStatusEphemeralHandler.cs` (invoked inside `EphemeralEventRelayService.ExecuteAsync` alongside the fan-out) that, on `session.status` / `session.idle`, calls `SessionActivityTracker.Update` and broadcasts on the `"sessions"` topic.

- [ ] **Step 3: Rebuild and run all tests.**

```bash
dotnet build --nologo
dotnet test --nologo
```
Expected: green. WebSocket clients now receive durable events exclusively via the projection and ephemeral events exclusively via the core NATS subscriber.

- [ ] **Step 4: Commit.**

```bash
git add src/WeaveFleet.Infrastructure/Services/HarnessEventRelay.cs src/WeaveFleet.Application/Projections/WebSocketFanOutProjection.cs src/WeaveFleet.Infrastructure/Nats/EphemeralEventRelayService.cs src/WeaveFleet.Infrastructure/Nats/ActivityStatusEphemeralHandler.cs
git commit -m "refactor: broadcast cutover — relay publishes to NATS only; projection + ephemeral service drive the broadcaster"
```

### Task 5.4: Retire the dual-write publish (now the only path)

No-op: after Task 5.3 the publish call in `HarnessEventRelay.PumpAsync` is no longer "dual-write" — it is the sole write. Just rename the internal log message `EventPublishFailed` → `EventPublishFailed` (unchanged) and drop any stale XML-doc comments referencing the compatibility window.

### Task 5.5: Outbox review (follow-up, not blocking)

The existing outbox path (`OutboxDispatchBackgroundService` → `InProcessOutboxDispatcher` → `IEventBroadcaster`) was serving as the durable-event fan-out mechanism. After Phase 5, durable events reach the broadcaster via `WebSocketFanOutProjection` instead. The outbox writes still happen inside `HarnessEventPersistenceService`, but the dispatcher produces a duplicate broadcast. Three options, pick one and file a follow-up plan:

1. Keep the outbox for crash-recovery redundancy (both paths feed the broadcaster — dedup by sequence id in the broadcaster).
2. Stop the outbox dispatcher but keep the outbox table for archival.
3. Remove the outbox entirely once NATS replay gives us equivalent durability.

**Do not do this in this plan.** Add a checklist line in `.weave/learnings/nats-substrate-dependency-audit.md` noting the decision owed.

---

## Phase 6 — Managed-cloud tenant wiring (gated)

Do not begin until managed-cloud rollout begins. Scope recorded here so it is not rediscovered from the design doc.

### Task 6.1: Resolve `TenantPrefix` per-workspace

- [ ] **Step 1:** Replace `NatsOptions.TenantPrefix = "tenant.default"` with a per-request prefix resolved from the authenticated workspace. `NatsNamingStrategy` accepts an `ITenantContext` (new) whose default implementation returns `"tenant.default"` in local/self-hosted modes.

- [ ] **Step 2:** Wire the context in `NatsEventPublisher` (publish path) and in `ProjectionListener`'s subject filter (consumer path — one consumer per tenant, or a tenant-wildcard consumer that reads tenant from the subject and validates against the consumer's assigned tenant).

- [ ] **Step 3:** Integration test: two synthetic workspaces' events do not cross-leak.

### Task 6.2: Application-layer authz at WebSocket subscribe

- [ ] **Step 1:** In `WebSocketEndpoints.PumpEventsAsync` (line 200+), assert that every incoming session topic is owned by the authenticated user. Today's broadcaster already scopes by `subscriberUserId` — verify this still holds once events come from the NATS projection.

- [ ] **Step 2:** Test: a second user subscribed to `session:{other-user-session}` receives no events.

### Task 6.3: Cloud creds wiring

- [ ] **Step 1:** Provision a NATS creds file per deployment. Load via `NatsOptions.CredsFile`. Verify `NatsServerHostedService` stays a no-op and the external-broker URL is used.

---

## Verification (final)

- [ ] Full unit suite: `dotnet test`
- [ ] `NatsEventSubstrateEndToEndTests` pass
- [ ] Launch Fleet with default config — embedded `nats-server` spawns, stream exists under `./data/nats/jetstream/`, restart is idempotent.
- [ ] Launch Fleet with `Fleet:Nats:ExternalUrl=nats://localhost:4222` (operator-launched `nats-server` container) — embedded skipped, stream created on external broker.
- [ ] Run a real OpenCode session end-to-end and verify messages land in SQLite via projection and appear on a WS client via broadcaster. (Manual — do not conflate with automated verification.)
- [ ] Reconcile against the design's pre-flight checklist (`docs/nats-event-substrate-design.md` § "Pre-flight Checklist") — every item addressed or deferred with a follow-up.
- [ ] Reconcile against `.weave/plans/nats-event-substrate-baseline.md` — no new failing tests beyond the pre-existing `SkillEndpointPathTraversalTests` entry.

---

## Self-review notes

- **Spec coverage:** every design decision (1–11) is represented. Decision 1 (scope = harness events only) is respected: `DelegationService` / `SessionOrchestrator` direct-broadcast path is untouched. Decision 5 (per-session serial publish with awaited PubAck) is in `NatsEventPublisher.PublishDurableAsync`. Decision 10 (interest retention + MaxAge) is in `NatsStreamInitializer`. Decision 6 (embedded server per-RID) is Task 1.0b + 1.6. Decision 8 (per-node projection + ephemeral subscriber) is Tasks 3.1 / 3.2 / 3.3. Decision 3 (wire-format = existing `HarnessEvent`) is preserved throughout. Cutover Timing section is addressed by Tasks 0.2 / 0.3 audits + Task 4.1 Step 3 (merge before publish).
- **No placeholders:** each step has exact file paths, code blocks, and commands.
- **Type consistency:** `IProjection<T>.HandleAsync(T, ProjectionContext, CancellationToken)`, `IEventPublisher.PublishAsync(HarnessEvent, EventPublishContext, CancellationToken)`, `IHarnessEventPersister.HandleAsync(string, string, HarnessEvent, CancellationToken)` are used consistently across tasks.
- **Known weaknesses:**
  - Task 1.8's DI wiring for `NatsConnection` uses a factory that requires `NatsServerHostedService` to have started — the host's startup-order guarantee is that hosted services start in registration order. Verify at runtime during Task 2.4 that this holds; if not, introduce a `Lazy<INatsConnection>` that resolves on first use.
  - Task 3.1's "merge deltas before publish" is the highest-risk refactor. Consider splitting it into its own mini-plan before executing Phase 4.
  - Task 3.4's `NatsEnabledFactory` currently overrides `DatabasePath` but does not override `AnalyticsDatabasePath`; under a parallel `dotnet test` run the shared analytics DB can cause `.deps.json` lock collisions (shouldly-assertion learning). Add an explicit `Fleet:AnalyticsDatabasePath` override in `ConfigureWebHost` before the test is enabled in CI.

---

---

## Post-Review Addendum (independent review against design + security)

A fresh independent review against `docs/nats-event-substrate-design.md` and a security pass surfaced the following. Entries are grouped by severity. Address blockers before starting execution; address should-fixes inline during the named task; nits before merging.

### Blockers — must be resolved before any task runs

**B1. `ack.EnsureSuccess()` is not an API on NATS.Net 2.x `PubAckResponse`.** Task 1.5 Step 3 (`NatsEventPublisher.PublishDurableAsync`) calls `ack.EnsureSuccess()` which will not compile. Replace with:

```csharp
var ack = await _js.PublishAsync(subject, payload, headers: headers,
    opts: new NatsJSPubOpts { MsgId = msgId }, cancellationToken: ct).ConfigureAwait(false);
if (ack.Error is not null)
    throw new NatsJSApiException(ack.Error);
if (ack.Duplicate)
    _metrics.RecordPublish(routing: "durable", eventType: evt.Type, tenant: _options.TenantPrefix, result: "duplicate");
```

Verify exact property names against the installed package's `PubAckResponse` before coding.

**B2. Subject injection via unsanitized `projectId` / `sessionId`.** `NatsNamingStrategy.DurableSubject` (Task 1.3 Step 3) interpolates ids directly into subjects. NATS uses `.`, `*`, `>` as structural tokens; a project id containing any of these breaks the hierarchy and can route to subscribers that should not receive the event. `ProjectId` is settable via the project-creation API, so this is a cross-project data leak vector, not just a bug. Fix in Task 1.3:

1. Add a private `ValidateSegment(string segment, string name)` method that throws `ArgumentException` on any `.`, `*`, `>`, whitespace, or empty value.
2. Call it inside `DurableSubject` / `EphemeralSubject` before interpolation.
3. Add unit test `DurableSubject_rejectsDotsInProjectId` (and `*`, `>`, whitespace) covering positive and negative cases.
4. Decide and document: either enforce UUID-only ids at the project/session creation layer, or centralize sanitization here. Do both for defence in depth.

**B3. `HandleShadowAsync` implementation is described but never specified.** Task 3.1 Step 2 declares the interface method; Step 3 proposes "a parallel `HarnessEventShadowPersistenceService`" but never shows its body. Phase 3's shadow diff harness cannot verify anything without it. Add an explicit Task 3.1.5:

- Create `src/WeaveFleet.Infrastructure/Services/HarnessEventShadowPersistenceService.cs` that mirrors `HarnessEventPersistenceService`'s routing switch but routes writes to `IShadowMessageRepository` / `IShadowSessionRepository` and emits **no** outbox entries (WS fan-out goes through `WebSocketFanOutProjection` → shadow broadcaster).
- `HarnessEventPersistenceService.HandleShadowAsync` delegates to it.
- Unit test: feed each durable `EventType` and assert the shadow repos receive the same call sequence as production repos get in `HarnessEventPersistenceServiceTests`.

### Should-fix — address during the named task

**S1. `Nats-Msg-Id` format does not match design.** Design §"What goes where" specifies `{sessionId}:{seq}`. Plan uses `{FleetSessionId}:{timestamp}:{guid}`. Change Task 1.5 Step 3 to use a per-session monotonic counter held in the relay pump (it is already single-producer per session) and pass it through `EventPublishContext` as an additional `long Sequence` field. Without this the JetStream 2-minute dedup window behaves unpredictably under retry.

**S2. `traceparent` / `tracestate` headers never wired.** Design §"What goes where" and "Observable from day one" mandate end-to-end trace propagation across publish/consume. Neither `NatsEventPublisher` (Task 1.5) nor `ProjectionListener` (Task 2.2) touch `Activity.Current`. Add to publisher: `DistributedContextPropagator.Current.Inject(Activity.Current, headers, (h, k, v) => ((NatsHeaders)h!)[k] = v)`. Add to listener: extract on consume before `HandleAsync`, and start a child activity named `nats.consume`.

**S3. `DurableConsumerName` is shared across nodes.** Task 1.3 Step 3 returns `{streamName}-{projection}`. The design §"Client read path" names the WS fan-out consumer `fleet-ws-fanout-durable-{nodeId}`. Without a `{nodeId}` suffix, two Fleet nodes share the same durable consumer (competing consumers) instead of each getting a copy — breaking the design's per-node fan-out assumption. Accept a `NodeId` option (default `Environment.MachineName`) and suffix the consumer name with it for `WebSocketFanOutProjection`. `MessagePersistenceProjection` is node-agnostic (single writer to SQLite) and should **not** be suffixed — one consumer across all nodes is correct.

**S4. Durable consumers must be pre-created.** `ProjectionListener.RunAsync` creates the consumer lazily on first invocation (Task 2.2 Step 3). Interest-based retention (Decision 10) requires **every** registered consumer to ack before GC. A publish that lands before the listener starts can be GC'd if MaxAge expires before the consumer is registered. Fix: extend `NatsStreamInitializer` to iterate over `ProjectionRegistry.ProjectionTypes` and call `CreateOrUpdateConsumerAsync` for each during startup, *before* the relay can publish.

**S5. Per-mode MaxAge defaults.** Design §"Hosting-Mode Mechanics" specifies local `24h`, self-hosted `7d`, managed cloud `30d`. Plan `NatsOptions.MaxAge = 24h` for all modes. Add a `NatsOptionsConfigurator : IConfigureOptions<NatsOptions>` (or post-binding fixup) that selects the default from `FleetOptions.Cloud.Enabled` + `Auth.Enabled`. User-provided config still wins.

**S6. Task 4.1 Step 3 "merge before publish" is under-specified.** Lift it to a dedicated Task 4.0 with:
1. Extract a static `MessagePersistenceService.MergeBufferedDeltasIntoEvent(HarnessEvent evt, IReadOnlyDictionary<(string msgId, string partId), string> buffer) → HarnessEvent` (pure function, fully testable).
2. TDD tests covering: `message.created` with no buffered deltas, `message.updated` with deltas for one part, `message.updated` with deltas for multiple parts, `message.part.updated` with a delta for the same partId, no-op for non-mergeable event types.
3. Integration test: JetStream payload for `message.updated` already contains merged text (no re-merge needed in the projection).

**S7. Phase 2 / Phase 3 ordering bug inside this plan.** Task 2.4's `NatsEventSubstrateEndToEndTests` depends on `NoOpProjection`; Task 3.1 replaces it with `MessagePersistenceProjection` (shadow). After Task 3.1, the e2e test runs against the shadow projection and requires shadow tables to exist — but Task 2.4 was written before shadow schema exists. Either:
   (a) move the e2e assertion to also check `NoOpProjection` log output and only run Task 2.4's test *at* Phase 2 (before the 3.1 swap); or
   (b) add a second e2e test under Task 3.2 that does the shadow-tables assertion.

**S8. `EphemeralEventRelayService` registered before its dependency exists.** `NatsServiceCollectionExtensions.AddEventStore` (Task 1.8) registers `EphemeralEventRelayService` as a hosted service. `IShadowEventBroadcaster` does not exist until Task 3.2. Move the `AddHostedService<EphemeralEventRelayService>()` line out of `AddEventStore` into `DependencyInjection.cs` next to the `IShadowEventBroadcaster` registration.

**S9. Bundled `nats-server` binaries lack integrity verification.** Task 1.0b drops multi-MB binaries into the repo with no SHA pinning. A compromised or swapped binary executes on every developer machine. Add:
1. `build/nats-server-checksums.txt` pinning SHA-256 of each RID's binary.
2. An MSBuild target in `Directory.Build.props` (or a pre-build PowerShell/bash script) that verifies each bundled binary against the checksum file. Build fails on mismatch.
3. Consider Git LFS for `Nats/EmbeddedNatsServer/Binaries/**` to keep repo size reasonable; alternatively download at build time from a pinned HTTPS URL + hash.

**S10. `JsonSerializer.Deserialize<HarnessEvent>(msg.Data!)` has no payload size bound.** Both `ProjectionListener.RunAsync` (Task 2.2) and `EphemeralEventRelayService.ExecuteAsync` (Task 3.3) deserialize without a size guard. An attacker or a bug on the publish side could OOM the consumer. Fix:
1. Set `NatsOpts.MaxMsgSize` (or equivalent) when constructing the connection in `AddEventStore`.
2. Add an explicit `if (msg.Data.Length > _maxPayloadBytes) { await msg.TermAsync(); continue; }` before deserialize.

**S11. Malformed payloads trigger infinite NAK loop.** `ProjectionListener.RunAsync` NAKs on any exception — including a malformed JSON payload that will never succeed. JetStream redelivers forever, stalling the projection. Fix: count per-message deliveries (available on `msg.Metadata.NumDelivered`) and call `TermAsync` after N attempts (default 5). Emit a `poison_message` metric and warn-level log.

**S12. WebSocket authz is asserted but not tested.** Decision 7 + Decision 11 rest on "application-layer authz at WebSocket subscribe" — meaning even if a publisher forges `x-fleet-user-id`, the WS endpoint filters events by the authenticated subscriber's identity. Add to Task 6.2: a test `WebSocketEndpoints_filtersEventsByAuthenticatedUser_notPublisherHeader` that publishes with a forged `x-fleet-user-id` and asserts only the legitimately-authenticated WS subscriber receives it.

**S13. `NatsOptions.CredsFile` must not be logged.** `NatsServerHostedService` currently logs `ResolvedUrl`; add an explicit negative assertion test that no log line ever contains `CredsFile`'s path (scan formatter args). Apply the same discipline to any future diagnostic endpoints.

### Nits

**N1. Unknown-event-type drop should log at warn level**, not just metric. Update Task 1.5.

**N2. `tenant` metric dimension has unbounded cardinality under managed cloud.** Flag in Task 1.2's metric definitions: under managed-cloud rollout, replace per-tenant label with a `tenant_class` (default / managed / selfhosted) or hash-bucket. Keep per-tenant for local/self-hosted where cardinality is 1.

**N3. `HarnessEvent.SessionId` vs `FleetSessionId` distinction not documented.** Add an XML-doc paragraph in `NatsNamingStrategy` clarifying that `{sid}` in the subject is the Fleet session id (globally unique), not the harness-provider session id.

**N4. Design doc has an internal inconsistency.** Managed-cloud §"Auth" says per-tenant NATS user credentials; Decision 11 says single deployment credential. Decision 11 is authoritative; flag for a follow-up design-doc patch rather than a plan change.

**N5. Activity-status ownership under `EphemeralEventRelayService` isn't in the design.** Task 5.3 Step 2 introduces `ActivityStatusEphemeralHandler` on implementer discretion. Request a short paragraph in the design doc ratifying it so future readers don't treat it as accidental.

**N6. Use `Git LFS` or `.gitattributes` for the bundled binaries** (see S9). Avoids blowing up clone times and diffs.

### Execution note

Given the blocker count (3) and should-fix count (13), the plan should be re-reviewed once these addendum items are incorporated before Phase 1 starts. The skeleton is sound; the gaps are in specific implementation details that this review was specifically intended to surface.

---

**Plan complete.** Saved to `.weave/plans/nats-event-substrate.md`. Review addendum appended. Resume tomorrow to decide which execution mode (subagent-driven vs inline) to use and in what order to address the addendum items.
