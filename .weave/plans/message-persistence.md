# Message Persistence

## TL;DR
> **Summary**: Persist agent messages to Fleet's SQLite database as they stream in from OpenCode SSE events, then serve them from DB when no live instance exists — enabling message history for stopped/disconnected sessions.
> **Estimated Effort**: Medium

## Context
### Original Request
Users navigating to previous (stopped) sessions see "Failed to load initial messages — scroll up to retry" because `SessionOrchestrator.GetSessionMessagesAsync()` requires a live `IHarnessInstance` via `GetLiveInstanceAsync()`. Messages only live in the OpenCode process memory. When that process is gone, messages are gone.

### Key Findings

**Current message flow:**
1. Frontend calls `GET /api/sessions/{id}/messages?limit=N&before=CURSOR`
2. `SessionEndpoints.cs` → `SessionOrchestrator.GetSessionMessagesAsync()`
3. Orchestrator calls `GetLiveInstanceAsync()` → looks up session in DB → gets instance from `InstanceTracker` (in-memory `ConcurrentDictionary`)
4. If instance found → `instance.GetMessagesAsync()` → `OpenCodeHttpClient.GetMessagesAsync()` → HTTP call to live OpenCode process
5. If no instance → returns `FleetError.NotFoundFor("Instance", ...)` → **404 to frontend**

**SSE event flow (where messages appear):**
1. `HarnessEventRelay` (BackgroundService) subscribes to `InstanceTracker.InstanceRegistered`
2. For each instance → starts a pump that calls `instance.SubscribeAsync()` → yields `HarnessEvent` objects
3. Events are broadcast via `IEventBroadcaster` to topic `session:{fleetSessionId}`
4. SSE event types include `message.created`, `message.updated` — these contain full message data in `Properties`
5. Analytics already intercepts `message.updated` events in `OpenCodeMapper.TryExtractTokenEvent()` for token/cost tracking

**Existing patterns:**
- Dapper repositories with `IDbConnectionFactory` — scoped lifetime, pattern in `DapperSessionRepository`
- SQL migrations as embedded resources in `src/WeaveFleet.Infrastructure/Migrations/*.sql`
- `MigrationRunner` auto-discovers `*.sql` files in the `Migrations` folder segment
- Domain entities in `src/WeaveFleet.Domain/Entities/` — plain classes with public setters (Dapper mapping)
- Repository interfaces in `src/WeaveFleet.Domain/Repositories/`
- `HarnessMessage` record has: `Id`, `Role`, `Parts` (polymorphic `MessagePart` list), `Timestamp`
- `MessagePage` record has: `Messages` (list), `HasMore` (bool)
- `MessageQuery` record has: `Limit` (int?), `Before` (string?)
- Configuration via `FleetOptions` class bound from `appsettings.json`

**SSE event structure for messages:**
- `message.created` / `message.updated` events carry the full message in `Properties`
- `Properties` contains an `info` object with `id`, `role`, `sessionId`, `time` etc.
- `Properties` contains a `parts` array with typed message parts
- The `HarnessEventRelay` already rewrites `sessionId` fields to Fleet session IDs
- The relay already resolves `fleetSessionId` from `instanceId` via `ISessionRepository`

## Objectives
### Core Objective
Enable message retrieval for sessions that don't have a live OpenCode instance, by persisting messages to SQLite as they stream in and falling back to DB reads when no instance is available.

### Deliverables
- [ ] SQLite migration adding `messages` table
- [ ] Domain entity `PersistedMessage` and repository interface `IMessageRepository`
- [ ] Dapper repository implementation `DapperMessageRepository`
- [ ] Message persistence interceptor in `HarnessEventRelay` (writes to DB as events stream)
- [ ] Fallback logic in `SessionOrchestrator.GetSessionMessagesAsync()` (DB when no instance)
- [ ] Page size configuration in `FleetOptions`
- [ ] Frontend page size update (50 → 10)
- [ ] Unit tests for repository, interceptor, and orchestrator fallback

