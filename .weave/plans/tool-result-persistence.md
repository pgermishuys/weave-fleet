# Tool Result Persistence Across Harness Mappers

## TL;DR
Fix tool results being lost after restart by making all three harness mappers emit `message.part.updated` events for tool results, and adding deserializer support so the persistence pipeline stores them as `ToolResultPart` entries in `parts_json`.

## Context
Tool results display "No output captured" after Fleet restarts because none of the three harness mappers emit persistable events for tool output. The persistence infrastructure already works end-to-end:
- `HarnessEventPersistenceService.TryPersistPartAsync()` handles `message.part.updated` events
- `MessagePersistenceService.MergePartAndMetadata()` default case appends any `ToolResultPart` to `parts_json`
- `SessionSnapshotBuilder` pre-scans for `ToolResultPart` entries and merges output into `ToolCompletedState`

Key files in the persistence path:
- `src/WeaveFleet.Infrastructure/Services/HarnessEventPersistenceService.cs` (line 334-338: deserializes via `OpenCodePartDeserializer`, maps via `OpenCodeMapper.MapPart()`)
- `src/WeaveFleet.Infrastructure/Harnesses/OpenCode/OpenCodePartDeserializer.cs` (type switch at line 30-45)
- `src/WeaveFleet.Infrastructure/Harnesses/OpenCode/OpenCodeMapper.cs` (MapPart at line 54-115)
- `src/WeaveFleet.Application/Services/MessagePersistenceService.cs` (MergePartAndMetadata at line 278)
- `src/WeaveFleet.Infrastructure/Events/SessionSnapshotBuilder.cs` (line 180-184, 291-307)

## Scope
- In scope: Making tool results persist for ClaudeCode, OpenCode, and Pi harnesses; updating tests that assert the dropping behavior; verifying snapshot rebuild includes tool output.
- Out of scope: Truncation of large tool output (follow-up); NuCode harness (already handles output differently); frontend rendering changes.
- Constraints / assumptions:
  - The wire format for tool-result events must be compatible with `OpenCodePartDeserializer` (needs a `type` discriminator and `messageID` property).
  - Pi already has output in `ToolCompletedState` in the live event -- the snapshot path handles it. The gap is only in persistence.
  - All three harnesses must emit a `message.part.updated` event with `type: "tool-result"` payload so the single persistence codepath handles all of them.

## Objectives
- Tool results are persisted to the database for all three harness types
- After restart, `SessionSnapshotBuilder` reconstructs tool output from persisted `ToolResultPart` entries
- Existing live-streaming behavior is unchanged (tool-result events are additive)

## Dependencies and Order
1. Add the `tool-result` model and deserializer support first (shared infrastructure used by all harnesses).
2. Fix each harness mapper (can be done in parallel after step 1).
3. Update tests last (depends on implementation being correct).
4. Verification requires all prior steps.

## Tasks

- [x] 1. Add `OpenCodeToolResultPart` model and deserializer support
  - **What**: Create a new `OpenCodeToolResultPart` record in `OpenCodeModels.cs` with properties `Id`, `MessageId`, `SessionId`, `CallId`, `Content`, `IsError`. Add `"tool-result"` case to `OpenCodePartDeserializer.DeserializePart()` switch. Register in `OpenCodeJsonContext`. Add a case in `OpenCodeMapper.MapPart()` that returns `new ToolResultPart(CallId, Content, IsError)`.
  - **Files**:
    - `src/WeaveFleet.Infrastructure/Harnesses/OpenCode/OpenCodeModels.cs`
    - `src/WeaveFleet.Infrastructure/Harnesses/OpenCode/OpenCodePartDeserializer.cs`
    - `src/WeaveFleet.Infrastructure/Harnesses/OpenCode/OpenCodeMapper.cs`
    - `src/WeaveFleet.Infrastructure/Harnesses/OpenCode/OpenCodeJsonContext.cs`
  - **Depends on**: None
  - **Acceptance**:
    - `OpenCodePartDeserializer.DeserializePart()` returns an `OpenCodeToolResultPart` for JSON with `"type": "tool-result"`
    - `OpenCodeMapper.MapPart()` returns a `ToolResultPart` for `OpenCodeToolResultPart` input

- [x] 2. Fix ClaudeCode mapper to emit tool-result events
  - **What**: In `ClaudeCodeMapper.CreatePartUpdatedEvent()`, add a case for `ToolResultPart` that serializes a payload with `type: "tool-result"`, `messageID`, `sessionID`, `callId`, `content`, `isError`. The payload shape must match what `OpenCodePartDeserializer` expects from Task 1.
  - **Files**:
    - `src/WeaveFleet.Infrastructure/Harnesses/ClaudeCode/ClaudeCodeMapper.cs`
  - **Depends on**: Task 1
  - **Acceptance**:
    - `CreatePartUpdatedEvent` no longer returns `null` for `ToolResultPart`
    - Emitted event has `Type = EventTypes.MessagePartUpdated` with the tool-result payload
    - Round-trip: `OpenCodePartDeserializer` can deserialize the emitted payload back to `OpenCodeToolResultPart`

