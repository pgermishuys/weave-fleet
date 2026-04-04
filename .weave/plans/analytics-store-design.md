# Analytics Store Design

## TL;DR

Weave Fleet has rich token usage data flowing through OpenCode SSE events but discards it entirely. This document evaluates three approaches for persisting analytics data (separate SQLite DB, append-only log files, OpenTelemetry metrics), recommends a **hybrid of Option A + Option C**, and sketches the concrete schema and integration points.

---

## Context

### The Gap

Token and cost data is fully modeled in the codebase:

- `OpenCodeAssistantMessage` carries `Cost` (double), `Tokens` (with `Input`, `Output`, `Reasoning`, `Cache.Read`, `Cache.Write`, `Total`), plus `ModelId` and `ProviderId`
- `Session` entity has `TotalTokens` and `TotalCost` fields, persisted in the `sessions` table
- `DapperSessionRepository.IncrementTokensAsync()` exists and is **never called**
- `FleetSummaryResponse` surfaces `TotalTokens`/`TotalCost` to the frontend via `GetFleetTokenTotalsAsync()`

The data arrives via SSE events from OpenCode (`OpenCodeHttpClient.SubscribeToEventsAsync`) → mapped by `OpenCodeMapper.ToHarnessEvent()` → streamed to the frontend via `SessionEventEndpoints` or `WebSocketEndpoints`. **No code intercepts this flow to extract and persist token data.**

### Current Architecture Facts

| Aspect | Detail |
|---|---|
| **Database** | Single SQLite file (`weave-fleet.db`), WAL mode, `busy_timeout=5000`, FK enabled |
| **Connection factory** | `SqliteConnectionFactory` — singleton, creates new connections per call from `FleetOptions.DatabasePath` |
| **Migrations** | Embedded SQL resources in `WeaveFleet.Infrastructure/Migrations/`, applied by `MigrationRunner` |
| **DI pattern** | `IDbConnectionFactory` registered as singleton; repositories registered as scoped |
| **Event system** | `InMemoryEventBroadcaster` — `Channel<T>`-based fan-out, topics: `sessions`, `instances`, `activity` |
| **OTEL** | `FleetInstrumentation.Meter` + `ActivitySource` singletons exist. OTLP exporter configured. No custom metrics defined. |
| **Deletion cascading** | `DeleteByProjectIdAsync` deletes sessions by project. `DeleteAsync` deletes individual sessions. No FK CASCADE — manual deletion. |

### Key Constraint

**Analytics must survive entity deletion.** When a session, project, or workspace is deleted from the main DB, the analytics for those entities must remain intact. This rules out storing analytics as FK-dependent rows in the main database.

---

## Design Options

### Option A: Separate SQLite Analytics Database

A second SQLite file (`weave-fleet-analytics.db`) alongside the main DB, with its own connection factory, migration runner, and WAL-mode configuration.

#### Data Flow

```
OpenCode SSE → OpenCodeHarnessInstance.SubscribeAsync()
     ↓
  [intercept point: new AnalyticsCollector service]
     ↓
  Channel<AnalyticsEvent> (in-memory buffer)
     ↓
  AnalyticsWriterService (BackgroundService, batched writes every N seconds)
     ↓
  weave-fleet-analytics.db (separate file, WAL mode)
```

#### Schema Sketch

