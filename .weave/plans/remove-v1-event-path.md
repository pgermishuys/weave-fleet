# Remove V1 Event Subscription Path

## TL;DR
Delete the V1 session event composable and its bridging switch, making V2 (`use-session-stream.ts`) the sole event path. Remove V1 dispatch from the SignalR socket layer.

## Context
The frontend has parallel V1/V2 event protocols toggled by a `weave_v2_stream` localStorage flag. V2 is fully functional and the default. V1 code adds ~1000 lines of dead-weight complexity. The backend hub sends events identically for both — no backend changes needed.

Key interface gap: consumers (ActivityStream.vue, use-session-todos.ts) destructure `UseSessionEventsResult` which has 18 fields (status, error, reconnect, forceBusy, forceIdle, cacheHit, scrollPositionRef, etc.). `UseSessionStreamResult` only exposes 7 fields. The switch composable currently bridges this by providing stub/no-op values for V2. The plan must either:
- Create a thin wrapper that adds the missing fields as no-ops/defaults, OR
- Update each consumer to only use the fields V2 actually provides.

Consumer analysis:
- **ActivityStream.vue** uses: `messages`, `delegations`, `sessionStatus`, `forceIdle`, `hasMoreMessages`, `isLoadingOlder`, `loadOlderMessages` — `forceIdle` is called but was already a no-op in V2 path. The rest map directly.
- **use-session-todos.ts** uses: `messages` only.

