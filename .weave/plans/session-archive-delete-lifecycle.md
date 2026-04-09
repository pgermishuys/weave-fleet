# Session Archive/Delete Lifecycle

## TL;DR
> **Summary**: Split session semantics into two axes: execution lifecycle (`running/stopped/completed/error/disconnected`) and retention lifecycle (`active/archived/deleted`). Add explicit Stop, Archive, Unarchive, and Delete behaviors so old sessions remain viewable without cluttering primary Fleet views.
> **Estimated Effort**: Large

## Context
### Original Request
Create an implementation plan for introducing clear Fleet session lifecycle semantics with user-facing Archive and Delete actions. Cover a model such as Active, Archived, Deleted; whether archived sessions are resumable or read-only; dashboard/sidebar/detail/list UX; backend/API changes; data model/state transitions; migration considerations; search/filter implications; phased execution; and validation guidance.

### Key Findings
- Current semantics are conflated. `client/src/hooks/use-terminate-session.ts` and `client/src/hooks/use-delete-session.ts` both call `DELETE /api/sessions/{id}`, so “Stop” and “Delete” currently hit the same backend path.
- `src/WeaveFleet.Application/Services/SessionOrchestrator.cs` hard-deletes the session row in `DeleteSessionAsync`, then broadcasts `session_stopped`; there is no separate archive path or `session_deleted` event.
- `src/WeaveFleet.Infrastructure/Harnesses/OpenCode/OpenCodeHarnessInstance.cs` best-effort deletes the OpenCode session during `StopAsync`, which is exactly the stop/delete conflation this work must unwind.
- The backend already distinguishes execution-oriented fields (`Status`, `LifecycleStatus`, `StoppedAt`) from visibility (`IsHidden`), but `IsHidden` is used for delegated child sessions, not user archival.
- Fleet UI grouping and visibility are driven by `sessionStatus`, `lifecycleStatus`, and `isHidden` across `client/src/app/page.tsx`, `client/src/components/layout/fleet-panel.tsx`, `client/src/components/fleet/live-session-card.tsx`, and `client/src/components/layout/sidebar-session-item.tsx`; there is no retention axis.
- Session detail already preserves stopped history (`client/src/app/sessions/[id]/_page-client.tsx`), so archive should build on “readable after stop” rather than inventing a separate history store.
- Recommended model: keep execution lifecycle as-is, add a separate retention lifecycle with `active` and `archived` persisted in the DB, and treat `deleted` as the irreversible terminal action that removes persisted data. Archived sessions should be read-only while archived and must be unarchived before resume/prompting.

## Objectives
### Core Objective
Introduce explicit, user-comprehensible session lifecycle semantics where Stop affects execution, Archive affects retention/visibility, and Delete permanently removes data.

### Deliverables
- [ ] A dual-axis session model: execution lifecycle + retention lifecycle
- [ ] Explicit backend/API operations for stop, archive, unarchive, and permanent delete
- [ ] User-facing Archive/Unarchive/Delete UX across dashboard, sidebar, and detail views
- [ ] Search/filter behavior that hides archived sessions by default but keeps them discoverable
- [ ] Migration, test coverage, and rollout guidance that avoid regressing delegated child-session behavior

### Definition of Done
- [ ] `GET /api/sessions` returns active sessions by default and can include archived sessions via an explicit filter
- [ ] Stopping a session no longer deletes its Fleet row or provider-backed history
- [ ] Archiving a session removes it from default Fleet/sidebar views while keeping its detail page accessible
- [ ] Archived sessions are read-only and require unarchive before resume or new prompts
- [ ] Permanent delete removes the session from Fleet and emits a distinct delete signal
- [ ] Validation passes via `dotnet test`, `npm --prefix client test`, `npm --prefix client typecheck`, and `dotnet build -c Release`

### Guardrails (Must NOT)
- [ ] Must NOT overload `lifecycle_status` to mean archived/deleted
- [ ] Must NOT reuse `is_hidden` for user archival; that flag remains for delegated/hidden sessions
- [ ] Must NOT hard-delete a session on Stop or Archive
- [ ] Must NOT allow archived sessions to accept prompts or resume in-place without explicit unarchive
- [ ] Must NOT break direct links to archived session detail pages

## TODOs

- [x] 1. Phase 0 — Lock the lifecycle contract and API shape
  **What**: Formalize the model as two separate axes: execution lifecycle (`running`, `stopped`, `completed`, `error`, `disconnected`) and retention lifecycle (`active`, `archived`). Treat Delete as a permanent operation, not a listable status. Add API fields such as `retentionStatus` and `archivedAt`, and define state transitions: `active ↔ archived`, `active/archived → deleted`; `running → stopped/completed/error/disconnected` stays execution-only. Archived sessions are read-only while archived; if a stopped/disconnected session still has resume capability, that capability is preserved but gated behind Unarchive.
  **Files**: `src/WeaveFleet.Application/DTOs/SessionDtos.cs`, `src/WeaveFleet.Api/Endpoints/SessionEndpoints.cs`, `client/src/lib/api-types.ts`, `client/src/lib/types.ts`
  **Acceptance**: Shared DTOs and TypeScript types clearly expose execution vs retention semantics, and no API contract implies that “stopped” or “completed” means archived or deleted.