```sql
-- Message-level token events (append-only fact table)
CREATE TABLE token_events (
    id INTEGER PRIMARY KEY AUTOINCREMENT,
    event_id TEXT NOT NULL UNIQUE,           -- dedup key
    session_id TEXT NOT NULL,                -- Weave session ID (snapshot, not FK)
    project_id TEXT,                         -- snapshot at event time
    project_name TEXT,                       -- snapshot (survives project rename/delete)
    workspace_directory TEXT,                -- snapshot
    model_id TEXT,                           -- e.g. "claude-sonnet-4-20250514"
    provider_id TEXT,                        -- e.g. "anthropic"
    tokens_input REAL NOT NULL DEFAULT 0,
    tokens_output REAL NOT NULL DEFAULT 0,
    tokens_reasoning REAL NOT NULL DEFAULT 0,
    tokens_cache_read REAL NOT NULL DEFAULT 0,
    tokens_cache_write REAL NOT NULL DEFAULT 0,
    tokens_total REAL NOT NULL DEFAULT 0,
    cost REAL NOT NULL DEFAULT 0,
    created_at TEXT NOT NULL                 -- ISO 8601
);

-- Session-level snapshots (written on session create + periodically updated)
CREATE TABLE session_snapshots (
    session_id TEXT PRIMARY KEY,
    project_id TEXT,
    project_name TEXT,
    workspace_directory TEXT,
    title TEXT,
    status TEXT,
    total_tokens REAL NOT NULL DEFAULT 0,
    total_cost REAL NOT NULL DEFAULT 0,
    message_count INTEGER NOT NULL DEFAULT 0,
    model_ids TEXT,                          -- JSON array of distinct models used
    created_at TEXT NOT NULL,
    ended_at TEXT,
    duration_seconds REAL
);

-- Daily rollups (materialized by background service)
CREATE TABLE daily_rollups (
    date TEXT NOT NULL,                      -- "2026-04-04"
    project_id TEXT,                         -- NULL = fleet-wide
    model_id TEXT,                           -- NULL = all models
    provider_id TEXT,                        -- NULL = all providers
    total_tokens REAL NOT NULL DEFAULT 0,
    total_cost REAL NOT NULL DEFAULT 0,
    session_count INTEGER NOT NULL DEFAULT 0,
    message_count INTEGER NOT NULL DEFAULT 0,
    PRIMARY KEY (date, COALESCE(project_id,''), COALESCE(model_id,''), COALESCE(provider_id,''))
);

CREATE INDEX idx_token_events_session ON token_events(session_id);
CREATE INDEX idx_token_events_created ON token_events(created_at);
CREATE INDEX idx_token_events_project ON token_events(project_id);
CREATE INDEX idx_token_events_model ON token_events(model_id);
CREATE INDEX idx_daily_rollups_date ON daily_rollups(date);
```

#### How It Survives Deletion

The analytics DB has no foreign keys to the main DB. Session/project IDs are stored as plain text snapshots. Deleting a session from `weave-fleet.db` has zero effect on `weave-fleet-analytics.db`.

#### Performance Characteristics

| Factor | Assessment |
|---|---|
| **Write contention** | None with main DB. Analytics DB gets batched writes (e.g. flush every 2s or every 50 events). WAL mode means readers never block writers. |
| **Memory** | `Channel<T>` buffer holds ~100 events max in practice (AI responses are not high-frequency). Negligible. |
| **Latency** | Zero impact on SSE event delivery — analytics collection is fire-and-forget into the channel. |
| **Disk** | ~200 bytes/row × 1000 messages/day = ~200KB/day. Years of data fit in < 100MB. |

#### Queryability / Harvesting

- Standard SQL queries via any SQLite client (DB Browser, `sqlite3` CLI, DBeaver)
- API endpoints can expose analytics (e.g. `GET /api/analytics/summary?from=&to=`)
- Export to CSV/JSON with a single SQL query
- Full ad-hoc query capability — any aggregation, grouping, filtering

#### Implementation Complexity: **Medium**

- New `IAnalyticsConnectionFactory` + `AnalyticsSqliteConnectionFactory`
- New `AnalyticsMigrationRunner` (reuse pattern from existing `MigrationRunner`)
- New `AnalyticsCollector` service (intercepts SSE events, pushes to channel)
- New `AnalyticsWriterService` (`BackgroundService`, reads channel, batches writes)
- New `AnalyticsRepository` (Dapper queries against analytics DB)
- ~8 new files, ~400 lines of code

#### Pros

- **Full SQL expressiveness** — any query you can imagine, you can run
- **Familiar pattern** — identical to existing SQLite + Dapper + WAL approach
- **Atomic batched writes** — reliable, transactional
- **Zero new dependencies** — uses Dapper + Microsoft.Data.Sqlite already in the project
- **Offline-first** — no external service needed
- **Schema evolution** — migrations are already a solved problem in this codebase

#### Cons

- **Two databases to manage** — backup, migration, corruption scenarios doubled
- **No real-time dashboarding** — need to build query endpoints (but the data is there)
- **Rollup computation** — needs a periodic background job to maintain `daily_rollups`

---

