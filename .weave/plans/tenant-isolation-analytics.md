# Tenant Isolation: Analytics Security Fix

## TL;DR
> **Summary**: The analytics subsystem (token_events, session_snapshots, daily_rollups) stores data without `user_id` and queries without tenant scoping, allowing any authenticated user to see all users' analytics data. This plan adds `user_id` to the analytics schema, threads it through the write path, and scopes all read queries by user — plus adds comprehensive tenant-isolation tests.
> **Estimated Effort**: Medium

## Context

### Original Request
Fix tenant isolation in analytics and add missing tenant-isolation tests. This is a security fix.

### Key Findings

**The vulnerability**: The analytics database is a separate SQLite DB with three tables (`token_events`, `session_snapshots`, `daily_rollups`). None of these tables have a `user_id` column. The `AnalyticsRepository` (read path) does not take `IUserContext` and queries are scoped only by date/project — not by user. In a multi-user deployment, **User A can see User B's token usage, costs, session analytics, and export all raw events**.

**Write path gap**: The data pipeline flows as:
1. `OpenCodeHarnessInstance` / `ClaudeCodeHarnessInstance` → `IAnalyticsCollector.AcceptTokenEvent(TokenEventData)` — `TokenEventData` has no `UserId` field
2. `SessionOrchestrator` → `IAnalyticsCollector.AcceptSessionSnapshot(SessionSnapshotData)` — `SessionSnapshotData` has no `UserId` field
3. `AnalyticsWriterService` → writes to analytics DB — no `user_id` column exists

Both harness instances and the orchestrator have access to the user ID (`_ownerUserId` / `userContext.UserId`) at the point of event creation, but it is not propagated.

**Read path gap**: `AnalyticsRepository` is constructed with only `IAnalyticsDbConnectionFactory` — no `IUserContext`. All 5 query methods (`GetSummaryAsync`, `GetDailyAsync`, `GetSessionsAsync`, `GetModelsAsync`, `ExportTokenEventsAsync`) have no user filtering whatsoever.

**Rollup gap**: `AnalyticsRollupService` computes `daily_rollups` from `token_events` without user scoping. After adding `user_id`, rollups must also be partitioned by user.

**Existing tenant isolation pattern** (from `DapperSessionRepository` and others):
- Inject `IUserContext` into repository constructor
- Add `AND user_id = @UserId` to every SQL query
- Pass `new { ..., UserId = userContext.UserId }` as parameter

**Affected endpoints** (all under `/api/analytics`):
- `GET /api/analytics/summary`
- `GET /api/analytics/daily`
- `GET /api/analytics/sessions`
- `GET /api/analytics/models`
- `GET /api/analytics/export`

**Test gaps identified**:
- Zero tenant-isolation tests for analytics (repository or API level)
- No API-level integration tests for analytics endpoints at all
- No API-level tenant-isolation tests for sessions/events endpoints

## Objectives

### Core Objective
Ensure analytics data is scoped per-user at all layers: schema, write path, read path, and rollups — making it impossible for one user to access another user's analytics data.

### Deliverables
- [ ] Analytics schema migration adding `user_id` to all three analytics tables
- [ ] `TokenEventData` and `SessionSnapshotData` extended with `UserId` property
- [ ] Write path (`AnalyticsWriterService`, `AnalyticsCollector`) propagates `user_id`
- [ ] All callers (`OpenCodeHarnessInstance`, `ClaudeCodeHarnessInstance`, `SessionOrchestrator`) supply `user_id`
- [ ] `AnalyticsRepository` scoped by `IUserContext` on all queries
- [ ] `AnalyticsRollupService` partitions rollups by `user_id`
- [ ] Repository-level tenant isolation tests for analytics
- [ ] API-level tenant isolation tests for analytics endpoints
- [ ] API-level tenant isolation tests for sessions/events endpoints

### Definition of Done
- [x] `dotnet test` — all tests pass (unit, integration, infrastructure)
- [x] `dotnet build -c Release` — clean build with no warnings
- [ ] Manual: two-user scenario confirms User A cannot see User B's analytics via API

### Guardrails (Must NOT)
- Must NOT break backwards compatibility for single-user (local) deployments — `user_id` defaults to `"local-user"`
- Must NOT alter the main DB schema — only the analytics DB
- Must NOT change the analytics API contract (request/response shapes) — only filter results
- Must NOT introduce new NuGet dependencies

## TODOs

### Phase 1: Schema & Write Path — Add `user_id` to Analytics

