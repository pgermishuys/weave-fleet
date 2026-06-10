# Pooled OpenCode Lifecycle & Capabilities

## TL;DR
> **Summary**: Add per-session capabilities DTO and server-side auto-activation so pooled/automatic harness sessions never require manual Resume — prompt triggers lazy rebind, and the frontend renders actions from backend-driven capabilities instead of hardcoded lifecycle logic.
> **Estimated Effort**: Medium

## Context
### Original Request
Productize pooled OpenCode (Option B) by completing lifecycle/capabilities/auto-activation before release. After Fleet restart, pooled sessions appear stopped but users should be able to prompt without manual Resume.

### Key Findings
1. `Session` entity has `HarnessType` (string) and `LifecycleStatus` but no persisted runtime mode (pooled vs per-session). The global setting can change after session creation.
2. `InstanceTracker.Get()` returns null for sessions without a live harness process. `SessionOrchestrator.GetLiveInstanceAsync` returns `NotFound` error in that case, blocking prompt.
3. Frontend computes `canResume`/`canStop` locally from `lifecycleStatus` — no backend capabilities DTO exists.
4. `SessionActionToolbar.vue` already accepts `canResume`/`canStop`/`canAbort` as props but they're computed client-side.
5. Pooled infrastructure (registry, lease, demux) is complete per the pooled-opencode-harness plan — all items checked.
6. NuCode harness exists (`NuCodeHarnessSession.cs`, `NuCodeHarnessRuntime.cs`) with similar lifecycle.

## Objectives
### Core Objective
Enable seamless prompting of pooled/automatic harness sessions without manual lifecycle actions, driven by backend capabilities.

### Deliverables
- [x] Persist `RuntimeMode` on `Session` entity at creation time
- [x] `SessionActionCapabilities` DTO computed server-side and included in session responses
- [x] Auto-activation in `PromptSessionAsync` for automatic-mode sessions with no live instance
- [x] Frontend consumes capabilities from API instead of computing locally
- [x] NuCode semantics handled (hide Stop/Resume, allow prompt via auto-activation or explicit TODO)

### Definition of Done
- [x] User can prompt a pooled session after Fleet restart without clicking Resume
- [x] Context menu/toolbar actions match backend capabilities exactly
- [x] `dotnet test` passes; frontend tests pass
- [x] Feature flag OFF → no behavior change

### Guardrails (Must NOT)
- Must NOT remove manual Resume for per-session (non-pooled) OpenCode — those still need it
- Must NOT change REST API paths (additive DTO fields only)
- Must NOT allow prompting archived sessions (backend guard remains)
- Must NOT expose internal harness type details to frontend beyond capabilities

## TODOs

### Phase 1: Backend — Runtime Mode & Capabilities DTO

- [x] 1. Add `RuntimeMode` to Session entity
  **What**: Add `RuntimeMode` string property (`"manual"` | `"automatic"`) to `Session`. Set at creation time based on current pooled setting + harness type. Migrate existing sessions to `"manual"` default. This decouples the session's behavior from the global setting changing later.
  **Files**: `src/WeaveFleet.Domain/Entities/Session.cs`, `src/WeaveFleet.Infrastructure/Persistence/Migrations/` (new migration), `src/WeaveFleet.Application/Services/SessionOrchestrator.cs` (set on create)
  **Acceptance**: New sessions get correct RuntimeMode; existing sessions default to "manual"

- [x] 2. Define `SessionActionCapabilities` DTO
  **What**: Create a DTO with boolean properties: `canPrompt`, `canStop`, `canResume`, `canRestart`, `canAbort`, `canArchive`, `canUnarchive`, `canFork`, `canDelete`. Optionally include `disabledReason` string per action for tooltip UX.
  **Files**: `src/WeaveFleet.Domain/DTOs/SessionActionCapabilities.cs`
  **Acceptance**: DTO compiles; used in session response

- [x] 3. Implement capabilities computation
  **What**: Create `SessionCapabilitiesResolver` that computes capabilities from: `RuntimeMode`, `LifecycleStatus`, `RetentionStatus`, `ActivityStatus`, whether instance is live (`InstanceTracker.Get != null`). Key rules:
  - `automatic` + stopped/disconnected/completed → `canPrompt=true`, `canResume=false`, `canStop=false`
  - `manual` + stopped/disconnected → `canPrompt=false`, `canResume=true`
  - `manual` + running → `canPrompt=true`, `canStop=true`
  - archived → `canPrompt=false`, `canArchive=false`, `canUnarchive=true`
  - running + busy → `canAbort=true`
  **Files**: `src/WeaveFleet.Application/Services/SessionCapabilitiesResolver.cs`
  **Acceptance**: Unit tests cover all state combinations

- [x] 4. Include capabilities in session API responses
  **What**: Add `capabilities` field to session list item and session detail DTOs returned by `GET /api/sessions` and `GET /api/sessions/{id}`. Compute via resolver. Also emit in WS session-status-changed events so frontend stays in sync.
  **Files**: `src/WeaveFleet.Api/Endpoints/SessionEndpoints.cs`, `src/WeaveFleet.Api/DTOs/SessionListItemDto.cs` (or equivalent mapping), WS event publisher
  **Acceptance**: API responses include capabilities; WS events include capabilities

