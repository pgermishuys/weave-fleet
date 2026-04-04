# Fleet API Migration — .NET Backend + SPA Frontend + Projects

## TL;DR
> **Summary**: Replace Next.js API routes with a real .NET Fleet API using Dapper/SQLite, convert the frontend to a stateless SPA, and introduce a Projects organizational layer from day one.
> **Estimated Effort**: XL

## Context

### Original Request
Major architectural migration in three dimensions:
1. Build out the .NET Fleet API as the real backend (replacing Next.js API routes)
2. Convert the Next.js frontend to a stateless SPA (delete all server-side code)
3. Introduce "Projects" concept from day one as the top-level organizational unit

### Key Findings

**Existing .NET Structure:**
- Clean 4-project layered architecture already exists (Domain → Application → Infrastructure → Api)
- `Domain/Common/` has `Result<T>`, `FleetError`, `Unit` — good foundation for service return types
- `Domain/Harnesses/` and `Application/Harnesses/` are real, working code (keep as-is)
- All API endpoints are stubs returning empty arrays, 404s, or 501s
- Infrastructure has `Microsoft.EntityFrameworkCore.Sqlite` (to be replaced)
- Central package versioning via `Directory.Packages.props`
- OpenTelemetry already wired in Api layer with `FleetInstrumentation` ActivitySource + Meter
- Target framework: `net10.0`, C# 14

**Existing Frontend Structure:**
- `client/src/lib/api-client.ts` already has configurable base URL (`apiFetch`, `apiUrl`, `wsUrl`, `sseUrl`)
- `client/src/lib/api-types.ts` has comprehensive type definitions (549 lines)
- `client/src/hooks/` has ~50 hooks, all use `apiFetch("/api/...")` pattern
- `client/src/lib/server/` has 16+ modules (database, db-repository, process-manager, workspace-manager, etc.)
- `client/src/app/api/` has 72+ route files (all server-side)
- `next.config.ts` already supports `output: 'export'` via `NEXT_BUILD_SPA=1` flag
- `scripts/build-spa.mjs` already hides `src/app/api/` during SPA build
- `better-sqlite3` and `@opencode-ai/sdk` are server-only dependencies to remove

**Database Layer (from `db-repository.ts`, 612 lines):**
- 5 tables: workspaces, instances, sessions, session_callbacks, workspace_roots
- ~40 query functions covering full CRUD + specialized queries
- Token tracking: `incrementSessionTokens`, `getFleetTokenTotals`
- Recovery functions: `markAllInstancesStopped`, `markAllNonTerminalSessionsStopped`
- Schema evolved via ad-hoc ALTER TABLE migrations (to be replaced by numbered SQL files)

**Server-side Logic (from `lib/server/`):**
- `process-manager.ts` (1098 lines) — spawns/tracks opencode instances, port allocation
- `workspace-manager.ts` (286 lines) — isolation strategies (existing/worktree/clone)
- `session-status-watcher.ts` — SSE event monitoring, activity/lifecycle status
- `config-manager.ts` — reads/writes weave-opencode.jsonc config
- `repository-scanner.ts` — scans workspace roots for git repos
- `integration-store.ts` — JSON file for integration tokens
- `auth-store.ts` — reads OpenCode's auth.json
- `activity-emitter.ts` — in-memory pub/sub for real-time events
- `callback-monitor.ts`, `callback-service.ts` — session callback system

## Objectives

### Core Objective
Migrate from a Next.js full-stack app to a .NET API backend + React SPA frontend, with Projects as a first-class concept, using Dapper for data access.

### Deliverables
- [ ] Fully functional .NET API replacing all Next.js API routes
- [ ] Projects feature (organizational layer above workspaces)
- [ ] Dapper-based data access with repository pattern
- [ ] SQL migration system applied at startup
- [ ] WebSocket-based real-time event broadcasting
- [ ] GitHub integration (OAuth device flow, repo/issue/PR APIs)
- [ ] Stateless SPA frontend with no server-side dependencies
- [ ] Works in both integrated (single binary) and split (dev) modes

### Definition of Done
- [ ] `dotnet build src/WeaveFleet.Api` succeeds with zero warnings
- [ ] `dotnet test` passes all tests
- [ ] `cd client && npm run build` produces a working SPA in `client/dist/`
- [ ] `cd client && npm run typecheck` passes with no errors
- [ ] SPA + .NET API works in split-mode dev (`npm run dev:split` + `dotnet run`)
- [ ] SPA + .NET API works in integrated mode (SPA served from wwwroot)
- [ ] No references to `better-sqlite3`, `@opencode-ai/sdk`, or `lib/server/` in client code
- [ ] No references to `Microsoft.EntityFrameworkCore` in .NET code

### Guardrails (Must NOT)
- Do NOT change the existing Domain/Common types (Result, FleetError, Unit)
- Do NOT change the Harness abstractions (IHarness, IHarnessInstance, IHarnessRegistry, HarnessRegistry)
- Do NOT change the OpenTelemetry wiring
- Do NOT change the api-client.ts base URL resolution mechanism
- Do NOT introduce Entity Framework — use Dapper exclusively
- Do NOT change the project structure (keep the 4-project layout)
- Do NOT change the existing api-types.ts shapes (frontend contract) unless adding Projects fields

---

## TODOs

### Phase 1: Data Foundation

**Goal:** Replace EF Core with Dapper + Microsoft.Data.Sqlite, build the migration system, and create the initial schema including the new `projects` table.

- [x] 1. **Swap NuGet packages in `Directory.Packages.props`**
  **What**: Remove EF Core package versions, add Dapper + Microsoft.Data.Sqlite versions
  **Files**:
  - Modify `Directory.Packages.props`:
    - Remove: `<PackageVersion Include="Microsoft.EntityFrameworkCore.Sqlite" .../>` and `<PackageVersion Include="Microsoft.EntityFrameworkCore.Design" .../>`
    - Add: `<PackageVersion Include="Dapper" Version="2.1.66" />` (or latest stable)
    - Add: `<PackageVersion Include="Microsoft.Data.Sqlite" Version="10.0.0" />`
  **Acceptance**: `dotnet restore` succeeds, no EF Core references remain

- [x] 2. **Update Infrastructure project references**
  **What**: Replace EF Core with Dapper + Microsoft.Data.Sqlite in the Infrastructure csproj
  **Files**:
  - Modify `src/WeaveFleet.Infrastructure/WeaveFleet.Infrastructure.csproj`:
    - Remove: `<PackageReference Include="Microsoft.EntityFrameworkCore.Sqlite" />`
    - Add: `<PackageReference Include="Dapper" />`
    - Add: `<PackageReference Include="Microsoft.Data.Sqlite" />`
  **Acceptance**: `dotnet build src/WeaveFleet.Infrastructure` succeeds

- [x] 3. **Create `IDbConnectionFactory` interface in Application layer**
  **What**: Define the abstraction for obtaining database connections. Services depend on this interface, not on concrete SQLite types. Returns `System.Data.IDbConnection`.
  **Files**:
  - Create `src/WeaveFleet.Application/Data/IDbConnectionFactory.cs`:
    ```csharp
    namespace WeaveFleet.Application.Data;
    public interface IDbConnectionFactory
    {
        IDbConnection CreateConnection();
    }
    ```
  **Acceptance**: Builds, no dependencies on Infrastructure or SQLite

- [x] 4. **Create `SqliteConnectionFactory` in Infrastructure layer**
  **What**: Implement `IDbConnectionFactory` using `Microsoft.Data.Sqlite`. Accepts database path from `FleetOptions.DatabasePath`. Ensures parent directory exists. Sets WAL mode, busy_timeout=5000, foreign_keys=ON on each connection.
  **Files**:
  - Create `src/WeaveFleet.Infrastructure/Data/SqliteConnectionFactory.cs`:
    - Constructor takes `FleetOptions` (or just `string databasePath`)
    - `CreateConnection()` returns a new `SqliteConnection` with pragmas applied
    - Ensures directory exists on first call
  **Acceptance**: Unit test creates an in-memory connection and verifies pragmas

