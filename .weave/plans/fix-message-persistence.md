# Fix Message Parts Lost on Persistence

## TL;DR
> **Summary**: Messages lose their parts when persisted because `message.updated` (which carries no parts) overwrites the record saved by `message.created`, and `message.part.updated` events (which carry the actual content) are not persisted at all. The fix: skip persistence on `message.updated` (metadata-only event) and add incremental part persistence on `message.part.updated`.
> **Estimated Effort**: Medium

## Context
### Original Request
After page refresh, users see "1 inference step" instead of actual message content. The assistant messages in the database have `parts_json = "[]"`.

### Key Findings

**Event lifecycle (confirmed by contract fixture `tests/contracts/opencode-to-fleet-events.json`):**
1. `message.created` → initial message record (user messages have text parts; assistant messages are empty skeletons with `parts: []`)
2. `message.part.updated` → individual parts arrive (text content, tool calls) — **NOT persisted**
3. `message.part.delta` → streaming text increments — not relevant for persistence
4. `message.updated` → final metadata (cost/tokens/completion time) with **no `parts` property** — overwrites DB with `parts_json = "[]"`

**The overwrite mechanism:**
- `TryPersistMessageAsync()` (line 165-217 of `HarnessEventRelay.cs`) handles both `message.created` and `message.updated`
- When payload has no `parts` property, `parts` defaults to `[]` (line 199-201)
- The upsert SQL uses `ON CONFLICT DO UPDATE SET parts_json = excluded.parts_json` — full replacement
- So `message.updated` (no parts) overwrites whatever `message.created` stored

**Impact:**
- User messages: `message.created` has parts → persisted OK, but then `message.updated` might wipe them (if a `message.updated` fires for user messages too)
- Assistant messages: `message.created` has empty parts → saved as `[]`, parts arrive via `message.part.updated` → **never saved**, `message.updated` → overwrites with `[]` again
- Frontend `isCollapsibleMessage()` checks for text/tool/file parts — empty parts array → collapsed as "inference step"

**No `GetByIdAsync` on `IMessageRepository`** — will need to add it for read-modify-write in part persistence.

### Architecture Constraints
- Persistence must **never block** the SSE event stream (fire-and-forget pattern)
- Silent failure on persistence errors (already implemented)
- `DapperMessageRepository.UpsertAsync` uses `ON CONFLICT(id, session_id) DO UPDATE SET parts_json = excluded.parts_json` — full replacement, not merge
- `OpenCodeMessagePart` is polymorphic (discriminated by `"type"`) — needs `OpenCodeJsonOptions.Default` for deserialization
- Parts identified by `id` field; tool parts also have `callID`

## Objectives
### Core Objective
Ensure assistant messages retain their parts across page refresh by correctly persisting content from `message.part.updated` events and not overwriting with empty data from `message.updated`.

### Deliverables
- [ ] `message.updated` events no longer overwrite parts with empty data
- [ ] `message.part.updated` events trigger incremental part persistence
- [ ] New `GetByIdAsync` method on `IMessageRepository` for read-modify-write
- [ ] Tests covering the full event lifecycle (created → part.updated → updated)
- [ ] All existing tests continue to pass

### Definition of Done
- [ ] `dotnet build -c Release` succeeds with no warnings
- [ ] `dotnet test` passes all tests (existing + new)
- [ ] Manual verification: send a message, refresh page, see actual content (not "inference step")

### Guardrails (Must NOT)
- Must NOT block the SSE event stream
- Must NOT introduce persistence for `message.part.delta` (streaming increments — too chatty for DB writes)
- Must NOT change the DB schema (no migrations needed — `parts_json` column already exists)
- Must NOT change the frontend rendering logic (the fix is purely server-side persistence)
- Must NOT break the existing `message.created` persistence for user messages (which correctly have parts)

## TODOs

