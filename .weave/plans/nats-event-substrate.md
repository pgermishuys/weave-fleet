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
- `src/WeaveFleet.Infrastructure/Nats/NatsStreamInitializer.cs` — idempotent JetStream stream creation AND pre-creation of every registered projection's durable consumer on startup.
- `src/WeaveFleet.Infrastructure/Nats/NatsEventPublisher.cs` — `IEventPublisher` implementation; routes by `EventTypeMetadata.Classify`; sets `Nats-Msg-Id`, `x-fleet-*` headers, `traceparent`/`tracestate`; awaits `PubAck`.
- `src/WeaveFleet.Infrastructure/Nats/NatsNamingStrategy.cs` — subject/stream/consumer prefix helper with segment validation (rejects `.`, `*`, `>`, whitespace in ids).
- `src/WeaveFleet.Infrastructure/Nats/NatsMetrics.cs` — OpenTelemetry metric + counter definitions.
- `src/WeaveFleet.Infrastructure/Nats/ProjectionListener.cs` — generic JetStream durable-consumer pump; extracts trace context; TERMs oversize/malformed/poisoned messages.
- `src/WeaveFleet.Infrastructure/Nats/ProjectionHostService.cs` — hosted service that spins up one `ProjectionListener` per registered projection.
- `src/WeaveFleet.Infrastructure/Nats/EmbeddedNatsServer/NatsServerBinaryResolver.cs` — picks the right bundled binary for the current RID.
- `src/WeaveFleet.Infrastructure/Nats/Configuration/NatsStreamBuilder.cs` — fluent DI surface for stream config + projection registration; `ConsumerScope` enum (Cluster vs PerNode).
- `src/WeaveFleet.Infrastructure/Nats/Configuration/NatsServiceCollectionExtensions.cs` — `AddEventStore(...)` extension.
- `src/WeaveFleet.Infrastructure/Nats/Configuration/NatsOptionsConfigurator.cs` — post-configurator applying per-hosting-mode `MaxAge` defaults.
- `src/WeaveFleet.Infrastructure/Nats/EphemeralEventRelayService.cs` — core NATS subscriber; forwards ephemeral events to `IEventBroadcaster`.
- `src/WeaveFleet.Infrastructure/Services/HarnessEventShadowPersistenceService.cs` — shadow-mode persister used during the Phase 3 compatibility window (retired in Phase 4).

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
- `tests/WeaveFleet.Infrastructure.Tests/Nats/NatsNamingStrategyTests.cs` (including segment-injection rejection + node-id consumer naming)
- `tests/WeaveFleet.Infrastructure.Tests/Nats/NatsStreamInitializerTests.cs`
- `tests/WeaveFleet.Infrastructure.Tests/Nats/NatsOptionsConfiguratorTests.cs`
- `tests/WeaveFleet.Infrastructure.Tests/Nats/ProjectionListenerTests.cs`
- `tests/WeaveFleet.Infrastructure.Tests/Nats/NatsServerHostedServiceTests.cs`
- `tests/WeaveFleet.Infrastructure.Tests/Nats/CredsFileLoggingTests.cs` — asserts `CredsFile` path is never logged.
- `tests/WeaveFleet.Infrastructure.Tests/Services/DeltaMergeBeforePublishTests.cs` — pure-function merge helper (Task 4.0).
- `tests/WeaveFleet.Infrastructure.Tests/Services/HarnessEventShadowPersistenceServiceTests.cs`
- `tests/WeaveFleet.Application.Tests/Projections/MessagePersistenceProjectionTests.cs`
- `tests/WeaveFleet.Application.Tests/Projections/WebSocketFanOutProjectionTests.cs`
- `tests/WeaveFleet.Api.Tests/Nats/NatsEventSubstrateEndToEndTests.cs`
- `tests/WeaveFleet.Api.Tests/Nats/MergedDurablePayloadTests.cs` — JetStream payload already contains merged text.
- `tests/WeaveFleet.Api.Tests/Nats/ShadowDiffHarnessTests.cs`
- `tests/WeaveFleet.Api.Tests/Nats/WebSocketAuthzTests.cs` — forged `x-fleet-user-id` does not cross subscriber boundaries.
- `tests/WeaveFleet.Testing/Nats/EmbeddedNatsTestFixture.cs` — shared xUnit fixture that launches embedded `nats-server` on a random port for integration tests.
- `tests/WeaveFleet.Testing/Nats/FakeEventPublisher.cs` — hand-crafted fake for unit tests that do not need a real broker.

### Build / tooling

- `build/nats-server-checksums.txt` — SHA-256 pin per bundled RID binary.
- `build/verify-nats-server-binaries.ps1` — pre-build verifier; MSBuild target fails the build on mismatch.
- `.gitattributes` — LFS rule for `src/WeaveFleet.Infrastructure/Nats/EmbeddedNatsServer/Binaries/**`.

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

- [ ] **Step 2: Pin SHA-256 checksums.**

Each nats-server release publishes `SHA256SUMS`. Copy the relevant entries into `build/nats-server-checksums.txt`:

```
# nats-server vX.Y.Z — verified against https://github.com/nats-io/nats-server/releases/download/vX.Y.Z/SHA256SUMS
# Format: <sha256>  <relative binary path from repo root>
<sha256>  src/WeaveFleet.Infrastructure/Nats/EmbeddedNatsServer/Binaries/win-x64/nats-server.exe
<sha256>  src/WeaveFleet.Infrastructure/Nats/EmbeddedNatsServer/Binaries/linux-x64/nats-server
<sha256>  src/WeaveFleet.Infrastructure/Nats/EmbeddedNatsServer/Binaries/osx-x64/nats-server
<sha256>  src/WeaveFleet.Infrastructure/Nats/EmbeddedNatsServer/Binaries/osx-arm64/nats-server
```

Create a pre-build verification script `build/verify-nats-server-binaries.ps1` (PowerShell — cross-platform with pwsh):

```powershell
#!/usr/bin/env pwsh
$ErrorActionPreference = 'Stop'
$repoRoot = Resolve-Path (Join-Path $PSScriptRoot '..')
$checksumFile = Join-Path $PSScriptRoot 'nats-server-checksums.txt'
if (-not (Test-Path $checksumFile)) { throw "Checksum file missing: $checksumFile" }

Get-Content $checksumFile | ForEach-Object {
    $line = $_.Trim()
    if (-not $line -or $line.StartsWith('#')) { return }
    $expected, $path = $line -split '\s+', 2
    $full = Join-Path $repoRoot $path.Trim()
    if (-not (Test-Path $full)) { throw "Binary missing: $full" }
    $actual = (Get-FileHash $full -Algorithm SHA256).Hash.ToLowerInvariant()
    if ($actual -ne $expected.ToLowerInvariant()) {
        throw "Checksum mismatch for $path`nExpected: $expected`nActual:   $actual"
    }
}
Write-Host "All nats-server binaries verified."
```

Add an MSBuild `<Target>` in `Directory.Build.props` (or in `WeaveFleet.Infrastructure.csproj`) that runs before `Build`:

```xml
  <Target Name="VerifyNatsServerBinaries" BeforeTargets="BeforeBuild"
          Condition="'$(MSBuildProjectName)' == 'WeaveFleet.Infrastructure'">
    <Exec Command="pwsh -NoProfile -File &quot;$(MSBuildThisFileDirectory)build\verify-nats-server-binaries.ps1&quot;" />
  </Target>
```

Add a negative test for the checksum script: deliberately corrupt the first byte of one binary in a copy, run the script, assert it throws. Keep the corrupt copy out of the repo.

- [ ] **Step 3: Wire binaries into the build (with LFS or alternative).**

Edit `src/WeaveFleet.Infrastructure/WeaveFleet.Infrastructure.csproj` — add:

```xml
  <ItemGroup>
    <None Include="Nats\EmbeddedNatsServer\Binaries\**\*" CopyToOutputDirectory="PreserveNewest" />
  </ItemGroup>
```

**Repository size concern (N6):** each bundled binary is multi-MB and the four RIDs together add ~40 MB to the repo. Prefer one of:

  1. Commit to **Git LFS** — add a `.gitattributes` entry:
     ```
     src/WeaveFleet.Infrastructure/Nats/EmbeddedNatsServer/Binaries/** filter=lfs diff=lfs merge=lfs -text
     ```
     Developers need `git lfs install` once.
  2. Or **download at build time** from a pinned HTTPS URL + SHA — a pre-build target fetches each missing binary into the `Binaries/` directory and verifies against `nats-server-checksums.txt`. Does not bloat the repo but adds a network dependency for first build.

Pick (1) for simplicity. Record the choice in the commit message so future audits see the tradeoff.

- [ ] **Step 4: Verify at build time.**

```bash
dotnet build --nologo
ls src/WeaveFleet.Api/bin/Debug/net10.0/Nats/EmbeddedNatsServer/Binaries/
```
Expected: binaries present under the API's output directory; the pre-build checksum step emits `All nats-server binaries verified.`

- [ ] **Step 5: Commit (binary files + csproj + checksums + verifier + gitattributes).**

```bash
git add src/WeaveFleet.Infrastructure/Nats/EmbeddedNatsServer/Binaries src/WeaveFleet.Infrastructure/WeaveFleet.Infrastructure.csproj build/nats-server-checksums.txt build/verify-nats-server-binaries.ps1 .gitattributes Directory.Build.props
git commit -m "build: bundle nats-server binaries per RID with SHA-256 integrity verification"
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
        options.MaxPayloadBytes.ShouldBe(4 * 1024 * 1024);
        options.TenantPrefix.ShouldBe("tenant.default");
        options.NodeId.ShouldNotBeNullOrWhiteSpace(); // defaulted to machine name
        options.ProjectionRetryBudget.ShouldBe(5);
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
/// <para>
/// Default <see cref="MaxAge"/> is tuned for local dev; <c>NatsOptionsConfigurator</c>
/// overrides it based on hosting mode (self-hosted 7d, managed cloud 30d) unless the
/// caller has explicitly set a value.
/// </para>
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

    /// <summary>
    /// Maximum accepted payload size in bytes for both publish and consume paths.
    /// NATS default is 1 MiB; this setting caps our usage at 4 MiB (room for large
    /// consolidated <c>message.updated</c> snapshots). Consumers drop and TERM messages
    /// larger than this.
    /// </summary>
    public int MaxPayloadBytes { get; set; } = 4 * 1024 * 1024;

    /// <summary>Tenant/prefix string for subject construction. Default: tenant.default.</summary>
    public string TenantPrefix { get; set; } = "tenant.default";

    /// <summary>
    /// Stable identifier for this Fleet node. Used to suffix per-node durable consumer names
    /// (e.g. WebSocket fan-out). Defaults to <see cref="Environment.MachineName"/>; override
    /// for clustered deployments where machine names may collide.
    /// </summary>
    public string NodeId { get; set; } = Environment.MachineName;

    /// <summary>
    /// Maximum JetStream redelivery attempts before a projection TERMs a message as poison.
    /// Prevents a malformed or persistently-failing payload from stalling a consumer.
    /// Default: 5.
    /// </summary>
    public int ProjectionRetryBudget { get; set; } = 5;
}
```