- [x] 5. **Create SQL migration runner**
  **What**: Reads numbered `.sql` files from an embedded resource folder or file path, tracks applied migrations in a `_migrations` table, applies unapplied ones in order at startup. Each migration runs in a transaction.
  **Files**:
  - Create `src/WeaveFleet.Infrastructure/Data/MigrationRunner.cs`:
    - `async Task ApplyMigrationsAsync(IDbConnection connection)` or sync equivalent
    - Creates `_migrations (id INTEGER PRIMARY KEY, name TEXT NOT NULL UNIQUE, applied_at TEXT NOT NULL)` if not exists
    - Scans `src/WeaveFleet.Infrastructure/Migrations/*.sql` (embedded resources)
    - Sorts by filename (e.g., `001_initial_schema.sql`, `002_add_projects.sql`)
    - For each unapplied migration: executes SQL, inserts into `_migrations`
    - Logs each migration applied via `ILogger<MigrationRunner>`
  - Modify `src/WeaveFleet.Infrastructure/WeaveFleet.Infrastructure.csproj`:
    - Add `<ItemGroup><EmbeddedResource Include="Migrations\*.sql" /></ItemGroup>`
  **Acceptance**: Unit test applies migrations to in-memory DB, verifies table creation

- [x] 6. **Create initial migration: `001_initial_schema.sql`**
  **What**: Complete schema including all existing tables PLUS the new `projects` table with `project_id` added to `sessions`.
  **Files**:
  - Create `src/WeaveFleet.Infrastructure/Migrations/001_initial_schema.sql`:
    ```sql
    -- Projects table (new)
    CREATE TABLE projects (
      id TEXT PRIMARY KEY,
      name TEXT NOT NULL,
      description TEXT,
      type TEXT NOT NULL DEFAULT 'user',  -- 'user' | 'scratch'
      position INTEGER NOT NULL DEFAULT 0,
      created_at TEXT NOT NULL DEFAULT (datetime('now')),
      updated_at TEXT NOT NULL DEFAULT (datetime('now'))
    );

    -- Workspaces table
    CREATE TABLE workspaces (
      id TEXT PRIMARY KEY,
      directory TEXT NOT NULL,
      source_directory TEXT,
      isolation_strategy TEXT NOT NULL DEFAULT 'existing',
      branch TEXT,
      created_at TEXT NOT NULL DEFAULT (datetime('now')),
      cleaned_up_at TEXT,
      display_name TEXT
    );

    -- Instances table
    CREATE TABLE instances (
      id TEXT PRIMARY KEY,
      port INTEGER NOT NULL,
      pid INTEGER,
      directory TEXT NOT NULL,
      url TEXT NOT NULL,
      status TEXT NOT NULL DEFAULT 'running',
      created_at TEXT NOT NULL DEFAULT (datetime('now')),
      stopped_at TEXT
    );

    -- Sessions table (with project_id FK)
    CREATE TABLE sessions (
      id TEXT PRIMARY KEY,
      workspace_id TEXT NOT NULL REFERENCES workspaces(id),
      instance_id TEXT NOT NULL REFERENCES instances(id),
      project_id TEXT REFERENCES projects(id),
      opencode_session_id TEXT NOT NULL,
      title TEXT NOT NULL DEFAULT 'Untitled',
      status TEXT NOT NULL DEFAULT 'active',
      directory TEXT NOT NULL,
      created_at TEXT NOT NULL DEFAULT (datetime('now')),
      stopped_at TEXT,
      parent_session_id TEXT,
      activity_status TEXT,
      lifecycle_status TEXT,
      total_tokens INTEGER NOT NULL DEFAULT 0,
      total_cost REAL NOT NULL DEFAULT 0
    );

    -- Session callbacks table
    CREATE TABLE session_callbacks (
      id TEXT PRIMARY KEY,
      source_session_id TEXT NOT NULL,
      target_session_id TEXT NOT NULL,
      target_instance_id TEXT NOT NULL,
      status TEXT NOT NULL DEFAULT 'pending',
      created_at TEXT NOT NULL DEFAULT (datetime('now')),
      fired_at TEXT
    );

    -- Workspace roots table
    CREATE TABLE workspace_roots (
      id TEXT PRIMARY KEY,
      path TEXT NOT NULL UNIQUE,
      created_at TEXT NOT NULL DEFAULT (datetime('now'))
    );

    -- Indexes
    CREATE INDEX idx_callbacks_source ON session_callbacks(source_session_id, status);
    CREATE INDEX idx_sessions_status ON sessions(status);
    CREATE INDEX idx_sessions_parent ON sessions(parent_session_id);
    CREATE INDEX idx_sessions_created_at ON sessions(created_at DESC);
    CREATE INDEX idx_sessions_project ON sessions(project_id);
    CREATE INDEX idx_projects_type ON projects(type);
    CREATE INDEX idx_projects_position ON projects(position);
    ```
  **Acceptance**: Migration runner applies this to empty DB, all tables exist

- [x] 7. **Wire DI and startup**
  **What**: Register `IDbConnectionFactory` as singleton, run migrations at startup
  **Files**:
  - Modify `src/WeaveFleet.Infrastructure/DependencyInjection.cs`:
    - Remove the commented-out EF Core TODO
    - Register `SqliteConnectionFactory` as singleton implementing `IDbConnectionFactory`
    - Register `MigrationRunner` as singleton
  - Modify `src/WeaveFleet.Api/Program.cs`:
    - After `builder.Build()` and before `app.Run()`, resolve `MigrationRunner` and call `ApplyMigrationsAsync()`
    - Resolve `IDbConnectionFactory`, create a connection, apply migrations
  **Acceptance**: `dotnet run` starts, creates DB file, applies migration 001, logs success

- [x] 8. **Add Infrastructure.Tests for migration + connection factory**
  **What**: xUnit tests verifying migration runner and connection factory work correctly
  **Files**:
  - Modify `tests/WeaveFleet.Infrastructure.Tests/WeaveFleet.Infrastructure.Tests.csproj`:
    - Add `<PackageReference Include="Microsoft.Data.Sqlite" />` for in-memory testing
  - Create `tests/WeaveFleet.Infrastructure.Tests/Data/SqliteConnectionFactoryTests.cs`:
    - Test: creates connection, pragmas are set
  - Create `tests/WeaveFleet.Infrastructure.Tests/Data/MigrationRunnerTests.cs`:
    - Test: applies migrations to empty DB
    - Test: skips already-applied migrations
    - Test: applies in correct order
  **Acceptance**: `dotnet test tests/WeaveFleet.Infrastructure.Tests` passes

---

### Phase 2: Domain & Repository Layer

**Goal:** Define entity classes, repository interfaces, and Dapper implementations covering all existing query functions.

- [x] 9. **Create Domain entity classes**
  **What**: Simple POCO classes matching the database schema. No EF annotations — these are Dapper-friendly plain objects.
  **Files**:
  - Create `src/WeaveFleet.Domain/Entities/Project.cs`:
    - Properties: `Id` (string), `Name` (string), `Description` (string?), `Type` (string, "user"|"scratch"), `Position` (int), `CreatedAt` (string), `UpdatedAt` (string)
  - Create `src/WeaveFleet.Domain/Entities/Workspace.cs`:
    - Properties: `Id`, `Directory`, `SourceDirectory?`, `IsolationStrategy`, `Branch?`, `CreatedAt`, `CleanedUpAt?`, `DisplayName?`
  - Create `src/WeaveFleet.Domain/Entities/Instance.cs`:
    - Properties: `Id`, `Port` (int), `Pid` (int?), `Directory`, `Url`, `Status`, `CreatedAt`, `StoppedAt?`
  - Create `src/WeaveFleet.Domain/Entities/Session.cs`:
    - Properties: `Id`, `WorkspaceId`, `InstanceId`, `ProjectId?`, `OpencodeSessionId`, `Title`, `Status`, `Directory`, `CreatedAt`, `StoppedAt?`, `ParentSessionId?`, `ActivityStatus?`, `LifecycleStatus?`, `TotalTokens` (int), `TotalCost` (double)
  - Create `src/WeaveFleet.Domain/Entities/SessionCallback.cs`:
    - Properties: `Id`, `SourceSessionId`, `TargetSessionId`, `TargetInstanceId`, `Status`, `CreatedAt`, `FiredAt?`
  - Create `src/WeaveFleet.Domain/Entities/WorkspaceRoot.cs`:
    - Properties: `Id`, `Path`, `CreatedAt`
  **Acceptance**: All entities compile, no external dependencies

