# Snapshot Tool Part Rehydration Fix

## TL;DR
Fix client-side snapshot merge so tool call cards appear in rehydrated conversation history. Currently `mapCommittedSnapshotPart` and `mergeCommittedSnapshotParts` silently drop tool parts.

## Context
- **Root cause**: `client/src/lib/event-state.ts` lines ~119–144 (`mapCommittedSnapshotPart`) only handles "text" and "file" part types; "tool" returns null and is filtered out.
- **Live path works**: `applyPartUpdate` (~247–269) correctly builds `AccumulatedToolPart` from streaming events.
- **Server is correct**: persistence stores `ToolUsePart`, `SessionSnapshotBuilder` emits `ToolMessageEventPart { id, sessionId, messageId, toolName, callId, state }` with polymorphic `ToolInvocationState`.
- **Existing test gap**: `client/src/lib/__tests__/snapshot-tool-output-hydration.test.ts` only tests the live path (`applyPartUpdate`), not the snapshot rehydration path.

Key types:
- `AccumulatedToolPart` (client-side accumulated state for tool cards)
- `ToolMessageEventPart` (server snapshot shape, discriminated by `type: "tool"`)
- `ToolInvocationState` subtypes: pending, running, completed (has `output`), error (has `error`), cancelled

## Scope
- In scope:
  - Map "tool" type in `mapCommittedSnapshotPart` → `AccumulatedToolPart`
  - Merge tool parts in `mergeCommittedSnapshotParts` with callId-based dedup
  - Unit tests for the snapshot rehydration path
- Out of scope:
  - Other dropped part types (reasoning, step-start/finish) — deferred, file a follow-up if needed
  - Changes to server-side snapshot builder or persistence
  - The 12 pre-existing SessionDetailPanel test failures
- Constraints / assumptions:
  - Snapshot state must not clobber a more-advanced live state (state precedence: completed > error > cancelled > running > pending)
  - Must pass `bun run test` and `bunx vue-tsc --noEmit`

## Objectives
- Tool call cards render in conversation history after session rehydration from snapshot
- No regression to live streaming tool card behaviour
- Type-safe mapping from `ToolMessageEventPart` to `AccumulatedToolPart`

## Dependencies and Order
1. Task 1 (mapping) must land before Task 2 (merge logic) since merge calls the mapper.
2. Task 3 (tests) can be written in parallel but validated after Tasks 1–2.

## Tasks

- [ ] 1. Extend `mapCommittedSnapshotPart` to handle tool parts
  - **What**: Add a `case "tool"` branch that maps `ToolMessageEventPart` → `AccumulatedToolPart`. Extract `toolName`, `callId`, `state` (including `output` for completed, `error` for error state). Return a well-formed `AccumulatedToolPart` with `status` derived from `state.type`.
  - **Files**: `client/src/lib/event-state.ts`
  - **Depends on**: None
  - **Acceptance**:
    - `mapCommittedSnapshotPart` returns a valid `AccumulatedToolPart` for tool-type snapshot parts
    - Completed state includes output text; error state includes error message
    - Pending/running/cancelled states map to appropriate status values
    - `bunx vue-tsc --noEmit` passes

- [ ] 2. Update `mergeCommittedSnapshotParts` to merge tool parts
  - **What**: In the merge loop, handle `AccumulatedToolPart` entries. Deduplicate by `callId` — if a live-accumulated part already exists with the same callId and a more-advanced state, keep the live version. Define state precedence: completed > error > cancelled > running > pending.
  - **Files**: `client/src/lib/event-state.ts`
  - **Depends on**: Task 1
  - **Acceptance**:
    - Tool parts from snapshot appear in merged message parts array
    - A live part with state "completed" is not overwritten by a snapshot part with state "running"
    - Duplicate callIds are deduped correctly

- [ ] 3. Add unit tests for snapshot tool part rehydration
  - **What**: Create tests exercising the full snapshot path: `applyMessageLifecycle` → `mergeMessageUpdate` with realistic `ToolMessageEventPart` payloads for completed, error, and pending states. Assert the resulting `AccumulatedToolPart` is correct and that `toToolCardItem` renders output. Also test merge precedence (snapshot vs live dedup).
  - **Files**: `client/src/lib/__tests__/snapshot-tool-rehydration.test.ts`
  - **Depends on**: Tasks 1–2
  - **Acceptance**:
    - Tests cover completed (with output), error (with error message), and pending states
    - Tests cover merge precedence (live state wins when more advanced)
    - All new tests pass with `bun run test`

- [ ] 4. Verify
  - **Depends on**: Tasks 1–3
  - **Acceptance**:
    - `cd client && bun run test` — all tests pass (excluding 12 known SessionDetailPanel failures)
    - `cd client && bunx vue-tsc --noEmit` — no type errors
    - Manual smoke: restart Fleet, open existing session with tool calls, verify tool cards render

## Verification
```bash
cd client
bun run test
bunx vue-tsc --noEmit
```
All tests green (12 pre-existing SessionDetailPanel failures are unrelated and acceptable). Type-check clean.