### Phase 2: Backend — Auto-Activation on Prompt

- [x] 5. Implement auto-activation in `GetLiveInstanceAsync` / `PromptSessionCoreAsync`
  **What**: When `GetLiveInstanceAsync` returns NotFound AND session's `RuntimeMode == "automatic"`: instead of returning error, call the harness runtime's `ResumeAsync` (which for pooled mode acquires a lease and lazy-starts the process), register the instance in `InstanceTracker`, then proceed with the prompt. This must be atomic from the caller's perspective — one API call, no frontend retry needed.
  **Files**: `src/WeaveFleet.Application/Services/SessionOrchestrator.cs` (modify `GetLiveInstanceAsync` or add `GetOrActivateInstanceAsync`)
  **Acceptance**: Prompt to a stopped automatic session succeeds without prior Resume call; prompt to a stopped manual session still fails with "instance not found"

- [x] 6. Auto-activation for commands (slash commands)
  **What**: Same pattern as prompt — `CommandSessionAsync` and any other paths that call `GetLiveInstanceAsync` should use the auto-activate variant for automatic sessions.
  **Files**: `src/WeaveFleet.Application/Services/SessionOrchestrator.cs`
  **Acceptance**: Slash command to stopped automatic session auto-activates

- [x] 7. Lifecycle status transitions for auto-activated sessions
  **What**: After auto-activation succeeds, update session's `LifecycleStatus` to `"running"` and broadcast status change (with updated capabilities) via WS. If auto-activation fails (e.g., harness crash loop), set `LifecycleStatus` to `"error"` and return appropriate error to caller.
  **Files**: `src/WeaveFleet.Application/Services/SessionOrchestrator.cs`, WS event publisher
  **Acceptance**: Frontend sees lifecycle transition to running after successful auto-activate prompt

- [x] 8. API endpoint guards — enforce capabilities server-side
  **What**: Stop/Resume/Restart endpoints must check capabilities before executing. E.g., if `canResume=false` (automatic session), the Resume endpoint returns 409/400 with reason. This ensures UI hiding is not the only protection.
  **Files**: `src/WeaveFleet.Api/Endpoints/SessionEndpoints.cs`
  **Acceptance**: Calling Resume on an automatic session returns error; calling Stop on a stopped session returns error

### Phase 3: Frontend — Capabilities-Driven UI

- [x] 9. Add capabilities to frontend session types
  **What**: Extend `SessionListItem` type with optional `capabilities` object. Define `SessionActionCapabilities` TypeScript interface matching the backend DTO.
  **Files**: `client/src/lib/api-types.ts`, `client/src/lib/types.ts`
  **Acceptance**: Types compile; no runtime errors

- [x] 10. Replace client-side capability computation with backend capabilities
  **What**: In `SessionDetailPanel.vue` and `SessionItem.vue`, replace the local `canResume`/`canStop`/`canAbort`/`canArchive` computed properties with values from `session.capabilities`. Fall back to current local logic if capabilities are undefined (backward compat during rollout).
  **Files**: `client/src/components/session/SessionDetailPanel.vue`, `client/src/components/sessions/SessionItem.vue`
  **Acceptance**: Actions shown/hidden match backend capabilities; no Resume button for automatic sessions

- [x] 11. Composer prompt-ability from capabilities
  **What**: Composer's "can send" logic should incorporate `capabilities.canPrompt`. If `canPrompt` is true, composer is enabled regardless of `lifecycleStatus`. Remove or gate the existing check that disables composer when lifecycle is stopped/disconnected.
  **Files**: `client/src/composables/use-send-prompt.ts`, Composer component
  **Acceptance**: Composer enabled for stopped automatic session; disabled for stopped manual session; disabled for archived session

- [x] 12. Update WS event handling to refresh capabilities
  **What**: When session-status-changed WS event arrives with capabilities, update the session store's capabilities. This keeps UI in sync after auto-activation or external state changes.
  **Files**: `client/src/composables/use-session-events.ts`, `client/src/stores/sessions.ts`
  **Acceptance**: After prompting a stopped automatic session, toolbar updates without page refresh

- [x] 13. Remove harness-type-specific frontend branching
  **What**: Audit frontend for any `if (harnessType === 'opencode')` or `if (harnessType === 'nucode')` branching that controls action visibility. Replace with capabilities-driven logic. Document any remaining harness-specific UI (e.g., model picker availability) as separate from action capabilities.
  **Files**: Various frontend components (audit and fix)
  **Acceptance**: No action visibility logic references harness type directly

### Phase 4: NuCode & Edge Cases