- [x] 10. **Create repository interfaces in Domain layer**
  **What**: Define interfaces for each aggregate root. Method signatures mirror the existing `db-repository.ts` functions but use C# conventions + `Result<T>` where appropriate.
  **Files**:
  - Create `src/WeaveFleet.Domain/Repositories/IProjectRepository.cs`:
    ```csharp
    Task<Project?> GetByIdAsync(string id);
    Task<Project?> GetScratchProjectAsync();
    Task<IReadOnlyList<Project>> ListAsync();
    Task InsertAsync(Project project);
    Task UpdateAsync(Project project);
    Task DeleteAsync(string id);
    Task ReorderAsync(string id, int newPosition);
    Task<int> GetSessionCountAsync(string projectId);
    Task MoveSessionsToProjectAsync(string fromProjectId, string toProjectId);
    ```
  - Create `src/WeaveFleet.Domain/Repositories/IWorkspaceRepository.cs`:
    ```csharp
    Task InsertAsync(Workspace workspace);
    Task<Workspace?> GetByIdAsync(string id);
    Task<Workspace?> GetByDirectoryAsync(string directory, string isolationStrategy);
    Task<IReadOnlyList<Workspace>> ListAsync();
    Task MarkCleanedAsync(string id);
    Task UpdateDisplayNameAsync(string id, string displayName);
    ```
  - Create `src/WeaveFleet.Domain/Repositories/IInstanceRepository.cs`:
    ```csharp
    Task InsertAsync(Instance instance);
    Task<Instance?> GetByIdAsync(string id);
    Task<Instance?> GetByDirectoryAsync(string directory);
    Task<IReadOnlyList<Instance>> ListAsync();
    Task UpdateStatusAsync(string id, string status, string? stoppedAt = null);
    Task<IReadOnlyList<Instance>> GetRunningAsync();
    Task<int> MarkAllStoppedAsync(string stoppedAt);
    ```
  - Create `src/WeaveFleet.Domain/Repositories/ISessionRepository.cs`:
    ```csharp
    Task InsertAsync(Session session);
    Task<Session?> GetByIdAsync(string id);
    Task<Session?> GetByHarnessIdAsync(string harnessSessionId);
    Task<IReadOnlyList<Session>> ListAsync(int limit = 100, int offset = 0, IReadOnlyList<string>? statuses = null);
    Task<int> CountAsync(IReadOnlyList<string>? statuses = null);
    Task<(int Active, int Idle)> GetStatusCountsAsync();
    Task<IReadOnlyList<Session>> ListActiveAsync();
    Task UpdateStatusAsync(string id, string status, string? stoppedAt = null);
    Task<IReadOnlyList<Session>> GetForInstanceAsync(string instanceId);
    Task<Session?> GetAnyForInstanceAsync(string instanceId);
    Task<IReadOnlyList<Session>> GetNonTerminalForInstanceAsync(string instanceId);
    Task UpdateTitleAsync(string id, string title);
    Task UpdateForResumeAsync(string id, string instanceId);
    Task<IReadOnlyList<Session>> GetActiveChildrenAsync(string parentDbId);
    Task<IReadOnlySet<string>> GetIdsWithActiveChildrenAsync();
    Task<IReadOnlyList<Session>> GetForWorkspaceAsync(string workspaceId);
    Task<bool> DeleteAsync(string id);
    Task<(int TotalTokens, double TotalCost)?> IncrementTokensAsync(string id, int tokens, double cost);
    Task<(int TotalTokens, double TotalCost)> GetFleetTokenTotalsAsync();
    Task<int> MarkAllNonTerminalStoppedAsync(string stoppedAt);
    Task UpdateProjectAsync(string id, string? projectId);
    ```
  - Create `src/WeaveFleet.Domain/Repositories/ISessionCallbackRepository.cs`:
    ```csharp
    Task InsertAsync(SessionCallback callback);
    Task<IReadOnlyList<SessionCallback>> GetPendingForSessionAsync(string sourceSessionId);
    Task MarkFiredAsync(string id);
    Task<bool> ClaimPendingAsync(string id);
    Task<IReadOnlyList<SessionCallback>> GetAllPendingAsync();
    Task<int> DeleteForSessionAsync(string sessionId);
    ```
  - Create `src/WeaveFleet.Domain/Repositories/IWorkspaceRootRepository.cs`:
    ```csharp
    Task InsertAsync(WorkspaceRoot root);
    Task<IReadOnlyList<WorkspaceRoot>> ListAsync();
    Task<bool> DeleteAsync(string id);
    Task<WorkspaceRoot?> GetByPathAsync(string path);
    ```
  **Acceptance**: All interfaces compile, Domain project has zero external package dependencies

- [x] 11. **Create Dapper repository implementations in Infrastructure**
  **What**: Implement each repository interface using Dapper's `QueryAsync`, `ExecuteAsync`, `QueryFirstOrDefaultAsync`. Use `IDbConnectionFactory` to get connections. All methods open/close connections per call (Dapper connection-per-query pattern).
  **Files**:
  - Create `src/WeaveFleet.Infrastructure/Data/Repositories/DapperProjectRepository.cs`
  - Create `src/WeaveFleet.Infrastructure/Data/Repositories/DapperWorkspaceRepository.cs`
  - Create `src/WeaveFleet.Infrastructure/Data/Repositories/DapperInstanceRepository.cs`
  - Create `src/WeaveFleet.Infrastructure/Data/Repositories/DapperSessionRepository.cs`
  - Create `src/WeaveFleet.Infrastructure/Data/Repositories/DapperSessionCallbackRepository.cs`
  - Create `src/WeaveFleet.Infrastructure/Data/Repositories/DapperWorkspaceRootRepository.cs`
  **Implementation pattern** (same for each):
  ```csharp
  public class DapperSessionRepository(IDbConnectionFactory connectionFactory) : ISessionRepository
  {
      public async Task<Session?> GetByIdAsync(string id)
      {
          using var conn = connectionFactory.CreateConnection();
          return await conn.QueryFirstOrDefaultAsync<Session>(
              "SELECT * FROM sessions WHERE id = @Id", new { Id = id });
      }
      // ... etc
  }
  ```
  **Key Dapper notes**:
  - Use Dapper's `DefaultTypeMap.MatchNamesWithUnderscores = true` globally (set once in DI setup) so that `snake_case` columns map to `PascalCase` properties
  - For `ListAsync` with dynamic status filtering, build SQL with parameterized IN clauses
  - `IncrementTokensAsync` uses UPDATE + SELECT in same connection (not a transaction, SQLite single-writer handles this)
  **Acceptance**: Each repository compiles, basic unit tests pass with in-memory SQLite

- [x] 12. **Register repositories in DI**
  **What**: Add all repository registrations to `DependencyInjection.AddFleetInfrastructure()`
  **Files**:
  - Modify `src/WeaveFleet.Infrastructure/DependencyInjection.cs`:
    - Add: `Dapper.DefaultTypeMap.MatchNamesWithUnderscores = true;` (once, static)
    - Register each repository interface → Dapper implementation as Scoped
    - Keep existing `HarnessRegistry` singleton registration unchanged
  **Acceptance**: `dotnet build` succeeds, DI container resolves all repositories

- [x] 13. **Add repository integration tests**
  **What**: Tests that exercise each repository against a real in-memory SQLite database
  **Files**:
  - Create `tests/WeaveFleet.Infrastructure.Tests/Data/Repositories/DapperProjectRepositoryTests.cs`
  - Create `tests/WeaveFleet.Infrastructure.Tests/Data/Repositories/DapperSessionRepositoryTests.cs`
  - Create `tests/WeaveFleet.Infrastructure.Tests/Data/Repositories/DapperWorkspaceRepositoryTests.cs`
  - Create `tests/WeaveFleet.Infrastructure.Tests/Data/Repositories/DapperInstanceRepositoryTests.cs`
  - Create test helper: `tests/WeaveFleet.Infrastructure.Tests/Data/TestDbHelper.cs` — creates in-memory SQLite with migrations applied
  **Acceptance**: `dotnet test` — all repository tests pass

---

### Phase 3: Core API — Projects & Sessions

**Goal:** Replace session endpoint stubs with real implementations, add project endpoints, wire up fleet summary to real DB queries.