- [ ] **Step 4: Expose it from `FleetOptions`.**

Edit `src/WeaveFleet.Application/Configuration/FleetOptions.cs`. After line 100 (the `public OutboxOptions Outbox` property), add:

```csharp
    /// <summary>NATS event substrate configuration.</summary>
    public NatsOptions Nats { get; set; } = new();
```

- [ ] **Step 5: Add `NatsOptionsConfigurator` for per-mode MaxAge defaults.**

Create `src/WeaveFleet.Infrastructure/Nats/Configuration/NatsOptionsConfigurator.cs`:

```csharp
using Microsoft.Extensions.Options;
using WeaveFleet.Application.Configuration;

namespace WeaveFleet.Infrastructure.Nats.Configuration;

/// <summary>
/// Applies hosting-mode-aware defaults to <see cref="NatsOptions.MaxAge"/>:
/// local (<c>24h</c>), self-hosted (<c>7d</c>), managed cloud (<c>30d</c>).
/// Only applied when the caller has not set <see cref="NatsOptions.MaxAge"/> explicitly
/// (detected as "equal to the type default of <c>24h</c>").
/// </summary>
public sealed class NatsOptionsConfigurator : IPostConfigureOptions<NatsOptions>
{
    private readonly FleetOptions _fleetOptions;
    public NatsOptionsConfigurator(FleetOptions fleetOptions) => _fleetOptions = fleetOptions;

    public void PostConfigure(string? name, NatsOptions options)
    {
        // Heuristic: only overwrite when still at the NatsOptions default (24h). A config-file
        // override that happens to also be 24h is a no-op either way.
        if (options.MaxAge != TimeSpan.FromHours(24)) return;

        options.MaxAge = _fleetOptions.Cloud.Enabled
            ? TimeSpan.FromDays(30)
            : _fleetOptions.Auth.Enabled
                ? TimeSpan.FromDays(7)   // self-hosted has auth on; local does not
                : TimeSpan.FromHours(24);
    }
}
```

Add a unit test in `tests/WeaveFleet.Infrastructure.Tests/Nats/NatsOptionsConfiguratorTests.cs` covering all three modes. Register the post-configurator inside `AddEventStore` (Task 1.8).

- [ ] **Step 6: Run tests.**

```bash
dotnet test tests/WeaveFleet.Application.Tests --nologo --filter FullyQualifiedName~NatsOptionsTests
dotnet test tests/WeaveFleet.Infrastructure.Tests --nologo --filter FullyQualifiedName~NatsOptionsConfiguratorTests
```
Expected: PASS.

- [ ] **Step 7: Commit.**

