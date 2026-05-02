# Remove NATS — Replace with In-Process Event Bus

> **PR**: https://github.com/damianh/weave-fleet/pull/1

## TL;DR
> **Summary**: Remove the NATS event substrate (packages, embedded server binaries, all NATS source/test files, configuration) and make the existing in-process `System.Threading.Channels`-based event bus the only transport.
> **Estimated Effort**: Medium

## Context
### Original Request
Remove NATS from the project entirely. The in-process event bus already exists and is feature-complete — NATS was a second transport option for multi-node scenarios that is no longer needed.

### Key Findings
- **Transport toggle**: `FleetOptions.EventBus.Transport` (`TransportKind` enum: `InProcess` | `Nats`) selects the bus in `DependencyInjection.cs:139-158`. Default is already `InProcess`.
- **In-process bus is complete**: `src/WeaveFleet.Infrastructure/EventBus/` has `InProcessEventPublisher`, `InProcessProjectionHost`, `InProcessFanOutService`, `InProcessEventStore`, `InProcessChannels` — all wired via `AddInProcessEventBus()`. It implements `IEventPublisher`, projection dispatch, and WebSocket fan-out identically to NATS.
- **NATS source files** (11 files): `src/WeaveFleet.Infrastructure/Nats/` — publisher, stream initializer, projection host/listener, WebSocket fan-out subscriber, naming strategy, metrics, embedded server host, binary resolver, configuration extensions, stream builder.
- **Embedded nats-server binaries** (~4 platform RIDs): `Nats/EmbeddedNatsServer/Binaries/{win-x64,win-arm64,osx-arm64,linux-x64}/nats-server[.exe]` — shipped in csproj via `CopyToOutputDirectory`.
- **NATS NuGet package**: `NATS.Net` in `WeaveFleet.Infrastructure.csproj`.
- **Shared types in wrong namespace**: `ProjectionRegistry`, `ProjectionRegistryEntry`, `ConsumerScope` live in `Nats.Configuration.NatsStreamBuilder.cs` but are consumed by the InProcess bus (`InProcessEventBusBuilder`, `InProcessProjectionHost`, `InProcessServiceCollectionExtensions`). These must be relocated before deleting the Nats folder.
- **Application-layer NATS types**: `NatsOptions` class in `src/WeaveFleet.Application/Configuration/NatsOptions.cs`, `TransportKind.Nats` enum value in `EventBusOptions.cs`, `FleetOptions.Nats` property.
- **Test files** (8 files): `tests/WeaveFleet.Infrastructure.Tests/Nats/` (7 files) + `tests/WeaveFleet.Api.Tests/Nats/NatsEventSubstrateEndToEndTests.cs` + `tests/WeaveFleet.Application.Tests/Configuration/NatsOptionsTests.cs`.
- **Config/launch profiles**: `launchSettings.json` has a "WeaveFleet.Api (NATS)" profile; `release.yml` sets `Fleet__Nats__Enabled="false"`.
- **Docs**: `docs/nats-event-substrate-design.md`, `.weave/plans/nats-event-substrate.md`.
- **No docker-compose** files reference NATS.

## Objectives
### Core Objective
Remove all NATS code, binaries, packages, and configuration so the in-process event bus is the sole transport.

### Deliverables
- [ ] All `Nats/` source and binary directories deleted
- [ ] `NATS.Net` package reference removed
- [ ] Shared projection types relocated to `EventBus/` namespace
- [ ] `TransportKind` enum, `EventBusOptions`, `NatsOptions` simplified
- [ ] All NATS test files deleted
- [ ] Launch profiles / CI config cleaned up
- [ ] NATS design doc removed

### Definition of Done
- [ ] `dotnet build` succeeds with zero warnings related to missing NATS types
- [ ] `dotnet test` — all remaining tests pass
- [ ] `rg -i "nats" src/ tests/` returns zero hits (excluding this plan file)