- [x] 2. Phase 1 — Add retention fields and repository/query support
  **What**: Extend session persistence with `retention_status TEXT NOT NULL DEFAULT 'active'` and `archived_at TEXT NULL`. Keep existing `status`, `lifecycle_status`, and `is_hidden` intact. Add repository methods to archive/unarchive, fetch by retention filter, and optionally search within the selected retention scope. Add indexes for `retention_status` and common list queries.
  **Files**: `src/WeaveFleet.Domain/Entities/Session.cs`, `src/WeaveFleet.Domain/Repositories/ISessionRepository.cs`, `src/WeaveFleet.Infrastructure/Migrations/008_add_session_retention_status.sql`, `src/WeaveFleet.Infrastructure/Data/Repositories/DapperSessionRepository.cs`, `tests/WeaveFleet.Infrastructure.Tests/Data/Repositories/DapperSessionRepositoryTests.cs`
  **Acceptance**: New databases default all sessions to `active`; migrated databases backfill existing rows without changing visibility or execution state; repository tests cover archive/unarchive/list filtering.

- [x] 3. Phase 2 — Separate Stop from provider delete and permanent delete
  **What**: Replace the current “DELETE means stop or delete” behavior with explicit backend operations: `POST /api/sessions/{id}/stop`, `PATCH /api/sessions/{id}/retention` (or equivalent archive/unarchive endpoints), and `DELETE /api/sessions/{id}` for permanent delete only. Refactor orchestrator logic so Stop updates execution state and preserves the Fleet session record; Archive updates retention state only; Delete performs final cleanup and emits a distinct delete event. Audit harness behavior so provider-side session deletion moves out of the stop path and into permanent delete/best-effort purge semantics.
  **Files**: `src/WeaveFleet.Application/Services/SessionService.cs`, `src/WeaveFleet.Application/Services/SessionOrchestrator.cs`, `src/WeaveFleet.Api/Endpoints/SessionEndpoints.cs`, `src/WeaveFleet.Api/Endpoints/SessionEventEndpoints.cs`, `src/WeaveFleet.Domain/Harnesses/IHarnessInstance.cs`, `src/WeaveFleet.Infrastructure/Harnesses/OpenCode/OpenCodeHarnessInstance.cs`, `src/WeaveFleet.Infrastructure/Harnesses/ClaudeCode/ClaudeCodeHarnessInstance.cs`, `tests/WeaveFleet.Application.Tests/Services/SessionServiceTests.cs`, `tests/WeaveFleet.Application.Tests/Services/SessionOrchestratorTests.cs`, `tests/WeaveFleet.Api.Tests/Endpoints/SessionLifecycleEndpointTests.cs`
  **Acceptance**: Stop leaves the session accessible in Fleet data; Archive does not touch provider/runtime state beyond validation; Delete is the only permanent removal path; emitted events distinguish `session_stopped`, `session_archived`, `session_unarchived`, and `session_deleted`.

- [x] 4. Phase 3 — Update list/search/filter behavior around retention
  **What**: Make active sessions the default list everywhere users manage current work. Add explicit retention filtering to session list APIs and frontend polling so archived sessions are excluded by default but can be listed on demand. Ensure search respects the selected retention scope and that direct `GET /api/sessions/{id}` still returns archived sessions for deep links. Keep delegated hidden sessions (`is_hidden`) separate from archived sessions in all list logic.
  **Files**: `src/WeaveFleet.Domain/Repositories/ISessionRepository.cs`, `src/WeaveFleet.Infrastructure/Data/Repositories/DapperSessionRepository.cs`, `src/WeaveFleet.Application/Services/SessionService.cs`, `src/WeaveFleet.Api/Endpoints/SessionEndpoints.cs`, `client/src/hooks/use-sessions.ts`, `client/src/contexts/sessions-context.tsx`, `client/src/lib/workspace-utils.ts`, `client/src/components/fleet/fleet-toolbar.tsx`, `client/src/app/page.tsx`, `tests/WeaveFleet.Api.Tests/Endpoints/SessionLifecycleEndpointTests.cs`, `client/src/lib/__tests__/workspace-utils.test.ts`, `client/src/contexts/__tests__/sessions-context.test.ts`
  **Acceptance**: Archived sessions no longer appear in the default dashboard/sidebar query, can be surfaced with an explicit filter, and do not alter existing delegated child-session hiding rules.

