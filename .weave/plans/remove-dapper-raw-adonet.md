# Remove Dapper — Replace with Raw ADO.NET

## TL;DR
> **Summary**: Remove all Dapper usage from `src/` and replace with raw ADO.NET (`DbCommand` + `DbDataReader`) to eliminate NativeAOT `PlatformNotSupportedException` caused by Dapper's runtime reflection fallback.
> **Estimated Effort**: Large

## Context
### Original Request
Dapper + Dapper.AOT silently falls back to runtime Dapper (Reflection.Emit) for dynamic SQL, interpolated strings, tuples, and DynamicParameters — causing `PlatformNotSupportedException` under NativeAOT. Decision: remove Dapper entirely from `src/`, replace with raw ADO.NET.

### Key Findings
- 15 files in `src/` use Dapper (14 repositories + 1 event store)
- `AnalyticsRepository` (354 lines) uses Dapper with interpolated SQL and tuple returns
- `InProcessEventStore` already partially uses raw ADO.NET (`ReadPending`) but `Append` and `MarkDispatched` still use Dapper
- `SqlInExpander` uses `DynamicParameters` — needs rewrite for `DbCommand`
- `DependencyInjection.cs` sets `DefaultTypeMap.MatchNamesWithUnderscores = true` — must be removed
- `DapperAotAssembly.cs` contains assembly-level Dapper attributes — must be deleted
- All entities use simple types: `string`, `string?`, `int`, `long`, `double`, `bool`
- `IDbConnectionFactory.CreateConnection()` returns `IDbConnection` but actual type is `SqliteConnection` (extends `DbConnection`)
- `BoardRepository` is the largest at 867 lines with complex transaction/rebalance logic
- `DapperUserRepository` also exists (missed in original scope) — 46 lines, must be included
- Column naming uses `snake_case` in DB, `PascalCase` in C# — mapper methods must handle this

## Objectives
### Core Objective
Eliminate all Dapper dependencies from production code so NativeAOT publish works without reflection fallback.

### Deliverables
- [ ] `DbCommandHelper.cs` — shared extension methods for raw ADO.NET queries
- [ ] Rewritten `SqlInExpander.cs` using `DbCommand` instead of `DynamicParameters`
- [ ] All 15 repository/store files converted to raw ADO.NET
- [ ] `DapperAotAssembly.cs` deleted
- [ ] Dapper packages removed from csproj
- [ ] `DependencyInjection.cs` cleaned of Dapper config

### Definition of Done
- [ ] `dotnet build src/WeaveFleet.Infrastructure/WeaveFleet.Infrastructure.csproj` succeeds with no Dapper references
- [ ] `dotnet test` passes (tests still use Dapper in JIT mode via their own references)
- [ ] `grep -r "using Dapper" src/` returns no results

### Guardrails (Must NOT)
- Do NOT change test code (tests use Dapper in JIT mode — fine)
- Do NOT change `IDbConnectionFactory` interface
- Do NOT add new NuGet packages
- Do NOT rename repository class names or files
- Do NOT change any SQL queries — only the C#/Dapper plumbing around them
- Preserve all existing behavior exactly

## TODOs

- [x] 1. Create `DbCommandHelper.cs`
  **What**: Create a static helper class with extension methods that replace Dapper's API surface. Methods:
  - `AddParameter(this DbCommand cmd, string name, object? value)` — adds a `DbParameter`
  - `QueryAsync<T>(this DbConnection conn, string sql, Action<DbCommand>? configureParams, Func<DbDataReader, T> mapper, CancellationToken ct = default)` — opens conn if needed, executes reader, maps rows
  - `QueryFirstOrDefaultAsync<T>(...)` — same but returns first row or default
  - `ExecuteNonQueryAsync(this DbConnection conn, string sql, Action<DbCommand>? configureParams, CancellationToken ct = default)` — returns int rows affected
  - `ExecuteScalarAsync<T>(this DbConnection conn, string sql, Action<DbCommand>? configureParams, CancellationToken ct = default)` — returns scalar
  - Overloads that accept `IDbConnection` + `IDbTransaction?` (cast to `DbConnection`/`DbTransaction` internally) for methods that take a connection+transaction pair
  - Synchronous variants `Query<T>`, `ExecuteScalar<T>`, `ExecuteNonQuery` for `InProcessEventStore`
  - All methods must handle `conn.State != Open` by calling `OpenAsync()`/`Open()`
  **Files**: `src/WeaveFleet.Infrastructure/Data/DbCommandHelper.cs`
  **Acceptance**: File compiles; provides the API surface needed by all repositories

- [x] 2. Rewrite `SqlInExpander.cs`
  **What**: Change signature from `AppendInClause<T>(StringBuilder sql, DynamicParameters parameters, string prefix, IReadOnlyList<T> values)` to `AppendInClause<T>(StringBuilder sql, DbCommand cmd, string prefix, IReadOnlyList<T> values)`. Use `cmd.AddParameter(...)` (from helper) instead of `parameters.Add(...)`. Remove `using Dapper;`.
  **Files**: `src/WeaveFleet.Infrastructure/Data/SqlInExpander.cs`
  **Acceptance**: Compiles without Dapper reference; same SQL output shape

