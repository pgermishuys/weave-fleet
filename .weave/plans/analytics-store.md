# Analytics Store — Implementation Plan

## TL;DR
> **Summary**: Implement a hybrid SQLite + OTEL analytics system that captures per-message token/cost data from OpenCode SSE events into a separate `weave-fleet-analytics.db`, also wires up the existing unused `IncrementTokensAsync`, and emits OTEL counter/histogram metrics.
> **Estimated Effort**: Large

## Context

### Original Request
Build the analytics persistence layer described in the analytics-store-design document. User decisions on open questions:
1. **Retention**: Keep raw `token_events` forever — no cleanup needed
2. **Fleet summary**: Keep reading from main DB — don't change `GetFleetSummaryAsync`
3. **Frontend**: CLI/API only — no UI pages
4. **Fork attribution**: Include `parent_session_id` in `session_snapshots` (nice-to-have)
5. **Cost estimation**: Store estimated costs alongside actuals (model pricing lookup)

### Key Findings

**Connection & Migration patterns**: `SqliteConnectionFactory` takes `FleetOptions`, creates connections with WAL/busy_timeout/FK pragmas. `MigrationRunner` reads embedded SQL resources from the `WeaveFleet.Infrastructure` assembly (`.sql` files under `Migrations/` folder, included via `<EmbeddedResource Include="Migrations\*.sql" />`). It extracts migration names as `{second-to-last}.{last}` from the dotted resource name. The analytics system needs its own migration runner that reads from a different resource prefix to avoid cross-contamination.

**Dapper patterns**: `DefaultTypeMap.MatchNamesWithUnderscores = true` is set globally in `DependencyInjection.cs`. Repositories use primary constructors with `IDbConnectionFactory`, create+dispose connections per method call. All queries use anonymous object parameters.

