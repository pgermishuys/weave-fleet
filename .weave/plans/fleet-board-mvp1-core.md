# Fleet Board MVP 1 — Core Board & Card Management

## TL;DR
> **Summary**: Ship a persistent kanban board with user-defined lanes, manual cards, and an inbox lane concept — no external integrations yet, but domain ready for future sync.
> **Estimated Effort**: Medium

## Context
### Original Request
Build the foundational board: one board per user, CRUD for lanes/cards, drag-and-drop reordering, inbox lane designation, and backend persistence via SQLite.

### Key Findings
- No board entities exist in the domain. Next migration is `015`.
- Existing patterns: Domain entities in `src/WeaveFleet.Domain/Entities/`, repository interfaces in `Domain/Repositories/`, implementations in `Infrastructure/Repositories/`, minimal API endpoints in `Api/Endpoints/`.
- Entities use string IDs (ULIDs), string timestamps (ISO 8601), no constructors.
- Existing `board-product-direction.md` and `fleet-board-mvp.md` plans provide prior thinking — this plan supersedes both with tighter scope (no GitHub sync, no session linking in MVP 1).
- Frontend has existing mock board (`board.ts` store, `board-mock-data.ts`) and kanban components that will be replaced.

### Rationale
The board must exist as a standalone, useful tool before any integrations. Users should be able to create cards manually, organize them across lanes, and designate one lane as the inbox where future synced items will land. This clean separation ensures the integration layer (MVP 1.1) has a solid foundation.

## Objectives
### Core Objective
Deliver a fully persistent, single-board-per-user kanban with lanes, cards, ordering, and an inbox lane marker.

### Deliverables
- [x] SQLite migration for `boards`, `board_lanes`, `board_cards` tables
- [x] Domain entities: `Board`, `BoardLane`, `BoardCard`
- [x] Repository interfaces and SQLite/Dapper implementations
- [x] API endpoints for full board/lane/card CRUD + reorder + move
- [x] Frontend store rewrite (API-backed, remove mock data)
- [x] Frontend kanban UI with drag-and-drop

### Definition of Done
- [x] `dotnet build` succeeds with no warnings
- [x] `dotnet test` passes all existing + new tests
- [x] Board data persists across app restarts
- [x] Full lifecycle works: create board → add lanes → mark inbox → create cards → drag between lanes → reorder → archive card

### Guardrails (Must NOT)
- Must NOT add GitHub sync or any external integration
- Must NOT add session linking
- Must NOT modify existing entities or migrations
- Must NOT add multi-user/sharing features
- Must NOT add background jobs or webhooks

## Scope

### In Scope
- One board per user (auto-created or explicit create)
- Rename board
- Create / rename / delete / reorder lanes
- Exactly one lane marked as `isInbox` per board (enforced by backend)
- Default lanes on board creation: Inbox (isInbox=true), In Progress, Done
- Create / rename / delete / archive cards (manual, no source)
- Move cards between lanes
- Reorder cards within a lane
- `BoardCard` has `sourceType` and `sourceKey` fields (nullable) — unused in MVP 1 but present for future sync
- Backend persistence (SQLite)
- API endpoints
- Frontend store + kanban UI

### Out of Scope
- GitHub integration / sync
- Multiple boards per user
- Session spawning from cards
- Background jobs, webhooks, triggers
- Card descriptions / rich content (title-only in MVP 1)
- Board templates
- Filtering / search

## Domain Model

```
Board
  Id          string (ULID)
  UserId      string
  Name        string (default: "My Board")
  CreatedAt   string (ISO 8601)
  UpdatedAt   string (ISO 8601)

BoardLane
  Id          string (ULID)
  BoardId     string (FK → Boards)
  Name        string
  Position    int (gap-based ordering, increment by 100)
  IsInbox     bool (exactly one per board)
  CreatedAt   string (ISO 8601)
  UpdatedAt   string (ISO 8601)

BoardCard
  Id          string (ULID)
  BoardId     string (FK → Boards, denormalized)
  LaneId      string (FK → BoardLanes)
  Title       string
  SourceType  string? (null for manual cards; future: "github_issue")
  SourceKey   string? (null for manual; future: "owner/repo#123")
  Metadata    string? (JSON blob, null for manual cards)
  Position    int (gap-based ordering within lane)
  ArchivedAt  string? (soft archive)
  CreatedAt   string (ISO 8601)
  UpdatedAt   string (ISO 8601)
```

**Key decisions:**
- `IsInbox` is a boolean on `BoardLane`. Backend enforces exactly-one invariant: setting a lane as inbox unsets the previous one.
- `SourceType`/`SourceKey` are nullable — manual cards leave them null. MVP 1.1 will populate them for GitHub issues.
- `Metadata` is a JSON blob for extensibility. Unused in MVP 1.
- Gap-based integer positions (100, 200, 300). Rebalance when inserting between adjacent integers.
- Cards use soft-archive (`ArchivedAt`) not hard delete for `archive` action. `delete` is a true delete.

## API Surface

