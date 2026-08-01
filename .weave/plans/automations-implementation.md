# Automations Implementation

## TL;DR
Add a full automations system to Weave Fleet, borrowing Foundry's design but adapted to Fleet's Dapper/SQLite stack. Automations create sessions via `SessionOrchestrator`, support schedule (cron) and event triggers, optional per-automation model/agent override, and optional workspace scoping.

## Context
Foundry has a mature automations system (event-sourced `Automation` aggregate, scheduler, event dispatcher, execution service, 9 REST endpoints, Vue frontend). Weave Fleet needs the same capability but adapted to its patterns:

- **No event sourcing** — plain entities + Dapper repositories (like `Session`, `SmartLink`, etc.)
- **Session instead of InboxItem** — automations create sessions via `SessionOrchestrator`
- **SQLite + DbUp migrations** — numbered `.sql` files in `src/WeaveFleet.Infrastructure/Migrations/`
- **Existing domain events** — `SessionStarted`, `SessionStopped`, `SessionArchived`, `SessionIdled`, `SessionDeleted`, `TurnStarted`, `TurnEnded`, `MessageCreated`, etc.
- **Existing event bus** — `IEventPublisher` + `IEventBroadcaster` + in-process event store
- **Existing BackgroundService pattern** — `OutboxDispatchBackgroundService`, `OutboxCleanupBackgroundService`
- **Repository pattern** — `IDbConnectionFactory` + raw ADO.NET with `QueryAsync`/`ExecuteAsync` helpers (see `SmartLinkRepository`)
- **Endpoint pattern** — static `Map*Endpoints` extension methods, registered in `EndpointExtensions.cs`
- **Client routing** — TanStack Router file-based routes, existing `/pipelines` route + mock UI

Key Foundry reference files:
- `C:\source\foundry\src\Foundry.Engine\Domain\Automation.cs` — aggregate (adapt to plain entity)
- `C:\source\foundry\src\Foundry.Api\Contracts\AutomationContracts.cs` — API contracts
- `C:\source\foundry\src\Foundry.Api\Endpoints\AutomationEndpoints.cs` — 9 endpoints
- `C:\source\foundry\src\Foundry.Api\Services\AutomationExecutionService.cs` — creates InboxItem + dispatches
- `C:\source\foundry\src\Foundry.Api\Services\AutomationSchedulerService.cs` — 30-sec polling + Cronos
- `C:\source\foundry\src\Foundry.Api\Services\AutomationEventDispatcherService.cs` — event-triggered automations

## Scope
- In scope:
  - Domain entity, repository, service, API endpoints for automation CRUD
  - Schedule triggers (Cronos cron) and event triggers (domain event matching)
  - Execution engine that creates sessions via `SessionOrchestrator`
  - Per-automation model/agent override with fallback to workspace/system defaults
  - Optional `WorkspaceId` (nullable for global automations)
  - `SourceReference` on `Session` for feedback-loop guard
  - Deduplication ledger for event-triggered automations
  - Frontend: rename route, add to rail, composable, form, wire to real API
  - Integration and unit tests
