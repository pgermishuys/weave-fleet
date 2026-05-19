# Fix Empty Expanded Tool/Task Cards

## TL;DR
> **Summary**: Tool cards expand to blank bodies because state updates overwrite instead of merging, the mapper only extracts `summary`/`output`/`diffLines`, and there is no fallback rendering for tools with other state shapes.
> **Estimated Effort**: Short

## Context
### Original Request
Diagnose and fix blank expanded tool/task cards in the session UI.

### Key Findings
1. **State overwrite** (`event-state.ts:233`): When a tool part update arrives, the entire `state` object is replaced (`state: part.state`). If early events carry `input`/`summary` and later events only carry `status`, the earlier fields are lost.
2. **Narrow field extraction** (`ActivityStream.vue:645-647`): `toToolCardItem` only reads `state.summary`, `state.output`, and `state.diffLines`/`diff`/`patch`. Many tools emit state under different keys (e.g. `result`, `content`, `error`, `message`).
3. **No fallback in template** (`ToolCard.vue:96-111`): The expanded body renders nothing when all three fields are empty — no "no output" placeholder or raw-state dump.

## Objectives
### Core Objective
Ensure every expanded tool card displays meaningful content.

### Deliverables
- [x] Merge tool state incrementally instead of overwriting
- [x] Broaden field extraction in `toToolCardItem` to cover common state shapes
- [x] Add fallback rendering in ToolCard when no recognized fields are present
- [x] Add unit tests for state merging and mapper logic

### Definition of Done
- [x] Expanding any tool card always shows non-blank content (covered by E2E and unit fallback checks)
- [x] `npm run test` passes with new unit tests covering merge + mapper
- [x] No regressions in existing tool card rendering (diff view, summary, output still work)

### Guardrails (Must NOT)
- Do not change the SSE event schema or backend
- Do not remove existing rendering paths (summary, diff, output)
- Do not expose raw JSON to users in normal cases — only as a last-resort fallback

## TODOs

- [x] 1. Merge tool state incrementally
  **What**: In `applyPartUpdate`, shallow-merge incoming `part.state` with the existing tool part's state instead of replacing it. Use `{ ...existingPart.state, ...part.state }` so later updates augment rather than erase earlier fields.
  **Files**: `client/src/lib/event-state.ts`
  **Acceptance**: Unit test — apply two sequential partial state updates and assert all fields are present in the merged result.

- [x] 2. Broaden `toToolCardItem` field extraction
  **What**: After checking `summary`/`output`, fall back to additional common keys: `result`, `content`, `error`, `message`, `stdout`, `stderr`. Stringify the first truthy match into the `output` field. If none match but state has keys, JSON-stringify the state (excluding `input` and `status`) as output.
  **Files**: `client/src/components/session/ActivityStream.vue`
  **Acceptance**: Unit test — given a tool part with `state: { status: "completed", result: "hello" }`, mapper produces `output: "hello"`.

- [x] 3. Add fallback rendering in ToolCard
  **What**: When `!summary && !output && diffLines.length === 0`, render a muted italic placeholder: "No output captured". This prevents a visually empty expanded body.
  **Files**: `client/src/components/session/ToolCard.vue`
  **Acceptance**: Manual — expand a tool card with empty state and see the placeholder text.

- [x] 4. Unit tests
  **What**: Add/extend tests for `event-state.ts` (state merge) and extract `toToolCardItem` into a testable utility so it can be unit-tested independently.
  **Files**: `client/src/lib/__tests__/event-state.test.ts`, `client/src/components/session/__tests__/activity-stream-utils.test.ts` (new or existing)
  **Acceptance**: `npm run test` green, coverage for new logic.

## Verification
- [x] `npm run test` passes
- [x] `npm run build` succeeds (no type errors)
- [x] `dotnet build "WeaveFleet.slnx" -c Release` passes with 0 warnings / 0 errors
- [x] Backend, API, and E2E .NET suites pass
- [x] Manual equivalent: E2E opens a session with tool calls, expands cards, and asserts non-blank content
- [x] Manual equivalent: E2E verifies inline diff-view cards render diff rows correctly
