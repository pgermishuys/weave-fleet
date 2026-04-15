# Narrow Durable History Fidelity to Minimal Final Text

## TL;DR
> **Summary**: Re-scope this work to durable transcript fidelity only: persist full user prompts and final assistant text reliably, make reconnect/history replay use those same final snapshots, and explicitly avoid durable storage of tool outputs or other high-churn data unless they are strictly required to reconstruct assistant text.
> **Estimated Effort**: Medium

## Context
### Original Request
Revise the existing conversation/history fidelity plan around a narrower goal: store only what is absolutely necessary to load history correctly, prioritize full user prompts plus full assistant responses in durable history, treat tool call result/output persistence as out of scope unless needed to reconstruct assistant text history, and add explicit performance guardrails to minimize DB/outbox churn.

### Key Findings
- The biggest transcript-fidelity gap is not “all metadata everywhere”; it is that OpenCode prompt sends are fire-and-forget and do not durably persist a synthetic user message the way Claude already does.
- The second critical gap is replay divergence: `OpenCodeHarnessSession.WriteDurableEventAsync` currently outboxes raw SSE payloads, while DB state may already contain buffered text merged into the persisted snapshot.
- The current persisted envelope already covers the core transcript shape (`id`, `session_id`, `role`, `parts_json`, `timestamp`, `created_at`, `agent_name`), so the narrowed objective may be achievable without broad schema growth if optional metadata stays deferred.
- `tool-result` history fidelity was previously treated as a gap, but for this reduced scope it is only relevant if assistant text cannot be reconstructed without it; current evidence does not justify durable storage of tool output bodies.
- `cost`/token data already has an analytics-oriented path and is not required to reconstruct the final transcript shown in history.
- This still requires the `.weave/plans` workflow because the change touches durable message persistence, outbox semantics, reconnect behavior, and regression coverage across multiple layers even though the objective is narrower.

| History element | Status | Decision | Why / storage rule |
|---|---|---|---|
| User prompt text | Required | Persist durably | Highest-priority transcript input; must survive reload even if the harness never echoes the prompt. |
| Assistant final text content | Required | Persist durably | Highest-priority transcript output; reload and reconnect must show the same final assistant text. |
| Message identity/order fields (`id`, `role`, `timestamp`) | Required | Persist durably | Needed to load and order history correctly. |
| Agent label (`agent`) | Required | Persist durably | Cheap to keep and useful for meaningful history rendering in multi-agent sessions. |
| File parts already represented in message parts | Optional | Keep existing behavior only | Preserve current behavior where already supported, but do not expand scope into heavy attachment fidelity work. |
| `modelId` / `providerId` | Optional | Keep only if available on final snapshot at low cost | Nice for labels/debugging, but not required to reconstruct transcript text. No extra streaming writes. |
| `completedAt` | Optional | Keep only if available on final snapshot at low cost | Useful for UX timing, but not required for transcript correctness. |
| `parentId` | Out of scope | Do not add durable storage for this work | Not required by the current linear history loader. |
| `cost` / token totals | Out of scope | Do not persist in durable history | Analytics concern, not transcript concern; avoid extra writes. |
| Tool result/output bodies | Out of scope unless proven necessary | Do not persist durably by default | Large/high-churn payloads; only justify if assistant text history cannot be reconstructed otherwise. |
| Streaming text deltas | Out of scope for durable storage | Keep ephemeral only | Durable path should prefer coalesced/final assistant snapshots, not per-delta writes. |

## Objectives
### Core Objective
Guarantee minimal durable history fidelity for transcript loading: full user prompts, full final assistant responses, and enough lightweight metadata to render useful history without depending on tool outputs or other high-churn payloads.

### Deliverables
- [x] Persist every user prompt exactly once in durable history for both harness flows, with full prompt text preserved.
- [x] Persist and replay final assistant text snapshots so `/api/sessions/{id}/messages` and `/api/sessions/{id}/committed-events` converge on the same final visible text.
- [x] Keep durable history narrow by excluding tool result/output bodies, streaming deltas, and analytics metadata unless they are strictly needed for transcript reconstruction.

