# Session Idle/Delegation State Handling

## TL;DR
> **Summary**: Fix premature busy→idle flips during subagent delegation by deriving UI state from explicit idle signals AND delegation lifecycle, not turn/message completion.
> **Estimated Effort**: Medium
> **Pull Request**: https://github.com/pgermishuys/weave-fleet/pull/116

## Context
### Original Request
Improve session busy/idle/delegating state so the parent session doesn't appear idle while child sessions are still in flight.

### Key Findings
- **`domain-event-reducer.ts` line 73-77**: `turn.ended` immediately sets `sessionStatus: "idle"` — this fires before delegations complete.
- **`use-session-events.ts` line 716**: `message.updated` triggers an idle fallback timer that can flip state prematurely.
- **Reliable idle signals**: authoritative idle must come from explicit idle events in the relevant stream path, not message/tool completion.
  - **v2/domain reducer path**: `session.idled`
  - **v1/socket path**: `session.idle`, `session.status` with `status: "idle"`, and `activity_status` with `activityStatus: "idle"`
- **Captured opencode evidence is consistent across two logs**:
  - `.weave/learnings/opencode-delegated-session.log`
  - `.weave/learnings/opencode-subagent-session.log`
  Both show the child/subagent session becoming truly idle only after explicit `session.status: idle` + `session.idle`, and both show the parent remaining effectively non-idle through child tool/message completion.
- **Important ordering from the second log**: child explicit idle can arrive before the parent task tool is marked completed, so parent UI state must not depend on task completion alone.
- **Delegations already tracked**: `SessionStreamState.delegations` and `DelegationDto` with status `pending|running|completed|error|cancelled` exist. The reducer handles `delegation.created/updated/completed` but never uses them to influence `sessionStatus`.
- **Delegation semantics need to be explicit**:
  - **Active**: `pending`, `running`
  - **Terminal**: `completed`, `error`, `cancelled`
- **Snapshot already has `delegations`** with status field — can derive active count at init time.

## Objectives
### Core Objective
Parent session shows `busy` or `delegating` while child sessions are in flight; only shows `idle` when explicitly idle AND no active delegations.

### Deliverables
- [x] Tri-state `sessionStatus`: `"idle" | "busy" | "delegating"`
- [x] Derived status logic: explicit idle + no active delegations → idle; active delegations → delegating; otherwise → busy
- [x] Remove `turn.ended` → idle transition from reducer
- [x] UI reflects delegating state (existing busy indicator is acceptable initially)

### Definition of Done
- [x] Sending a prompt that triggers delegation: status stays busy/delegating until all delegations complete AND explicit idle received
- [x] Unit tests cover all state transitions
- [x] No regression: sessions without delegations still flip idle on explicit idle signals
- [x] No visible busy/idle flicker while any delegation remains active
- [x] Event ordering where child explicit idle arrives before parent task completion does not cause parent idle flicker

### Guardrails (Must NOT)
- Do not change the WebSocket protocol or backend event emission
- Do not break v1 stream path; this work intentionally covers **v2 reducer plus targeted v1 idle-fallback hardening**
- Do not introduce new API endpoints

## TODOs

- [x] 1. Extend `SessionStreamState` type
  **What**: Add `explicitStatus: "idle" | "busy"` and computed `sessionStatus` that factors in active delegations. Change `sessionStatus` type to `"idle" | "busy" | "delegating"`. Document that reducer/composable status may be tri-state even if downstream list/store activity badges temporarily map `delegating` to busy/active.
  **Files**: `client/src/lib/domain-event-reducer.ts`
  **Acceptance**: Type compiles; existing tests updated.

- [x] 2. Fix reducer transitions
  **What**: Remove `turn.ended → idle`. Keep `turn.started → busy` and ensure authoritative busy signals reset `explicitStatus` to `"busy"`. In the v2 reducer, only consume domain events it actually receives (not v1 socket event names). For v2 `session.idled`, set `explicitStatus: "idle"`. For delegation events, recompute derived status via helper `deriveSessionStatus(explicitStatus, delegations)` using active=`pending|running` and terminal=`completed|error|cancelled`.
  **Files**: `client/src/lib/domain-event-reducer.ts`
  **Acceptance**: `turn.ended` no longer changes status. Delegation active + explicit idle → delegating. Final terminal delegation after prior explicit idle → idle without needing a second idle event.

