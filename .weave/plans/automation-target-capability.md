# Automation Target Capability

## TL;DR
Add a configurable "target" to automations that controls where the automation runs — new session (default), most recent session, or a session matched by tags — instead of always creating a new session.

## Context
- Automations always create a new session today (`AutomationExecutionService.ExecuteAsync` calls `sessionOrchestrator.CreateSessionAsync`)
- Domain: `src/WeaveFleet.Domain/Entities/Automation.cs`
- API contracts: `src/WeaveFleet.Api/Contracts/AutomationContracts.cs`
- Execution: `src/WeaveFleet.Application/Services/AutomationExecutionService.cs`
- Frontend types: `client/src/composables/use-automations.ts`
- Frontend form: `client/src/components/automations/AutomationForm.vue`
- DB migrations: `src/WeaveFleet.Infrastructure/Migrations/` (next number: 027)
- The entity already has `TargetTags` (used for tag-based session matching in the future)

## Scope
- In scope:
  - New `TargetType` field on automations: `new_session` (default), `most_recent_session`, `tagged_session`
  - `TargetConfig` JSON field for target-specific config (e.g., tag filter for `tagged_session`)
  - Execution service routing based on target type
  - DB migration adding the column
  - API contract updates
  - Frontend form UI to select target type and configure it
- Out of scope:
  - "Specific session by ID" target (future)
  - Retry/fallback logic if target session not found (fail gracefully with log)
  - Changes to the scheduler or event dispatcher (they already delegate to ExecutionService)
- Constraints / assumptions:
  - `tagged_session` reuses the existing `TargetTags` field for the tag filter
  - `most_recent_session` picks the most recently active session for the user
  - If no matching session is found for non-`new_session` targets, fall back to creating a new session (with a warning log)

## Objectives
- Users can configure automation target via the UI
- Execution service routes to existing sessions when configured
- Backward compatible: existing automations default to `new_session`

## Dependencies and Order
1. Domain model + migration first (everything else depends on the new fields)
2. API contracts (depends on domain)
3. Repository/persistence layer (depends on migration + domain)
4. Execution service routing (depends on domain + a way to query sessions)
5. Frontend (depends on API contracts being stable)

## Tasks

- [x] 1. Add `TargetType` to domain entity
  - **What**: Add `public string TargetType { get; set; } = "new_session";` to `Automation.cs`.
  - **Files**: `src/WeaveFleet.Domain/Entities/Automation.cs`
  - **Depends on**: None
  - **Acceptance**:
    - Field exists with default `"new_session"`
    - No compile errors

- [x] 2. Database migration
  - **What**: Create migration `027_add_automation_target_type.sql` adding `target_type TEXT NOT NULL DEFAULT 'new_session'` to the `automations` table.
  - **Files**: `src/WeaveFleet.Infrastructure/Migrations/027_add_automation_target_type.sql`
  - **Depends on**: None
  - **Acceptance**:
    - Column added with default value
    - Existing rows get `new_session` automatically

- [x] 3. Update API contracts
  - **What**: Add `string? TargetType = null` to `CreateAutomationRequest` and `UpdateAutomationRequest`. Add `string TargetType` to `AutomationResponse`.
  - **Files**: `src/WeaveFleet.Api/Contracts/AutomationContracts.cs`
  - **Depends on**: Task 1
  - **Acceptance**:
    - Contracts compile
    - `TargetType` is nullable on requests (defaults handled server-side), required on response

- [x] 4. Update persistence layer (repository mapping)
  - **What**: Ensure the automation repository reads/writes `target_type` column. Search for the Dapper/SQL mapping code and add the column.
  - **Files**: Grep for `INSERT INTO automations` and `SELECT.*FROM automations` in `*.cs` to find the repository file(s).
  - **Depends on**: Tasks 1, 2
  - **Acceptance**:
    - `TargetType` is persisted on create/update
    - `TargetType` is populated on read

- [x] 5. Update endpoint mapping
  - **What**: Map `TargetType` from request to entity on create/update, and from entity to response. Find in `AutomationEndpoints.cs`.
  - **Files**: `src/WeaveFleet.Api/Endpoints/AutomationEndpoints.cs`
  - **Depends on**: Tasks 1, 3
  - **Acceptance**:
    - Round-trip: create with `targetType: "most_recent_session"` → GET returns same value

- [x] 6. Execution service: route by target type
  - **What**: In `AutomationExecutionService.ExecuteAsync`, branch on `automation.TargetType`:
    - `"new_session"`: current behavior (create session)
    - `"most_recent_session"`: query for the user's most recent session and send the prompt to it
    - `"tagged_session"`: query for sessions matching `automation.TargetTags` and pick the most recent
    - Fallback: if no session found, create new + log warning
  - **Files**: `src/WeaveFleet.Application/Services/AutomationExecutionService.cs`
  - **Depends on**: Tasks 1, 4. Also depends on the session query mechanism — check if `SessionOrchestrator` or a repository exposes session listing.
  - **Acceptance**:
    - `new_session` behavior unchanged
    - `most_recent_session` sends prompt to existing session
    - `tagged_session` filters by tags
    - Missing target falls back to new session with warning log

- [x] 7. Frontend: update TypeScript types
  - **What**: Add `targetType?: string` to `Automation` interface and `CreateAutomationRequest`.
  - **Files**: `client/src/composables/use-automations.ts`
  - **Depends on**: Task 3
  - **Acceptance**:
    - Types include `targetType`
    - No TS errors

- [x] 8. Frontend: add target selector to form
  - **What**: Add a `<Select>` for target type in `AutomationForm.vue` with options: "New Session", "Most Recent Session", "Tagged Session". When "Tagged Session" is selected, show the existing tags input. Wire `targetType` into the submit payload.
  - **Files**: `client/src/components/automations/AutomationForm.vue`
  - **Depends on**: Task 7
  - **Acceptance**:
    - Target selector renders with 3 options
    - Default is "New Session"
    - "Tagged Session" shows tag input
    - Value is included in create/update request

- [x] 9. Frontend: display target in detail panel
  - **What**: Show the configured target type in `AutomationDetailPanel.vue`.
  - **Files**: `client/src/components/automations/AutomationDetailPanel.vue`
  - **Depends on**: Task 7
  - **Acceptance**:
    - Detail panel displays "Target: New Session" (or whichever is configured)

- [x] 10. Verify end-to-end
  - **Depends on**: All prior tasks
  - **Acceptance**:
    - `bun run build` succeeds in `client/`
    - `dotnet build` succeeds for the solution
    - Existing automation tests still pass
    - Manual: create automation with "most recent session" target → triggers correctly

## Verification
```bash
cd client && bun run build
cd .. && dotnet build
dotnet test
```
All must pass. Additionally, manually test creating an automation with each target type via the UI.