### Definition of Done
- [ ] `dotnet build` succeeds with zero warnings
- [ ] `dotnet test` passes all existing + new tests
- [ ] Stopped sessions return message history from DB via same API endpoint
- [ ] Live sessions continue to proxy messages from OpenCode (existing behavior)
- [ ] SSE message events are persisted without blocking the event stream
- [ ] Cursor-based pagination works identically for both live and DB sources

### Guardrails (Must NOT)
- Must NOT change the API response shape — frontend contract stays identical
- Must NOT block SSE event delivery on DB write failures
- Must NOT introduce EF Core — continue using Dapper
- Must NOT add encryption (deferred to infrastructure layer)
- Must NOT change `IHarnessInstance` interface

## TODOs

- [ ] 1. **Add `messages` table migration**
  **What**: Create `002_add_messages_table.sql` with the messages table schema. Store messages with their parts serialized as JSON (since parts are polymorphic and variable-length, a single JSON column is simpler and sufficient vs. a normalized parts table).
  **Files**:
  - Create `src/WeaveFleet.Infrastructure/Migrations/002_add_messages_table.sql`
  **Schema**:
  ```sql
  CREATE TABLE messages (
    id TEXT NOT NULL,
    session_id TEXT NOT NULL REFERENCES sessions(id) ON DELETE CASCADE,
    role TEXT NOT NULL,
    parts_json TEXT NOT NULL,
    timestamp TEXT NOT NULL,
    created_at TEXT NOT NULL DEFAULT (datetime('now')),
    PRIMARY KEY (id, session_id)
  );

  -- Cursor-based pagination: fetch messages for a session ordered by timestamp descending
  CREATE INDEX idx_messages_session_timestamp ON messages(session_id, timestamp DESC);
  ```
  **Design decisions**:
  - Composite PK `(id, session_id)` — message IDs come from OpenCode and are unique within a session but we can't guarantee global uniqueness. The composite PK also provides automatic deduplication via `INSERT OR REPLACE` (latest version always wins).
  - `parts_json` stores the full `IReadOnlyList<MessagePart>` serialized as JSON using the existing `System.Text.Json` polymorphic serialization. This avoids a complex normalized schema for the polymorphic part types.
  - `timestamp` stored as ISO 8601 text (consistent with all other timestamp columns in the schema).
  - `ON DELETE CASCADE` ensures messages are cleaned up when a session is deleted.
  - `created_at` tracks when Fleet persisted the message (separate from the message's own timestamp from OpenCode).
  **Acceptance**: Migration runs successfully; `sqlite3 weave-fleet.db '.schema messages'` shows the table.

- [ ] 2. **Add `PersistedMessage` entity**
  **What**: Create a domain entity that maps to the `messages` table. This is distinct from `HarnessMessage` (which is a harness-layer record) — `PersistedMessage` is a persistence-layer entity with Dapper-friendly properties.
  **Files**:
  - Create `src/WeaveFleet.Domain/Entities/PersistedMessage.cs`
  **Shape**:
  ```csharp
  public sealed class PersistedMessage
  {
      public string Id { get; set; } = string.Empty;
      public string SessionId { get; set; } = string.Empty;
      public string Role { get; set; } = string.Empty;
      public string PartsJson { get; set; } = "[]";
      public string Timestamp { get; set; } = string.Empty;
      public string CreatedAt { get; set; } = string.Empty;
  }
  ```
  **Acceptance**: File compiles; entity follows existing pattern from `Session.cs`.

- [ ] 3. **Add `IMessageRepository` interface**
  **What**: Define the repository contract for message persistence and retrieval.
  **Files**:
  - Create `src/WeaveFleet.Domain/Repositories/IMessageRepository.cs`
  **Interface**:
  ```csharp
  public interface IMessageRepository
  {
      /// <summary>
      /// Upsert a message. Uses INSERT OR REPLACE to always keep the latest
      /// version (complete response, not the initial skeleton from message.created).
      /// </summary>
      Task UpsertAsync(PersistedMessage message);

      /// <summary>
      /// Batch upsert multiple messages in a single transaction.
      /// </summary>
      Task UpsertBatchAsync(IReadOnlyList<PersistedMessage> messages);

      /// <summary>
      /// Retrieve messages for a session with cursor-based pagination.
      /// Returns messages ordered by timestamp ascending (oldest first),
      /// matching the order returned by OpenCode's live API.
      /// </summary>
      Task<IReadOnlyList<PersistedMessage>> GetBySessionAsync(
          string sessionId, int limit, string? beforeMessageId);

      /// <summary>
      /// Count total messages for a session (used to determine hasMore).
      /// </summary>
      Task<int> CountBySessionAsync(string sessionId);

      /// <summary>
      /// Check if any messages exist for a session.
      /// </summary>
      Task<bool> HasMessagesAsync(string sessionId);

      /// <summary>
      /// Delete all messages for a session (called when session is deleted).
      /// Note: ON DELETE CASCADE handles this at DB level, but explicit
      /// method useful for testing and non-cascade scenarios.
      /// </summary>
      Task DeleteBySessionAsync(string sessionId);
  }
  ```
  **Acceptance**: Interface compiles; follows naming pattern of other repository interfaces.

- [ ] 4. **Implement `DapperMessageRepository`**
  **What**: Dapper-based implementation of `IMessageRepository`. Key concern: the cursor-based pagination query must find the timestamp of the `before` message and return messages older than that.
  **Files**:
  - Create `src/WeaveFleet.Infrastructure/Data/Repositories/DapperMessageRepository.cs`
  **Implementation notes**:
  - `UpsertAsync`: Use `INSERT OR REPLACE INTO messages ...` — the composite PK `(id, session_id)` handles deduplication naturally. `INSERT OR REPLACE` ensures we always keep the latest version of a message (complete assistant response with all tool results, not the initial empty skeleton from `message.created`).
  - `UpsertBatchAsync`: Wrap multiple `INSERT OR REPLACE` in a single transaction for efficiency.
  - `GetBySessionAsync` without cursor: `SELECT * FROM messages WHERE session_id = @SessionId ORDER BY timestamp DESC LIMIT @Limit` then reverse the result to ascending order.
  - `GetBySessionAsync` with cursor: First lookup the cursor message's timestamp, then `SELECT * FROM messages WHERE session_id = @SessionId AND timestamp < @CursorTimestamp ORDER BY timestamp DESC LIMIT @Limit`, then reverse. Edge case: if two messages share the same timestamp, use `(timestamp, id) < (@CursorTimestamp, @CursorId)` compound comparison for deterministic cursor behavior.
  - Constructor takes `IDbConnectionFactory` (same pattern as all other Dapper repos).
  **Acceptance**: Unit tests pass for insert, dedup, pagination, and cursor behavior.

- [ ] 5. **Add `MessagePersistenceService` for serialization logic**
  **What**: A small application-layer service that converts between `HarnessMessage` and `PersistedMessage`, encapsulating the JSON serialization/deserialization of the polymorphic `Parts` list. This keeps serialization concerns out of both the domain entity and the repository.
  **Files**:
  - Create `src/WeaveFleet.Application/Services/MessagePersistenceService.cs`
  **Responsibilities**:
  - `ToPersistedMessage(string sessionId, HarnessMessage message) → PersistedMessage` — serializes `Parts` to JSON using `System.Text.Json` with the polymorphic `[JsonPolymorphic]` configuration already on `MessagePart`.
  - `ToHarnessMessage(PersistedMessage persisted) → HarnessMessage` — deserializes `PartsJson` back to `IReadOnlyList<MessagePart>`.
  - `ToHarnessMessages(IReadOnlyList<PersistedMessage> persisted) → IReadOnlyList<HarnessMessage>` — batch conversion.
  **Why separate service**: The `MessagePart` hierarchy uses `[JsonPolymorphic]` with `[JsonDerivedType]` discriminators. Serialization options must match (e.g., camelCase policy). Centralizing this prevents subtle serialization mismatches.
  **Acceptance**: Round-trip test: `HarnessMessage → PersistedMessage → HarnessMessage` preserves all fields including polymorphic parts.

- [ ] 6. **Add page size configuration to `FleetOptions`**
  **What**: Add two new properties to `FleetOptions` for configurable page sizes.
  **Files**:
  - Modify `src/WeaveFleet.Application/Configuration/FleetOptions.cs`
  **Changes**:
  ```csharp
  /// <summary>Default page size when fetching messages from a live instance. Default: 10.</summary>
  public int LiveMessagePageSize { get; set; } = 10;

  /// <summary>Default page size when fetching messages from the database (history). Default: 10.</summary>
  public int HistoryMessagePageSize { get; set; } = 10;
  ```
  **Acceptance**: Properties compile; defaults are 10.

- [ ] 7. **Intercept SSE messages in `HarnessEventRelay` for persistence**
  **What**: Add message persistence as a fire-and-forget side-channel in the existing `PumpAsync` method of `HarnessEventRelay`. This is the natural interception point because:
  - The relay already iterates every SSE event per instance
  - It already resolves the `fleetSessionId` from the instance ID
  - It already creates DI scopes for repository access
  - The analytics interceptor in `OpenCodeHarnessInstance.SubscribeAsync()` sets the pattern for side-channel processing

  **Files**:
  - Modify `src/WeaveFleet.Infrastructure/Services/HarnessEventRelay.cs`

  **Approach**:
  - Resolve `IMessageRepository` and `MessagePersistenceService` from a scoped DI scope (same pattern as the session lookup already in `PumpAsync`)
  - In `PumpAsync`, after broadcasting each event, check if `evt.Type` is `"message.created"` or `"message.updated"`
  - For qualifying events, extract the message from `evt.Payload` (a `JsonElement`) — see extraction logic below
  - Convert to `PersistedMessage` via `MessagePersistenceService` and call `IMessageRepository.UpsertAsync()`
  - Wrap the entire persistence path in try/catch — **never** let a DB failure propagate to the SSE event stream
  - Create a new DI scope per persistence operation to avoid holding a DB connection open for the entire event stream lifetime

  **Why `HarnessEventRelay` and not `OpenCodeHarnessInstance.SubscribeAsync()`**:
  - `SubscribeAsync` is in the Infrastructure layer and already has one side-channel (analytics). Adding another couples it to more concerns.
  - `HarnessEventRelay` already does the fleet session ID resolution needed for the `session_id` foreign key.
  - `HarnessEventRelay` has access to `IServiceScopeFactory` for scoped repository resolution.
  - `OpenCodeHarnessInstance` is created by `OpenCodeHarness.SpawnAsync()` which would need additional constructor parameters, complicating an already-long constructor.

  **Important context on event shape**: By the time events reach `HarnessEventRelay`, they are `HarnessEvent` objects (mapped from `OpenCodeSseEvent` via `OpenCodeMapper.ToHarnessEvent()`). The `HarnessEvent.Payload` is a `JsonElement` which is the raw `OpenCodeSseEvent.Properties` — the same JSON that `TryExtractTokenEvent` reads from. The `Payload` for message events has this shape:
  ```json
  {
    "info": { "id": "...", "role": "user"|"assistant", "time": { "created": 1234567890 }, ... },
    "parts": [ { "type": "text", "text": "..." }, ... ]
  }
  ```
  This is the `OpenCodeMessageWithParts` structure, which can be deserialized from the `Payload` JsonElement.

  **Message extraction logic** (new private method `TryPersistMessageAsync`):
  ```
  1. Check evt.Type is "message.created" or "message.updated"
  2. Check evt.Payload is not null and has JsonValueKind.Object
  3. Check Payload has "info" property (same guard pattern as TryExtractTokenEvent in OpenCodeMapper)
  4. Check role is "user" or "assistant" (read directly from info.role string, same approach as TryExtractTokenEvent)
  5. Deserialize Payload as OpenCodeMessageWithParts using OpenCodeJsonOptions.Default
  6. Convert via OpenCodeMapper.ToHarnessMessage(openCodeMessage)
  7. Convert via MessagePersistenceService.ToPersistedMessage(fleetSessionId, harnessMessage)
  8. Upsert via IMessageRepository.UpsertAsync()
  ```

  **Note**: Step 5 requires `OpenCodeMessageWithParts` and `OpenCodeMapper` which are in the `WeaveFleet.Infrastructure.Harnesses.OpenCode` namespace. Since `HarnessEventRelay` is also in `WeaveFleet.Infrastructure`, this is an internal reference — no cross-project dependency issues.

  **Error handling**: Log at Warning level on failure, never re-throw. Use same pattern as analytics: `catch { // Silent failure — persistence must never crash event relay }`.

  **Note on `message.created` vs `message.updated`**: The analytics interceptor (`TryExtractTokenEvent`) intentionally skips `message.created` because its structure wasn't verified against a contract fixture at the time. For message persistence we process **both** event types because: (a) `INSERT OR REPLACE` means the latest write always wins regardless of which event arrives first, (b) user messages may only emit `message.created` without subsequent updates, and (c) the try/catch wrapper means any deserialization failure is silently caught. If `message.created` has an incompatible structure for some message types, the worst case is a caught exception — the `message.updated` event will succeed and persist the final state.

  **Performance note**: `message.updated` events fire frequently (every token chunk). The `INSERT OR REPLACE` with composite PK means repeated writes for the same message ID update the row in place. This is acceptable for SQLite's write throughput. If it becomes a bottleneck, batch writes can be introduced later (same pattern as `AnalyticsWriterService`'s bounded channel).

  **Acceptance**: Messages appear in the `messages` table after an agent conversation; SSE delivery is unaffected by DB failures.

