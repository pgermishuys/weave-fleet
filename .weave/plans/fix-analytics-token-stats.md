# Fix Analytics Token Stats Race Condition

## TL;DR
> **Summary**: Replace `INSERT OR IGNORE` with SQLite UPSERT in the analytics writer so streaming token updates overwrite partial data instead of being silently discarded, and guard the main DB increment path against double-counting.
> **Estimated Effort**: Short

## Context
### Original Request
Token statistics are not showing in the analytics view. The root cause is that OpenCode sends multiple `message.updated` SSE events for the same assistant message as tokens stream in. The first event (often with partial/zero counts) gets inserted, and all subsequent updates — including the final one with actual token totals — are silently discarded by `INSERT OR IGNORE` due to the UNIQUE constraint on `event_id`.

### Key Findings
1. **`AnalyticsWriterService.cs` line 128**: Uses `INSERT OR IGNORE INTO token_events`. The `event_id` is `{sessionId}:{assistant.Id}` (generated in `OpenCodeMapper.cs` line 220), so all updates for the same assistant message share the same key. The first partial event wins; the final event with real totals is discarded.

2. **`OpenCodeMapper.cs` line 205**: The filter `if (assistant.Tokens is null && assistant.Cost is null)` uses AND logic. Events where `Cost` is non-null (e.g., `cost: 0`) but `Tokens` is null pass through. Events where `Tokens` is present but all zeros AND cost is zero also pass through, generating unnecessary writes.

3. **Double-counting in main DB**: `UpdateMainDbAsync` (line 193-223) calls `sessionRepo.IncrementTokensAsync()` for **every** `TokenEventData` in the batch — not just successfully inserted ones. `IncrementTokensAsync` does `SET total_tokens = total_tokens + @Tokens`, which is additive. Today, with `INSERT OR IGNORE`, the same event_id can appear multiple times in different batches (channel delivers it, INSERT ignores it, but the increment still fires). However, because `INSERT OR IGNORE` silently succeeds and the token event still reaches `UpdateMainDbAsync`, the main DB is already double-counting. Switching to UPSERT doesn't make this worse but we should fix it.

4. **ClaudeCode harness is not affected**: `ClaudeCodeMapper.TryExtractTokenEvent` uses `result.GetHashCode()` for event_id uniqueness and only fires once on `result` messages — no streaming updates. No changes needed there.

5. **Migration system**: SQL files under `src/WeaveFleet.Infrastructure/AnalyticsMigrations/` are embedded resources (`.csproj` line 14: `<EmbeddedResource Include="AnalyticsMigrations\*.sql" />`). The `MigrationRunner` discovers them by dotted-segment match on `AnalyticsMigrations`, orders alphabetically, and skips already-applied ones. A new `002_*.sql` file will be auto-discovered.

6. **Schema**: No schema changes needed. The UNIQUE constraint on `event_id` (line 6 of `001_analytics_initial.sql`) stays — the UPSERT's `ON CONFLICT(event_id)` clause references it directly.

## Objectives
### Core Objective
Ensure the final token/cost data for each assistant message is persisted in the analytics DB, and the main DB session totals are not double-counted.

