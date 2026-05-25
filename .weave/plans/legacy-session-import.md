# Legacy Weave Agent Fleet Session Import

## TL;DR
> **Summary**: Import sessions from the legacy `weave-agent-fleet` SQLite DB (`~/.weave/fleet.db`) into the new Weave Fleet DB, auto-triggering on fresh DBs and available as an explicit CLI command otherwise.
> **Estimated Effort**: Medium

## Context
### Original Request
Migrate session data from the legacy Weave Agent Fleet (TypeScript/better-sqlite3, `~/.weave/fleet.db`) into the new .NET-based Weave Fleet (SQLite via Microsoft.Data.Sqlite, `~/Library/Application Support/WeaveFleet/fleet.db`).

### Key Findings
- **Legacy DB schema** (`~/.weave/fleet.db`): tables `workspaces`, `instances`, `sessions`, `session_callbacks`, `workspace_roots`. Sessions have statuses: `active|idle|stopped|completed|disconnected|error|waiting_input`. Columns: `id, workspace_id, instance_id, opencode_session_id, title, status, directory, created_at, stopped_at, parent_session_id, activity_status, lifecycle_status, total_tokens, total_cost`.
- **New DB schema**: Same core tables plus `projects` (with `user_id`), `users`. Sessions gain `project_id, retention_status, archived_at, is_hidden, harness_type, harness_resume_token, user_id, selected_provider_id, selected_model_id`.
- **New DB location**: `FleetPaths.DefaultAppDataDirectory` = `~/Library/Application Support/WeaveFleet/fleet.db` (macOS).
- **Existing pattern**: `LegacyDataMigrator` in `WeaveFleet.Application.Configuration` handles file-level DB relocation on startup. The new importer follows a similar startup-hook pattern.
- **CLI**: `WeaveFleet.Cli/Program.cs` is a placeholder; the API project (`WeaveFleet.Api/Program.cs`) is the real entry point. CLI command can be added as a System.CommandLine verb or as an API endpoint callable from CLI.
- **DI/Repos**: `SessionRepository`, `ProjectRepository`, `IDbConnectionFactory`, `MigrationRunner` are all in `WeaveFleet.Infrastructure`.
- **User context**: Sessions require `user_id`. For local (non-auth) mode, a default local user is used.

## Objectives
### Core Objective
Provide a safe, transactional, idempotent import of legacy sessions into the new Fleet DB.

### Deliverables
- [x] `LegacySessionImporter` service class
- [x] `legacy_imports` marker table (migration)
- [x] Auto-import logic on startup (empty DB detection)
- [x] Explicit CLI/API endpoint for non-empty DBs
- [x] Status normalization (active/idle/waiting_input -> stopped)
- [x] Integration tests

### Definition of Done
- [x] `dotnet test` passes all new import tests
- [x] `dotnet build -c Release` succeeds with no warnings in new files
- [x] Auto-import works on fresh DB with legacy data present
- [x] Explicit import works on populated DB
- [x] Duplicate import is a no-op

### Guardrails (Must NOT)
- Must NOT revive legacy sessions (all become `stopped`)
- Must NOT modify the legacy DB (read-only access)
- Must NOT import profile DBs (phase 1 = default path only)
- Must NOT silently overwrite data on ID collision (abort with message)

## TODOs

- [x] 1. Add `legacy_imports` marker table migration
  **What**: New SQL migration `023_add_legacy_imports_table.sql` creating a table to track import runs: `id TEXT PK, source_path TEXT, imported_at TEXT, session_count INTEGER, status TEXT`.
  **Files**: `src/WeaveFleet.Infrastructure/Migrations/023_add_legacy_imports_table.sql`
  **Acceptance**: Migration applies cleanly; table exists after `MigrationRunner.ApplyMigrationsAsync()`.