- [x] 14. NuCode RuntimeMode and auto-activation
  **What**: If NuCode sessions can auto-activate (similar lazy-start pattern), set `RuntimeMode = "automatic"` and implement auto-activation in `NuCodeHarnessRuntime.ResumeAsync`. If NuCode cannot yet auto-activate, set `RuntimeMode = "manual"` and add a TODO comment. Either way, capabilities resolver handles NuCode correctly.
  **Files**: `src/WeaveFleet.Infrastructure/Harnesses/NuCode/NuCodeHarnessRuntime.cs`, `src/WeaveFleet.Application/Services/SessionCapabilitiesResolver.cs`
  **Acceptance**: NuCode sessions show correct capabilities; if automatic, prompt auto-activates; if manual, Resume is shown

- [x] 15. Fleet restart recovery scenario
  **What**: On Fleet startup, sessions persisted as `"running"` with `RuntimeMode = "automatic"` but no live instance should NOT be force-transitioned to `"stopped"`. Instead, leave them as-is — the auto-activation path handles them on next prompt. For `"manual"` sessions, existing behavior (mark as disconnected/stopped) remains.
  **Files**: `src/WeaveFleet.Application/Services/SessionOrchestrator.cs` (startup reconciliation if any), or startup host service
  **Acceptance**: After Fleet restart, automatic sessions are promptable immediately; manual sessions show Resume

- [x] 16. Concurrent auto-activation guard
  **What**: If two prompts arrive simultaneously for the same stopped automatic session, only one should trigger activation; the other should wait/retry. Use a per-session semaphore or similar to serialize activation attempts.
  **Files**: `src/WeaveFleet.Application/Services/SessionOrchestrator.cs`
  **Acceptance**: Concurrent prompts don't spawn duplicate instances; both succeed after single activation

### Phase 5: Tests & Rollout

- [x] 17. Backend unit tests — capabilities resolver
  **What**: Test all state matrix combinations: (manual|automatic) × (running|stopped|completed|disconnected|error) × (active|archived) × (instance live|not live) × (busy|idle).
  **Files**: `tests/WeaveFleet.UnitTests/Services/SessionCapabilitiesResolverTests.cs`
  **Acceptance**: Full matrix covered; >95% branch coverage on resolver

- [x] 18. Backend integration tests — auto-activation flow
  **What**: Test: create pooled session → stop it (or simulate Fleet restart removing instance) → send prompt → verify auto-activation → verify response arrives. Also test: manual session → stop → prompt → verify error returned.
  **Files**: `tests/WeaveFleet.IntegrationTests/Sessions/AutoActivationTests.cs`
  **Acceptance**: Both scenarios pass

- [x] 19. Frontend tests — capabilities-driven rendering
  **What**: Update existing `SessionDetailPanel` and `SessionItem` tests to provide capabilities in session data. Verify correct button visibility. Add test for composer enabled/disabled based on `canPrompt`.
  **Files**: `client/src/components/session/__tests__/SessionDetailPanel.files-changed.test.ts`, `client/src/components/__tests__/SessionItem.test.ts`, `client/src/components/__tests__/Composer.test.ts`
  **Acceptance**: Tests pass with capabilities-driven logic

- [x] 20. API endpoint guard tests
  **What**: Test that Resume/Stop/Restart endpoints return appropriate errors when capabilities say the action is not allowed.
  **Files**: `tests/WeaveFleet.IntegrationTests/Sessions/EndpointGuardTests.cs`
  **Acceptance**: Unauthorized actions rejected with clear error messages

- [x] 21. Rollout gating
  **What**: Ensure pooled feature flag remains OFF by default. Auto-activation only applies to `RuntimeMode = "automatic"` sessions (which only exist when pooled flag is ON). Document that this plan is safe to merge with flag OFF — no behavior change for existing users.
  **Acceptance**: Default deploy unchanged; opt-in via settings toggle enables full flow

  **Rollout note**: Safe to merge with pooled OpenCode disabled. `Fleet:Harness:PooledOpenCodeHarness` remains `false` in default, Development, and Cloud appsettings, and the per-user `PooledOpenCodeHarness` preference defaults to `false` in the settings UI. With the flag/preference off, new OpenCode sessions resolve to `RuntimeMode = "manual"`, so existing per-session lifecycle behavior is unchanged. Opting in via the settings toggle creates new OpenCode sessions in pooled automatic mode, enabling lazy auto-activation on prompt after restart while existing sessions keep their persisted runtime mode.

## Verification
- [x] `dotnet test` — all unit + integration tests green
- [x] `npm run test` (frontend) — all tests green
- [x] Feature flag OFF → no behavior change (manual sessions work as before)
- [x] Feature flag ON → pooled session promptable after Fleet restart without Resume
- [x] Capabilities DTO present in `GET /api/sessions` and `GET /api/sessions/{id}` responses
- [x] WS events include capabilities on status change
- [x] No Resume/Stop buttons shown for automatic sessions in stopped state
- [x] Composer enabled for stopped automatic sessions
- [x] Resume endpoint returns error for automatic sessions
- [x] Concurrent prompt to stopped automatic session doesn't duplicate instances
- [x] NuCode sessions show correct capabilities (automatic or manual per implementation)