- [x] 1. Add analytics migration `003_add_user_id.sql`
  **What**: Create a new SQL migration that:
  - Adds `user_id TEXT NOT NULL DEFAULT 'local-user'` to `token_events`
  - Adds `user_id TEXT NOT NULL DEFAULT 'local-user'` to `session_snapshots`
  - Adds `user_id TEXT NOT NULL DEFAULT ''` to `daily_rollups` (part of composite PK)
  - Creates index `idx_token_events_user` on `token_events(user_id)`
  - Creates index `idx_session_snapshots_user` on `session_snapshots(user_id)`
  - Updates `daily_rollups` PRIMARY KEY to include `user_id` (requires table recreation since SQLite cannot ALTER PK)
  - The `daily_rollups` recreation: `CREATE TABLE daily_rollups_new (... user_id TEXT NOT NULL DEFAULT '', PRIMARY KEY(date, user_id, project_id, model_id, provider_id))`, copy data, drop old, rename new, recreate index
  **Files**: `src/WeaveFleet.Infrastructure/AnalyticsMigrations/003_add_user_id.sql`
  **Acceptance**: Migration applies cleanly on fresh and existing analytics DBs; existing data gets `user_id = 'local-user'` (or `''` for rollups)

- [x] 2. Add `UserId` to `TokenEventData` and `SessionSnapshotData`
  **What**: Add `string UserId` property to both record types. Since these are positional records, add the property at the end to minimize call-site churn. Update `AnalyticsEvents.cs`.
  **Files**: `src/WeaveFleet.Application/Analytics/AnalyticsEvents.cs`
  **Acceptance**: Both records compile with the new `UserId` field; all callers updated (will break until step 3)

- [x] 3. Thread `UserId` through all analytics event producers
  **What**: Update all call sites that create `TokenEventData` or `SessionSnapshotData` to supply the user ID:
  - `OpenCodeHarnessInstance` — use `_ownerUserId` (already available as field)
  - `ClaudeCodeHarnessInstance` — use equivalent owner user ID field
  - `SessionOrchestrator.CreateSessionAsync` — use `userContext.UserId` (3 snapshot call sites at lines ~193, ~428, ~774)
  **Files**:
  - `src/WeaveFleet.Infrastructure/Harnesses/OpenCode/OpenCodeHarnessInstance.cs`
  - `src/WeaveFleet.Infrastructure/Harnesses/ClaudeCode/ClaudeCodeHarnessInstance.cs`
  - `src/WeaveFleet.Application/Services/SessionOrchestrator.cs`
  **Acceptance**: No compiler errors; `UserId` is populated in all emitted analytics events

- [x] 4. Persist `user_id` in `AnalyticsWriterService`
  **What**: Update the `WriteToAnalyticsDbAsync` method to include `user_id` in both INSERT statements:
  - `token_events` INSERT: add `user_id` column and `@UserId` parameter, sourced from `TokenEventData.UserId`
  - `session_snapshots` INSERT: add `user_id` column and `@UserId` parameter, sourced from `SessionSnapshotData.UserId`
  **Files**: `src/WeaveFleet.Infrastructure/Analytics/AnalyticsWriterService.cs`
  **Acceptance**: Token events and session snapshots written to analytics DB include `user_id` column

- [x] 5. Update `AnalyticsRollupService` to partition by `user_id`
  **What**: Update the rollup SQL in `RunRollupAsync` to:
  - Include `user_id` in the `GROUP BY` clause for per-project rollups
  - Include `user_id` in the fleet-wide summary rollup
  - Include `user_id` in `INSERT INTO daily_rollups` column list
  - Delete/recompute logic must also account for `user_id` in the new composite PK
  **Files**: `src/WeaveFleet.Infrastructure/Analytics/AnalyticsRollupService.cs`
  **Acceptance**: Daily rollups are computed per-user; different users get separate rollup rows

- [x] 6. Update test helpers to supply `UserId`
  **What**: Update `MakeTokenEvent` and `MakeSnapshot` helper methods in existing test files to include the new `UserId` parameter:
  - `AnalyticsWriterServiceTests` — update `MakeTokenEvent()` and `MakeSnapshot()` helpers
  - `AnalyticsCollectorTests` — update `MakeTokenEvent()` and `MakeSnapshot()` helpers
  - `AnalyticsRepositoryTests` — update seed data INSERT statements to include `user_id`
  **Files**:
  - `tests/WeaveFleet.Infrastructure.Tests/Analytics/AnalyticsWriterServiceTests.cs`
  - `tests/WeaveFleet.Infrastructure.Tests/Analytics/AnalyticsCollectorTests.cs`
  - `tests/WeaveFleet.Infrastructure.Tests/Analytics/AnalyticsRepositoryTests.cs`
  **Acceptance**: All existing analytics tests pass with updated data shapes

