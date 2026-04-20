# Make All 49 E2E Tests Pass

## TL;DR
> **Summary**: Add 40 missing `data-testid` attributes across Vue components, build a proper dashboard page with summary bar/session cards/retention filter, add session detail banners/actions/fork dialog, wire sidebar `data-tree-leaf`/`data-session-id`/`data-project-id` attributes, and add context menu items — all backed by real API data (no mocks).
> **Estimated Effort**: Large

## Context
### Original Request
Make all 49 Playwright E2E tests pass. Currently 12 pass (onboarding, OIDC, GitHub security), 37 fail because the Vue UI lacks `data-testid` attributes and several UX flows (dashboard session cards, retention filter, session detail banners, stop/archive/fork dialogs, sidebar context menus).

### Key Findings
1. **No `data-testid` attributes exist anywhere** in `client/src/` — all 40 are missing.
2. **The home page (`/`) is a placeholder** — it renders a static "Sessions view placeholder" div. Tests expect a full dashboard with `new-session-button`, `summary-bar`, `empty-state`, `session-card` elements, and a retention filter dropdown.
3. **Session detail page** (`/sessions/$id`) renders `ActivityStream` + `Composer` but lacks: status indicator (`data-status` attribute), archived/stopped banners, stop/archive/unarchive/resume/delete/fork actions, and the fork dialog.
4. **The sidebar** (`SessionsPanel` + `ProjectGroup` + `SessionItem`) lacks `data-tree-leaf`, `data-session-id`, `data-project-id` attributes and context menu items for "Archive", "Unarchive", "Permanently Delete", "New Session".
5. **NewSessionDialog** lacks `data-testid="new-session-dialog"` and `data-testid="create-session-submit"`. The `#session-title` input ID is `new-session-title` (test expects `#session-title`).
6. **ConfirmDeleteSessionDialog** lacks `data-testid="delete-dialog-confirm"` and `data-testid="delete-dialog-cancel"`.
7. **MessageBubble** lacks `data-testid="message-item"`, `data-role`, and `data-testid="message-sender-name"`.
8. **Composer** textarea lacks `data-testid="prompt-input"`, send button lacks `data-testid="prompt-send-button"`, and there's no disabled state for archived sessions.
9. **ActivityStream** lacks `data-testid="activity-stream"`.
10. **Analytics page** already has an `<h1>Analytics</h1>` heading — the test uses `GetByRole(Heading, "Analytics")` so it should work once the route loads without error.
11. **SkillPathTraversalSecurityTests** (5 tests) are pure API tests — they should already pass since they don't touch the UI. Need to verify.
12. **The `useSessions` composable** already supports `retentionStatus` parameter — the dashboard just needs to wire it up.

## Objectives
### Core Objective
Make all 49 E2E tests pass without modifying any test files or backend API contracts.

### Deliverables
- [x] Dashboard page with summary bar, session cards, empty state, new session button, retention filter
- [x] Session detail page with status indicator, banners, stop/archive/fork flows, and fork dialog
- [x] Sidebar with `data-tree-leaf`, `data-session-id`, `data-project-id` and context menu actions
- [x] All 40 `data-testid` attributes added to correct components
- [x] MessageBubble with `data-role` and `message-sender-name`
- [x] Composer with `prompt-input`, `prompt-send-button`, disabled state for archived sessions

### Definition of Done
- [x] `dotnet test tests/WeaveFleet.E2E/ --filter "Category=E2E&Category!=Benchmark"` — all 49 tests pass
- [x] `bun run build` succeeds
- [x] `bun run typecheck` passes
- [x] No mock/fallback data in API-backed components (except board)

### Guardrails (Must NOT)
- Must NOT modify any E2E test files (`tests/WeaveFleet.E2E/`)
- Must NOT change backend API contracts
- Must NOT break the 12 currently passing tests
- Must NOT touch kanban board code
- Must use Composition API with `<script setup lang="ts">`
- Must use shadcn-vue components
- Must use `bun` (not npm/yarn/pnpm)

## TODOs

### Phase 1: Foundation — Dashboard Page (Unblocks 90% of tests)