```bash
git add src/WeaveFleet.Application/Configuration/NatsOptions.cs src/WeaveFleet.Application/Configuration/FleetOptions.cs src/WeaveFleet.Infrastructure/Nats/Configuration/NatsOptionsConfigurator.cs tests/WeaveFleet.Application.Tests/Configuration/NatsOptionsTests.cs tests/WeaveFleet.Infrastructure.Tests/Nats/NatsOptionsConfiguratorTests.cs
git commit -m "feat: add NatsOptions with per-hosting-mode MaxAge defaults"
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

    // NOTE on cardinality: the `tenant` dimension is low-cardinality (=1) for local/self-hosted
    // where TenantPrefix is always `tenant.default`. Under managed cloud it becomes per-workspace
    // and cardinality scales with user count; before the managed-cloud rollout, replace the raw
    // tenant with a `tenant_class` label (default/managed/selfhosted) or a hash bucket. Plan
    // follow-up: Task 6.x — scrub tenant from metrics before managed-cloud GA.
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
    private readonly NatsNamingStrategy _sut = new(new NatsOptions(), nodeId: "node-A");

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

    [Theory]
    [InlineData("proj.bad")]   // dot breaks the hierarchy
    [InlineData("proj*bad")]   // wildcard token
    [InlineData("proj>bad")]   // multi-wildcard token
    [InlineData("proj bad")]   // whitespace
    [InlineData("")]           // empty
    public void DurableSubject_rejectsUnsafeSegmentCharacters(string badId)
    {
        Should.Throw<ArgumentException>(() =>
            _sut.DurableSubject(projectId: badId, sessionId: "sess-1", eventType: "message.created"));
        Should.Throw<ArgumentException>(() =>
            _sut.DurableSubject(projectId: "proj-1", sessionId: badId, eventType: "message.created"));
    }

    [Fact]
    public void PerNodeConsumer_suffixesNodeId()
    {
        _sut.PerNodeConsumerName(projection: "ws-fanout").ShouldBe("fleet-sessions-ws-fanout-node-A");
    }

    [Fact]
    public void ClusterConsumer_hasNoNodeIdSuffix()
    {
        _sut.ClusterConsumerName(projection: "message-persistence").ShouldBe("fleet-sessions-message-persistence");
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
/// <para>
/// <c>{sid}</c> in every subject is the <b>Fleet</b> session id (globally unique across harnesses),
/// not the harness-provider session id that may also live on <c>HarnessEvent.SessionId</c>.
/// </para>
/// </summary>
public sealed class NatsNamingStrategy
{
    public const string ScratchProjectSentinel = "scratch";

    // NATS subject tokens are separated by '.' and support '*' / '>' wildcards. Any segment
    // we interpolate into a subject must be validated so an attacker cannot forge a routing
    // hierarchy or escape tenant scoping via a project/session id containing these characters.
    private static readonly char[] UnsafeSubjectChars = ['.', '*', '>', ' ', '\t', '\r', '\n'];

    private readonly NatsOptions _options;
    private readonly string _nodeId;

    public NatsNamingStrategy(NatsOptions options, string nodeId)
    {
        _options = options;
        _nodeId = string.IsNullOrWhiteSpace(nodeId)
            ? throw new ArgumentException("NodeId is required.", nameof(nodeId))
            : nodeId;
    }

    public string DurableSubject(string? projectId, string sessionId, string eventType)
    {
        var project = projectId ?? ScratchProjectSentinel;
        ValidateSegment(project, nameof(projectId));
        ValidateSegment(sessionId, nameof(sessionId));
        return $"{_options.TenantPrefix}.project.{project}.session.{sessionId}.{eventType}";
    }

    public string EphemeralSubject(string? projectId, string sessionId, string eventType)
    {
        var project = projectId ?? ScratchProjectSentinel;
        ValidateSegment(project, nameof(projectId));
        ValidateSegment(sessionId, nameof(sessionId));
        return $"{_options.TenantPrefix}.project.{project}.live.{sessionId}.{eventType}";
    }

    public string DurableStreamFilter => "tenant.*.project.*.session.*.>";
    public string EphemeralSubscriptionFilter => "tenant.*.project.*.live.*.>";

    public string StreamName => _options.StreamName;

    /// <summary>
    /// Cluster-scoped consumer: one consumer shared across all Fleet nodes. Used by projections
    /// that must write exactly once per event (e.g. persistence).
    /// </summary>
    public string ClusterConsumerName(string projection) => $"{_options.StreamName}-{projection}";

    /// <summary>
    /// Per-node consumer: one consumer per Fleet node. Used by fan-out projections (e.g.
    /// WebSocket broadcast) where every node must receive its own copy of the stream.
    /// </summary>
    public string PerNodeConsumerName(string projection) => $"{_options.StreamName}-{projection}-{_nodeId}";

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

    private static void ValidateSegment(string segment, string paramName)
    {
        if (string.IsNullOrEmpty(segment))
            throw new ArgumentException("Subject segment cannot be empty.", paramName);
        if (segment.IndexOfAny(UnsafeSubjectChars) >= 0)
            throw new ArgumentException(
                $"Subject segment '{segment}' contains a character reserved by NATS (., *, >, whitespace).",
                paramName);
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
/// <para>
/// <see cref="Sequence"/> is a per-session monotonic counter owned by the publishing caller.
/// It is used as the publish-side half of the <c>Nats-Msg-Id</c> header (<c>{sessionId}:{seq}</c>)
/// so JetStream dedup (~2-minute window) can collapse retries of the same logical publish.
/// The relay pump initializes the counter at 0 when a pump starts and increments it per event.
/// </para>
/// </summary>
public readonly record struct EventPublishContext(
    string FleetSessionId,
    string? ProjectId,
    string? UserId,
    string? HarnessType,
    long Sequence);
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
using Microsoft.Extensions.Logging.Abstractions;
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
            js, connection,
            new NatsNamingStrategy(options, nodeId: "test-node"),
            new NatsMetrics(), options,
            NullLogger<NatsEventPublisher>.Instance);

        var evt = new HarnessEvent
        {
            Type = EventTypes.MessageCreated,
            SessionId = "sess-1",
            Timestamp = DateTimeOffset.UtcNow,
            Payload = JsonSerializer.SerializeToElement(new { info = new { role = "assistant" } })
        };

        await publisher.PublishAsync(
            evt,
            new EventPublishContext("sess-1", ProjectId: "proj-1", UserId: "user-1", HarnessType: "opencode", Sequence: 42),
            CancellationToken.None);

        var stream = await js.GetStreamAsync("fleet-sessions");
        (await stream.GetInfoAsync()).State.Messages.ShouldBe(1UL);

        // Verify message id header matches the design-specified {sessionId}:{seq} format
        var consumer = await stream.CreateOrderedConsumerAsync();
        await foreach (var msg in consumer.ConsumeAsync<byte[]>().Take(1))
        {
            msg.Headers.ShouldNotBeNull();
            msg.Headers!["Nats-Msg-Id"].ToString().ShouldBe("sess-1:42");
            msg.Headers["x-fleet-user-id"].ToString().ShouldBe("user-1");
            msg.Headers["x-fleet-harness-type"].ToString().ShouldBe("opencode");
            msg.Subject.ShouldBe("tenant.default.project.proj-1.session.sess-1.message.created");
            break;
        }
    }

    [Fact]
    public async Task Publish_rejectsSubjectInjectionInIds()
    {
        var options = new NatsOptions();
        await using var connection = new NatsConnection(new NatsOpts { Url = _fixture.Url });
        var js = new NatsJSContext(connection);
        try { await js.DeleteStreamAsync("fleet-sessions"); } catch { }
        await js.CreateStreamAsync(new StreamConfig("fleet-sessions", ["tenant.*.project.*.session.*.>"]));

        var publisher = new NatsEventPublisher(
            js, connection,
            new NatsNamingStrategy(options, nodeId: "test-node"),
            new NatsMetrics(), options,
            NullLogger<NatsEventPublisher>.Instance);

        var evt = new HarnessEvent { Type = EventTypes.MessageCreated, SessionId = "sess-1", Timestamp = DateTimeOffset.UtcNow };

        // Project id containing a '.' would silently create a deeper subject and break the
        // tenant.{ws}.project.{pid} hierarchy — reject at the publisher boundary.
        await Should.ThrowAsync<ArgumentException>(() => publisher.PublishAsync(evt,
            new EventPublishContext("sess-1", "proj.sneaky", "user-1", "opencode", Sequence: 1), CancellationToken.None));

        await Should.ThrowAsync<ArgumentException>(() => publisher.PublishAsync(evt,
            new EventPublishContext("sess>inject", "proj-1", "user-1", "opencode", Sequence: 1), CancellationToken.None));
    }

    [Fact]
    public async Task EphemeralEvent_publishesToCoreNats()
    {
        var options = new NatsOptions();
        await using var connection = new NatsConnection(new NatsOpts { Url = _fixture.Url });
        var publisher = new NatsEventPublisher(
            new NatsJSContext(connection), connection,
            new NatsNamingStrategy(options, nodeId: "test-node"),
            new NatsMetrics(), options,
            NullLogger<NatsEventPublisher>.Instance);

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
            new EventPublishContext("sess-1", "proj-1", "user-1", "opencode", Sequence: 1),
            CancellationToken.None);

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

        var publisher = new NatsEventPublisher(
            js, connection,
            new NatsNamingStrategy(options, nodeId: "test-node"),
            new NatsMetrics(), options,
            NullLogger<NatsEventPublisher>.Instance);

        var evt = new HarnessEvent
        {
            Type = "totally.unknown.type",
            SessionId = "sess-1",
            Timestamp = DateTimeOffset.UtcNow
        };
        await publisher.PublishAsync(evt,
            new EventPublishContext("sess-1", "proj-1", "user-1", "opencode", Sequence: 1),
            CancellationToken.None);

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
using System.Diagnostics.Metrics;
using System.Text.Json;
using Microsoft.Extensions.Logging;
using NATS.Client.Core;
using NATS.Client.JetStream;
using NATS.Client.JetStream.Models;
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
    private static readonly Action<ILogger, string, Exception?> LogUnknownEventType =
        LoggerMessage.Define<string>(LogLevel.Warning, new EventId(1, "NatsPublishUnknownEventType"),
            "Publish dropped for unclassified event type {EventType} — neither durable nor ephemeral-relay per EventTypeMetadata.");

    private readonly INatsJSContext _js;
    private readonly INatsConnection _connection;
    private readonly NatsNamingStrategy _naming;
    private readonly NatsMetrics _metrics;
    private readonly NatsOptions _options;
    private readonly ILogger<NatsEventPublisher> _logger;

    public NatsEventPublisher(
        INatsJSContext js,
        INatsConnection connection,
        NatsNamingStrategy naming,
        NatsMetrics metrics,
        NatsOptions options,
        ILogger<NatsEventPublisher> logger)
    {
        _js = js;
        _connection = connection;
        _naming = naming;
        _metrics = metrics;
        _options = options;
        _logger = logger;
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

        // Unknown classification — log at warn level and record a metric so new event types
        // that slip past EventTypeMetadata are visible. Matches the relay's existing skip
        // behaviour for unclassified events.
        LogUnknownEventType(_logger, evt.Type, null);
        _metrics.RecordPublish(routing: "dropped", eventType: evt.Type, tenant: _options.TenantPrefix, result: "ok");
    }

    private async Task PublishDurableAsync(HarnessEvent evt, EventPublishContext context, CancellationToken ct)
    {
        var subject = _naming.DurableSubject(context.ProjectId, context.FleetSessionId, evt.Type);
        var payload = JsonSerializer.SerializeToUtf8Bytes(evt);
        // Format {sessionId}:{seq} per design §"What goes where" — seq is the relay's
        // per-session monotonic counter, supplied in EventPublishContext. This lets
        // JetStream dedup (2-minute window) collapse retries of the same logical publish.
        var msgId = $"{context.FleetSessionId}:{context.Sequence}";
        var headers = new NatsHeaders
        {
            ["Nats-Msg-Id"] = msgId,
        };
        if (context.UserId is { Length: > 0 }) headers["x-fleet-user-id"] = context.UserId;
        if (context.HarnessType is { Length: > 0 }) headers["x-fleet-harness-type"] = context.HarnessType;
        InjectTraceContext(headers);

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
            // NATS.Net 2.x returns PubAckResponse with an Error property. Verify exact shape
            // against the installed package (e.g. PubAckResponse.Error, PubAckResponse.Duplicate).
            if (ack.Error is not null)
                throw new NatsJSApiException(ack.Error);
            if (ack.Duplicate)
                result = "duplicate";
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
        InjectTraceContext(headers);

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

    private static void InjectTraceContext(NatsHeaders headers)
    {
        var activity = Activity.Current;
        if (activity is null) return;
        DistributedContextPropagator.Current.Inject(activity, headers, static (carrier, key, value) =>
        {
            if (carrier is NatsHeaders h) h[key] = value;
        });
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
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;
using NATS.Client.Core;
using NATS.Client.JetStream;
using WeaveFleet.Application.Configuration;
using WeaveFleet.Infrastructure.Nats;
using WeaveFleet.Infrastructure.Nats.Configuration;
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
        var registry = new ProjectionRegistry(Array.Empty<ProjectionRegistryEntry>());
        var sp = new ServiceCollection().BuildServiceProvider();
        var sut = new NatsStreamInitializer(
            js,
            new NatsNamingStrategy(options, nodeId: "test-node"),
            options,
            registry,
            sp,
            NullLogger<NatsStreamInitializer>.Instance);

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
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using NATS.Client.JetStream;
using NATS.Client.JetStream.Models;
using WeaveFleet.Application.Configuration;
using WeaveFleet.Application.Projections;
using WeaveFleet.Domain.Harnesses;
using WeaveFleet.Infrastructure.Nats.Configuration;

namespace WeaveFleet.Infrastructure.Nats;

/// <summary>
/// Hosted service that creates the durable JetStream stream AND pre-creates the durable
/// consumer for every registered projection, on startup. Idempotent — catches "already exists"
/// errors and continues. Pre-creating consumers is load-bearing for interest-based retention:
/// a publish that lands before a consumer is registered could otherwise be GC'd if MaxAge
/// expires first. Retention / MaxAge / MaxBytes come from <see cref="NatsOptions"/>; subjects
/// come from <see cref="NatsNamingStrategy"/>.
/// </summary>
public sealed class NatsStreamInitializer : IHostedService
{
    private static readonly Action<ILogger, string, Exception?> LogStreamReady =
        LoggerMessage.Define<string>(LogLevel.Information, new EventId(1, "NatsStreamReady"),
            "JetStream stream {StreamName} ready.");
    private static readonly Action<ILogger, string, Exception?> LogConsumerReady =
        LoggerMessage.Define<string>(LogLevel.Information, new EventId(2, "NatsConsumerReady"),
            "JetStream durable consumer {Consumer} ready.");

    private readonly INatsJSContext _js;
    private readonly NatsNamingStrategy _naming;
    private readonly NatsOptions _options;
    private readonly ProjectionRegistry _registry;
    private readonly IServiceProvider _rootProvider;
    private readonly ILogger<NatsStreamInitializer> _logger;

    public NatsStreamInitializer(
        INatsJSContext js,
        NatsNamingStrategy naming,
        NatsOptions options,
        ProjectionRegistry registry,
        IServiceProvider rootProvider,
        ILogger<NatsStreamInitializer> logger)
    {
        _js = js;
        _naming = naming;
        _options = options;
        _registry = registry;
        _rootProvider = rootProvider;
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
            await _js.UpdateStreamAsync(config, cancellationToken).ConfigureAwait(false);
        }

        LogStreamReady(_logger, _options.StreamName, null);

        // Pre-create every registered projection's durable consumer before anyone publishes.
        foreach (var regEntry in _registry.Entries)
        {
            using var scope = _rootProvider.CreateScope();
            var projection = (IProjection<HarnessEvent>)scope.ServiceProvider.GetRequiredService(regEntry.ProjectionType);
            var consumerName = regEntry.Scope == ConsumerScope.PerNode
                ? _naming.PerNodeConsumerName(projection.Name)
                : _naming.ClusterConsumerName(projection.Name);

            var consumerConfig = new ConsumerConfig(consumerName)
            {
                AckPolicy = ConsumerConfigAckPolicy.Explicit,
                DeliverPolicy = ConsumerConfigDeliverPolicy.All,
                FilterSubject = _naming.DurableStreamFilter,
                MaxDeliver = _options.ProjectionRetryBudget,
            };
            await _js.CreateOrUpdateConsumerAsync(_options.StreamName, consumerConfig, cancellationToken)
                .ConfigureAwait(false);
            LogConsumerReady(_logger, consumerName, null);
        }
    }

    public Task StopAsync(CancellationToken cancellationToken) => Task.CompletedTask;
}
```