### Deliverables
- [x] UPSERT SQL in `AnalyticsWriterService` that overwrites partial data with higher-total events
- [x] Deduplicated main DB increment logic (only increment for net-new or delta data)
- [x] Tighter filter in `OpenCodeMapper` to skip all-zero token events
- [x] Integration tests covering the upsert and dedup behaviors
- [x] Migration file (no-op, for documentation — schema doesn't change)

### Definition of Done
- [x] `dotnet test` passes with all new and existing tests green
- [x] `dotnet build -c Release` succeeds with no warnings
- [ ] Manually verify: two token events with same `event_id` but different `tokens_total` → only the higher value persists
- [x] `IncrementTokensAsync` is called at most once per unique `event_id` per session

### Guardrails (Must NOT)
- Must NOT edit `001_analytics_initial.sql` — schema changes go in `002_*.sql`
- Must NOT change the `AnalyticsCollector` channel behavior
- Must NOT break the ClaudeCode harness analytics path
- Must NOT let analytics failures crash sessions (maintain fire-and-forget contract)
- Must NOT change the `AnalyticsRepository` read queries

## TODOs

- [x] 1. **Change INSERT OR IGNORE to UPSERT in AnalyticsWriterService**
  **What**: Replace the `INSERT OR IGNORE INTO token_events` SQL (line 128) with SQLite UPSERT syntax: `INSERT INTO token_events (...) VALUES (...) ON CONFLICT(event_id) DO UPDATE SET` for all token/cost columns. The UPDATE must be conditional: only overwrite when the new `tokens_total` is greater than the existing `excluded.tokens_total` (to guard against out-of-order delivery where a late-arriving partial event could overwrite final data).
  **Files**: `src/WeaveFleet.Infrastructure/Analytics/AnalyticsWriterService.cs`
  **Detail**: The SQL should be:
  ```sql
  INSERT INTO token_events (
      event_id, session_id, project_id, project_name, workspace_directory,
      model_id, provider_id,
      tokens_input, tokens_output, tokens_reasoning,
      tokens_cache_read, tokens_cache_write, tokens_total,
      cost, estimated_cost, created_at
  ) VALUES (
      @EventId, @SessionId, @ProjectId, @ProjectName, @WorkspaceDirectory,
      @ModelId, @ProviderId,
      @TokensInput, @TokensOutput, @TokensReasoning,
      @TokensCacheRead, @TokensCacheWrite, @TokensTotal,
      @Cost, @EstimatedCost, @CreatedAt
  )
  ON CONFLICT(event_id) DO UPDATE SET
      tokens_input     = CASE WHEN excluded.tokens_total > token_events.tokens_total THEN excluded.tokens_input     ELSE token_events.tokens_input     END,
      tokens_output    = CASE WHEN excluded.tokens_total > token_events.tokens_total THEN excluded.tokens_output    ELSE token_events.tokens_output    END,
      tokens_reasoning = CASE WHEN excluded.tokens_total > token_events.tokens_total THEN excluded.tokens_reasoning ELSE token_events.tokens_reasoning END,
      tokens_cache_read  = CASE WHEN excluded.tokens_total > token_events.tokens_total THEN excluded.tokens_cache_read  ELSE token_events.tokens_cache_read  END,
      tokens_cache_write = CASE WHEN excluded.tokens_total > token_events.tokens_total THEN excluded.tokens_cache_write ELSE token_events.tokens_cache_write END,
      tokens_total     = CASE WHEN excluded.tokens_total > token_events.tokens_total THEN excluded.tokens_total     ELSE token_events.tokens_total     END,
      cost             = CASE WHEN excluded.tokens_total > token_events.tokens_total THEN excluded.cost             ELSE token_events.cost             END,
      estimated_cost   = CASE WHEN excluded.tokens_total > token_events.tokens_total THEN excluded.estimated_cost   ELSE token_events.estimated_cost   END
  ```
  Note: `excluded` is SQLite's keyword for the row that was proposed for insertion (equivalent to PostgreSQL's `EXCLUDED`).
  **Acceptance**: SQL compiles and runs against a SQLite analytics DB; existing `ProcessesTokenEvent_InsertsIntoTokenEventsTable` test still passes.