- [x] 3. Convert `DapperSessionRepository.cs`
  **What**: Replace all `conn.ExecuteAsync(sql, new {...})`, `conn.QueryAsync<Session>(...)`, `conn.QueryFirstOrDefaultAsync<Session>(...)`, `conn.ExecuteScalarAsync<int>(...)` calls with `DbCommandHelper` equivalents. Add `private static Session MapSession(DbDataReader r)` method mapping all 23 properties using ordinal-based reads with `IsDBNull` checks for nullable columns. For `GetStatusCountsAsync` (returns tuples), use a custom inline mapper. For `GetFleetTokenTotalsAsync` and `IncrementTokensAsync` (return tuples), map inline. Replace `DynamicParameters` usage with `DbCommand` parameter configuration via lambdas. Remove `using Dapper;`.
  **Files**: `src/WeaveFleet.Infrastructure/Data/Repositories/DapperSessionRepository.cs`
  **Acceptance**: Compiles; all 23 methods use raw ADO.NET

- [ ] 4. Convert `DapperSessionSourceUsageRepository.cs`
  **What**: Replace Dapper calls with `DbCommandHelper`. Add `private static SessionSourceUsage MapSessionSourceUsage(DbDataReader r)` for 11 properties. Replace `DynamicParameters` + `SqlInExpander` call in `GetPrimaryBySessionIdsAsync` with `DbCommand`-based equivalent.
  **Files**: `src/WeaveFleet.Infrastructure/Data/Repositories/DapperSessionSourceUsageRepository.cs`
  **Acceptance**: Compiles; remove `using Dapper;`

- [ ] 5. Convert `DapperOutboxRepository.cs`
  **What**: Replace Dapper calls. Add `private static OutboxMessage MapOutboxMessage(DbDataReader r)` for 8 properties (note: `Id` is `long`). Replace `DynamicParameters` + `SqlInExpander` in `MarkDispatchedAsync` with `DbCommand`-based equivalent.
  **Files**: `src/WeaveFleet.Infrastructure/Data/Repositories/DapperOutboxRepository.cs`
  **Acceptance**: Compiles; remove `using Dapper;`

- [ ] 6. Convert `DapperMessageRepository.cs`
  **What**: Replace Dapper calls. Add `private static PersistedMessage MapPersistedMessage(DbDataReader r)` for 8 properties. Handle `BeginTransaction` usage in `UpsertBatchAsync` (already uses `IDbTransaction`).
  **Files**: `src/WeaveFleet.Infrastructure/Data/Repositories/DapperMessageRepository.cs`
  **Acceptance**: Compiles; remove `using Dapper;`

- [ ] 7. Convert `DapperInstanceRepository.cs`
  **What**: Replace Dapper calls. Add `private static Instance MapInstance(DbDataReader r)` for 8 properties (note: `Port` is `int`, `Pid` is `int?`).
  **Files**: `src/WeaveFleet.Infrastructure/Data/Repositories/DapperInstanceRepository.cs`
  **Acceptance**: Compiles; remove `using Dapper;`

- [ ] 8. Convert `DapperProjectRepository.cs`
  **What**: Replace Dapper calls. Add `private static Project MapProject(DbDataReader r)` for 8 properties (note: `Position` is `int`). The `InsertAsync` and `UpdateAsync` pass the whole `project` object to Dapper — expand to explicit parameters.
  **Files**: `src/WeaveFleet.Infrastructure/Data/Repositories/DapperProjectRepository.cs`
  **Acceptance**: Compiles; remove `using Dapper;`

- [ ] 9. Convert `DapperWorkspaceRepository.cs`
  **What**: Replace Dapper calls. Add `private static Workspace MapWorkspace(DbDataReader r)` for 16 properties (many nullable strings).
  **Files**: `src/WeaveFleet.Infrastructure/Data/Repositories/DapperWorkspaceRepository.cs`
  **Acceptance**: Compiles; remove `using Dapper;`

- [ ] 10. Convert `DapperWorkspaceRootRepository.cs`
  **What**: Replace Dapper calls. Add `private static WorkspaceRoot MapWorkspaceRoot(DbDataReader r)` for 4 properties. Note: `InsertAsync` passes the whole `root` object — expand to explicit parameters.
  **Files**: `src/WeaveFleet.Infrastructure/Data/Repositories/DapperWorkspaceRootRepository.cs`
  **Acceptance**: Compiles; remove `using Dapper;`

- [ ] 11. Convert `DapperUserCredentialRepository.cs`
  **What**: Replace Dapper calls. Add `private static UserCredential MapUserCredential(DbDataReader r)` for 10 properties.
  **Files**: `src/WeaveFleet.Infrastructure/Data/Repositories/DapperUserCredentialRepository.cs`
  **Acceptance**: Compiles; remove `using Dapper;`

