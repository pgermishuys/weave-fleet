# New Session Composer

## TL;DR
Replace the five-card New Session form at `/sessions/new` with an empty session: the message box is focused, chips under it say where the session runs (pre-filled with your last choice), and Enter creates the session and sends the first message. Built in four stages that each ship on their own.

Agreed direction and mockup (2026-09-13): https://claude.ai/code/artifact/822ebe3d-b54b-40f5-b0f9-8ec25686486a
Before/after of the server fixes this builds on: https://claude.ai/code/artifact/2d6a2278-7a4e-4ef0-8661-00597c45a9ef

## Context
- `/sessions/new` renders `client/src/components/sessions/NewSessionForm.vue` (1,123 lines): five "Where to run" cards, a repository field, a branch field, and "More options" (title, project, tags, harness). `NewSessionDialog.vue` is its unused predecessor (`.weave/plans/inline-new-session-form.md`).
- Ways in, all navigating to `/sessions/new`: sidebar **+ New Session** (`SessionsPanel.vue`, `?projectId` from a project's **+**), command palette `new-session` (`use-commands.ts`), `FleetDashboard.vue`, and GitHub "start session" (`GitHubWorkItemDetailPage.vue`, preset passed through `useWorkspaceUiStore().newSessionDialogInitialSource`). None of these change.
- The create API already takes `initialPrompt` (`SessionEndpoints.cs`, `CreateSessionApiRequest`); the form never sends it. For GitHub sources the server prepends the issue context (`SessionOrchestrator.BuildCreateSessionInitialPrompt`).
- Lists the chips need already exist: `GET /api/repositories`, `GET /api/repositories/worktrees?path=`, `GET /api/repositories/detail?path=` (branches, sorted by recent commit).
- The session page shows prompts before the server confirms them from a registry keyed by session id (`client/src/composables/use-send-prompt.ts`, `sentPromptRegistry`).
- The session composer (`client/src/components/session/Composer.vue`, 1,161 lines) is bound to a running session (agents, models, drafts, queue, interrupt). The new page needs its look, not its wiring.
- End-to-end tests: seven test files create sessions through `tests/WeaveFleet.E2E/Pages/NewSessionFormPage.cs` (directory mode + title + submit, selectors `new-session-form`, `#new-session-directory`, `#session-title`, `create-session-submit`).
- The right panel (`SessionsV2RightPanel` via `AppShell.vue`) and `StatusBar.vue` render for the sessions rail regardless of route, so `/sessions/new` shows the previous session's panel and status.
- Stage 0 (below) fixed: worktrees start from `origin/<default>`, branch/folder clashes get `-2`, readable git errors, GitHub + existing worktree, client error messages, sidebar row for a just-opened session.

## Scope
- In scope: the `/sessions/new` page and its chips, first message on create, remembered choices, the sidebar draft row, choosing a base branch (small server addition), removing the old form and dialog.
- Out of scope: a model picker before the session exists (decided: not in the first version); model-generated branch names (decided: slug of the first message for now); changing the home page; the ways into the page.
- Constraints: Vue 3 `<script setup>`, Pinia, TanStack Vue Router, reka-ui menus/popovers already in `client/src/components/ui`, design tokens in `client/src/assets/main.css`; `bun` for scripts, CI parity on Node 22 (`npm ci`); never run the API with the real HOME (scratch runtime recipe in `.poc-runtime/`).

## Objectives
- Clicking **+ New Session** puts the cursor in a message box; typing and pressing Enter is the whole flow in the common case.
- Folder and workspace are chosen from chips that remember the last choice (workspace per repository; first time: New worktree).
- Nothing the old form could do is lost at any stage (directory, existing worktree, quick chat, project, title, tags, harness, GitHub context).
- The page never shows another session's panel or status.

## Dependencies and Order
0. Ship the fixes already made (separate PR, independent).
1. The composer page, at parity with today's form. Everything later builds on it.
2. Make it feel instant: draft row in the sidebar, first message visible at once, draft kept if you navigate away.
3. Base branch chip (needs a small server addition).
4. Clean-up.

Stages 2 and 3 are independent of each other once stage 1 has landed.

---

## Stage 0 — Ship the worktree and error fixes

- [x] 0.1 Commit the worktree work and open a PR — PR #193, branch `fix/worktree-creation`
  - **What**: The uncommitted changes in `.claude/worktrees/delegated-greeting-moonbeam`: worktree base/clash handling (`WorkspaceService.cs`), `RepositoryService.ResolveExistingWorktreeAsync` used by both repository and GitHub sources, orchestrator passes the real branch, `readErrorMessage` uses openapi-fetch's parsed error, `GetSessionResponse.ProjectName`, session route builds the sidebar row from `createdAt`/`projectId`/`projectName`, newest-first `upsertSession`. Tests: `WorkspaceServiceWorktreeTests`, `RealGitRepository` fixture, GitHub/repository provider tests, `EndpointGuardTests`, client store/action tests.
  - **Acceptance**: CI green on all .NET suites and the client (Node 22). PR description links the before/after page.

## Stage 1 — The composer page (parity with today)

- [x] 1.1 A pure request builder
  - **What**: `client/src/lib/new-session-request.ts`: `buildCreateSessionRequest(state)` turns chip state (folder: repo | directory | none; workspace: current | new | existing worktree path; project; title; tags; harness; GitHub preset; message) into the `createSession` arguments. Same source payloads the form builds today (`builtin.repository`, `builtin.local`, `builtin.quickchat`, `builtin.github` via `buildGitHubSessionSourceSelection` incl. `existingWorktreePath`). Branch for a new worktree: `fleet/<slug of the first message>` (lowercase, stop words dropped, ≤ 40 chars); none when there's no message (server generates one). `slugForBranch()` exported for reuse.
  - **Files**: `client/src/lib/new-session-request.ts`, `client/src/lib/__tests__/new-session-request.test.ts`
  - **Acceptance**: unit tests cover every folder × workspace combination, GitHub + existing worktree, slug edge cases (punctuation, emoji, very long, empty).

- [x] 1.2 Remembered choices
  - **What**: `useNewSessionDefaults()` on top of `usePersistedState`: last folder (repo path, directory path, or none) and, per repository path, last workspace mode (`current` | `new` | worktree path). First use of a repo: New worktree. A remembered repo or worktree that no longer exists falls back silently.
  - **Files**: `client/src/composables/use-new-session-defaults.ts` + test
  - **Acceptance**: tests for defaults, per-repo memory, and stale entries.

- [ ] 1.3 The page
  - **What**: `NewSessionComposer.vue` replaces `NewSessionForm.vue` in `routes/sessions.new.tsx`. Layout: sheet header "New session", empty state ("What should we work on?"), composer at the bottom where the session composer sits. Composer: autosizing textarea focused on open (`preventScroll`), harness picker only when more than one harness is enabled, send button. Enter creates, Shift+Enter is a new line. Under the box:
    - **Folder chip**: popover with search, "Recent" (from 1.2), all scanned repositories, "Browse for a folder…" (reuses `DirectoryPickerPopover`), "No folder, just chat".
    - **Workspace chip** (git repositories only): Current checkout (with its branch), New worktree, existing worktrees (last used first) from `useWorktrees`.
    - **"…" chip**: project (pre-set from `?projectId`, label shown on the chip when not Scratch), title (placeholder "Taken from your first message"), tags.
    - **Plan line**: one sentence saying what will happen ("New worktree …/fleet-fix-login on fleet/fix-login, from origin/main." / "Works directly in ~/source/repo on feature/x. That's not main." / "Chat only. No folder."), plus "Start without a message".
    - **GitHub preset**: the issue or PR shows as an attachment chip on the message (removable); repo pre-selected, New worktree, suggested branch.
    - Errors from create show above the composer (the server's message, now readable).
    - Esc closes an open menu and never leaves the page.
  - **Files**: `client/src/components/sessions/NewSessionComposer.vue` (+ small chip/popover subcomponents under `client/src/components/sessions/new-session/`), `client/src/routes/sessions.new.tsx`; delete `NewSessionForm.vue`
  - **Depends on**: 1.1, 1.2
  - **Acceptance**: component tests for chip visibility rules (workspace only for git repos, harness only with >1), Enter vs Shift+Enter, Esc, GitHub preset. Test ids kept: `new-session-form` on the page root, `create-session-submit` on the send button; `#new-session-directory` on the browse input and `#session-title` in the "…" menu, so the E2E page object keeps working with small changes.

- [ ] 1.4 Quick chat without the surprise
  - **What**: "No folder" selects the quick-chat source; nothing is created until Enter. Placeholder becomes "Ask anything…".
  - **Depends on**: 1.3
  - **Acceptance**: selecting "No folder" creates nothing; Enter creates a quick chat with the message.

- [ ] 1.5 No leftovers from the previous session
  - **What**: On `/sessions/new`, hide the sessions right panel (`AppShell.vue`, `showSessionsV2Panel`) and the status bar's session section (`StatusBar.vue` when there's no active session for the route; `sessions.new.tsx` clears `activeSessionId`).
  - **Files**: `client/src/components/layout/AppShell.vue`, `client/src/components/layout/StatusBar.vue`, `client/src/routes/sessions.new.tsx`
  - **Acceptance**: opening New Session from a busy session shows no right panel, no "IDLE · model · tokens".

- [ ] 1.6 End-to-end page object
  - **What**: Keep `NewSessionFormPage`'s public methods (`SetDirectoryAsync`, `SetTitleAsync`, `SubmitAsync`); change internals to: Folder chip → Browse → type path; "…" → title; `create-session-submit` (or "Start without a message" when no message is set). Update `tests/WeaveFleet.E2E/README.md` if it describes the form.
  - **Files**: `tests/WeaveFleet.E2E/Pages/NewSessionFormPage.cs`
  - **Acceptance**: the seven E2E test files pass unchanged (CI, or locally under the scratch-HOME recipe).

- [ ] 1.7 Live check
  - **What**: Scratch Fleet from the branch; Playwright through every way in (sidebar, project +, palette, GitHub preset via the store) and every folder × workspace choice; confirm the worktree/branch in the session terminal like the before/after page. Screenshots in both themes and at 400 px wide. Check what title a session gets when none is sent (see Decisions).
  - **Acceptance**: screenshots published with the PR; nothing the old form could do is missing.

## Stage 2 — Feels instant

- [ ] 2.1 Draft row in the sidebar
  - **What**: While `/sessions/new` is open, a "draft" row sits at the top of the group it will land in (Scratch or the chosen project), titled from the first line of the message as you type. Draft state (message, chips) lives in `useWorkspaceUiStore` so it survives navigating away and back; the row disappears when the draft is empty and you leave.
  - **Files**: `client/src/stores/workspace-ui.ts`, `client/src/components/sessions/SessionsPanel.vue`, `NewSessionComposer.vue`
  - **Acceptance**: typing updates the row; leaving with text keeps the draft and the row; sending turns it into the real session row in the same place (no jump).

- [ ] 2.2 First message visible at once
  - **What**: After create, seed the sent-prompt registry for the new session id before navigating, so the message shows immediately and reconciles when the server's copy arrives. If `initialPrompt` delivery can't be reconciled (no correlation id), switch to create-then-send through `useSendPrompt` for non-GitHub sources (see Decisions).
  - **Files**: `client/src/composables/use-send-prompt.ts` (export a seeding helper), `NewSessionComposer.vue`
  - **Acceptance**: no empty-conversation moment after Enter; no duplicated first message; works for GitHub sources (context + message).

## Stage 3 — Choose the base branch

- [ ] 3.1 Server: base branch and fetch as inputs
  - **What**: `RepositorySourceInput` and `GitHubSourceInput` accept optional `baseBranch` and `fetchOrigin` (default true). `WorkspaceIntent` gains an optional base; `WorkspaceService.CreateWorktreeAsync` uses it instead of the resolved default when given (still `--no-track`, still fetches `origin/<base>` first when asked). `GET /api/repositories/detail` adds `defaultBranch` (the same resolution the worktree code uses).
  - **Files**: `src/WeaveFleet.Infrastructure/JsonContext.cs`, `…/SessionSources/RepositorySessionSourceProvider.cs`, `…/GitHubSessionSourceProvider.cs`, `src/WeaveFleet.Application/SessionSources/*` (WorkspaceIntent), `src/WeaveFleet.Application/Services/WorkspaceService.cs`, `RepositoryService.cs`, `FleetEndpoints.cs`
  - **Acceptance**: `WorkspaceServiceWorktreeTests` gain cases for an explicit base, fetch off, and an unknown base (readable error).

- [ ] 3.2 Base chip
  - **What**: Shown for New worktree only: "from origin/main". Popover: branches (default and checked-out marked), "Fetch origin first" switch, branch name override (placeholder shows the generated name).
  - **Depends on**: 3.1, 1.3
  - **Acceptance**: component tests; live check that the worktree starts where the chip says.

## Stage 4 — Clean-up

- [ ] 4.1 Delete `NewSessionDialog.vue` and the store fields only it used (`newSessionDialogOpen`, `openNewSessionDialog`, …) and the commented-out block in `SessionsPanel.vue`.
- [ ] 4.2 Share the composer frame: extract the box/toolbar look into a presentational `ComposerFrame.vue` used by both the session composer and the new page, so they can't drift.
- [ ] 4.3 Quick-chat folders (if agreed): delete `~/.weave-fleet/quick-chats/<id>` when its session is deleted, only when no other live workspace uses the folder.
- [ ] 4.4 Write `.weave/learnings/new-session-composer.md` (what we learned, especially about first-message delivery).

## Decisions

Made (2026-09-13):
- First use of a repository defaults to New worktree; afterwards the last choice per repository.
- Branch names: slug of the first message (`fleet/fix-login-redirect`); a model-generated rename may come later.
- No model picker on this page in the first version.
- The ways into the page stay as they are.

Open, with a recommendation:
- **Session title when none is typed**: check in 1.7 whether OpenCode names the session from the first message. If it does, send no title; if not, send the first line (≤ 60 chars).
- **First message delivery**: start with `initialPrompt` (already used by GitHub and automations). Switch to create-then-send in 2.2 only if the first message can't be shown without a gap or a duplicate.
- **Esc on the page**: closes menus only, never leaves (the draft is kept in stage 2 anyway).
- **Quick-chat folders**: delete with their session (4.3). Needs a yes.

## Verification (every stage)
- Unit/component tests (vitest, Node 22) and the .NET suites; E2E where the page object changed.
- A live check against a scratch Fleet built from the branch (scratch HOME, port 5131, `.poc-runtime/`), driven by Playwright, with screenshots in both themes and at phone width attached to the PR.