- [x] 1. **Add `GetByIdAsync` to `IMessageRepository`**
  **What**: Add a method to retrieve a single message by its composite key `(id, session_id)`. This is needed for the read-modify-write pattern when handling `message.part.updated`.
  **Files**:
  - `src/WeaveFleet.Domain/Repositories/IMessageRepository.cs` — add `Task<PersistedMessage?> GetByIdAsync(string id, string sessionId);`
  - `src/WeaveFleet.Infrastructure/Data/Repositories/DapperMessageRepository.cs` — implement with `SELECT * FROM messages WHERE id = @Id AND session_id = @SessionId`
  **Acceptance**: Compiles. Existing tests pass. New method returns `null` for non-existent messages.

- [x] 2. **Expose `SerializerOptions` from `MessagePersistenceService`**
  **What**: The `SerializerOptions` field in `MessagePersistenceService` is currently `private static`. The new `TryPersistPartAsync` method (TODO 5) needs to use the **exact same** serializer options for polymorphic `MessagePart` round-tripping. Change visibility from `private` to `internal`. Also add a `MergePart` static helper method that encapsulates the read-modify-write pattern: given a `PersistedMessage` and a `MessagePart`, deserialize existing parts, add/update the part, and reserialize.
  **Files**:
  - `src/WeaveFleet.Application/Services/MessagePersistenceService.cs` — change `private static readonly JsonSerializerOptions SerializerOptions` to `internal static readonly JsonSerializerOptions SerializerOptions`. Add `MergePart(PersistedMessage existing, MessagePart newPart)` method.
  **Part matching strategy for `MergePart`**:
  - **Tool parts**: Match by `ToolCallId`. Replace in-place when found, append otherwise.
  - **Text parts**: Replace the first existing `TextPart` if one exists, otherwise append. (Assistant messages typically have one text part.)
  **Acceptance**: Compiles. Existing tests pass. `SerializerOptions` accessible from `WeaveFleet.Infrastructure` (same assembly or `InternalsVisibleTo`).

- [x] 3. **Extract `MapPart()` helper from `OpenCodeMapper`**
  **What**: The mapping from `OpenCodeMessagePart` → `MessagePart` already exists in `OpenCodeMapper.ToHarnessMessage()` (lines 20-54). Extract the per-part mapping into a static method `MapPart(OpenCodeMessagePart) → MessagePart?` so it can be reused by both `ToHarnessMessage` and the new `TryPersistPartAsync` (TODO 5).
  **Files**:
  - `src/WeaveFleet.Infrastructure/Harnesses/OpenCode/OpenCodeMapper.cs` — extract `internal static MessagePart? MapPart(OpenCodeMessagePart part)` from the switch in `ToHarnessMessage`, then call `MapPart` from within `ToHarnessMessage`'s loop
  **Acceptance**: `ToHarnessMessage` behavior unchanged (verified by existing `OpenCodeMapperTests`). New `MapPart` is callable from `HarnessEventRelay`.

- [x] 4. **Stop `message.updated` from persisting (remove from filter)**
  **What**: In `TryPersistMessageAsync()`, remove `message.updated` from the event type filter. The `message.updated` event carries only metadata (cost, tokens, completion time) that we don't store in the message row. Removing it eliminates the overwrite of parts with empty data.
  **Files**:
  - `src/WeaveFleet.Infrastructure/Services/HarnessEventRelay.cs` — change line 168 from:
    ```csharp
    if (evt.Type is not ("message.created" or "message.updated"))
    ```
    to:
    ```csharp
    if (evt.Type is not "message.created")
    ```
  **Acceptance**: Sending `message.updated` with no parts does NOT call `UpsertAsync`. Verified by test (TODO 8).

- [x] 5. **Guard `message.created` against overwriting non-empty parts**
  **What**: Add a guard in `TryPersistMessageAsync()` so that `message.created` with empty parts does NOT overwrite an existing message that already has parts. This protects against the race where `message.part.updated` persists a part before `message.created` finishes, and against the normal case where `message.created` for assistant messages arrives with an empty skeleton.
  **Files**:
  - `src/WeaveFleet.Infrastructure/Services/HarnessEventRelay.cs` — after building the `persisted` object, add:
    ```csharp
    // Don't overwrite existing message that may already have parts from message.part.updated
    if (harnessMessage.Parts.Count == 0)
    {
        var existing = await messageRepo.GetByIdAsync(persisted.Id, persisted.SessionId);
        if (existing is not null) return; // Don't overwrite with empty skeleton
    }
    await messageRepo.UpsertAsync(persisted);
    ```
  **Acceptance**: An existing message with non-empty parts survives a `message.created` event with empty parts. Verified by test (TODO 10).