- [x] 14. **Create Application-layer services**
  **What**: Service classes that encapsulate business logic, called by endpoints. Return `Result<T>` for error handling.
  **Files**:
  - Create `src/WeaveFleet.Application/Services/ProjectService.cs`:
    - `CreateProjectAsync(name, description?)` — assigns next position
    - `GetProjectAsync(id)`
    - `ListProjectsAsync()` — ordered by position
    - `UpdateProjectAsync(id, name?, description?)`
    - `DeleteProjectAsync(id, mode: "move_to_scratch" | "delete_sessions")` — cannot delete scratch project
    - `ReorderProjectAsync(id, newPosition)`
    - `EnsureScratchProjectAsync()` — creates scratch if not exists, returns it
  - Create `src/WeaveFleet.Application/Services/SessionService.cs`:
    - `ListSessionsAsync(limit, offset, statuses?, projectId?)`
    - `GetSessionAsync(id)`
    - `DeleteSessionAsync(id)`
    - `UpdateSessionTitleAsync(id, title)`
    - `MoveSessionToProjectAsync(sessionId, projectId)`
    - `GetFleetSummaryAsync()` — aggregates from DB
  **Acceptance**: Services compile, unit-testable with mocked repositories

- [x] 15. **Create Application-layer DTOs**
  **What**: Request/response DTOs for the API layer (separate from domain entities)
  **Files**:
  - Create `src/WeaveFleet.Application/DTOs/ProjectDtos.cs`:
    - `CreateProjectRequest { Name, Description? }`
    - `UpdateProjectRequest { Name?, Description? }`
    - `DeleteProjectRequest { Mode }` (enum: MoveToScratch, DeleteSessions)
    - `ReorderProjectRequest { Position }`
    - `ProjectResponse { Id, Name, Description, Type, Position, SessionCount, CreatedAt, UpdatedAt }`
  - Create `src/WeaveFleet.Application/DTOs/SessionDtos.cs`:
    - `SessionListResponse` — matching existing `SessionListItem` shape from api-types.ts
    - `MoveSessionRequest { ProjectId }`
  - Create `src/WeaveFleet.Application/DTOs/FleetSummaryDto.cs`:
    - `FleetSummaryResponse { ActiveSessions, IdleSessions, TotalTokens, TotalCost, QueuedTasks }`
  **Acceptance**: DTOs compile, match frontend api-types.ts contract

- [x] 16. **Replace `SessionEndpoints.cs` with real implementations**
  **What**: Wire endpoints to `SessionService`, returning real data from DB
  **Files**:
  - Modify `src/WeaveFleet.Api/Endpoints/SessionEndpoints.cs`:
    - `GET /api/sessions` — accepts `?limit=&offset=&status=&projectId=`, returns `SessionListItem[]`
    - `GET /api/sessions/{id}` — returns full session with workspace/instance data
    - `DELETE /api/sessions/{id}` — delegates to SessionService
    - `PATCH /api/sessions/{id}` — rename (title update)
    - `PATCH /api/sessions/{id}/project` — move to different project
    - Keep `POST /api/sessions` as 501 for now (Phase 5 will implement with harness)
  **Acceptance**: `GET /api/sessions` returns real data from DB (may be empty), proper status codes

- [x] 17. **Create new `ProjectEndpoints.cs`**
  **What**: Full CRUD for projects
  **Files**:
  - Create `src/WeaveFleet.Api/Endpoints/ProjectEndpoints.cs`:
    - `GET /api/projects` — list all projects ordered by position
    - `GET /api/projects/{id}` — single project with session count
    - `POST /api/projects` — create new project
    - `PATCH /api/projects/{id}` — update name/description
    - `DELETE /api/projects/{id}` — with `?mode=move_to_scratch|delete_sessions`
    - `PATCH /api/projects/{id}/reorder` — change position
  - Modify `src/WeaveFleet.Api/Endpoints/EndpointExtensions.cs`:
    - Add `app.MapProjectEndpoints();`
  **Acceptance**: Full project CRUD works via curl/Postman

- [x] 18. **Replace `FleetEndpoints.cs` summary with real DB queries**
  **What**: Wire fleet summary to real aggregation queries
  **Files**:
  - Modify `src/WeaveFleet.Api/Endpoints/FleetEndpoints.cs`:
    - `GET /api/fleet/summary` — calls `SessionService.GetFleetSummaryAsync()`, returns `{ activeSessions, idleSessions, totalTokens, totalCost, queuedTasks }`
    - Keep version, profile, and other stub endpoints for now (Phase 7)
  **Acceptance**: Summary reflects actual DB state

- [x] 19. **Auto-create scratch project at startup**
  **What**: After migrations, ensure a scratch project exists
  **Files**:
  - Modify `src/WeaveFleet.Api/Program.cs`:
    - After migration runner, resolve `ProjectService` and call `EnsureScratchProjectAsync()`
  **Acceptance**: On first boot, a "Scratch" project with `type='scratch'` exists in DB

- [x] 20. **Add endpoint helper for Result<T> → IResult mapping**
  **What**: Extension method to convert `Result<T>` to minimal API `IResult` with proper HTTP status codes
  **Files**:
  - Create `src/WeaveFleet.Api/Endpoints/ResultExtensions.cs`:
    ```csharp
    public static IResult ToApiResult<T>(this Result<T> result) =>
        result.Match(
            value => Results.Ok(value),
            error => error.Code switch
            {
                "General.NotFound" or var c when c.EndsWith(".NotFound") => Results.NotFound(new { error = error.Description }),
                "General.Conflict" => Results.Conflict(new { error = error.Description }),
                var c when c.StartsWith("Validation.") => Results.BadRequest(new { error = error.Description }),
                _ => Results.Problem(error.Description)
            });
    ```
  **Acceptance**: NotFound errors return 404, Validation errors return 400, etc.

- [x] 21. **Add Application.Tests for services**
  **What**: Unit tests for ProjectService and SessionService with mocked repositories
  **Files**:
  - Create `tests/WeaveFleet.Application.Tests/Services/ProjectServiceTests.cs`
  - Create `tests/WeaveFleet.Application.Tests/Services/SessionServiceTests.cs`
  - May need to add a mocking library to test projects — add `NSubstitute` to `Directory.Packages.props` and test csprojs
  **Acceptance**: `dotnet test tests/WeaveFleet.Application.Tests` passes

- [x] 22. **Update frontend api-types.ts with Project types**
  **What**: Add project-related types to the frontend type definitions
  **Files**:
  - Modify `client/src/lib/api-types.ts`:
    - Add `ProjectResponse` interface
    - Add `CreateProjectRequest`, `UpdateProjectRequest` interfaces
    - Add `projectId?: string` to `SessionListItem`
    - Add `projectName?: string` to `SessionListItem`
  **Acceptance**: `npm run typecheck` passes

---

### Phase 4: Workspace & Instance Management

**Goal:** Replace workspace root stubs with real implementations, add workspace CRUD, and instance tracking.

- [x] 23. **Create `WorkspaceService` in Application layer**
  **What**: Business logic for workspace creation with isolation strategies, mirroring `workspace-manager.ts`
  **Files**:
  - Create `src/WeaveFleet.Application/Services/WorkspaceService.cs`:
    - `CreateWorkspaceAsync(sourceDirectory, strategy, branch?)` — runs git commands for worktree/clone
    - `CleanupWorkspaceAsync(id)` — removes worktree/clone directories
    - `GetWorkspaceDirectoryAsync(id)`
    - `ListWorkspacesAsync()`
    - `UpdateDisplayNameAsync(id, displayName)`
  **Acceptance**: Service compiles, workspace creation logic matches TypeScript version

- [x] 24. **Create `InstanceService` in Application layer**
  **What**: Instance lifecycle management, mirroring relevant `process-manager.ts` functions
  **Files**:
  - Create `src/WeaveFleet.Application/Services/InstanceService.cs`:
    - `RegisterInstanceAsync(id, port, pid?, directory, url)` — inserts into DB
    - `GetInstanceAsync(id)`
    - `ListInstancesAsync()`
    - `GetRunningInstancesAsync()`
    - `UpdateInstanceStatusAsync(id, status, stoppedAt?)`
    - `MarkAllStoppedAsync()` — recovery at startup
    - `MarkAllNonTerminalSessionsStoppedAsync()` — recovery at startup
  **Acceptance**: Service compiles and is unit-testable

- [x] 25. **Create `WorkspaceRootService` in Application layer**
  **What**: Manages the user-configurable workspace root directories
  **Files**:
  - Create `src/WeaveFleet.Application/Services/WorkspaceRootService.cs`:
    - `ListRootsAsync()` — combines DB roots + env var roots, checks existence
    - `AddRootAsync(path)` — validates path exists, inserts, returns root
    - `RemoveRootAsync(id)` — deletes from DB
    - `GetAllowedRootsAsync()` — returns all paths (for path validation)
  **Acceptance**: Service compiles