- [ ] 12. Convert `DapperSessionCallbackRepository.cs`
  **What**: Replace Dapper calls. Add `private static SessionCallback MapSessionCallback(DbDataReader r)` for 7 properties.
  **Files**: `src/WeaveFleet.Infrastructure/Data/Repositories/DapperSessionCallbackRepository.cs`
  **Acceptance**: Compiles; remove `using Dapper;`

- [ ] 13. Convert `DapperHarnessEventLogRepository.cs`
  **What**: Replace Dapper calls. Add `private static HarnessEventLogEntry MapHarnessEventLogEntry(DbDataReader r)` for 7 properties (note: `Id` is `long`, `SequenceNumber` is `long`).
  **Files**: `src/WeaveFleet.Infrastructure/Data/Repositories/DapperHarnessEventLogRepository.cs`
  **Acceptance**: Compiles; remove `using Dapper;`

- [ ] 14. Convert `DapperDelegationRepository.cs`
  **What**: Replace Dapper calls. Add `private static Delegation MapDelegation(DbDataReader r)` for 9 properties.
  **Files**: `src/WeaveFleet.Infrastructure/Data/Repositories/DapperDelegationRepository.cs`
  **Acceptance**: Compiles; remove `using Dapper;`

- [ ] 15. Convert `BoardRepository.cs`
  **What**: Replace all Dapper calls (largest file, 867 lines). Add mapper methods for `Board` (5 props), `BoardLane` (7 props, `Position` int, `IsInbox` bool), `BoardCard` (11 props, `Position` int), `BoardSource` (7 props). Many private helper methods use `IDbConnection`+`IDbTransaction` — use `DbCommandHelper` overloads that accept these. Handle `QueryAsync<string>` calls (for lane/card IDs) with a simple `r => r.GetString(0)` mapper.
  **Files**: `src/WeaveFleet.Infrastructure/Data/Repositories/BoardRepository.cs`
  **Acceptance**: Compiles; remove `using Dapper;`

- [ ] 16. Convert `DapperUserRepository.cs`
  **What**: Replace Dapper calls. Add `private static User MapUser(DbDataReader r)` for 7 properties. The `UpsertAsync` passes the whole `user` object — expand to explicit parameters.
  **Files**: `src/WeaveFleet.Infrastructure/Data/Repositories/DapperUserRepository.cs`
  **Acceptance**: Compiles; remove `using Dapper;`

- [ ] 17. Convert `InProcessEventStore.cs`
  **What**: Replace `conn.ExecuteScalar<long>(sql, new {...})` in `Append` and `conn.Execute(sql, new {...})` in `MarkDispatched` with raw ADO.NET (synchronous). `ReadPending` already uses raw ADO.NET — no change needed there. Remove `using Dapper;`.
  **Files**: `src/WeaveFleet.Infrastructure/EventBus/InProcessEventStore.cs`
  **Acceptance**: Compiles; remove `using Dapper;`

- [ ] 18. Convert `AnalyticsRepository.cs`
  **What**: Replace all Dapper calls. This file uses interpolated SQL strings (the primary AOT failure case). Add mapper methods for internal classes: `SessionSnapshotRow`, `DailyRollupRow`, `ModelAnalyticsRow`. Also add mapper for `TokenEventRow`. Replace tuple-returning `QueryFirstAsync<(...)>` calls with scalar queries or explicit reader mapping. Remove the `#pragma warning disable CA1812` since classes are now explicitly instantiated by mappers. Remove `using Dapper;`.
  **Files**: `src/WeaveFleet.Infrastructure/Analytics/AnalyticsRepository.cs`
  **Acceptance**: Compiles; remove `using Dapper;`

- [ ] 19. Clean up `DependencyInjection.cs`
  **What**: Remove `using Dapper;` and the line `DefaultTypeMap.MatchNamesWithUnderscores = true;` (and its comment). DI registrations remain unchanged since class names aren't changing.
  **Files**: `src/WeaveFleet.Infrastructure/DependencyInjection.cs`
  **Acceptance**: Compiles; no Dapper imports

- [ ] 20. Delete `DapperAotAssembly.cs` and remove Dapper packages
  **What**: Delete `src/WeaveFleet.Infrastructure/DapperAotAssembly.cs`. Remove from `WeaveFleet.Infrastructure.csproj`: the `<InterceptorsNamespaces>` property containing `Dapper.AOT`, `<PackageReference Include="Dapper" />`, and `<PackageReference Include="Dapper.AOT" ... />`.
  **Files**: `src/WeaveFleet.Infrastructure/DapperAotAssembly.cs`, `src/WeaveFleet.Infrastructure/WeaveFleet.Infrastructure.csproj`
  **Acceptance**: `dotnet build` succeeds; no Dapper references remain in `src/`

## Verification
- [ ] `dotnet build src/WeaveFleet.Infrastructure/WeaveFleet.Infrastructure.csproj` succeeds
- [ ] `dotnet build` at solution level succeeds
- [ ] `dotnet test` — all tests pass
- [ ] No `using Dapper` in any file under `src/` (grep verification)
- [ ] Column name mapping works correctly (snake_case DB columns → PascalCase properties) via explicit ordinal reads in mapper methods