- [x] 2. Create `LegacySessionImporter` service
  **What**: New class in `WeaveFleet.Application.Services` (interface) + `WeaveFleet.Infrastructure.Services` (implementation). Responsibilities:
  - Open legacy DB read-only at `~/.weave/fleet.db`
  - Read all rows from `sessions`, `workspaces`, `instances`
  - Create "Imported Legacy Sessions" project (type=`"user"`) if not exists
  - Create a sentinel workspace + instance for imported sessions (since legacy instances are dead)
  - Map legacy sessions to new `Session` entity with status normalization
  - Insert in a single transaction; on any ID collision, abort and rollback
  - Write a row to `legacy_imports` on success
  - Idempotency: check `legacy_imports` table before starting; skip if already imported from same source path
  **Files**: `src/WeaveFleet.Application/Services/ILegacySessionImporter.cs`, `src/WeaveFleet.Infrastructure/Services/LegacySessionImporter.cs`
  **Acceptance**: Unit-testable with in-memory SQLite; imports N sessions and records marker.

- [x] 3. Status normalization logic
  **What**: Within the importer, map legacy statuses to new values:
  - `active`, `idle`, `waiting_input` -> `status = "stopped"`, `lifecycle_status = "completed"`, `activity_status = null`
  - `stopped`, `completed`, `error`, `disconnected` -> keep as-is (already terminal)
  - Set `stopped_at` to import timestamp if null and status was active/idle
  **Files**: `src/WeaveFleet.Infrastructure/Services/LegacySessionImporter.cs` (internal method)
  **Acceptance**: Test verifies each legacy status maps correctly.

- [x] 4. Auto-import on startup
  **What**: Add a startup hook (hosted service or inline in `Program.cs` after migrations) that:
  1. Checks if sessions table is empty (SELECT COUNT)
  2. Checks if `~/.weave/fleet.db` exists
  3. If both true, runs `LegacySessionImporter.ImportAsync()`
  4. If sessions table has data AND legacy DB exists, log info: "Legacy sessions detected at ~/.weave/fleet.db. Use `import-legacy-sessions` to import explicitly."
  **Files**: `src/WeaveFleet.Infrastructure/Services/LegacySessionImportStartupService.cs`, `src/WeaveFleet.Api/Program.cs` (register hosted service)
  **Acceptance**: Fresh DB auto-imports; populated DB logs notification only.

- [x] 5. Explicit CLI/API command
  **What**: Add an API endpoint `POST /api/admin/import-legacy-sessions` (or System.CommandLine verb in CLI project) that invokes the importer regardless of DB state. Returns import result (count, skipped, errors). Phase 1: abort on ID collision with clear error message.
  **Files**: `src/WeaveFleet.Api/Endpoints/AdminEndpoints.cs` (new file or extend existing)
  **Acceptance**: Calling endpoint on populated DB imports sessions; calling twice is idempotent.

- [x] 6. Register services in DI
  **What**: Register `ILegacySessionImporter` and the startup hosted service in `DependencyInjection.cs`.
  **Files**: `src/WeaveFleet.Infrastructure/DependencyInjection.cs`
  **Acceptance**: Services resolve from DI container.

- [x] 7. Integration tests
  **What**: Test project `tests/WeaveFleet.Infrastructure.Tests` (or new test class). Tests:
  1. Empty DB auto-import: seeds legacy DB, verifies sessions imported into "Imported Legacy Sessions" project
  2. Existing DB explicit import: pre-populate new DB, call importer, verify sessions added
  3. Failed import rollback: simulate ID collision, verify no partial data
  4. Duplicate import prevention: import twice, verify idempotent (marker check)
  5. Status normalization: verify each legacy status maps correctly
  6. ID collision abort: insert conflicting ID, verify abort with message
  **Files**: `tests/WeaveFleet.Infrastructure.Tests/Services/LegacySessionImporterTests.cs`
  **Acceptance**: All 6 test scenarios pass.

## Architecture / Design