- [x] 1. **Build the Fleet Dashboard page at `/`**
  **What**: Replace the placeholder `index.tsx` home page with a real dashboard that renders: (a) a summary bar with `data-testid="summary-bar"` containing count elements with `data-testid="summary-{label}-count"` for "active", "idle", "queued", "tokens", "cost"; (b) a "New Session" button with `data-testid="new-session-button"`; (c) an empty state div with `data-testid="empty-state"` when no sessions exist; (d) session cards with `data-testid="session-card"` and `data-session-id="{id}"` when sessions exist; (e) a retention filter dropdown with `data-testid="retention-filter-trigger"` and options `data-testid="retention-filter-option-active"`, `retention-filter-option-archived"`, `retention-filter-option-all"`.

  The dashboard should use `useFleetSummary()` for the summary bar and `useSessions({ retentionStatus })` for session cards. The retention filter should be a shadcn-vue `DropdownMenu` (not Select) since the test uses `Force: true` click patterns typical of Radix dropdowns.

  Each session card must contain: `data-testid="session-status-indicator"`, `data-testid="session-title"`, and hover-revealed action buttons: `data-testid="session-delete-button"`, `data-testid="session-terminate-button"`, `data-testid="session-archive-button"`, `data-testid="session-unarchive-button"` (conditional on session state), and `data-testid="session-card-archived-badge"` (visible when archived).

  Clicking a session card navigates to `/sessions/{id}?instanceId={instanceId}`. The "New Session" button opens the `NewSessionDialog`.

  **Files**: `client/src/routes/index.tsx`, `client/src/components/dashboard/FleetDashboard.vue` (new), `client/src/components/dashboard/SummaryBar.vue` (new), `client/src/components/dashboard/SessionCard.vue` (new), `client/src/components/dashboard/RetentionFilter.vue` (new)
  **Acceptance**: `dotnet test tests/WeaveFleet.E2E/ --filter "FullyQualifiedName~FleetDashboardTests.Dashboard_WithNoSessions_ShowsEmptyStateAndSummaryBar"` passes; `dotnet test tests/WeaveFleet.E2E/ --filter "FullyQualifiedName~FleetDashboardTests.Dashboard_SummaryBar_IsVisible"` passes

### Phase 2: NewSessionDialog + ConfirmDeleteSessionDialog test IDs

- [x] 2. **Add data-testid attributes to NewSessionDialog**
  **What**: Add `data-testid="new-session-dialog"` to the `<DialogContent>` element. Add `data-testid="create-session-submit"` to the submit `<Button>`. Change the title input `id` from `new-session-title` to `session-title` (the test locates it via `Dialog.Locator("#session-title")`). The "Directory" label input must be findable via `page.GetByLabel("Directory")` — the existing `<label for="new-session-directory">Directory</label>` should work. The Source radio group must be findable via `page.GetByLabel("Source")` — the existing `<legend>Source</legend>` inside a `<fieldset>` should work with `GetByRole(AriaRole.Radio, { Name = "Directory" })` and `GetByRole(AriaRole.Radio, { Name = "Repository" })`.
  **Files**: `client/src/components/sessions/NewSessionDialog.vue`
  **Acceptance**: `dotnet test tests/WeaveFleet.E2E/ --filter "FullyQualifiedName~SessionLifecycleTests.CreateSession_RedirectsToSessionDetailPage"` passes

- [x] 3. **Add data-testid attributes to ConfirmDeleteSessionDialog**
  **What**: Add `data-testid="delete-dialog-confirm"` to the `<AlertDialogAction>` element. Add `data-testid="delete-dialog-cancel"` to the `<AlertDialogCancel>` element.
  **Files**: `client/src/components/sessions/ConfirmDeleteSessionDialog.vue`
  **Acceptance**: `dotnet test tests/WeaveFleet.E2E/ --filter "FullyQualifiedName~SessionLifecycleTests.DeleteSession_ShowsConfirmationDialog"` passes

### Phase 3: Session Detail Page — Activity Stream, Composer, Status Indicator

- [x] 4. **Add data-testid to ActivityStream and wire instanceId from URL**
  **What**: Add `data-testid="activity-stream"` to the `<section class="activity-stream">` element. The session detail route (`sessions.$id.tsx`) must also read `instanceId` from the URL search params (via `Route.useSearch()`) and pass it to `ActivityStream` and `Composer`. Currently it only looks up instanceId from the sessions store, which won't work for direct-URL navigation to DB-seeded sessions.
  **Files**: `client/src/components/session/ActivityStream.vue`, `client/src/routes/sessions.$id.tsx`
  **Acceptance**: `dotnet test tests/WeaveFleet.E2E/ --filter "FullyQualifiedName~MessagePersistenceTests.DirectLoad_RendersPersistedActivityFromDatabase"` passes