- [x] 2. **Make main DB increment idempotent per event_id**
  **What**: Track which `event_id` values have already been incremented, so `IncrementTokensAsync` is called at most once per unique event. The simplest approach: after the UPSERT in `WriteToAnalyticsDbAsync`, check whether each event was a new insert or a no-op update (SQLite's `changes()` returns 1 for either insert or update, so we can't distinguish). Instead, **track seen event_ids**: maintain a bounded `HashSet<string>` of recently-processed event_ids on the `AnalyticsWriterService` instance. Before calling `IncrementTokensAsync`, check if the event_id was already seen. If the UPSERT actually updated to higher values, compute the **delta** (new - old) and increment by the delta instead of the full amount.
  
  **Simpler alternative** (recommended): Since the UPSERT only updates when `tokens_total` increases, we can restructure `UpdateMainDbAsync` to only increment when a row was newly inserted (first time we see this event_id). Use a `HashSet<string> _processedEventIds` field on `AnalyticsWriterService`. For each token event:
  - If `_processedEventIds.Add(eventId)` returns `true` → first time → call `IncrementTokensAsync` with the current values
  - If it returns `false` → duplicate → skip the increment (the UPSERT in the analytics DB will handle overwriting if needed, but the main DB total is already counted from the first event's values)
  
  This under-counts slightly (first partial event's tokens, not final), but is idempotent. To be fully correct, we'd need to compute deltas by querying the old row before UPSERT. Given the main DB totals are secondary to the analytics DB (which is the source of truth for the analytics view), the slight under-count is acceptable and will self-correct when the session snapshot is written (which contains total_tokens from OpenCode's authoritative data).
  
  Add a bounded capacity (e.g., 100,000 entries) with eviction to prevent unbounded memory growth for long-running Fleet instances.
  **Files**: `src/WeaveFleet.Infrastructure/Analytics/AnalyticsWriterService.cs`
  **Acceptance**: `IncrementTokensAsync` is called exactly once per unique `event_id` across duplicate events in the same or different batches.

- [x] 3. **Tighten OpenCodeMapper filter for all-zero token events**
  **What**: Change the filter on line 205 from:
  ```csharp
  if (assistant.Tokens is null && assistant.Cost is null)
      return null;
  ```
  to also skip events where tokens and cost are all zero:
  ```csharp
  if (assistant.Tokens is null && assistant.Cost is null)
      return null;

  var tokens = assistant.Tokens;
  var tokensTotal = tokens?.Total ?? 0;
  var cost = assistant.Cost ?? 0;

  // Skip events with no meaningful data — reduces unnecessary writes
  if (tokensTotal == 0 && cost == 0)
      return null;
  ```
  Move the `tokens` and `tokensTotal` and `cost` variable declarations (currently on lines 208-210) above this new check, since we now need them earlier. The existing logic that follows should continue to work as-is.
  **Files**: `src/WeaveFleet.Infrastructure/Harnesses/OpenCode/OpenCodeMapper.cs`
  **Acceptance**: Events with `tokens.total: 0` and `cost: 0` (or `cost: null`) return `null`. The existing test `TryExtractTokenEvent_AssistantMessageNoTokenData_ReturnsNull` still passes. New test confirms zero-data is filtered.

- [x] 4. **Add integration test: UPSERT overwrites partial data with higher totals**
  **What**: Add a test to `AnalyticsWriterServiceTests` that sends two `TokenEventData` instances with the same `event_id` but different `tokens_total` values (first: 100, second: 360). Assert that the DB contains exactly one row with `tokens_total = 360`.
  **Files**: `tests/WeaveFleet.Infrastructure.Tests/Analytics/AnalyticsWriterServiceTests.cs`
  **Acceptance**: Test passes; verifies final data wins.

- [x] 5. **Add integration test: UPSERT does NOT overwrite with lower totals**
  **What**: Add a test that sends two `TokenEventData` instances with the same `event_id` where the second has LOWER `tokens_total` (simulating out-of-order delivery). Assert that the DB retains the higher value.
  **Files**: `tests/WeaveFleet.Infrastructure.Tests/Analytics/AnalyticsWriterServiceTests.cs`
  **Acceptance**: Test passes; verifies out-of-order protection.

- [x] 6. **Add unit test: OpenCodeMapper skips all-zero token events**
  **What**: Add a test to `OpenCodeMapperTests` that constructs a `message.updated` event with `tokens: { total: 0 }` and `cost: 0` and asserts `TryExtractTokenEvent` returns `null`.
  **Files**: `tests/WeaveFleet.Infrastructure.Tests/Harnesses/OpenCode/OpenCodeMapperTests.cs`
  **Acceptance**: Test passes.

- [x] 7. **Update existing DuplicateEventId test to verify data correctness**
  **What**: The existing `DuplicateEventId_DoesNotCauseDuplicate` test (line 130) asserts `COUNT(*) = 1` but doesn't verify which event's data was retained. Update it to send two events where the second has higher totals, and assert the retained row has the higher values.
  **Files**: `tests/WeaveFleet.Infrastructure.Tests/Analytics/AnalyticsWriterServiceTests.cs`
  **Acceptance**: Test passes with data-level assertions.

- [x] 8. **Add integration test: IncrementTokensAsync called once per event_id**
  **What**: Add a test that sends two `TokenEventData` instances with the same `event_id`. Assert that `_sessionRepo.IncrementTokensAsync` was called exactly once (using NSubstitute's `.Received(1)` assertion).
  **Files**: `tests/WeaveFleet.Infrastructure.Tests/Analytics/AnalyticsWriterServiceTests.cs`
  **Acceptance**: Test passes; NSubstitute verifies single call.

## Verification
- [x] `dotnet build -c Release` succeeds with no warnings in both `WeaveFleet.Infrastructure` and `WeaveFleet.Infrastructure.Tests`
- [x] `dotnet test` passes — all existing and new tests green
- [x] No regressions in `ClaudeCodeMapperTests.TryExtractTokenEvent_*` tests (ClaudeCode path untouched)
- [ ] Manual smoke test: run a Fleet session with OpenCode, verify analytics view shows non-zero token counts after the session completes
