# Fleet Board MVP 1.1 — GitHub Integration & Manual Sync

## TL;DR
> **Summary**: Add GitHub Issues as the first board integration — configure repo sources, trigger manual sync, upsert issues into the inbox lane by source key, and preserve local lane/order assignments.
> **Estimated Effort**: Medium
> **Related PR**: https://github.com/pgermishuys/weave-fleet/pull/119

## Context
### Original Request
Layer GitHub as the first built-in integration on top of the MVP 1 board. Users configure a repo source, hit "Sync now," and GitHub issues land in the inbox lane. No scheduled jobs or webhooks yet.

### Key Findings
- MVP 1 establishes `BoardCard` with nullable `SourceType`, `SourceKey`, and `Metadata` fields — ready for external cards.
- MVP 1 establishes `BoardLane.IsInbox` — the designated landing lane for synced items.
- Existing GitHub plugin provides: `GET /api/integrations/github/repos/{owner}/{repo}/issues` (with filtering, pagination, labels), `GET /api/integrations/github/repos` (bookmarked repos), `GET /api/integrations/github/auth/status`.
- No new GitHub API proxy endpoints needed — reuse existing infrastructure.
- `BoardCard.SourceKey` format: `"github:owner/repo#123"` — unique per board, used for upsert dedup.

### Rationale
GitHub Issues is the most universal work-tracking primitive. The existing GitHub plugin already provides the data pipeline. Manual sync keeps complexity low while proving the integration model. The upsert-by-source-key pattern generalizes to any future provider.

## Scope