### Phase 2: Read Path — Scope `AnalyticsRepository` by User

- [x] 7. Add `IUserContext` to `AnalyticsRepository` and scope all queries
  **What**: 
  - Add `IUserContext userContext` parameter to the `AnalyticsRepository` constructor
  - Update `IAnalyticsReader` interface: no change needed (user context is injected, not passed per-call)
  - Update `GetSummaryAsync`: add `AND user_id = @UserId` to the token_events WHERE clause (via `BuildWhereClause` or inline)
  - Update `GetDailyAsync`: add `AND user_id = @UserId` to daily_rollups WHERE clause
  - Update `GetSessionsAsync`: add `AND ss.user_id = @UserId` to session_snapshots WHERE clause (note: this query uses `ss` alias for `session_snapshots`)
  - Update `GetModelsAsync`: add `AND user_id = @UserId` to token_events WHERE clause
  - Update `ExportTokenEventsAsync`: add `AND user_id = @UserId` to token_events WHERE clause
  - Update `BuildWhereClause` helper to always include `user_id = @UserId`
  - Update `BuildDateFilter` and `BuildDateOnlyFilter` to include user_id
  - Pass `UserId = userContext.UserId` in all Dapper parameter objects
  **Files**:
  - `src/WeaveFleet.Infrastructure/Analytics/AnalyticsRepository.cs`
  **Acceptance**: All 5 query methods filter by `user_id`; SQL injection-safe via parameterized queries

- [x] 8. Register `AnalyticsRepository` with `IUserContext` in DI
  **What**: Update the DI registration for `IAnalyticsReader` / `AnalyticsRepository` to include `IUserContext` in the constructor. Find the service registration (likely in `ServiceCollectionExtensions` or `Program.cs`) and ensure `IUserContext` is resolved.
  **Files**: `src/WeaveFleet.Infrastructure/DependencyInjection.cs` (line 137: `services.AddScoped<IAnalyticsReader, AnalyticsRepository>()`)
  **Acceptance**: `AnalyticsRepository` resolves correctly at runtime with both `IAnalyticsDbConnectionFactory` and `IUserContext`. Since it's already registered as scoped (same as `IUserContext`), no lifetime change is needed — just verify the constructor signature matches

### Phase 3: Analytics Tenant Isolation Tests (Repository Level)

- [x] 9. Add tenant-isolation tests to `AnalyticsRepositoryTests`
  **What**: Add a new test class or extend existing `AnalyticsRepositoryTests` with cross-user isolation tests. Pattern: seed data for two users (`user-a`, `user-b`), query as `user-a`, assert only `user-a` data returned. Tests needed:
  - `GetSummaryAsync_ReturnsOnlyCurrentUsersData` — seed events for two users, query as user-a, verify totals match only user-a's events
  - `GetDailyAsync_ReturnsOnlyCurrentUsersDailyRollups` — seed daily_rollups for two users, verify isolation
  - `GetSessionsAsync_ReturnsOnlyCurrentUsersSessions` — seed session_snapshots for two users, verify isolation
  - `GetModelsAsync_ReturnsOnlyCurrentUsersModelData` — seed token_events for two users with different models, verify isolation
  - `ExportTokenEventsAsync_ReturnsOnlyCurrentUsersEvents` — seed events for two users, export as user-a, verify no user-b events
  - `GetSummaryAsync_WithProjectFilter_StillScopedByUser` — ensure project filter doesn't bypass user scoping
  
  Create a new `AnalyticsRepository` instance per user context (like `DapperSessionRepositoryTests` does with `RepositoryOwnershipTestHelper`). Use the existing `AnalyticsTestDbHelper.CreateSharedDbAsync()` for DB setup.
  **Files**: `tests/WeaveFleet.Infrastructure.Tests/Analytics/AnalyticsRepositoryTests.cs`
  **Acceptance**: All new tests pass; demonstrate that user-a cannot see user-b's analytics

### Phase 4: API-Level Analytics Tenant Isolation Tests

