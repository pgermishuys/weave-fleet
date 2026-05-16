# Select Existing Worktree

## TL;DR
> **Summary**: Allow users to select an existing git worktree as the workspace when creating a session with "worktree" isolation strategy, instead of always creating a new one.
> **Estimated Effort**: Medium

## Context
### Original Request
When creating a session with "worktree" isolation strategy for a repository source, allow the user to pick an existing worktree (from `git worktree list`) in addition to the current "create new worktree" behavior.

### Key Findings
- `RepositorySourceInput` (in `Infrastructure/JsonContext.cs`) currently has: `repositoryPath`, `isolationStrategy`, `branch`. Adding an `existingWorktreePath` field is the simplest extension.
- `WorkspaceService.CreateWorkspaceAsync` switches on strategy: `"worktree"` creates new, `"existing"` uses source dir as-is. We can add handling for when an existing worktree path is provided alongside strategy `"worktree"`.
- `RepositoryService` already has `RunGitAsync` helper for git commands — ideal place to add `ListWorktreesAsync`.
- Frontend `NewSessionDialog.vue` already shows branch input when strategy is "worktree" — we add a sub-mode toggle (New vs Existing) and conditionally show branch input or worktree picker.
- The `RepositorySessionSourceProvider` validates inputs and builds a `WorkspaceIntent`. It needs to pass through the existing worktree path.

## Objectives
### Core Objective
Enable users to reuse an existing worktree as session workspace without creating a new one.

### Deliverables
- [ ] Backend API endpoint to list worktrees for a repository
- [ ] Backend support for `existingWorktreePath` in session creation flow
- [ ] Frontend UI for selecting between "New" and "Existing" worktree modes
- [ ] Frontend worktree picker when "Existing" is selected

### Definition of Done
- [x] `GET /api/repositories/worktrees?path=<repoPath>` returns worktree list
- [x] Creating a session with `existingWorktreePath` uses that path as workspace directory
- [x] UI shows worktree list with branch + path, user can select one
- [x] Main worktree (bare repo) is filtered out of the list

### Guardrails (Must NOT)
- Do NOT introduce a new isolation strategy value — keep `"worktree"` and add optional field
- Do NOT delete or prune worktrees
- Do NOT block selection if worktree is in use (show info only, v1)

## TODOs

- [x] 1. Add `ListWorktreesAsync` to `RepositoryService`
  **What**: Run `git worktree list --porcelain` in the repository, parse output into a list of `WorktreeInfo` records (path, branch, commit hash, isBare). Filter out the main worktree.
  **Files**: `src/WeaveFleet.Application/Services/RepositoryService.cs`
  **Acceptance**: Method returns parsed worktree list; bare/main worktree excluded.

- [x] 2. Add API endpoint `GET /api/repositories/worktrees`
  **What**: New endpoint accepting `path` query param, calls `RepositoryService.ListWorktreesAsync`, returns JSON array of `{ path, branch, commitHash }`.
  **Files**: `src/WeaveFleet.Api/Endpoints/FleetEndpoints.cs`, `src/WeaveFleet.Api/Endpoints/ApiResponses.cs`, `src/WeaveFleet.Api/JsonContext.cs`
  **Acceptance**: `GET /api/repositories/worktrees?path=/repo` returns worktree list JSON.

- [x] 3. Extend `RepositorySourceInput` with `existingWorktreePath`
  **What**: Add optional `existingWorktreePath` property to `RepositorySourceInput`. Update `JsonUnmappedMemberHandling` — already `Disallow`, so the field must be declared.
  **Files**: `src/WeaveFleet.Infrastructure/JsonContext.cs`
  **Acceptance**: Payload with `existingWorktreePath` deserializes without error.

- [x] 4. Extend `WorkspaceIntent` to carry existing worktree path
  **What**: Add optional `ExistingWorktreePath` field to `WorkspaceIntent` record.
  **Files**: `src/WeaveFleet.Application/SessionSources/SessionSourceContracts.cs`
  **Acceptance**: `WorkspaceIntent` can hold an existing worktree path.

- [x] 5. Update `RepositorySessionSourceProvider` validation and resolution
  **What**: When `isolationStrategy` is `"worktree"` and `existingWorktreePath` is provided: validate the path is a known worktree of the repo (run `git worktree list` and check), skip branch validation, pass path through in `WorkspaceIntent`.
  **Files**: `src/WeaveFleet.Infrastructure/SessionSources/RepositorySessionSourceProvider.cs`
  **Acceptance**: Valid existing worktree path resolves; invalid path returns validation error.

- [x] 6. Update `WorkspaceService.CreateWorkspaceAsync` to use existing worktree
  **What**: When strategy is `"worktree"` and `WorkspaceIntent.ExistingWorktreePath` is set, use that path directly (like `"existing"` strategy) instead of calling `CreateWorktreeAsync`. Adjust `CreateWorkspaceAsync` signature or add overload accepting `WorkspaceIntent`.
  **Files**: `src/WeaveFleet.Application/Services/WorkspaceService.cs`
  **Acceptance**: Session creation with existing worktree path uses it as workspace dir without creating new worktree.

- [x] 7. Wire `WorkspaceIntent.ExistingWorktreePath` through `SessionOrchestrator`
  **What**: Ensure `SessionOrchestrator.CreateSessionAsync` passes the full `WorkspaceIntent` (including existing worktree path) to `WorkspaceService`. Currently it passes `directory`, `strategy`, `branch` separately — either add a parameter or pass the intent object.
  **Files**: `src/WeaveFleet.Application/Services/SessionOrchestrator.cs`
  **Acceptance**: End-to-end flow from API to workspace creation uses existing worktree path.

- [x] 8. Add frontend composable `useWorktrees`
  **What**: New composable that fetches `GET /api/repositories/worktrees?path=X` when a repository is selected and strategy is "worktree". Returns reactive list of worktrees.
  **Files**: `client/src/composables/use-worktrees.ts`
  **Acceptance**: Composable fetches and exposes worktree list reactively.

- [x] 9. Update `NewSessionDialog.vue` UI
  **What**: When isolation strategy is "worktree", show a radio/toggle: "New worktree" (shows branch input, current behavior) vs "Existing worktree" (shows select dropdown of worktrees). When "Existing" is selected, populate `sessionSource.input.existingWorktreePath` instead of `branch`.
  **Files**: `client/src/components/sessions/NewSessionDialog.vue`
  **Acceptance**: UI shows toggle and worktree picker; selecting existing worktree sends correct payload.

- [x] 10. Add frontend API types
  **What**: Add `WorktreeInfo` type and update `SessionSourceSelection` input typing if needed.
  **Files**: `client/src/lib/api-types.ts`
  **Acceptance**: Types compile, used by composable and dialog.

- [x] 11. Add backend unit tests
  **What**: Test `ListWorktreesAsync` parsing, `RepositorySessionSourceProvider` validation with existing worktree path, `WorkspaceService` handling of existing worktree.
  **Files**: `tests/WeaveFleet.Application.Tests/Services/RepositoryServiceTests.cs`, `tests/WeaveFleet.Application.Tests/Services/SessionSourceResolutionServiceTests.cs`
  **Acceptance**: Tests pass covering happy path and validation errors.

## Verification
- [x] All existing tests pass (no regressions)
- [x] New backend tests cover worktree listing and session creation with existing worktree
- [x] Manual test: create session selecting existing worktree → session workspace is that worktree directory
- [x] Manual test: worktree list excludes main worktree
- [x] `dotnet build` succeeds
- [x] `npm run build` succeeds (client)