### In Scope
- `BoardSource` entity and persistence (tracks which repos feed a board)
- Add/remove board sources (repo + optional filter config)
- "Sync now" API endpoint that fetches issues from all configured sources
- Upsert logic: match by `sourceKey`, create new cards in inbox lane, update metadata on existing cards
- Preserve local `laneId` and `position` on re-sync (source wins for title/metadata, local wins for placement)
- Closed issues: update metadata but don't auto-move (user controls lane assignment)
- Stale detection: if an issue disappears from source, mark card metadata as stale (don't delete)
- Frontend: source configuration UI (pick repo from bookmarks, optional label filter)
- Frontend: "Sync now" button with progress/result feedback
- Frontend: visual indicator for synced cards (GitHub icon, link to issue)

### Out of Scope
- Scheduled/automatic sync (MVP v2)
- Webhooks (MVP v2)
- GitHub PRs as a source
- Write-back to GitHub (no closing issues, no label changes)
- Multiple source types (only GitHub in this phase)
- Background polling

## Domain Model Additions

```
BoardSource
  Id          string (ULID)
  BoardId     string (FK → Boards)
  ProviderType string ("github")
  Config      string (JSON: { "owner": "...", "repo": "...", "labels": "bug,feature", "state": "open" })
  LastSyncAt  string? (ISO 8601)
  CreatedAt   string (ISO 8601)
  UpdatedAt   string (ISO 8601)

BoardCard changes (no schema change, usage of existing fields):
  SourceType  = "github_issue" (for synced cards)
  SourceKey   = "github:owner/repo#123"
  Metadata    = JSON { number, state, labels[], assignee, html_url, updated_at }
```

**Key decisions:**
- `BoardSource.Config` is a JSON blob — flexible for different filter combinations per provider.
- Upsert key is `(BoardId, SourceKey)`. The `board_cards(source_type, source_key)` index from MVP 1 supports this.
- Sync is a backend operation: API fetches from GitHub, upserts cards, returns counts. Frontend doesn't orchestrate individual issue imports.
- No new migration for `BoardSource` if we add it to `015` during MVP 1. If MVP 1 is already shipped, add `016_add_board_sources.sql`.

## API Additions

```
GET    /api/boards/{boardId}/sources                  → BoardSource[]
POST   /api/boards/{boardId}/sources                  → BoardSource { providerType, config }
PATCH  /api/boards/{boardId}/sources/{sourceId}       → BoardSource { config }
DELETE /api/boards/{boardId}/sources/{sourceId}        → 204
POST   /api/boards/{boardId}/sync                     → SyncResult { added, updated, stale, errors }
```

`POST /api/boards/{boardId}/sync` behavior:
1. Load all `BoardSource` for the board
2. For each source, fetch issues from GitHub API (using existing plugin infrastructure)
3. For each issue, compute `sourceKey = "github:{owner}/{repo}#{number}"`
4. Upsert: if card with `sourceKey` exists → update title + metadata, preserve laneId/position. If not → create in inbox lane at next position.
5. For existing cards whose `sourceKey` no longer appears in source results → set `metadata.stale = true`
6. Update `BoardSource.LastSyncAt`
7. Return counts

## Deliverables
- [x] `BoardSource` entity and migration (016 if MVP 1 shipped, or extend 015)
- [x] `BoardSource` repository methods (CRUD)
- [x] Sync service: `IBoardSyncService` with upsert logic
- [x] Sync API endpoint
- [x] Source CRUD API endpoints
- [x] Frontend: source configuration UI (repo picker + filter)
- [x] Frontend: sync button with result feedback
- [x] Frontend: synced card visual treatment (icon, link, stale indicator)
- [x] Backend tests for sync upsert logic
- [x] Frontend tests for source management

### Definition of Done
- [x] Can add a GitHub repo as a board source
- [x] "Sync now" imports issues into inbox lane
- [x] Re-sync updates metadata but preserves lane/position
- [x] Removing a source does not delete its cards
- [x] Stale cards are visually indicated
- [x] `dotnet test` passes
- [x] Frontend tests pass

### Guardrails (Must NOT)
- Must NOT add scheduled jobs or background sync
- Must NOT add webhook receivers
- Must NOT write back to GitHub
- Must NOT add non-GitHub providers (but architecture should not preclude them)
- Must NOT auto-move cards on sync (user controls placement)

## TODOs

- [x] 1. **BoardSource Migration**
  **What**: Add `board_sources` table with columns matching the entity. Index on `board_sources(board_id)`. If MVP 1 migration is already applied, create `016_add_board_sources.sql`; otherwise fold into `015`.
  **Files**: `src/WeaveFleet.Infrastructure/Migrations/016_add_board_sources.sql`
  **Acceptance**: Migration applies cleanly.

- [x] 2. **BoardSource Entity**
  **What**: Add `BoardSource.cs` to domain entities.
  **Files**: `src/WeaveFleet.Domain/Entities/BoardSource.cs`
  **Acceptance**: Entity compiles, matches migration schema.

- [x] 3. **BoardSource Repository Methods**
  **What**: Add source CRUD methods to `IBoardRepository` and implementation. GetByBoardId, Create, Update, Delete.
  **Files**: `src/WeaveFleet.Domain/Repositories/IBoardRepository.cs`, `src/WeaveFleet.Infrastructure/Repositories/BoardRepository.cs`
  **Acceptance**: All CRUD operations work.

- [x] 4. **Board Sync Service**
  **What**: Create `IBoardSyncService` / `BoardSyncService`. Orchestrates: load sources → fetch issues from GitHub plugin → compute source keys → upsert cards → detect stale → update LastSyncAt → return result.
  **Files**: `src/WeaveFleet.Application/Services/IBoardSyncService.cs`, `src/WeaveFleet.Infrastructure/Services/BoardSyncService.cs`
  **Acceptance**: Upsert creates new cards in inbox lane, updates existing cards preserving lane/position, marks stale cards.

- [x] 5. **Source & Sync API Endpoints**
  **What**: Add source CRUD and sync endpoints to `BoardEndpoints.cs`.
  **Files**: `src/WeaveFleet.Api/Endpoints/BoardEndpoints.cs`
  **Acceptance**: All endpoints callable. Sync returns correct counts.

- [x] 6. **Backend Sync Tests**
  **What**: Test upsert logic: new issue → inbox, existing issue → metadata update only, disappeared issue → stale, lane/position preserved on re-sync.
  **Files**: `tests/WeaveFleet.Tests/Services/BoardSyncServiceTests.cs`
  **Acceptance**: All sync scenarios covered.

- [x] 7. **Frontend Source Configuration UI**
  **What**: UI to add/remove board sources. Repo picker from bookmarked repos. Optional label filter input. Shows configured sources with last sync time.
  **Files**: `client/src/components/board/BoardSourceConfig.vue`
  **Acceptance**: Can add repo source, see it listed, remove it.

- [x] 8. **Frontend Sync Button & Feedback**
  **What**: "Sync now" button on board. Shows loading state during sync. Displays result toast (X added, Y updated, Z stale).
  **Files**: `client/src/components/board/KanbanBoard.vue`, `client/src/stores/board.ts`
  **Acceptance**: Sync triggers, board refreshes with new/updated cards.

- [x] 9. **Frontend Synced Card Treatment**
  **What**: Synced cards show GitHub icon, issue number, link to GitHub. Stale cards show warning indicator. Distinguish manual vs synced cards visually.
  **Files**: `client/src/components/board/KanbanCard.vue`
  **Acceptance**: Visual distinction clear between manual and synced cards.

- [x] 10. **Frontend API Client Extensions**
  **What**: Add source CRUD and sync API client functions.
  **Files**: `client/src/lib/board-api.ts`
  **Acceptance**: All new endpoints have typed client functions.

- [x] 11. **Frontend Tests**
  **What**: Tests for source management store logic and sync flow.
  **Files**: `client/src/stores/__tests__/board.test.ts`
  **Acceptance**: Tests pass with mocked API responses.

## Verification
- [x] `dotnet build src/WeaveFleet.Api` succeeds with no warnings
- [x] `dotnet test` — all tests pass
- [x] Frontend builds without errors
- [x] Frontend tests pass
- [x] Manual smoke test: add GitHub repo source → sync → issues appear in inbox → move card to another lane → re-sync → card stays in moved lane with updated metadata
