# Fix Streaming Delta Race Conditions

## TL;DR
> **Summary**: Fix two related bugs where streaming text deltas are permanently lost on the backend and visually glitch on the frontend, by merging buffered deltas before clearing in `TryPersistPartAsync` and keeping the longer text in frontend snapshot merges.
> **Estimated Effort**: Short

## Context
### Original Request
Fix two streaming bugs: (1) backend race causing permanent data loss when `message.part.updated` clears buffered deltas without merging; (2) frontend reload glitch where `mergeCommittedSnapshotParts` blindly replaces accumulated text with a shorter snapshot.

### Key Findings
- **Event flow**: `message.part.delta` → buffered in `_bufferedTextDeltas` → `message.updated` merges buffer into DB + clears buffer → more deltas may arrive → `message.part.updated` carries harness's "final" part snapshot.
- **Bug 1 root cause**: `TryPersistPartAsync` (line 326) calls `ClearBufferedTextDelta` which discards the buffer WITHOUT merging. If deltas arrived after the harness built its `message.part.updated` snapshot, those deltas are lost forever. The `message.updated` path (`ApplyBufferedTextDeltaIfPresent`, line 522) correctly merges then clears — that path is fine.
- **Bug 2 root cause**: `mergeCommittedSnapshotParts` (line 120-129) replaces all text parts with snapshot parts regardless of length. During streaming, accumulated deltas may be ahead of the snapshot.
- The pump loop is single-threaded per session — no concurrency concerns, just event ordering.

## Objectives
### Core Objective
Eliminate permanent text truncation and visual glitches during streaming.

### Deliverables
- [x] Backend: merge buffered deltas in `TryPersistPartAsync` before clearing
- [x] Frontend: keep longer text in `mergeCommittedSnapshotParts`
- [x] Tests for both fixes

### Definition of Done
- [x] `dotnet test` passes
- [x] `npm test` passes in `client/`

### Guardrails (Must NOT)
- Do not change event ordering or pump loop structure
- Do not introduce locks or concurrency primitives
- Do not change the `_bufferedTextDeltas` data structure

## TODOs

- [x] 1. **Merge buffered deltas in `TryPersistPartAsync` before clearing**
  **What**: In `HarnessEventPersistenceService.TryPersistPartAsync`, at line 326, `ClearBufferedTextDelta` discards buffered text without merging. Fix: before clearing, look up the buffered delta for this `(fleetSessionId, messageId, partId)`. If the buffered accumulated text extends beyond what the `message.part.updated` snapshot contains (i.e., buffered text is longer), append the excess to the part's text in `persisted`. Then clear. Specifically:
  - After line 323 (`persisted = MergePartAndMetadata(...)`) and before line 326, retrieve the buffered delta string via `_bufferedTextDeltas.TryGetValue(...)`.
  - Extract the text part from `persisted.PartsJson` for the matching `partId`.
  - If the buffered delta is longer than the part's text, the part text in the snapshot is a prefix of the full accumulated text. Replace the part's text with the buffered delta (which is the full accumulated text from all deltas).
  - Then call `ClearBufferedTextDelta` as before.
  **Files**: `src/WeaveFleet.Infrastructure/Services/HarnessEventPersistenceService.cs`
  **Acceptance**: Deltas buffered after the harness snapshot are preserved in the persisted message

- [x] 2. **Frontend: keep longer text in `mergeCommittedSnapshotParts`**
  **What**: In `client/src/lib/event-state.ts`, modify `mergeCommittedSnapshotParts` (lines 120-129). Instead of blindly replacing all text parts with snapshot parts, match each snapshot text part to the existing accumulated part by `partId`. For each matched pair, keep whichever has the longer `.text`. For unmatched snapshot parts, use the snapshot. For unmatched existing text parts, keep them. Implementation:
  ```
  function mergeCommittedSnapshotParts(
    existingParts: AccumulatedMessage["parts"],
    snapshotParts: Array<AccumulatedTextPart>,
  ): AccumulatedMessage["parts"] {
    const existingTextByPartId = new Map(
      existingParts
        .filter((p): p is AccumulatedTextPart => p.type === "text")
        .map((p) => [p.partId, p]),
    );

    const mergedText = snapshotParts.map((snap) => {
      const existing = existingTextByPartId.get(snap.partId);
      if (existing && existing.text.length > snap.text.length) {
        return existing;
      }
      return snap;
    });

    const nonTextParts = existingParts.filter(
      (part) => part.type !== "text" && part.type !== "reasoning",
    );

    return [...mergedText, ...nonTextParts];
  }
  ```
  **Files**: `client/src/lib/event-state.ts`
  **Acceptance**: When accumulated text is longer than snapshot text, accumulated text is preserved

- [x] 3. **Backend test: deltas surviving `message.part.updated`**
  **What**: Add a test that: (a) buffers several text deltas, (b) processes a `message.part.updated` event whose snapshot text is shorter than the accumulated deltas, (c) verifies the persisted message contains the full accumulated text. Use the existing test patterns from `MessagePersistenceServiceTests.cs`. Will need to mock `IMessageRepository` and `SessionActivityWriteService`.
  **Files**: `tests/WeaveFleet.Application.Tests/Services/HarnessEventPersistenceServiceTests.cs`
  **Acceptance**: Test passes covering the exact race condition

- [x] 4. **Frontend test: snapshot merge preserves longer accumulated text**
  **What**: Add test cases to the existing `event-state.test.ts`. Test `mergeMessageUpdate` where existing message has accumulated text parts longer than the incoming snapshot's text parts. Verify the longer text is kept. Also test the case where snapshot is longer (normal case — snapshot wins).
  **Files**: `client/src/lib/__tests__/event-state.test.ts`
  **Acceptance**: Tests pass verifying both directions of the length comparison

## Verification
- [x] `dotnet test` — all backend tests pass
- [x] `npm test` in `client/` — all frontend tests pass
- [x] No regressions in existing tests