```
Startup (Program.cs)
  -> MigrationRunner (applies 023_add_legacy_imports_table.sql)
  -> LegacySessionImportStartupService (IHostedService.StartAsync)
       -> Check: sessions table empty?
       -> Check: ~/.weave/fleet.db exists?
       -> Yes/Yes: LegacySessionImporter.ImportAsync(legacyDbPath, autoMode: true)
       -> Yes/No: no-op
       -> No/Yes: log "use explicit import"
       -> No/No: no-op

LegacySessionImporter.ImportAsync(legacyDbPath, autoMode)
  1. Check legacy_imports for existing record with same source_path -> skip
  2. Open legacy DB read-only (new SqliteConnection, immutable=1)
  3. Read sessions, workspaces, instances
  4. Begin transaction on new DB
  5. Ensure "Imported Legacy Sessions" project exists (INSERT OR IGNORE)
  6. Create sentinel workspace (id="legacy-import-workspace") + instance (id="legacy-import-instance", status=stopped)
  7. For each legacy session:
     - Check ID collision (SELECT EXISTS)
     - If collision: rollback, throw/return error
     - Normalize status
     - Map to Session entity (project_id=import project, workspace_id=sentinel, instance_id=sentinel, user_id=local user, harness_type="opencode")
     - INSERT
  8. Insert legacy_imports marker row
  9. Commit transaction
```

## Data Mapping

| Legacy Column | New Column | Transformation |
|---|---|---|
| id | Id | Direct |
| workspace_id | WorkspaceId | Remap to sentinel `"legacy-import-workspace"` |
| instance_id | InstanceId | Remap to sentinel `"legacy-import-instance"` |
| — | ProjectId | Set to import project ID |
| opencode_session_id | OpencodeSessionId | Direct |
| title | Title | Direct |
| status | Status | Normalize (active/idle/waiting_input -> stopped) |
| directory | Directory | Direct |
| created_at | CreatedAt | Direct |
| stopped_at | StoppedAt | Direct or set to import time if normalizing |
| parent_session_id | ParentSessionId | Direct (may be null if parent not imported) |
| activity_status | ActivityStatus | Null (dead session) |
| lifecycle_status | LifecycleStatus | "completed" for normalized, keep for already-terminal |
| total_tokens | TotalTokens | Direct |
| total_cost | TotalCost | Direct |
| — | RetentionStatus | "active" |
| — | ArchivedAt | null |
| — | IsHidden | false |
| — | HarnessType | "opencode" |
| — | HarnessResumeToken | null |
| — | UserId | Local default user ID |

## Verification
- [x] `dotnet build -c Release` — no errors/warnings
- [x] `dotnet test --filter "LegacySessionImporter"` — all pass
- [x] Manual: delete new DB, place legacy DB at `~/.weave/fleet.db`, start Fleet, verify sessions appear under "Imported Legacy Sessions" project
- [x] Manual: with existing data, start Fleet, check logs for import notification
- [x] Manual: call `POST /api/admin/import-legacy-sessions`, verify import

## Risks / Open Questions
1. **Legacy DB may have FK to workspaces/instances that don't exist in new DB** — mitigated by using sentinel workspace/instance rather than importing legacy workspaces/instances (they're meaningless in new Fleet).
2. **Parent session references** — if `parent_session_id` references a session that wasn't imported (unlikely since we import all), it will be a dangling FK. Accept this; the field is nullable and non-enforced.
3. **User ID for local mode** — need to confirm what the default local user ID is. Check `IUserContext` implementation for unauthenticated mode.
4. **Large legacy DBs** — if thousands of sessions, single transaction may be slow. Acceptable for phase 1.
5. **Legacy DB locked by running legacy Fleet** — open with `Mode=ReadOnly;Immutable=True` to avoid lock contention.
6. **Migration numbering** — verify `023` is the next available number (check for conflicts with in-flight PRs).

## Suggested Commands to Verify
```bash
# Build
dotnet build -c Release src/WeaveFleet.Infrastructure/WeaveFleet.Infrastructure.csproj

# Run specific tests
dotnet test tests/WeaveFleet.Infrastructure.Tests --filter "LegacySessionImporter"

# Full test suite
dotnet test

# Manual smoke test
rm -f ~/Library/Application\ Support/WeaveFleet/fleet.db
dotnet run --project src/WeaveFleet.Api
# Check logs for "Imported X legacy sessions"
```