- [ ] 8. **Add DB fallback in `SessionOrchestrator.GetSessionMessagesAsync()`**
  **What**: Modify the orchestrator to fall back to DB when no live instance exists, instead of returning a 404.
  **Files**:
  - Modify `src/WeaveFleet.Application/Services/SessionOrchestrator.cs`

  **Current logic** (lines 265-284):
  ```csharp
  public async Task<Result<MessagePage>> GetSessionMessagesAsync(
      string id, MessageQuery? query = null, CancellationToken ct = default)
  {
      var instanceResult = await GetLiveInstanceAsync(id);
      if (instanceResult.IsFailure)
          return instanceResult.Error;  // ← This is the problem
      // ... proxy to live instance
  }
  ```

  **New logic**:
  ```csharp
  public async Task<Result<MessagePage>> GetSessionMessagesAsync(
      string id, MessageQuery? query = null, CancellationToken ct = default)
  {
      // Validate session exists
      var session = await sessionRepository.GetByIdAsync(id);
      if (session is null)
          return FleetError.NotFoundFor(nameof(Session), id);

      // Try live instance first
      var instance = instanceTracker.Get(session.InstanceId);
      if (instance is not null)
      {
          try
          {
              var liveLimit = query?.Limit ?? options.LiveMessagePageSize;
              var page = await instance.GetMessagesAsync(
                  new MessageQuery(liveLimit, query?.Before), ct);
              return Result.Success(page);
          }
          catch (Exception ex) when (ex is not OperationCanceledException)
          {
              LogGetMessagesFailed(ex, id);
              // Fall through to DB — instance might be in a bad state
          }
      }

      // Fall back to persisted messages
      return await GetPersistedMessagesAsync(id, query, ct);
  }
  ```

  **New private method** `GetPersistedMessagesAsync`:
  - Resolve page size: `query?.Limit ?? options.HistoryMessagePageSize`
  - Call `messageRepository.GetBySessionAsync(sessionId, limit, query?.Before)`
  - Convert via `messagePersistenceService.ToHarnessMessages()`
  - Determine `hasMore`: request `limit + 1` rows from DB, if we get more than `limit` there are more
  - Return `MessagePage` with the converted messages

  **Constructor changes**: Add `IMessageRepository`, `MessagePersistenceService`, and `FleetOptions` to the primary constructor parameters.

  **Acceptance**: `GET /api/sessions/{stopped-session-id}/messages` returns persisted messages instead of 404.