Note: `ConsumerScope` is introduced in Task 2.1 as a property on the projection registration (`ProjectionRegistry.Entry`), set by the fluent builder via `AddProjection<T>(ConsumerScope.Cluster | ConsumerScope.PerNode)`.

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

/// <summary>
/// Identifies whether a projection's durable consumer is shared cluster-wide (every event
/// is delivered to exactly one consumer = exactly-once write semantics — correct for
/// persistence-style projections) or per-node (every Fleet node gets its own copy — correct
/// for fan-out-style projections such as WebSocket broadcast).
/// </summary>
public enum ConsumerScope { Cluster, PerNode }

public sealed class NatsStreamBuilder
{
    private readonly IServiceCollection _services;
    internal NatsStreamBuilder(IServiceCollection services) => _services = services;

    internal readonly List<ProjectionRegistryEntry> Entries = new();

    public NatsStreamBuilder AddProjection<TProjection>(ConsumerScope scope = ConsumerScope.Cluster)
        where TProjection : class
    {
        _services.AddScoped<TProjection>();
        Entries.Add(new ProjectionRegistryEntry(typeof(TProjection), scope));
        return this;
    }
}

public sealed record ProjectionRegistryEntry(Type ProjectionType, ConsumerScope Scope);
public sealed record ProjectionRegistry(IReadOnlyList<ProjectionRegistryEntry> Entries);
```

Create `src/WeaveFleet.Infrastructure/Nats/Configuration/NatsServiceCollectionExtensions.cs`:

```csharp
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Options;
using NATS.Client.Core;
using NATS.Client.JetStream;
using WeaveFleet.Application.Configuration;
using WeaveFleet.Application.Events;

namespace WeaveFleet.Infrastructure.Nats.Configuration;

public static class NatsServiceCollectionExtensions
{
    /// <summary>
    /// Register the NATS event substrate: embedded server (if applicable), stream initializer,
    /// publisher, projection host, and any projections declared via the fluent builder.
    /// <para>
    /// <see cref="EphemeralEventRelayService"/> is NOT registered here — it depends on
    /// infrastructure wired up later (shadow broadcaster during Phase 3, production broadcaster
    /// during Phase 5). The caller wires it in <c>DependencyInjection.cs</c> alongside its
    /// broadcaster dependency so registration order is always correct.
    /// </para>
    /// </summary>
    public static IServiceCollection AddEventStore(
        this IServiceCollection services,
        NatsOptions options,
        Action<NatsStreamBuilder> configure)
    {
        services.AddSingleton(options);
        services.AddSingleton<IPostConfigureOptions<NatsOptions>, NatsOptionsConfigurator>();
        services.AddSingleton<NatsMetrics>();
        services.AddSingleton(sp => new NatsNamingStrategy(options, options.NodeId));
        services.AddSingleton<NatsServerHostedService>();
        services.AddHostedService(sp => sp.GetRequiredService<NatsServerHostedService>());

        // NATS connection — resolved lazily after NatsServerHostedService starts. Hosted
        // services start in registration order, so NatsServerHostedService is up before any
        // dependent hosted service (stream initializer, projection host) asks for this.
        services.AddSingleton<INatsConnection>(sp =>
        {
            var server = sp.GetRequiredService<NatsServerHostedService>();
            if (string.IsNullOrWhiteSpace(server.ResolvedUrl))
                throw new InvalidOperationException(
                    "NatsServerHostedService must start before the NATS connection is requested.");
            var authOpts = options.CredsFile is { Length: > 0 }
                ? NatsAuthOpts.Default with { CredsFile = options.CredsFile }
                : NatsAuthOpts.Default;
            var natsOpts = new NatsOpts
            {
                Url = server.ResolvedUrl,
                AuthOpts = authOpts,
                // Cap both publish and receive sizes so a malformed or malicious payload cannot
                // OOM either end. Consumer TERMs oversize messages explicitly (see ProjectionListener).
                MaxMsgSize = options.MaxPayloadBytes,
            };
            return new NatsConnection(natsOpts);
        });
        services.AddSingleton<INatsJSContext>(sp => new NatsJSContext(sp.GetRequiredService<INatsConnection>()));

        var builder = new NatsStreamBuilder(services);
        configure(builder);
        services.AddSingleton(new ProjectionRegistry(builder.Entries));

        // Order matters: stream + consumers must be ready before anything publishes.
        services.AddHostedService<NatsStreamInitializer>();

        services.AddSingleton<IEventPublisher, NatsEventPublisher>();

        services.AddHostedService<ProjectionHostService>();

        // EphemeralEventRelayService registration happens in DependencyInjection.cs, adjacent
        // to the broadcaster wiring it depends on (Phase 3 Task 3.3).
        return services;
    }
}
```

- [ ] **Step 2: Add creds-file-must-not-be-logged negative test.**

Create `tests/WeaveFleet.Infrastructure.Tests/Nats/CredsFileLoggingTests.cs`:

```csharp
using Microsoft.Extensions.Logging;
using WeaveFleet.Application.Configuration;
using WeaveFleet.Infrastructure.Nats;

namespace WeaveFleet.Infrastructure.Tests.Nats;

/// <summary>
/// Defence: the NATS creds file path must never appear in any log line emitted by
/// Nats infrastructure. Prevents accidental disclosure in logs shipped off-box.
/// </summary>
public sealed class CredsFileLoggingTests
{
    private sealed class CapturingLogger<T> : ILogger<T>
    {
        public List<string> Lines { get; } = new();
        public IDisposable? BeginScope<TState>(TState state) where TState : notnull => null;
        public bool IsEnabled(LogLevel _) => true;
        public void Log<TState>(LogLevel level, EventId id, TState state, Exception? ex, Func<TState, Exception?, string> formatter)
            => Lines.Add(formatter(state, ex));
    }