- [x] 6. **Add `TryPersistPartAsync` for `message.part.updated` events**
  **What**: Add a new method `TryPersistPartAsync()` that handles `message.part.updated` events using a read-modify-write pattern. **Depends on TODOs 1, 2, 3.**
  
  **CRITICAL**: `TryPersistMessageAsync` receives the original `evt` object (before `RewriteSessionIds`). The `fleetSessionId` is passed as a separate parameter. The raw SSE payload uses `"messageID"` (uppercase D) for the message ID field. Use raw `JsonElement` to extract `messageID`, then deserialize the part via `OpenCodeJsonOptions.Default`.
  
  **Implementation outline**:
  1. Guard: `evt.Type is not "message.part.updated"` → return
  2. Extract `payload.part` object from `JsonElement`
  3. Extract `messageID` from the part (raw JSON key is `"messageID"`)
  4. Deserialize part as `OpenCodeMessagePart` via `OpenCodeJsonOptions.Default`
  5. Map to Fleet `MessagePart` via `OpenCodeMapper.MapPart()` (from TODO 3)
  6. If `fleetPart` is null → return (unsupported part type)
  7. Resolve scope, get `IMessageRepository`
  8. Call `messageRepo.GetByIdAsync(messageId, fleetSessionId)`
  9. If existing is null → create new `PersistedMessage` with role "assistant", serialize `[fleetPart]` using `MessagePersistenceService.SerializerOptions` (from TODO 2)
  10. If existing is not null → call `MessagePersistenceService.MergePart(existing, fleetPart)` (from TODO 2)
  11. `await messageRepo.UpsertAsync(persisted)`
  12. Wrap in try/catch, log and swallow errors (fire-and-forget pattern)
  
  **Files**:
  - `src/WeaveFleet.Infrastructure/Services/HarnessEventRelay.cs` — add `TryPersistPartAsync` method
  **Acceptance**: Part events are persisted. After sending text + tool parts, the DB contains those parts.

- [x] 7. **Wire `TryPersistPartAsync` into the event pump**
  **What**: In `PumpAsync`, after the existing `TryPersistMessageAsync` fire-and-forget call (line 144), add:
  ```csharp
  _ = TryPersistPartAsync(evt, fleetSessionId);
  ```
  Both calls are fire-and-forget and independent.
  **Files**:
  - `src/WeaveFleet.Infrastructure/Services/HarnessEventRelay.cs` — add the fire-and-forget call in the pump loop
  **Acceptance**: `message.part.updated` events trigger persistence without blocking the event stream.

- [x] 8. **Update existing test: `message.updated` should NOT persist**
  **What**: Update the existing `PumpAsync_MessageUpdatedEvent_PersistsMessage` test — it currently expects `message.updated` to persist. After the fix, `message.updated` should NOT persist. Change the test to verify `message.updated` does NOT call `UpsertAsync`.
  **Files**:
  - `tests/WeaveFleet.Infrastructure.Tests/Services/HarnessEventRelayTests.cs` — rename and update `PumpAsync_MessageUpdatedEvent_PersistsMessage` to `PumpAsync_MessageUpdatedEvent_DoesNotPersist`
  **Acceptance**: Test verifies that `message.updated` events do NOT trigger `UpsertAsync`.

- [x] 9. **Add test: `message.part.updated` text part persists incrementally**
  **What**: New test that:
  1. Sets up a mock `IMessageRepository` where `GetByIdAsync` returns null (no existing message)
  2. Emits `message.part.updated` with a text part
  3. Verifies `UpsertAsync` was called with a `PersistedMessage` containing the text part in `PartsJson`
  **Files**:
  - `tests/WeaveFleet.Infrastructure.Tests/Services/HarnessEventRelayTests.cs` — add `PumpAsync_MessagePartUpdated_TextPart_PersistsIncrementally`
  **Acceptance**: Test passes.

