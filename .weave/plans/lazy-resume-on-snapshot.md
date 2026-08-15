# Lazy Resume on Snapshot Request

## TL;DR
When a user views a session after Fleet restart, trigger a lazy resume of the OpenCode harness so full conversation history is fetched from OpenCode's persisted session data, instead of returning partial SQLite fallback.

## Context
`OpenCodeSessionMessageProxy.GetSnapshotAsync()` checks `InstanceTracker` for a live harness. After restart, all in-memory state is gone. The proxy falls back to SQLite which has incomplete data. However, `SessionOrchestrator.GetOrActivateInstanceAsync()` already has lazy-resume logic but only for `RuntimeMode == "automatic"` sessions. Direct-mode sessions (the default) short-circuit with an error.

Key files:
- `src/WeaveFleet.Infrastructure/Services/OpenCodeSessionMessageProxy.cs` — the proxy (fix goes here)
- `src/WeaveFleet.Application/Services/SessionOrchestrator.cs` — has `GetOrActivateInstanceAsync` and `ActivateAutomaticSessionAsync`
- `src/WeaveFleet.Application/Services/InstanceTracker.cs` — in-memory registry
- `src/WeaveFleet.Domain/Entities/Session.cs` — `HarnessResumeToken`, `RuntimeMode`

The `GetOrActivateInstanceAsync` pattern (line 1660) already handles:
- Double-check with semaphore lock
- Calling `ActivateAutomaticSessionAsync` which does full resume (prepare runtime → resume → register instance)

## Scope
- In scope: Make the proxy trigger session resume when harness is missing but `HarnessResumeToken` exists, for both direct and automatic runtime modes.
- Out of scope: Changing how resume tokens are persisted, changing the SSE subscription model, pooled-mode specific changes (the resume logic already handles both modes internally).
- Constraints/assumptions:
  - Resume should be a full resume (with SSE subscription) so subsequent events flow normally.
  - Must be lazy — only triggered when a user actually requests the snapshot.
  - Must handle stale tokens gracefully (OpenCode session deleted from disk).
  - Must not deadlock with existing activation locks.

## Objectives
- After Fleet restart, navigating to a session with a resume token restores full conversation history.
- Works for both `manual` and `automatic` runtime modes.
- Gracefully degrades to partial snapshot if resume fails (stale token, missing process).

## Dependencies and Order
1. Extract/generalize the activation logic first (Task 1) so both modes can resume.
2. Wire the proxy to call the activation logic (Task 2).
3. Add tests (Task 3).

## Tasks

- [x] 1. Generalize `GetOrActivateInstanceAsync` to support all runtime modes
  - **What**: Remove the `RuntimeMode == "automatic"` gate in `GetOrActivateInstanceAsync` (line 1671). Instead, check if `HarnessResumeToken` is present regardless of mode. The existing `ActivateAutomaticSessionAsync` method already does the right thing (it checks `HarnessResumeToken` at line 1700). Rename or refactor so it's clear this isn't automatic-only. Alternatively, extract a new method `EnsureHarnessActivatedAsync(Session, CancellationToken)` that both the orchestrator and proxy can call.
  - **Files**: `src/WeaveFleet.Application/Services/SessionOrchestrator.cs`
  - **Depends on**: None
  - **Acceptance**:
    - `GetOrActivateInstanceAsync` no longer rejects direct-mode sessions that have a resume token.
    - Automatic-mode sessions continue to work identically.
    - Sessions with no resume token and no active instance still return `NotFound`.

- [x] 2. Make the proxy trigger lazy resume before falling back
  - **What**: In `OpenCodeSessionMessageProxy.GetSnapshotAsync()`, when `instanceTracker.Get()` returns null and `session.HarnessResumeToken` is not null, call the orchestrator's activation method to resume the session. If resume succeeds, proceed with fetching from the live harness. If resume fails (stale token, runtime error), log warning and fall through to the existing persisted fallback. Same change needed in `GetMessagesAsync()`.
  - **Files**: `src/WeaveFleet.Infrastructure/Services/OpenCodeSessionMessageProxy.cs`
  - **Depends on**: Task 1
  - **Acceptance**:
    - Proxy attempts resume when harness is missing but resume token exists.
    - Successful resume returns `IsPartial = false` snapshot from live harness.
    - Failed resume logs warning and returns `IsPartial = true` fallback.
    - No infinite loops or deadlocks with the activation semaphore.

- [x] 3. Handle circular dependency between proxy and orchestrator
  - **What**: The proxy is already injected into the orchestrator (line 38 of SessionOrchestrator). Injecting the orchestrator into the proxy would create a circular dependency. Solution options: (a) Extract an `ISessionActivator` interface with a single method, implemented by the orchestrator, injected into the proxy. (b) Use `Lazy<SessionOrchestrator>`. (c) Extract activation into a standalone service. Option (a) is cleanest.
  - **Files**:
    - `src/WeaveFleet.Application/Services/ISessionActivator.cs` (new)
    - `src/WeaveFleet.Application/Services/SessionOrchestrator.cs` (implement interface)
    - `src/WeaveFleet.Infrastructure/Services/OpenCodeSessionMessageProxy.cs` (inject interface)
    - `src/WeaveFleet.Infrastructure/DependencyInjection.cs` or `src/WeaveFleet.Application/DependencyInjection.cs` (register)
  - **Depends on**: Task 1
  - **Acceptance**:
    - No circular DI registrations.
    - `ISessionActivator` has a method like `Task<Result<IHarnessSession>> ActivateSessionAsync(string sessionId, CancellationToken ct)`.
    - Proxy uses this interface, not a direct orchestrator reference.

- [x] 4. Add unit tests for the proxy resume path
  - **What**: Test the proxy with a mocked `ISessionActivator` that: (a) returns a harness session successfully, (b) returns failure (stale token), (c) throws. Verify the proxy returns correct `IsPartial` flag in each case.
  - **Files**: `tests/WeaveFleet.Infrastructure.Tests/Services/OpenCodeSessionMessageProxyTests.cs` (new or existing)
  - **Depends on**: Tasks 2, 3
  - **Acceptance**:
    - Test: resume succeeds → snapshot from harness, `IsPartial = false`.
    - Test: resume fails → fallback snapshot, `IsPartial = true`.
    - Test: session has no resume token → no activation attempted, fallback returned.

- [x] 5. Add integration test for post-restart scenario
  - **What**: Test that after clearing `InstanceTracker` (simulating restart), requesting a snapshot for a session with a resume token triggers activation and returns full history.
  - **Files**: `tests/WeaveFleet.IntegrationTests/Sessions/` (new test class)
  - **Depends on**: Tasks 1-3
  - **Acceptance**:
    - Full round-trip: session created → instance tracker cleared → snapshot requested → resume triggered → full messages returned.

## Verification
```bash
dotnet build src/WeaveFleet.Api -c Release
dotnet test tests/WeaveFleet.Infrastructure.Tests --filter "OpenCodeSessionMessageProxy"
dotnet test tests/WeaveFleet.IntegrationTests --filter "LazyResume"
dotnet test tests/WeaveFleet.Application.Tests --filter "GetOrActivate"
```
All tests pass. No circular dependency exceptions at startup.
