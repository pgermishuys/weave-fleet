# Canonical Message Identity & Ordering

## TL;DR
Replace Fleet's `user-{GUID}` message ID scheme with a C# port of OpenCode's `msg_` ascending ID generator, pass it to OpenCode via `messageID` on `prompt_async`, remove echo suppression heuristics, and unify ordering to ID-based everywhere.

## Context
OpenCode generates monotonic ascending IDs: `msg_` + 6-byte hex (timestamp_ms * 0x1000 + counter, big-endian) + 14-char base62 random. Fleet currently fabricates `user-{GUID}` for user prompts (MessagePersistenceService.cs:80), suppresses OpenCode's user-message echo via heuristic role+id matching (HarnessEventRelay.cs:416-433), and sorts by `(timestamp DESC, id DESC)` in snapshots. The `prompt_async` endpoint already accepts an optional `messageID` field (validated by `MessageID.zod.optional()` in prompt.ts:1719).

## Scope
- In scope: C# ID generator, passing messageID to OpenCode, removing echo suppression, ID-based ordering in snapshot builder and client, removing `user-{GUID}`, tests.
- Out of scope: Descending IDs, other harnesses (Pi, ClaudeCode, NuCode) — they don't use OpenCode's prompt API. Migration of existing persisted data (breaking change accepted).
- Constraints: Breaking change is acceptable. No backwards-compat shims.

## Objectives
- Single source of truth for user message IDs (Fleet generates, OpenCode accepts)
- Deterministic ordering by ID (encodes timestamp + monotonic counter)
- Remove echo suppression complexity

## Dependencies and Order
1. ID generator must exist before orchestrator can use it.
2. `OpenCodePromptRequest` must include `messageID` before orchestrator can pass it.
3. Echo suppression removal depends on identity-based dedup (same ID from Fleet and OpenCode).
4. Client ordering change is independent but should align with server.

## Tasks

- [x] 1. Implement C# ascending message ID generator
  - **What**: Port `Identifier.ascending("message")` to a static `MessageId` class. Logic: thread-safe (lock or Interlocked) `lastTimestamp`/`counter`; `BigInt(ms) * 0x1000 + counter`; take lower 6 bytes big-endian → hex; append 14 random base62 chars. Prefix: `msg_`. Total ID length = 4 + 12 hex + 14 base62 = 30 chars.
  - **Files**: `src/WeaveFleet.Domain/Identity/AscendingMessageId.cs` (new)
  - **Depends on**: None
  - **Acceptance**:
    - IDs match format `msg_[0-9a-f]{12}[0-9A-Za-z]{14}`
    - Two IDs generated in same millisecond sort ascending (counter increments)
    - `AscendingMessageId.ExtractTimestamp(id)` returns the original ms
    - Cross-validated against known TS outputs (hardcode a test with fixed timestamp + counter to verify byte layout)

- [x] 2. Unit tests for ID generator
  - **What**: Test format, monotonicity, timestamp extraction, thread-safety (parallel generation yields unique sorted IDs).
  - **Files**: `tests/WeaveFleet.Domain.Tests/Identity/AscendingMessageIdTests.cs` (new, or in existing Domain tests project)
  - **Depends on**: Task 1
  - **Acceptance**:
    - All tests pass
    - At least: format test, monotonic ordering test, timestamp round-trip test, concurrency test

- [x] 3. Add `messageID` to `OpenCodePromptRequest`
  - **What**: Add `[JsonPropertyName("messageID")] public string? MessageId { get; init; }` to the request record.
  - **Files**: `src/WeaveFleet.Infrastructure/Harnesses/OpenCode/OpenCodeModels.cs` (~line 451)
  - **Depends on**: None
  - **Acceptance**:
    - Serialized JSON includes `"messageID": "msg_..."` when set, omitted when null

- [x] 4. Orchestrator generates msg_ ID and passes to harness
  - **What**: In `SessionOrchestrator.PromptSessionAsync` (~line 805), replace the correlationId-based `userMessageId` with `AscendingMessageId.New()`. Pass the same ID through to `OpenCodeHarnessSession.SendPromptAsync` so it lands in the request body. This requires either: (a) adding a `messageID` param to `IHarnessSession.SendPromptAsync`, or (b) adding it to `PromptOptions`. Option (b) is cleaner — add `MessageId` to `PromptOptions`.
  - **Files**:
    - `src/WeaveFleet.Domain/Harnesses/PromptOptions.cs` (add `MessageId` property)
    - `src/WeaveFleet.Domain/Harnesses/IHarnessSession.cs` (no change if using PromptOptions)
    - `src/WeaveFleet.Application/Services/SessionOrchestrator.cs` (~line 805-818)
    - `src/WeaveFleet.Infrastructure/Harnesses/OpenCode/OpenCodeHarnessSession.cs` (~line 205, populate `MessageId` on request)
  - **Depends on**: Tasks 1, 3
  - **Acceptance**:
    - User prompt message broadcast to clients has `msg_` ID
    - Same ID is sent to OpenCode in `prompt_async` request body as `messageID`
    - `MessagePersistenceService.CreateUserPromptMessage` no longer has `user-{GUID}` fallback