- [x] 10. Add API integration test for analytics tenant isolation
  **What**: Create a new test class `AnalyticsEndpointTenantIsolationTests` in `WeaveFleet.Api.Tests`. Use the `ApiWebApplicationFactory` pattern (with `useTestAuthentication: true`). The approach:
  - Set up `ApiWebApplicationFactory` with analytics enabled (`Fleet:AnalyticsEnabled=true`)
  - Seed analytics data for two different users directly into the analytics DB
  - Make HTTP requests as the authenticated test user (who gets `sub=test-user` from `TestAuthHandler`)
  - Assert that responses contain only the test user's data, not the other user's data
  - Test all 5 analytics endpoints: `/api/analytics/summary`, `/daily`, `/sessions`, `/models`, `/export`
  
  Key implementation detail: The `ApiWebApplicationFactory` currently sets `AnalyticsEnabled=false`. Create a subclass or extend it to enable analytics for these tests.
  **Files**:
  - `tests/WeaveFleet.Api.Tests/Endpoints/AnalyticsEndpointTenantIsolationTests.cs`
  - `tests/WeaveFleet.Api.Tests/Infrastructure/ApiWebApplicationFactory.cs` (may need an overload or builder pattern to enable analytics)
  **Acceptance**: All analytics API endpoints return only the authenticated user's data

### Phase 5: API-Level Session/Event Tenant Isolation Tests

- [x] 11. Add API integration tests for session endpoint tenant isolation
  **What**: Create `SessionEndpointTenantIsolationTests` in `WeaveFleet.Api.Tests`. Use `ApiWebApplicationFactory` with `useTestAuthentication: true`. Tests:
  - `ListSessions_DoesNotReturnOtherUsersSessions` — seed sessions for two users in DB, GET `/api/sessions`, assert only test-user's sessions returned
  - `GetSession_ReturnsNotFoundForOtherUsersSession` — seed session owned by `other-user`, GET `/api/sessions/{id}`, assert 404
  - `DeleteSession_ReturnsNotFoundForOtherUsersSession` — attempt to DELETE another user's session, assert 404
  - `StopSession_ReturnsNotFoundForOtherUsersSession` — attempt to stop another user's session, assert 404
  
  These tests need to seed data directly into the SQLite DB using `IDbConnectionFactory` from the test host, then make HTTP requests through the test client.
  **Files**: `tests/WeaveFleet.Api.Tests/Endpoints/SessionEndpointTenantIsolationTests.cs`
  **Acceptance**: All cross-user session access attempts return 404 or empty lists

## Verification

- [x] `dotnet build -c Release` passes with zero warnings
- [x] `dotnet test --filter "Category!=E2E"` — all non-E2E tests pass
- [x] `dotnet test --filter "AnalyticsRepositoryTests"` — all existing + new analytics repo tests pass
- [x] `dotnet test --filter "AnalyticsEndpointTenantIsolation"` — all API-level analytics isolation tests pass
- [x] `dotnet test --filter "SessionEndpointTenantIsolation"` — all API-level session isolation tests pass
- [x] `dotnet test --filter "AnalyticsWriterServiceTests"` — writer tests still pass with user_id changes
- [x] Manual verification: review every SQL query in `AnalyticsRepository.cs` includes `user_id` filter
- [x] Manual verification: review `AnalyticsRollupService` rollup SQL includes `user_id` in GROUP BY and PK

## Implementation Notes

### Migration Strategy for `daily_rollups` PK Change
SQLite does not support `ALTER TABLE ... ADD PRIMARY KEY`. The migration must:
1. `CREATE TABLE daily_rollups_new` with the new schema including `user_id` in PK
2. `INSERT INTO daily_rollups_new SELECT *, '' as user_id FROM daily_rollups`
3. `DROP TABLE daily_rollups`
4. `ALTER TABLE daily_rollups_new RENAME TO daily_rollups`
5. Recreate the `idx_daily_rollups_date` index

### Default `user_id` Values
- For `token_events` and `session_snapshots`: default to `'local-user'` (matches `LocalUserContext.UserId`)
- For `daily_rollups`: default to `''` (fleet-wide rollups used this as placeholder; after migration, rollups will have proper user_id from recomputation)

### DI Registration
`AnalyticsRepository` is already registered as scoped at `DependencyInjection.cs:137`: `services.AddScoped<IAnalyticsReader, AnalyticsRepository>()`. Since `IUserContext` is also scoped (one per HTTP request), no registration change is needed — just adding `IUserContext` to the constructor will be auto-resolved by the DI container.

### Backwards Compatibility
In local/single-user mode, `LocalUserContext` returns `"local-user"`. All analytics data will have `user_id = 'local-user'`, and all queries will filter by `user_id = 'local-user'`. This is functionally equivalent to no filter, maintaining backwards compatibility.
