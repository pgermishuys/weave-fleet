# Move Migrations To DbUp

## Summary

Replace the custom embedded-SQL migration runner with DbUp for both the main SQLite database and the analytics SQLite database.

Decision: move to DbUp as the only migration system, while migrating old custom-runner databases onto DbUp with a small one-time bridge. Do not keep two active migration systems. The bridge should seed DbUp's journal from `_migrations`, preserve the existing known repair behavior, then let DbUp own all future migrations.

## Current State

- `src/WeaveFleet.Infrastructure/Data/MigrationRunner.cs` discovers embedded `.sql` resources under the exact dotted resource segment `Migrations`, executes them in ordinal order, and records migration file names in `_migrations`.
- `src/WeaveFleet.Infrastructure/Data/AnalyticsMigrationRunner.cs` adapts `IAnalyticsDbConnectionFactory` and reuses `MigrationRunner` with the `AnalyticsMigrations` segment.
- `src/WeaveFleet.Api/Program.cs` runs the main migration runner first, then the analytics migration runner when analytics is enabled.
- `src/WeaveFleet.Infrastructure/DependencyInjection.cs` registers `MigrationRunner` and, when enabled, `AnalyticsMigrationRunner` as singletons.
- SQL files are embedded via `src/WeaveFleet.Infrastructure/WeaveFleet.Infrastructure.csproj`:
  - Main: `src/WeaveFleet.Infrastructure/Migrations/001_initial_schema.sql` through `016_add_board_sources.sql`.
  - Analytics: `src/WeaveFleet.Infrastructure/AnalyticsMigrations/001_analytics_initial.sql` through `003_add_user_id.sql`.
- `_migrations` schema is `id INTEGER PRIMARY KEY AUTOINCREMENT`, `name TEXT NOT NULL UNIQUE`, `applied_at TEXT NOT NULL DEFAULT (datetime('now'))`.
- Existing repair behavior in `MigrationRunner`:
  - If `_migrations` has entries but `projects` is missing, clear `_migrations` and rerun all migrations.
  - If `_migrations` is empty but legacy `workspaces` exists, drop known incompatible legacy tables and rerun.
- Connection factories are responsible for SQLite pragmas:
  - Main DB: WAL, busy timeout, foreign keys on.
  - Analytics DB: WAL, busy timeout, no foreign key pragma.
- Tests currently assert custom runner behavior in `tests/WeaveFleet.Infrastructure.Tests/Data/MigrationRunnerTests.cs` and use the runner from `TestDbHelper` and `AnalyticsTestDbHelper`.

## Goals

- Use DbUp for embedded SQL migrations in both databases.
- Keep migration startup behavior simple and explicit.
- Preserve ordered embedded resource execution and per-script transactions.
- Keep main and analytics migrations isolated from each other.
- Preserve SQLite connection pragma behavior by continuing to use the existing connection factories.
- Preserve old custom-runner databases during upgrade without keeping the legacy runner as an active migration engine.

## Non-Goals

- Do not rewrite existing SQL migrations unless DbUp parsing exposes a concrete incompatibility.
- Do not introduce a new migration file naming convention as part of the cutover.
- Do not support multiple active migration journals indefinitely.
- Do not implement rollback/down migrations; DbUp is forward-only for this use case.

## DbUp Shape

Add DbUp package references centrally and to infrastructure:

- `Directory.Packages.props`: add `DbUp` package version.
- `src/WeaveFleet.Infrastructure/WeaveFleet.Infrastructure.csproj`: add `PackageReference Include="DbUp"`.

Create DbUp-backed runners, preferably keeping the current public runner names so startup and tests have a minimal blast radius:

- `MigrationRunner` becomes a DbUp wrapper for the main database.
- `AnalyticsMigrationRunner` becomes a DbUp wrapper for analytics.

Suggested shared implementation shape:

- A private/internal helper method builds a `DeployChanges` pipeline from an already-created `IDbConnection` or connection string compatible with DbUp SQLite.
- Configure embedded scripts from `typeof(MigrationRunner).Assembly` with a resource-name filter:
  - Main filter: resource ends in `.sql` and has exact dotted segment `Migrations`.
  - Analytics filter: resource ends in `.sql` and has exact dotted segment `AnalyticsMigrations`.
- Ensure scripts run in lexical resource order. DbUp usually orders scripts by name, but keep tests around this and explicitly sort/filter if the API path chosen allows it.
- Use per-script transactions to match current behavior.
- Use separate journal tables for main and analytics databases. Because they are different database files, the default `SchemaVersions` table can work for both, but explicit names improve clarity:
  - Main: `SchemaVersions` or `schema_versions`.
  - Analytics: `SchemaVersions` or `analytics_schema_versions`.
- Use DbUp logging integration or a small adapter to `ILogger` so startup logs retain enough migration visibility.

Important implementation detail: prefer passing an opened connection from the existing factories if DbUp's SQLite API supports it cleanly. If DbUp requires a connection string, expose or derive connection strings carefully and verify pragmas are still applied for the migration connection. The existing factories are the source of desired SQLite behavior.

## Option A: Legacy Bridge From `_migrations` To DbUp