### Option B: Append-Only Log Files + Periodic Rollup

Raw analytics events written as JSONL (one JSON object per line) to append-only log files. A background service periodically reads the logs and computes summary tables.

#### Data Flow

```
OpenCode SSE → AnalyticsCollector
     ↓
  Channel<AnalyticsEvent>
     ↓
  AnalyticsFileWriter (BackgroundService)
     ↓
  ~/.weave/analytics/events/2026-04-04.jsonl  (append-only)
     ↓ (periodic rollup)
  ~/.weave/analytics/rollups/daily.json
  ~/.weave/analytics/rollups/by-project.json
  ~/.weave/analytics/rollups/by-model.json
```

#### File Structure

```
~/.weave/analytics/
├── events/
│   ├── 2026-04-04.jsonl        # One file per day
│   ├── 2026-04-03.jsonl
│   └── ...
├── rollups/
│   ├── daily.json              # Pre-computed daily summaries
│   ├── projects.json           # Per-project aggregates
│   └── models.json             # Per-model aggregates
└── sessions/
    └── {session-id}.json       # Session metadata snapshot
```

#### Event Format (JSONL line)

```json
{"ts":"2026-04-04T10:15:00Z","sid":"abc-123","pid":"proj-1","pname":"My Project","model":"claude-sonnet-4-20250514","provider":"anthropic","ti":1500,"to":450,"tr":200,"tcr":800,"tcw":100,"tt":2250,"cost":0.0234}
```

#### How It Survives Deletion

Files live in `~/.weave/analytics/`, completely outside the DB. Entity deletion never touches the filesystem.

#### Performance Characteristics

| Factor | Assessment |
|---|---|
| **Write contention** | Essentially zero — file append is an OS-level atomic operation for small writes. No DB locking at all. |
| **Memory** | Same Channel<T> buffer as Option A. Rollup computation may need to scan recent files (~KB). |
| **Latency** | Marginally faster than SQLite — no SQL parsing, no index maintenance. |
| **Disk** | Similar to Option A. JSONL is slightly larger per-event (~300 bytes vs ~200 bytes). |

#### Queryability / Harvesting

- JSONL files can be processed with `jq`, Python, PowerShell
- Pre-computed rollup JSON files serve common queries without scanning
- No ad-hoc SQL — custom queries require parsing JSONL
- Export is trivial — the files *are* the export format

#### Implementation Complexity: **Low-Medium**

- New `AnalyticsCollector` (same as Option A)
- New `AnalyticsFileWriter` (`BackgroundService`, appends JSONL)
- New `AnalyticsRollupService` (`BackgroundService`, periodic rollup computation)
- New `AnalyticsReader` (reads rollup JSONs for API endpoints)
- ~6 new files, ~300 lines of code

#### Pros

- **Simplest write path** — file append, no schema, no migrations
- **Zero contention** — no database involved in writes at all
- **Natural log format** — easy to ship to external tools (Splunk, Elastic, etc.)
- **Trivial backup** — copy the directory
- **Immutable audit trail** — append-only files are inherently tamper-evident

#### Cons

- **No ad-hoc queries** — can't `SELECT ... GROUP BY ... WHERE ...` against JSONL
- **Rollup staleness** — pre-computed summaries are only as fresh as the last rollup cycle
- **File management** — need rotation, cleanup, and size monitoring
- **Consistency risk** — crash during write can corrupt the last line (recoverable, but needs handling)
- **Cross-event queries are painful** — "show me all sessions that used model X and cost > $1" requires scanning all files
- **Schema evolution** — changing the JSONL format requires handling multiple versions during reads

---

### Option C: OpenTelemetry Metrics Pipeline

Leverage the existing OTEL infrastructure to define custom `Counter` and `Histogram` instruments on `FleetInstrumentation.Meter`. Token data becomes metrics exported to an OTLP collector.

#### Data Flow

```
OpenCode SSE → AnalyticsCollector (same intercept point)
     ↓
  FleetInstrumentation.Meter instruments
     ↓
  OTEL SDK auto-collects at export interval
     ↓
  OTLP exporter → Collector → Prometheus/Grafana/etc.
```

#### Metrics to Define