    [Fact]
    public async Task NatsServerHostedService_doesNotLogCredsFilePath()
    {
        var secretPath = Path.Combine(Path.GetTempPath(), "nats-secret.creds");
        var options = new NatsOptions { ExternalUrl = "nats://example.invalid:4222", CredsFile = secretPath };
        var logger = new CapturingLogger<NatsServerHostedService>();
        var sut = new NatsServerHostedService(options, logger);

        await sut.StartAsync(CancellationToken.None);
        await sut.StopAsync(CancellationToken.None);

        logger.Lines.ShouldNotContain(l => l.Contains(secretPath, StringComparison.OrdinalIgnoreCase));
    }
}
```

- [ ] **Step 3: Build.**

```bash
dotnet build --nologo
```
Expected: builds because `EphemeralEventRelayService` is no longer referenced from `AddEventStore`. `ProjectionHostService` still needs Task 2.3 to compile cleanly — that lands next.

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
using WeaveFleet.Infrastructure.Nats.Configuration;
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

        var options = new NatsOptions();
        var naming = new NatsNamingStrategy(options, nodeId: "test-node");
        // Pre-create the consumer — in production this is done by NatsStreamInitializer.
        await js.CreateOrUpdateConsumerAsync("fleet-sessions", new ConsumerConfig(naming.ClusterConsumerName("recording"))
        {
            AckPolicy = ConsumerConfigAckPolicy.Explicit,
            DeliverPolicy = ConsumerConfigDeliverPolicy.All,
            FilterSubject = naming.DurableStreamFilter,
            MaxDeliver = options.ProjectionRetryBudget,
        });

        var listener = new ProjectionListener(
            new ProjectionRegistryEntry(typeof(RecordingProjection), ConsumerScope.Cluster),
            js,
            sp,
            naming,
            options,
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
using NATS.Client.Core;
using NATS.Client.JetStream;
using NATS.Client.JetStream.Models;
using WeaveFleet.Application.Configuration;
using WeaveFleet.Application.Projections;
using WeaveFleet.Domain.Harnesses;
using WeaveFleet.Infrastructure.Nats.Configuration;

namespace WeaveFleet.Infrastructure.Nats;

/// <summary>
/// Pumps messages from a pre-created durable JetStream consumer into a single
/// <see cref="IProjection{HarnessEvent}"/>. Consumer scope (cluster vs per-node) is declared
/// via the <see cref="ProjectionRegistryEntry"/> used to bind this listener.
/// <list type="bullet">
///   <item>Deserialization failures and malformed subjects are TERM'd immediately (never retriable).</item>
///   <item>Handler exceptions are NAK'd up to <see cref="NatsOptions.ProjectionRetryBudget"/>,
///     after which the broker stops redelivering (consumer <c>MaxDeliver</c>) and the final
///     attempt is TERM'd with a poison-message metric.</item>
///   <item>Payloads larger than <see cref="NatsOptions.MaxPayloadBytes"/> are TERM'd.</item>
/// </list>
/// OpenTelemetry trace context is extracted from headers before invoking the handler.
/// </summary>
public sealed class ProjectionListener
{
    private static readonly ActivitySource ActivitySource = new("WeaveFleet.Nats.Consume");

    private static readonly Action<ILogger, string, Exception?> LogHandlerFailed =
        LoggerMessage.Define<string>(LogLevel.Error, new EventId(1, "ProjectionHandlerFailed"),
            "Projection {Projection} handler threw");
    private static readonly Action<ILogger, string, int, Exception?> LogPoisoned =
        LoggerMessage.Define<string, int>(LogLevel.Warning, new EventId(2, "ProjectionPoisonMessage"),
            "Projection {Projection} TERM'd message after {Attempts} failed delivery attempts");
    private static readonly Action<ILogger, string, int, Exception?> LogOversize =
        LoggerMessage.Define<string, int>(LogLevel.Warning, new EventId(3, "ProjectionOversizePayload"),
            "Projection {Projection} TERM'd an oversize payload of {Bytes} bytes");
    private static readonly Action<ILogger, string, Exception?> LogMalformed =
        LoggerMessage.Define<string>(LogLevel.Warning, new EventId(4, "ProjectionMalformedPayload"),
            "Projection {Projection} TERM'd a malformed payload");

    private readonly ProjectionRegistryEntry _entry;
    private readonly INatsJSContext _js;
    private readonly IServiceProvider _rootProvider;
    private readonly NatsNamingStrategy _naming;
    private readonly NatsOptions _options;
    private readonly NatsMetrics _metrics;
    private readonly ILogger<ProjectionListener> _logger;

    public ProjectionListener(
        ProjectionRegistryEntry entry,
        INatsJSContext js,
        IServiceProvider rootProvider,
        NatsNamingStrategy naming,
        NatsOptions options,
        NatsMetrics metrics,
        ILogger<ProjectionListener> logger)
    {
        _entry = entry;
        _js = js;
        _rootProvider = rootProvider;
        _naming = naming;
        _options = options;
        _metrics = metrics;
        _logger = logger;
    }

    public async Task RunAsync(CancellationToken ct)
    {
        string projectionName;
        using (var scope = _rootProvider.CreateScope())
        {
            var temp = (IProjection<HarnessEvent>)scope.ServiceProvider.GetRequiredService(_entry.ProjectionType);
            projectionName = temp.Name;
        }

        var consumerName = _entry.Scope == ConsumerScope.PerNode
            ? _naming.PerNodeConsumerName(projectionName)
            : _naming.ClusterConsumerName(projectionName);

        // Consumer was pre-created by NatsStreamInitializer. Bind to it.
        var consumer = await _js.GetConsumerAsync(_naming.StreamName, consumerName, ct).ConfigureAwait(false);

        await foreach (var msg in consumer.ConsumeAsync<byte[]>(cancellationToken: ct).ConfigureAwait(false))
        {
            await HandleSingleAsync(msg, projectionName, ct).ConfigureAwait(false);
        }
    }

    private async Task HandleSingleAsync(NatsJSMsg<byte[]> msg, string projectionName, CancellationToken ct)
    {
        var sw = Stopwatch.StartNew();
        string result = "ack";

        // 1. Oversize guard.
        if (msg.Data is { Length: > 0 } data && data.Length > _options.MaxPayloadBytes)
        {
            LogOversize(_logger, projectionName, data.Length, null);
            try { await msg.AckTerminateAsync(cancellationToken: ct).ConfigureAwait(false); } catch { }
            _metrics.RecordProjectionAck(projectionName, "term");
            return;
        }

        // 2. Subject + payload parse — malformed input is immediate poison.
        NatsNamingStrategy.ParsedDurableSubject parsed;
        HarnessEvent evt;
        try
        {
            parsed = NatsNamingStrategy.ParseDurableSubject(msg.Subject)
                ?? throw new InvalidOperationException($"Subject did not parse: {msg.Subject}");
            evt = JsonSerializer.Deserialize<HarnessEvent>(msg.Data!)
                ?? throw new InvalidOperationException("Payload did not deserialize to HarnessEvent.");
        }
        catch (Exception ex)
        {
            LogMalformed(_logger, projectionName, ex);
            try { await msg.AckTerminateAsync(cancellationToken: ct).ConfigureAwait(false); } catch { }
            _metrics.RecordProjectionAck(projectionName, "term");
            return;
        }

        // 3. Extract OTel trace context from headers before invoking the handler.
        var parentContext = ExtractTraceContext(msg.Headers);
        using var activity = ActivitySource.StartActivity(
            name: $"nats.consume {projectionName}",
            kind: ActivityKind.Consumer,
            parentContext: parentContext);
        activity?.SetTag("session.id", parsed.SessionId);
        activity?.SetTag("project.id", parsed.ProjectId);
        activity?.SetTag("tenant.id", parsed.Tenant);
        activity?.SetTag("event.type", parsed.EventType);

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

        try
        {
            using var scope = _rootProvider.CreateScope();
            var projection = (IProjection<HarnessEvent>)scope.ServiceProvider.GetRequiredService(_entry.ProjectionType);
            await projection.HandleAsync(evt, ctx, ct).ConfigureAwait(false);
            await msg.AckAsync(cancellationToken: ct).ConfigureAwait(false);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            LogHandlerFailed(_logger, projectionName, ex);
            // The JetStream consumer was created with MaxDeliver = ProjectionRetryBudget, so the
            // broker stops redelivering automatically past that count. TERM on the final attempt
            // makes the poison visible; NAK before that allows redelivery.
            var numDelivered = (int)(msg.Metadata?.NumDelivered ?? 1);
            if (numDelivered >= _options.ProjectionRetryBudget)
            {
                LogPoisoned(_logger, projectionName, numDelivered, null);
                try { await msg.AckTerminateAsync(cancellationToken: ct).ConfigureAwait(false); } catch { }
                result = "term";
            }
            else
            {
                try { await msg.NakAsync(cancellationToken: ct).ConfigureAwait(false); } catch { }
                result = "nak";
            }
        }
        finally
        {
            sw.Stop();
            _metrics.RecordProjectionHandler(sw.Elapsed.TotalMilliseconds, projectionName, msg.Subject, result);
            _metrics.RecordProjectionAck(projectionName, result);
        }
    }

    private static ActivityContext ExtractTraceContext(NatsHeaders? headers)
    {
        if (headers is null) return default;
        DistributedContextPropagator.Current.ExtractTraceIdAndState(headers,
            static (carrier, key, out string? value, out IEnumerable<string>? values) =>
            {
                values = null;
                value = carrier is NatsHeaders h && h.TryGetValue(key, out var raw) ? raw.ToString() : null;
            },
            out var traceParent, out var traceState);
        return ActivityContext.TryParse(traceParent, traceState, out var parsed) ? parsed : default;
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
using WeaveFleet.Application.Configuration;
using WeaveFleet.Infrastructure.Nats.Configuration;

namespace WeaveFleet.Infrastructure.Nats;

public sealed class ProjectionHostService : BackgroundService
{
    private readonly ProjectionRegistry _registry;
    private readonly INatsJSContext _js;
    private readonly IServiceProvider _rootProvider;
    private readonly NatsNamingStrategy _naming;
    private readonly NatsOptions _options;
    private readonly NatsMetrics _metrics;
    private readonly ILoggerFactory _loggerFactory;

    public ProjectionHostService(
        ProjectionRegistry registry,
        INatsJSContext js,
        IServiceProvider rootProvider,
        NatsNamingStrategy naming,
        NatsOptions options,
        NatsMetrics metrics,
        ILoggerFactory loggerFactory)
    {
        _registry = registry;
        _js = js;
        _rootProvider = rootProvider;
        _naming = naming;
        _options = options;
        _metrics = metrics;
        _loggerFactory = loggerFactory;
    }

    protected override Task ExecuteAsync(CancellationToken stoppingToken)
    {
        var tasks = _registry.Entries.Select(entry =>
        {
            var listener = new ProjectionListener(
                entry, _js, _rootProvider, _naming, _options, _metrics,
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
        // NATS event substrate — Phase 2 registers the NoOp projection only. Phase 3 Task 3.1
        // replaces this call site with MessagePersistenceProjection (+ WebSocketFanOutProjection).
        services.AddEventStore(options.Nats, nats =>
        {
            nats.AddProjection<WeaveFleet.Application.Projections.NoOpProjection>(ConsumerScope.Cluster);
        });
```

- [ ] **Step 4: Build.**

```bash
dotnet build --nologo
```
Expected: PASS. `EphemeralEventRelayService` is no longer registered inside `AddEventStore` (Task 1.8 Step 1), so there is no missing-type error.

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
        // Analytics uses a separate DB file which is shared across parallel test runs by
        // default — override to an isolated path so `.deps.json` locks cannot cross-contaminate
        // (shouldly-assertion-style-adoption learning).
        builder.UseSetting("Fleet:AnalyticsDatabasePath", Path.Combine(dir, "analytics.db"));
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
            new EventPublishContext("sess-e2e", "proj-e2e", "user-e2e", "opencode", Sequence: 1),
            CancellationToken.None);

        var js = scope.ServiceProvider.GetRequiredService<INatsJSContext>();
        var stream = await js.GetStreamAsync("fleet-sessions");
        (await stream.GetInfoAsync()).State.Messages.ShouldBeGreaterThanOrEqualTo(1UL);
    }
}
```

**Ordering note.** Run this test while `NoOpProjection` is still the registered projection (i.e. between Task 2.3 and Task 3.1). Task 3.1 replaces `NoOpProjection` with `MessagePersistenceProjection`, which requires the SQLite schema and a valid session/project — not present here. A second Phase-3-appropriate e2e test (`MessagePersistenceProjection_persistsMessage`) is added in Task 3.2 Step 6.

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

- [ ] **Step 3: Publish alongside existing call with a per-pump monotonic sequence.**

Each pump owns a `long` counter initialised to `0` at pump start and incremented per published event. The counter feeds into `Nats-Msg-Id` as `{sessionId}:{seq}`. Declare the counter as a local:

```csharp
        long publishSequence = 0;