### What It Does

Before invoking DbUp for a database, inspect legacy `_migrations` and seed DbUp's journal with equivalent script names so DbUp does not rerun migrations that were already applied by the custom runner.

The bridge should run once and then get out of the way:

1. Ensure DbUp journal exists or let DbUp create it.
2. If DbUp journal has entries, do nothing; DbUp already owns this database.
3. If `_migrations` does not exist, do nothing; this is a fresh DbUp database.
4. If `_migrations` exists and contains all expected legacy names for the current embedded scripts, insert matching DbUp journal rows for those scripts.
5. If `_migrations` exists but state is known-bad or incomplete, run the existing repair logic before DbUp or fail with a clear message.

### Complexity

- Need to know the exact script name DbUp stores for embedded resources. The custom runner stores only file names like `001_initial_schema.sql`; DbUp commonly journals full script names like `WeaveFleet.Infrastructure.Migrations.001_initial_schema.sql` unless configured otherwise.
- Need a reliable mapping between `_migrations.name` and DbUp script names.
- Need to preserve existing repair behavior without duplicating too much of `MigrationRunner`.
- Need to decide what to do with partially applied legacy databases:
  - If `_migrations` has `001` through `010`, DbUp can seed only those and then apply `011+`, but only if script-name mapping is exact.
  - If `_migrations` has entries but required tables are missing, keep the current poisoned-state repair behavior.
  - If `_migrations` is empty but legacy `workspaces` exists, keep the current drop-legacy-tables behavior before DbUp runs.
- Need tests for all bridge states for both main and analytics, though analytics has less legacy repair behavior documented.

### Pros

- Preserves existing user databases from custom-runner versions.
- Allows users on any already-migrated state to upgrade without destructive schema rebuilds.
- Retains current compatibility repairs for known legacy/poisoned states.

### Cons

- Adds migration code whose only purpose is migrating the migration system.
- Increases risk around DbUp journal internals and script-name mismatches.
- Makes tests more complex and can obscure the new steady-state path.
- Keeps `_migrations` knowledge in the codebase after moving to DbUp.

### If Chosen, Keep It Small

- Implement the bridge as an internal one-time `LegacyMigrationJournalBridge` with no public surface.
- Restrict it to startup before DbUp runs.
- Do not maintain `_migrations` after cutover.
- Do not attempt clever schema inference beyond the existing repair checks.
- Add a clear TODO or removal target after one or two releases if this is a distributed product.

## Option B: Fresh DbUp Only

### What It Does

Remove custom `_migrations` handling and let DbUp own migration state for all databases going forward. Existing databases with no DbUp journal are treated as unsupported for in-place upgrade unless manually migrated or recreated.

### Complexity

- Straightforward replacement of runner implementation.
- No mapping from `_migrations` to DbUp journal.
- No legacy repair behavior unless deliberately retained outside the journal bridge.

### Pros

- Smallest code change and easiest to reason about.
- Avoids long-term compatibility scaffolding.
- Clean DbUp mental model from day one.
- Lower test burden and lower migration cutover risk for new installs.

### Cons

- Existing custom-runner databases can fail because DbUp will try to run already-applied `CREATE TABLE` or `ALTER TABLE` scripts.
- Dropping support for old local data may surprise users if this repo has released versions with persistent databases.
- The current legacy repair behavior would be removed unless explicitly ported.

## Decision

Use DbUp for all future migrations and include a bounded compatibility bridge for old databases.

This is effectively: **Option B steady state, plus a one-time legacy journal migration**. Old installs are migrated onto DbUp; new installs use DbUp directly; no release should continue applying migrations through the old custom runner.

Recommended implementation:

1. Implement fresh DbUp runners.
2. Before DbUp runs, invoke a small `LegacyMigrationJournalBridge` when `_migrations` exists and DbUp's journal is empty.
3. Seed DbUp's journal from `_migrations` by mapping legacy file names to embedded DbUp script names.
4. Preserve the two existing known repairs before seeding:
   - `_migrations` has entries but `projects` is missing: clear legacy journal, then let DbUp rebuild from scripts.
   - `_migrations` is empty but legacy `workspaces` exists: drop known incompatible legacy tables, then let DbUp rebuild from scripts.
5. Fail clearly on unknown or unmappable legacy migration names rather than guessing.

This preserves existing work without keeping the old runner around. The extra code is limited to startup journal conversion and can be removed after a compatibility window if desired.

## Implementation Steps

### Phase 1: Dependency And Runner Skeleton

1. Add DbUp package version to `Directory.Packages.props`.
2. Add DbUp reference to `src/WeaveFleet.Infrastructure/WeaveFleet.Infrastructure.csproj`.
3. Replace `MigrationRunner` internals with a DbUp pipeline while preserving `ApplyMigrationsAsync()` and `ApplyMigrationsAsync(IDbConnection)` if tests still benefit from direct in-memory connections.
4. Replace `AnalyticsMigrationRunner` internals with either its own DbUp pipeline or a shared internal helper parameterized by connection factory, logger, resource segment, and journal table.
5. Keep DI registrations in `DependencyInjection.cs` mostly unchanged if public runner names remain.
6. Keep startup order in `Program.cs`: main DB first, analytics second.