```csharp
// On FleetInstrumentation.Meter:
Counter<long>   TokensConsumed;        // tags: model, provider, project, token_type (input/output/reasoning/cache_read/cache_write)
Counter<double> CostIncurred;          // tags: model, provider, project
Counter<long>   MessagesProcessed;     // tags: model, provider, project, role (user/assistant)
Histogram<double> MessageCost;         // tags: model, provider — distribution of per-message costs
Histogram<long> MessageTokens;         // tags: model, provider, token_type — distribution of token counts
Counter<long>   SessionsCreated;       // tags: project
Counter<long>   SessionsCompleted;     // tags: project
```

#### How It Survives Deletion

OTEL metrics are inherently decoupled from the application database. Once exported to a collector/backend, they persist in whatever storage the collector uses (Prometheus, ClickHouse, etc.).

#### Performance Characteristics

| Factor | Assessment |
|---|---|
| **Write contention** | None — `Counter.Add()` and `Histogram.Record()` are lock-free, thread-safe, ~nanosecond operations |
| **Memory** | OTEL SDK maintains aggregated state per metric×tag combination. With ~10 models × ~5 projects × ~5 token types = ~250 time series. Negligible. |
| **Latency** | Near-zero — metric recording is the fastest of all three options |
| **Export overhead** | OTLP export at 60s intervals; tiny payload |

#### Queryability / Harvesting

- **With a collector**: Full PromQL/Grafana dashboard capability. Excellent for time-series analysis, alerting, dashboards.
- **Without a collector**: Data goes nowhere. The console exporter can dump to stdout, but that's not queryable.
- **No per-event detail**: Metrics are pre-aggregated. You can't query "what model did session X use?" — only "how many tokens did model Y consume across all sessions?"

#### Implementation Complexity: **Low**

- Add ~30 lines to `FleetInstrumentation.cs` defining instruments
- Add `AnalyticsCollector` service that calls `.Add()` / `.Record()` on each SSE event
- ~2 modified files + 1 new file, ~100 lines of code

#### Pros

- **Cheapest to implement** — leverages existing OTEL scaffolding entirely
- **Industry-standard observability** — dashboards, alerting, SLOs out of the box
- **Zero disk I/O** — metrics are in-memory until exported
- **Already wired** — `TelemetryExtensions.cs` already configures the `Meter` and OTLP exporter
- **Time-series native** — perfect for trend analysis, rate calculations, anomaly detection

#### Cons

- **Requires external infrastructure** — an OTLP collector must be running to capture anything. Without it, data is lost.
- **No per-event granularity** — can't drill down to individual messages or sessions
- **Pre-aggregated only** — metrics answer "how much" but not "which specific sessions"
- **Not self-contained** — breaks the "single binary, no dependencies" philosophy of Weave Fleet
- **No historical replay** — if the collector was down, that data is gone forever
- **Session-level correlation impossible** — OTEL metrics don't carry session context in a queryable way

---

## Comparison Matrix

| Criterion | Option A (SQLite) | Option B (JSONL) | Option C (OTEL) |
|---|---|---|---|
| **Ad-hoc queries** | Excellent | Poor | Moderate (needs collector) |
| **Per-event detail** | Yes | Yes | No |
| **Session correlation** | Yes | Yes (with scanning) | No |
| **Write performance** | Good (batched WAL) | Excellent (file append) | Excellent (lock-free) |
| **Self-contained** | Yes | Yes | No |
| **Implementation effort** | Medium (~400 LOC) | Low-Medium (~300 LOC) | Low (~100 LOC) |
| **Schema evolution** | Migrations (proven) | Manual versioning | N/A (tags are flexible) |
| **Real-time dashboards** | Build endpoints | Stale rollups | Native (Grafana) |
| **Survives deletion** | Yes | Yes | Yes |
| **External dependencies** | None | None | OTLP collector |
| **Export/harvest** | SQL → CSV/JSON | Files are export | PromQL/API |
| **Disk footprint** | ~200 KB/day | ~300 KB/day | 0 (in-memory) |

---

## Recommendation: Option A (Separate SQLite) + Option C (OTEL Metrics)

### Why a Hybrid

The two approaches are complementary and almost zero-cost to combine:

1. **Option A provides the "source of truth"** — detailed, per-event, queryable, self-contained analytics that survive entity deletion and require no external infrastructure. This is the primary store for the "harvestable data that helps improve Weave Fleet" requirement.

2. **Option C provides real-time observability for free** — since the OTEL scaffolding already exists and metric recording is ~5 lines of code, there's no reason not to emit `Counter.Add()` calls alongside the SQLite writes. Users who run an OTLP collector get dashboards; users who don't lose nothing.

3. **Option B is dominated** — JSONL gives you slightly faster writes than SQLite-WAL (both are fast enough that the difference is immaterial for AI message volumes) but dramatically worse queryability. The "natural log format" benefit doesn't justify giving up SQL.

### Why Not Option B

The "harvestable" requirement implies ad-hoc querying — "which model is most cost-effective?", "what's my cost trend over the last 30 days?", "which project consumed the most tokens?" These are trivial SQL queries and painful JSONL-scanning exercises. The write performance advantage of JSONL is irrelevant when we're writing ~1 event per AI response (not thousands per second).

### Why Not Option C Alone

OTEL metrics are the wrong tool for "analytics that help improve Weave Fleet." They answer rate/volume questions but can't answer instance-level questions like "that session was expensive — what model was it using?" or "show me the full cost breakdown for Project Alpha." The external infrastructure requirement also conflicts with Weave Fleet's single-binary, local-first philosophy.

---

## Recommended Architecture Detail

### Data to Collect

#### Per-Message (token_events table)

| Field | Source | Purpose |
|---|---|---|
| `session_id` | Weave Fleet session ID | Correlate events to sessions |
| `project_id` | Session's project at event time | Group by project |
| `project_name` | Project name snapshot | Readable even after project deletion |
| `workspace_directory` | Session's directory | Correlate to codebase |
| `model_id` | `OpenCodeAssistantMessage.ModelId` | Model usage analysis |
| `provider_id` | `OpenCodeAssistantMessage.ProviderId` | Provider cost comparison |
| `tokens_input` | `Tokens.Input` | Input token tracking |
| `tokens_output` | `Tokens.Output` | Output token tracking |
| `tokens_reasoning` | `Tokens.Reasoning` | Reasoning token tracking |
| `tokens_cache_read` | `Tokens.Cache.Read` | Cache efficiency analysis |
| `tokens_cache_write` | `Tokens.Cache.Write` | Cache efficiency analysis |
| `tokens_total` | `Tokens.Total` | Convenience total |
| `cost` | `Cost` | Dollar cost tracking |
| `created_at` | Event timestamp | Time-series analysis |

#### Per-Session (session_snapshots table)

| Field | Source | Purpose |
|---|---|---|
| `session_id` | Session.Id | Primary key |
| `project_id` | Session.ProjectId | Grouping |
| `project_name` | Project.Name (looked up) | Survives deletion |
| `workspace_directory` | Session.Directory | Context |
| `title` | Session.Title | Human-readable |
| `status` | Session.Status | Final state |
| `total_tokens` | Sum of token_events | Aggregate |
| `total_cost` | Sum of token_events | Aggregate |
| `message_count` | Count of token_events | Volume |
| `model_ids` | Distinct models from token_events | Model mix |
| `created_at` | Session creation time | Timeline |
| `ended_at` | Session stop time | Duration calc |
| `duration_seconds` | ended_at - created_at | Direct metric |

#### Daily Rollups (daily_rollups table)

Computed by a periodic background job (every 5 minutes or on-demand):

- Fleet-wide daily totals (tokens, cost, session count, message count)
- Per-project daily totals
- Per-model daily totals
- Per-provider daily totals

#### OTEL Metrics (emitted alongside SQLite writes)

```
weave_fleet.tokens.consumed        Counter<long>    {model, provider, project, token_type}
weave_fleet.cost.incurred          Counter<double>  {model, provider, project}
weave_fleet.messages.processed     Counter<long>    {model, provider, role}
weave_fleet.message.cost           Histogram<double>{model, provider}
weave_fleet.message.tokens         Histogram<long>  {model, provider, token_type}
weave_fleet.sessions.created       Counter<long>    {project}
weave_fleet.sessions.completed     Counter<long>    {project}
```

### Integration Points — Where to Intercept Token Data

