# Learnings: conversation-history-fidelity-gaps

## Task 1: Persist full user prompts as durable history entries
- **Discrepancy**: The plan referenced Claude-specific mapper logic for synthetic prompt creation, but the reusable implementation point was the shared `MessagePersistenceService`, not `ClaudeCodeMapper`.
- **Resolution**: Added a shared synthetic prompt-message factory in `MessagePersistenceService` and reused it from both harnesses.
- **Suggestion**: When prompt persistence must work across harnesses, point the plan at the shared message persistence layer first and treat harness mappers as secondary.

## Task 2: Make durable replay use final assistant snapshots instead of raw stream fragments
- **Discrepancy**: The plan focused on the outbox write path, but reconnect parity also required the client to treat committed `message.updated` snapshots as authoritative for text parts.
- **Resolution**: Switched OpenCode committed `message.updated` payloads to serialized persisted snapshots and taught `mergeMessageUpdate` to replace stale text parts from those snapshots.
- **Suggestion**: When changing committed-event payload semantics, audit both server serialization and client merge behavior together because reconnect parity spans both halves.

## Task 3: Keep the durable history contract minimal and explicitly classify non-essential metadata
- **Discrepancy**: The plan implied a possible contract/schema touch, but the narrowed transcript-fidelity acceptance criteria were already met by the existing minimal persisted message envelope.
- **Resolution**: Verified the current repository/API/client path continues to load full prompt and final assistant text without adding `parentId`, cost, token, or tool-result dependencies.
- **Suggestion**: For scope-control tasks, verify whether the current contract already satisfies the acceptance criteria before scheduling additive schema work.

## Task 4: Explicitly exclude durable tool output persistence from the narrowed history path
- **Discrepancy**: The plan framed this as an audit task, but the current code already excluded durable tool-result output; the missing piece was regression coverage to prevent later drift.
- **Resolution**: Added mapper and sanitizer guardrail tests proving tool outputs are not promoted into durable history/replay payloads while visible text remains intact.
- **Suggestion**: For “do not widen scope” tasks, prefer executable negative tests over speculative code changes.

## Task 5: Add narrow regression coverage for transcript fidelity and churn protection
- **Discrepancy**: The plan listed additional test files that were not needed because equivalent coverage already lived in the existing application, infrastructure, API, client, and E2E suites.
- **Resolution**: Extended the existing focused suites instead of creating new endpoint/E2E files, keeping the regression surface small while still covering prompt persistence, replay parity, sanitizer behavior, and churn guardrails.
- **Suggestion**: Prefer augmenting the closest existing suite before creating a new test file when the behavior already has a natural home.

## Post-plan follow-up: Reviewer blocker fixes
- **Discrepancy**: The original checked-off plan missed three terminal-review blockers: OpenCode slash commands still skipped durable prompt persistence, committed client snapshot merges could wipe file/tool parts, and committed outbox snapshots still stored reasoning at rest.
- **Resolution**: Added shared synthetic command-message creation and persisted it in `SendCommandAsync(...)`, changed client committed snapshot merge to replace only text/reasoning while preserving non-text parts, and filtered committed durable payloads down to visible text parts only.
- **Suggestion**: When a plan changes committed snapshot semantics, include explicit checklist items for command-entry parity, client mixed-part preservation, and at-rest reasoning/privacy review before marking the plan done.