### Guardrails (Must NOT)
- Do NOT change the in-process event bus behavior or its public API
- Do NOT remove `IEventPublisher`, `IEventBroadcaster`, or projection infrastructure
- Do NOT alter the `HarnessEventRelay` (it's transport-agnostic)

## TODOs

- [x] 1. **Relocate shared projection types out of Nats namespace**
  **What**: Move `ConsumerScope`, `ProjectionRegistryEntry`, `ProjectionRegistry` from `Nats/Configuration/NatsStreamBuilder.cs` into a new file `EventBus/ProjectionRegistry.cs` (namespace `WeaveFleet.Infrastructure.EventBus`). Update `using` directives in `InProcessEventBusBuilder.cs`, `InProcessProjectionHost.cs`, `InProcessServiceCollectionExtensions.cs`, and `DependencyInjection.cs`.
  **Files**: `src/WeaveFleet.Infrastructure/EventBus/ProjectionRegistry.cs` (new), `src/WeaveFleet.Infrastructure/EventBus/InProcessEventBusBuilder.cs`, `src/WeaveFleet.Infrastructure/EventBus/InProcessProjectionHost.cs`, `src/WeaveFleet.Infrastructure/EventBus/InProcessServiceCollectionExtensions.cs`, `src/WeaveFleet.Infrastructure/DependencyInjection.cs`
  **Acceptance**: Build succeeds; InProcess bus tests pass

- [x] 2. **Remove NATS branch from DependencyInjection.cs**
  **What**: Delete the `if (options.EventBus.Transport == TransportKind.Nats)` branch (lines 139-150). Keep only the `else` body (in-process bus), removing the `else` wrapper. Remove `using WeaveFleet.Infrastructure.Nats.Configuration;`.
  **Files**: `src/WeaveFleet.Infrastructure/DependencyInjection.cs`
  **Acceptance**: `DependencyInjection.cs` unconditionally calls `AddInProcessEventBus()`

- [x] 3. **Simplify EventBusOptions and remove NatsOptions**
  **What**: (a) Remove `TransportKind.Nats` from enum (or remove the entire `TransportKind` enum and `EventBusOptions` class if no longer needed — the transport is always InProcess). (b) Delete `NatsOptions.cs`. (c) Remove `FleetOptions.Nats` and `FleetOptions.EventBus` properties (or simplify `EventBus` to remove `Transport`).
  **Files**: `src/WeaveFleet.Application/Configuration/EventBusOptions.cs`, `src/WeaveFleet.Application/Configuration/NatsOptions.cs` (delete), `src/WeaveFleet.Application/Configuration/FleetOptions.cs`
  **Acceptance**: Build succeeds; no references to `NatsOptions` or `TransportKind.Nats`

- [x] 4. **Delete entire Nats source directory**
  **What**: Delete `src/WeaveFleet.Infrastructure/Nats/` (all 11 .cs files + embedded binary folder with ~4 nats-server executables).
  **Files**: `src/WeaveFleet.Infrastructure/Nats/` (delete entire directory)
  **Acceptance**: Directory gone; build succeeds

- [x] 5. **Remove NATS.Net package reference and binary copy target**
  **What**: Remove `<PackageReference Include="NATS.Net" />` and the `<None Include="Nats\EmbeddedNatsServer\Binaries\**\*" .../>` item group from the csproj.
  **Files**: `src/WeaveFleet.Infrastructure/WeaveFleet.Infrastructure.csproj`
  **Acceptance**: `dotnet restore` + `dotnet build` succeed

- [x] 6. **Delete all NATS test files**
  **What**: Delete `tests/WeaveFleet.Infrastructure.Tests/Nats/` (7 files), `tests/WeaveFleet.Api.Tests/Nats/` (1 file), `tests/WeaveFleet.Application.Tests/Configuration/NatsOptionsTests.cs`. Update any test that references NATS in comments (e.g., `HarnessEventRelayTests.cs` line 17 doc comment).
  **Files**: `tests/WeaveFleet.Infrastructure.Tests/Nats/` (delete), `tests/WeaveFleet.Api.Tests/Nats/` (delete), `tests/WeaveFleet.Application.Tests/Configuration/NatsOptionsTests.cs` (delete), `tests/WeaveFleet.Infrastructure.Tests/Services/HarnessEventRelayTests.cs`
  **Acceptance**: `dotnet test` passes; no NATS test files remain

- [x] 7. **Update test helpers referencing Nats.Configuration namespace**
  **What**: `tests/WeaveFleet.Infrastructure.Tests/EventBus/InProcessTests.cs` (lines 242-243) uses `WeaveFleet.Infrastructure.Nats.Configuration.ProjectionRegistry` — update to new namespace after step 1.
  **Files**: `tests/WeaveFleet.Infrastructure.Tests/EventBus/InProcessTests.cs`
  **Acceptance**: InProcess event bus tests compile and pass

- [x] 8. **Clean up launch profile and CI config**
  **What**: (a) Remove the `"WeaveFleet.Api (NATS)"` profile from `launchSettings.json`. Remove `Fleet__EventBus__Transport` from the remaining profile (no longer needed). (b) Remove `Fleet__Nats__Enabled="false"` lines from `.github/workflows/release.yml`.
  **Files**: `src/WeaveFleet.Api/Properties/launchSettings.json`, `.github/workflows/release.yml`
  **Acceptance**: Launch profiles valid JSON; CI workflow valid YAML

- [x] 9. **Remove NATS documentation**
  **What**: Delete `docs/nats-event-substrate-design.md` and `.weave/plans/nats-event-substrate.md`.
  **Files**: `docs/nats-event-substrate-design.md` (delete), `.weave/plans/nats-event-substrate.md` (delete)
  **Acceptance**: Files gone

- [x] 10. **Final grep verification**
  **What**: Run `rg -i "nats" src/ tests/` and verify zero hits. Run `rg "NATS" src/ tests/` for case-sensitive check. Fix any remaining references (comments, doc strings, etc.).
  **Acceptance**: No NATS references in source or test code

## Verification
- [x] `dotnet build` succeeds (all projects)
- [x] `dotnet test` passes (all test projects)
- [x] `rg -i "nats" src/ tests/` returns zero results
- [x] No `NATS.Net` in any csproj
- [x] No nats-server binaries in output directory
