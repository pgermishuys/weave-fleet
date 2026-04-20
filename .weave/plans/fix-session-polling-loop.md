# Fix Session Polling Loop

## TL;DR
> **Summary**: Break the reactive loop caused by `Object.assign` on deep-reactive store objects triggering cascading re-renders and redundant fetches. Remove the duplicate `/api/sessions/{id}` fetch in SessionDetailPanel.
> **Estimated Effort**: Medium

## Context
### Original Request
Fix a bug where navigating to a session detail page causes `/api/sessions/{id}` to fire every ~1ms in a continuous loop.

### Key Findings
1. **Two independent fetchers** hit the same endpoint: `sessions.$id.tsx:120` (route component) and `SessionDetailPanel.vue:292` (right panel). Both watch reactive values derived from the store.
2. **Root cause**: `upsertSession` (store line 37) does `Object.assign(existingSession, nextSession)` on a deep `ref<SessionListItem[]>`. This mutates the reactive proxy in-place, triggering all watchers that read from `sessions` — including `selectedSession` in RightPanel.vue (line 88) which is passed as a prop to SessionDetailPanel. The panel's watcher on `[sessionId, sessionInstanceId, refreshVersion]` re-fires because `sessionInstanceId` is `computed(() => props.session?.instanceId)` and the prop reference changed due to deep reactivity.
3. **`syncSessionStore`** in `use-session-events.ts:510` has the same `Object.assign(session, patch)` pattern — also mutates deep reactive objects.
4. **`patchSession`** in the store (line 31) has the same pattern.
5. **`sessionsChanged`** in `session-utils.ts` already implements structural equality checking for the session list — this pattern should be extended to single-session upserts.
6. **SessionDetailPanel fetch is redundant** — it receives the session as a prop and the route component already fetches `/api/sessions/{id}`.

## Objectives
### Core Objective
Eliminate the reactive feedback loop and remove redundant network requests.

### Deliverables
- [ ] Store mutations that avoid triggering watchers when data is structurally unchanged
- [ ] Removal of the redundant `/api/sessions/{id}` fetch in SessionDetailPanel
- [ ] Safe `syncSessionStore` in use-session-events.ts
- [ ] Tests covering the new behavior

### Definition of Done
- [ ] `npx vitest run` passes with no failures
- [ ] Navigating to a session detail page produces exactly 1 `/api/sessions/{id}` request (from the route component), not a continuous loop
- [ ] SessionDetailPanel still fetches `/api/sessions/{id}/messages` and diffs (those are unique to the panel)

### Guardrails (Must NOT)
- Do NOT change the store from `ref` to `shallowRef` (would break other deep-reactive consumers)
- Do NOT remove the `refreshVersion` mechanism in SessionDetailPanel (it's used for explicit refresh after actions)
- Do NOT change server-side code or WebSocket event shapes
- Do NOT remove the route component's fetch (it's the canonical source of session detail data)

## TODOs

- [ ] 1. Add `sessionChanged` utility for single-session structural equality
  **What**: Add a `sessionChanged(prev: SessionListItem, next: SessionListItem): boolean` function to `session-utils.ts`, following the same field-comparison pattern as the existing `sessionsChanged`. This compares all UI-visible fields and returns `false` when nothing changed.
  **Files**: `src/lib/session-utils.ts`
  **Acceptance**: Function exported and unit-testable. Compares `session.id`, `session.title`, `sessionStatus`, `activityStatus`, `lifecycleStatus`, `retentionStatus`, `archivedAt`, `isHidden`, `instanceStatus`, `instanceId`, `totalTokens`, `totalCost`.

- [ ] 2. Make store `upsertSession` skip mutation when data is unchanged
  **What**: In `upsertSession`, before `Object.assign(existingSession, nextSession)`, call `sessionChanged(existingSession, nextSession)`. If it returns `false`, return early without mutating. This breaks the reactive loop at the source.
  **Files**: `src/stores/sessions.ts`
  **Acceptance**: Calling `upsertSession` with identical data does not trigger Vue reactivity (no watcher re-runs).

- [ ] 3. Make store `patchSession` skip mutation when patch values match current
  **What**: In `patchSession`, before `Object.assign(session, patch)`, check if every key in `patch` already matches the current value on `session`. If all match, return early.
  **Files**: `src/stores/sessions.ts`
  **Acceptance**: Calling `patchSession` with values that already match does not trigger reactivity.

- [ ] 4. Make `syncSessionStore` in use-session-events.ts skip no-op mutations
  **What**: In the `syncSessionStore` function (line 497-511), before `Object.assign(session, patch)`, check if every key in `patch` already matches. If all match, return early. Same guard as TODO 3.
  **Files**: `src/composables/use-session-events.ts`
  **Acceptance**: WebSocket events that repeat the same status don't trigger store reactivity cascades.

- [ ] 5. Remove redundant `/api/sessions/{id}` fetch from SessionDetailPanel
  **What**: Remove the `apiFetch(\`/api/sessions/${...}\`)` call from the first watcher in SessionDetailPanel.vue (lines 292-300). Keep the `/messages` fetch but source the `instanceId` from props instead of the removed `remoteSessionDetail`. Remove the `remoteSessionDetail` ref and all computed properties that fall back to it (they already have prop-based values). The watcher should only fetch messages and metadata, not the session detail itself.
  **Files**: `src/components/session/SessionDetailPanel.vue`
  **Acceptance**: SessionDetailPanel no longer calls `/api/sessions/{id}` (GET with no suffix). It still calls `/api/sessions/{id}/messages`. All computed properties still work using prop data.

- [ ] 6. Add tests for `sessionChanged` utility
  **What**: Add test cases in a new or existing test file for `sessionChanged`: returns `false` for identical sessions, returns `true` when any compared field differs.
  **Files**: `src/lib/__tests__/session-utils.test.ts`
  **Acceptance**: Tests pass via `npx vitest run src/lib/__tests__/session-utils.test.ts`.

- [ ] 7. Add tests for store `upsertSession` no-op behavior
  **What**: Extend `src/stores/__tests__/sessions.test.ts` with tests that verify: (a) `upsertSession` with identical data does not change the array reference or trigger deep mutation, (b) `upsertSession` with changed data still works, (c) `patchSession` with matching values is a no-op.
  **Files**: `src/stores/__tests__/sessions.test.ts`
  **Acceptance**: Tests pass via `npx vitest run src/stores/__tests__/sessions.test.ts`.

- [ ] 8. Add test for `syncSessionStore` no-op behavior
  **What**: Add a test in `src/composables/__tests__/use-session-events.test.ts` that verifies `syncSessionStore` (via `handleEvent` with a `session.status` event) does not trigger store mutation when the status is already the same. Use the existing `mountComposable` + mock pattern.
  **Files**: `src/composables/__tests__/use-session-events.test.ts`
  **Acceptance**: Tests pass via `npx vitest run src/composables/__tests__/use-session-events.test.ts`.

## Verification
- [ ] `npx vitest run` — all tests pass
- [ ] No regressions in session list polling (15s interval still works)
- [ ] Manual verification: navigate to session detail, observe exactly 1 `/api/sessions/{id}` request in Network tab (from route component), no loop
- [ ] SessionDetailPanel still shows agent/model metadata (from `/messages` endpoint)
- [ ] Session actions (abort, resume, stop, etc.) still trigger `refreshVersion` and re-fetch messages