- [ ] 9. **Register new services in DI**
  **What**: Wire up the new repository and service in `DependencyInjection.cs`.
  **Files**:
  - Modify `src/WeaveFleet.Infrastructure/DependencyInjection.cs`
  **Changes**:
  - Add `services.AddScoped<IMessageRepository, DapperMessageRepository>();` alongside other repository registrations (line ~48)
  - Add `services.AddScoped<MessagePersistenceService>();` alongside other application services (line ~56)
  **Acceptance**: Application starts without DI resolution failures.

- [ ] 10. **Update frontend page size**
  **What**: Change `DEFAULT_PAGE_SIZE` from 50 to 10 in the frontend pagination hook. This aligns with the new backend defaults and reduces initial load when served from DB.
  **Files**:
  - Modify `client/src/hooks/use-message-pagination.ts`
  **Changes**:
  - Line 52: `const DEFAULT_PAGE_SIZE = 50;` → `const DEFAULT_PAGE_SIZE = 10;`
  **Acceptance**: Frontend compiles; initial message load requests `limit=10`.

- [ ] 11. **Add unit tests for `DapperMessageRepository`**
  **What**: Test the repository against an in-memory SQLite database using the existing `TestDbHelper` pattern.
  **Files**:
  - Create `tests/WeaveFleet.Infrastructure.Tests/Data/Repositories/DapperMessageRepositoryTests.cs`
  **Test cases**:
  - [ ] `UpsertAsync_InsertsNewMessage`
  - [ ] `UpsertAsync_ReplacesExistingMessage` (latest version wins)
  - [ ] `GetBySessionAsync_ReturnsMessagesInAscendingTimestampOrder`
  - [ ] `GetBySessionAsync_RespectsLimit`
  - [ ] `GetBySessionAsync_WithCursor_ReturnsOlderMessages`
  - [ ] `GetBySessionAsync_EmptySession_ReturnsEmpty`
  - [ ] `CountBySessionAsync_ReturnsCorrectCount`
  - [ ] `DeleteBySessionAsync_RemovesAllMessages`
  - [ ] `UpsertBatchAsync_InsertsMultipleMessages`
  - [ ] `CascadeDelete_RemovesMessagesWhenSessionDeleted`
  **Acceptance**: All tests pass with `dotnet test`.