- [x] 5. **Add data-testid to MessageBubble**
  **What**: Add `data-testid="message-item"` and `data-role="{user|assistant}"` to the `<article class="message">` element. The `data-role` must be passed as a prop from `ActivityStream`. Add `data-testid="message-sender-name"` to the `<span class="msg-author">` element.
  **Files**: `client/src/components/session/MessageBubble.vue`, `client/src/components/session/ActivityStream.vue`
  **Acceptance**: `dotnet test tests/WeaveFleet.E2E/ --filter "FullyQualifiedName~MessagePersistenceTests.PersistedMessages_DisplayCorrectAgentNames"` passes

- [x] 6. **Add data-testid to Composer and implement disabled state**
  **What**: Add `data-testid="prompt-input"` to the `<textarea>`. Add `data-testid="prompt-send-button"` to the send `<button>`. The composer must accept an `archived` or `disabled` prop — when the session is archived, the textarea and send button must be `disabled`. The session detail route must pass the session's retention status to the Composer.
  **Files**: `client/src/components/session/Composer.vue`, `client/src/routes/sessions.$id.tsx`
  **Acceptance**: `dotnet test tests/WeaveFleet.E2E/ --filter "FullyQualifiedName~SessionLifecycleTests.SessionDetail_PromptInputAndActivityStreamVisible"` passes

- [x] 7. **Add session status indicator to session detail page**
  **What**: Add a `<span data-testid="session-status-indicator" data-status="{idle|working}">` element to the session detail page. The `data-status` value must be `"working"` when `activityStatus === "busy"` and `"idle"` otherwise. This element must be visible on the session detail page (can be in the route component or a new header component). The status must update in real-time via WebSocket events (the existing `useSessionEvents` composable already tracks session status changes via the sessions store).
  **Files**: `client/src/routes/sessions.$id.tsx`
  **Acceptance**: `dotnet test tests/WeaveFleet.E2E/ --filter "FullyQualifiedName~SessionMessageTests.SendPrompt_ReceivesTextResponse_SessionTransitionsToIdle"` passes

### Phase 4: Session Detail Banners and Actions

- [x] 8. **Add stop/archive/unarchive/resume/delete banners and actions to session detail**
  **What**: Add the following to the session detail page (either in the route component or a new `SessionDetailHeader.vue`):

  - **Stop flow**: A `data-testid="session-stop-button"` button (visible when session is running). Clicking it shows a confirmation with `data-testid="session-stop-confirm-button"`. Confirming calls `POST /api/sessions/{id}/terminate` (via `useTerminateSession`).
  - **Stopped banner**: `data-testid="session-stopped-banner"` — visible when `lifecycleStatus` is "stopped"/"completed"/"disconnected". Contains `data-testid="session-archive-banner-button"` to archive, and `data-testid="session-resume-button"` to resume.
  - **Archived banner**: `data-testid="session-archived-banner"` — visible when `retentionStatus === "archived"`. Text must contain "Unarchive before resuming or sending prompts". Contains `data-testid="session-unarchive-banner-button"` and `data-testid="session-unarchive-button"` (can be same element).
  - **Archived badge**: `data-testid="session-archived-badge"` — visible when archived.
  - **Delete button**: `data-testid="session-delete-button"` — opens the `ConfirmDeleteSessionDialog`. After deletion, navigates to `/`.
  - **Abort button**: `data-testid="abort-button"` — visible when session is busy. Calls `POST /api/sessions/{id}/abort`.
  - **Fork button**: `data-testid="session-archived-fork-button"` — visible when archived. Opens the fork dialog.

  The session detail route must fetch session metadata (lifecycle/activity/retention status) from the API and update reactively via WebSocket. Use the existing `useSessionEvents` composable and sessions store.

  **Files**: `client/src/routes/sessions.$id.tsx`, `client/src/components/session/SessionDetailHeader.vue` (new)
  **Acceptance**: `dotnet test tests/WeaveFleet.E2E/ --filter "FullyQualifiedName~SessionLifecycleTests.StoppedSession_CanBeArchivedUnarchivedAndResumed"` passes