- [x] 26. **Replace `WorkspaceRootEndpoints.cs` with real implementations**
  **What**: Wire to WorkspaceRootService
  **Files**:
  - Modify `src/WeaveFleet.Api/Endpoints/WorkspaceRootEndpoints.cs`:
    - `GET /api/workspace-roots` — returns `{ roots: WorkspaceRootItem[] }` matching frontend type
    - `POST /api/workspace-roots` — accepts `{ path }`, validates, inserts
    - `DELETE /api/workspace-roots/{id}` — deletes root
  **Acceptance**: Full workspace root CRUD works

- [x] 27. **Create workspace endpoints**
  **What**: New endpoints for workspace management
  **Files**:
  - Create `src/WeaveFleet.Api/Endpoints/WorkspaceEndpoints.cs`:
    - `GET /api/workspaces` — list all workspaces
    - `GET /api/workspaces/{id}` — single workspace detail
    - `PATCH /api/workspaces/{id}` — rename (display name)
  - Modify `src/WeaveFleet.Api/Endpoints/EndpointExtensions.cs`:
    - Add `app.MapWorkspaceEndpoints();`
  **Acceptance**: Workspace list/detail/rename endpoints work

- [x] 28. **Add recovery logic at startup**
  **What**: On boot, mark all running instances stopped and cascade to sessions (mirroring `markAllInstancesStopped` + `markAllNonTerminalSessionsStopped`)
  **Files**:
  - Modify `src/WeaveFleet.Api/Program.cs`:
    - After migration + scratch project setup, resolve `InstanceService`
    - Call `MarkAllStoppedAsync()` + `MarkAllNonTerminalSessionsStoppedAsync()`
    - Log counts of instances/sessions transitioned
  **Acceptance**: After restart, all previously-running instances show as stopped

---

### Phase 5: Session Lifecycle & Communication

**Goal:** Implement session creation, prompt, abort, resume, fork — the operations that require harness integration.

- [x] 29. **Create `SessionOrchestrator` in Application layer**
  **What**: High-level service that coordinates workspace creation, instance spawning, session creation, and harness communication. This is the "create session" business logic.
  **Files**:
  - Create `src/WeaveFleet.Application/Services/SessionOrchestrator.cs`:
    - `CreateSessionAsync(directory, title?, isolationStrategy?, branch?, harnessType?, projectId?, context?, onComplete?)`:
      1. Create workspace (via WorkspaceService)
      2. Spawn harness instance (via IHarnessRegistry → IHarness.SpawnAsync)
      3. Insert instance record
      4. Insert session record (with project_id — default to scratch if not specified)
      5. If onComplete, insert session_callback
      6. Return session + instanceId + workspaceId
    - `ResumeSessionAsync(id)` — find existing session, spawn new instance, update session
    - `ForkSessionAsync(id, title?)` — create new session from parent's workspace
    - `PromptSessionAsync(id, text, options?)` — route to harness instance
    - `AbortSessionAsync(id)` — route to harness instance
    - `DeleteSessionAsync(id)` — stop instance, cleanup workspace, delete session
    - `GetSessionMessagesAsync(id)` — route to harness instance
    - `GetSessionDiffsAsync(id)` — query harness for file diffs
  **Acceptance**: CreateSessionAsync compiles with proper flow

- [x] 30. **Create `InstanceTracker` for in-memory instance management**
  **What**: Tracks live `IHarnessInstance` objects by instanceId (in-memory map). Needed because DB only stores metadata, but we need the actual process handle for send/abort/messages.
  **Files**:
  - Create `src/WeaveFleet.Application/Services/InstanceTracker.cs`:
    - Singleton, `ConcurrentDictionary<string, IHarnessInstance>`
    - `Register(instanceId, IHarnessInstance)`
    - `Get(instanceId) → IHarnessInstance?`
    - `Remove(instanceId)`
    - `GetAll() → IReadOnlyDictionary`
  **Acceptance**: Thread-safe tracker compiles

- [x] 31. **Wire session lifecycle endpoints**
  **What**: Implement the remaining session endpoints that were 501
  **Files**:
  - Modify `src/WeaveFleet.Api/Endpoints/SessionEndpoints.cs`:
    - `POST /api/sessions` — create session (delegates to SessionOrchestrator)
    - Add sub-routes:
      - `POST /api/sessions/{id}/prompt` — send prompt
      - `POST /api/sessions/{id}/abort` — abort current operation
      - `POST /api/sessions/{id}/resume` — resume stopped session
      - `POST /api/sessions/{id}/fork` — fork session
      - `GET /api/sessions/{id}/messages` — get message history
      - `GET /api/sessions/{id}/diffs` — get file diffs
      - `GET /api/sessions/{id}/status` — get current status
      - `POST /api/sessions/{id}/command` — send command
  **Acceptance**: Session CRUD + lifecycle endpoints return proper responses

- [x] 32. **Create `SessionCallbackService`**
  **What**: Manages session callbacks (parent-child notification on completion), mirroring `callback-service.ts` + `callback-monitor.ts`
  **Files**:
  - Create `src/WeaveFleet.Application/Services/SessionCallbackService.cs`:
    - `RegisterCallbackAsync(sourceSessionId, targetSessionId, targetInstanceId)`
    - `TryFireCallbacksAsync(sourceSessionId)` — fires when source session completes
    - `ProcessPendingCallbacksAsync()` — poll safety net
  **Acceptance**: Callback registration and firing logic works

- [x] 33. **Add Application.Tests for orchestrator**
  **What**: Unit tests for SessionOrchestrator with mocked dependencies
  **Files**:
  - Create `tests/WeaveFleet.Application.Tests/Services/SessionOrchestratorTests.cs`
  **Acceptance**: Tests pass

---

### Phase 6: Real-time Events

**Goal:** Replace the WebSocket stub with real event broadcasting for activity status, token updates, and session events.

- [x] 34. **Create `EventBroadcaster` service**
  **What**: In-memory pub/sub service that WebSocket connections subscribe to. Replaces `activity-emitter.ts`.
  **Files**:
  - Create `src/WeaveFleet.Application/Services/IEventBroadcaster.cs`:
    ```csharp
    public interface IEventBroadcaster
    {
        Task BroadcastAsync(string topic, object payload, CancellationToken ct = default);
        IAsyncEnumerable<BroadcastEvent> SubscribeAsync(IReadOnlyList<string> topics, CancellationToken ct);
    }
    ```
  - Create `src/WeaveFleet.Application/Services/BroadcastEvent.cs`:
    - `record BroadcastEvent(string Topic, string Type, JsonElement Payload, DateTimeOffset Timestamp)`
  - Create `src/WeaveFleet.Infrastructure/Services/InMemoryEventBroadcaster.cs`:
    - Uses `Channel<BroadcastEvent>` per subscriber
    - Thread-safe subscription management
  **Acceptance**: Unit test: publish to topic, subscriber receives event

- [x] 35. **Replace `WebSocketEndpoints.cs` with real broadcasting**
  **What**: Handle subscribe/unsubscribe messages, push events from EventBroadcaster to connected clients
  **Files**:
  - Modify `src/WeaveFleet.Api/Endpoints/WebSocketEndpoints.cs`:
    - On subscribe: register topics with EventBroadcaster, start pushing events to WebSocket
    - On unsubscribe: remove subscriptions
    - Handle client disconnect gracefully
    - Broadcast event types: `activity_status`, `token_update`, `session_created`, `session_stopped`, `session_completed`
  **Acceptance**: WebSocket clients receive real-time events when sessions change status

- [x] 36. **Create SSE endpoint for session events**
  **What**: Server-Sent Events endpoint for per-session event streaming (agent messages, tool use, etc.)
  **Files**:
  - Create `src/WeaveFleet.Api/Endpoints/SessionEventEndpoints.cs`:
    - `GET /api/sessions/{id}/events` — SSE stream that proxies harness events
    - Subscribes to IHarnessInstance.SubscribeAsync() and formats as SSE
  - Modify `src/WeaveFleet.Api/Endpoints/EndpointExtensions.cs`:
    - Wire session event endpoints
  **Acceptance**: EventSource connects and receives harness events