- [ ] 12. **Add unit tests for `MessagePersistenceService`**
  **What**: Test round-trip serialization of polymorphic message parts.
  **Files**:
  - Create `tests/WeaveFleet.Application.Tests/Services/MessagePersistenceServiceTests.cs` (or in nearest existing test project)
  **Test cases**:
  - [ ] `RoundTrip_TextPart_PreservesContent`
  - [ ] `RoundTrip_ToolUsePart_PreservesAllFields`
  - [ ] `RoundTrip_ToolResultPart_PreservesContent`
  - [ ] `RoundTrip_MixedParts_PreservesOrder`
  - [ ] `ToPersistedMessage_SetsSessionIdAndTimestamp`
  **Acceptance**: All tests pass with `dotnet test`.

- [ ] 13. **Add unit tests for orchestrator fallback**
  **What**: Test the new `GetSessionMessagesAsync` fallback logic.
  **Files**:
  - Create or modify tests in the appropriate test project for `SessionOrchestrator`
  **Test cases**:
  - [ ] `GetSessionMessages_LiveInstance_ProxiesToInstance`
  - [ ] `GetSessionMessages_NoInstance_FallsBackToDb`
  - [ ] `GetSessionMessages_NoInstance_NoDbMessages_ReturnsEmptyPage`
  - [ ] `GetSessionMessages_LiveInstanceThrows_FallsBackToDb`
  - [ ] `GetSessionMessages_SessionNotFound_ReturnsNotFound`
  - [ ] `GetSessionMessages_UsesHistoryPageSizeForDbFallback`
  - [ ] `GetSessionMessages_UsesLivePageSizeForLiveInstance`
  **Acceptance**: All tests pass with `dotnet test`.