```
GET    /api/boards                              → Board[] (user's boards; MVP: 0 or 1)
POST   /api/boards                              → Board { name }
PATCH  /api/boards/{boardId}                    → Board { name }
DELETE /api/boards/{boardId}                    → 204

GET    /api/boards/{boardId}/lanes              → BoardLane[]
POST   /api/boards/{boardId}/lanes              → BoardLane { name, position? }
PATCH  /api/boards/{boardId}/lanes/{laneId}     → BoardLane { name?, position?, isInbox? }
DELETE /api/boards/{boardId}/lanes/{laneId}     → 204 (fails if lane has cards)
PATCH  /api/boards/{boardId}/lanes/reorder      → 204 { laneIds: string[] }

GET    /api/boards/{boardId}/cards              → BoardCard[] (all, includes laneId)
POST   /api/boards/{boardId}/cards              → BoardCard { laneId, title }
PATCH  /api/boards/{boardId}/cards/{cardId}     → BoardCard { title?, laneId?, position? }
DELETE /api/boards/{boardId}/cards/{cardId}     → 204
POST   /api/boards/{boardId}/cards/{cardId}/archive → BoardCard
POST   /api/boards/{boardId}/cards/{cardId}/move    → BoardCard { laneId, position }
```

## TODOs

- [x] 1. **Database Migration**
  **What**: Create `015_add_board_tables.sql` with `boards`, `board_lanes`, `board_cards` tables. Add indexes on `boards(user_id)`, `board_lanes(board_id, position)`, `board_cards(board_id)`, `board_cards(lane_id, position)`, `board_cards(source_type, source_key)`.
  **Files**: `src/WeaveFleet.Infrastructure/Migrations/015_add_board_tables.sql`
  **Acceptance**: Migration applies cleanly on fresh and existing DBs.

- [x] 2. **Domain Entities**
  **What**: Add `Board.cs`, `BoardLane.cs`, `BoardCard.cs` following existing entity conventions (string properties, no constructors, matching migration schema).
  **Files**: `src/WeaveFleet.Domain/Entities/Board.cs`, `src/WeaveFleet.Domain/Entities/BoardLane.cs`, `src/WeaveFleet.Domain/Entities/BoardCard.cs`
  **Acceptance**: Entities compile, properties match migration columns exactly.

- [x] 3. **Repository Interfaces**
  **What**: Add `IBoardRepository` covering board CRUD, lane CRUD + reorder + inbox toggle, card CRUD + move + reorder + archive.
  **Files**: `src/WeaveFleet.Domain/Repositories/IBoardRepository.cs`
  **Acceptance**: Interface methods cover all API operations.

- [x] 4. **Repository Implementation**
  **What**: SQLite/Dapper implementation of `IBoardRepository`. Enforce inbox uniqueness constraint in lane update. Gap-based position rebalancing helper.
  **Files**: `src/WeaveFleet.Infrastructure/Repositories/BoardRepository.cs`
  **Acceptance**: All methods implemented. DI registration added in `Program.cs` or service registration file.

- [x] 5. **API Endpoints**
  **What**: `BoardEndpoints.cs` with all routes from API surface above. Follow existing minimal API patterns (`EndpointExtensions`, `Result<T>` returns). User-scoped via `IUserContext`.
  **Files**: `src/WeaveFleet.Api/Endpoints/BoardEndpoints.cs`
  **Acceptance**: All endpoints callable and returning correct status codes. Board ownership enforced.

- [x] 6. **Backend Tests**
  **What**: Repository tests (in-memory SQLite) and endpoint integration tests. Cover: CRUD, reorder, move, inbox toggle invariant, archive, delete lane with cards (should fail).
  **Files**: `tests/WeaveFleet.Tests/Repositories/BoardRepositoryTests.cs`, `tests/WeaveFleet.Tests/Endpoints/BoardEndpointTests.cs`
  **Acceptance**: All operations tested including edge cases.

- [x] 7. **Frontend API Client**
  **What**: Typed API client functions for all board endpoints.
  **Files**: `client/src/lib/board-api.ts`
  **Acceptance**: All endpoints have typed request/response functions.

- [x] 8. **Frontend Store Rewrite**
  **What**: Replace mock `board.ts` store with API-backed store. Remove `board-mock-data.ts`. Optimistic updates for drag operations.
  **Files**: `client/src/stores/board.ts`
  **Acceptance**: Store fetches real data, persists changes via API.

- [x] 9. **Frontend Kanban UI**
  **What**: Update kanban components for user-defined lanes, drag-and-drop card movement, inline card creation, lane management (add/rename/delete/reorder), inbox lane indicator.
  **Files**: `client/src/components/board/KanbanBoard.vue`, `client/src/components/board/KanbanColumn.vue`, `client/src/components/board/KanbanCard.vue`
  **Acceptance**: Full kanban flow works end-to-end.

- [x] 10. **Frontend Tests**
  **What**: Store tests with mocked API. Component tests for card creation and lane management.
  **Files**: `client/src/stores/__tests__/board.test.ts`
  **Acceptance**: Tests pass with mocked API responses.

## Verification
- [x] `dotnet build src/WeaveFleet.Api` succeeds with no warnings
- [x] `dotnet test` — all tests pass
- [x] Frontend builds without errors
- [x] Frontend tests pass
- [x] Manual smoke test: create board → add lanes → mark inbox → create cards → drag → reorder → archive → rename → delete lane (empty) → persists across restart