- [x] 37. **Wire event emission into session lifecycle**
  **What**: Emit events when session status changes, tokens update, etc.
  **Files**:
  - Modify `src/WeaveFleet.Application/Services/SessionOrchestrator.cs`:
    - After creating session → broadcast `session_created`
    - After status change → broadcast `activity_status` or `session_stopped`/`session_completed`
  - Modify `src/WeaveFleet.Application/Services/SessionService.cs`:
    - After token increment → broadcast `token_update`
  **Acceptance**: Creating/stopping sessions triggers WebSocket events

- [x] 38. **Create activity stream SSE endpoint**
  **What**: Global SSE stream for dashboard-level activity events, replacing `client/src/app/api/activity-stream/route.ts`
  **Files**:
  - Create or modify to add `GET /api/activity-stream` in `FleetEndpoints.cs` or a new `ActivityStreamEndpoints.cs`
    - SSE stream that pushes all `activity_status` and `token_update` events
  **Acceptance**: EventSource at `/api/activity-stream` receives real-time updates

---

### Phase 7: Supporting APIs

**Goal:** Implement the remaining API endpoints that the frontend depends on — config, directories, repositories, version, profile, tools, skills.

- [x] 39. **Create `ConfigService` in Application layer**
  **What**: Reads/writes weave config files, mirroring `config-manager.ts`
  **Files**:
  - Create `src/WeaveFleet.Application/Services/ConfigService.cs`:
    - `GetUserConfigAsync()` — reads `~/.weave/weave-opencode.jsonc`
    - `GetMergedConfigAsync(directory?)` — user + project config
    - `UpdateUserConfigAsync(config)` — writes config
    - `GetConfigPathsAsync()` — returns paths for display
  **Acceptance**: Config read/write works

- [x] 40. **Replace `ConfigEndpoints.cs` with real implementation**
  **What**: Wire config endpoints to ConfigService
  **Files**:
  - Modify `src/WeaveFleet.Api/Endpoints/ConfigEndpoints.cs`:
    - `GET /api/config` — returns merged config (or user config if no directory param)
    - `PUT /api/config` — updates user config
  **Acceptance**: Config endpoint returns real config data

- [x] 41. **Create `DirectoryService` and replace `DirectoryEndpoints.cs`**
  **What**: Directory browsing functionality, mirroring `client/src/app/api/directories/route.ts`
  **Files**:
  - Create `src/WeaveFleet.Application/Services/DirectoryService.cs`:
    - `ListDirectoryAsync(path?)` — lists subdirectories, checks .git, validates against allowed roots
    - Returns `{ entries, currentPath, parentPath, roots }`
  - Modify `src/WeaveFleet.Api/Endpoints/DirectoryEndpoints.cs`:
    - `GET /api/directories?path=` — returns directory listing
  - Create `src/WeaveFleet.Api/Endpoints/OpenDirectoryEndpoints.cs`:
    - `POST /api/open-directory` — opens a directory in the OS file manager (shell execute)
  **Acceptance**: Directory browsing returns real filesystem data

- [x] 42. **Create `RepositoryService` and endpoints**
  **What**: Repository scanning and detail, mirroring `repository-scanner.ts`
  **Files**:
  - Create `src/WeaveFleet.Application/Services/RepositoryService.cs`:
    - `ScanRepositoriesAsync()` — scans workspace roots for git repos
    - `GetRepositoryInfoAsync(path)` — git metadata for single repo
    - `GetRepositoryDetailAsync(path)` — full enriched repo detail
    - `RefreshScanAsync()` — invalidates cache and rescans
    - In-memory cache with manual invalidation
  - Modify `src/WeaveFleet.Api/Endpoints/FleetEndpoints.cs`:
    - `GET /api/repositories` — returns scanned repos
    - `GET /api/repositories/info?path=` — single repo info
    - `GET /api/repositories/detail?path=` — enriched detail
    - `POST /api/repositories/refresh` — invalidate cache
  **Acceptance**: Repository endpoints return real git data

- [x] 43. **Wire remaining `FleetEndpoints.cs` stubs**
  **What**: Implement version, profile, integrations, skills, available-tools
  **Files**:
  - Modify `src/WeaveFleet.Api/Endpoints/FleetEndpoints.cs`:
    - `GET /api/version` — return assembly version + git commit (from `FleetInstrumentation.ServiceVersion`)
    - `GET /api/profile` — return current profile name (from `FleetOptions` or env)
    - `GET /api/integrations` — return integration statuses (from integration store)
    - `GET /api/skills` — return installed skills (from config service)
    - `GET /api/available-tools` — return available tools (from config service)
  **Acceptance**: All endpoints return real data

- [x] 44. **Create skills endpoints**
  **What**: CRUD for skill management
  **Files**:
  - Create `src/WeaveFleet.Api/Endpoints/SkillEndpoints.cs`:
    - `GET /api/skills` — list installed skills
    - `GET /api/skills/{name}` — get single skill
    - `POST /api/skills` — install skill
    - `DELETE /api/skills/{name}` — remove skill
  - Modify `src/WeaveFleet.Api/Endpoints/EndpointExtensions.cs`:
    - Add `app.MapSkillEndpoints();`
  **Acceptance**: Skill CRUD works

- [x] 45. **Create instance proxy endpoints**
  **What**: Endpoints that proxy requests to specific harness instances
  **Files**:
  - Create `src/WeaveFleet.Api/Endpoints/InstanceEndpoints.cs`:
    - `GET /api/instances/{id}/models` — proxy to harness for model list
    - `GET /api/instances/{id}/commands` — proxy to harness for command list
    - `GET /api/instances/{id}/agents` — proxy to harness for agent list
    - `GET /api/instances/{id}/find/files?q=` — proxy file search
  - Modify `src/WeaveFleet.Api/Endpoints/EndpointExtensions.cs`:
    - Add `app.MapInstanceEndpoints();`
  **Acceptance**: Instance proxy endpoints return data from harness instances

---

### Phase 8: GitHub Integrations

**Goal:** Implement GitHub OAuth device flow and all GitHub API proxy endpoints.

- [x] 46. **Create `IntegrationStore` in Infrastructure**
  **What**: Reads/writes `~/.weave/integrations.json` for token storage, mirroring `integration-store.ts`
  **Files**:
  - Create `src/WeaveFleet.Application/Services/IIntegrationStore.cs` (interface)
  - Create `src/WeaveFleet.Infrastructure/Services/FileIntegrationStore.cs`:
    - `GetConfigAsync(id)` — read token/settings for integration
    - `SetConfigAsync(id, config)` — upsert
    - `RemoveConfigAsync(id)` — delete
    - `GetAllConfigsAsync()` — list all
  **Acceptance**: Integration store reads/writes JSON file

- [x] 47. **Create `GitHubService` in Application layer**
  **What**: GitHub OAuth device flow + API proxy
  **Files**:
  - Create `src/WeaveFleet.Application/Services/GitHubService.cs`:
    - `InitiateDeviceFlowAsync()` — POST to GitHub device code endpoint, return `DeviceCodeResponse`
    - `PollForTokenAsync(deviceCode)` — poll GitHub token endpoint, store on success
    - `IsConnectedAsync()` — check if token exists
    - `GetTokenAsync()` — retrieve stored token
    - Constants: `GITHUB_OAUTH_CLIENT_ID = "Ov23liJT2Q0HXHj9xLGM"`, scopes, URLs
  - Create `src/WeaveFleet.Application/Services/GitHubApiProxy.cs`:
    - `FetchAsync(token, path, method, body?)` — proxies requests to api.github.com
    - Used by all GitHub API endpoints
  **Acceptance**: Device flow initiation returns valid response from GitHub

- [x] 48. **Create GitHub auth endpoints**
  **What**: OAuth device flow endpoints
  **Files**:
  - Create `src/WeaveFleet.Api/Endpoints/GitHubAuthEndpoints.cs`:
    - `POST /api/integrations/github/auth/device-code` — initiates device flow
    - `POST /api/integrations/github/auth/poll` — polls for token
  **Acceptance**: Device flow works end-to-end