The critical question is: **where in the event flow do we tap into token data?**

#### Current Flow (no analytics)

```
OpenCode process → SSE → OpenCodeHttpClient.SubscribeToEventsAsync()
    → OpenCodeMapper.ToHarnessEvent() → HarnessEvent
    → SessionEventEndpoints (SSE to frontend)
    → WebSocketEndpoints (WS to frontend)
```

#### Proposed Intercept Point

The cleanest intercept is in `OpenCodeHarnessInstance.SubscribeAsync()`. Currently this method yields `HarnessEvent` objects. We need to either:

**(a) Enrich the HarnessEvent** — add token/cost data to `HarnessEvent` so downstream consumers can see it. This requires changing the domain type.

**(b) Side-channel extraction** — before yielding the `HarnessEvent`, check if the underlying SSE event is a `message.updated` or `message.created` event containing an assistant message with token data, and if so, push an analytics event to the collector channel.

**(c) Subscriber-based collection** — add an `IAnalyticsCollector` that subscribes to the event broadcaster as another consumer, parses relevant events, and extracts token data.

**Recommended: Option (b) — side-channel in the mapper layer.** The `OpenCodeMapper.ToHarnessEvent()` method already has access to the raw `OpenCodeSseEvent` with its `Properties` JSON. We add a new method `TryExtractTokenEvent()` that attempts to parse token data from the SSE event payload. The `AnalyticsCollector` singleton accepts these events via a `Channel<T>`.

Why not (c)? The `InMemoryEventBroadcaster` delivers `HarnessEvent` objects where the `Payload` is already a `JsonElement`. To extract token data, we'd have to re-parse the JSON to find `cost`/`tokens` fields. With (b), we're already in the layer where we have the typed `OpenCodeAssistantMessage` available (or can deserialize it from the SSE event properties).

### Proposed File Layout

```
src/WeaveFleet.Application/
  Analytics/
    IAnalyticsCollector.cs            -- Interface: AcceptTokenEvent(), AcceptSessionEvent()
    IAnalyticsReader.cs               -- Interface: query methods for API endpoints
    AnalyticsEvent.cs                 -- DTOs: TokenEvent, SessionSnapshotEvent

src/WeaveFleet.Infrastructure/
  Analytics/
    AnalyticsSqliteConnectionFactory.cs   -- Separate DB connection factory
    AnalyticsMigrationRunner.cs           -- Reuses MigrationRunner pattern
    AnalyticsCollector.cs                 -- Implements IAnalyticsCollector, buffers via Channel<T>
    AnalyticsWriterService.cs             -- BackgroundService: reads channel, batched writes
    AnalyticsRepository.cs                -- Dapper queries against analytics DB
    AnalyticsRollupService.cs             -- BackgroundService: periodic rollup computation
  Analytics/Migrations/
    001_analytics_initial.sql             -- Schema creation

src/WeaveFleet.Api/
  Endpoints/
    AnalyticsEndpoints.cs                 -- GET /api/analytics/* endpoints
```

### Configuration

Add to `FleetOptions`:

```csharp
/// <summary>Path to the analytics SQLite database file. Default: weave-fleet-analytics.db alongside main DB.</summary>
public string AnalyticsDatabasePath { get; set; } = "weave-fleet-analytics.db";

/// <summary>Enable analytics collection. Default: true.</summary>
public bool AnalyticsEnabled { get; set; } = true;

/// <summary>Batch flush interval for analytics writes in seconds. Default: 2.</summary>
public int AnalyticsFlushIntervalSeconds { get; set; } = 2;

/// <summary>Maximum batch size for analytics writes. Default: 50.</summary>
public int AnalyticsMaxBatchSize { get; set; } = 50;
```

### Key Design Decisions

1. **Separate connection factory, not a named registration.** Register `AnalyticsSqliteConnectionFactory` as a distinct singleton (not via keyed services), injected directly into analytics components. This avoids any confusion or accidental cross-wiring with the main DB.

2. **Channel<T> buffer with bounded capacity.** Use `BoundedChannelOptions` with a generous capacity (10,000) and `FullMode = DropOldest`. Analytics should never exert back-pressure on the session event pipeline.