- [x] 3. Fix OpenCode mapper to emit tool-result events on completion
  - **What**: In `OpenCodeHarnessRuntime` (or wherever `message.part.updated` events are emitted for tool parts), when the tool state is `OpenCodeToolCompleted` with non-null `Output`, emit an additional `message.part.updated` event with a `tool-result` payload containing the stringified output. The `callId` comes from `OpenCodeToolPart.CallId`. Use the same payload format as Task 2.
  - **Files**:
    - `src/WeaveFleet.Infrastructure/Harnesses/OpenCode/OpenCodeHarnessRuntime.cs`
  - **Depends on**: Task 1
  - **Acceptance**:
    - When a tool completes with output, a second `message.part.updated` event with type `tool-result` is emitted
    - The event's `callId` matches the original `ToolUsePart`'s `ToolCallId`

- [x] 4. Fix Pi mapper to emit tool-result events on completion
  - **What**: In `PiMapper.MapToolExecutionEnd()`, after the existing `CreateToolPartUpdatedEvent` call, also emit a tool-result event using the same payload format. Extract content from `evt.Result` (already converted to `JsonElement?` as `output`). Stringify it for the `content` field. Include `evt.IsError`.
  - **Files**:
    - `src/WeaveFleet.Infrastructure/Harnesses/Pi/PiMapper.cs`
  - **Depends on**: Task 1
  - **Acceptance**:
    - `MapToolExecutionEnd` returns 2 events: the existing tool-state event plus a new tool-result event
    - The tool-result event payload deserializes via `OpenCodePartDeserializer` to `OpenCodeToolResultPart`

- [x] 5. Add a shared helper for building tool-result event payloads
  - **What**: To avoid duplicating the payload construction across three mappers, add a static helper (e.g. in a shared `ToolResultEventBuilder` class or as a method on an existing shared type) that takes `messageId`, `sessionId`, `callId`, `content`, `isError` and returns the `JsonElement` payload. All three mappers should use it.
  - **Files**:
    - `src/WeaveFleet.Infrastructure/Harnesses/ToolResultEventBuilder.cs` (new file)
    - Update files from Tasks 2, 3, 4 to use it
  - **Depends on**: Task 1
  - **Acceptance**:
    - Single source of truth for the tool-result payload shape
    - All three harnesses produce identical payload structure

- [x] 6. Update ClaudeCode mapper tests
  - **What**: Change `ToFrontendEvents_AssistantMessageWithToolResult_DoesNotEmitToolResultPartEvent` to assert that a tool-result event IS emitted. Rename test appropriately. Add assertion that the event payload contains the tool output content and correct callId.
  - **Files**:
    - `tests/WeaveFleet.Infrastructure.Tests/Harnesses/ClaudeCode/ClaudeCodeMapperTests.cs`
  - **Depends on**: Task 2
  - **Acceptance**:
    - Test asserts a `message.part.updated` event with tool-result payload is emitted
    - Test passes with `dotnet test --filter "FullyQualifiedName~ClaudeCodeMapperTests"`

- [x] 7. Update OpenCode mapper tests
  - **What**: Change `ToHarnessMessage_ToolPart_DoesNotPersistCompletedOutputBody` to verify the output IS available (either as a separate `ToolResultPart` in the message parts, or via the emitted event). Add a new test that verifies `OpenCodeToolCompleted` with output produces a tool-result event.
  - **Files**:
    - `tests/WeaveFleet.Infrastructure.Tests/Harnesses/OpenCode/OpenCodeMapperTests.cs`
  - **Depends on**: Task 3
  - **Acceptance**:
    - Tests pass with `dotnet test --filter "FullyQualifiedName~OpenCodeMapperTests"`

- [x] 8. Add Pi mapper test for tool-result event emission
  - **What**: Add a test that verifies `MapToolExecutionEnd` returns both a tool-state event and a tool-result event. Assert the tool-result event contains the output content.
  - **Files**:
    - `tests/WeaveFleet.Infrastructure.Tests/Harnesses/Pi/PiMapperTests.cs`
  - **Depends on**: Task 4
  - **Acceptance**:
    - Test passes with `dotnet test --filter "FullyQualifiedName~PiMapperTests"`

- [x] 9. Add integration test for tool-result round-trip persistence
  - **What**: Add a test in the persistence/snapshot tests that: (a) persists a tool-result event via `HarnessEventPersistenceService`, (b) rebuilds the snapshot via `SessionSnapshotBuilder`, (c) asserts the tool's `ToolCompletedState.Output` contains the expected content.
  - **Files**:
    - `tests/WeaveFleet.Infrastructure.Tests/Events/SessionSnapshotBuilderTests.cs` (or appropriate existing test file)
  - **Depends on**: Tasks 1-5
  - **Acceptance**:
    - Test passes proving the full persistence round-trip works
    - `dotnet test --filter "FullyQualifiedName~SnapshotBuilder"`

- [x] 10. Verify full test suite passes
  - **Depends on**: All prior tasks
  - **Acceptance**:
    - `dotnet test` from solution root passes with no failures
    - `dotnet build -c Release` succeeds with no warnings from modified files

## Verification
```bash
dotnet build -c Release
dotnet test --filter "FullyQualifiedName~ClaudeCodeMapperTests|FullyQualifiedName~OpenCodeMapperTests|FullyQualifiedName~PiMapperTests|FullyQualifiedName~SnapshotBuilder"
dotnet test
```
All tests pass. Manual verification: start Fleet, run a session with tool calls, restart Fleet, confirm tool results display correctly (not "No output captured").