- [x] 49. **Create GitHub API proxy endpoints**
  **What**: Proxy GitHub API requests with stored token
  **Files**:
  - Create `src/WeaveFleet.Api/Endpoints/GitHubEndpoints.cs`:
    - `GET /api/integrations/github/repos` — list user repos
    - `GET /api/integrations/github/repos/{owner}/{repo}/issues` — list issues
    - `GET /api/integrations/github/repos/{owner}/{repo}/issues/search?q=` — search issues
    - `GET /api/integrations/github/repos/{owner}/{repo}/issues/{number}` — single issue
    - `GET /api/integrations/github/repos/{owner}/{repo}/issues/{number}/comments` — issue comments
    - `GET /api/integrations/github/repos/{owner}/{repo}/labels` — repo labels
    - `GET /api/integrations/github/repos/{owner}/{repo}/milestones` — repo milestones
    - `GET /api/integrations/github/repos/{owner}/{repo}/assignees` — repo assignees
    - `GET /api/integrations/github/repos/{owner}/{repo}/pulls` — list PRs
    - `GET /api/integrations/github/repos/{owner}/{repo}/pulls/{number}` — single PR
    - `GET /api/integrations/github/repos/{owner}/{repo}/pulls/{number}/comments` — PR comments
    - `GET /api/integrations/github/repos/{owner}/{repo}/pulls/{number}/status` — PR checks/status
    - `GET /api/integrations/github/bookmarks` — stored bookmarked repos
  - Modify `src/WeaveFleet.Api/Endpoints/EndpointExtensions.cs`:
    - Add `app.MapGitHubEndpoints();`
  **Acceptance**: GitHub API proxy works with stored token

- [x] 50. **Register GitHub services in DI**
  **What**: Wire up HttpClient, GitHubService, etc.
  **Files**:
  - Modify `src/WeaveFleet.Infrastructure/DependencyInjection.cs`:
    - Register `IIntegrationStore` → `FileIntegrationStore`
    - Add `services.AddHttpClient()` for GitHub API calls
  **Acceptance**: GitHub endpoints resolve dependencies correctly

---

### Phase 9: Frontend Conversion

**Goal:** Convert the Next.js app to a pure stateless SPA — delete all server-side code, rewire hooks to call the .NET API.

- [x] 51. **Delete server-side API routes**
  **What**: Remove entire `client/src/app/api/` directory
  **Files**:
  - Delete `client/src/app/api/` (72+ files)
  **Acceptance**: Directory no longer exists

- [x] 52. **Delete server-side library modules**
  **What**: Remove entire `client/src/lib/server/` directory
  **Files**:
  - Delete `client/src/lib/server/` (46 files including tests)
  **Acceptance**: Directory no longer exists

- [x] 53. **Delete CLI modules (if present and server-only)**
  **What**: Remove CLI modules that were only used by server-side code
  **Files**:
  - Check and delete `client/src/cli/` if it exists and is only used by server code
  - Verify no client-side imports reference these modules
  **Acceptance**: No broken imports

- [x] 54. **Remove server-only dependencies from package.json**
  **What**: Remove packages that are only needed for server-side functionality
  **Files**:
  - Modify `client/package.json`:
    - Remove from `dependencies`: `better-sqlite3`, `@opencode-ai/sdk`
    - Remove from `devDependencies`: `@types/better-sqlite3`
  - Run `npm install` to update lockfile
  **Acceptance**: `npm install` succeeds, no missing dependency errors

- [x] 55. **Simplify `next.config.ts`**
  **What**: Remove server-specific config since there are no API routes
  **Files**:
  - Modify `client/next.config.ts`:
    - Remove `serverExternalPackages: ["better-sqlite3"]` (no longer needed)
    - Consider always using `output: 'export'` since there's no server functionality
    - Keep `distDir: 'dist'`, image optimization disabled, turbopack root
  **Acceptance**: `next.config.ts` is simpler, no server-only config

- [x] 56. **Simplify `scripts/build-spa.mjs`**
  **What**: Since `src/app/api/` is deleted, the hide/restore dance is unnecessary
  **Files**:
  - Modify `client/scripts/build-spa.mjs`:
    - Remove the hidePairs logic (no api/ to hide)
    - Simplify to: clean dist + .next → run next build → done
    - Keep NEXT_BUILD_SPA=1 env var for next.config.ts
  **Acceptance**: `npm run build` works without the rename hack

- [x] 57. **Verify and update `api-client.ts`**
  **What**: Ensure the API client works correctly for split-mode and integrated mode. It should already work since it uses configurable base URL, but verify no changes needed.
  **Files**:
  - Review `client/src/lib/api-client.ts` — likely no changes needed
  - Ensure `.env.development.local` or similar sets `NEXT_PUBLIC_API_BASE_URL=http://localhost:3000` for split-mode dev
  **Acceptance**: `apiFetch("/api/sessions")` correctly resolves to .NET API in both modes

- [x] 58. **Add project hooks to frontend**
  **What**: Create new hooks for project management
  **Files**:
  - Create `client/src/hooks/use-projects.ts` — list/fetch projects
  - Create `client/src/hooks/use-create-project.ts` — create project
  - Create `client/src/hooks/use-update-project.ts` — update project
  - Create `client/src/hooks/use-delete-project.ts` — delete project
  - Create `client/src/hooks/use-reorder-project.ts` — reorder projects
  - Create `client/src/hooks/use-move-session.ts` — move session between projects
  **Acceptance**: Hooks compile, follow existing pattern (`apiFetch` + state management)

- [x] 59. **Verify all existing hooks still work**
  **What**: Review all 50 hooks to ensure they call `/api/*` paths that the .NET API now serves. Most should work unchanged since they already use `apiFetch`.
  **Files**:
  - Audit each hook in `client/src/hooks/`:
    - Hooks that fetch `/api/sessions`, `/api/fleet/summary`, etc. → should work (same paths)
    - Hooks that depend on removed server modules → fix imports
    - `use-session-events.ts` → verify SSE URL points to .NET endpoint
    - `use-weave-socket.ts` → verify WebSocket URL points to .NET endpoint
  - Fix any broken imports or path references
  **Acceptance**: `npm run typecheck` passes, all hooks compile

- [x] 60. **Ensure SPA build works end-to-end**
  **What**: Verify the static export produces a working SPA
  **Files**:
  - Run `cd client && npm run build` (which runs build-spa.mjs)
  - Verify `client/dist/index.html` exists
  - Verify no server-side code is referenced in the bundle
  **Acceptance**: `npm run build` succeeds, `dist/` contains valid SPA

---

### Phase 10: Cleanup & Verification

**Goal:** Remove all dead code, verify both dev and production modes work correctly.

- [x] 61. **Remove dead TypeScript code and imports**
  **What**: Clean up any remaining references to deleted server modules
  **Files**:
  - Grep for imports from `@/lib/server/`, `@/app/api/`, `@/cli/`
  - Remove or replace any found references
  - Remove any unused type exports from `api-types.ts`
  **Acceptance**: `npm run typecheck` and `npm run lint` pass clean

- [x] 62. **Remove dead .NET code**
  **What**: Clean up any unused using statements, commented-out EF Core references
  **Files**:
  - Verify no `using Microsoft.EntityFrameworkCore` anywhere
  - Verify no commented-out EF Core code in DependencyInjection.cs (should already be replaced)
  - Run `dotnet build` with warnings-as-errors (already configured in Directory.Build.props)
  **Acceptance**: `dotnet build` succeeds with zero warnings

- [x] 63. **Update dev workflow documentation**
  **What**: Ensure the dev workflow comments in Program.cs are still accurate
  **Files**:
  - Review `src/WeaveFleet.Api/Program.cs` header comments
  - Verify Mode A (integrated) and Mode B (split) instructions are correct
  - Update if any ports or commands changed
  **Acceptance**: Following the documented steps works

- [x] 64. **Create `.env.development.local` for split-mode dev**
  **What**: Ensure frontend split-mode dev has the correct API base URL configured
  **Files**:
  - Create or verify `client/.env.development.local`:
    ```
    NEXT_PUBLIC_API_BASE_URL=http://localhost:3000
    ```
  **Acceptance**: `npm run dev:split` connects to .NET API at localhost:3000

- [x] 65. **End-to-end smoke test — split mode**
  **What**: Verify the full system works with separate frontend and backend processes
  **Files**: N/A
  **Steps**:
  1. `dotnet run --project src/WeaveFleet.Api` — starts on :3000
  2. `cd client && npm run dev:split` — starts on :3001
  3. Open http://localhost:3001 — dashboard loads
  4. Verify: session list loads (empty initially), fleet summary shows zeros, project list shows "Scratch"
  5. Verify: WebSocket connects, no console errors
  **Acceptance**: Dashboard renders with real data from .NET API