- [x] 14. **Add integration-level test for message persistence via event relay**
  **What**: Test that the event relay correctly persists messages from SSE events.
  **Files**:
  - Create or extend `tests/WeaveFleet.Infrastructure.Tests/Services/HarnessEventRelayTests.cs`
  **Test cases**:
  - [ ] `PumpAsync_MessageCreatedEvent_PersistsMessage`
  - [ ] `PumpAsync_MessageUpdatedEvent_UpsertsMessage`
  - [ ] `PumpAsync_NonMessageEvent_DoesNotPersist`
  - [ ] `PumpAsync_PersistenceFailure_DoesNotBlockEventDelivery`
  **Acceptance**: All tests pass with `dotnet test`.

## Verification
- [ ] `dotnet build src/WeaveFleet.Api/` succeeds with zero warnings
- [ ] `dotnet test` passes all existing tests (no regressions)
- [ ] `dotnet test` passes all new tests
- [ ] Manual verification: create session, send messages, stop session, navigate to stopped session → messages load from DB
- [ ] Manual verification: create session, send messages → messages load from live instance (existing behavior preserved)
- [ ] Manual verification: view stopped session, resume it → old messages from DB, new messages from live instance

## Architecture Notes

### Why `HarnessEventRelay` is the right interception point

| Option | Pros | Cons |
|--------|------|------|
| **`HarnessEventRelay.PumpAsync`** ✅ | Already iterates all events; has fleet session ID; has DI scope access; clean separation from harness layer | Couples relay to persistence (mitigated: fire-and-forget pattern) |
| `OpenCodeHarnessInstance.SubscribeAsync` | Closest to source | No fleet session ID; no DI access; already has analytics interceptor; long constructor |
| New BackgroundService | Maximum separation | Duplicates the event subscription; extra complexity |
| Middleware/filter on API endpoint | — | Only captures read events, not the full stream |