**DI patterns**: Connection factory = singleton. Migration runner = singleton. Repositories = scoped. Application services = scoped. Singletons for cross-cutting: `InstanceTracker`, `IEventBroadcaster`, `ConfigService`, `RepositoryService`, `GitHubService`, `GitHubApiProxy`, `IIntegrationStore`. `HarnessEventRelay` registered as hosted service via `AddHostedService<HarnessEventRelay>()`. All DI registration lives in `src/WeaveFleet.Infrastructure/DependencyInjection.cs` (there is no Application-layer DI class). The analytics collector and writer should be singletons (they're shared, long-lived, channel-based), and the background services should follow `HarnessEventRelay`'s `AddHostedService` pattern.

**SSE event flow**: `OpenCodeHttpClient.SubscribeToEventsAsync()` → yields `OpenCodeSseEvent` → `OpenCodeHarnessInstance.SubscribeAsync()` calls `OpenCodeMapper.ToHarnessEvent(sseEvt, _openCodeSessionId)` → yields `HarnessEvent` → consumed by `SessionEventEndpoints` SSE and `WebSocketEndpoints`. The `OpenCodeSseEvent.Properties` is a `JsonElement` containing event-specific JSON. For `message.updated` events, Properties has the shape `{ "info": { ... } }` where `"info"` is an `OpenCodeMessageInfo` object (confirmed by contract fixture `tests/contracts/opencode-to-fleet-events.json`). The `OpenCodeAssistantMessage` subtype carries `Cost`, `Tokens`, `ModelId`, `ProviderId`. Note: `message.created` event Properties structure is unverified — no contract fixture or test data exists for it in the codebase.

**Token data models**: `OpenCodeAssistantMessage` has `Cost` (double?), `Tokens` (`OpenCodeTokenUsage`?), `ModelId` (string?), `ProviderId` (string?). `OpenCodeTokenUsage` has `Input`, `Output`, `Reasoning` (all double), `Cache` (`OpenCodeCacheTokens`?), `Total` (double?). `OpenCodeCacheTokens` has `Read`, `Write` (both double).

**IncrementTokensAsync gap**: `DapperSessionRepository.IncrementTokensAsync(id, tokens, cost)` exists and works (tested!) but is never called. Need to call it from the analytics intercept point. **Type mismatch**: The `tokens` parameter is `int`, but `TokenEventData.TokensTotal` is `double` (matching OpenCode's `OpenCodeTokenUsage` which uses `double` for all fields). Callers must cast with `(int)Math.Round(data.TokensTotal)`.

**Existing BackgroundService pattern**: `HarnessEventRelay` (`src/WeaveFleet.Infrastructure/Services/HarnessEventRelay.cs`) is a `BackgroundService` registered via `services.AddHostedService<HarnessEventRelay>()`. It demonstrates the key patterns we need:
- Uses `IServiceScopeFactory` to resolve scoped dependencies (`ISessionRepository`) from within a singleton service (creates a scope per-use, disposes it immediately)
- `ExecuteAsync` with `CancellationToken stoppingToken` — stores the token and uses `CancellationTokenSource.CreateLinkedTokenSource(_stoppingToken)` for child tasks
- Exception handling: catches `OperationCanceledException` (expected on shutdown) separately from unexpected exceptions
- Uses `LoggerMessage.Define` for high-performance structured logging
The analytics `BackgroundService` implementations should follow these same patterns.

**Test patterns**: Tests use `TestDbHelper.CreateSharedDbAsync()` which returns a `(SqliteConnection Keeper, IDbConnectionFactory Factory)` tuple. The keeper connection keeps the in-memory DB alive. Tests are xUnit `[Fact]` methods.

**Embedded resource naming**: The csproj has `<EmbeddedResource Include="Migrations\*.sql" />`. This means a file at `Migrations/001_initial_schema.sql` gets the resource name `WeaveFleet.Infrastructure.Migrations.001_initial_schema.sql`. The `MigrationRunner` reads ALL `.sql` resources from the assembly — no filtering beyond `.EndsWith(".sql")`. **Critical issue**: Any new `.sql` embedded resources (analytics migrations) will be picked up by the main migration runner and incorrectly applied to the main DB.

**Why substring filtering fails**: The original plan proposed a `.Migrations.` substring filter. But if analytics migrations live under `Analytics/Migrations/`, the resource name `WeaveFleet.Infrastructure.Analytics.Migrations.001_analytics_initial.sql` contains `.Migrations.` as a substring — the main runner would still match it. Even using `AnalyticsMigrations/` as the folder name fails because `.AnalyticsMigrations.` contains `.Migrations.` as a substring.

**Decision — dotted-segment matching**: Refactor `MigrationRunner` to accept a `folderSegment` parameter (string, default `"Migrations"`). Filter resources by checking that the resource name, when split on `.`, contains the segment as an **exact match** — not a substring. This cleanly separates:
- Main: folder `Migrations/` → segment `"Migrations"` → matches `...Infrastructure.Migrations.001_initial...`
- Analytics: folder `AnalyticsMigrations/` → segment `"AnalyticsMigrations"` → matches `...Infrastructure.AnalyticsMigrations.001_analytics...`
- The main runner's `"Migrations"` segment filter does NOT match `"AnalyticsMigrations"` because they are different dotted segments.

This is robust, not tied to assembly names, and backward-compatible (default `"Migrations"` preserves existing behavior).

## Objectives

### Core Objective
Capture every token/cost event from OpenCode SSE into a queryable, deletion-proof SQLite analytics database, while also updating the main DB's session token totals and emitting OTEL metrics.

### Deliverables
- [x] Separate `weave-fleet-analytics.db` with three tables: `token_events`, `session_snapshots`, `daily_rollups`
- [x] `Channel<T>`-based fire-and-forget analytics collection from SSE pipeline
- [x] `BackgroundService` batched writer to analytics DB
- [x] `BackgroundService` periodic rollup computation
- [x] SSE intercept in mapper layer extracting token data
- [x] `IncrementTokensAsync` wired up to fix main DB token totals
- [x] OTEL counter/histogram instruments on `FleetInstrumentation.Meter`
- [x] API endpoints for querying analytics (`/api/analytics/*`)
- [x] Cost estimation via model pricing lookup
- [x] Configuration options in `FleetOptions`

### Definition of Done
- [x] `dotnet build` succeeds with no warnings
- [x] `dotnet test` passes all existing + new tests
- [ ] Token events are persisted when an AI response arrives via SSE
- [ ] Session token totals update in the main DB
- [ ] `GET /api/analytics/summary` returns aggregated data
- [ ] OTEL metrics are emitted (visible via console exporter)

### Guardrails (Must NOT)
- Never add foreign keys between analytics DB and main DB
- Never block or slow down the SSE event pipeline (fire-and-forget only)
- Never cause session failures if analytics writes fail
- Never change `GetFleetSummaryAsync` to read from analytics DB

> **Note**: The frontend dashboard plan is at `.weave/plans/analytics-frontend.md` — it consumes the API endpoints defined in Phase 6 below.

## TODOs

### Phase 1: Foundation (Schema, Config, Connection)

- [x] 1. **Add Analytics Configuration to FleetOptions**
  **What**: Add analytics-related configuration properties to `FleetOptions`.
  **Files**:
  - Modify `src/WeaveFleet.Application/Configuration/FleetOptions.cs`
  **Changes**:
  ```
  Add these properties to FleetOptions:
  - AnalyticsDatabasePath (string, default: "" — computed alongside DatabasePath)
  - AnalyticsEnabled (bool, default: true)
  - AnalyticsFlushIntervalSeconds (int, default: 2)
  - AnalyticsMaxBatchSize (int, default: 50)
  - AnalyticsRollupIntervalMinutes (int, default: 5)
  ```
  Add a computed property `ResolvedAnalyticsDatabasePath` that defaults to placing `weave-fleet-analytics.db` in the same directory as `DatabasePath` when `AnalyticsDatabasePath` is empty.
  **Acceptance**: FleetOptions compiles with new properties. Default values are sensible.

- [x] 2. **Create Analytics SQLite Connection Factory**
  **What**: Create `IAnalyticsDbConnectionFactory` interface and `AnalyticsSqliteConnectionFactory` implementation. The analytics DB does NOT need foreign keys (no FK references), so skip `PRAGMA foreign_keys=ON`.
  **Files**:
  - Create `src/WeaveFleet.Application/Data/IAnalyticsDbConnectionFactory.cs`
  - Create `src/WeaveFleet.Infrastructure/Data/AnalyticsSqliteConnectionFactory.cs`
  **Patterns to follow**: Mirror `IDbConnectionFactory` / `SqliteConnectionFactory` exactly. Same WAL + busy_timeout pragmas, but skip `foreign_keys=ON`. Constructor takes the resolved analytics DB path (string), not `FleetOptions` directly (the DI layer resolves the path).
  **Acceptance**: Factory creates connections to a separate DB file with WAL mode.

- [x] 3. **Refactor MigrationRunner for Dotted-Segment Filtering**
  **What**: Add a `folderSegment` constructor parameter (string, optional, default `"Migrations"`) to `MigrationRunner` so it only processes embedded SQL resources whose dotted resource name contains the segment as an **exact segment match** (not a substring). This prevents the main runner from accidentally picking up analytics migrations (whose segment is `"AnalyticsMigrations"`).
  **Files**:
  - Modify `src/WeaveFleet.Infrastructure/Data/MigrationRunner.cs`
  **Changes**:
  - Add `private readonly string _folderSegment;` field
  - Add optional `string folderSegment = "Migrations"` parameter to constructor (after logger)
  - Change the resource name filter from `.EndsWith(".sql")` to:
    ```
    .EndsWith(".sql") && name.Split('.').Contains(_folderSegment)
    ```
    This splits the dotted resource name into segments and checks for an **exact segment match**. `"Migrations"` matches `...Infrastructure.Migrations.001_initial...` but does NOT match `...Infrastructure.AnalyticsMigrations.001_analytics...` because the segment `"AnalyticsMigrations"` ≠ `"Migrations"`.
  - This is backward-compatible — existing DI registration passes no argument, getting the default `"Migrations"`
  **Acceptance**: Existing migration tests still pass. Main runner only picks up `Migrations/*.sql`, not `AnalyticsMigrations/*.sql`.

- [x] 4. **Create Analytics Migration Runner**
  **What**: Create `AnalyticsMigrationRunner` — a thin wrapper that reuses the `MigrationRunner` pattern but uses `IAnalyticsDbConnectionFactory` and filters for the `"AnalyticsMigrations"` dotted segment.
  **Files**:
  - Create `src/WeaveFleet.Infrastructure/Data/AnalyticsMigrationRunner.cs`
  **Implementation**: Create `AnalyticsMigrationRunner` as a new sealed class that takes `IAnalyticsDbConnectionFactory` and creates a private `MigrationRunner` with an adapter and the segment filter `"AnalyticsMigrations"`.
  
  Create a small adapter: `AnalyticsConnectionFactoryAdapter : IDbConnectionFactory` that wraps `IAnalyticsDbConnectionFactory`. Then `AnalyticsMigrationRunner` creates `new MigrationRunner(adapter, logger, "AnalyticsMigrations")` and exposes `ApplyMigrationsAsync()`.
  
  Since `MigrationRunner` takes `IDbConnectionFactory` and the analytics DB uses `IAnalyticsDbConnectionFactory`, the adapter bridges the two interfaces. The `folderSegment` parameter `"AnalyticsMigrations"` ensures this runner only picks up resources with that exact dotted segment, not the main `"Migrations"` segment.
  **Acceptance**: Analytics migrations are applied only to the analytics DB, not the main DB.

- [x] 5. **Create Analytics Schema Migration SQL**
  **What**: Write the SQL migration for the three analytics tables with all indexes.
  **Files**:
  - Create `src/WeaveFleet.Infrastructure/AnalyticsMigrations/001_analytics_initial.sql`
  - Modify `src/WeaveFleet.Infrastructure/WeaveFleet.Infrastructure.csproj` — add `<EmbeddedResource Include="AnalyticsMigrations\*.sql" />`
  **Schema** (refined from design doc):
  ```sql
  -- token_events: append-only fact table, one row per assistant message with token data
  CREATE TABLE token_events (
      id INTEGER PRIMARY KEY AUTOINCREMENT,
      event_id TEXT NOT NULL UNIQUE,
      session_id TEXT NOT NULL,
      project_id TEXT,
      project_name TEXT,
      workspace_directory TEXT,
      model_id TEXT,
      provider_id TEXT,
      tokens_input REAL NOT NULL DEFAULT 0,
      tokens_output REAL NOT NULL DEFAULT 0,
      tokens_reasoning REAL NOT NULL DEFAULT 0,
      tokens_cache_read REAL NOT NULL DEFAULT 0,
      tokens_cache_write REAL NOT NULL DEFAULT 0,
      tokens_total REAL NOT NULL DEFAULT 0,
      cost REAL NOT NULL DEFAULT 0,
      estimated_cost REAL,
      created_at TEXT NOT NULL
  );

  -- session_snapshots: one row per session, updated on create + stop
  CREATE TABLE session_snapshots (
      session_id TEXT PRIMARY KEY,
      parent_session_id TEXT,
      project_id TEXT,
      project_name TEXT,
      workspace_directory TEXT,
      title TEXT,
      status TEXT,
      total_tokens REAL NOT NULL DEFAULT 0,
      total_cost REAL NOT NULL DEFAULT 0,
      total_estimated_cost REAL NOT NULL DEFAULT 0,
      message_count INTEGER NOT NULL DEFAULT 0,
      model_ids TEXT,
      created_at TEXT NOT NULL,
      ended_at TEXT,
      duration_seconds REAL
  );

  -- daily_rollups: materialized aggregates
  CREATE TABLE daily_rollups (
      date TEXT NOT NULL,
      project_id TEXT NOT NULL DEFAULT '',
      model_id TEXT NOT NULL DEFAULT '',
      provider_id TEXT NOT NULL DEFAULT '',
      total_tokens REAL NOT NULL DEFAULT 0,
      total_cost REAL NOT NULL DEFAULT 0,
      total_estimated_cost REAL NOT NULL DEFAULT 0,
      session_count INTEGER NOT NULL DEFAULT 0,
      message_count INTEGER NOT NULL DEFAULT 0,
      PRIMARY KEY (date, project_id, model_id, provider_id)
  );

  CREATE INDEX idx_token_events_session ON token_events(session_id);
  CREATE INDEX idx_token_events_created ON token_events(created_at);
  CREATE INDEX idx_token_events_project ON token_events(project_id);
  CREATE INDEX idx_token_events_model ON token_events(model_id);
  CREATE INDEX idx_session_snapshots_project ON session_snapshots(project_id);
  CREATE INDEX idx_session_snapshots_created ON session_snapshots(created_at);
  CREATE INDEX idx_daily_rollups_date ON daily_rollups(date);
  ```
  **Acceptance**: Migration SQL is valid. Embedded resource is discoverable by analytics migration runner.

### Phase 2: Domain Types & Interfaces

- [x] 6. **Create Analytics Event DTOs**
  **What**: Define the domain types for analytics events that flow through the `Channel<T>` buffer. These are internal value types, not database entities.
  **Files**:
  - Create `src/WeaveFleet.Application/Analytics/AnalyticsEvents.cs`
  **Types**:
  ```
  TokenEventData — record with:
    EventId (string), SessionId (string), ProjectId (string?), ProjectName (string?),
    WorkspaceDirectory (string?), ModelId (string?), ProviderId (string?),
    TokensInput (double), TokensOutput (double), TokensReasoning (double),
    TokensCacheRead (double), TokensCacheWrite (double), TokensTotal (double),
    Cost (double), EstimatedCost (double?), CreatedAt (DateTimeOffset)

  SessionSnapshotData — record with:
    SessionId (string), ParentSessionId (string?), ProjectId (string?),
    ProjectName (string?), WorkspaceDirectory (string?), Title (string?),
    Status (string?), TotalTokens (double), TotalCost (double),
    TotalEstimatedCost (double), MessageCount (int), ModelIds (List<string>),
    CreatedAt (DateTimeOffset), EndedAt (DateTimeOffset?), DurationSeconds (double?)

  AnalyticsEventEnvelope — discriminated union (abstract record with two subtypes):
    TokenEventEnvelope(TokenEventData Data)
    SessionSnapshotEnvelope(SessionSnapshotData Data)
  ```
  **Acceptance**: Types compile. No dependencies on infrastructure.

- [x] 7. **Create IAnalyticsCollector Interface**
  **What**: Define the interface for accepting analytics events. This lives in the Application layer so the Infrastructure SSE intercept can use it.
  **Files**:
  - Create `src/WeaveFleet.Application/Analytics/IAnalyticsCollector.cs`
  **Interface**:
  ```
  IAnalyticsCollector:
    void AcceptTokenEvent(TokenEventData data)
    void AcceptSessionSnapshot(SessionSnapshotData data)
  ```
  Both methods are fire-and-forget (void). They write to a `Channel<T>` internally — never block, never throw.
  **Acceptance**: Interface compiles.

- [x] 8. **Create IAnalyticsReader Interface**
  **What**: Define the read-side interface for analytics queries, used by API endpoints.
  **Files**:
  - Create `src/WeaveFleet.Application/Analytics/IAnalyticsReader.cs`
  **Interface**:
  ```
  IAnalyticsReader:
    Task<AnalyticsSummary> GetSummaryAsync(DateTimeOffset? from, DateTimeOffset? to, string? projectId)
    Task<IReadOnlyList<DailyAnalytics>> GetDailyAsync(DateTimeOffset? from, DateTimeOffset? to, string? projectId)
    Task<IReadOnlyList<SessionAnalytics>> GetSessionsAsync(DateTimeOffset? from, DateTimeOffset? to, string? projectId, int limit)
    Task<IReadOnlyList<ModelAnalytics>> GetModelsAsync(DateTimeOffset? from, DateTimeOffset? to)
    Task<IReadOnlyList<TokenEventRow>> ExportTokenEventsAsync(DateTimeOffset? from, DateTimeOffset? to, string? projectId)
  ```
  **Also create the response DTOs** in the same file (or a companion `AnalyticsDtos.cs`):
  - `AnalyticsSummary` — TotalTokens, TotalCost, TotalEstimatedCost, SessionCount, MessageCount, TopModels[], TopProjects[]
  - `DailyAnalytics` — Date, Tokens, Cost, EstimatedCost, Sessions, Messages
  - `SessionAnalytics` — SessionId, Title, ProjectId, ProjectName, Tokens, Cost, EstimatedCost, Models[], DurationSeconds, CreatedAt
  - `ModelAnalytics` — ModelId, ProviderId, Tokens, Cost, EstimatedCost, MessageCount, AvgCostPerMessage
  - `TokenEventRow` — all columns from token_events table
  **Files**:
  - Create `src/WeaveFleet.Application/Analytics/IAnalyticsReader.cs`
  - Create `src/WeaveFleet.Application/Analytics/AnalyticsDtos.cs`
  **Acceptance**: Interfaces and DTOs compile.

- [x] 9. **Create Model Pricing Lookup**
  **What**: A static/configurable lookup table mapping model IDs to per-token pricing so we can compute estimated costs. Start with a hardcoded dictionary of well-known model prices (Anthropic Claude, OpenAI GPT-4, etc.). The estimated cost = `(input_tokens * input_price) + (output_tokens * output_price) + (cache_read_tokens * cache_read_price)`.
  **Files**:
  - Create `src/WeaveFleet.Application/Analytics/ModelPricing.cs`
  **Implementation**:
  ```
  static class ModelPricing:
    // Dictionary<string, ModelPriceInfo> — keyed by model ID substring match
    // ModelPriceInfo: InputPricePerToken, OutputPricePerToken, CacheReadPricePerToken
    static double? EstimateCost(string? modelId, double inputTokens, double outputTokens,
                                 double reasoningTokens, double cacheReadTokens)
    // Returns null if model not recognized
  ```
  Include pricing for: claude-sonnet-4, claude-opus-4, claude-haiku, gpt-4o, gpt-4o-mini, gpt-4.1, o3, o4-mini. Use published per-million-token rates divided to per-token.
  **Acceptance**: `ModelPricing.EstimateCost("claude-sonnet-4-20250514", 1000, 500, 0, 0)` returns a reasonable number.

### Phase 3: Infrastructure — Collector & Writer

- [x] 10. **Implement AnalyticsCollector**
  **What**: Singleton service implementing `IAnalyticsCollector`. Owns a `Channel<AnalyticsEventEnvelope>` (bounded, capacity 10,000, `FullMode.DropOldest`). `AcceptTokenEvent` / `AcceptSessionSnapshot` wrap the data in an envelope and `TryWrite` it to the channel. Exposes `ChannelReader<AnalyticsEventEnvelope>` for the writer service.
  **Files**:
  - Create `src/WeaveFleet.Infrastructure/Analytics/AnalyticsCollector.cs`
  **Pattern**: Similar to `InMemoryEventBroadcaster` (`src/WeaveFleet.Infrastructure/Services/InMemoryEventBroadcaster.cs`) channel usage but simpler — single reader, single writer, bounded.
  **Acceptance**: Can accept events without blocking. Reader can drain them.

- [x] 11. **Implement AnalyticsWriterService (BackgroundService)**
  **What**: Hosted `BackgroundService` that reads from the collector's channel and batch-inserts into the analytics DB. Uses configurable flush interval and max batch size.
  **Files**:
  - Create `src/WeaveFleet.Infrastructure/Analytics/AnalyticsWriterService.cs`
  **Pattern to follow**: `HarnessEventRelay` (`src/WeaveFleet.Infrastructure/Services/HarnessEventRelay.cs`) — specifically its `IServiceScopeFactory` usage for resolving scoped `ISessionRepository`, `CancellationTokenSource.CreateLinkedTokenSource` pattern, and `LoggerMessage.Define` for structured logging.
  **Implementation**:
  ```
  - Inherits BackgroundService
  - ExecuteAsync loop:
    1. Read events from channel with timeout (flush interval)
    2. Accumulate into batch list
    3. When batch is full OR flush interval expires, write batch
    4. Token events: INSERT OR IGNORE INTO token_events (idempotent via event_id)
    5. Session snapshots: INSERT OR REPLACE INTO session_snapshots (upsert)
    6. All writes in a single transaction for the batch
    7. Catch exceptions, log warnings, continue (never crash)
       — catch OperationCanceledException separately (expected on shutdown, like HarnessEventRelay)
       — catch Exception for unexpected errors, log and continue
  - Also calls IncrementTokensAsync on the main DB for each token event
      **Type cast required**: `IncrementTokensAsync(string id, int tokens, double cost)` takes `int tokens`, but `TokenEventData.TokensTotal` is `double`. Use `(int)Math.Round(data.TokensTotal)` when calling: `await sessionRepo.IncrementTokensAsync(data.SessionId, (int)Math.Round(data.TokensTotal), data.Cost);`
  - Also records OTEL metrics for each token event
  ```
  **Dependencies**: `AnalyticsCollector` (for channel reader), `IAnalyticsDbConnectionFactory`, `IServiceScopeFactory` (to resolve scoped `ISessionRepository` for IncrementTokensAsync — same pattern as `HarnessEventRelay`), `FleetOptions`, `ILogger`, and the OTEL instruments.
  
  **Scoped dependency handling**: Follow `HarnessEventRelay.PumpAsync` pattern exactly — inject `IServiceScopeFactory`, create a scope with `_scopeFactory.CreateScope()`, resolve `ISessionRepository` from `scope.ServiceProvider.GetRequiredService<ISessionRepository>()`, dispose scope after use.
  **Acceptance**: Token events accumulate in the analytics DB. Main DB session totals update.

- [x] 12. **Implement AnalyticsRepository (read side)**
  **What**: Dapper-based repository implementing `IAnalyticsReader`. Queries the analytics DB for summaries, daily breakdowns, session listings, model breakdowns, and raw export.
  **Files**:
  - Create `src/WeaveFleet.Infrastructure/Analytics/AnalyticsRepository.cs`
  **Pattern**: Follow `DapperSessionRepository` style — primary constructor with `IAnalyticsDbConnectionFactory`, create/dispose connection per method. Use parameterized SQL with `@From`, `@To`, `@ProjectId` filters. Date range filtering uses `created_at >= @From AND created_at < @To` on ISO 8601 strings (SQLite string comparison works for ISO 8601).
  **Key queries**:
  - `GetSummaryAsync`: Aggregate from `token_events` + count distinct `session_id`. Include top 5 models/projects by cost.
  - `GetDailyAsync`: Read from `daily_rollups` table (fast), falling back to `token_events` GROUP BY for uncovered dates.
  - `GetSessionsAsync`: Read from `session_snapshots`, ordered by `created_at DESC`, with LIMIT.
  - `GetModelsAsync`: Aggregate from `token_events` GROUP BY `model_id, provider_id`.
  - `ExportTokenEventsAsync`: Select all columns from `token_events` with date/project filters.
  **Acceptance**: Queries return correct results against test data.

- [x] 13. **Implement AnalyticsRollupService (BackgroundService)**
  **What**: Periodic `BackgroundService` that recomputes `daily_rollups` from `token_events`. Runs every N minutes (configurable). Computes rollups for the last 2 days (to catch late-arriving events).
  **Files**:
  - Create `src/WeaveFleet.Infrastructure/Analytics/AnalyticsRollupService.cs`
  **Pattern to follow**: `HarnessEventRelay` — use `LoggerMessage.Define` for structured logging, handle `OperationCanceledException` cleanly on shutdown, use `CancellationToken` from `ExecuteAsync`.
  **Implementation**:
  ```
  - Inherits BackgroundService
  - ExecuteAsync: loop with Task.Delay(rollupInterval, stoppingToken)
  - On each tick:
    1. Compute date range: today and yesterday (UTC)
    2. DELETE FROM daily_rollups WHERE date IN (today, yesterday)
    3. INSERT INTO daily_rollups SELECT ... FROM token_events WHERE date(created_at) IN (...)
       GROUP BY date, project_id, model_id, provider_id
    4. Also insert fleet-wide rows (project_id='', model_id='', provider_id='')
    5. Wrap in transaction
  - Catch OperationCanceledException separately (expected on shutdown)
  - Catch Exception, log warning, continue loop
  ```
  **Acceptance**: Rollups are computed and queryable.

### Phase 4: SSE Event Intercept

- [x] 14. **Add Token Data Extraction to OpenCodeMapper**
  **What**: Add a new static method `TryExtractTokenEvent` to `OpenCodeMapper` that attempts to parse token/cost data from an `OpenCodeSseEvent`. This is the side-channel extraction point.
  **Files**:
  - Modify `src/WeaveFleet.Infrastructure/Harnesses/OpenCode/OpenCodeMapper.cs`
  **Implementation**:
  ```
  internal static TokenEventData? TryExtractTokenEvent(
      OpenCodeSseEvent evt,
      string? sessionId,
      string? projectId,
      string? projectName,
      string? workspaceDirectory)
  {
      // Only process message.updated events.
      // NOTE: message.created is NOT handled here — there is no contract fixture
      // or test data confirming its Properties structure. If message.created support
      // is needed later, add a contract case first to verify the shape, then add
      // handling here (it may or may not use the same { "info": { ... } } wrapper).
      if (evt.Type is not "message.updated")
          return null;

      // The message.updated Properties nests the message under "info":
      //   { "info": { "id": "...", "role": "assistant", ... } }
      // Confirmed by contract fixture: tests/contracts/opencode-to-fleet-events.json
      // (message_updated case). Do NOT deserialize Properties directly.
      try
      {
          if (evt.Properties.ValueKind != JsonValueKind.Object)
              return null;

          if (!evt.Properties.TryGetProperty("info", out var infoEl))
              return null;

          var msgInfo = infoEl.Deserialize<OpenCodeMessageInfo>(OpenCodeJsonOptions.Default);
          if (msgInfo is not OpenCodeAssistantMessage assistant)
              return null;

          // Only extract if there's token data
          if (assistant.Tokens is null && assistant.Cost is null)
              return null;

          var tokens = assistant.Tokens;
          var tokensTotal = tokens?.Total ?? 0;
          var cost = assistant.Cost ?? 0;

          // Compute estimated cost
          var estimatedCost = ModelPricing.EstimateCost(
              assistant.ModelId,
              tokens?.Input ?? 0,
              tokens?.Output ?? 0,
              tokens?.Reasoning ?? 0,
              tokens?.Cache?.Read ?? 0);

          return new TokenEventData(
              EventId: $"{sessionId}:{assistant.Id}",
              SessionId: sessionId ?? "",
              ProjectId: projectId,
              ProjectName: projectName,
              WorkspaceDirectory: workspaceDirectory,
              ModelId: assistant.ModelId,
              ProviderId: assistant.ProviderId,
              TokensInput: tokens?.Input ?? 0,
              TokensOutput: tokens?.Output ?? 0,
              TokensReasoning: tokens?.Reasoning ?? 0,
              TokensCacheRead: tokens?.Cache?.Read ?? 0,
              TokensCacheWrite: tokens?.Cache?.Write ?? 0,
              TokensTotal: tokensTotal,
              Cost: cost,
              EstimatedCost: estimatedCost,
              CreatedAt: DateTimeOffset.FromUnixTimeMilliseconds(assistant.Time.Created));
      }
      catch
      {
          return null; // Silent failure — analytics never crashes sessions
      }
  }
  ```
  **Properties structure (verified)**: For `message.updated` events, `OpenCodeSseEvent.Properties` is `{ "info": { ... } }` where the inner object is an `OpenCodeMessageInfo` (polymorphic on `role`). This is confirmed by the contract fixture at `tests/contracts/opencode-to-fleet-events.json` (`message_updated` case, lines 84–105). The code uses `TryGetProperty("info", ...)` to extract the nested element before deserializing.
  
  **`message.created` status**: No contract fixture, test data, or codebase usage confirms the `Properties` structure for `message.created` events. The codebase has zero references to `message.created` in C# code or JSON fixtures. Until a contract case is added verifying its shape, `message.created` is excluded from extraction to avoid silent deserialization failures against an unknown structure.
  **Acceptance**: Given a `message.updated` SSE event with token data, returns a valid `TokenEventData`. Returns null for non-message events.

- [x] 15. **Wire Analytics Collection into OpenCodeHarnessInstance.SubscribeAsync**
  **What**: Modify `SubscribeAsync` to call `TryExtractTokenEvent` on each SSE event and push results to the analytics collector. The instance needs access to `IAnalyticsCollector` and session context (project ID, project name, directory).
  **Files**:
  - Modify `src/WeaveFleet.Infrastructure/Harnesses/OpenCode/OpenCodeHarnessInstance.cs`
  - Modify `src/WeaveFleet.Infrastructure/Harnesses/OpenCode/OpenCodeHarness.cs` (the factory that creates instances — to pass the collector)
  **Changes to `OpenCodeHarnessInstance`**:
  - Add `IAnalyticsCollector? _analyticsCollector` field (nullable — analytics can be disabled)
  - Add `string? _projectId`, `string? _projectName`, `string? _workspaceDirectory` fields for context
  - Constructor receives these from the factory
  - In `SubscribeAsync`, before `yield return`:
    ```
    var tokenEvent = OpenCodeMapper.TryExtractTokenEvent(
        sseEvt, _openCodeSessionId, _projectId, _projectName, _workingDirectory);
    if (tokenEvent is not null)
        _analyticsCollector?.AcceptTokenEvent(tokenEvent);
    ```
  **Changes to the factory** (the class that creates `OpenCodeHarnessInstance`):
  - Accept `IAnalyticsCollector?` in constructor
  - Pass it through when creating instances
  
  **Context enrichment**: The instance doesn't know the Weave Fleet session ID or project info. This comes from the `SessionOrchestrator`. We need to pass the Weave session's project context into the instance. **Approach**: The `OpenCodeHarnessInstance` already receives `_workingDirectory`. We need to also pass `projectId` and `projectName`. These are available in `SessionOrchestrator.CreateSessionAsync()` where the session is created. 
  
  **Simpler alternative**: Since the SSE event contains `sessionId` (the OpenCode session ID), and the writer service can look up the Weave session by harness ID to get project context, we could skip passing context entirely and resolve it lazily. But this adds DB lookups in the hot path. **Better**: Pass context at creation time via the instance constructor.
  
  Look at `OpenCodeHarness.SpawnAsync()` — it creates the `OpenCodeHarnessInstance`. We need to either:
  (a) Pass `IAnalyticsCollector` to `OpenCodeHarness` (singleton) and thread it through
  (b) Use a service locator pattern (avoid)
  
  Go with (a). `OpenCodeHarness` already takes dependencies. Add `IAnalyticsCollector?`.
  
  For project context: `HarnessSpawnOptions` has `SessionId` and `WorkingDirectory`. We can add `ProjectId` and `ProjectName` to `HarnessSpawnOptions` (or a new options type). **Simplest**: Add optional `ProjectId`/`ProjectName` to `HarnessSpawnOptions` in the Application layer. Then `SessionOrchestrator` populates them.
  **Acceptance**: Token events are pushed to the collector when AI messages arrive via SSE.

- [x] 16. **Emit Session Snapshots on Create and Stop**
  **What**: Push `SessionSnapshotData` to the analytics collector when sessions are created and stopped/deleted.
  **Files**:
  - Modify `src/WeaveFleet.Application/Services/SessionOrchestrator.cs`
  **Changes**:
  - Add `IAnalyticsCollector?` to constructor (nullable — injected only if analytics enabled)
  - After `sessionRepository.InsertAsync(session)` in `CreateSessionAsync`, call:
    ```
    _analyticsCollector?.AcceptSessionSnapshot(new SessionSnapshotData(
        SessionId: session.Id, ParentSessionId: session.ParentSessionId,
        ProjectId: session.ProjectId, ProjectName: <lookup>, ...
        Status: "active", CreatedAt: DateTimeOffset.UtcNow, ...));
    ```
  - In `DeleteSessionAsync` (or wherever session stop is handled), push an updated snapshot with final status and ended_at.
  
  **Project name lookup**: The orchestrator already has `IProjectRepository`. Look up the project name if `projectId` is non-null.
  
  **Alternative for stop**: The session status changes happen in multiple places (DeleteSessionAsync, the instance tracker's event flow, etc.). The cleanest intercept for "session ended" is in the AnalyticsWriterService itself — when processing a `TokenEventData`, check if we've seen a new session_id and create/update the snapshot. But that misses sessions with zero messages. **Better**: Emit snapshots from the orchestrator on create and from `SessionService`/`InstanceService` on stop.
  **Acceptance**: Session snapshots appear in the analytics DB for new and stopped sessions.

### Phase 5: OTEL Metrics

- [x] 17. **Add OTEL Metric Instruments to FleetInstrumentation**
  **What**: Define Counter and Histogram instruments on the existing `FleetInstrumentation.Meter`.
  **Files**:
  - Modify `src/WeaveFleet.Application/Diagnostics/FleetInstrumentation.cs`
  **Add these static instrument fields**:
  ```csharp
  // Analytics metrics
  public static readonly Counter<long> TokensConsumed =
      Meter.CreateCounter<long>("weave_fleet.tokens.consumed", "tokens",
          "Total tokens consumed across all sessions");

  public static readonly Counter<double> CostIncurred =
      Meter.CreateCounter<double>("weave_fleet.cost.incurred", "USD",
          "Total cost incurred across all sessions");

  public static readonly Counter<double> EstimatedCostIncurred =
      Meter.CreateCounter<double>("weave_fleet.cost.estimated", "USD",
          "Total estimated cost across all sessions");

  public static readonly Counter<long> MessagesProcessed =
      Meter.CreateCounter<long>("weave_fleet.messages.processed", "messages",
          "Total AI messages processed");

  public static readonly Histogram<double> MessageCost =
      Meter.CreateHistogram<double>("weave_fleet.message.cost", "USD",
          "Distribution of per-message costs");

  public static readonly Histogram<long> MessageTokens =
      Meter.CreateHistogram<long>("weave_fleet.message.tokens", "tokens",
          "Distribution of per-message token counts");
  ```
  **Acceptance**: Instruments are registered. Console exporter can display them when telemetry is enabled.

- [x] 18. **Record OTEL Metrics in AnalyticsWriterService**
  **What**: In the writer service, after processing each `TokenEventData`, call the OTEL instruments to record the metrics with appropriate tags.
  **Files**:
  - Modify `src/WeaveFleet.Infrastructure/Analytics/AnalyticsWriterService.cs` (from task 11)
  **Implementation**:
  ```
  For each TokenEventData:
    var tags = new TagList
    {
        { "model", data.ModelId ?? "unknown" },
        { "provider", data.ProviderId ?? "unknown" },
        { "project", data.ProjectId ?? "unknown" }
    };
    FleetInstrumentation.TokensConsumed.Add((long)data.TokensTotal, tags);
    FleetInstrumentation.CostIncurred.Add(data.Cost, tags);
    if (data.EstimatedCost.HasValue)
        FleetInstrumentation.EstimatedCostIncurred.Add(data.EstimatedCost.Value, tags);
    FleetInstrumentation.MessagesProcessed.Add(1, tags);
    FleetInstrumentation.MessageCost.Record(data.Cost, tags);
    FleetInstrumentation.MessageTokens.Record((long)data.TokensTotal, tags);
  ```
  **Acceptance**: OTEL metrics are recorded for each token event.

### Phase 6: API Endpoints

- [x] 19. **Create Analytics API Endpoints**
  **What**: Minimal API endpoints for querying analytics data.
  **Files**:
  - Create `src/WeaveFleet.Api/Endpoints/AnalyticsEndpoints.cs`
  - Modify `src/WeaveFleet.Api/Endpoints/EndpointExtensions.cs` — add `app.MapAnalyticsEndpoints();` call
  **Endpoints**:
  ```
  GET /api/analytics/summary?from=&to=&projectId=
    → AnalyticsSummary JSON
    
  GET /api/analytics/daily?from=&to=&projectId=
    → DailyAnalytics[] JSON

  GET /api/analytics/sessions?from=&to=&projectId=&limit=50
    → SessionAnalytics[] JSON

  GET /api/analytics/models?from=&to=
    → ModelAnalytics[] JSON

  GET /api/analytics/export?from=&to=&projectId=&format=json
    → TokenEventRow[] JSON (or CSV if format=csv)
  ```
  **Pattern**: Follow the existing endpoint registration convention in `EndpointExtensions.cs` — currently has 14 `Map*Endpoints()` calls (e.g. `app.MapSessionEndpoints()`, `app.MapProjectEndpoints()`, `app.MapFleetSummaryEndpoints()`, etc.). Each endpoint file defines a static `Map*Endpoints(this WebApplication app)` extension method. Add `app.MapAnalyticsEndpoints();` to `MapFleetEndpoints()`. Follow `FleetEndpoints.cs` style — `MapGroup("/api/analytics").WithTags("Analytics")`, inject `IAnalyticsReader`, parse query params.
  
  **Date parsing**: Accept ISO 8601 strings for `from`/`to`, parse to `DateTimeOffset`.
  
  **CSV export**: For `format=csv`, set `Content-Type: text/csv` and stream rows as CSV. Use a simple manual CSV writer (no external dependency).
  **Acceptance**: Endpoints return correct data. `/api/analytics/summary` works with no params (returns all-time summary).

### Phase 7: DI Wiring & Startup

- [x] 20. **Register All Analytics Services in DI**
  **What**: Wire up all analytics services in the DI container.
  **Files**:
  - Modify `src/WeaveFleet.Infrastructure/DependencyInjection.cs`
  **Current DI structure** (in `AddFleetInfrastructure(services, options)`): The method registers FleetOptions as singleton, then database factory + migration runner as singletons, then 6 repositories as scoped, then 7 application services as scoped, then singletons for ConfigService/DirectoryService/RepositoryService/IntegrationStore/GitHubService/GitHubApiProxy/InstanceTracker/IEventBroadcaster, then `AddHostedService<HarnessEventRelay>()`, then harness registry + OpenCode harness as singletons. All registrations live in this single method — there is no `Application/DependencyInjection.cs`.
  
  **Registrations** (add analytics block after the `HarnessEventRelay` registration, before harness registry):
  ```csharp
  if (options.AnalyticsEnabled)
  {
      // Analytics connection factory (singleton — same pattern as IDbConnectionFactory)
      var analyticsDbPath = options.ResolvedAnalyticsDatabasePath;
      services.AddSingleton<IAnalyticsDbConnectionFactory>(
          _ => new AnalyticsSqliteConnectionFactory(analyticsDbPath));

      // Analytics migration runner (singleton — same pattern as MigrationRunner)
      services.AddSingleton<AnalyticsMigrationRunner>();

      // Analytics collector (singleton — channel buffer, shared across requests)
      services.AddSingleton<AnalyticsCollector>();
      services.AddSingleton<IAnalyticsCollector>(sp => sp.GetRequiredService<AnalyticsCollector>());

      // Analytics repository (scoped — same pattern as other repositories)
      services.AddScoped<IAnalyticsReader, AnalyticsRepository>();

      // Background services (same pattern as HarnessEventRelay)
      services.AddHostedService<AnalyticsWriterService>();
      services.AddHostedService<AnalyticsRollupService>();
  }
  ```
  **Important**: When analytics is disabled, `IAnalyticsCollector` should resolve to a no-op. Since `SessionOrchestrator` and `OpenCodeHarness` receive `IAnalyticsCollector` in their constructors, always register the interface:
  ```csharp
  if (!options.AnalyticsEnabled)
  {
      services.AddSingleton<IAnalyticsCollector, NullAnalyticsCollector>();
  }
  ```
  Create a `NullAnalyticsCollector` that implements `IAnalyticsCollector` with empty methods. This avoids nullable DI parameters in primary constructors (which `SessionOrchestrator` uses).
  **Acceptance**: App starts with analytics enabled (default). All services resolve correctly. With analytics disabled, `NullAnalyticsCollector` is injected instead.

- [x] 21. **Run Analytics Migrations at Startup**
  **What**: Apply analytics migrations during app startup, after main migrations.
  **Files**:
  - Modify `src/WeaveFleet.Api/Program.cs`
  **Changes**: After `await migrationRunner.ApplyMigrationsAsync();`, add:
  ```csharp
  // Run analytics database migrations
  var analyticsMigrationRunner = app.Services.GetService<AnalyticsMigrationRunner>();
  if (analyticsMigrationRunner is not null)
      await analyticsMigrationRunner.ApplyMigrationsAsync();
  ```
  Use `GetService` (not `GetRequiredService`) so it's null when analytics is disabled.
  **Acceptance**: Analytics DB is created and migrated at startup.

- [x] 22. **Thread IAnalyticsCollector Through Harness Factory**
  **What**: Ensure `IAnalyticsCollector` is available in `OpenCodeHarness` and threaded to each `OpenCodeHarnessInstance`.
  **Files**:
  - Modify `src/WeaveFleet.Infrastructure/Harnesses/OpenCode/OpenCodeHarness.cs`
  - Modify `src/WeaveFleet.Application/Harnesses/HarnessModels.cs` (add ProjectId/ProjectName to HarnessSpawnOptions)
  - Modify `src/WeaveFleet.Application/Services/SessionOrchestrator.cs` (populate ProjectId/ProjectName in spawn options)
  **Changes**:
  - `HarnessSpawnOptions`: Add `string? ProjectId` and `string? ProjectName`
  - `SessionOrchestrator.CreateSessionAsync`: Look up project name if projectId is non-null, populate spawn options
  - `OpenCodeHarness`: Accept `IAnalyticsCollector?` in constructor, pass to each spawned instance
  - `OpenCodeHarnessInstance`: Accept `IAnalyticsCollector?`, `string? projectId`, `string? projectName` in constructor
  **Acceptance**: Analytics collector is available in each instance for SSE intercept.

### Phase 8: Tests

- [x] 23. **Analytics DB Test Helper**
  **What**: Create a test helper similar to `TestDbHelper` but for the analytics DB.
  **Files**:
  - Create `tests/WeaveFleet.Infrastructure.Tests/Analytics/AnalyticsTestDbHelper.cs`
  **Implementation**: Similar to `TestDbHelper.CreateSharedDbAsync()` but uses a `SharedCacheFactory` adapted to `IAnalyticsDbConnectionFactory`, and applies analytics migrations.
  **Acceptance**: In-memory analytics DB can be created in tests.

- [x] 24. **AnalyticsCollector Tests**
  **What**: Test that the collector accepts events and they can be read from the channel.
  **Files**:
  - Create `tests/WeaveFleet.Infrastructure.Tests/Analytics/AnalyticsCollectorTests.cs`
  **Tests**:
  - AcceptTokenEvent writes to channel reader
  - AcceptSessionSnapshot writes to channel reader
  - Channel is bounded — excess events are dropped (not blocking)
  - Concurrent writes don't throw
  **Acceptance**: All tests pass.

- [x] 25. **AnalyticsRepository Tests**
  **What**: Test all read queries against a pre-populated analytics DB.
  **Files**:
  - Create `tests/WeaveFleet.Infrastructure.Tests/Analytics/AnalyticsRepositoryTests.cs`
  **Tests**:
  - GetSummaryAsync returns correct aggregates
  - GetDailyAsync returns correct daily breakdown
  - GetSessionsAsync returns paginated sessions
  - GetModelsAsync returns per-model breakdown
  - ExportTokenEventsAsync returns all matching rows
  - Date range filtering works correctly
  - Project filtering works correctly
  **Acceptance**: All tests pass.

- [x] 26. **AnalyticsWriterService Tests**
  **What**: Test that the writer service correctly processes events from the channel and writes to DB.
  **Files**:
  - Create `tests/WeaveFleet.Infrastructure.Tests/Analytics/AnalyticsWriterServiceTests.cs`
  **Tests**:
  - Processes token events and inserts into token_events table
  - Processes session snapshots and upserts into session_snapshots table
  - Idempotent: duplicate event_id doesn't cause error or duplicate row
  - Batch flush: multiple events are written in one transaction
  **Acceptance**: All tests pass.

- [x] 27. **OpenCodeMapper.TryExtractTokenEvent Tests**
  **What**: Test the token data extraction from SSE events.
  **Files**:
  - Modify `tests/WeaveFleet.Infrastructure.Tests/Harnesses/OpenCode/OpenCodeMapperTests.cs`
  **Tests**:
  - Extracts token data from a valid message.updated event with assistant message (Properties has `"info"` wrapper)
  - Returns null for user message events (role != assistant)
  - Returns null for non-message events (e.g., session.updated)
  - Returns null for message.created events (unverified Properties structure — excluded until contract case added)
  - Returns null for assistant message with no token data
  - Returns null when Properties lacks `"info"` key
  - Handles missing/null fields gracefully
  - Computes estimated cost correctly
  **Acceptance**: All tests pass.

- [x] 28. **ModelPricing Tests**
  **What**: Test the cost estimation logic.
  **Files**:
  - Create `tests/WeaveFleet.Application.Tests/Analytics/ModelPricingTests.cs`
  **Tests**:
  - Known model returns estimated cost
  - Unknown model returns null
  - Zero tokens returns zero cost
  - Cache read tokens use reduced pricing
  **Acceptance**: All tests pass.

- [x] 29. **Migration Runner Segment Filter Tests**
  **What**: Test that the refactored MigrationRunner correctly filters by exact dotted-segment matching.
  **Files**:
  - Modify `tests/WeaveFleet.Infrastructure.Tests/Data/MigrationRunnerTests.cs`
  **Tests**:
  - Default `"Migrations"` segment filter still applies main migrations correctly
  - `"AnalyticsMigrations"` segment filter only applies analytics migrations
  - Main runner with `"Migrations"` segment does NOT match resources containing `"AnalyticsMigrations"` (exact segment match, not substring)
  - Segment matching is case-sensitive (segments must match exactly)
  **Acceptance**: Existing tests still pass. New segment filter tests pass.

## Verification

- [x] `dotnet build` succeeds across all projects with no errors or warnings
- [x] `dotnet test` passes all existing tests (no regressions from MigrationRunner refactor)
- [x] `dotnet test` passes all new analytics tests
- [ ] Manual verification: start the app, create a session, send a prompt, observe:
  - `weave-fleet-analytics.db` is created alongside `weave-fleet.db`
  - `token_events` table has rows with token/cost data
  - `session_snapshots` table has the session
  - Main DB `sessions.total_tokens` and `sessions.total_cost` are non-zero
  - `GET /api/analytics/summary` returns aggregated data
  - OTEL console exporter shows `weave_fleet.tokens.consumed` counter (if telemetry enabled)
- [ ] Analytics DB deletion has no effect on main DB operation
- [ ] Main DB session deletion has no effect on analytics DB data
