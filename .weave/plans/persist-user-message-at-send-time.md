# Persist User Message at Send Time (OpenCode)

## TL;DR
> **Summary**: Persist user prompt text immediately in `OpenCodeHarnessSession.SendPromptAsync` so it's never lost when OpenCode omits `message.part.updated` for user messages.
> **Estimated Effort**: Short

## Context
### Original Request
Fix bug where user prompt text is lost because OpenCode's SSE stream sometimes sends `message.updated` (role=user, no parts) without a subsequent `message.part.updated`.

### Key Findings
- **ClaudeCode already does this** — `ClaudeCodeHarnessSession.SendPromptAsync` (line 134) calls `MessagePersistenceService.CreateUserPromptMessage` and persists immediately via `PersistMessageAsync`.
- **TestHarness already does this** — `TestHarnessSession.PersistUserPromptAsync` (line 391) persists at send time.
- **OpenCode does NOT** — `OpenCodeHarnessSession.SendPromptAsync` (line 137) fires the HTTP request but never persists the user message.
- **Deduplication concern**: When OpenCode echoes back `message.updated` with role=user, `HarnessEventPersistenceService.TryPersistMessageAsync` will create a *second* row with OpenCode's message ID (different from our synthetic `user-{guid}` ID). We must suppress the echo or reconcile IDs.
- The persistence service already handles "existing row + no parts = metadata-only merge" (line 232-253). If we persist first with a synthetic ID, the OpenCode echo creates a duplicate. Best fix: **skip persisting echoed user messages** in `HarnessEventPersistenceService` when a user message for that session was already persisted at send time, OR track the last-sent prompt's OpenCode message ID and replace our synthetic ID.

**Chosen approach**: Persist at send time with synthetic ID (matching ClaudeCode pattern). In `HarnessEventPersistenceService.TryPersistMessageAsync`, when we see a `message.updated` with role=user and the session already has a recent user message with matching (empty or identical) text, skip creating a duplicate. Simpler alternative: just let the dedup happen naturally — the echoed `message.updated` with role=user + no parts will find no existing row (different ID) and create a stub, but since we already have the full text in our synthetic message, the UI shows the correct one. **Actually simplest**: suppress echoed user messages entirely in the persistence layer — if a user message with the same session already exists with text, don't create another empty stub.

**Simplest correct approach**: Persist at send time. In `TryPersistMessageAsync`, when role=user AND parts are empty AND there's already a user message persisted within the last few seconds for that session, skip the stub. This avoids duplicates.

## Objectives
### Core Objective
Ensure user prompt text is always durably persisted regardless of OpenCode SSE behavior.

### Deliverables
- [ ] Persist user message in `OpenCodeHarnessSession.SendPromptAsync`
- [ ] Prevent duplicate user messages from OpenCode echo
- [ ] E2E test proving user text survives when `message.part.updated` is omitted

### Definition of Done
- [ ] `dotnet test` passes (all projects)
- [ ] E2E test: prompt sent → no `message.part.updated` emitted → GET messages API returns user message with correct text

### Guardrails (Must NOT)
- Must not break existing message ordering or ID references
- Must not suppress assistant message persistence
- Must not change the TestHarness or ClaudeCode paths (they already work)

## TODOs

- [ ] 1. Add user message persistence to OpenCodeHarnessSession.SendPromptAsync
  **What**: After the HTTP fire-and-forget call (line 180-184), persist a synthetic user message using the same pattern as ClaudeCode (line 134-135). Use `MessagePersistenceService.CreateUserPromptMessage(text, DateTimeOffset.UtcNow, options?.Agent)` and persist via `SessionActivityWriteService`.
  **Files**: `src/WeaveFleet.Infrastructure/Harnesses/OpenCode/OpenCodeHarnessSession.cs`
  **Acceptance**: After `SendPromptAsync` returns, the user message row exists in the DB with correct text.

- [ ] 2. Suppress duplicate user message stubs from OpenCode echo
  **What**: In `HarnessEventPersistenceService.TryPersistMessageAsync`, when processing a `message.updated`/`message.created` with role=user AND the event has no parts (empty parts list), check if a user message already exists for that session (query by session + role=user ordered by timestamp desc, limit 1). If one exists with non-empty `PartsJson`, skip persisting the stub. This prevents the empty-parts echo from creating a duplicate row.
  **Files**: `src/WeaveFleet.Infrastructure/Services/HarnessEventPersistenceService.cs`
  **Acceptance**: When OpenCode echoes `message.updated` (role=user, no parts) after we already persisted, no duplicate row is created.

- [ ] 3. Add E2E test: user message persisted without message.part.updated
  **What**: Create a test scenario where the prompt response sequence includes `message.updated` (role=user) but NO `message.part.updated` for that user message. After prompting, verify via the messages API that the user message text is present. Use `TestScenarioBuilder` to craft the response sequence — emit only `message.updated` with role=user + empty parts, then the assistant response. Assert GET `/api/sessions/{id}/messages` returns the user message with the original prompt text.
  **Files**: `tests/WeaveFleet.E2E/Tests/MessagePersistenceTests.cs`
  **Acceptance**: Test passes — user message text is returned by the API even though `message.part.updated` was never emitted.

## Verification
- [ ] All tests pass: `dotnet test`
- [ ] No regressions in existing MessagePersistenceTests
- [ ] New E2E test passes in CI
- [ ] Manual verification: send prompt in OpenCode session, kill SSE mid-stream, reload — user message text visible