- Out of scope:
  - Event context resolvers (Foundry's `IEventContextResolver` pattern) — defer to future
  - Target routing ("source" vs "new" from Foundry) — all automations create new sessions
  - Plugin-generated events — only built-in domain events
  - Automation history/run log UI — defer to future
- Constraints / assumptions:
  - IDs are `string` (ULID-style), matching Fleet convention, not `Guid`
  - Automations table uses snake_case columns matching existing schema convention
  - Cronos NuGet package needed (add to `Directory.Packages.props`)
  - The in-process event bus (`IEventPublisher`) is the subscription mechanism for event triggers

## Objectives
- Users can create, edit, enable/disable, delete, and manually run automations
- Schedule-triggered automations fire on cron schedule
- Event-triggered automations fire on matching domain events
- Automations create real sessions with the correct harness/model configuration
- Feedback loops are prevented via `source_reference` on sessions
- Frontend provides full CRUD UI accessible from the icon rail

## Dependencies and Order
1. **Phase 1 (Backend Foundation)** must complete first — entity, migration, repository, service, endpoints.
2. **Phase 2 (Execution Engine)** depends on Phase 1 — needs the entity/repo to load automations.
3. **Phase 2 also requires** adding `source_reference` to `Session` entity (migration + entity change).
4. **Phase 3 (Frontend)** can start after Phase 1 endpoints exist (CRUD works), but full integration needs Phase 2.
5. **Phase 4 (Testing)** runs alongside and after Phases 2-3.

## Tasks

### Phase 1: Backend Foundation

- [x] 1. **Add Cronos NuGet package**
  - **What**: Add `Cronos` package reference to `Directory.Packages.props` and reference it in `WeaveFleet.Infrastructure.csproj`.
  - **Files**: `Directory.Packages.props`, `src/WeaveFleet.Infrastructure/WeaveFleet.Infrastructure.csproj`
  - **Depends on**: None
  - **Acceptance**:
    - `Cronos` appears in `Directory.Packages.props` with a pinned version
    - `WeaveFleet.Infrastructure.csproj` has a `<PackageReference Include="Cronos" />` entry
    - `dotnet restore` succeeds

- [x] 2. **Create `Automation` entity**
  - **What**: Plain entity class (not event-sourced). Properties: `Id` (string), `Name`, `Prompt`, `TriggerType` (string: "schedule"|"event"), `TriggerConfig` (string — cron expression or JSON event config), `MaxConcurrentRuns` (int), `MaxRunsPerHour` (int), `TimeoutMinutes` (int), `IsEnabled` (bool), `IsDeleted` (bool), `WorkspaceId` (string?, nullable for global), `Model` (string?, provider:model format), `Agent` (string?), `CreatedAt` (string, ISO-8601), `UpdatedAt` (string?). Follow the flat property style of `Session.cs` — no value objects, just simple properties.
  - **Files**: `src/WeaveFleet.Domain/Entities/Automation.cs`
  - **Depends on**: None
  - **Acceptance**:
    - Entity compiles with all listed properties
    - Uses `string` ID matching Fleet convention
    - No event-sourcing machinery

- [x] 3. **Add `source_reference` to `Session` entity**
  - **What**: Add `SourceReference` property (string?, nullable) to `Session.cs`. This identifies the origin of automated sessions (e.g., `"automation:{id}"`) for feedback-loop guard.
  - **Files**: `src/WeaveFleet.Domain/Entities/Session.cs`
  - **Depends on**: None
  - **Acceptance**:
    - `Session.SourceReference` is a nullable string property
    - No breaking changes to existing code

- [x] 4. **Database migration: automations table**
  - **What**: Create `025_add_automations_table.sql`. Schema:
    ```sql
    CREATE TABLE IF NOT EXISTS automations (
      id TEXT PRIMARY KEY,
      name TEXT NOT NULL,
      prompt TEXT NOT NULL,
      trigger_type TEXT NOT NULL,
      trigger_config TEXT NOT NULL,
      max_concurrent_runs INTEGER NOT NULL DEFAULT 1,
      max_runs_per_hour INTEGER NOT NULL DEFAULT 10,
      timeout_minutes INTEGER NOT NULL DEFAULT 30,
      is_enabled INTEGER NOT NULL DEFAULT 0,
      is_deleted INTEGER NOT NULL DEFAULT 0,
      workspace_id TEXT,
      model TEXT,
      agent TEXT,
      created_at TEXT NOT NULL,
      updated_at TEXT,
      user_id TEXT NOT NULL
    );
    ```
  - **Files**: `src/WeaveFleet.Infrastructure/Migrations/025_add_automations_table.sql`
  - **Depends on**: None
  - **Acceptance**:
    - Migration creates the table with all columns
    - `workspace_id`, `model`, `agent` are nullable
    - Uses snake_case matching existing schema

- [x] 5. **Database migration: source_reference on sessions**
  - **What**: Create `025_add_session_source_reference.sql`: `ALTER TABLE sessions ADD COLUMN source_reference TEXT;`
  - **Files**: `src/WeaveFleet.Infrastructure/Migrations/025_add_session_source_reference.sql`
  - **Depends on**: Task 3
  - **Acceptance**:
    - Migration adds nullable `source_reference` column to `sessions`

- [x] 6. **Database migration: automation event ledger**
  - **What**: Create `025_add_automation_event_ledger.sql`:
    ```sql
    CREATE TABLE IF NOT EXISTS automation_event_ledger (
      automation_id TEXT NOT NULL,
      source_event_id TEXT NOT NULL,
      processed_at TEXT NOT NULL,
      PRIMARY KEY (automation_id, source_event_id)
    );
    ```
  - **Files**: `src/WeaveFleet.Infrastructure/Migrations/025_add_automation_event_ledger.sql`
  - **Depends on**: None
  - **Acceptance**:
    - Composite PK prevents duplicate processing

- [x] 7. **Create `IAutomationRepository` interface**
  - **What**: Repository interface with methods: `InsertAsync`, `UpdateAsync`, `GetByIdAsync`, `ListAsync` (with optional `workspaceId` filter), `ListEnabledByTriggerTypeAsync(string triggerType)`, `DeleteAsync` (soft-delete sets `is_deleted = 1`), `SetEnabledAsync(string id, bool enabled)`.
  - **Files**: `src/WeaveFleet.Domain/Repositories/IAutomationRepository.cs`
  - **Depends on**: Task 2
  - **Acceptance**:
    - Interface matches the entity properties
    - Supports filtering by workspace and trigger type

- [x] 8. **Create `AutomationRepository` implementation**
  - **What**: Dapper-style repository using `IDbConnectionFactory` + raw ADO.NET helpers, following `SmartLinkRepository` pattern. Implements `IAutomationRepository`. All queries filter `is_deleted = 0` by default. Include `user_id` filtering via `IUserContext`.
  - **Files**: `src/WeaveFleet.Infrastructure/Data/Repositories/AutomationRepository.cs`
  - **Depends on**: Tasks 4, 7
  - **Acceptance**:
    - All interface methods implemented
    - Uses `IDbConnectionFactory` pattern
    - Soft-delete (never physically deletes)
    - User-scoped queries via `IUserContext`

- [x] 9. **Create `IAutomationEventLedgerRepository` interface + implementation**
  - **What**: Interface with `IsProcessedAsync(string automationId, string sourceEventId)` and `RecordAsync(string automationId, string sourceEventId)`. Implementation uses `IDbConnectionFactory`, INSERT OR IGNORE for idempotency.
  - **Files**: `src/WeaveFleet.Domain/Repositories/IAutomationEventLedgerRepository.cs`, `src/WeaveFleet.Infrastructure/Data/Repositories/AutomationEventLedgerRepository.cs`
  - **Depends on**: Task 6
  - **Acceptance**:
    - Duplicate inserts are silently ignored
    - Lookup returns true/false correctly

- [x] 10. **Create `AutomationService`**
  - **What**: Application service with CRUD methods: `CreateAsync`, `UpdateAsync`, `EnableAsync`, `DisableAsync`, `DeleteAsync`, `GetByIdAsync`, `ListAsync`, `TriggerManuallyAsync`. Generates ULID for new automations. Validates trigger config (cron expressions via Cronos for schedule type). Returns `OperationResult<T>` or throws — follow whichever pattern `SessionService` uses.
  - **Files**: `src/WeaveFleet.Application/Services/AutomationService.cs`
  - **Depends on**: Tasks 2, 7
  - **Acceptance**:
    - Cron validation rejects invalid expressions
    - Enable/disable toggle works
    - Soft-delete marks `is_deleted = true`
    - Manual trigger returns the automation for execution

- [x] 11. **Create API contracts**
  - **What**: Request/response records: `CreateAutomationRequest` (name, prompt, triggerType, triggerConfig, maxConcurrentRuns, maxRunsPerHour, timeoutMinutes, workspaceId?, model?, agent?), `UpdateAutomationRequest` (same fields), `AutomationResponse` (all fields + id + isEnabled + createdAt), `AutomationListResponse`. Follow existing contract style in `WeaveFleet.Application.DTOs` or `WeaveFleet.Api`.
  - **Files**: `src/WeaveFleet.Api/Contracts/AutomationContracts.cs`
  - **Depends on**: Task 2
  - **Acceptance**:
    - Contracts include `workspaceId`, `model`, `agent` fields
    - Records are immutable

- [x] 12. **Create `AutomationEndpoints`**
  - **What**: 9 minimal API endpoints under `/api/automations`:
    - `POST /` — create
    - `PUT /{id}` — update
    - `GET /` — list (optional `?workspaceId=` query param)
    - `GET /{id}` — get by ID
    - `DELETE /{id}` — soft-delete
    - `POST /{id}/enable` — enable
    - `POST /{id}/disable` — disable
    - `POST /{id}/run` — manual trigger (returns 202)
    - `GET /event-catalog` — returns available event type names
    Follow Foundry's endpoint structure but use Fleet's `ResultExtensions` pattern for error handling where applicable. Wire into `EndpointExtensions.cs`.
  - **Files**: `src/WeaveFleet.Api/Endpoints/AutomationEndpoints.cs`, `src/WeaveFleet.Api/Endpoints/EndpointExtensions.cs`
  - **Depends on**: Tasks 10, 11
  - **Acceptance**:
    - All 9 endpoints respond correctly
    - Event catalog returns: `SessionStarted`, `SessionIdled`, `SessionStopped`, `SessionDeleted`, `SessionArchived`, `TurnStarted`, `TurnEnded`, `MessageCreated`, `MessageUpdated`, `DelegationCreated`, `DelegationUpdated`, `DelegationCompleted`
    - `MapAutomationEndpoints()` is called in `EndpointExtensions.cs`

- [x] 13. **Register DI services**
  - **What**: Register `IAutomationRepository` → `AutomationRepository`, `IAutomationEventLedgerRepository` → `AutomationEventLedgerRepository`, `AutomationService` in `DependencyInjection.cs`. Also register the Phase 2 background services (can be registered now, they'll no-op until implemented).
  - **Files**: `src/WeaveFleet.Infrastructure/DependencyInjection.cs`
  - **Depends on**: Tasks 8, 9, 10
  - **Acceptance**:
    - All services resolve from DI
    - `dotnet build` succeeds

### Phase 2: Execution Engine

- [x] 14. **Create `AutomationExecutionService`**
  - **What**: Adapted from Foundry's version. Instead of creating an InboxItem, creates a Session via `SessionOrchestrator`. Key logic:
    - Resolve harness/model: automation `Model` → workspace default → system default
    - Resolve agent: automation `Agent` → workspace default → none
    - Replace template variables: `{{name}}` → automation name, `{{timestamp}}` → UTC ISO-8601
    - Set `SourceReference = "automation:{automationId}"` on the created session
    - If event-triggered, prepend event context to the prompt: `[Context]\n{eventType}: {eventSummary}\n\n[Instruction]\n{expandedPrompt}`
    - Call `SessionOrchestrator` to create + start the session, then send the prompt
    - Log execution with structured logging
  - **Files**: `src/WeaveFleet.Application/Services/AutomationExecutionService.cs`
  - **Depends on**: Tasks 2, 3, 10
  - **Acceptance**:
    - Creates a session with correct `SourceReference`
    - Template variables are replaced
    - Model/agent override chain works: automation → workspace → system default
    - Errors are caught and logged, not thrown to callers

- [x] 15. **Update `SessionOrchestrator` for automation sessions**
  - **What**: Ensure `SessionOrchestrator.CreateSessionAsync` (or equivalent) can accept a `SourceReference` parameter and persist it. May need a new overload or parameter. Also ensure the `source_reference` column is read/written in `SessionRepository`.
  - **Files**: `src/WeaveFleet.Application/Services/SessionOrchestrator.cs`, `src/WeaveFleet.Infrastructure/Data/Repositories/SessionRepository.cs`
  - **Depends on**: Tasks 3, 5
  - **Acceptance**:
    - Sessions created with a `SourceReference` persist it to DB
    - Sessions created without it remain null (backward compatible)

- [x] 16. **Create `AutomationSchedulerService`**
  - **What**: `BackgroundService` that polls every 30 seconds. Loads enabled automations with `trigger_type = 'schedule'` via `IAutomationRepository`. For each, parses cron with `CronExpression.Parse()`, checks if next occurrence falls within the polling window. Fires `AutomationExecutionService.ExecuteAsync` on a background task. Tracks running automations in a `ConcurrentDictionary` to enforce `MaxConcurrentRuns`. Adapted from Foundry's `AutomationSchedulerService`.
  - **Files**: `src/WeaveFleet.Infrastructure/Services/AutomationSchedulerService.cs`
  - **Depends on**: Tasks 1, 8, 14
  - **Acceptance**:
    - Polls on 30-second interval
    - Correctly evaluates Cronos cron expressions
    - Skips automations that are already at max concurrent runs
    - Graceful shutdown on cancellation

- [x] 17. **Create `EventTriggerMatcher`**
  - **What**: Given a domain event type name (e.g., `"SessionStarted"`), queries enabled automations with `trigger_type = 'event'` whose `trigger_config` matches the event type. `trigger_config` for event automations is a JSON object: `{"eventType": "SessionStarted"}`. The matcher deserializes and compares.
  - **Files**: `src/WeaveFleet.Application/Services/EventTriggerMatcher.cs`
  - **Depends on**: Task 8
  - **Acceptance**:
    - Returns matching automation IDs for a given event type
    - Returns empty list for non-matching events
    - Handles malformed `trigger_config` gracefully (log + skip)

- [x] 18. **Create `AutomationEventDispatcherService`**
  - **What**: `BackgroundService` that subscribes to the in-process event bus (or uses a `Channel<DomainEvent>` fed by the event publisher). On each domain event: uses `EventTriggerMatcher` to find matching automations, checks `IAutomationEventLedgerRepository` for deduplication, records ledger entry, fires `AutomationExecutionService.ExecuteAsync`. Feedback-loop guard: if the event's session has `source_reference` starting with `"automation:"`, skip to prevent infinite loops.
  - **Files**: `src/WeaveFleet.Infrastructure/Services/AutomationEventDispatcherService.cs`
  - **Depends on**: Tasks 9, 14, 17
  - **Acceptance**:
    - Processes domain events from the bus
    - Deduplicates via ledger
    - Prevents feedback loops (automation-created sessions don't re-trigger automations)
    - Errors per-event don't crash the service

- [x] 19. **Wire automation event channel into event publisher**
  - **What**: The event dispatcher needs to receive domain events. Options: (a) add a `Channel<DomainEvent>` that `IEventPublisher` writes to alongside its existing targets, or (b) create a projection that feeds the automation channel. Evaluate the existing `InProcessEventPublisher` / `InProcessFanOutService` to find the cleanest integration point. The dispatcher should receive all durable domain events.
  - **Files**: Depends on investigation — likely `src/WeaveFleet.Infrastructure/EventBus/InProcessEventPublisher.cs` or a new projection
  - **Depends on**: Task 18
  - **Acceptance**:
    - Domain events flow to `AutomationEventDispatcherService`
    - No disruption to existing event consumers

- [x] 20. **Register Phase 2 background services**
  - **What**: Add `AutomationSchedulerService` and `AutomationEventDispatcherService` as hosted services in DI. Register `AutomationExecutionService` and `EventTriggerMatcher` as scoped/transient services.
  - **Files**: `src/WeaveFleet.Infrastructure/DependencyInjection.cs`
  - **Depends on**: Tasks 14, 16, 17, 18
  - **Acceptance**:
    - Both background services start on app launch
    - `dotnet build` and `dotnet run` succeed

### Phase 3: Frontend

- [x] 21. **Rename route from `/pipelines` to `/automations`**
  - **What**: Rename `client/src/routes/pipelines.tsx` to `client/src/routes/automations.tsx`. Update the route definition to `createFileRoute("/automations")`. The route tree will auto-regenerate. Rename `PipelinesPage.vue` to `AutomationsPage.vue`.
  - **Files**: `client/src/routes/pipelines.tsx` → `client/src/routes/automations.tsx`, `client/src/components/pages/PipelinesPage.vue` → `client/src/components/pages/AutomationsPage.vue`
  - **Depends on**: None (can start independently)
  - **Acceptance**:
    - `/automations` route works
    - `/pipelines` no longer exists
    - Route tree regenerates cleanly

- [x] 22. **Add automations to IconRail**
  - **What**: Add an "Automations" item to `ALL_TOP_ITEMS` (or `bottomItems`) in `IconRail.vue`. Use the `Zap` icon from lucide-vue-next. Route to `/automations`.
  - **Files**: `client/src/components/layout/IconRail.vue`
  - **Depends on**: Task 21
  - **Acceptance**:
    - Automations icon appears in the rail
    - Clicking navigates to `/automations`
    - Active state highlights correctly

- [x] 23. **Create `use-automations.ts` composable**
  - **What**: Composable that wraps API calls: `fetchAutomations()`, `createAutomation()`, `updateAutomation()`, `deleteAutomation()`, `enableAutomation()`, `disableAutomation()`, `runAutomation()`, `fetchEventCatalog()`. Uses `apiFetch` from `@/lib/api-client`. Returns reactive `automations` ref, `loading`, `error` state. Optionally accepts `workspaceId` filter.
  - **Files**: `client/src/composables/use-automations.ts`
  - **Depends on**: Task 12 (API must exist)
  - **Acceptance**:
    - All CRUD operations work against `/api/automations`
    - Reactive state updates after mutations
    - Error handling wraps API errors

- [x] 24. **Create `AutomationForm.vue`**
  - **What**: Form component for create/edit. Fields: name (text), prompt (textarea), trigger type (select: schedule|event), trigger config (cron input for schedule, event type dropdown for event — populated from event catalog endpoint), max concurrent runs (number), max runs per hour (number), timeout minutes (number), workspace (optional select — populated from workspace list), model (optional text/select), agent (optional text). Emits `submit` with form data. Validation: name required, prompt required, cron must be valid format, at least trigger config required.
  - **Files**: `client/src/components/automations/AutomationForm.vue`
  - **Depends on**: Task 23
  - **Acceptance**:
    - Form renders all fields
    - Trigger config input changes based on trigger type selection
    - Event catalog loads from API
    - Validation prevents invalid submissions

- [x] 25. **Wire `AutomationsPage.vue` to real API**
  - **What**: Replace mock data in `AutomationsPage.vue` with `useAutomations()` composable. Wire button handlers to real API calls. Add a dialog/panel for `AutomationForm.vue` (create + edit modes). Add confirmation for delete. Show loading and error states.
  - **Files**: `client/src/components/pages/AutomationsPage.vue`
  - **Depends on**: Tasks 23, 24
  - **Acceptance**:
    - Page loads automations from API
    - Create, edit, delete, enable/disable, manual run all work
    - Empty state shown when no automations exist
    - Loading spinner during fetch

- [x] 26. **Update `AutomationCard.vue` for new fields**
  - **What**: Add display for workspace name (if set), model override, agent override. Update the `Automation` interface to match API response shape (add `workspaceId`, `model`, `agent`, `createdAt`). Move the interface to a shared types file or the composable.
  - **Files**: `client/src/components/automations/AutomationCard.vue`, `client/src/composables/use-automations.ts` (or `client/src/lib/api-types.ts`)
  - **Depends on**: Tasks 23, 25
  - **Acceptance**:
    - Card displays all fields from API
    - Type definition matches API contract

### Phase 4: Testing & Polish

- [x] 27. **Integration tests for automation API endpoints**
  - **What**: Test all 9 endpoints: create, update, get, list, delete, enable, disable, run, event-catalog. Use the existing integration test pattern in `tests/WeaveFleet.IntegrationTests/`. Verify response shapes and status codes.
  - **Files**: `tests/WeaveFleet.IntegrationTests/Automations/AutomationEndpointTests.cs`
  - **Depends on**: Task 12
  - **Acceptance**:
    - All 9 endpoints have at least one happy-path test
    - Error cases tested: not found, already enabled, already deleted
    - Tests pass with `dotnet test`

- [x] 28. **Unit tests for scheduler service**
  - **What**: Test cron evaluation logic, concurrent run tracking, graceful shutdown. Mock `IAutomationRepository` and `AutomationExecutionService`. Verify that automations fire at correct times and skip when at max concurrent.
  - **Files**: `tests/WeaveFleet.Application.Tests/Services/AutomationSchedulerServiceTests.cs` or `tests/WeaveFleet.Infrastructure.Tests/Services/AutomationSchedulerServiceTests.cs`
  - **Depends on**: Task 16
  - **Acceptance**:
    - Tests verify cron timing logic
    - Tests verify concurrent run limit enforcement
    - Tests verify graceful shutdown

- [x] 29. **Unit tests for event dispatcher + trigger matcher**
  - **What**: Test event matching logic, deduplication, feedback-loop guard. Mock repositories and execution service.
  - **Files**: `tests/WeaveFleet.Application.Tests/Services/EventTriggerMatcherTests.cs`, `tests/WeaveFleet.Infrastructure.Tests/Services/AutomationEventDispatcherServiceTests.cs`
  - **Depends on**: Tasks 17, 18
  - **Acceptance**:
    - Matching returns correct automations for event types
    - Deduplication prevents double-processing
    - Feedback-loop guard skips automation-sourced events

- [x] 30. **E2E test for automation CRUD**
  - **What**: Playwright test: navigate to `/automations`, create an automation, verify it appears, edit it, enable/disable, delete. Follow existing E2E patterns in `tests/WeaveFleet.E2E/`.
  - **Files**: `tests/WeaveFleet.E2E/Automations/AutomationCrudTests.cs`
  - **Depends on**: Tasks 25, 27
  - **Acceptance**:
    - Test creates and verifies an automation through the UI
    - Test passes with `dotnet test tests/WeaveFleet.E2E --filter "FullyQualifiedName~AutomationCrudTests"`

## Verification
1. **Build**: `dotnet build src/WeaveFleet.Api` — no errors or warnings
2. **Backend tests**: `dotnet test tests/WeaveFleet.IntegrationTests --filter "FullyQualifiedName~Automation"` — all pass
3. **Unit tests**: `dotnet test tests/WeaveFleet.Application.Tests tests/WeaveFleet.Infrastructure.Tests --filter "FullyQualifiedName~Automation"` — all pass
4. **Frontend build**: `cd client && bun install && bun run build` — no errors
5. **E2E**: `cd client && bun run build && dotnet test tests/WeaveFleet.E2E --filter "FullyQualifiedName~AutomationCrudTests"` — pass
6. **Manual smoke**: Start app, navigate to `/automations`, create a schedule automation with `0 * * * *`, verify it appears enabled, manually run it, verify a session is created