### Definition of Done
- [x] `dotnet test tests/WeaveFleet.Application.Tests tests/WeaveFleet.Infrastructure.Tests tests/WeaveFleet.Api.Tests` passes.
- [x] `npm --prefix client test` passes.
- [x] Reloading a session preserves the full user prompt text and full final assistant text previously seen live.
- [x] Reconnect/gap-fill restores the same final assistant text without requiring stored tool-result output.
- [x] The narrowed solution does not introduce extra durable writes for streaming deltas or large tool outputs.

### Guardrails (Must NOT)
- [x] Do NOT broaden this effort back into full tool lifecycle/history fidelity unless transcript reconstruction demonstrably requires it.
- [x] Do NOT add durable storage for large tool outputs, tool result bodies, or raw streaming deltas by default.
- [x] Do NOT make cost/tokens a dependency of history loading in this change set.
- [x] Do NOT require destructive migrations or historical backfills to ship the forward fix.
- [x] Do NOT relax existing reasoning sanitization.

### Performance Guardrails
- [x] Avoid durable writes for `message.part.delta` and other streaming delta events.
- [x] Prefer coalesced/final assistant text snapshots over raw SSE passthrough in the outbox path.
- [x] Suppress DB/outbox writes when the merged visible text and required history fields have not changed.
- [x] Keep optional metadata write-once/write-late: only on final assistant snapshots, never on per-delta updates.
- [x] Avoid storing large tool outputs in durable history or the outbox unless strictly required to reconstruct assistant text.

## TODOs

- [x] 1. Persist full user prompts as durable history entries
  **What**: Make prompt submission itself responsible for durable transcript fidelity instead of assuming the harness will echo a usable user message. Reuse the Claude synthetic-user pattern as the baseline and add equivalent prompt persistence for OpenCode so every sent prompt has a durable user `TextPart` snapshot before history/reconnect depends on it.
  **Files**: `src/WeaveFleet.Infrastructure/Harnesses/OpenCode/OpenCodeHarnessSession.cs`, `src/WeaveFleet.Infrastructure/Harnesses/ClaudeCode/ClaudeCodeHarnessSession.cs`, `src/WeaveFleet.Infrastructure/Harnesses/ClaudeCode/ClaudeCodeMapper.cs`, `src/WeaveFleet.Application/Services/MessagePersistenceService.cs`
  **Acceptance**: After sending a prompt, reloading the session shows the full user prompt text even if the harness never emits a corresponding user message event.

- [x] 2. Make durable replay use final assistant snapshots instead of raw stream fragments
  **What**: Change the durable write/outbox path so committed replay is derived from the persisted post-merge message snapshot, not the raw SSE payload. Keep streaming deltas ephemeral, fold any buffered text into the stored message snapshot, and make committed replay/history loading converge on the same final assistant text.
  **Files**: `src/WeaveFleet.Infrastructure/Harnesses/OpenCode/OpenCodeHarnessSession.cs`, `src/WeaveFleet.Infrastructure/Harnesses/ClaudeCode/ClaudeCodeHarnessSession.cs`, `src/WeaveFleet.Application/Services/MessagePersistenceService.cs`, `src/WeaveFleet.Api/Endpoints/SessionEndpoints.cs`
  **Acceptance**: For an assistant response that streamed through deltas before the final snapshot arrived, `/api/sessions/{id}/messages` and `/api/sessions/{id}/committed-events` both expose the same final text after reconnect.

- [x] 3. Keep the durable history contract minimal and explicitly classify non-essential metadata
  **What**: Preserve the existing minimal transcript envelope as the default. Treat `modelId`/`providerId` and `completedAt` as optional-only-if-cheap final-snapshot metadata; keep `parentId`, `cost`, and token totals out of durable history unless transcript parity proves otherwise. Only add schema/contract fields if the narrowed acceptance criteria cannot be met without them.
  **Files**: `src/WeaveFleet.Domain/Harnesses/HarnessTypes.cs`, `src/WeaveFleet.Domain/Entities/PersistedMessage.cs`, `src/WeaveFleet.Application/Services/MessagePersistenceService.cs`, `src/WeaveFleet.Domain/Repositories/IMessageRepository.cs`, `src/WeaveFleet.Infrastructure/Data/Repositories/DapperMessageRepository.cs`, `client/src/lib/api-types.ts`, `client/src/lib/pagination-utils.ts`
  **Acceptance**: History loading works for full prompt + final assistant text without depending on `parentId`, `cost`, tokens, or stored tool-result output; any retained optional metadata remains nullable and additive.

