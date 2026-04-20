# API Wiring — Connect Vue Components to Real Backend

## TL;DR
> **Summary**: Replace all mock/hardcoded data in Vue components with real API calls via existing composables. Every user-facing screen should fetch from the ASP.NET Core backend on localhost:5001.
> **Estimated Effort**: Large

## Context
### Original Request
Wire the Vue Fleet Dashboard UI to the real backend API. Composables and API client infrastructure exist and are correct — they just aren't used by most components. Skip the Kanban board entirely.

### Reference Codebase
The original Fleet UI (React/Next.js) at `/Users/pgermishuys/source/weave-fleet/client` is the authoritative reference for all UX flows, API contracts, and component behavior. The backend is **ASP.NET Core** (not Go) at `/Users/pgermishuys/source/weave-fleet/src/WeaveFleet.Api/`.

### Key Findings

**Endpoint Coverage Audit:**

| Endpoint Group | Composable | Component Consumer | Status |
|---|---|---|---|
| **Sessions** | | | |
| GET /api/sessions | `use-sessions.ts` | `SessionsPanel.vue` uses `fallbackSessions` instead | ⚠️ |
| GET /api/sessions/{id} | `use-session-events.ts` (loads messages) | `SessionDetailPanel.vue` uses `mockSessionDetails` | ⚠️ |
| GET /api/sessions/{id}/delegations | `use-session-events.ts` | Used internally by session events | ✅ |
| GET /api/sessions/{id}/messages | `use-session-events.ts` | `ActivityStream.vue` uses hardcoded messages | ⚠️ |
| GET /api/sessions/{id}/committed-events | `use-session-events.ts` | Used internally | ✅ |
| GET /api/sessions/{id}/diffs | `use-diffs.ts` | `DiffView.vue` — needs verification | ⚠️ |
| GET /api/sessions/{id}/status | `session-status-utils.ts` | Used by `use-session-events.ts` | ✅ |
| POST /api/sessions | `use-session-actions.ts` → `useCreateSession()` | `SessionsPanel.vue` `handleNewSession()` is a stub | ⚠️ |
| POST /api/sessions/{id}/prompt | Not wired — `use-send-prompt.ts` only pushes to local array | `Composer.vue` uses `useSendPrompt()` but it never calls API | ⚠️ |
| POST /api/sessions/{id}/abort | `use-session-actions.ts` → `useAbortSession()` | No component uses it | ⚠️ |
| POST /api/sessions/{id}/resume | `use-session-actions.ts` → `useResumeSession()` | No component uses it | ⚠️ |
| POST /api/sessions/{id}/stop | `use-session-actions.ts` → `useTerminateSession()` | No component uses it | ⚠️ |
| POST /api/sessions/{id}/fork | `use-session-actions.ts` → `useForkSession()` | No component uses it | ⚠️ |
| POST /api/sessions/{id}/command | None | None | ❌ |
| POST /api/sessions/{id}/source-preview | None | None | ❌ |
| POST /api/sessions/{id}/sources | None | None | ❌ |
| DELETE /api/sessions/{id} | `use-session-actions.ts` → `useDeleteSession()` | No component uses it | ⚠️ |
| PATCH /api/sessions/{id} | `use-session-actions.ts` → `useRenameSession()` | No component uses it | ⚠️ |
| PATCH /api/sessions/{id}/retention | `use-session-actions.ts` → `useArchiveSession()`/`useUnarchiveSession()` | No component uses it | ⚠️ |
| PATCH /api/sessions/{id}/project | `use-session-actions.ts` → `useMoveSession()` | No component uses it | ⚠️ |
| **Projects** | | | |
| GET /api/projects | `use-projects.ts` | `SessionsPanel.vue` doesn't use it (groups by `projectName` from sessions) | ⚠️ |
| POST /api/projects | `use-session-actions.ts` → `useCreateProject()` | `handleNewProject()` is a stub | ⚠️ |
| PATCH /api/projects/{id} | `use-session-actions.ts` → `useUpdateProject()` | No component uses it | ⚠️ |
| PATCH /api/projects/{id}/reorder | `use-session-actions.ts` → `useReorderProject()` | No component uses it | ⚠️ |
| DELETE /api/projects/{id} | `use-session-actions.ts` → `useDeleteProject()` | No component uses it | ⚠️ |
| **Real-time** | | | |
| WS /ws | `use-weave-socket.ts` | Used by `use-session-events.ts` and `use-activity-stream.ts` | ✅ |
| GET /api/sessions/{id}/events (SSE) | `use-session-events.ts` (uses WS instead) | Connected via WS subscription | ✅ |
| GET /api/activity-stream (SSE) | `use-activity-stream.ts` (uses WS) | `BoardActivityPanel.vue` uses mock timer instead | ⚠️ |
| **Credentials** | | | |
| GET /api/credentials | `use-credentials.ts` | `CredentialsSection.vue` calls `apiFetch` directly (works!) | ✅ |
| PUT /api/credentials | `use-credentials.ts` | `CredentialsSection.vue` calls `apiFetch` directly | ✅ |
| PUT /api/credentials/{id} | `use-credentials.ts` | `CredentialsSection.vue` calls `apiFetch` directly | ✅ |
| DELETE /api/credentials/{id} | `use-credentials.ts` | `CredentialsSection.vue` calls `apiFetch` directly | ✅ |
| **Skills** | | | |
| GET /api/skills | `use-skills.ts` | No component uses it | ⚠️ |
| POST /api/skills | `use-skills.ts` | No component uses it | ⚠️ |
| DELETE /api/skills/{name} | `use-skills.ts` | No component uses it | ⚠️ |
| **Analytics** | | | |
| GET /api/analytics/summary | `use-analytics-summary.ts` | `AnalyticsPage.vue` uses hardcoded data | ⚠️ |
| GET /api/analytics/daily | `use-analytics-daily.ts` | `AnalyticsPage.vue` uses hardcoded data | ⚠️ |
| GET /api/analytics/sessions | `use-analytics-sessions.ts` | `AnalyticsPage.vue` uses hardcoded data | ⚠️ |
| GET /api/analytics/models | `use-analytics-models.ts` | `AnalyticsPage.vue` uses hardcoded data | ⚠️ |
| GET /api/analytics/export | None | None | ❌ |
| **GitHub** | | | |
| POST /api/integrations/github/auth/* | None (no composable file) | `GitHubPanel.vue` / `GitHubSettings.vue` | ❌ |
| GET /api/integrations/github/repos | None | `GitHubPanel.vue` uses mock data with fallback | ⚠️ |
| GET /api/integrations/github/repos/.../issues | None | `GitHubPanel.vue` uses `mockIssues` fallback | ⚠️ |
| GET /api/integrations/github/repos/.../pulls | None | `GitHubPanel.vue` uses `mockPullRequests` fallback | ⚠️ |
| GET /api/integrations/github/bookmarks | None | None | ❌ |
| **Workspaces** | | | |
| GET /api/workspace-roots | None (inline in `SettingsPage.vue`) | `SettingsPage.vue` calls `apiFetch` directly | ✅ |
| POST /api/workspace-roots | None | `SettingsPage.vue` calls `apiFetch` directly | ✅ |
| DELETE /api/workspace-roots/{id} | None | `SettingsPage.vue` calls `apiFetch` directly | ✅ |
| **Config** | | | |
| GET /api/config | `use-config.ts` | Needs verification | ⚠️ |
| **Fleet** | | | |
| GET /api/fleet/summary | `use-fleet-summary.ts` | Needs verification | ⚠️ |
| GET /api/repositories | `use-repositories.ts` | `RepositoriesPage.vue` calls `apiFetch` directly | ✅ |
| POST /api/repositories/refresh | None | `SettingsPage.vue` calls `apiFetch` directly | ✅ |
| GET /api/instances/{id}/models | None (composable exists but uses mock data) | `Composer.vue` via `useModels()` — mock data | ⚠️ |
| GET /api/instances/{id}/agents | None (composable exists but uses mock data) | `Composer.vue` via `useAgents()` — mock data | ⚠️ |
| GET /api/instances/{id}/commands | `use-commands.ts` | Needs verification | ⚠️ |
| **Board** | | | |
| All board-related | `board.ts` store | `KanbanBoard.vue`, `KanbanColumn.vue`, etc. | 🚫 |

**Components with mock data that need wiring:**
1. `SessionsPanel.vue` — `fallbackSessions` array (lines 25-68)
2. `SessionDetailPanel.vue` — `mockSessionDetails` + `fallbackSessionDetail` (lines 43-138)
3. `ActivityStream.vue` — hardcoded `baseMessages` (lines 51-162)
4. `AnalyticsPage.vue` — hardcoded `dailyAnalytics`, `modelAnalytics`, `sessionAnalytics` (lines 58-111)
5. `PlanPanel.vue` — `mockPlans` (line 17)
6. `use-agents.ts` — `mockAgents` (line 7)
7. `use-models.ts` — `mockModels` (line 8)
8. `use-send-prompt.ts` — pushes to local array, never calls API (line 58)
9. `GitHubPanel.vue` — `mockIssues`, `mockPullRequests` (lines 128, 164)
10. `BoardActivityPanel.vue` — `fallbackSessions`, mock timer (🚫 board — skip)
11. `TemplatesPage.vue` — `mockTemplates` from `mock-data.ts`
12. `QueuePage.vue` — `mockQueueItems` from `mock-data.ts`
13. `PipelinesPage.vue` — `mockPipelines` from `mock-data.ts`
14. `board.ts` store — `mockSessions` from `mock-data.ts` (🚫 skip)

## Objectives
### Core Objective
Every component renders real data from the backend API. No mock data remains in the active code paths (except board/kanban which is deferred).

### Deliverables
- [x] Sessions list fetched from API and rendered in SessionsPanel
- [x] Session detail fetched from API and rendered in SessionDetailPanel
- [x] Activity stream shows real messages from session events
- [x] Composer sends prompts via POST /api/sessions/{id}/prompt
- [x] Session actions (abort, resume, stop, fork, delete, rename, archive) wired to UI controls
- [x] New Session dialog creates sessions via API
- [x] New Project dialog creates projects via API
- [x] Analytics page uses real analytics composables
- [x] Models and agents fetched from instance API
- [x] GitHub plugin panel fetches real data
- [x] Skills management wired to settings
- [x] mock-data.ts imports removed from all non-board code

### Definition of Done
- [x] `bun run build` succeeds
- [x] `bun run typecheck` passes
- [x] `bun run test` passes
- [x] No mock/fallback data used for any wired endpoint
- [x] All buttons/actions trigger real API calls
- [x] Real-time updates flow via WebSocket

### Guardrails (Must NOT)
- Must NOT touch kanban board code (`board.ts`, `KanbanBoard.vue`, `KanbanColumn.vue`, `KanbanCard.vue`, `BoardSummaryPanel.vue`, `BoardControlsPanel.vue`, `BoardActivityPanel.vue`)
- Must NOT change backend API contracts
- Must NOT introduce new dependencies without justification
- Must NOT rewrite existing composables — use them as-is
- Must use Composition API with `<script setup lang="ts">`
- Must use shadcn-vue Dialog for new session/project dialogs
- Must use `bun` (not npm/yarn/pnpm)

### Guidance for Missing Components
When E2E tests or API wiring reveals missing components or functionality in the Vue UI (e.g. missing dialogs, missing page sections, missing `data-testid` attributes, missing context menus, etc.), the implementer should:
- **Use best judgement** to create the missing components or add the missing functionality
- **Use `/Users/pgermishuys/source/weave-fleet/client` as the source of inspiration** for behavior, UX flows, and feature completeness
- **Keep the aesthetics of the Vue UI rebrand** — match the existing design language (Tailwind 4, shadcn-vue, CSS custom properties from `mockups/mockup-j-evolved.html`), do NOT copy React styling verbatim
- **Prioritize functional parity** — if the original Fleet UI has a feature that the Vue UI is missing and the E2E tests expect it, implement it

## TODOs

### Phase 0: E2E Test Infrastructure

- [x] 0a. Fix NATS binary for E2E tests on macOS ARM64
  **What**: The E2E tests fail at startup because the embedded NATS server binary is only bundled for `win-x64`. The `NatsServerBinaryResolver` looks for `Nats/EmbeddedNatsServer/Binaries/osx-arm64/nats-server` in the build output. Two options:
  - **Quick fix**: Download the `nats-server` binary from https://github.com/nats-io/nats-server/releases for `osx-arm64` and place it at `src/WeaveFleet.Infrastructure/Nats/EmbeddedNatsServer/Binaries/osx-arm64/nats-server` (the `.csproj` already copies `Binaries/**/*` to output).
  - **Alternative**: Set `Fleet:Nats:ExternalUrl` in `FleetWebApplicationFactory.ConfigureWebHost()` to skip embedded NATS (the `NatsServerHostedService` has an escape hatch — if `ExternalUrl` is non-null, it skips binary startup).
  **Files**: `src/WeaveFleet.Infrastructure/Nats/EmbeddedNatsServer/Binaries/osx-arm64/nats-server` (new binary), or `tests/WeaveFleet.E2E/Infrastructure/FleetWebApplicationFactory.cs`
  **Acceptance**: `dotnet test tests/WeaveFleet.E2E/ --filter "Category=E2E"` gets past the NATS startup. Tests may still fail on UI assertions (expected — that's what the rest of this plan fixes).

- [x] 0b. Audit E2E page objects for data-testid selectors needed
  **What**: The E2E tests use `data-testid` selectors (e.g. `[data-testid='session-card']`). Audit all page objects in `tests/WeaveFleet.E2E/Pages/` to extract every `data-testid` value the tests expect. Cross-reference with the Vue components to identify which `data-testid` attributes are missing. Create a checklist of all required `data-testid` attributes that must be added to Vue components during the wiring work.
  **Files**: `tests/WeaveFleet.E2E/Pages/*.cs` (read-only), output: checklist in this plan or a separate file
  **Reference**: Page objects: `FleetDashboardPage.cs`, `NewSessionDialog.cs`, `SessionDetailPage.cs`, `AnalyticsPage.cs`, `FleetSidebarPage.cs`, `FleetLoginPage.cs`, `OnboardingWizardPage.cs`
  **Acceptance**: Complete list of `data-testid` values expected by E2E tests, mapped to the Vue component that should have them.

### Phase 1: Core Data Flow

- [x] 1. Wire SessionsPanel to useSessions composable
  **What**: Remove `fallbackSessions` array. Call `useSessions()` composable to fetch real sessions from `GET /api/sessions`. Replace the sessions store population (`sessions.value = [...fallbackSessions]`) with data from the composable. Map `SessionListItem` (from API types) to the `SessionSummary` shape the store expects, or refactor the store to use `SessionListItem` directly. Also call `useProjects()` to get real project data for grouping (colors, names).
  **Files**: `client/src/components/sessions/SessionsPanel.vue`, `client/src/stores/sessions.ts`
  **Acceptance**: Sessions panel shows sessions from the API. No `fallbackSessions` reference remains. Loading and error states displayed.

- [x] 2. Wire SessionDetailPanel to real session data
  **What**: Remove `mockSessionDetails` and `fallbackSessionDetail`. The component receives a session prop — enhance it to accept a full `SessionListItem` or fetch session detail via `apiFetch('/api/sessions/{id}')`. Display real agent, model, workspace, branch, token usage, cost, and files changed from the API response. Use `use-diffs.ts` for files changed data.
  **Files**: `client/src/components/session/SessionDetailPanel.vue`
  **Acceptance**: Session detail panel shows real data from API. No mock objects remain.

- [x] 3. Wire ActivityStream to useSessionEvents
  **What**: Remove hardcoded `baseMessages`. Use `useSessionEvents(sessionId, instanceId)` composable to get real messages. The composable already handles WebSocket subscriptions, message accumulation, and pagination. Render `AccumulatedMessage` objects via `MessageBubble`. The `instanceId` must come from the session detail (fetched in task 2).
  **Files**: `client/src/components/session/ActivityStream.vue`
  **Acceptance**: Activity stream shows real messages from the session. New messages appear in real-time via WebSocket.

- [x] 4. Wire PlanPanel to real todo data from session messages
  **What**: Remove `mockPlans`. There is **no dedicated plan/todo endpoint** — todo data comes from `todowrite` tool calls embedded in session messages. The original Fleet UI extracts todos by scanning `AccumulatedToolPart.state.output` for JSON arrays of `{content: string, status: "pending"|"in_progress"|"completed"|"cancelled", priority: "high"|"medium"|"low"}`. Port or create a `todo-utils.ts` (reference: `/Users/pgermishuys/source/weave-fleet/client/src/lib/todo-utils.ts`) that extracts the latest todos from accumulated messages by reverse-scanning for the last `todowrite`/`todo_write` tool call. The panel is **read-only** — no mutations. Display: progress bar, status icons (✅🔄❌⭕), priority badges, strikethrough for completed/cancelled. Todos update in real-time via WebSocket events flowing through `useSessionEvents`.
  **Files**: `client/src/components/session/PlanPanel.vue`, `client/src/lib/todo-utils.ts` (new)
  **Reference**: `/Users/pgermishuys/source/weave-fleet/client/src/lib/todo-utils.ts` (95 lines), `/Users/pgermishuys/source/weave-fleet/client/src/components/session/todo-sidebar-panel.tsx`
  **Acceptance**: Plan panel shows real todos extracted from session messages. Todos update in real-time. No mock plans remain. Missing status defaults to "pending", missing priority defaults to "medium".

### Phase 2: Session Interaction

- [x] 5. Wire Composer to send real prompts
  **What**: `use-send-prompt.ts` currently pushes to a local `sentPromptRegistry` array and never calls the API. Rewrite it to call `POST /api/sessions/{id}/prompt` via `apiFetch`. The request body should include `text`, `agentId`, `modelId`, and `effort`. After sending, the response will flow back via WebSocket events (handled by `useSessionEvents`). Keep the optimistic local message for immediate UI feedback.
  **Files**: `client/src/composables/use-send-prompt.ts`
  **Acceptance**: Typing a message and pressing Send/Ctrl+Enter calls POST /api/sessions/{id}/prompt. The message appears in the activity stream via WebSocket events.

- [x] 6. Wire useAgents to fetch from instance API
  **What**: Replace `mockAgents` with a real API call to `GET /api/instances/{instanceId}/agents`. The composable needs to accept an `instanceId` parameter. Return reactive agent list. Provide sensible defaults while loading.
  **Files**: `client/src/composables/use-agents.ts`
  **Acceptance**: Agent selector in Composer shows real agents from the backend. No mock agents remain.

- [x] 7. Wire useModels to fetch from instance API
  **What**: Replace `mockModels` with a real API call to `GET /api/instances/{instanceId}/models`. Same pattern as agents.
  **Files**: `client/src/composables/use-models.ts`
  **Acceptance**: Model selector in Composer shows real models from the backend. No mock models remain.

- [x] 8. Add session action buttons to SessionDetailPanel
  **What**: Add Abort, Resume, Stop, Fork, Delete, Rename, Archive/Unarchive buttons to the session detail panel. Each button calls the corresponding composable from `use-session-actions.ts`. Show loading states and error handling. Conditionally show buttons based on session status (e.g., Abort only when running, Resume only when idle).
  **Files**: `client/src/components/session/SessionDetailPanel.vue`
  **Acceptance**: Each action button triggers the correct API call. Loading spinners show during requests. Errors display inline.

### Phase 3: Dialogs

- [x] 9. Create NewSessionDialog component
  **What**: Create a dialog using shadcn-vue `Dialog` components. The dialog must match the original Fleet UI's `new-session-dialog.tsx` flow.

  **Backend contract** — `POST /api/sessions` accepts `CreateSessionApiRequest`:
  ```typescript
  {
    source?: { key: { providerId: string, sourceType: string, actionId: string, contractVersion: number }, input: Record<string, unknown> },
    directory?: string,           // Required unless cloud mode
    title?: string,               // Optional session name
    isolationStrategy?: "existing" | "worktree" | "clone",
    branch?: string,              // For worktree strategy
    harnessType?: string,         // "opencode" | "claude-code" — default "opencode"
    projectId?: string,           // Optional project assignment
    initialPrompt?: string,       // Optional
    onComplete?: { notifySessionId: string, notifyInstanceId: string }  // For delegation
  }
  ```
  Response: `{ instanceId: string, workspaceId: string, session: { id, title, time: { created, updated } } }`

  **Form fields**:
  1. **Source** (radio): Repository, Directory, or Managed Workspace (cloud only)
  2. **Repository** (autocomplete): Select from `GET /api/repositories` — only if Repository source
  3. **Branch** (text): Auto-generated from title (lowercase, no special chars, hyphens) — only if worktree strategy
  4. **Directory** (text/browse): Path input — only if Directory source
  5. **Title** (text): Optional session name
  6. **Project** (dropdown): Select from `GET /api/projects` — optional
  7. **Harness** (dropdown): Select from `GET /api/harnesses` — only if multiple available

  **Source payloads**:
  - Repository: `{ key: { providerId: "builtin.repository", sourceType: "repository", actionId: "start-session", contractVersion: 1 }, input: { repositoryPath, isolationStrategy, branch? } }`
  - Directory: `{ key: { providerId: "builtin.local", sourceType: "directory", actionId: "start-session", contractVersion: 1 }, input: { directory, isolationStrategy: "existing" } }`
  - Managed: `{ key: { providerId: "builtin.managed", sourceType: "managed-workspace", actionId: "start-session", contractVersion: 1 }, input: {} }`

  **Post-creation**: Close dialog → refetch sessions → navigate to `/sessions/{sessionId}?instanceId={instanceId}`
  **Error handling**: Show error banner in dialog. Button shows "Spawning…" with spinner during request.

  **Files**: `client/src/components/sessions/NewSessionDialog.vue`, `client/src/components/sessions/SessionsPanel.vue`
  **Reference**: `/Users/pgermishuys/source/weave-fleet/client/src/components/session/new-session-dialog.tsx`
  **Acceptance**: Clicking "New Session" opens dialog. Source picker works. Form validates (directory required unless cloud). Submit creates session via API. Dialog closes, navigates to new session.

- [x] 10. Create NewProjectDialog component
  **What**: Create a dialog using shadcn-vue `Dialog`.

  **Backend contract** — `POST /api/projects` accepts:
  ```typescript
  { name: string, description?: string }
  ```
  Response (201 Created): `{ id, name, description, type: "user", position, sessionCount: 0, createdAt, updatedAt }`

  **Form fields**:
  1. **Name** (text): Required, trimmed, auto-focused
  2. **Description** (text): Optional, trimmed, sent as undefined if empty

  **UX**: Submit disabled until name non-empty. Button shows "Creating…" with spinner. Error displayed in red alert. On success: close dialog → refetch projects → refetch sessions (to update grouping). No navigation — user stays on current page.

  **Files**: `client/src/components/sessions/NewProjectDialog.vue`, `client/src/components/sessions/SessionsPanel.vue`
  **Reference**: `/Users/pgermishuys/source/weave-fleet/client/src/components/fleet/create-project-dialog.tsx`
  **Acceptance**: Clicking "New Project" opens dialog. Name required. Submit creates project via API. New project appears in sidebar.

- [x] 11. Add context menu to SessionItem with full action set
  **What**: Add a **right-click context menu** to `SessionItem.vue` using shadcn-vue `ContextMenu` component. The original Fleet UI uses Radix ContextMenu triggered by right-click (no three-dot button).

  **Actions (11 total, conditionally shown based on session status)**:
  1. **Rename** (Pencil icon) — always available. Opens **inline edit** (not a dialog). Commits on Enter/blur, cancels on Escape. Calls `useRenameSession()`.
  2. **Interrupt** (OctagonX) — only if session is running + busy. Calls `useAbortSession()`.
  3. **Stop** (StopCircle) — only if session is running. Calls `useTerminateSession()`.
  4. **Resume** (Play) — only if stopped/completed/disconnected. Calls `useResumeSession()`.
  5. **Archive** (Square) — only if not archived and not running. Calls `useArchiveSession()`.
  6. **Unarchive** (Play) — only if archived. Calls `useUnarchiveSession()`.
  7. **New context window / Fork** (GitFork) — always available. Calls `useForkSession()`.
  8. **Move to Project** (FolderOpen) — **submenu** listing all projects. Calls `useMoveSession()`.
  9. **Copy Session ID** (Copy) — always available. Copies to clipboard.
  10. **Open in...** (ExternalLink) — **submenu** if external tools available.
  11. **Permanently Delete** (Trash2, destructive styling) — always available. Shows **AlertDialog confirmation** before calling `useDeleteSession()`.

  **Inline edit component**: Create a reusable `InlineEdit.vue` component (reference: `/Users/pgermishuys/source/weave-fleet/client/src/components/layout/inline-edit.tsx`, 134 lines). Text input that replaces the label, commits on Enter/blur, cancels on Escape.

  **Delete confirmation**: Use shadcn-vue `AlertDialog` — simple "Are you sure?" with cancel/confirm.

  **Files**: `client/src/components/sessions/SessionItem.vue`, `client/src/components/sessions/InlineEdit.vue` (new), `client/src/components/sessions/ConfirmDeleteSessionDialog.vue` (new)
  **Reference**: `/Users/pgermishuys/source/weave-fleet/client/src/components/layout/sidebar-session-item.tsx` (370 lines)
  **Acceptance**: Right-click on session shows context menu. All actions call correct API endpoints. Rename uses inline edit. Delete shows confirmation. Actions conditionally shown based on session status.

- [x] 12. Add context menu to ProjectGroup with full action set
  **What**: Add a **right-click context menu** to `ProjectGroup.vue` header using shadcn-vue `ContextMenu`. NOT available for the "Ungrouped" section.

  **Actions (5 total)**:
  1. **New Session** (Plus) — opens NewSessionDialog with this project pre-selected.
  2. **Rename** (Pencil) — opens **inline edit** on the project name. Uses `useUpdateProject()`. Keyboard shortcut: F2.
  3. **Move Up** (ArrowUp) — only if not first project. Calls `useReorderProject()`.
  4. **Move Down** (ArrowDown) — only if not last project. Calls `useReorderProject()`.
  5. **Delete** (Trash2, destructive) — shows **AlertDialog with 2 modes**: (a) move sessions to ungrouped, or (b) delete project and all sessions. Calls `useDeleteProject()`.

  **Keyboard shortcuts**: F2 for rename, Enter to toggle collapse, Space to toggle collapse.

  **Files**: `client/src/components/sessions/ProjectGroup.vue`, `client/src/components/sessions/ConfirmDeleteProjectDialog.vue` (new)
  **Reference**: `/Users/pgermishuys/source/weave-fleet/client/src/components/layout/sidebar-project-item.tsx` (304 lines), `/Users/pgermishuys/source/weave-fleet/client/src/components/layout/confirm-delete-project-dialog.tsx` (110 lines)
  **Acceptance**: Right-click on project header shows context menu. Rename uses inline edit. Delete shows 2-mode confirmation. Reorder works. Not available on "Ungrouped".

### Phase 4: Real-time

- [x] 13. Verify WebSocket connection bootstraps on app mount
  **What**: Ensure `useWeaveSocket()` is called at the app shell level so the WebSocket connection is established when the app loads. The composable uses `onMounted`/`onUnmounted` lifecycle hooks with subscriber counting. Verify that `AppShell.vue` or a parent component initializes the connection. If not, add it.
  **Files**: `client/src/components/layout/AppShell.vue`
  **Acceptance**: WebSocket connects to `ws://localhost:5001/ws` on app load. Reconnects automatically on disconnect.

- [x] 14. Wire activity stream subscription for session list updates
  **What**: Use `useActivityStream()` in `SessionsPanel.vue` (or the sessions store) to listen for `session.created`, `session.updated`, `session.deleted` events. On receiving these events, call `refetch()` on the `useSessions()` composable to refresh the list. This provides near-real-time session list updates without polling.
  **Files**: `client/src/components/sessions/SessionsPanel.vue`
  **Acceptance**: Creating/updating/deleting a session from another client or the backend causes the session list to update within seconds.

### Phase 5: Settings & Config

- [x] 15. Add Skills management section to SettingsPage
  **What**: Create a `SkillsSection.vue` component that uses `useSkills()` composable. Display installed skills list with name, description, path. Add "Install Skill" form (URL input). Add "Remove" button per skill. Wire to `installSkill()` and `removeSkill()` from the composable.
  **Files**: `client/src/components/settings/SkillsSection.vue`, `client/src/components/settings/SettingsPage.vue`
  **Acceptance**: Skills section shows installed skills from API. Install and remove actions work.

- [x] 16. Wire config composable if not already connected
  **What**: Verify `use-config.ts` is used by any component. If not, identify where config data (GET /api/config, GET /api/config/client) should be consumed — likely for displaying version info, paths, or client configuration in settings or the top bar.
  **Files**: `client/src/composables/use-config.ts`
  **Acceptance**: Config data from API is accessible where needed. No action needed if already wired.

### Phase 6: GitHub Integration

- [x] 17. Create GitHub plugin composables
  **What**: The GitHub integration is a **plugin** and its code must stay organized under `client/src/plugins/builtin/github/`. The original Fleet UI has 40+ files, 9 hooks, and 13 API endpoints for GitHub. Create composables within the plugin directory (not in the shared `composables/` folder).

  **Composables to create** (all under `plugins/builtin/github/composables/`):
  1. `use-github-auth.ts` — RFC 8628 device flow: `POST .../device-code` → `POST .../poll` (with interval) → `GET .../status`. State machine: idle → initiating → awaiting-auth → complete. Also PAT fallback.
  2. `use-github-repos.ts` — `GET /api/integrations/github/repos` with 15min TTL cache, deduplication via in-flight tracking.
  3. `use-github-issues.ts` — Dual-mode: REST API (`GET .../issues`) + Search API (`GET .../issues/search`). 300ms debounce on search. 30 items/page.
  4. `use-github-pulls.ts` — `GET .../pulls` with state/sort filters. 30 items/page.
  5. `use-github-metadata.ts` — Factory pattern for labels (`GET .../labels`), milestones (`GET .../milestones`), assignees (`GET .../assignees`). 5min TTL per-repo cache.
  6. `use-github-bookmarks.ts` — `GET/PUT/POST/DELETE .../bookmarks`. Server sync with localStorage migration.

  **Reference**: `/Users/pgermishuys/source/weave-fleet/client/src/hooks/integrations/github/` (9 hook files), `/Users/pgermishuys/source/weave-fleet/client/src/integrations/github/`
  **Files**: `client/src/plugins/builtin/github/composables/*.ts`
  **Acceptance**: All composables exist with typed functions. Auth flow handles device code polling. Caching works with TTLs.

- [x] 18. Wire GitHubPanel and GitHubSettings to real composables
  **What**: Replace `mockIssues` and `mockPullRequests` fallbacks in `GitHubPanel.vue`. Use the new plugin composables from task 17. The panel should show: bookmarked repos selector, tabs for Issues/PRs, real data with loading/error/empty states. Wire `GitHubSettings.vue` to `use-github-auth.ts` for the device flow UI (show device code, "Copy code" button, "Open GitHub" link, polling status) and PAT entry in a collapsible "Advanced" section. Add a `GitHubRepoCacheWarmer` startup hook that preloads repo list on mount.

  **Files**: `client/src/plugins/builtin/github/GitHubPanel.vue`, `client/src/plugins/builtin/github/GitHubSettings.vue`
  **Reference**: `/Users/pgermishuys/source/weave-fleet/client/src/integrations/github/components/`
  **Acceptance**: GitHub panel shows real issues and PRs when authenticated. Auth device flow works end-to-end. Bookmarked repos persist. No mock data remains.

### Phase 7: Analytics

- [x] 19. Wire AnalyticsPage to analytics composables
  **What**: Replace hardcoded `dailyAnalytics`, `modelAnalytics`, `sessionAnalytics` arrays with data from `useAnalyticsSummary()`, `useAnalyticsDaily()`, `useAnalyticsSessions()`, `useAnalyticsModels()`. Add `useAnalyticsFilters()` for date range and project filtering. Show loading states while data fetches. Handle empty states gracefully.
  **Files**: `client/src/components/analytics/AnalyticsPage.vue`
  **Acceptance**: Analytics page shows real data from API. Charts update when filters change. Loading and empty states work.

### Phase 8: Cleanup

- [x] 20. Remove mock-data.ts imports from non-board code
  **What**: `TemplatesPage.vue`, `QueuePage.vue`, `PipelinesPage.vue` import from `mock-data.ts`. These pages have no backend endpoints (templates, queue, pipelines are not in the API). Either: (a) show "Coming soon" empty states, or (b) leave mock data but document it as intentional placeholder. The board store (`board.ts`) imports `mockSessions` — leave this as-is (board is skipped).
  **Files**: `client/src/components/pages/TemplatesPage.vue`, `client/src/components/pages/QueuePage.vue`, `client/src/components/pages/PipelinesPage.vue`
  **Acceptance**: Non-board pages either show placeholder states or are documented as intentional mocks. No accidental mock data in API-backed features.

- [x] 21. Remove unused mock data from mock-data.ts
  **What**: After all wiring is complete, audit `mock-data.ts`. Remove exports that are no longer imported anywhere (except by board code). If the file becomes board-only, rename to `board-mock-data.ts` or move into the `board/` directory.
  **Files**: `client/src/lib/mock-data.ts`
  **Acceptance**: `mock-data.ts` contains only board-related mock data (or is deleted if board doesn't need it after refactor). No dead exports.

- [x] 22. Convert LinearPanel to "not connected" empty state
  **What**: `LinearPanel.vue` has `mockTickets`. **Confirmed: no Linear backend integration exists.** Replace mock data with a clean "Connect Linear" empty state — icon, description text, and a disabled/placeholder "Connect" button. Keep the plugin registration so it appears in the plugin list but clearly shows it's not yet available.
  **Files**: `client/src/plugins/builtin/linear/LinearPanel.vue`
  **Acceptance**: Linear panel shows "not connected" empty state. No mock tickets.

- [x] 23. Update sessions store to use API types
  **What**: The `SessionSummary` interface in `stores/sessions.ts` is a simplified type with only `id`, `title`, `projectName`, `status`. Replace with `SessionListItem` from `api-types.ts` which has the full API shape. Update all consumers.
  **Files**: `client/src/stores/sessions.ts`, `client/src/components/sessions/SessionsPanel.vue`, `client/src/components/sessions/SessionItem.vue`, `client/src/components/session/ActivityStream.vue`, `client/src/components/layout/RightPanel.vue`
  **Acceptance**: Sessions store uses the real API type. All consumers compile and render correctly.

## Verification
- [x] `bun run build` succeeds with zero errors
- [x] `bun run typecheck` passes with zero errors
- [x] `bun run test` passes — all existing tests still green
- [ ] `dotnet test tests/WeaveFleet.E2E/ --filter "Category=E2E"` — all 51 E2E tests pass (the primary acceptance gate)
- [x] Manual smoke test: open app, see real sessions list, click a session, see real messages, send a prompt, see response
- [x] Manual smoke test: create session, create project, rename session, delete session
- [x] Manual smoke test: analytics page shows real charts
- [x] Manual smoke test: settings page credentials and skills work
- [x] `grep -r "mockSessions\|mockAgents\|mockModels\|fallbackSession\|mockPlans\|mockIssues\|mockPullRequests\|mockTickets" client/src/ --include="*.vue" --include="*.ts" | grep -v board | grep -v __tests__` returns no results (no mock data outside board and tests)