## Scope
- In scope: Delete V1 composable + tests, delete switch composable, rewire consumers to V2, strip V1 from SignalR socket, remove backend comment.
- Out of scope: Backend changes, V2 feature additions, `event-state.ts` (shared by V2's `domain-event-reducer.ts` and `use-session-stream.ts`).
- Constraints: `event-state.ts` must NOT be deleted — it's imported by `domain-event-reducer.ts` and `use-session-stream.ts`. Only the V1-only comment at the top can optionally be updated.

## Objectives
- Remove all V1 event path code and the localStorage feature flag
- Ensure consumers compile and function identically to their current V2 behavior
- Remove V1 dispatch from the SignalR socket module

## Dependencies and Order
1. Update consumers first (tasks 1-2) so they don't import deleted files.
2. Delete V1 files (task 3) after consumers are rewired.
3. Clean up SignalR socket (task 4) — independent of consumer changes but logically after file deletions.
4. Clean up backend comment (task 5) — independent.
5. Verify (task 6) — last.

## Tasks

- [x] 1. Rewire ActivityStream.vue to use `useSessionStream` directly
  - **What**: Replace `import { useSessionEventsSwitch }` with `import { useSessionStream }`. Update the destructure at line 68 to match `UseSessionStreamResult` shape: `messages` → `messages`, `delegations` → `delegations`, `sessionStatus` → `sessionStatus`, `hasMoreMessages` → `hasMore`, `isLoadingOlder` → `isLoadingOlder`, `loadOlderMessages` → `loadOlder`. Remove `forceIdle` usage at line 155 (it was a no-op in V2). Remove `instanceId` argument (V2 doesn't need it). The `enabled` param can be hardcoded to `true` or omitted.
  - **Files**: `client/src/components/session/ActivityStream.vue`
  - **Depends on**: None
  - **Acceptance**:
    - No imports from `use-session-events-switch` or `use-session-events`
    - Destructured fields match `UseSessionStreamResult` (renamed: `hasMore` not `hasMoreMessages`, `loadOlder` not `loadOlderMessages`)
    - `forceIdle()` call removed
    - TypeScript compiles without errors

- [x] 2. Rewire use-session-todos.ts to use `useSessionStream` directly
  - **What**: Replace import and call. Only `messages` is used. Remove `instanceId` parameter from the function signature since V2 doesn't need it. Update any callers of `useSessionTodos` to drop the `instanceId` argument.
  - **Files**: `client/src/composables/use-session-todos.ts`
  - **Depends on**: None
  - **Acceptance**:
    - No imports from `use-session-events-switch`
    - Function signature drops `instanceId` or makes it optional (check callers first)
    - TypeScript compiles

- [x] 3. Delete V1 files
  - **What**: Delete these files entirely:
    - `client/src/composables/use-session-events.ts`
    - `client/src/composables/use-session-events-switch.ts`
    - `client/src/composables/__tests__/use-session-events.test.ts`
  - **Files**: `client/src/composables/use-session-events.ts`, `client/src/composables/use-session-events-switch.ts`, `client/src/composables/__tests__/use-session-events.test.ts`
  - **Depends on**: Tasks 1, 2
  - **Acceptance**:
    - Files no longer exist
    - No remaining imports reference these files (`grep` confirms)

- [x] 4. Strip V1 from SignalR socket
  - **What**: In `client/src/composables/use-signalr-socket.ts`:
    - Remove `topicListeners` map (line 42) and all code that reads/writes it
    - Remove `dispatch()` function (lines 59-68)
    - Remove `addTopicListeners()` function (lines 212-238)
    - Remove `stableSubscribe` (line 394-395)
    - Remove `subscribe` from `WeaveSocketAPI` interface (line 19) and from the return object (line 468)
    - Remove `TopicCallback` type export (line 6)
    - In the `"Event"` handler (lines 135-145): remove the `if (topicListeners.has(topic))` branch, keep only the V2 dispatch
    - In `hasListenersForTopic` (line 186-188): remove the `topicListeners` check, only check `topicListenersV2`
    - In `_resetForTesting` (line 354): remove `topicListeners.clear()`
    - In `resubscribeAll`: it already only resubscribes V2 topics — no change needed
  - **Files**: `client/src/composables/use-signalr-socket.ts`
  - **Depends on**: Task 3
  - **Acceptance**:
    - No references to `topicListeners` (the V1 map) remain
    - `subscribe` no longer in `WeaveSocketAPI`
    - `TopicCallback` type no longer exported
    - Event handler only dispatches to V2

- [x] 5. Remove V1 test suite from SignalR socket tests
  - **What**: Delete the `describe("v1 compatibility", ...)` block (lines 417-453) in the test file.
  - **Files**: `client/src/composables/__tests__/use-signalr-socket.test.ts`
  - **Depends on**: Task 4
  - **Acceptance**:
    - No "v1 compatibility" describe block
    - Remaining tests pass

- [x] 6. Update `use-weave-socket.ts` re-export
  - **What**: Check if `use-weave-socket.ts` re-exports `subscribe` or `TopicCallback`. If so, remove those re-exports.
  - **Files**: `client/src/composables/use-weave-socket.ts`
  - **Depends on**: Task 4
  - **Acceptance**:
    - No V1 types or functions re-exported

- [x] 7. Remove backend comment
  - **What**: Remove or update the comment at line 1348 of `OpenCodeHarnessRuntime.cs` referencing "v1 client".
  - **Files**: `src/WeaveFleet.Infrastructure/Harnesses/OpenCode/OpenCodeHarnessRuntime.cs`
  - **Depends on**: None
  - **Acceptance**:
    - No "v1 client" comment remains

- [x] 8. Update `event-state.ts` comment
  - **What**: Line 3 says "Extracted from useSessionEvents for testability". Update to reflect it's shared utility for domain event reduction (remove V1 reference).
  - **Files**: `client/src/lib/event-state.ts`
  - **Depends on**: None
  - **Acceptance**:
    - Comment no longer references `useSessionEvents`

- [x] 9. Verify
  - **What**: Run `bunx vue-tsc --noEmit` from `client/` to confirm TypeScript compiles. Run `bun run test` from `client/` to confirm all tests pass. Grep for any remaining references to `useSessionEvents`, `useSessionEventsSwitch`, `use-session-events-switch`, `use-session-events`, `weave_v2_stream`, `STREAM_PROTOCOL_V2`, `topicListeners` (non-V2), `TopicCallback`.
  - **Depends on**: All prior tasks
  - **Acceptance**:
    - `vue-tsc --noEmit` exits 0
    - `bun run test` passes
    - No stale references found by grep

## Verification
```bash
cd client
bunx vue-tsc --noEmit
bun run test

# Confirm no stale references
rg -l 'useSessionEvents[^S]|use-session-events[^-]|useSessionEventsSwitch|use-session-events-switch|weave_v2_stream|STREAM_PROTOCOL_V2|TopicCallback' --type ts --type vue client/src/
```