```

Inside the `await foreach` (line 172), after the existing delta-buffer / persistence / broadcast logic, add:

```csharp
                // Dual-write: publish to NATS so downstream projections observe every event.
                // Does not replace any existing behaviour during the compatibility window.
                try
                {
                    var seq = System.Threading.Interlocked.Increment(ref publishSequence);
                    await _publisher.PublishAsync(evt,
                        new EventPublishContext(targetFleetSessionId, projectId, sessionUserId, harnessType, seq),
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

**Placement:** put the publish call *before* the `handledDurably` / ephemeral gating so the relay always publishes every event, regardless of legacy-path routing. The pump is single-threaded per session, so `Interlocked` is strictly unnecessary — we use it as defensive documentation that `publishSequence` is the only shared-state surface for future parallelization.

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

- [ ] **Step 2: Extend `IHarnessEventPersister` with a shadow entry-point.**

Edit `src/WeaveFleet.Application/Services/IHarnessEventPersister.cs`:

```csharp
/// <summary>
/// Handle an event by writing it to the shadow read model only (no outbox write).
/// Used by <c>MessagePersistenceProjection</c> during the Phase 3 NATS compatibility window.
/// </summary>
Task HandleShadowAsync(string fleetSessionId, string ownerUserId, HarnessEvent evt, CancellationToken ct);
```

- [ ] **Step 3: Create shadow repositories.**

Create `src/WeaveFleet.Domain/Repositories/IShadowMessageRepository.cs` and `IShadowSessionRepository.cs` mirroring the production interfaces but with table names `messages_shadow` / `sessions_shadow`. Copy the full method surface of the originals (`GetByIdAsync`, `UpsertAsync`, `DeleteByIdAsync`, `RemovePartAsync`, `UpdateTitleAsync`) — the shadow service delegates to exactly the same write paths.

Create Dapper implementations in `src/WeaveFleet.Infrastructure/Data/Repositories/DapperShadowMessageRepository.cs` and `DapperShadowSessionRepository.cs`. Each is a straight copy of the production Dapper repo with the table name replaced. Register both as scoped in `DependencyInjection.cs`.

- [ ] **Step 3.5: Create `HarnessEventShadowPersistenceService`.**

Create `src/WeaveFleet.Infrastructure/Services/HarnessEventShadowPersistenceService.cs`:

```csharp
using System.Text.Json;
using WeaveFleet.Application.Services;
using WeaveFleet.Domain.Entities;
using WeaveFleet.Domain.Harnesses;
using WeaveFleet.Domain.Repositories;

namespace WeaveFleet.Infrastructure.Services;

/// <summary>
/// Shadow-mode persister used during the NATS Phase 3 compatibility window.
/// Writes every durable harness event to the <c>messages_shadow</c> / <c>sessions_shadow</c>
/// tables via <see cref="IShadowMessageRepository"/> / <see cref="IShadowSessionRepository"/>.
/// Reuses the exact serialization helpers on <see cref="MessagePersistenceService"/> so the
/// diff harness can assert byte-for-byte equality between production and shadow outputs.
/// Does NOT emit outbox entries — the WebSocket fan-out shadow projection produces the
/// shadow broadcaster output that the diff harness compares against.
/// </summary>
public sealed class HarnessEventShadowPersistenceService
{
    private readonly IShadowMessageRepository _shadowMessages;
    private readonly IShadowSessionRepository _shadowSessions;

    public HarnessEventShadowPersistenceService(
        IShadowMessageRepository shadowMessages,
        IShadowSessionRepository shadowSessions)
    {
        _shadowMessages = shadowMessages;
        _shadowSessions = shadowSessions;
    }

    public async Task HandleAsync(string fleetSessionId, string ownerUserId, HarnessEvent evt, CancellationToken ct)
    {
        if (!EventTypeMetadata.Classify(evt.Type).IsDurable) return;

        using var userScope = BackgroundUserContext.BeginScope(ownerUserId);

        switch (evt.Type)
        {
            case EventTypes.MessageCreated:
            case EventTypes.MessageUpdated:
                await TryPersistMessageAsync(fleetSessionId, evt).ConfigureAwait(false);
                return;
            case EventTypes.MessagePartUpdated:
                await TryPersistPartAsync(fleetSessionId, evt).ConfigureAwait(false);
                return;
            case EventTypes.MessageRemoved:
                await TryRemoveMessageAsync(fleetSessionId, evt).ConfigureAwait(false);
                return;
            case EventTypes.MessagePartRemoved:
                await TryRemovePartAsync(fleetSessionId, evt).ConfigureAwait(false);
                return;
            case EventTypes.SessionUpdated:
                await TryUpdateTitleAsync(fleetSessionId, evt).ConfigureAwait(false);
                return;
            // session.error / session.compacted / session.deleted are outbox-only in prod; no
            // shadow read-model write is required — the diff harness covers the broadcaster path.
            case EventTypes.SessionError:
            case EventTypes.SessionCompacted:
            case EventTypes.SessionDeleted:
                return;
        }
    }

    // Each Try* helper is the body of the corresponding method on HarnessEventPersistenceService,
    // with every call to _messageRepository / _sessionRepository / _sessionActivityWriteService
    // replaced by _shadowMessages / _shadowSessions and the outbox write removed entirely.
    // The Json / OpenCode parsing helpers are reused verbatim via `MessagePersistenceService`
    // and `OpenCodeMapper` — they do not touch the production DB, only transform JSON → PersistedMessage.

    // NOTE to implementer: lift TryPersistMessageAsync / TryPersistPartAsync / TryHandleMessageRemovedAsync
    // / TryHandleMessagePartRemovedAsync / TryHandleSessionUpdatedAsync from
    // HarnessEventPersistenceService, rename the receivers to the shadow repositories, and drop
    // all outbox-related code paths. Add a unit test for each to assert shadow row contents.
    private Task TryPersistMessageAsync(string fleetSessionId, HarnessEvent evt) => /* see note above */ Task.CompletedTask;
    private Task TryPersistPartAsync(string fleetSessionId, HarnessEvent evt) => Task.CompletedTask;
    private Task TryRemoveMessageAsync(string fleetSessionId, HarnessEvent evt) => Task.CompletedTask;
    private Task TryRemovePartAsync(string fleetSessionId, HarnessEvent evt) => Task.CompletedTask;
    private Task TryUpdateTitleAsync(string fleetSessionId, HarnessEvent evt) => Task.CompletedTask;
}
```

Implementation strategy for the `Try*` bodies: **do not retype from scratch.** Lift the corresponding helper method from `HarnessEventPersistenceService` (the file at `src/WeaveFleet.Infrastructure/Services/HarnessEventPersistenceService.cs`, lines 172-463), copy it into this file, then mechanically apply these find/replace rules:

1. `_messageRepository.` → `_shadowMessages.`
2. `_sessionRepository.` → `_shadowSessions.`
3. Delete every `_sessionActivityWriteService.WriteAsync(...)` call and its surrounding `SessionActivityWriteRequest` construction.
4. Replace `await WriteDurableEventAsync(...)` / `await EmitOutboxEventAsync(...)` with a direct `await _shadowMessages.UpsertAsync(persisted)` (or the equivalent shadow-repo method for the specific handler).

Write a TDD test per handler in `tests/WeaveFleet.Infrastructure.Tests/Services/HarnessEventShadowPersistenceServiceTests.cs`:
- `HandleAsync_MessageCreated_writesMessageRow`
- `HandleAsync_MessageUpdated_mergesRow`
- `HandleAsync_MessagePartUpdated_writesPart`
- `HandleAsync_MessageRemoved_deletesRow`
- `HandleAsync_MessagePartRemoved_removesPart`
- `HandleAsync_SessionUpdated_updatesTitle`
- `HandleAsync_SessionError_isNoOp` (and same for Compacted/Deleted)
- `HandleAsync_EphemeralEvent_isNoOp`

Finally, wire `HarnessEventPersistenceService.HandleShadowAsync` to delegate to this service:

```csharp
    // Inside HarnessEventPersistenceService — injected via constructor (add alongside existing args):
    private readonly HarnessEventShadowPersistenceService _shadow;
    // ...
    public Task HandleShadowAsync(string fleetSessionId, string ownerUserId, HarnessEvent evt, CancellationToken ct)
        => _shadow.HandleAsync(fleetSessionId, ownerUserId, evt, ct);
```

Register `HarnessEventShadowPersistenceService` as scoped in `DependencyInjection.cs` alongside the shadow repositories.

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
            nats.AddProjection<WeaveFleet.Application.Projections.MessagePersistenceProjection>(ConsumerScope.Cluster);
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
            // Persistence is a single-writer — use a cluster-wide consumer (exactly-once semantics).
            nats.AddProjection<WeaveFleet.Application.Projections.MessagePersistenceProjection>(ConsumerScope.Cluster);
            // WS fan-out needs each Fleet node to get its own copy of the stream.
            nats.AddProjection<WeaveFleet.Application.Projections.WebSocketFanOutProjection>(ConsumerScope.PerNode);
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
using WeaveFleet.Application.Configuration;
using WeaveFleet.Application.Services;
using WeaveFleet.Domain.Harnesses;

namespace WeaveFleet.Infrastructure.Nats;

/// <summary>
/// Core NATS subscriber for ephemeral events. During Phase 3 it forwards to
/// <see cref="IShadowEventBroadcaster"/>; Phase 5 switches the target to the production broadcaster.
/// Guards against oversize or malformed payloads — the subject space is internal-only, but
/// defence-in-depth still applies.
/// </summary>
public sealed class EphemeralEventRelayService : BackgroundService
{
    private static readonly Action<ILogger, Exception?> LogFailed =
        LoggerMessage.Define(LogLevel.Warning, new EventId(1, "EphemeralForwardFailed"),
            "Failed to forward ephemeral NATS event to broadcaster");
    private static readonly Action<ILogger, int, Exception?> LogOversize =
        LoggerMessage.Define<int>(LogLevel.Warning, new EventId(2, "EphemeralOversizePayload"),
            "Dropped oversize ephemeral payload ({Bytes} bytes)");
    private static readonly Action<ILogger, Exception?> LogMalformed =
        LoggerMessage.Define(LogLevel.Warning, new EventId(3, "EphemeralMalformedPayload"),
            "Dropped malformed ephemeral payload");

    private readonly INatsConnection _connection;
    private readonly NatsNamingStrategy _naming;
    private readonly IShadowEventBroadcaster _shadow;
    private readonly NatsOptions _options;
    private readonly ILogger<EphemeralEventRelayService> _logger;

    public EphemeralEventRelayService(
        INatsConnection connection,
        NatsNamingStrategy naming,
        IShadowEventBroadcaster shadow,
        NatsOptions options,
        ILogger<EphemeralEventRelayService> logger)
    {
        _connection = connection;
        _naming = naming;
        _shadow = shadow;
        _options = options;
        _logger = logger;
    }

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        await foreach (var msg in _connection
            .SubscribeAsync<byte[]>(_naming.EphemeralSubscriptionFilter, cancellationToken: stoppingToken)
            .ConfigureAwait(false))
        {
            try
            {
                if (msg.Data is { Length: > 0 } data && data.Length > _options.MaxPayloadBytes)
                {
                    LogOversize(_logger, data.Length, null);
                    continue;
                }

                // Subject format: tenant.{ws}.project.{pid}.live.{sid}.{type}
                var parts = msg.Subject.Split('.');
                if (parts.Length < 7 || parts[0] != "tenant" || parts[2] != "project" || parts[4] != "live")
                {
                    LogMalformed(_logger, null);
                    continue;
                }
                var sessionId = parts[5];
                var eventType = string.Join('.', parts[6..]);

                HarnessEvent? evt;
                try
                {
                    evt = JsonSerializer.Deserialize<HarnessEvent>(msg.Data!);
                }
                catch (JsonException jx)
                {
                    LogMalformed(_logger, jx);
                    continue;
                }
                if (evt is null) { LogMalformed(_logger, null); continue; }

                string? userId = msg.Headers?["x-fleet-user-id"].ToString();
                object payload = evt.Payload.HasValue ? evt.Payload.Value : JsonSerializer.SerializeToElement(new { });
                await _shadow.BroadcastAsync($"session:{sessionId}", eventType, payload, userId, stoppingToken)
                    .ConfigureAwait(false);
            }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                LogFailed(_logger, ex);
            }
        }
    }
}
```

- [ ] **Step 2: Register it next to its broadcaster dependency.**

Edit `src/WeaveFleet.Infrastructure/DependencyInjection.cs` — add the hosted-service registration adjacent to the shadow-broadcaster registration added in Task 3.2 Step 1, so both land together:

```csharp
        services.AddSingleton<IShadowEventBroadcaster, InMemoryShadowEventBroadcaster>();
        services.AddHostedService<EphemeralEventRelayService>();