- [x] 5. Remove `user-{GUID}` fallback
  - **What**: In `MessagePersistenceService.CreateUserPromptMessage`, make `userMessageId` required (non-nullable) or assert it's always provided. Remove the `$"user-{Guid.NewGuid():N}"` fallback entirely.
  - **Files**: `src/WeaveFleet.Application/Services/MessagePersistenceService.cs` (lines 59-80)
  - **Depends on**: Task 4
  - **Acceptance**:
    - No reference to `user-{Guid` pattern anywhere in codebase
    - Compilation succeeds; all callers pass a valid ID

- [x] 6. Replace echo suppression with identity-based dedup
  - **What**: In `HarnessEventRelay.cs`, instead of suppressing ALL user-role messages, compare the incoming message ID against the already-broadcast ID. If it matches the Fleet-generated `msg_` ID, suppress (upsert/skip). This is simpler: the set of "already broadcast user message IDs" is populated when Fleet broadcasts, and the echo from OpenCode carries the same ID. The existing `suppressedUserMessageIds` HashSet already does this — verify it works correctly with `msg_` IDs (it should, since it's keyed on the message ID string). Remove any text-matching heuristics if present.
  - **Files**: `src/WeaveFleet.Infrastructure/Services/HarnessEventRelay.cs` (lines 238, 416-449)
  - **Depends on**: Task 4
  - **Acceptance**:
    - Echo suppression works by exact ID match (no role-based heuristic needed beyond confirming it's the same ID)
    - User message appears exactly once in the client event stream

- [x] 7. Update snapshot ordering to use ID as primary sort
  - **What**: Change `ORDER BY m.timestamp DESC, m.id DESC` to `ORDER BY m.id DESC` (since ID encodes timestamp monotonically, ID alone is sufficient and correct). Alternatively keep `ORDER BY m.id DESC` with timestamp as documentation-only.
  - **Files**: `src/WeaveFleet.Infrastructure/Events/SessionSnapshotBuilder.cs` (line 92)
  - **Depends on**: Task 1
  - **Acceptance**:
    - Messages in snapshot are ordered chronologically
    - Two messages in same millisecond are ordered by counter (encoded in ID)

- [x] 8. Client: ID-based insertion sort
  - **What**: Replace `insertMessageSorted` in `event-state.ts` to sort by message ID string comparison (since `msg_` IDs with hex-encoded timestamps sort lexicographically in chronological order). Use binary search like OpenCode's clients.
  - **Files**: `client/src/lib/event-state.ts` (lines 20-50)
  - **Depends on**: None (can be done in parallel)
  - **Acceptance**:
    - Messages sorted by ID ascending
    - Binary search insertion (O(log n) find + splice)
    - Falls back to end-append for messages without proper `msg_` prefix (defensive)

- [x] 9. Update client unit tests
  - **What**: Update tests in `client/src/lib/__tests__/` to use `msg_` IDs and verify ordering by ID.
  - **Files**: `client/src/lib/__tests__/event-state.test.ts` (or similar)
  - **Depends on**: Task 8
  - **Acceptance**:
    - Tests pass with `msg_` format IDs
    - Test case: two messages same ms, different counter → correct order

- [x] 10. Integration/orchestrator tests
  - **What**: Update or add tests verifying the orchestrator generates `msg_` IDs, passes them to the harness, and the echo suppression works by ID match.
  - **Files**: Existing test files in `tests/WeaveFleet.Application.Tests/` or `tests/WeaveFleet.IntegrationTests/`
  - **Depends on**: Tasks 4, 5, 6
  - **Acceptance**:
    - Test sends prompt, verifies broadcast message has `msg_` ID
    - Test verifies echo with same ID is suppressed
    - Test verifies echo with different ID is NOT suppressed

- [x] 11. Verify full build and test suite
  - **Depends on**: All above
  - **Acceptance**:
    - `dotnet build` succeeds (Release)
    - `dotnet test` all pass
    - `cd client && bun run build && bun run test` passes

## Verification
```bash
# Server
dotnet build -c Release
dotnet test --no-build -c Release

# Client
cd client && bun install && bun run build && bun run test
```
All green, no references to `user-{Guid` pattern remain (`grep -r "user-{Guid" src/` returns nothing).
