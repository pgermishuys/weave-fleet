# Learnings: Pooled OpenCode Lifecycle & Capabilities

## Task 1: Add `RuntimeMode` to Session entity
- **Discrepancy**: Plan referenced `src/WeaveFleet.Infrastructure/Persistence/Migrations/`, but this repository stores SQL migrations under `src/WeaveFleet.Infrastructure/Migrations/` and session persistence mapping in `src/WeaveFleet.Infrastructure/Data/Repositories/SessionRepository.cs` also had to be updated.
- **Resolution**: Added migration `024_add_session_runtime_mode.sql` in the actual migrations folder and updated `SessionRepository` insert/read mapping for `runtime_mode`.
- **Suggestion**: Include repository persistence mappings and actual migrations path in future backend schema-change tasks.

## Task 2: Define `SessionActionCapabilities` DTO
- **Discrepancy**: The plan targeted `src/WeaveFleet.Domain/DTOs/SessionActionCapabilities.cs`, but existing DTO conventions mostly use `WeaveFleet.Application.DTOs`; no existing Domain DTO folder was present.
- **Resolution**: Created `WeaveFleet.Domain.DTOs.SessionActionCapabilities` as requested and registered it in API JSON serialization. The API currently exposes it on detail response as `Actions`, while later plan tasks require a `capabilities` field and list/WS coverage.
- **Suggestion**: Specify the external JSON field name (`capabilities`) consistently when introducing the DTO to avoid temporary API naming drift.

## Task 3: Implement capabilities computation
- **Discrepancy**: The plan referenced `busy` activity status, while existing code commonly uses effective activity statuses such as `working`/`idle`; resolver tests currently cover the plan's `busy` value directly.
- **Resolution**: Added `SessionCapabilitiesResolver` with state-matrix unit coverage and tracker-based live-instance behavior. Running lifecycle with no live instance is treated as effectively disconnected.
- **Suggestion**: Clarify canonical activity status values in plans and align capability rules with existing tracker/effective status naming.

## Task 4: Include capabilities in session API responses
- **Discrepancy**: There was no `src/WeaveFleet.Api/DTOs/SessionListItemDto.cs`; list DTOs are in `src/WeaveFleet.Application/DTOs/SessionDtos.cs`, and WS/status payloads are spread across API, application, and infrastructure fan-out/relay code.
- **Resolution**: Added `Capabilities` to list/detail DTOs, registered `SessionCapabilitiesResolver`, and enriched session status/activity WS payloads with `capabilities`.
- **Suggestion**: Future API/WS tasks should list concrete DTO and event fan-out files instead of a generic WS publisher reference.

## Task 5: Implement auto-activation in prompt path
- **Discrepancy**: Auto-activation requires a persisted `HarnessResumeToken`; an automatic session without one still returns `Instance.NotFound`.
- **Resolution**: Added `GetOrActivateInstanceAsync` for prompt flow that resumes automatic sessions, registers the instance, updates persisted session resume state, and sends the prompt in one call. Manual missing-instance behavior remains unchanged.
- **Suggestion**: Plans should explicitly call out required resume token persistence and any expected fallback behavior when it is absent.

## Task 7: Lifecycle status transitions for auto-activated sessions
- **Discrepancy**: The plan named a generic WS event publisher, but auto-activation status broadcasting required new source-generated payload types plus repository/fake status mapping updates.
- **Resolution**: Added activation success/error status broadcasts with capabilities, lifecycle `running`/`error` persistence, and targeted orchestrator coverage.
- **Suggestion**: Lifecycle transition tasks should list serialization context and repository status mapping files when broadcasts introduce new payload shapes.

## Task 8: API endpoint guards — enforce capabilities server-side
- **Discrepancy**: The plan mentioned Stop/Resume/Restart endpoints, but `SessionEndpoints.cs` has no restart endpoint to guard.
- **Resolution**: Added capability guards for the existing Resume and Stop endpoints and return 409 with resolver disabled reasons when actions are not allowed.
- **Suggestion**: Plans should verify endpoint inventory before naming route-specific guard work.

## Task 14: NuCode RuntimeMode and auto-activation
- **Discrepancy**: NuCode has a `ResumeAsync` method, but it spawns a fresh in-process runtime and does not rehydrate durable NuCode session state from a resume token.
- **Resolution**: Kept NuCode sessions manual via existing runtime-mode resolution, added a TODO in NuCode runtime, and confirmed the resolver treats manual NuCode sessions as resumable rather than auto-promptable.
- **Suggestion**: Plans should distinguish manual resume support from automatic lazy activation support for each harness.

## Task 15: Fleet restart recovery scenario
- **Discrepancy**: Startup reconciliation lives in `Program.cs` through `InstanceService.MarkAllNonTerminalSessionsStoppedAsync()` and repository recovery, not in `SessionOrchestrator`.
- **Resolution**: Updated repository recovery to skip automatic sessions while still stopping manual sessions, and added repository/fake regression coverage.
- **Suggestion**: Restart-recovery tasks should identify the startup host service and repository method that performs reconciliation.

## Task 17: Backend unit tests — capabilities resolver
- **Discrepancy**: The plan referenced `tests/WeaveFleet.UnitTests/Services/SessionCapabilitiesResolverTests.cs`, but resolver tests live under `tests/WeaveFleet.Application.Tests/Services/`.
- **Resolution**: Expanded the existing Application test file with the full state matrix and measured 100% resolver branch coverage.
- **Suggestion**: Test tasks should use the repository's existing test project naming conventions instead of assuming a UnitTests project.

## Task 20: API endpoint guard tests
- **Discrepancy**: The plan referenced an integration test path, but endpoint host tests use `tests/WeaveFleet.Api.Tests/Endpoints/`; no restart endpoint exists to test.
- **Resolution**: Added API endpoint tests for Resume and Stop guard conflicts with resolver disabled reasons.
- **Suggestion**: Endpoint guard test tasks should target the API test project and only include routes that exist.