- [x] 5. Phase 4 — Add Archive/Unarchive/Delete UX in dashboard and sidebar views
  **What**: Rework session actions so the primary semantics are obvious: running sessions offer Stop; stopped/completed/disconnected active sessions offer Archive, Resume (if applicable), and Delete in destructive UI; archived sessions offer Unarchive and Delete, and are visually labeled as archived. Add an “Archived” filter or section in the Fleet dashboard/sidebar so clutter reduction is explicit and reversible.
  **Files**: `client/src/app/page.tsx`, `client/src/components/fleet/live-session-card.tsx`, `client/src/components/fleet/session-group.tsx`, `client/src/components/layout/fleet-panel.tsx`, `client/src/components/layout/sidebar-session-item.tsx`, `client/src/components/fleet/confirm-delete-session-dialog.tsx`, `client/src/hooks/use-terminate-session.ts`, `client/src/hooks/use-delete-session.ts`, `client/src/hooks/use-archive-session.ts`, `client/src/hooks/use-unarchive-session.ts`, `client/src/components/layout/__tests__/sidebar-session-item.test.tsx`
  **Acceptance**: Users can no longer confuse Stop with Delete from dashboard/sidebar affordances, archived sessions are hidden from primary active views by default, and destructive copy clearly says “permanently delete”.

- [x] 6. Phase 5 — Make session detail archived-aware and read-only
  **What**: Update the session detail page to show retention state distinctly from execution state. Archived sessions should remain fully viewable, show an archived banner/badge, disable prompt input and runtime actions, and surface Unarchive plus “New context window” as the safe continuation path. If a session is stopped/disconnected and archived, Resume should be blocked until unarchive. Direct URLs to archived sessions must continue to load history and metadata.
  **Files**: `client/src/app/sessions/[id]/_page-client.tsx`, `client/src/components/session/prompt-input.tsx`, `tests/WeaveFleet.E2E/Tests/SessionLifecycleTests.cs`
  **Acceptance**: An archived session detail page is readable but not writable, direct navigation works, and the page makes it clear why Resume/Prompt are unavailable until Unarchive.

- [x] 7. Phase 6 — Handle migration, compatibility, and delegated-session edge cases
  **What**: Backfill all existing sessions to `retention_status='active'`; do not infer archive state from `stopped` or `completed`. Preserve `is_hidden` semantics for delegated child sessions and ensure archive filters do not accidentally surface hidden children in Fleet views. Update any event listeners/pollers that currently only refetch on `session_created` so archive/delete changes are reflected without full reloads.
  **Files**: `src/WeaveFleet.Infrastructure/Migrations/008_add_session_retention_status.sql`, `src/WeaveFleet.Application/Services/SessionOrchestrator.cs`, `client/src/contexts/sessions-context.tsx`, `client/src/components/layout/fleet-panel.tsx`, `tests/WeaveFleet.Application.Tests/Services/SessionOrchestratorTests.cs`, `tests/WeaveFleet.E2E/Tests/SubAgentDelegationTests.cs`
  **Acceptance**: Existing installs retain their current visible sessions as active, delegated hidden children remain hidden unless explicitly navigated to, and archive/delete state changes propagate live.

- [x] 8. Phase 7 — Validate end-to-end behavior and rollout safety
  **What**: Add focused API, repository, frontend, and E2E coverage for the new semantics. Validate the happy path and edge cases: running → stop → archive → unarchive → resume; stop without archive; archive without delete; permanent delete from active and archived states; direct-link access to archived detail; delegated child sessions unaffected by archive filters.
  **Acceptance**: Test coverage exists for backend transitions, list filtering, and user-facing actions; manual smoke guidance is documented for product review before rollout.

## Verification
- [x] All tests pass (`dotnet test` and `npm --prefix client test`)
- [x] No regressions in delegated child-session hiding and delegation links
- [x] `npm --prefix client typecheck` passes
- [x] `npm --prefix client lint` passes
- [x] `dotnet build -c Release` passes
- [x] Manual smoke: create session → stop → archive → confirm it disappears from default Fleet/sidebar but opens via direct URL
- [x] Manual smoke: archived session → unarchive → resume/new context window behaves as designed
- [x] Manual smoke: permanent delete removes the session from active and archived views and does not reuse the stop UX

## Manual Smoke Guidance
1. Create a new session from Fleet and confirm it appears in the default active view and sidebar.
2. Stop the session from detail or Fleet, then archive it. Confirm:
   - it disappears from the default active Fleet view and sidebar
   - it appears under the Archived and All retention filters
   - the direct `/sessions/{id}` URL still loads history and shows the archived read-only banner
3. From the archived detail page, unarchive the session. Confirm:
   - the archived banner disappears
   - stopped/disconnected state is preserved until Resume is clicked
   - Resume or New context window are the continuation paths, not inline prompt send
4. Re-archive the session and permanently delete it. Confirm:
   - delete uses the permanent-delete confirmation copy, not stop wording
   - the session disappears from Active, Archived, and All views after confirmation
   - delegated child sessions remain hidden from Fleet/sidebar throughout, but parent delegation links still navigate directly.
