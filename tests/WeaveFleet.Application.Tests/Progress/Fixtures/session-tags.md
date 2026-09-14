# Session & Automation Tags

## TL;DR
Add a `Tags` field to sessions and a `TargetTags` field to automations, enabling tag-based filtering and automation targeting. Display and manage tags in the session header.

## Context
- Storage: SQLite with embedded SQL migrations (`src/WeaveFleet.Infrastructure/Migrations/`). Highest existing migration prefix is `025`.
- Tags stored as JSON array text column (e.g. `["github","review"]`).
- Session entity: `src/WeaveFleet.Domain/Entities/Session.cs`
- Automation entity: `src/WeaveFleet.Domain/Entities/Automation.cs`
- Repositories: `src/WeaveFleet.Infrastructure/Data/Repositories/SessionRepository.cs`, `AutomationRepository.cs`
- API contracts: `src/WeaveFleet.Api/Endpoints/SessionEndpoints.cs` (CreateSessionApiRequest line 805), `src/WeaveFleet.Api/Contracts/AutomationContracts.cs`
- Application: `src/WeaveFleet.Application/Services/SessionOrchestrator.cs` (CreateSessionRequest line 1909), `AutomationExecutionService.cs`
- DTOs: `src/WeaveFleet.Application/DTOs/SessionDtos.cs`
- Frontend header: `client/src/components/session/SessionDetailHeader.vue`

## Scope
- In scope: tags on sessions (create/update/list-filter), target_tags on automations (create/update, matching logic), UI display and management of session tags
- Out of scope: tag autocomplete/suggestions, bulk tag operations
- Constraints: SQLite — no array columns; store as JSON text. Tags are case-insensitive strings, max 50 chars each, max 20 per entity.

## Objectives
- Sessions carry a `List<string>` of tags, settable at creation and updatable
- Automations carry `TargetTags` — only sessions with overlapping tags are targeted
- Session list API supports `?tags=foo,bar` filter (any-match)
- Tags render as pill badges in the session detail header with inline add/remove

## Dependencies and Order
1. Migration first (schema must exist before repo/domain changes)
2. Domain entities next (no dependency on infra beyond migration)
3. Repository read/write (depends on domain + migration)
4. Application layer (depends on repo)
5. API layer (depends on application)
6. Frontend (depends on API)

## Tasks

- [x] 1. Add SQLite migration for tags columns
  - **What**: Create `026_add_tags_columns.sql` adding `tags TEXT` to `sessions` table and `target_tags TEXT` to `automations` table (nullable, default NULL).
  - **Files**: `src/WeaveFleet.Infrastructure/Migrations/026_add_tags_columns.sql`
  - **Depends on**: None
  - **Acceptance**:
    - Migration applies cleanly on existing DB
    - Both columns accept NULL and JSON array strings

- [x] 2. Add Tags property to Session entity
  - **What**: Add `public List<string> Tags { get; set; } = [];` to Session.
  - **Files**: `src/WeaveFleet.Domain/Entities/Session.cs`
  - **Depends on**: None
  - **Acceptance**:
    - Property exists, defaults to empty list

- [x] 3. Add TargetTags property to Automation entity
  - **What**: Add `public List<string> TargetTags { get; set; } = [];` to Automation.
  - **Files**: `src/WeaveFleet.Domain/Entities/Automation.cs`
  - **Depends on**: None
  - **Acceptance**:
    - Property exists, defaults to empty list

- [x] 4. Update SessionRepository to read/write tags
  - **What**: Serialize `Tags` as JSON when inserting/updating; deserialize on read. Add optional `tags` filter parameter to list queries (WHERE clause using JSON or LIKE matching).
  - **Files**: `src/WeaveFleet.Infrastructure/Data/Repositories/SessionRepository.cs`
  - **Depends on**: Tasks 1, 2
  - **Acceptance**:
    - Tags round-trip through persistence
    - List query with tags filter returns only matching sessions

- [x] 5. Update AutomationRepository to read/write target_tags
  - **What**: Serialize/deserialize `TargetTags` as JSON text column.
  - **Files**: `src/WeaveFleet.Infrastructure/Data/Repositories/AutomationRepository.cs`
  - **Depends on**: Tasks 1, 3
  - **Acceptance**:
    - TargetTags round-trip through persistence

