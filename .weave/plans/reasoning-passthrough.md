# Reasoning Content Passthrough & Collapsible UI

## TL;DR
Stop stripping model reasoning content on the server so it flows to the client and persists in the database, then update the UI to render reasoning in collapsible sections with a brain icon.

## Context
Reasoning content (thinking/chain-of-thought) is currently stripped at four points before it can reach the client or database. The client already has types and handlers for reasoning parts (`AccumulatedReasoningPart`, `mapCommittedSnapshotPart`, streaming accumulation), but they never receive data because the server filters it out. The rendering currently uses blockquotes, which needs to change to a collapsible Brain-icon section.

Key files involved:
- **Server filter logic**: `ReasoningFilter.cs`, `HarnessEventRelay.cs`, `ClientPayloadSanitizer.cs`, `MessagePersistenceService.cs`, `OpenCodeSessionMessageProxy.cs`
- **Server snapshot builder**: `MessagePersistenceService.BuildCommittedMessagePartPayload` (missing `ReasoningPart` case), `JsonContext.cs` (missing `CommittedReasoningPart` record)
- **Client state**: `event-state.ts` (already handles reasoning in streaming and snapshot mapping)
- **Client rendering**: `ActivityStream.vue` lines 599-623
- **Client types**: `client-types.ts` (already has `AccumulatedReasoningPart`)