3. **Idempotent writes with event_id.** Each `token_events` row has a unique `event_id` derived from the SSE event (e.g., `{sessionId}:{messageId}`). The `UNIQUE` constraint + `INSERT OR IGNORE` ensures replay safety.

4. **Session snapshots written on create + update on stop.** When a session is created, write a snapshot row with metadata. When a session stops, update with final aggregates and duration. This captures sessions that get deleted before stopping — the snapshot already exists.

5. **Rollup computation is purely derived.** The `daily_rollups` table can be fully recomputed from `token_events` at any time. The periodic job is an optimization, not a source of truth.

6. **Also update the main DB's TotalTokens/TotalCost.** The existing `IncrementTokensAsync()` method should finally be called so the fleet summary endpoint returns real data. This is a small side-effect of the analytics work, not the primary storage.

### Harvesting / Export API

Minimal API endpoints for common queries:

```
GET /api/analytics/summary
    ?from=2026-04-01&to=2026-04-04
    → { totalTokens, totalCost, sessionCount, messageCount, topModels[], topProjects[] }

GET /api/analytics/daily
    ?from=2026-04-01&to=2026-04-04&projectId=...
    → [{ date, tokens, cost, sessions, messages }]

GET /api/analytics/sessions
    ?from=...&to=...&projectId=...&limit=50
    → [{ sessionId, title, project, tokens, cost, models[], duration }]

GET /api/analytics/models
    ?from=...&to=...
    → [{ modelId, providerId, tokens, cost, messageCount, avgCostPerMessage }]

GET /api/analytics/export
    ?from=...&to=...&format=csv
    → CSV download of token_events
```

### Implementation Order

The work naturally layers:

1. **Schema + connection factory + migration** — the foundation
2. **Analytics event DTOs + IAnalyticsCollector interface** — the contract
3. **AnalyticsCollector + Channel<T> buffer** — the ingestion path
4. **SSE event interception in OpenCodeMapper/HarnessInstance** — the data source
5. **AnalyticsWriterService (BackgroundService)** — persistence
6. **Wire up IncrementTokensAsync** — fix the existing gap in the main DB
7. **OTEL metrics on FleetInstrumentation.Meter** — the observability layer
8. **AnalyticsRollupService** — periodic aggregation
9. **AnalyticsEndpoints** — query/export API
10. **Tests** — unit tests for collector, writer, rollup logic

Steps 1–6 form the MVP. Steps 7–9 are valuable but independent enhancements.

### Potential Pitfalls

| Risk | Mitigation |
|---|---|
| **SSE event format changes** | `TryExtractTokenEvent` returns null on parse failure. Analytics silently degrades — never crashes the session. |
| **Analytics DB corruption** | WAL mode + batched transactions minimize risk. Worst case: delete and recreate the analytics DB (it's derivable from future events, and historical data is lost — acceptable for analytics). |
| **Background service crash** | `BackgroundService` has built-in restart. Channel acts as buffer. Log warnings on repeated failures. |
| **High message volume** | Bounded channel with `DropOldest`. Analytics loss is acceptable under extreme load. The system favors session stability over analytics completeness. |
| **Clock skew between SSE events and server time** | Use the SSE event's own timestamp (`Time.Created`) as the canonical timestamp, not `DateTime.UtcNow`. |
| **Duplicate events from SSE reconnection** | Idempotent writes via `event_id` + `INSERT OR IGNORE`. |
| **Analytics DB grows unbounded** | Add a configurable retention policy (e.g., drop `token_events` older than 90 days). Keep `daily_rollups` forever — they're tiny. |

---

## Open Questions for Discussion

1. **Retention policy**: How long should raw `token_events` be kept? 30 days? 90 days? Forever? Rollups can be kept indefinitely regardless.

2. **Real-time fleet summary**: Should `GetFleetSummaryAsync` read from the analytics DB instead of summing the main sessions table? This would give accurate totals even after session deletion.

3. **Frontend integration**: Should the Next.js client have analytics pages (charts, cost breakdowns), or is CLI/API access sufficient for now?

4. **Cross-session attribution**: Should we track parent→child token attribution for forked sessions? The `parent_session_id` data is available.

5. **Cost estimation**: OpenCode provides actual cost per message. Should we also store estimated costs for comparison (e.g., based on published model pricing)?