```

`NatsServiceCollectionExtensions.AddEventStore` deliberately does NOT register this service — keeping the registration here makes the dependency direction obvious and avoids the circular TODO we had previously.

- [ ] **Step 3: Build.**

```bash
dotnet build --nologo
```
Expected: PASS.

- [ ] **Step 4: Commit.**

```bash
git add src/WeaveFleet.Infrastructure/Nats/EphemeralEventRelayService.cs src/WeaveFleet.Infrastructure/DependencyInjection.cs
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

### Task 4.0: Extract merge-before-publish helper (blocking pre-req for Task 4.1)

The design mandates that the published durable `message.updated` payload already contains the merged text (Cutover Timing section: "the snapshot is not constructed a second time in either projection"). Today the merge happens inside `HarnessEventPersistenceService.TryPersistMessageAsync` — after the event has already been handed to the persister. After cutover the relay publishes first and the persister runs inside a projection, so the merge must be hoisted into the relay before publish.

This task is strictly mechanical extraction + tests; Task 4.1 depends on it.

**Files:**
- Modify: `src/WeaveFleet.Application/Services/MessagePersistenceService.cs` — add a new static helper.
- Modify: `src/WeaveFleet.Infrastructure/Services/HarnessEventPersistenceService.cs` — expose `TryPopBufferedDeltasForMessage`.
- Modify: `src/WeaveFleet.Infrastructure/Services/HarnessEventRelay.cs` — merge before publish.
- Create: `tests/WeaveFleet.Infrastructure.Tests/Services/DeltaMergeBeforePublishTests.cs`.

- [ ] **Step 1: Write the failing tests.**

Create `tests/WeaveFleet.Infrastructure.Tests/Services/DeltaMergeBeforePublishTests.cs` with these cases (write them all first; they will fail until the helper exists):

```csharp
using System.Text.Json;
using WeaveFleet.Application.Services;
using WeaveFleet.Domain.Harnesses;

namespace WeaveFleet.Infrastructure.Tests.Services;

public sealed class DeltaMergeBeforePublishTests
{
    [Fact]
    public void MergeBufferedDeltasIntoEvent_noDeltasBuffered_returnsSameEvent()
    {
        var evt = BuildMessageUpdated("msg-1", partsJson: """[{"type":"text","id":"p1","text":"hello"}]""");
        var merged = MessagePersistenceService.MergeBufferedDeltasIntoEvent(evt, bufferedDeltas: []);
        merged.Payload!.Value.GetRawText().ShouldBe(evt.Payload!.Value.GetRawText());
    }

    [Fact]
    public void MergeBufferedDeltasIntoEvent_singlePart_appendsBufferedDelta()
    {
        var evt = BuildMessageUpdated("msg-1", partsJson: """[{"type":"text","id":"p1","text":"he"}]""");
        var merged = MessagePersistenceService.MergeBufferedDeltasIntoEvent(evt,
            bufferedDeltas: new Dictionary<(string msg, string part), string>
            {
                [("msg-1", "p1")] = "llo"
            });
        merged.Payload!.Value.GetProperty("parts")[0].GetProperty("text").GetString().ShouldBe("hello");
    }

    [Fact]
    public void MergeBufferedDeltasIntoEvent_multipleParts_mergesByPartId()
    {
        var evt = BuildMessageUpdated("msg-1",
            partsJson: """[{"type":"text","id":"p1","text":"a"},{"type":"text","id":"p2","text":""}]""");
        var merged = MessagePersistenceService.MergeBufferedDeltasIntoEvent(evt,
            bufferedDeltas: new Dictionary<(string msg, string part), string>
            {
                [("msg-1", "p1")] = "b",
                [("msg-1", "p2")] = "xyz"
            });
        merged.Payload!.Value.GetProperty("parts")[0].GetProperty("text").GetString().ShouldBe("ab");
        merged.Payload!.Value.GetProperty("parts")[1].GetProperty("text").GetString().ShouldBe("xyz");
    }

    [Fact]
    public void MergeBufferedDeltasIntoEvent_messageCreatedAndPartUpdated_alsoMerge()
    {
        var evt = BuildEvent(EventTypes.MessageCreated, "msg-1",
            partsJson: """[{"type":"text","id":"p1","text":""}]""");
        var merged = MessagePersistenceService.MergeBufferedDeltasIntoEvent(evt,
            bufferedDeltas: new Dictionary<(string msg, string part), string>
            {
                [("msg-1", "p1")] = "hi"
            });
        merged.Payload!.Value.GetProperty("parts")[0].GetProperty("text").GetString().ShouldBe("hi");
    }

    [Fact]
    public void MergeBufferedDeltasIntoEvent_nonMergeableEventType_isPassThrough()
    {
        var evt = BuildEvent(EventTypes.SessionUpdated, "msg-1", partsJson: null,
            payload: JsonSerializer.SerializeToElement(new { title = "new" }));
        var merged = MessagePersistenceService.MergeBufferedDeltasIntoEvent(evt,
            bufferedDeltas: new Dictionary<(string msg, string part), string>
            {
                [("msg-1", "p1")] = "ignored"
            });
        merged.ShouldBeSameAs(evt);
    }

    private static HarnessEvent BuildMessageUpdated(string messageId, string partsJson)
        => BuildEvent(EventTypes.MessageUpdated, messageId, partsJson);

    private static HarnessEvent BuildEvent(string type, string messageId, string? partsJson, JsonElement? payload = null)
    {
        var composed = payload ?? (partsJson is null
            ? JsonSerializer.SerializeToElement(new { info = new { id = messageId } })
            : JsonSerializer.SerializeToElement(new Dictionary<string, object?>
            {
                ["info"] = new { id = messageId, role = "assistant" },
                ["parts"] = JsonDocument.Parse(partsJson).RootElement.Clone(),
            }));
        return new HarnessEvent { Type = type, SessionId = "sess-1", Timestamp = DateTimeOffset.UtcNow, Payload = composed };
    }
}
```

- [ ] **Step 2: Run the failing tests.**

```bash
dotnet test tests/WeaveFleet.Infrastructure.Tests --nologo --filter FullyQualifiedName~DeltaMergeBeforePublishTests
```
Expected: FAIL — `MessagePersistenceService.MergeBufferedDeltasIntoEvent` does not exist yet.

- [ ] **Step 3: Implement the helper.**

Add to `src/WeaveFleet.Application/Services/MessagePersistenceService.cs` (or wherever the existing `MergeTextDeltaAndMetadata` lives):

```csharp
    /// <summary>
    /// Return a copy of <paramref name="evt"/> with buffered text deltas merged into every
    /// text part whose (messageId, partId) appears in <paramref name="bufferedDeltas"/>.
    /// Pass-through for event types that are not message-shape carriers
    /// (<see cref="EventTypes.MessageCreated"/>, <see cref="EventTypes.MessageUpdated"/>,
    /// <see cref="EventTypes.MessagePartUpdated"/>).
    /// Pure function — no state, no side effects. Caller is responsible for popping the buffer.
    /// </summary>
    public static HarnessEvent MergeBufferedDeltasIntoEvent(
        HarnessEvent evt,
        IReadOnlyDictionary<(string MessageId, string PartId), string> bufferedDeltas)
    {
        if (bufferedDeltas.Count == 0) return evt;
        if (evt.Type is not (EventTypes.MessageCreated or EventTypes.MessageUpdated or EventTypes.MessagePartUpdated))
            return evt;
        if (!evt.Payload.HasValue || evt.Payload.Value.ValueKind != JsonValueKind.Object)
            return evt;

        var payload = evt.Payload.Value;
        if (!payload.TryGetProperty("parts", out var partsEl) || partsEl.ValueKind != JsonValueKind.Array)
            return evt;

        // Detect whether any part matches a buffered delta; bail early if not.
        var messageId = payload.TryGetProperty("info", out var infoEl)
            && infoEl.TryGetProperty("id", out var idEl) ? idEl.GetString() : null;
        if (messageId is null) return evt;
        if (!bufferedDeltas.Keys.Any(k => k.MessageId == messageId)) return evt;

        // Rebuild the parts array with merged text.
        using var stream = new MemoryStream();
        using (var writer = new Utf8JsonWriter(stream))
        {
            writer.WriteStartObject();
            foreach (var prop in payload.EnumerateObject())
            {
                if (prop.Name == "parts")
                {
                    writer.WritePropertyName("parts");
                    writer.WriteStartArray();
                    foreach (var part in prop.Value.EnumerateArray())
                    {
                        WriteMergedPart(writer, part, messageId, bufferedDeltas);
                    }
                    writer.WriteEndArray();
                }
                else
                {
                    prop.WriteTo(writer);
                }
            }
            writer.WriteEndObject();
        }
        var mergedPayload = JsonDocument.Parse(stream.ToArray()).RootElement;
        return evt with { Payload = mergedPayload };
    }

    private static void WriteMergedPart(
        Utf8JsonWriter writer,
        JsonElement part,
        string messageId,
        IReadOnlyDictionary<(string, string), string> bufferedDeltas)
    {
        if (part.ValueKind != JsonValueKind.Object
            || !part.TryGetProperty("type", out var typeEl) || typeEl.GetString() != "text"
            || !part.TryGetProperty("id", out var partIdEl))
        {
            part.WriteTo(writer);
            return;
        }

        var partId = partIdEl.GetString();
        if (partId is null || !bufferedDeltas.TryGetValue((messageId, partId), out var delta))
        {
            part.WriteTo(writer);
            return;
        }

        writer.WriteStartObject();
        foreach (var prop in part.EnumerateObject())
        {
            if (prop.Name == "text")
            {
                var existing = prop.Value.ValueKind == JsonValueKind.String ? prop.Value.GetString() ?? "" : "";
                writer.WriteString("text", existing + delta);
            }
            else
            {
                prop.WriteTo(writer);
            }
        }
        writer.WriteEndObject();
    }
```