- [x] 4. Explicitly exclude durable tool output persistence from the narrowed history path
  **What**: Audit the OpenCode and Claude durable event/persistence paths so they do not start storing tool result/output bodies just to satisfy reconnect/history parity. If a harness emits tool output live, keep that behavior ephemeral unless a concrete transcript reconstruction case proves durable retention is required.
  **Files**: `src/WeaveFleet.Infrastructure/Harnesses/OpenCode/OpenCodeMapper.cs`, `src/WeaveFleet.Infrastructure/Harnesses/OpenCode/OpenCodeHarnessSession.cs`, `src/WeaveFleet.Infrastructure/Harnesses/ClaudeCode/ClaudeCodeMapper.cs`, `src/WeaveFleet.Infrastructure/Harnesses/ClaudeCode/ClaudeCodeHarnessSession.cs`, `src/WeaveFleet.Api/Endpoints/ClientPayloadSanitizer.cs`
  **Acceptance**: The durable history/outbox design does not rely on persisted tool-result bodies, and large tool outputs do not become part of the normal replay path.

- [x] 5. Add narrow regression coverage for transcript fidelity and churn protection
  **What**: Focus tests on the reduced scope: full prompt preservation, full final assistant response preservation, parity between history load and reconnect replay for final text, null-safe backward compatibility for older rows, and negative coverage that guards against new DB/outbox churn from deltas or tool outputs.
  **Files**: `tests/WeaveFleet.Application.Tests/Services/MessagePersistenceServiceTests.cs`, `tests/WeaveFleet.Infrastructure.Tests/Data/Repositories/DapperMessageRepositoryTests.cs`, `tests/WeaveFleet.Infrastructure.Tests/Harnesses/OpenCode/OpenCodeHarnessSessionPersistenceTests.cs`, `tests/WeaveFleet.Infrastructure.Tests/Harnesses/OpenCode/OpenCodeMapperTests.cs`, `tests/WeaveFleet.Infrastructure.Tests/Harnesses/ClaudeCode/ClaudeCodeMapperTests.cs`, `tests/WeaveFleet.Api.Tests/Endpoints/ClientPayloadSanitizerTests.cs`, `tests/WeaveFleet.Api.Tests/Endpoints/WebSocketMessageFormatTests.cs`, `tests/WeaveFleet.E2E/Tests/MessagePersistenceTests.cs`, `client/src/lib/__tests__/pagination-utils.test.ts`, `client/src/lib/__tests__/fleet-event-contract.test.ts`, `client/src/lib/__tests__/event-state.test.ts`, `client/src/hooks/__tests__/use-session-events.test.ts`
  **Acceptance**: The new tests fail on at least one current transcript-fidelity gap (prompt loss, final-text replay mismatch, or churn regression) before implementation and pass once the narrowed behavior is in place.

## Verification
- [x] All tests pass
- [x] No regressions
- [x] Prompt-persistence tests cover OpenCode and Claude flows, including the case where the harness never echoes a user message.
- [x] Replay/history tests cover buffered assistant text that reconnects through committed events and still matches `/api/sessions/{id}/messages` final text.
- [x] Backward-compatibility tests cover older persisted rows that do not contain any newly-added optional metadata.
- [x] Negative churn tests verify no durable writes for streaming deltas and no accidental durable storage of large tool outputs.

## Post-plan follow-up fixes
- [x] Persist synthetic durable user history for `OpenCodeHarnessSession.SendCommandAsync(...)` so slash-command submissions survive reload/reconnect just like regular prompts.
- [x] Preserve existing non-text client parts when committed `message.updated` snapshots refresh authoritative text content after reconnect.
- [x] Exclude reasoning parts from durable committed outbox payloads so hidden reasoning is not stored at rest in replay snapshots.
- [x] Re-run release build and focused regressions after reviewer findings to confirm zero-warning build and corrected transcript behavior.