## Scope
- In scope: Remove all server-side reasoning filters, persist reasoning in the database, add `CommittedReasoningPart` for snapshot delivery, update UI rendering to collapsible brain-icon sections, update affected tests.
- Out of scope: Adding user preferences to hide/show reasoning, reasoning token cost tracking, any new API endpoints.
- Constraints / assumptions: Existing data in the database will not have reasoning (that is fine; old sessions just won't show it). The `Brain` icon is available from `lucide-vue-next`. No database migration needed since `PartsJson` is a flexible JSON column.

## Objectives
- Reasoning parts flow through live events (streaming) to the client without filtering
- Reasoning parts are persisted durably in the database
- Reasoning parts are included in snapshot responses (page load / reconnect)
- UI renders reasoning as collapsible sections with a Brain icon

## Dependencies and Order
1. Server snapshot support (Task 1-2) must come before filter removal (Task 3-5), because removing filters without snapshot support would cause reasoning to stream but disappear on reload.
2. Filter removal tasks (3-5) are independent of each other and can be done in any order.
3. Client UI changes (Task 6) can happen in parallel with server work since the client already handles reasoning types.
4. Test updates (Task 7) should come last to validate everything.

## Tasks

- [x] 1. Add `CommittedReasoningPart` record and register in source-gen context
  - **What**: Add a `CommittedReasoningPart` record similar to `CommittedTextPart` and `CommittedFilePart`, then register it in `ApplicationJsonContext`. Add a `ReasoningPart` case to `BuildCommittedMessagePartPayload` so snapshots include reasoning.
  - **Files**:
    - `src/WeaveFleet.Application/JsonContext.cs` (add record at line ~43, add `[JsonSerializable]` attribute at line ~107)
    - `src/WeaveFleet.Application/Services/MessagePersistenceService.cs` (add `ReasoningPart` case in `BuildCommittedMessagePartPayload` switch at line 329-351)
  - **Depends on**: None
  - **Acceptance**:
    - `CommittedReasoningPart` record exists with fields: `Id`, `MessageID`, `SessionID`, `Type` ("reasoning"), `Text`, `Summary` (nullable)
    - `BuildCommittedMessagePartPayload` returns a serialized `CommittedReasoningPart` for `ReasoningPart` inputs instead of `null`
    - `ApplicationJsonContext` includes `[JsonSerializable(typeof(CommittedReasoningPart))]`

- [x] 2. Stop filtering reasoning in `MessagePersistenceService` persistence paths
  - **What**: Remove `ReasoningFilter.FilterDurableParts` calls from `ToPersistedMessage` and `ToHarnessMessage`. Stop skipping `ReasoningPart` in `MergePartAndMetadata` and `MergeMissingSnapshotParts`.
  - **Files**:
    - `src/WeaveFleet.Application/Services/MessagePersistenceService.cs`
      - Line 29: Change `ReasoningFilter.FilterDurableParts(message.Parts)` to `message.Parts` (and adjust type; serialize the `IReadOnlyList<MessagePart>` directly)
      - Line 107: Change `ReasoningFilter.FilterDurableParts(JsonSerializer.Deserialize(...))` to just use the deserialized list directly (cast to array if needed)
      - Line 262-263: Remove the early return for `ReasoningPart` in `MergePartAndMetadata`; add a proper merge case (match by first existing `ReasoningPart`, replace or append)
      - Lines 395-396: Remove the `case ReasoningPart: continue;` in `MergeMissingSnapshotParts`; add a proper merge case similar to `TextPart`
  - **Depends on**: None
  - **Acceptance**:
    - `ReasoningPart` instances are serialized into `PartsJson` during persistence
    - `ReasoningPart` instances are deserialized back from `PartsJson` without filtering
    - `MergePartAndMetadata` handles `ReasoningPart` (replace first existing or append)
    - `MergeMissingSnapshotParts` includes `ReasoningPart` from snapshots

- [x] 3. Remove reasoning filter from `HarnessEventRelay` live event pipeline
  - **What**: Remove the `RequiresReasoningFilter` block in `PumpAsync` (lines 230-236) so reasoning events pass through unfiltered. The whole `if (classification.RequiresReasoningFilter)` block should be removed; `eventToPublish` stays as `evt`.
  - **Files**:
    - `src/WeaveFleet.Infrastructure/Services/HarnessEventRelay.cs` (lines 228-236)
  - **Depends on**: Task 2 (persistence must accept reasoning before the relay starts sending it)
  - **Acceptance**:
    - No reference to `ReasoningFilter` in `HarnessEventRelay.cs`
    - `classification` variable on line 229 can also be removed if not used elsewhere in the method (check; it's only used for the filter block)

- [x] 4. Remove reasoning filter from `ClientPayloadSanitizer` snapshot endpoint
  - **What**: Remove `SanitizeMessages` call from `SessionEndpoints.cs` line 323 (use `page.Messages` directly). Remove or gut `ClientPayloadSanitizer` entirely since its only purpose is reasoning filtering. Also remove `SanitizeEventPayload` from the sanitizer.
  - **Files**:
    - `src/WeaveFleet.Api/Endpoints/SessionEndpoints.cs` (line 323: replace `ClientPayloadSanitizer.SanitizeMessages(page.Messages)` with `page.Messages`)
    - `src/WeaveFleet.Api/Endpoints/ClientPayloadSanitizer.cs` (delete file or leave empty static class if other references exist)
  - **Depends on**: None
  - **Acceptance**:
    - Snapshot endpoint returns messages with reasoning parts intact
    - No call to `SanitizeMessages` or `SanitizeEventPayload` remains in the codebase

- [x] 5. Remove reasoning filter from `OpenCodeSessionMessageProxy`
  - **What**: Remove `ReasoningFilter.FilterDurableParts` call at line 241 in `ToMessageLifecyclePayload`. Use `message.Parts` directly. Add handling for `ReasoningPart` in the `foreach` loop that converts parts to `MessageEventPart` (currently reasoning parts would be skipped by the default case).
  - **Files**:
    - `src/WeaveFleet.Infrastructure/Services/OpenCodeSessionMessageProxy.cs` (line 241 and surrounding conversion loop)
  - **Depends on**: None
  - **Acceptance**:
    - `ReasoningPart` is converted to a `MessageEventPart` with type "reasoning" in the proxy
    - No reference to `ReasoningFilter` in this file

- [x] 6. Update client UI to render reasoning as collapsible brain-icon sections
  - **What**: Replace the blockquote rendering of reasoning parts with a collapsible disclosure section. When collapsed, show the summary (if available) or a truncated preview. When expanded, show full text. Use the `Brain` icon from `lucide-vue-next`.
  - **Files**:
    - `client/src/components/session/ActivityStream.vue`
      - Lines 620-623: Replace blockquote rendering in `renderMessagePart` with a placeholder or remove (since reasoning will now be rendered as a Vue component, not raw markdown)
      - Add a new component or template section for reasoning parts that renders: Brain icon + "Reasoning" label + collapsible content
      - The `hasVisibleContent` function (lines 599-601) is already correct
    - Potentially extract a `ReasoningBlock.vue` component in `client/src/components/session/` for cleaner separation
  - **Depends on**: None (client already has the types)
  - **Acceptance**:
    - Reasoning parts render with a Brain icon from `lucide-vue-next`
    - Content is collapsible; collapsed by default
    - If `summary` is available, it shows as the collapsed preview; clicking expands to full `text`
    - If no `summary`, show truncated text (first ~100 chars) when collapsed
    - Reasoning is visually distinct from regular text (different background/border)

- [x] 7. Update server tests
  - **What**: Update tests that assert reasoning filtering behavior. Tests that verify reasoning IS stripped need to be inverted or removed. Tests that verify reasoning passes through need to be added.
  - **Files**:
    - `tests/WeaveFleet.Api.Tests/Endpoints/ClientPayloadSanitizerTests.cs` (delete file if sanitizer is deleted, or update tests)
    - `tests/WeaveFleet.Application.Tests/Services/ReasoningFilterTests.cs` (remove or update `FilterDurableParts` tests at lines 132-169; keep `FilterMessageEventPayload` and `IsReasoningPartEvent` tests only if `ReasoningFilter` is kept for other purposes)
    - `tests/WeaveFleet.Domain.Tests/Harnesses/EventTypeMetadataTests.cs` (update assertions about `RequiresReasoningFilter` if the property is removed)
  - **Depends on**: Tasks 2-5
  - **Acceptance**:
    - All tests pass with `dotnet test`
    - No tests assert that reasoning content is stripped
    - At least one test verifies reasoning survives the persistence round-trip
    - At least one test verifies reasoning appears in snapshot payloads

- [x] 8. Consider cleanup of `ReasoningFilter` and `RequiresReasoningFilter`
  - **What**: Evaluate whether `ReasoningFilter.cs` and the `RequiresReasoningFilter` property on `EventTypeMetadata` should be removed entirely or retained for potential future use. If no callers remain, delete them. If keeping for future opt-in filtering, mark with a comment.
  - **Files**:
    - `src/WeaveFleet.Application/Services/ReasoningFilter.cs` (potentially delete)
    - `src/WeaveFleet.Domain/Harnesses/EventTypeMetadata.cs` (potentially remove `RequiresReasoningFilter` property)
  - **Depends on**: Tasks 3-5 (all callers removed first)
  - **Acceptance**:
    - No dead code remains; either the filter is deleted or has a clear documented reason to stay

## Verification
1. **Server build**: `dotnet build` succeeds with no warnings related to reasoning
2. **Server tests**: `dotnet test` all pass
3. **Client build**: `cd client && bun run build` succeeds
4. **Manual verification**:
   - Start a session with a model that produces reasoning (e.g., Claude with extended thinking)
   - Verify reasoning appears in the UI as a collapsible section with Brain icon
   - Navigate away and back; reasoning should still be visible (loaded from snapshot)
   - Check browser devtools network tab: snapshot response includes reasoning parts in `partsJson`
5. **E2E** (if applicable): `dotnet test tests/WeaveFleet.E2E -p:SkipFrontendBuild=true --filter "Category=E2E"`