- [x] 9. **Build the Fork Session dialog**
  **What**: Create a fork dialog with `data-testid="fork-session-dialog"`. It must contain: `data-testid="fork-session-dialog-title"` (heading), `data-testid="fork-session-source-title"` (shows the source session's title), `data-testid="fork-session-title-input"` (input for new session title), `data-testid="fork-session-submit"` (submit button). Submitting calls `POST /api/sessions/{id}/fork` with the title, then navigates to the new session's detail page. The dialog opens when `session-archived-fork-button` is clicked.
  **Files**: `client/src/components/session/ForkSessionDialog.vue` (new), `client/src/routes/sessions.$id.tsx` or `client/src/components/session/SessionDetailHeader.vue`
  **Acceptance**: `dotnet test tests/WeaveFleet.E2E/ --filter "FullyQualifiedName~SessionLifecycleTests.ArchivedDetail_NewContextWindow_OpensForkDialogAndNavigatesToFreshSession"` passes

### Phase 5: Sidebar — Tree Leaf Attributes and Context Menu

- [x] 10. **Add data-tree-leaf, data-session-id to sidebar SessionItem**
  **What**: Add `data-tree-leaf` and `data-session-id="{sessionId}"` attributes to the outermost clickable element in `SessionItem.vue` (the `<div class="session-item-shell">` or the `<button class="session-item">`). The `FleetSidebarPage` test locates sessions via `[data-tree-leaf][data-session-id='{sessionId}']`.
  **Files**: `client/src/components/sessions/SessionItem.vue`
  **Acceptance**: `dotnet test tests/WeaveFleet.E2E/ --filter "FullyQualifiedName~SessionLifecycleTests.Detail_StopWithoutArchive_RemainsVisibleInActiveFleetAndSidebar"` passes

- [x] 11. **Add data-project-id to sidebar ProjectGroup**
  **What**: Add `data-project-id="{projectId}"` to the project header element in `ProjectGroup.vue`. The `FleetSidebarPage` test locates projects via `[data-project-id='{projectId}']`.
  **Files**: `client/src/components/sessions/ProjectGroup.vue`
  **Acceptance**: `dotnet test tests/WeaveFleet.E2E/ --filter "FullyQualifiedName~SessionLifecycleTests.NewSessionFromProjectContextMenu_OpensDialogAndCreatesSession"` passes

- [x] 12. **Add "Archive", "Unarchive", "Permanently Delete", "New Session" context menu items to sidebar**
  **What**: The sidebar `SessionItem.vue` context menu already has "Archive" (mapped to `handleArchive`), but the test expects the menu item text to be exactly "Archive", "Unarchive", and "Permanently Delete" (not "Delete"). Verify the existing context menu item labels match. The `ProjectGroup.vue` context menu already has "New Session". Ensure the "Permanently Delete" label is used instead of "Delete" for the destructive action in the sidebar context menu (the test calls `ClickSessionMenuItemAsync(sessionId, "Permanently Delete")`).
  **Files**: `client/src/components/sessions/SessionItem.vue`
  **Acceptance**: `dotnet test tests/WeaveFleet.E2E/ --filter "FullyQualifiedName~SessionLifecycleTests.Sidebar_ArchiveUnarchiveDelete_Flows"` passes

### Phase 6: Retention Filter Integration with Sidebar

- [x] 13. **Wire retention filter to both dashboard cards and sidebar sessions**
  **What**: When the retention filter changes on the dashboard, the sidebar must also reflect the same filter. The `useSessions` composable in `SessionsPanel.vue` must accept the retention status from a shared store or from the dashboard's filter state. The simplest approach: add `retentionStatus` to the sessions store, have the dashboard's `RetentionFilter` update it, and have `SessionsPanel` read from it. The sidebar's `FleetSidebarPage` test expects that when "Archived" filter is active, archived sessions appear in the sidebar, and when "Active" is active, they don't.

  Additionally, the retention filter dropdown trigger button must show the current filter label (e.g., "Show: Active"). The `SubAgentDelegationTests` test clicks `GetByRole(Button, { Name = "Show: Active" })` then `GetByRole(Menuitem, { Name = "All" })` — this confirms the dropdown must be a `DropdownMenu` with a trigger button whose accessible name includes the current filter.
  **Files**: `client/src/stores/sessions.ts`, `client/src/components/dashboard/FleetDashboard.vue`, `client/src/components/dashboard/RetentionFilter.vue`, `client/src/components/sessions/SessionsPanel.vue`
  **Acceptance**: `dotnet test tests/WeaveFleet.E2E/ --filter "FullyQualifiedName~SessionLifecycleTests.ArchivedSession_IsHiddenFromActiveAndVisibleInArchivedAndAllFleetAndSidebar"` passes

### Phase 7: Delegation / Sub-Agent Support

- [x] 14. **Render delegation links in activity stream and support parentSessionId breadcrumb**
  **What**: When a message contains a `tool` part with `toolName === "task"` and a delegation exists for that tool call, render a clickable link to the child session: `<a href="/sessions/{childId}?instanceId={childInstanceId}&parentSessionId={parentId}">`. The session detail route must read `parentSessionId` from search params and render a breadcrumb link back to the parent: `<a href="/sessions/{parentId}?instanceId={parentInstanceId}">Parent Session Title</a>`. The child session's messages must load from `/api/sessions/{childId}/messages` and stream via WebSocket. Use `GET /api/sessions/{sessionId}/delegations` to fetch delegation mappings.
  **Files**: `client/src/routes/sessions.$id.tsx`, `client/src/components/session/ActivityStream.vue`, `client/src/components/session/MessageBubble.vue`, `client/src/composables/use-delegations.ts` (new)
  **Acceptance**: `dotnet test tests/WeaveFleet.E2E/ --filter "FullyQualifiedName~SubAgentDelegationTests.DelegatedChildSession_StreamsLiveActivity_AndShowsBreadcrumb"` passes

### Phase 8: Remaining Test Coverage

- [x] 15. **Verify SkillPathTraversalSecurityTests pass (API-only)**
  **What**: These 5 tests are pure API tests that don't interact with the UI. They should already pass. Run them to confirm. If any fail, investigate the backend skill endpoint routing.
  **Acceptance**: `dotnet test tests/WeaveFleet.E2E/ --filter "FullyQualifiedName~SkillPathTraversalSecurityTests"` — all 5 pass

- [x] 16. **Verify AnalyticsPageTests pass**
  **What**: The analytics page already has `<h1>Analytics</h1>` which the test finds via `GetByRole(Heading, "Analytics")`. The test also checks `HasErrorAsync()` which looks for "Failed to load" text. Ensure the analytics page doesn't show "Failed to load" when the API returns empty data. Run to confirm.
  **Acceptance**: `dotnet test tests/WeaveFleet.E2E/ --filter "FullyQualifiedName~AnalyticsPageTests.AnalyticsPage_LoadsWithoutErrors"` passes

- [x] 17. **Verify ErrorHandlingTests pass**
  **What**: `SpawnFailure_ShowsErrorInDialog` — after spawn failure, the page should still show `new-session-button` (dashboard stays loaded) and URL should not contain `/sessions/`. `SendPromptFailure_ShowsErrorState` — after prompt failure, `prompt-input` should still be visible. These depend on tasks 1, 2, 4, 6 being complete. Run to verify.
  **Acceptance**: `dotnet test tests/WeaveFleet.E2E/ --filter "FullyQualifiedName~ErrorHandlingTests"` — both pass

- [x] 18. **Verify GoldenPathTests pass**
  **What**: The golden path test exercises the full flow: dashboard → new session → session detail → send prompt → receive response → idle. This depends on tasks 1-7 being complete. Run to verify.
  **Acceptance**: `dotnet test tests/WeaveFleet.E2E/ --filter "FullyQualifiedName~GoldenPathTests.CreateSession_SendPrompt_ReceivesStreamedResponse"` passes

- [x] 19. **Build the SPA and run the full E2E suite**
  **What**: Run `bun run build` to produce the `client/dist/` output that the ASP.NET Core server serves. Then run the full E2E suite. Fix any remaining failures.
  **Acceptance**: `bun run build && bun run typecheck && dotnet test tests/WeaveFleet.E2E/ --filter "Category=E2E&Category!=Benchmark"` — all 49 pass

## Verification
- [x] `bun run build` succeeds without errors
- [x] `bun run typecheck` passes without errors
- [x] `dotnet test tests/WeaveFleet.E2E/ --filter "Category=E2E&Category!=Benchmark"` — all 49 tests pass
- [x] The 12 previously passing tests still pass (onboarding, OIDC, GitHub security)
- [x] No mock/fallback data in API-backed components (except board)