### Phase 2: Resource Filtering And Journaling

1. Implement exact dotted-segment resource filtering equivalent to the current behavior.
2. Verify DbUp stores stable script names. Prefer full embedded resource names if that is DbUp's natural behavior.
3. Choose journal table names and document them in code:
   - Recommended: accept DbUp default `SchemaVersions` unless analytics and main can ever share a database file.
   - If explicit names are cheap, use `schema_versions` for main and `analytics_schema_versions` for analytics.
4. Configure per-script transactions.
5. Wire DbUp logging to `ILogger<MigrationRunner>` or equivalent.

### Phase 3: Legacy Journal Bridge

1. Extract the existing legacy repair checks before replacing the runner:
   - `_migrations` entries with missing `projects`: clear legacy journal.
   - Empty `_migrations` with legacy `workspaces`: drop known incompatible legacy tables.
2. Add `LegacyMigrationJournalBridge` for main DB.
3. Bridge only when DbUp journal is empty and `_migrations` exists.
4. Map legacy file names to DbUp script names by matching embedded resource suffixes.
5. Seed only scripts present in `_migrations`; then let DbUp apply remaining scripts.
6. If a legacy migration name cannot be mapped to an embedded resource, fail clearly rather than guessing.
7. Add the same bridge shape for analytics only if analytics databases have shipped and should be preserved. Otherwise, document analytics as fresh-DbUp-only/disposable and allow recreation.
8. Do not delete `_migrations` during the bridge. Leave it unused for rollback and diagnostics.

### Phase 4: Test Updates

1. Update `MigrationRunnerTests` to assert DbUp behavior rather than `_migrations` behavior:
   - All main tables are created.
   - Analytics tables are not created by the main runner.
   - Analytics runner creates analytics tables and not main tables.
   - Re-running migrations is idempotent.
   - Migrations run in order.
   - DbUp journal has entries.
2. Replace `_migrations` count assertions with DbUp journal assertions.
3. Update helper tests that manually apply migrations through a specific point. Prefer using embedded SQL directly and seeding the DbUp journal in the same way DbUp records scripts, or split old-version simulation into a dedicated helper.
4. Update `TestDbHelper` and `AnalyticsTestDbHelper` only as needed if runner method signatures change.
5. Add bridge-specific tests:
    - Fully migrated legacy `_migrations` seeds DbUp journal and does not rerun scripts.
    - Partially migrated legacy DB seeds applied scripts and applies only remaining scripts.
    - Poisoned `_migrations` with missing `projects` follows chosen repair behavior.
    - Empty `_migrations` with legacy `workspaces` follows chosen repair behavior.
    - Unknown legacy migration name fails clearly.
6. Add fresh DbUp tests:
   - Fresh database migrates successfully and creates DbUp journal.
   - Re-running against a DbUp-owned database does not invoke the legacy bridge.

### Phase 5: Verification

1. Run `dotnet test tests/WeaveFleet.Infrastructure.Tests/WeaveFleet.Infrastructure.Tests.csproj`.
2. Run `dotnet test` at solution level if time allows.
3. Run the API locally against a temporary database and confirm startup migrations complete.
4. For the legacy bridge, manually create a small legacy database with `_migrations` and verify startup converts it once and then DbUp owns subsequent runs.

## Rollback And Compatibility

- DbUp creates a different journal table than `_migrations`. Once a database starts with DbUp, reverting the application to a custom-runner version can cause old code to ignore DbUp's journal and rely on stale `_migrations` state.
- Keep `_migrations` untouched during the bridge if rollback to the previous binary is a concern. Seeding DbUp's journal without deleting `_migrations` lets old code still see its prior journal state.
- Do not drop `_migrations` during the initial DbUp cutover. It is harmless if unused and useful for rollback/diagnostics.
- SQLite DDL rollback behavior should be verified with DbUp per-script transactions. Some SQLite schema operations can have edge cases depending on transaction boundaries.
- Back up real user database files before first release that switches migration systems.

## Tradeoffs

- Simplicity vs preservation: fresh DbUp alone is simpler, but the chosen bridge keeps existing data and limits complexity to startup journal conversion.
- Explicit guard vs automatic bridge: a guard is easier, but the chosen automatic bridge provides a better upgrade path for old installs.
- Default journal names vs explicit names: DbUp defaults are conventional, but explicit names reduce ambiguity when there are two database domains.
- Keeping runner class names vs renaming to DbUp-specific names: keeping names minimizes DI/startup churn; renaming makes the implementation obvious but touches more files.
- Preserving repair behavior vs deleting it: repair behavior protects known old bad states, but carrying it forward keeps legacy complexity alive.

## Open Questions

- Is analytics data considered durable user data requiring the same bridge, or disposable telemetry/cache data that can be recreated?
- Which DbUp SQLite package/API version should be used, and does it support opened `IDbConnection` cleanly for preserving factory-applied pragmas?
- What exact journal table name should be standardized for main and analytics databases?
- Should `_migrations` remain forever as an ignored legacy table, or should a future cleanup remove it after a compatibility window?