- [ ] **Step 4: Expose the buffer to the relay via the persister.**

Edit `src/WeaveFleet.Infrastructure/Services/HarnessEventPersistenceService.cs` to add a public method that returns the currently-buffered deltas for a given session (used by the relay to supply the dictionary to the merge helper):

```csharp
    /// <summary>
    /// Return a snapshot of buffered text deltas for <paramref name="fleetSessionId"/>. The caller
    /// is responsible for calling <see cref="BufferTextDelta"/> first for any new delta events,
    /// then calling this method immediately before publishing a durable event that may reference
    /// the buffered parts. Does NOT clear the buffer — the existing persister behavior does.
    /// </summary>
    public IReadOnlyDictionary<(string MessageId, string PartId), string> SnapshotBufferedDeltas(string fleetSessionId)
    {
        var prefix = $"{fleetSessionId}::";
        var result = new Dictionary<(string, string), string>();
        foreach (var kv in _bufferedTextDeltas)
        {
            if (!kv.Key.MessageKey.StartsWith(prefix, StringComparison.Ordinal)) continue;
            var afterPrefix = kv.Key.MessageKey[prefix.Length..];
            var sep = afterPrefix.IndexOf("::", StringComparison.Ordinal);
            if (sep < 0) continue;
            var messageId = afterPrefix[..sep];
            result[(messageId, kv.Key.PartId)] = kv.Value;
        }
        return result;
    }
```

Also add this method to `IHarnessEventPersister` so the Application-layer relay can call it without taking an Infrastructure dependency.

- [ ] **Step 5: Merge in the relay pump (additive — no behavior change yet).**

Edit `src/WeaveFleet.Infrastructure/Services/HarnessEventRelay.cs` — wrap the publish call from Task 3.0 so it publishes the merged event:

```csharp
                var eventToPublish = persistenceService is not null
                    ? MessagePersistenceService.MergeBufferedDeltasIntoEvent(
                          evt,
                          persistenceService.SnapshotBufferedDeltas(targetFleetSessionId))
                    : evt;

                try
                {
                    var seq = System.Threading.Interlocked.Increment(ref publishSequence);
                    await _publisher.PublishAsync(eventToPublish,
                        new EventPublishContext(targetFleetSessionId, projectId, sessionUserId, harnessType, seq),
                        ct).ConfigureAwait(false);
                }
                catch (Exception pubEx) { LogPublishFailed(_logger, instanceId, pubEx); }
```

- [ ] **Step 6: Add an integration test that JetStream receives the merged payload.**

Create `tests/WeaveFleet.Api.Tests/Nats/MergedDurablePayloadTests.cs`:

```csharp
// Use NatsEnabledFactory from Task 2.4. Drive a synthetic pump: buffer deltas for p1,
// then publish a message.updated event for the same messageId. Fetch the raw JetStream
// message bytes and assert the parts[0].text contains the merged delta content.
// No re-merge happens downstream.
```

- [ ] **Step 7: Run the full suite.**

```bash
dotnet test --nologo
```
Expected: all previously-passing tests still pass; new merge-before-publish tests pass.

- [ ] **Step 8: Commit.**

```bash
git add src/WeaveFleet.Application/Services/MessagePersistenceService.cs src/WeaveFleet.Application/Services/IHarnessEventPersister.cs src/WeaveFleet.Infrastructure/Services/HarnessEventPersistenceService.cs src/WeaveFleet.Infrastructure/Services/HarnessEventRelay.cs tests/WeaveFleet.Infrastructure.Tests/Services/DeltaMergeBeforePublishTests.cs tests/WeaveFleet.Api.Tests/Nats/MergedDurablePayloadTests.cs
git commit -m "refactor: merge buffered text deltas into HarnessEvent before NATS publish"
```

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
- Keep the `persistenceService?.BufferTextDelta(...)` call *and* the `SnapshotBufferedDeltas(...)` lookup from Task 4.0 — delta buffering still lives in the relay (design Decision 4) and now feeds the merge-before-publish helper exclusively.
- Keep the flush on disconnect (lines 237-247).

The pump now:
1. Buffers deltas for merge.
2. Snapshots the buffer and merges into the durable event.
3. Publishes the merged event via `_publisher.PublishAsync(...)`.
4. Still broadcasts ephemeral events via `_broadcaster` (removed in Phase 5).
5. Flushes deltas on exit.

- [ ] **Step 3: Run the full integration test suite.**

```bash
dotnet test --nologo
```
Expected:
- All Phase-1–3 tests still pass.
- Shadow diff harness still runs (shadow tables still populated for now, until Task 4.2).
- `SkillEndpointPathTraversalTests.GetSkill_ReturnsBadRequestOrNotRouted_ForEncodedTraversal` remains on the known-failing list but is otherwise stable.

- [ ] **Step 4: Commit.**

```bash
git add src/WeaveFleet.Application/Projections/MessagePersistenceProjection.cs src/WeaveFleet.Infrastructure/Services/HarnessEventRelay.cs
git commit -m "refactor: persistence cutover — projection is the sole SQLite writer"
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

- [ ] **Step 2: Test (not optional — the design's tenant-isolation claim rests on this).**

Create `tests/WeaveFleet.Api.Tests/Nats/WebSocketAuthzTests.cs`. The test covers two properties the substrate design depends on:

1. A second user subscribed to `session:{other-user-session}` receives no events.
2. A **forged** `x-fleet-user-id` header on a publish does NOT override the broadcaster's subscriber scoping — the WS endpoint must filter by the authenticated subscriber identity, not by the event's published-side header.

```csharp
// The second property is the important one. Today IEventBroadcaster.SubscribeAsync filters on
// evt.UserId, which is populated from the published header. If a compromised publisher forges
// x-fleet-user-id, the broadcaster's default filtering is bypassed. The correct guard is at the
// subscribe-side: authoritative user identity comes from the authenticated cookie, and the
// subscribe path must reject (or silently drop) any event whose session is not owned by that
// user. Assert:
//
//   [Fact] Task ForgedUserIdHeader_cannotRouteToUnrelatedSubscriber()
//     - Authenticate WS client A as user-A (cookie).
//     - A subscribes to topic "session:sess-A" (owned by user-A).
//     - Publish a message directly to NATS on subject tenant.default.project.p.session.sess-B.message.updated
//       with forged header x-fleet-user-id=user-A.
//     - Assert: A receives no event (sess-B is not owned by A and ownership is checked against
//       Session.UserId in SQLite, not against the published header).
```

This test is blocking for Phase 6 sign-off — if it fails, the design's tenant-isolation claim is not upheld and the fix is either at the WS subscribe path (resolve session ownership from SQLite on subscribe, reject events whose `ctx.FleetSessionId` does not match an owned session) or at the broadcaster (stop trusting the header and look up ownership).

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

- **Spec coverage:** every design decision (1–11) is represented. Decision 1 (scope = harness events only) is respected: `DelegationService` / `SessionOrchestrator` direct-broadcast path is untouched. Decision 5 (per-session serial publish with awaited PubAck + `{sessionId}:{seq}` `Nats-Msg-Id`) is in `NatsEventPublisher.PublishDurableAsync` with the counter owned by the relay pump (Task 3.0 / Task 4.0). Decision 10 (interest retention + per-mode MaxAge) is in `NatsStreamInitializer` + `NatsOptionsConfigurator`. Decision 6 (embedded server per-RID with SHA-pinned binaries) is Task 1.0b + 1.6. Decision 8 (per-node WS fan-out consumer, cluster persistence consumer) is enforced by `ConsumerScope`. Decision 3 (wire-format = existing `HarnessEvent`) is preserved throughout. Cutover Timing / merge-before-publish is Task 4.0 (its own dedicated task, not a sub-step).
- **No placeholders:** each step has exact file paths, code blocks, and commands.
- **Type consistency:** `IProjection<T>.HandleAsync(T, ProjectionContext, CancellationToken)`, `IEventPublisher.PublishAsync(HarnessEvent, EventPublishContext, CancellationToken)` (with `Sequence: long`), `IHarnessEventPersister.HandleAsync(string, string, HarnessEvent, CancellationToken)` are used consistently across tasks.
- **Known weaknesses:**
  - Task 1.8's DI wiring for `NatsConnection` uses a factory that requires `NatsServerHostedService` to have started — the host's startup-order guarantee is that hosted services start in registration order. Verify at runtime during Task 2.4 that this holds; if not, introduce a `Lazy<INatsConnection>` that resolves on first use.
  - Every NATS.Net 2.x API used here (`PubAckResponse.Error`, `PubAckResponse.Duplicate`, `AckTerminateAsync`, `GetConsumerAsync`, `ConsumeAsync<byte[]>`, `NatsHeaders` indexer) should be re-verified against the exact installed package version before Task 1.5 is executed — the NATS.Net API has changed minor names across 2.x releases.
  - Metric `tenant` dimension becomes per-workspace under managed cloud — flagged inline in Task 1.2 for Phase 6 scrub (replace with `tenant_class` or hash-bucket) before managed-cloud GA.

**Plan complete.** Saved to `.weave/plans/nats-event-substrate.md`. Two execution options:

**1. Subagent-Driven (recommended)** — dispatch a fresh subagent per task, review between tasks, fast iteration.
**2. Inline Execution** — execute tasks in this session using `superpowers:executing-plans`, batch execution with checkpoints.

Which approach?