- [x] 3. Update `createSessionStreamState` initialization
  **What**: Derive initial `explicitStatus` from snapshot `activityStatus`. Derive initial `sessionStatus` considering active delegations in snapshot.
  **Files**: `client/src/lib/domain-event-reducer.ts`
  **Acceptance**: Snapshot with active delegations initializes as `delegating`, including reconnect/hydration scenarios.

- [x] 4. Propagate tri-state to composables
  **What**: Update `useSessionStream` and `useSessionEventsSwitch` to expose `"idle" | "busy" | "delegating"`. In `use-session-events.ts` v1 path, harden idle fallback logic so it never emits idle while active delegations exist, and ensure fallback can still resolve to idle once all delegations become terminal.
  **Files**: `client/src/composables/use-session-stream.ts`, `client/src/composables/use-session-events-switch.ts`, `client/src/composables/use-session-events.ts`
  **Acceptance**: Composable types compile; downstream consumers handle new value; timer-driven fallback cannot produce idle during active delegation.

- [x] 5. Update UI consumers
  **What**: Treat `"delegating"` same as `"busy"` for now in activity indicators. Update `SessionItem.vue`, `SessionDetailPanel.vue`, `Composer.vue`, route/store adapters, and any session/activity mapping code so `delegating` never normalizes to idle by accident.
  **Files**: `client/src/components/sessions/SessionItem.vue`, `client/src/components/session/SessionDetailPanel.vue`, `client/src/components/session/Composer.vue`, `client/src/routes/sessions.$id.tsx`, `client/src/composables/use-session-events.ts`, `client/src/stores/sessions.ts`, `client/src/lib/api-types.ts`, `client/src/lib/types.ts`
  **Acceptance**: No visual regression; delegating sessions show busy indicator; all downstream busy checks treat `delegating` as active.

- [x] 6. Unit tests for reducer
  **What**: Test sequences: (a) turn.ended with active delegation → stays non-idle, (b) explicit idle before delegation completion → stays delegating, then flips idle when final delegation becomes terminal, (c) multiple delegations — only idle when all active delegations are terminal, (d) `error` and `cancelled` count as terminal, (e) no delegations + session.idled → idle, (f) snapshot idle + active delegation initializes delegating, (g) child explicit idle arrives before parent task completion and parent still remains non-idle until delegation state is terminal in parent state.
  **Files**: `client/src/lib/__tests__/domain-event-reducer.test.ts` (create if needed)
  **Acceptance**: All scenarios pass; reducer never oscillates to idle while any delegation is active.

- [x] 7. Update existing composable tests
  **What**: Fix any broken assertions due to type change. Add delegation-aware test cases, including fake-timer coverage for the v1 idle fallback path.
  **Files**: `client/src/composables/__tests__/use-session-events.test.ts`
  **Acceptance**: All existing tests pass with updated expectations; fallback timer cannot create busy→idle→busy flicker during active delegation.

- [x] 8. Manual event-order verification
  **What**: Replay or simulate the real problematic ordering from captured logs: (1) explicit idle arriving before child completion, (2) child completion arriving before explicit idle, and (3) child explicit idle arriving before parent task completion. Verify the UI remains non-idle until both conditions for idle are satisfied.
  **Files**: N/A (manual verification; optionally add fixture/log-driven helper if useful)
  **Acceptance**: Both event orders produce stable non-idle UI until safe idle.

## Verification
- [x] `pnpm test` passes in `client/`
- [x] Manual test: trigger delegation via fleet, observe parent stays busy/delegating
- [x] No regressions in non-delegation sessions
- [x] TypeScript compiles cleanly (`pnpm typecheck`)
- [x] Manual or fixture-based verification shows no busy→idle→busy oscillation during active delegation
- [x] Captured-log-derived ordering (child idle before parent task complete) is covered by tests or replay verification