### Message deduplication strategy

OpenCode emits both `message.created` and multiple `message.updated` events for the same message ID. We use `INSERT OR REPLACE` with the composite PK `(id, session_id)`:
- Each write replaces the previous version of that message
- The latest `message.updated` event wins — giving us the complete assistant response with all tool results and final text
- This is essential because `message.created` often contains an empty/partial skeleton that gets filled in by subsequent `message.updated` events

### Schema design: JSON parts vs. normalized parts table

Chose JSON blob (`parts_json`) over a normalized `message_parts` table because:
1. `MessagePart` is polymorphic with 3+ subtypes — normalized schema would need type discriminators and nullable columns
2. Parts are always read/written as a complete set (never queried individually)
3. The existing `[JsonPolymorphic]` / `[JsonDerivedType]` configuration on `MessagePart` gives us free serialization
4. SQLite JSON1 extension allows future querying if needed
5. Simpler migration, simpler repository code

### Pagination: DB cursor implementation

The cursor-based pagination uses `timestamp` as the ordering key with `id` as tiebreaker:
```sql
-- Without cursor (initial load): last N messages
SELECT * FROM messages
WHERE session_id = @SessionId
ORDER BY timestamp DESC, id DESC
LIMIT @Limit

-- With cursor: messages before the cursor
SELECT * FROM messages
WHERE session_id = @SessionId
  AND (timestamp < @CursorTimestamp
       OR (timestamp = @CursorTimestamp AND id < @CursorId))
ORDER BY timestamp DESC, id DESC
LIMIT @Limit
```
Results are reversed to ascending order before returning, matching the live API's behavior.

### Transition scenario (resume)

When a user views a stopped session then resumes it:
1. **Before resume**: Messages served from DB (history)
2. **Resume triggers**: `SessionOrchestrator.ResumeSessionAsync()` → spawns new instance → registers in `InstanceTracker`
3. **After resume**: New messages from the resumed conversation stream via SSE and are persisted to DB by `HarnessEventRelay`
4. **Older messages**: The live OpenCode instance has NO knowledge of previous messages (OpenCode has no disk persistence — sessions are in-memory only). All historical messages come exclusively from Fleet's DB via cursor-based pagination.
5. **Read strategy after resume**: The orchestrator will proxy to the live instance for the current page. For "scroll up" pagination, the live instance may return fewer messages than exist in history. The frontend will continue paginating — if the live instance's `hasMore` is false but DB has older messages, a follow-up iteration could fall back to DB. However, for the initial implementation, this edge case is acceptable: the live instance serves what it has, and if the user navigates away and back, the full history is in the DB.