- [x] 66. **End-to-end smoke test — integrated mode**
  **What**: Verify the single-binary mode works
  **Files**: N/A
  **Steps**:
  1. `cd client && npm run build` — builds SPA to dist/
  2. `dotnet run --project src/WeaveFleet.Api` — serves SPA + API on :3000
  3. Open http://localhost:3000 — dashboard loads
  4. Verify: same checks as split mode
  5. Verify: client-side routing works (navigate to /sessions/test → SPA handles it)
  **Acceptance**: Single-process mode serves SPA + API correctly

- [x] 67. **Run full test suites**
  **What**: Verify no regressions
  **Files**: N/A
  **Steps**:
  1. `dotnet test` — all .NET tests pass
  2. `cd client && npm test` — all frontend tests pass
  3. `cd client && npm run typecheck` — no type errors
  4. `cd client && npm run lint` — no lint errors
  **Acceptance**: All CI checks pass

---

## Verification

- [ ] `dotnet build` succeeds with zero warnings across all projects
- [ ] `dotnet test` passes all tests (Domain, Application, Infrastructure, Api)
- [ ] `cd client && npm run build` produces a working SPA
- [ ] `cd client && npm run typecheck` passes with no errors
- [ ] `cd client && npm test` passes all frontend tests
- [ ] No references to `Microsoft.EntityFrameworkCore` in any .NET file
- [ ] No references to `better-sqlite3` or `@opencode-ai/sdk` in any client file
- [ ] No imports from `@/lib/server/` or `@/app/api/` in client code
- [ ] `GET /api/fleet/summary` returns real data from SQLite
- [ ] `GET /api/projects` returns at least the Scratch project
- [ ] `GET /api/sessions` returns data with `projectId` field
- [ ] WebSocket at `/ws` broadcasts real events
- [ ] Split-mode dev works (frontend :3001 → backend :3000)
- [ ] Integrated mode works (SPA served from wwwroot on :3000)

---

## Appendix: File Impact Summary

### New Files (~50+)
**Domain layer (8):**
- `src/WeaveFleet.Domain/Entities/Project.cs`
- `src/WeaveFleet.Domain/Entities/Workspace.cs`
- `src/WeaveFleet.Domain/Entities/Instance.cs`
- `src/WeaveFleet.Domain/Entities/Session.cs`
- `src/WeaveFleet.Domain/Entities/SessionCallback.cs`
- `src/WeaveFleet.Domain/Entities/WorkspaceRoot.cs`
- `src/WeaveFleet.Domain/Repositories/IProjectRepository.cs`
- `src/WeaveFleet.Domain/Repositories/IWorkspaceRepository.cs`
- `src/WeaveFleet.Domain/Repositories/IInstanceRepository.cs`
- `src/WeaveFleet.Domain/Repositories/ISessionRepository.cs`
- `src/WeaveFleet.Domain/Repositories/ISessionCallbackRepository.cs`
- `src/WeaveFleet.Domain/Repositories/IWorkspaceRootRepository.cs`

**Application layer (12+):**
- `src/WeaveFleet.Application/Data/IDbConnectionFactory.cs`
- `src/WeaveFleet.Application/Services/ProjectService.cs`
- `src/WeaveFleet.Application/Services/SessionService.cs`
- `src/WeaveFleet.Application/Services/SessionOrchestrator.cs`
- `src/WeaveFleet.Application/Services/SessionCallbackService.cs`
- `src/WeaveFleet.Application/Services/WorkspaceService.cs`
- `src/WeaveFleet.Application/Services/InstanceService.cs`
- `src/WeaveFleet.Application/Services/InstanceTracker.cs`
- `src/WeaveFleet.Application/Services/WorkspaceRootService.cs`
- `src/WeaveFleet.Application/Services/ConfigService.cs`
- `src/WeaveFleet.Application/Services/DirectoryService.cs`
- `src/WeaveFleet.Application/Services/RepositoryService.cs`
- `src/WeaveFleet.Application/Services/GitHubService.cs`
- `src/WeaveFleet.Application/Services/GitHubApiProxy.cs`
- `src/WeaveFleet.Application/Services/IEventBroadcaster.cs`
- `src/WeaveFleet.Application/Services/BroadcastEvent.cs`
- `src/WeaveFleet.Application/Services/IIntegrationStore.cs`
- `src/WeaveFleet.Application/DTOs/ProjectDtos.cs`
- `src/WeaveFleet.Application/DTOs/SessionDtos.cs`
- `src/WeaveFleet.Application/DTOs/FleetSummaryDto.cs`

**Infrastructure layer (10+):**
- `src/WeaveFleet.Infrastructure/Data/SqliteConnectionFactory.cs`
- `src/WeaveFleet.Infrastructure/Data/MigrationRunner.cs`
- `src/WeaveFleet.Infrastructure/Data/Repositories/DapperProjectRepository.cs`
- `src/WeaveFleet.Infrastructure/Data/Repositories/DapperWorkspaceRepository.cs`
- `src/WeaveFleet.Infrastructure/Data/Repositories/DapperInstanceRepository.cs`
- `src/WeaveFleet.Infrastructure/Data/Repositories/DapperSessionRepository.cs`
- `src/WeaveFleet.Infrastructure/Data/Repositories/DapperSessionCallbackRepository.cs`
- `src/WeaveFleet.Infrastructure/Data/Repositories/DapperWorkspaceRootRepository.cs`
- `src/WeaveFleet.Infrastructure/Services/InMemoryEventBroadcaster.cs`
- `src/WeaveFleet.Infrastructure/Services/FileIntegrationStore.cs`
- `src/WeaveFleet.Infrastructure/Migrations/001_initial_schema.sql`

**API layer (6+):**
- `src/WeaveFleet.Api/Endpoints/ProjectEndpoints.cs`
- `src/WeaveFleet.Api/Endpoints/ResultExtensions.cs`
- `src/WeaveFleet.Api/Endpoints/WorkspaceEndpoints.cs`
- `src/WeaveFleet.Api/Endpoints/InstanceEndpoints.cs`
- `src/WeaveFleet.Api/Endpoints/SessionEventEndpoints.cs`
- `src/WeaveFleet.Api/Endpoints/SkillEndpoints.cs`
- `src/WeaveFleet.Api/Endpoints/GitHubAuthEndpoints.cs`
- `src/WeaveFleet.Api/Endpoints/GitHubEndpoints.cs`
- `src/WeaveFleet.Api/Endpoints/ActivityStreamEndpoints.cs` (or in FleetEndpoints)
- `src/WeaveFleet.Api/Endpoints/OpenDirectoryEndpoints.cs`

**Frontend (6+):**
- `client/src/hooks/use-projects.ts`
- `client/src/hooks/use-create-project.ts`
- `client/src/hooks/use-update-project.ts`
- `client/src/hooks/use-delete-project.ts`
- `client/src/hooks/use-reorder-project.ts`
- `client/src/hooks/use-move-session.ts`

### Modified Files (~15)
- `Directory.Packages.props` — swap EF Core → Dapper + Microsoft.Data.Sqlite
- `src/WeaveFleet.Infrastructure/WeaveFleet.Infrastructure.csproj` — swap packages
- `src/WeaveFleet.Infrastructure/DependencyInjection.cs` — register all new services
- `src/WeaveFleet.Api/Program.cs` — startup: migrations, scratch project, recovery
- `src/WeaveFleet.Api/Endpoints/EndpointExtensions.cs` — wire new endpoint groups
- `src/WeaveFleet.Api/Endpoints/SessionEndpoints.cs` — replace stubs with real
- `src/WeaveFleet.Api/Endpoints/FleetEndpoints.cs` — replace stubs with real
- `src/WeaveFleet.Api/Endpoints/ConfigEndpoints.cs` — replace stub with real
- `src/WeaveFleet.Api/Endpoints/DirectoryEndpoints.cs` — replace stub with real
- `src/WeaveFleet.Api/Endpoints/WorkspaceRootEndpoints.cs` — replace stubs with real
- `src/WeaveFleet.Api/Endpoints/WebSocketEndpoints.cs` — replace stub with real
- `client/src/lib/api-types.ts` — add Project types
- `client/package.json` — remove server-only deps
- `client/next.config.ts` — simplify
- `client/scripts/build-spa.mjs` — simplify

### Deleted Files (~120+)
- `client/src/app/api/**/*` — 72+ route files
- `client/src/lib/server/**/*` — 46 files (including tests)