- [x] 6. Update CreateSessionRequest and orchestrator
  - **What**: Add `List<string>? Tags` to `CreateSessionRequest`. Pass through to entity on creation. Add an `UpdateSessionTagsAsync` method or extend existing update path.
  - **Files**: `src/WeaveFleet.Application/Services/SessionOrchestrator.cs`
  - **Depends on**: Task 4
  - **Acceptance**:
    - Tags set at creation time are persisted
    - Tags can be updated after creation

- [x] 7. Update AutomationExecutionService tag matching
  - **What**: When an automation fires, filter candidate sessions by checking overlap between `automation.TargetTags` and `session.Tags`. If `TargetTags` is empty, match all (existing behaviour).
  - **Files**: `src/WeaveFleet.Application/Services/AutomationExecutionService.cs`
  - **Depends on**: Tasks 5, 6
  - **Acceptance**:
    - Automation with TargetTags=["review"] only runs on sessions containing "review" tag
    - Automation with empty TargetTags matches all sessions (backward compat)

- [x] 8. Update API contracts and endpoints — Sessions
  - **What**: Add `List<string>? Tags` to `CreateSessionApiRequest`. Add `tags` query param to list endpoint. Include `Tags` in session response DTOs (`SessionListResponse`, `SessionFleetInfo`).
  - **Files**: `src/WeaveFleet.Api/Endpoints/SessionEndpoints.cs`, `src/WeaveFleet.Application/DTOs/SessionDtos.cs`
  - **Depends on**: Task 6
  - **Acceptance**:
    - POST /sessions with tags persists them
    - GET /sessions?tags=foo returns filtered results
    - Response includes tags array

- [x] 9. Update API contracts and endpoints — Automations
  - **What**: Add `List<string>? TargetTags` to `CreateAutomationRequest`, `UpdateAutomationRequest`, and `AutomationResponse`.
  - **Files**: `src/WeaveFleet.Api/Contracts/AutomationContracts.cs`
  - **Depends on**: Task 5
  - **Acceptance**:
    - TargetTags accepted on create/update
    - TargetTags returned in response

- [x] 10. Add PATCH /sessions/{id}/tags endpoint
  - **What**: Accept `{ tags: string[] }` body, replace session tags entirely. Return updated session.
  - **Files**: `src/WeaveFleet.Api/Endpoints/SessionEndpoints.cs`
  - **Depends on**: Task 8
  - **Acceptance**:
    - Tags can be replaced via PATCH
    - Returns updated session

- [x] 11. Update frontend API types
  - **What**: Add `tags?: string[]` to session types and `targetTags?: string[]` to automation types in the client API layer.
  - **Files**: `client/src/api/` (type definitions)
  - **Depends on**: Tasks 8, 9
  - **Acceptance**:
    - TypeScript types include new fields

- [x] 12. Display and manage tags in SessionDetailHeader
  - **What**: Accept `tags` prop. Render as a row of `<Badge>` pills below the project/harness row. Each badge has an "x" button to remove. Include a "+" button or inline input to add a new tag. On change, call `PATCH /sessions/{id}/tags` with the updated array.
  - **Files**: `client/src/components/session/SessionDetailHeader.vue`
  - **Depends on**: Tasks 10, 11
  - **Acceptance**:
    - Tags render as badges when present
    - Users can remove a tag by clicking "x"
    - Users can add a tag via inline input
    - Changes persist immediately via API
    - No visual change when tags are empty/undefined

- [x] 13. Write integration tests
  - **What**: Test tag persistence round-trip, list filtering by tags, automation target matching.
  - **Files**: `tests/WeaveFleet.IntegrationTests/` (new test class or extend existing)
  - **Depends on**: Tasks 8, 9, 10
  - **Acceptance**:
    - All tests pass
    - Coverage for: create with tags, update tags, filter by tags, automation matching

## Verification
```bash
# Backend builds
dotnet build src/WeaveFleet.Api -c Release

# Tests pass
dotnet test tests/ -c Debug

# Frontend builds
cd client && bun install && bunx vue-tsc --noEmit && bun run build
```