- [x] 10. **Add test: `message.created` with empty parts does NOT overwrite existing**
  **What**: Regression test for the race condition guard (TODO 5):
  1. Set up mock `IMessageRepository` where `GetByIdAsync` returns an existing message with non-empty parts
  2. Emit `message.created` with empty parts for the same message ID
  3. Verify `UpsertAsync` was NOT called (guard prevented overwrite)
  **Files**:
  - `tests/WeaveFleet.Infrastructure.Tests/Services/HarnessEventRelayTests.cs` — add `PumpAsync_MessageCreated_EmptyParts_DoesNotOverwriteExisting`
  **Acceptance**: Test passes. Parts survive.

- [x] 11. **Add test: full lifecycle regression (created → part.updated → updated)**
  **What**: Integration-style test that verifies the full lifecycle:
  1. Emit `message.created` (assistant skeleton with empty parts)
  2. Emit `message.part.updated` (text part persisted)
  3. Emit `message.updated` (metadata only, no parts)
  4. Verify the final DB state still has the text part (not overwritten)
  This is the **regression test** for the original bug.
  **Files**:
  - `tests/WeaveFleet.Infrastructure.Tests/Services/HarnessEventRelayTests.cs` — add `PumpAsync_FullLifecycle_MessageUpdatedDoesNotOverwriteParts`
  **Acceptance**: Test passes. Parts survive the `message.updated` event.

- [x] 12. **Add `GetByIdAsync` repository integration test**
  **What**: Test `GetByIdAsync` returns the correct message and `null` for non-existent messages. Uses the existing `TestDbHelper` pattern.
  **Files**:
  - `tests/WeaveFleet.Infrastructure.Tests/Data/Repositories/DapperMessageRepositoryTests.cs` — add `GetByIdAsync_ReturnsMessage` and `GetByIdAsync_ReturnsNullForMissing`
  **Acceptance**: Tests pass.

- [x] 13. **Add `MessagePersistenceService.MergePart` unit tests**
  **What**: Unit tests for the `MergePart` method added in TODO 2:
  - Adding a text part to empty parts list
  - Adding a tool part to empty parts list
  - Updating an existing tool part (matched by ToolCallId)
  - Adding a second text part (appended, not replaced when first already exists — wait, text parts replace. Test both: first text replaces, and non-text parts don't get replaced by text)
  **Files**:
  - `tests/WeaveFleet.Application.Tests/Services/MessagePersistenceServiceTests.cs` — add merge tests
  **Acceptance**: All merge scenarios tested.

- [x] 14. **Build and verify**
  **What**: Run `dotnet build -c Release` and `dotnet test` to confirm no warnings or regressions.
  **Files**: None (verification step)
  **Acceptance**: `dotnet build -c Release` exits 0 with no warnings. `dotnet test` exits 0 with all tests passing.

## Implementation Notes

### Serializer options
`MessagePersistenceService.SerializerOptions` (changed to `internal` in TODO 2) must be used consistently wherever `MessagePart` is serialized/deserialized. This ensures the polymorphic `[JsonDerivedType]` attributes work correctly. The `MergePart` helper uses these options internally.

### Part matching strategy (implemented in `MergePart`)
When updating an existing message's parts:
- **Tool parts**: Match by `ToolCallId` — each tool call has a unique ID. Replace in-place when found, append otherwise.
- **Text parts**: Replace the first existing `TextPart` if one exists, otherwise append. Assistant messages typically have one text part. OpenCode sends `id` on each part, but we don't persist part IDs in the Fleet domain model.

### Race condition: `message.part.updated` before `message.created`
Handled by TODO 5 (guard on `message.created`) and TODO 6 (create new `PersistedMessage` when `GetByIdAsync` returns null in `TryPersistPartAsync`).

## Verification
- [x] `dotnet build -c Release` succeeds with zero warnings
- [x] `dotnet test` — all existing tests pass
- [x] `dotnet test` — all new tests pass (6 new tests: TODO 6-11)
- [ ] Manual test: send message → refresh → content visible (not "inference step")
- [x] No regressions in SSE event delivery (fire-and-forget pattern preserved)
