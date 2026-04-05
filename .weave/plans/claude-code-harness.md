# Claude Code Harness — Full Implementation

## TL;DR
> **Summary**: Add a production-grade `claude-code` harness with **instance-owned persistence** (Option B): each harness instance persists its own messages, the `HarnessEventRelay` is refactored to remove all OpenCode imports and become a generic broadcast-only service, and a new schema migration tracks harness type per session — proving the architecture supports multiple harness types with a clean abstraction boundary.
> **Estimated Effort**: Large

## Context

### Original Request
Add Claude Code as a new harness type to Weave Fleet, enabling the system to spawn and manage Claude Code agent sessions alongside the existing OpenCode harness. The implementation must use **instance-owned persistence** (Option B): every harness instance is responsible for persisting its own messages, the relay is a generic subscription-only service with zero harness imports.

### Key Findings

**Architecture (verified against current codebase 2026-04-05)**:
- `IHarness` (in `WeaveFleet.Application/Harnesses/IHarness.cs`) — singleton factory: `Type`, `DisplayName`, `Capabilities`, `CheckAvailabilityAsync(CancellationToken)`, `SpawnAsync(HarnessSpawnOptions, CancellationToken)`.
- `IHarnessInstance` (in `WeaveFleet.Domain/Harnesses/IHarnessInstance.cs`) — per-session bridge: `SendPromptAsync`, `GetMessagesAsync`, `SubscribeAsync`, `AbortAsync`, `CheckHealthAsync`, `StopAsync`, plus `IAsyncDisposable`. Properties: `InstanceId`, `HarnessType`, `Status`.
- `HarnessSpawnOptions` (in `WeaveFleet.Application/Harnesses/HarnessModels.cs`) includes: `SessionId`, `WorkingDirectory`, `InitialPrompt?`, `Branch?`, `ProjectId?`, `ProjectName?`, `Environment`.
- `HarnessRegistry` (in `WeaveFleet.Infrastructure/Harnesses/HarnessRegistry.cs`) auto-discovers all `IHarness` registrations from DI. Registering `services.AddSingleton<IHarness, ClaudeCodeHarness>()` is all that's needed.
- `SessionOrchestrator.CreateSessionAsync` resolves harness by `harnessType` string, calls `SpawnAsync`, registers in `InstanceTracker`.
- **Analytics integration**: `OpenCodeHarness` takes an optional `IAnalyticsCollector?` and passes it to instances. `OpenCodeHarnessInstance` intercepts SSE events via `OpenCodeMapper.TryExtractTokenEvent()` to feed `IAnalyticsCollector.AcceptTokenEvent()`. Claude Code must have equivalent analytics integration.
- **Build settings**: `net10.0`, `LangVersion=14`, `TreatWarningsAsErrors=true`, `EnforceCodeStyleInBuild=true`, `AnalysisLevel=latest-recommended`.
- **InternalsVisibleTo**: `WeaveFleet.Infrastructure.csproj` already grants `InternalsVisibleTo` to `WeaveFleet.Infrastructure.Tests`.
- **Test framework**: xUnit + NSubstitute + coverlet. Tests use `Microsoft.Extensions.Logging.Abstractions` (NullLogger, NullLoggerFactory).

**OpenCode pattern (reference implementation — verified)**:
- `OpenCodeHarness` is **`public sealed class`** (not `internal`), takes: `IHttpClientFactory`, `PortAllocator`, `FleetOptions`, `ILogger<OpenCodeHarness>`, `ILoggerFactory`, `IAnalyticsCollector?`.
- `OpenCodeHarnessInstance` is **`internal sealed class`**.
- `OpenCodeProcessManager` is **`internal sealed class`**.
- `OpenCodeMapper` is **`internal static class`** — includes `TryExtractTokenEvent` for analytics side-channel.
- `OpenCodeModels.cs` uses `[JsonPropertyName]` on all properties. `OpenCodeJsonOptions.Default` uses `JsonNamingPolicy.CamelCase` + `WhenWritingNull`.
- `ValidateWorkingDirectory` is a `private static` method in `OpenCodeHarness`.

**Message Persistence Flow (current — OpenCode-specific, TO BE REFACTORED)**:
1. `OpenCodeHarnessInstance.SubscribeAsync()` yields `HarnessEvent` objects with `Type` = `"message.created"`, `"message.updated"`, `"message.part.updated"`, `"session.status"`, etc.
2. `HarnessEventRelay.PumpAsync()` iterates the events, broadcasts each one, then fire-and-forgets two persistence methods:
   - `TryPersistMessageAsync()` — handles only `"message.created"` events. Extracts `"info"` and `"parts"` from the payload, deserializes as `OpenCodeAssistantMessage`/`OpenCodeUserMessage`, maps via `OpenCodeMapper.ToHarnessMessage()`, then upserts via `IMessageRepository`.
   - `TryPersistPartAsync()` — handles only `"message.part.updated"` events. Extracts the `"part"` object, deserializes as an OpenCode part type (`OpenCodeTextPart`, `OpenCodeToolPart`, `OpenCodeReasoningPart`), maps via `OpenCodeMapper.MapPart()`, then merges into the existing persisted message.
3. Both methods use `IServiceScopeFactory` to resolve scoped `IMessageRepository`.
4. Both methods are fire-and-forget with `try/catch` — persistence must never crash the event relay.
5. **Problem**: Both methods are completely coupled to OpenCode's event format and DTO types. They import `OpenCodeAssistantMessage`, `OpenCodeUserMessage`, `OpenCodeMapper`, `OpenCodeJsonOptions`, `OpenCodeTextPart`, `OpenCodeToolPart`, `OpenCodeReasoningPart`, and `OpenCodeMessageWithParts`. The relay's `using` directives include `WeaveFleet.Infrastructure.Harnesses.OpenCode`. This is a leak in the harness abstraction that must be fixed.

**Message Retrieval Flow (current)**:
1. `SessionOrchestrator.GetSessionMessagesAsync()` tries the live harness instance first via `instance.GetMessagesAsync()`.
2. If the instance is unavailable or errors, it falls back to `GetPersistedMessagesAsync()` which reads from `IMessageRepository`.
3. `OpenCodeHarnessInstance.GetMessagesAsync()` calls the OpenCode HTTP API to get live messages.
4. For Claude Code: there is no "get messages" API — the CLI doesn't support it. So `GetMessagesAsync()` must **always** read from the database. This makes full persistence non-optional.

**Session Entity Gap — No Harness Type Tracking**:
- `Session` entity has no `HarnessType` column. `ResumeSessionAsync()` hard-codes `DefaultHarnessType = "opencode"`.
- `ForkSessionAsync()` also hard-codes `HarnessType = DefaultHarnessType` instead of using the parent session's harness type.
- For multi-harness support, sessions must track which harness created them so they can be resumed/forked with the correct harness.
- Requires a new migration to add `harness_type TEXT NOT NULL DEFAULT 'opencode'` to the `sessions` table.

**Persistence Design: Option B — Instance-Owned Persistence (CHOSEN)**

The architectural principle is: **each harness instance owns its own message persistence**. The relay is a generic broadcast service with zero knowledge of message formats or persistence.

This means:
1. **`OpenCodeHarnessInstance` refactor**: Move the persistence logic currently in `HarnessEventRelay.TryPersistMessageAsync()` and `TryPersistPartAsync()` INTO `OpenCodeHarnessInstance`. The instance receives `IServiceScopeFactory` via its constructor and persists messages when processing SSE events in `SubscribeAsync()` — before yielding them to the relay.
2. **`ClaudeCodeHarnessInstance`**: Persists messages directly when processing NDJSON events. It already has the parsed domain types, so it writes them to the persistence layer.
3. **`HarnessEventRelay` cleanup**: Remove all OpenCode imports (`using WeaveFleet.Infrastructure.Harnesses.OpenCode`), remove `TryPersistMessageAsync()`, remove `TryPersistPartAsync()`, remove all references to `OpenCodeMapper`, `OpenCodeJsonOptions`, OpenCode DTO types. The relay's ONLY job becomes: subscribe to events → rewrite session IDs → broadcast to `IEventBroadcaster`. It retains `IServiceScopeFactory` only for the session lookup retry loop (`ISessionRepository`).
4. **Shared persistence interface**: Both instances use `IServiceScopeFactory` → `IMessageRepository` + `MessagePersistenceService` (Application layer). The persistence layer is harness-agnostic — it works with `HarnessMessage`, `PersistedMessage`, and `MessagePart` types from the Domain layer.

**Claude Code differences (critical design implications)**:
1. **No HTTP server** — communication is via stdio (subprocess with JSON on stdout).
2. **No port allocation** — no TCP listener needed.
3. **No Basic Auth** — process is local, no network exposure.
4. **Process-per-prompt model** — each `SendPromptAsync` spawns a new `claude` process (or resumes with `--resume <session-id>`). The process runs to completion and exits.
5. **Session continuity** — the `init` message on stdout contains a `session_id`; subsequent prompts use `--resume <session_id>`.
6. **Streaming via NDJSON** — newline-delimited JSON on stdout (not SSE). Three message types: `system` (init), `assistant` (content blocks), `result` (final).
7. **Abort** — kill the running `claude` subprocess (SIGTERM/SIGKILL).
8. **Health check** — `claude auth status` (exit code 0 = ok) and `claude --version`.
9. **No "get messages" API** — Claude Code has no way to retrieve past messages. `GetMessagesAsync()` must always read from the database.

**CLI invocation**:
```
claude -p "prompt" --output-format stream-json --bare --allowedTools "..." --permission-mode bypassPermissions
```
**Session resume**:
```
claude -p "prompt" --resume <session-id> --output-format stream-json --bare
```

**Stdout message format** (newline-delimited JSON):
```json
{"type":"system","subtype":"init","session_id":"xxx","tools":[...],"model":"...","mcp_servers":[...]}
{"type":"assistant","message":{"id":"msg_123","content":[{"type":"text","text":"..."},{"type":"tool_use","id":"toolu_xxx","name":"Edit","input":{...}}],"stop_reason":"end_turn|tool_use","usage":{"input_tokens":100,"output_tokens":50,"cache_creation_input_tokens":0,"cache_read_input_tokens":0}}}
{"type":"result","subtype":"success","result":"...","duration_ms":5432,"num_turns":3,"usage":{"input_tokens":500,"output_tokens":200},"total_cost_usd":0.042,"session_id":"xxx"}
```

## Objectives

### Core Objective
Implement the **instance-owned persistence** architecture: refactor `HarnessEventRelay` to remove all OpenCode coupling, move persistence into `OpenCodeHarnessInstance`, implement `ClaudeCodeHarness` and `ClaudeCodeHarnessInstance` with full database persistence, add harness-type tracking to sessions, and prove the architecture supports multiple harness types with clean abstraction boundaries.

### Deliverables
- [ ] `HarnessEventRelay` refactored: all OpenCode imports and persistence logic removed, becoming a pure broadcast service
- [ ] `OpenCodeHarnessInstance` refactored: owns its own message persistence (moved from relay)
- [ ] `OpenCodeHarness` refactored: receives and passes `IServiceScopeFactory` to instances
- [ ] Database migration adding `harness_type` column to sessions table
- [ ] `Session` entity updated with `HarnessType` property
- [ ] `SessionOrchestrator` updated to store/use harness type for create, resume, and fork
- [ ] Claude Code configuration added to `FleetOptions`
- [ ] Claude Code DTOs (`ClaudeCodeModels.cs`) matching the NDJSON stdout format
- [ ] Claude Code process manager (`ClaudeCodeProcessManager.cs`) for spawning and managing `claude` subprocesses
- [ ] Stdio communication layer (`ClaudeCodeStdioClient.cs`) for reading NDJSON from stdout
- [ ] Mapper (`ClaudeCodeMapper.cs`) converting Claude Code types to domain types + analytics extraction
- [ ] Harness instance (`ClaudeCodeHarnessInstance.cs`) implementing `IHarnessInstance` with full DB persistence
- [ ] Harness factory (`ClaudeCodeHarness.cs`) implementing `IHarness`
- [ ] Shared working directory validation helper (`HarnessHelpers.cs`)
- [ ] `RewriteSessionIds` updated to handle `session_id` (snake_case) from Claude Code payloads
- [ ] DI registration in `DependencyInjection.cs`
- [ ] Unit tests for all new code and updated existing tests for refactored code
- [ ] Contract test fixture for Claude Code message mapping

### Definition of Done
- [ ] `dotnet build` compiles with zero warnings (TreatWarningsAsErrors is enabled)
- [ ] `dotnet test` passes all existing + new tests
- [ ] `HarnessEventRelay.cs` has ZERO imports from `WeaveFleet.Infrastructure.Harnesses.OpenCode` — verified by grep
- [ ] `HarnessEventRelay.cs` contains ZERO references to OpenCode DTO types, `OpenCodeMapper`, or `OpenCodeJsonOptions`
- [ ] `GET /api/harnesses` returns both `opencode` and `claude-code` entries
- [ ] Creating a session with `harnessType: "claude-code"` spawns a Claude Code instance with `harness_type` stored in DB
- [ ] Sending a prompt streams assistant messages through the event pipeline
- [ ] Messages are persisted to the `messages` table with correct `session_id`, `role`, `parts_json` — by the instance, NOT the relay
- [ ] `GetMessagesAsync` returns messages from the database for Claude Code sessions
- [ ] OpenCode sessions continue to work identically — persistence moved from relay to instance with no functional change
- [ ] Resuming a Claude Code session uses the correct harness type (not hardcoded OpenCode)
- [ ] Forking a session inherits the parent's harness type
- [ ] Aborting kills the subprocess
- [ ] Health check verifies `claude` binary and auth
- [ ] Analytics token events are extracted and fed to `IAnalyticsCollector`

### Guardrails (Must NOT)
- Must NOT change any `IHarness` or `IHarnessInstance` interface signatures
- Must NOT add dependencies to `WeaveFleet.Domain` — all infrastructure stays in `WeaveFleet.Infrastructure`
- Must NOT hard-code the `claude` binary path — make it configurable via `FleetOptions`
- Must NOT use `public` visibility on infrastructure types except for `ClaudeCodeHarness` (the `IHarness` implementation, matching `OpenCodeHarness` pattern) and `HarnessHelpers` (if needed for API tests)
- Must NOT break existing OpenCode persistence — the refactor moves persistence from relay to instance with identical behavior
- Must NOT use in-memory-only message storage — all messages must be persisted to SQLite
- Must NOT leave any OpenCode imports in `HarnessEventRelay` after the refactor
- Must NOT introduce `IMessagePersistenceStrategy` or other unnecessary abstraction layers — instances use `IServiceScopeFactory` + `IMessageRepository` + `MessagePersistenceService` directly

## TODOs

### Phase 1: Architecture Refactor (Move Persistence to Instances)

- [x] 1. **Refactor `OpenCodeHarnessInstance` to own its persistence**
  **What**: Move the message persistence logic currently in `HarnessEventRelay.TryPersistMessageAsync()` (lines 167–227) and `TryPersistPartAsync()` (lines 229–314) INTO `OpenCodeHarnessInstance`. The instance intercepts events in `SubscribeAsync()` and persists them before yielding to the relay. This is the critical refactor that establishes the instance-owned persistence pattern.
  **Files**:
  - Modify `src/WeaveFleet.Infrastructure/Harnesses/OpenCode/OpenCodeHarnessInstance.cs`
  **Details**:
  The instance constructor gains two new parameters:
  ```csharp
  internal sealed class OpenCodeHarnessInstance : IHarnessInstance
  {
      // ... existing fields ...
      private readonly IServiceScopeFactory _scopeFactory;
      private readonly string _fleetSessionId;

      public OpenCodeHarnessInstance(
          string instanceId,
          string fleetSessionId,              // NEW — the Fleet session ID for DB persistence
          OpenCodeHttpClient httpClient,
          OpenCodeProcessManager processManager,
          PortAllocator portAllocator,
          int allocatedPort,
          string workingDirectory,
          TimeSpan shutdownTimeout,
          IServiceScopeFactory scopeFactory,   // NEW — for resolving scoped IMessageRepository
          ILogger<OpenCodeHarnessInstance> logger,
          IAnalyticsCollector? analyticsCollector,
          string? projectId,
          string? projectName)
  ```

  In `SubscribeAsync()`, after the existing analytics intercept (lines 183–190), add persistence intercept:
  ```csharp
  public async IAsyncEnumerable<HarnessEvent> SubscribeAsync(
      [EnumeratorCancellation] CancellationToken ct)
  {
      await foreach (var sseEvt in _httpClient
          .SubscribeToEventsAsync(_workingDirectory, ct)
          .ConfigureAwait(false))
      {
          // Fire-and-forget analytics intercept (existing — unchanged)
          if (_analyticsCollector is not null)
          {
              var tokenEvent = OpenCodeMapper.TryExtractTokenEvent(
                  sseEvt, _openCodeSessionId, _projectId, _projectName, _workingDirectory);
              if (tokenEvent is not null)
                  _analyticsCollector.AcceptTokenEvent(tokenEvent);
          }

          var harnessEvent = OpenCodeMapper.ToHarnessEvent(sseEvt, _openCodeSessionId);

          // Fire-and-forget persistence (moved from HarnessEventRelay)
          _ = TryPersistMessageAsync(harnessEvent);
          _ = TryPersistPartAsync(harnessEvent);

          yield return harnessEvent;
      }
  }
  ```

  Add private methods `TryPersistMessageAsync` and `TryPersistPartAsync` — these are **exact copies** of the logic from `HarnessEventRelay` (lines 167–314), using `_fleetSessionId` instead of the `fleetSessionId` parameter. The methods:
  - Use `_scopeFactory.CreateScope()` to resolve `IMessageRepository`
  - Use `OpenCodeMapper`, `OpenCodeJsonOptions`, and OpenCode DTO types (which are already in the same namespace)
  - Are wrapped in try/catch with silent failure (persistence must never crash the event stream)
  - Use `MessagePersistenceService.ToPersistedMessage()` and `MessagePersistenceService.MergePart()` for the actual persistence operations

  **Key point**: The logic is identical — we are literally moving code from the relay into the instance. The instance is the natural home because it already knows the OpenCode event format and has the mapper in scope.

  **Acceptance**: OpenCode persistence works identically to before (same behavior, different location). `OpenCodeHarnessInstance` has `IServiceScopeFactory` and `fleetSessionId`. Existing tests continue to pass. `dotnet build` zero warnings.

- [x] 2. **Refactor `OpenCodeHarness.SpawnAsync` to pass persistence dependencies**
  **What**: Update `OpenCodeHarness` to accept `IServiceScopeFactory` and pass it (along with `options.SessionId` as `fleetSessionId`) to `OpenCodeHarnessInstance`.
  **Files**:
  - Modify `src/WeaveFleet.Infrastructure/Harnesses/OpenCode/OpenCodeHarness.cs`
  **Details**:
  Add `IServiceScopeFactory` to the constructor:
  ```csharp
  public sealed class OpenCodeHarness : IHarness
  {
      // ... existing fields ...
      private readonly IServiceScopeFactory _scopeFactory;

      public OpenCodeHarness(
          IHttpClientFactory httpClientFactory,
          PortAllocator portAllocator,
          FleetOptions options,
          IServiceScopeFactory scopeFactory,   // NEW
          ILogger<OpenCodeHarness> logger,
          ILoggerFactory loggerFactory,
          IAnalyticsCollector? analyticsCollector)
  ```

  In `SpawnAsync`, pass `scopeFactory` and `options.SessionId` to the instance constructor:
  ```csharp
  var instance = new OpenCodeHarnessInstance(
      instanceId: instanceId,
      fleetSessionId: options.SessionId,     // NEW
      httpClient: openCodeHttpClient,
      processManager: processManager,
      portAllocator: _portAllocator,
      allocatedPort: allocatedPort,
      workingDirectory: options.WorkingDirectory,
      shutdownTimeout: TimeSpan.FromSeconds(_options.HarnessShutdownTimeoutSeconds),
      scopeFactory: _scopeFactory,           // NEW
      logger: _loggerFactory.CreateLogger<OpenCodeHarnessInstance>(),
      analyticsCollector: _analyticsCollector,
      projectId: options.ProjectId,
      projectName: options.ProjectName);
  ```
  **Acceptance**: `OpenCodeHarness` compiles with the new constructor; `SpawnAsync` passes persistence dependencies; `dotnet build` zero warnings.

- [x] 3. **Refactor `HarnessEventRelay` to remove all OpenCode imports and persistence**
  **What**: Strip `HarnessEventRelay` down to a pure broadcast service. Remove `TryPersistMessageAsync()`, `TryPersistPartAsync()`, all OpenCode imports, and the `LogPersistFailed` log definition. The relay's only job is: subscribe → rewrite session IDs → broadcast. It retains `IServiceScopeFactory` ONLY for the session lookup retry loop (`ISessionRepository`).
  **Files**:
  - Modify `src/WeaveFleet.Infrastructure/Services/HarnessEventRelay.cs`
  **Details**:
  Remove from `using` directives:
  ```diff
  -using WeaveFleet.Infrastructure.Harnesses.OpenCode;
  ```
  Remove from `PumpAsync`:
  ```diff
  -// Fire-and-forget message persistence (must never block the event stream)
  -_ = TryPersistMessageAsync(evt, fleetSessionId);
  -_ = TryPersistPartAsync(evt, fleetSessionId);
  ```
  Remove entire methods:
  - `TryPersistMessageAsync` (lines 167–227)
  - `TryPersistPartAsync` (lines 229–314)

  Remove the `LogPersistFailed` log message definition (lines 29–31) since it's only used by the persistence methods.

  Remove the `IMessageRepository` import (already unused after method removal) — the relay only needs `ISessionRepository`.

  Update `RewriteSessionIds` → `WriteRewritten` to also match `session_id` (snake_case) in addition to `sessionId` and `sessionID`:
  ```csharp
  if ((prop.Name is "sessionId" or "sessionID" or "session_id")
      && prop.Value.ValueKind == JsonValueKind.String)
  {
      writer.WriteStringValue(fleetSessionId);
  }
  ```

  After this refactor, the relay file should:
  - Have ZERO references to OpenCode types
  - Have ZERO `using WeaveFleet.Infrastructure.Harnesses.*` directives
  - Only import: `System.Collections.Concurrent`, `System.Text.Json`, `Microsoft.Extensions.DependencyInjection`, `Microsoft.Extensions.Hosting`, `Microsoft.Extensions.Logging`, `WeaveFleet.Application.Services`, `WeaveFleet.Domain.Entities`, `WeaveFleet.Domain.Harnesses`, `WeaveFleet.Domain.Repositories`
  - Contain only: constructor, `ExecuteAsync`, `OnInstanceRegistered`, `OnInstanceRemoved`, `StartSubscription`, `PumpAsync`, `RewriteSessionIds`, `WriteRewritten`

  **Acceptance**: `HarnessEventRelay.cs` has zero OpenCode imports. `grep -r "OpenCode" src/WeaveFleet.Infrastructure/Services/HarnessEventRelay.cs` returns nothing. Events are still broadcast correctly. `dotnet build` zero warnings.

- [x] 4. **Update `HarnessEventRelay` tests for the refactored relay**
  **What**: The existing `HarnessEventRelayTests` have 8 persistence-specific tests that directly test the relay's `TryPersistMessageAsync`/`TryPersistPartAsync` behavior. These tests must be relocated or rewritten since persistence is no longer the relay's responsibility. Additionally, add a test for `session_id` (snake_case) rewriting.
  **Files**:
  - Modify `tests/WeaveFleet.Infrastructure.Tests/Services/HarnessEventRelayTests.cs`
  - Create `tests/WeaveFleet.Infrastructure.Tests/Harnesses/OpenCode/OpenCodeHarnessInstancePersistenceTests.cs`
  **Details**:
  The following existing tests must be moved from `HarnessEventRelayTests` to `OpenCodeHarnessInstancePersistenceTests` (adapted to test instance-level persistence instead of relay-level):
  - `PumpAsync_MessageUpdatedEvent_DoesNotPersist` → `SubscribeAsync_MessageUpdatedEvent_DoesNotPersist`
  - `PumpAsync_MessageCreatedEvent_PersistsMessage` → `SubscribeAsync_MessageCreatedEvent_PersistsMessage`
  - `PumpAsync_NonMessageEvent_DoesNotPersist` → `SubscribeAsync_NonMessageEvent_DoesNotPersist`
  - `PumpAsync_PersistenceFailure_DoesNotBlockEventDelivery` → `SubscribeAsync_PersistenceFailure_DoesNotBlockEventDelivery`
  - `PumpAsync_MessagePartUpdated_TextPart_PersistsIncrementally` → `SubscribeAsync_MessagePartUpdated_TextPart_PersistsIncrementally`
  - `PumpAsync_MessageCreated_EmptyParts_DoesNotOverwriteExisting` → `SubscribeAsync_MessageCreated_EmptyParts_DoesNotOverwriteExisting`
  - `PumpAsync_FullLifecycle_MessageUpdatedDoesNotOverwriteParts` → `SubscribeAsync_FullLifecycle_MessageUpdatedDoesNotOverwriteParts`

  These new tests will require creating a testable `OpenCodeHarnessInstance` with mocked `IServiceScopeFactory`, `IMessageRepository`, `OpenCodeHttpClient`, and `OpenCodeProcessManager`. The `OpenCodeHttpClient` mock must yield SSE events that the instance processes in `SubscribeAsync()`.

  In `HarnessEventRelayTests`, the persistence-related tests are removed. The remaining tests (event broadcast, session lookup retry, pre-existing instance, session ID rewriting) stay unchanged. Add one new test:
  - `RewriteSessionIds_HandlesSnakeCaseSessionId` — verify `session_id` in payloads is rewritten

  The `BuildPersistenceDependencies` helper and `BuildMessagePayload`/`BuildPartPayload` helpers move to the new test file.

  **Acceptance**: All existing test *behaviors* are preserved (just moved to the correct location). New `session_id` rewrite test passes. `dotnet test` passes all tests. `dotnet build` zero warnings.

### Phase 2: Session Harness Type Tracking

- [x] 5. **Add `harness_type` column to sessions table (database migration)**
  **What**: Add a new SQL migration that adds a `harness_type` column to the `sessions` table, defaulting to `'opencode'` for all existing sessions. This is the foundation for multi-harness support — without it, `ResumeSessionAsync` cannot know which harness to use.
  **Files**:
  - Create `src/WeaveFleet.Infrastructure/Migrations/003_add_harness_type.sql`
  **Details**:
  ```sql
  ALTER TABLE sessions ADD COLUMN harness_type TEXT NOT NULL DEFAULT 'opencode';
  CREATE INDEX idx_sessions_harness_type ON sessions(harness_type);
  ```
  The `DEFAULT 'opencode'` ensures all existing sessions are correctly attributed. The index supports future queries filtering by harness type.
  **Acceptance**: Migration applies cleanly on a fresh DB and on one with existing sessions; existing sessions get `harness_type = 'opencode'`; `dotnet build` succeeds.

- [x] 6. **Update `Session` entity and `SessionOrchestrator` for harness type**
  **What**: Add `HarnessType` property to the `Session` entity and update `SessionOrchestrator` to populate it on create, use it on resume, and inherit it on fork.
  **Files**:
  - Modify `src/WeaveFleet.Domain/Entities/Session.cs` — add `HarnessType` property
  - Modify `src/WeaveFleet.Application/Services/SessionOrchestrator.cs` — set harness type on create; use stored harness type on resume; inherit on fork
  **Details**:
  In `Session.cs`, add:
  ```csharp
  public string HarnessType { get; set; } = "opencode";
  ```
  In `SessionOrchestrator.CreateSessionAsync`, set on the session entity:
  ```csharp
  var session = new Session
  {
      // ... existing fields ...
      HarnessType = harnessType,
  };
  ```
  In `SessionOrchestrator.ResumeSessionAsync`, replace the hardcoded default (line 185):
  ```csharp
  // BEFORE:
  var harness = harnessRegistry.GetByType(DefaultHarnessType);
  // AFTER:
  var harness = harnessRegistry.GetByType(session.HarnessType);
  if (harness is null)
      return FleetError.NotFoundFor("Harness", session.HarnessType);
  ```
  Also fix the error message on line 200 to use `session.HarnessType` instead of `DefaultHarnessType`.

  In `SessionOrchestrator.ForkSessionAsync`, inherit the parent's harness type (line 236):
  ```csharp
  // BEFORE:
  HarnessType = DefaultHarnessType,
  // AFTER:
  HarnessType = parent.HarnessType,
  ```

  **Acceptance**: New sessions have `harness_type` set correctly; resuming a Claude Code session uses `ClaudeCodeHarness`; forking inherits parent's harness type; existing tests still pass; `dotnet build` zero warnings.

### Phase 3: Claude Code Infrastructure

- [x] 7. **Add Claude Code configuration to FleetOptions**
  **What**: Add a `ClaudeCodeOptions` nested class to `FleetOptions` with configurable settings for the Claude Code harness.
  **Files**:
  - Modify `src/WeaveFleet.Application/Configuration/FleetOptions.cs` — add `ClaudeCodeOptions` property and nested class
  - Modify `src/WeaveFleet.Api/appsettings.json` — add `ClaudeCode` section under `Fleet`
  **Details**:
  The class goes inside `FleetOptions.cs` since the `Application` layer owns configuration. Add after the analytics properties:
  ```csharp
  // ─── Claude Code ─────────────────────────────────────────────────────────

  /// <summary>Claude Code harness configuration.</summary>
  public ClaudeCodeOptions ClaudeCode { get; set; } = new();
  ```
  The nested class:
  ```csharp
  /// <summary>Configuration for the Claude Code harness.</summary>
  public sealed class ClaudeCodeOptions
  {
      /// <summary>Path to the claude binary. Default: "claude" (assumes on PATH).</summary>
      public string BinaryPath { get; set; } = "claude";

      /// <summary>Default model to use. Null = let Claude Code choose.</summary>
      public string? DefaultModel { get; set; }

      /// <summary>Permission mode for tool execution. Default: "bypassPermissions".</summary>
      public string PermissionMode { get; set; } = "bypassPermissions";

      /// <summary>Allowed tools. Empty = use Claude Code defaults.</summary>
      public string[] AllowedTools { get; set; } = [];

      /// <summary>Maximum agentic turns per prompt. Null = no limit.</summary>
      public int? MaxTurns { get; set; }

      /// <summary>Maximum budget in USD per prompt. Null = no limit.</summary>
      public decimal? MaxBudgetUsd { get; set; }

      /// <summary>Timeout in seconds for each prompt process. Default: 300 (5 min).</summary>
      public int ProcessTimeoutSeconds { get; set; } = 300;
  }
  ```
  In `appsettings.json`, add under the `Fleet` object:
  ```json
  "ClaudeCode": {
    "BinaryPath": "claude",
    "PermissionMode": "bypassPermissions",
    "ProcessTimeoutSeconds": 300
  }
  ```
  **Acceptance**: `FleetOptions.ClaudeCode` is non-null with sensible defaults; `appsettings.json` has a `ClaudeCode` section; `dotnet build` succeeds.

- [x] 8. **Create Claude Code DTO models (`ClaudeCodeModels.cs`)**
  **What**: Define C# records matching the Claude Code NDJSON stdout format. Follow the same pattern as `OpenCodeModels.cs` (501 lines): a `ClaudeCodeJsonOptions` static class with shared serializer options, then DTOs for each message type.
  **Files**:
  - Create `src/WeaveFleet.Infrastructure/Harnesses/ClaudeCode/ClaudeCodeModels.cs`
  **Details** — key types to model:
  ```
  ClaudeCodeJsonOptions (internal static class)
    Default: JsonSerializerOptions
      PropertyNamingPolicy = JsonNamingPolicy.SnakeCaseLower
      DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull

  // Top-level stdout line — discriminated by "type"
  [JsonPolymorphic(TypeDiscriminatorPropertyName = "type",
      IgnoreUnrecognizedTypeDiscriminators = true,
      UnknownDerivedTypeHandling = FallBackToBaseType)]
  [JsonDerivedType(typeof(ClaudeCodeSystemMessage), "system")]
  [JsonDerivedType(typeof(ClaudeCodeAssistantMessage), "assistant")]
  [JsonDerivedType(typeof(ClaudeCodeResultMessage), "result")]
  ClaudeCodeStreamMessage (internal record)

  ClaudeCodeSystemMessage : ClaudeCodeStreamMessage
    Subtype: string
    SessionId: string
    Tools: JsonElement?
    Model: string?
    McpServers: JsonElement?

  ClaudeCodeAssistantMessage : ClaudeCodeStreamMessage
    Message: ClaudeCodeApiMessage

  ClaudeCodeApiMessage (internal sealed record)
    Id: string
    Content: IReadOnlyList<ClaudeCodeContentBlock>
    StopReason: string?
    Usage: ClaudeCodeUsage?
    Model: string?
    Role: string?

  ClaudeCodeResultMessage : ClaudeCodeStreamMessage
    Subtype: string
    Result: string?
    NumTurns: int?
    DurationMs: long?
    Usage: ClaudeCodeUsage?
    TotalCostUsd: decimal?
    SessionId: string?

  // Content blocks — discriminated by "type"
  [JsonPolymorphic(TypeDiscriminatorPropertyName = "type",
      IgnoreUnrecognizedTypeDiscriminators = true,
      UnknownDerivedTypeHandling = FallBackToBaseType)]
  [JsonDerivedType(typeof(ClaudeCodeTextBlock), "text")]
  [JsonDerivedType(typeof(ClaudeCodeToolUseBlock), "tool_use")]
  [JsonDerivedType(typeof(ClaudeCodeToolResultBlock), "tool_result")]
  ClaudeCodeContentBlock (internal record)

  ClaudeCodeTextBlock : ClaudeCodeContentBlock
    Text: string

  ClaudeCodeToolUseBlock : ClaudeCodeContentBlock
    Id: string
    Name: string
    Input: JsonElement

  ClaudeCodeToolResultBlock : ClaudeCodeContentBlock
    ToolUseId: string
    Content: string?
    IsError: bool?

  ClaudeCodeUsage (internal sealed record)
    InputTokens: int
    OutputTokens: int
    CacheReadInputTokens: int?
    CacheCreationInputTokens: int?
  ```
  **Critical**: Use `[JsonPropertyName("snake_case")]` on all properties since Claude Code uses snake_case JSON. Use `JsonNamingPolicy.SnakeCaseLower` for the `ClaudeCodeJsonOptions.Default` (unlike OpenCode's `CamelCase`).
  **Acceptance**: All types compile as `internal sealed`; JSON deserialization of sample NDJSON lines produces correct objects; polymorphic discriminators work.

- [x] 9. **Extract shared working directory validation (`HarnessHelpers.cs`)**
  **What**: `OpenCodeHarness.ValidateWorkingDirectory` is a `private static` method (lines 221–245) that `ClaudeCodeHarness` also needs. Extract it to a shared internal static helper class.
  **Files**:
  - Create `src/WeaveFleet.Infrastructure/Harnesses/HarnessHelpers.cs`
  - Modify `src/WeaveFleet.Infrastructure/Harnesses/OpenCode/OpenCodeHarness.cs` — change `ValidateWorkingDirectory` to call `HarnessHelpers.ValidateWorkingDirectory`
  **Details**:
  ```csharp
  namespace WeaveFleet.Infrastructure.Harnesses;

  /// <summary>Shared utilities for harness implementations.</summary>
  internal static class HarnessHelpers
  {
      /// <summary>
      /// Validates that <paramref name="directory"/> is a safe, absolute path
      /// that exists on disk. Throws <see cref="ArgumentException"/> otherwise.
      /// </summary>
      internal static void ValidateWorkingDirectory(string directory)
      {
          // Exact same logic from OpenCodeHarness:
          // 1. Path.IsPathFullyQualified check
          // 2. ".." segment rejection
          // 3. Directory.Exists check
      }
  }
  ```
  In `OpenCodeHarness.cs`, replace the private method body:
  ```csharp
  private static void ValidateWorkingDirectory(string directory)
      => HarnessHelpers.ValidateWorkingDirectory(directory);
  ```
  **Acceptance**: Both OpenCode and Claude Code harnesses use the shared validation; all existing tests still pass; `dotnet build` zero warnings.

- [x] 10. **Create Claude Code process manager (`ClaudeCodeProcessManager.cs`)**
  **What**: Manages the lifecycle of `claude` CLI subprocesses. Unlike OpenCode's persistent server (`OpenCodeProcessManager`, lines 1–243), Claude Code uses a process-per-prompt model.
  **Files**:
  - Create `src/WeaveFleet.Infrastructure/Harnesses/ClaudeCode/ClaudeCodeProcessManager.cs`
  **Details**:
  ```csharp
  /// <summary>Options for starting a claude CLI process.</summary>
  internal sealed record ClaudeCodeProcessOptions
  {
      public required string BinaryPath { get; init; }
      public required string WorkingDirectory { get; init; }
      public required string Prompt { get; init; }
      public string? SessionId { get; init; }          // null for first prompt, set for --resume
      public string? Model { get; init; }
      public required string PermissionMode { get; init; }
      public string[] AllowedTools { get; init; } = [];
      public int? MaxTurns { get; init; }
      public decimal? MaxBudgetUsd { get; init; }
      public required TimeSpan ProcessTimeout { get; init; }
      public IReadOnlyDictionary<string, string> EnvironmentVariables { get; init; }
          = new Dictionary<string, string>();
  }

  /// <summary>
  /// Manages a single claude CLI subprocess.
  /// One instance per prompt execution (not per session — sessions span multiple prompts).
  /// </summary>
  internal sealed class ClaudeCodeProcessManager : IAsyncDisposable
  {
      // Constructor: ILogger<ClaudeCodeProcessManager>

      bool IsRunning { get; }
      int? ProcessId { get; }

      // Starts the process, returns (Process.StandardOutput as StreamReader)
      // so the caller can read NDJSON lines.
      // Does NOT use BeginOutputReadLine — exposes raw StreamReader.
      // DOES use BeginErrorReadLine for stderr capture.
      Task<StreamReader> StartAsync(ClaudeCodeProcessOptions options, CancellationToken ct);

      // Graceful kill: SIGTERM → wait → SIGKILL
      // Same pattern as OpenCodeProcessManager.StopAsync (lines 199-232)
      Task StopAsync(TimeSpan timeout);

      // Event for process exit detection
      event EventHandler<int>? ProcessExited;

      ValueTask DisposeAsync();
  }
  ```
  **Key design decisions** (following OpenCode patterns):
  - Build arguments via `ProcessStartInfo.ArgumentList` (no shell injection) — same as `OpenCodeProcessManager` line 107.
  - Always include: `-p`, `--output-format`, `stream-json`, `--bare`.
  - Conditionally include: `--resume <sessionId>`, `--model <model>`, `--permission-mode <mode>`, `--allowedTools <tools>`, `--max-turns <n>`, `--max-budget-usd <n>`.
  - Redirect stdout and stderr. Expose `Process.StandardOutput` as a `StreamReader` for line-by-line async reading.
  - Capture stderr via `BeginErrorReadLine` for diagnostics (like `OpenCodeProcessManager` line 149–154).
  - Enable `Process.EnableRaisingEvents` for exit detection.
  - Apply process timeout via `CancellationTokenSource.CancelAfter`.
  - Use structured logging with `LoggerMessage.Define` (same pattern as OpenCode, not source generators for internal classes).
  **Acceptance**: Can spawn `claude` with correct arguments; `IsRunning` reflects actual process state; `StopAsync` terminates the process; `ProcessExited` fires on exit.

- [x] 11. **Create Claude Code stdio client (`ClaudeCodeStdioClient.cs`)**
  **What**: Reads newline-delimited JSON from the `claude` process's stdout and deserializes into `ClaudeCodeStreamMessage` instances.
  **Files**:
  - Create `src/WeaveFleet.Infrastructure/Harnesses/ClaudeCode/ClaudeCodeStdioClient.cs`
  **Details**:
  ```csharp
  /// <summary>
  /// Reads NDJSON from a claude process's stdout stream.
  /// Stateless — purely functional.
  /// </summary>
  internal static class ClaudeCodeStdioClient
  {
      /// <summary>
      /// Reads lines from <paramref name="stdout"/> and yields deserialized messages.
      /// Skips blank lines and malformed JSON (logs warnings).
      /// Completes when the stream ends or <paramref name="ct"/> is cancelled.
      /// </summary>
      internal static async IAsyncEnumerable<ClaudeCodeStreamMessage> ReadMessagesAsync(
          StreamReader stdout,
          ILogger logger,
          [EnumeratorCancellation] CancellationToken ct)
      {
          while (!ct.IsCancellationRequested)
          {
              var line = await stdout.ReadLineAsync(ct).ConfigureAwait(false);
              if (line is null) yield break; // EOF
              if (string.IsNullOrWhiteSpace(line)) continue;

              ClaudeCodeStreamMessage? msg;
              try
              {
                  msg = JsonSerializer.Deserialize<ClaudeCodeStreamMessage>(
                      line, ClaudeCodeJsonOptions.Default);
              }
              catch (JsonException)
              {
                  // Log warning and skip malformed line
                  continue;
              }

              if (msg is not null) yield return msg;
          }
      }
  }
  ```
  **Acceptance**: Given a stream of NDJSON lines, correctly yields deserialized `ClaudeCodeStreamMessage` objects; handles malformed lines gracefully (logs + skips).

- [x] 12. **Create Claude Code mapper (`ClaudeCodeMapper.cs`)**
  **What**: Maps Claude Code DTOs to domain types (`HarnessMessage`, `HarnessEvent`, `MessagePart`) and extracts analytics data. Follows `OpenCodeMapper` pattern (internal static class, 243 lines).
  **Files**:
  - Create `src/WeaveFleet.Infrastructure/Harnesses/ClaudeCode/ClaudeCodeMapper.cs`
  **Details**:
  ```csharp
  /// <summary>Maps Claude Code DTOs to Weave Fleet domain types.</summary>
  internal static class ClaudeCodeMapper
  {
      /// <summary>
      /// Maps a ClaudeCodeAssistantMessage to a HarnessMessage.
      /// Content blocks map as:
      ///   "text"        → TextPart(text)
      ///   "tool_use"    → ToolUsePart(id, name, input, State=Running)
      ///   "tool_result" → ToolResultPart(tool_use_id, content, is_error)
      /// </summary>
      internal static HarnessMessage ToHarnessMessage(
          ClaudeCodeAssistantMessage msg, DateTimeOffset timestamp)
      {
          // msg.Message.Content → List<MessagePart>
          // Id = msg.Message.Id
          // Role = "assistant"
          // Timestamp = timestamp (Claude Code doesn't include per-message timestamps)
      }

      /// <summary>
      /// Creates a synthetic user HarnessMessage for prompt tracking.
      /// Unlike OpenCode where user messages come from the SSE stream,
      /// Claude Code doesn't echo back user messages, so we create them synthetically
      /// when the user sends a prompt.
      /// </summary>
      internal static HarnessMessage ToUserMessage(
          string prompt, DateTimeOffset timestamp)
      {
          // Id = $"user-{Guid.NewGuid():N}"
          // Role = "user"
          // Parts = [new TextPart(prompt)]
          // Timestamp = timestamp
      }

      /// <summary>
      /// Maps a ClaudeCodeStreamMessage to a HarnessEvent for SubscribeAsync.
      /// system/init → type="system.init"
      /// assistant   → type="assistant.message"
      /// result      → type="result.{subtype}"
      /// </summary>
      internal static HarnessEvent ToHarnessEvent(
          ClaudeCodeStreamMessage msg, string sessionId)
      {
          // Serialize entire message as Payload
          // Timestamp = DateTimeOffset.UtcNow
      }

      /// <summary>
      /// Maps a single content block to a domain MessagePart.
      /// Returns null for unrecognized block types.
      /// </summary>
      internal static MessagePart? ToMessagePart(ClaudeCodeContentBlock block)
      {
          // text → TextPart
          // tool_use → ToolUsePart (State = ToolUseState.Running — Claude Code
          //            doesn't distinguish pending/completed during streaming)
          // tool_result → ToolResultPart
      }

      /// <summary>
      /// Extracts token/cost analytics from a result message.
      /// Equivalent to OpenCodeMapper.TryExtractTokenEvent (lines 161-231).
      /// Called from ClaudeCodeHarnessInstance when a result message arrives.
      /// </summary>
      internal static TokenEventData? TryExtractTokenEvent(
          ClaudeCodeResultMessage result,
          string? sessionId,
          string? projectId,
          string? projectName,
          string? workspaceDirectory,
          string? modelId)
      {
          // Extract from result: TotalCostUsd, Usage.InputTokens, Usage.OutputTokens
          // Use ModelPricing.EstimateCost for estimated cost
          // Return null if no cost/usage data
      }
  }
  ```
  **Acceptance**: Unit tests verify correct mapping of text blocks, tool_use blocks, and tool_result blocks to domain types. Analytics extraction returns correct `TokenEventData`.

### Phase 4: Claude Code Harness Implementation

- [x] 13. **Create Claude Code harness instance (`ClaudeCodeHarnessInstance.cs`) — with full DB persistence**
  **What**: Implements `IHarnessInstance`. Manages the process-per-prompt lifecycle, **persists all messages to the database**, and emits events via `SubscribeAsync`. Integrates with analytics collector. Follows the same instance-owned persistence pattern established in TODO 1.
  **Files**:
  - Create `src/WeaveFleet.Infrastructure/Harnesses/ClaudeCode/ClaudeCodeHarnessInstance.cs`
  **Details**:
  ```csharp
  /// <summary>
  /// Wraps a Claude Code CLI session for a single Fleet session.
  /// Implements <see cref="IHarnessInstance"/> with full database persistence.
  /// Each instance owns its own message persistence — the relay is not involved.
  /// </summary>
  internal sealed class ClaudeCodeHarnessInstance : IHarnessInstance
  {
      // Constructor parameters:
      //   string instanceId
      //   string fleetSessionId           // the Fleet session ID for DB persistence
      //   string workingDirectory
      //   FleetOptions.ClaudeCodeOptions config
      //   IReadOnlyDictionary<string, string> environmentVariables
      //   TimeSpan shutdownTimeout
      //   IServiceScopeFactory scopeFactory  // for resolving scoped IMessageRepository
      //   ILogger<ClaudeCodeHarnessInstance> logger
      //   ILoggerFactory loggerFactory       // for creating process manager loggers
      //   IAnalyticsCollector? analyticsCollector
      //   string? projectId
      //   string? projectName

      // Properties
      string InstanceId { get; }
      string HarnessType => "claude-code";
      HarnessInstanceStatus Status { get; }

      // Private state
      //   _claudeSessionId: string?        // from init message, for --resume
      //   _eventChannel: Channel<HarnessEvent>  // bounded, DropOldest (1000 capacity)
      //   _activeProcess: ClaudeCodeProcessManager?
      //   _promptLock: SemaphoreSlim(1,1)
      //   _disposed: bool
      //   _analyticsCollector: IAnalyticsCollector?
      //   _modelId: string?                // captured from init or result messages
      //   _scopeFactory: IServiceScopeFactory
      //   _fleetSessionId: string

      // SendPromptAsync:
      //   1. Acquire _promptLock
      //   2. Create synthetic user HarnessMessage and PERSIST TO DB via PersistMessageAsync
      //      (This ensures user prompts appear in message history)
      //   3. Write HarnessEvent for the user message to _eventChannel
      //   4. Spawn claude process via new ClaudeCodeProcessManager
      //      - If _claudeSessionId != null, add --resume flag
      //      - If options?.ModelId != null, add --model flag
      //   5. Set _activeProcess, Status = Running
      //   6. Start background task to pump stdout via ClaudeCodeStdioClient:
      //      a. On system/init: capture session_id → _claudeSessionId, capture model
      //         Write HarnessEvent to channel.
      //      b. On assistant:
      //         - Map to HarnessMessage via ClaudeCodeMapper.ToHarnessMessage
      //         - PERSIST TO DB via PersistMessageAsync
      //         - Write HarnessEvent to _eventChannel
      //      c. On result:
      //         - Write HarnessEvent to channel
      //         - Extract analytics via TryExtractTokenEvent
      //         - Set Status = Idle
      //   7. Release _promptLock immediately (fire-and-forget — matching OpenCode pattern)

      // GetMessagesAsync:
      //   ALWAYS reads from database via IMessageRepository.
      //   Creates a scope, resolves IMessageRepository, calls GetBySessionAsync.
      //   Converts via MessagePersistenceService.ToHarnessMessages.
      //   Applies cursor-based pagination (limit, before).
      //   Uses the same pattern as SessionOrchestrator.GetPersistedMessagesAsync.

      // SubscribeAsync:
      //   yield return from _eventChannel.Reader.ReadAllAsync(ct)

      // AbortAsync:
      //   Kill _activeProcess if running → StopAsync on the process manager
      //   Set Status = Idle

      // CheckHealthAsync:
      //   If _activeProcess?.IsRunning → Healthy (prompt is active)
      //   Else if Status is Idle → Healthy (idle is healthy)
      //   Else → Unhealthy

      // StopAsync:
      //   Status = Stopping
      //   Kill _activeProcess if running
      //   Complete _eventChannel.Writer
      //   Status = Stopped

      // DisposeAsync:
      //   Same pattern as OpenCodeHarnessInstance.DisposeAsync (lines 255-276)

      // Private helper: PersistMessageAsync(HarnessMessage message)
      //   using var scope = _scopeFactory.CreateScope();
      //   var repo = scope.ServiceProvider.GetRequiredService<IMessageRepository>();
      //   var persisted = MessagePersistenceService.ToPersistedMessage(_fleetSessionId, message);
      //   await repo.UpsertAsync(persisted);
      //   Wrapped in try/catch — persistence errors are logged but never crash the instance.
  }
  ```
  **Key design decisions**:
  - **Direct database persistence (instance-owned)**: Same pattern as the refactored `OpenCodeHarnessInstance` — the instance persists messages itself, the relay never touches them.
  - **`IServiceScopeFactory` injection**: Same pattern used by the refactored OpenCode instance.
  - **Fleet session ID**: Passed via constructor from `ClaudeCodeHarness.SpawnAsync`, which receives it in `HarnessSpawnOptions.SessionId`.
  - **Synthetic user messages**: Claude Code doesn't echo back user messages. The instance creates synthetic user messages on `SendPromptAsync` and persists them immediately.
  - **Fire-and-forget prompt**: Matches OpenCode pattern.
  - **Event channel**: `Channel.CreateBounded<HarnessEvent>(1000)` with `DropOldest`.
  - **Analytics**: On `result` message, call `ClaudeCodeMapper.TryExtractTokenEvent` and `_analyticsCollector?.AcceptTokenEvent`.
  **Acceptance**: Implements all `IHarnessInstance` methods; messages are persisted to DB on each assistant message; user prompts are persisted as synthetic user messages; `GetMessagesAsync` reads from DB; events flow through `SubscribeAsync`; abort kills subprocess; analytics events are emitted.

- [x] 14. **Create Claude Code harness factory (`ClaudeCodeHarness.cs`)**
  **What**: Implements `IHarness`. Checks availability, declares capabilities, and spawns `ClaudeCodeHarnessInstance`. Follows `OpenCodeHarness` pattern exactly (visibility: `public sealed class`).
  **Files**:
  - Create `src/WeaveFleet.Infrastructure/Harnesses/ClaudeCode/ClaudeCodeHarness.cs`
  **Details**:
  ```csharp
  /// <summary>
  /// <see cref="IHarness"/> implementation for the Claude Code AI coding agent.
  /// Checks binary availability and spawns claude CLI instances.
  /// </summary>
  public sealed class ClaudeCodeHarness : IHarness
  {
      // Constructor (matching OpenCodeHarness dependencies minus HTTP-specific ones):
      //   FleetOptions options
      //   IServiceScopeFactory scopeFactory    // passed to instances for DB persistence
      //   ILogger<ClaudeCodeHarness> logger
      //   ILoggerFactory loggerFactory
      //   IAnalyticsCollector? analyticsCollector = null

      string Type => "claude-code";
      string DisplayName => "Claude Code";

      HarnessCapabilities Capabilities => new()
      {
          RequiresInitialPrompt = true,    // Claude Code needs a prompt to start
          SupportsAgents = false,          // No agent system
          SupportsModelSelection = true,   // --model flag
          SupportsCommands = false,        // No command system
          SupportsForking = false,         // No fork API
          SupportsResume = true,           // --resume flag
          SupportsImageAttachments = false, // CLI doesn't support image input
          SupportsStreaming = true,        // NDJSON streaming
      };

      // CheckAvailabilityAsync:
      //   1. Try: spawn `{BinaryPath} --version`, check exit code 0
      //   2. Try: spawn `{BinaryPath} auth status`, check exit code 0
      //   3. If both succeed: Available(true, null)
      //   4. If version fails: Available(false, "claude binary not found on PATH")
      //   5. If auth fails: Available(false, "claude auth not configured")
      //   Use configurable BinaryPath from FleetOptions.ClaudeCode.BinaryPath

      // SpawnAsync:
      //   1. HarnessHelpers.ValidateWorkingDirectory(options.WorkingDirectory)
      //   2. instanceId = $"claude-code-{Guid.NewGuid():N}"
      //   3. Create ClaudeCodeHarnessInstance with:
      //      - instanceId
      //      - options.SessionId (Fleet session ID — for DB persistence)
      //      - options.WorkingDirectory
      //      - _options.ClaudeCode (config)
      //      - options.Environment
      //      - TimeSpan.FromSeconds(_options.HarnessShutdownTimeoutSeconds)
      //      - _scopeFactory (for DB access)
      //      - loggerFactory-created loggers
      //      - _analyticsCollector
      //      - options.ProjectId
      //      - options.ProjectName
      //   4. If options.InitialPrompt is set:
      //      call instance.SendPromptAsync(options.InitialPrompt, null, ct)
      //   5. Return instance
  }
  ```
  **Acceptance**: `Type` returns `"claude-code"`; availability check works with configurable binary path; `SpawnAsync` creates a valid instance with scopeFactory for persistence; initial prompt is sent when provided.

- [x] 15. **Register Claude Code harness in DI**
  **What**: Add the `ClaudeCodeHarness` singleton registration to `DependencyInjection.AddFleetInfrastructure()` so `HarnessRegistry` discovers it.
  **Files**:
  - Modify `src/WeaveFleet.Infrastructure/DependencyInjection.cs`
  **Details**:
  Add after the OpenCode harness registration block (after line 128):
  ```csharp
  // Claude Code harness — singleton to match HarnessRegistry lifetime.
  // No port allocator or named HttpClient needed (stdio-based).
  services.AddSingleton<IHarness, ClaudeCodeHarness>();
  ```
  Add `using WeaveFleet.Infrastructure.Harnesses.ClaudeCode;` at the top (after line 13).
  **Acceptance**: `HarnessRegistry.GetAll()` returns both `opencode` and `claude-code`; `GET /api/harnesses` lists both; `dotnet build` zero warnings.

### Phase 5: Tests

- [x] 16. **Unit tests: ClaudeCodeModels serialization**
  **What**: Test JSON deserialization of all Claude Code NDJSON message types. Follow the pattern in `OpenCodeModelsSerializationTests`.
  **Files**:
  - Create `tests/WeaveFleet.Infrastructure.Tests/Harnesses/ClaudeCode/ClaudeCodeModelsSerializationTests.cs`
  **Details** — test cases:
  - `SystemInitMessage_Deserializes` — `{"type":"system","subtype":"init","session_id":"xxx","tools":[...]}`
  - `AssistantMessage_WithTextContent_Deserializes` — text block in content array
  - `AssistantMessage_WithToolUse_Deserializes` — tool_use block with id, name, input
  - `AssistantMessage_WithToolResult_Deserializes` — tool_result block
  - `AssistantMessage_WithMixedContent_Deserializes` — text + tool_use in same message
  - `ResultMessage_Success_Deserializes` — `{"type":"result","subtype":"success",...}`
  - `ResultMessage_ErrorMaxTurns_Deserializes` — `{"type":"result","subtype":"error_max_turns",...}`
  - `Usage_Deserializes_WithAllFields` — input_tokens, output_tokens, cache fields
  - `ContentBlock_Polymorphic_Deserializes` — each type discriminator
  - `StreamMessage_MissingTypeDiscriminator_DeserializesAsBaseType` — graceful fallback
  - `ContentBlock_MissingTypeDiscriminator_DeserializesAsBaseType` — graceful fallback
  - `StreamMessage_UnknownType_DeserializesAsBaseType` — new types don't crash
  Use PascalCase test method names (per C# coding standards).
  **Acceptance**: All tests pass; deserialization handles unknown/missing discriminators gracefully.

- [x] 17. **Unit tests: ClaudeCodeMapper**
  **What**: Test mapping from Claude Code DTOs to domain types, including analytics extraction and user message generation.
  **Files**:
  - Create `tests/WeaveFleet.Infrastructure.Tests/Harnesses/ClaudeCode/ClaudeCodeMapperTests.cs`
  **Details** — test cases:
  - `ToHarnessMessage_MapsTextContent_ToTextPart` — assistant message with text → `TextPart`
  - `ToHarnessMessage_MapsToolUse_ToToolUsePart` — tool_use block → `ToolUsePart` with correct ToolCallId/ToolName/Arguments
  - `ToHarnessMessage_MapsToolResult_ToToolResultPart` — tool_result → `ToolResultPart`
  - `ToHarnessMessage_MapsMixedContent_ToMultipleParts` — multiple blocks → multiple parts in order
  - `ToUserMessage_CreatesValidUserMessage` — synthetic user message has correct role, parts, ID format
  - `ToHarnessEvent_SystemInit_MapsCorrectType` — type = "system.init"
  - `ToHarnessEvent_AssistantMessage_MapsCorrectType` — type = "assistant.message"
  - `ToHarnessEvent_Result_MapsSubtype` — type = "result.success"
  - `ToMessagePart_UnknownBlockType_ReturnsNull` — graceful handling
  - `TryExtractTokenEvent_SuccessResult_ReturnsData` — analytics extraction with cost/usage
  - `TryExtractTokenEvent_NoCostData_ReturnsNull` — skip when no analytics data
  - `TryExtractTokenEvent_EstimatedCost_ComputedForKnownModel` — `ModelPricing.EstimateCost` integration
  Pattern: match `OpenCodeMapperTests` (479 lines) structure with arrange-act-assert.
  **Acceptance**: All tests pass.

- [x] 18. **Unit tests: ClaudeCodeHarness metadata and availability**
  **What**: Test harness factory properties and capabilities. Follow `OpenCodeHarnessTests` pattern exactly.
  **Files**:
  - Create `tests/WeaveFleet.Infrastructure.Tests/Harnesses/ClaudeCode/ClaudeCodeHarnessTests.cs`
  **Details** — test cases:
  - `Type_ReturnsClaudeCode` — assert `"claude-code"`
  - `DisplayName_ReturnsClaudeCode` — assert `"Claude Code"`
  - `Capabilities_RequiresInitialPrompt_IsTrue`
  - `Capabilities_SupportsResume_IsTrue`
  - `Capabilities_SupportsModelSelection_IsTrue`
  - `Capabilities_SupportsStreaming_IsTrue`
  - `Capabilities_SupportsAgents_IsFalse`
  - `Capabilities_SupportsCommands_IsFalse`
  - `Capabilities_SupportsForking_IsFalse`
  - `Capabilities_SupportsImageAttachments_IsFalse`
  - `CheckAvailability_WhenBinaryMissing_ReturnsNotAvailable` — integration test (Trait)
  Helper method `CreateHarness()` creates instance with `NullLogger`, default `FleetOptions`, and a mock `IServiceScopeFactory`:
  ```csharp
  private static ClaudeCodeHarness CreateHarness() =>
      new(
          options: new FleetOptions(),
          scopeFactory: Substitute.For<IServiceScopeFactory>(),
          logger: NullLogger<ClaudeCodeHarness>.Instance,
          loggerFactory: NullLoggerFactory.Instance);
  ```
  **Acceptance**: All tests pass; capabilities match Claude Code's actual feature set.

- [x] 19. **Unit tests: ClaudeCodeStdioClient**
  **What**: Test the NDJSON stream reader with simulated stdout data.
  **Files**:
  - Create `tests/WeaveFleet.Infrastructure.Tests/Harnesses/ClaudeCode/ClaudeCodeStdioClientTests.cs`
  **Details** — test cases:
  - `ReadMessagesAsync_ValidNdjsonStream_YieldsAllMessages` — write 3 valid NDJSON lines, assert 3 messages
  - `ReadMessagesAsync_BlankLines_AreSkipped` — blank lines between messages
  - `ReadMessagesAsync_MalformedJson_IsSkippedGracefully` — invalid JSON line doesn't crash
  - `ReadMessagesAsync_EmptyStream_YieldsNothing` — immediate EOF
  - `ReadMessagesAsync_CancellationToken_StopsReading` — cancel mid-stream
  Use a `MemoryStream` + `StreamReader` to simulate process stdout.
  **Acceptance**: All tests pass; malformed input is handled gracefully.

- [x] 20. **Unit tests: ClaudeCodeHarnessInstance persistence**
  **What**: Test that `ClaudeCodeHarnessInstance` correctly persists messages to the database via `IMessageRepository`. This is the critical test that verifies the persistence guarantee.
  **Files**:
  - Create `tests/WeaveFleet.Infrastructure.Tests/Harnesses/ClaudeCode/ClaudeCodeHarnessInstancePersistenceTests.cs`
  **Details** — test cases:
  - `SendPromptAsync_PersistsSyntheticUserMessage` — verify `IMessageRepository.UpsertAsync` is called with a user message containing the prompt text
  - `GetMessagesAsync_ReadsFromDatabase` — verify messages are returned from `IMessageRepository.GetBySessionAsync`, not from in-memory state
  - `GetMessagesAsync_PaginationApplied` — verify limit and before cursor are passed through
  - `GetMessagesAsync_EmptyHistory_ReturnsEmpty` — no messages in DB returns empty page
  - `AssistantMessage_PersistedToDB` — when the stdout pump processes an assistant message, verify `UpsertAsync` is called with correct session ID, role, and parts JSON
  - `PersistenceFailure_DoesNotCrashInstance` — DB error is caught and logged, instance remains healthy

  These tests use NSubstitute to mock `IServiceScopeFactory` and `IMessageRepository`, similar to the pattern in `HarnessEventRelayTests.BuildPersistenceDependencies()`.
  **Acceptance**: All tests pass; every persistence path is verified; errors are handled gracefully.

- [x] 21. **Unit tests: Session harness type tracking**
  **What**: Test that `SessionOrchestrator` correctly stores and uses harness type.
  **Files**:
  - Modify `tests/WeaveFleet.Application.Tests/Services/SessionOrchestratorTests.cs` — add new test cases
  **Details** — new test cases:
  - `CreateSessionAsync_StoresHarnessType` — verify the session passed to `ISessionRepository.InsertAsync` has correct `HarnessType`
  - `CreateSessionAsync_DefaultsToOpenCode` — verify when no harnessType specified, defaults to "opencode"
  - `ResumeSessionAsync_UsesStoredHarnessType` — verify that resume resolves the harness by the session's stored `HarnessType`, not the hardcoded default
  - `ForkSessionAsync_InheritsParentHarnessType` — verify forking uses parent's harness type
  **Acceptance**: All tests pass; harness type is correctly round-tripped.

- [x] 22. **Create contract test fixture for Claude Code messages**
  **What**: Add a contract test fixture JSON file defining expected Claude Code → Fleet message mappings, similar to `tests/contracts/opencode-to-fleet-messages.json`.
  **Files**:
  - Create `tests/contracts/claudecode-to-fleet-messages.json`
  - Create `tests/WeaveFleet.Infrastructure.Tests/Harnesses/ClaudeCode/ClaudeCodeToFleetContractTests.cs`
  **Details**: Follow the `OpenCodeToFleetContractTests` pattern. The fixture contains:
  ```json
  {
    "cases": [
      {
        "name": "text-only assistant message",
        "claudecode_input": { "type": "assistant", "message": { "id": "msg_1", "content": [{ "type": "text", "text": "Hello" }], "role": "assistant" } },
        "expected_fleet_message": { "id": "msg_1", "role": "assistant", "parts": [{ "type": "text", "kind": 0, "text": "Hello" }] }
      }
    ]
  }
  ```
  The test loads the fixture, deserializes the `claudecode_input` as `ClaudeCodeAssistantMessage`, maps through `ClaudeCodeMapper.ToHarnessMessage`, serializes with API settings, and asserts structural JSON equality against `expected_fleet_message`.
  **Acceptance**: Contract tests pass, ensuring Claude Code messages map to the same Fleet format as OpenCode.

- [x] 23. **Verify HarnessRegistryTests and all existing tests still pass**
  **What**: The existing `HarnessRegistryTests.GetAvailabilityAsync_AggregatesAllHarnesses` already uses a `"claude-code"` fake. Verify no test changes needed — just confirm all existing tests pass after all refactoring.
  **Files**:
  - Review `tests/WeaveFleet.Infrastructure.Tests/Harnesses/HarnessRegistryTests.cs` — no changes expected
  **Acceptance**: `dotnet test` passes all existing + new tests.

## Verification

- [ ] `grep -r "OpenCode" src/WeaveFleet.Infrastructure/Services/HarnessEventRelay.cs` returns NO matches
- [ ] `grep -r "using WeaveFleet.Infrastructure.Harnesses" src/WeaveFleet.Infrastructure/Services/HarnessEventRelay.cs` returns NO matches
- [ ] `dotnet build src/WeaveFleet.Infrastructure/WeaveFleet.Infrastructure.csproj` — zero warnings
- [ ] `dotnet build` (solution-wide) — zero warnings
- [ ] `dotnet test` — all tests pass (existing + new)
- [ ] Manual: `GET /api/harnesses` returns both `opencode` and `claude-code` with correct capabilities
- [ ] Manual: Create session with `harnessType: "opencode"` and verify persistence still works (refactor didn't break anything)
- [ ] Manual: Create session with `harnessType: "claude-code"` and verify:
  - Session has `harness_type = 'claude-code'` in the database
  - Process spawns correctly
  - Prompt is persisted as user message in `messages` table
  - Assistant messages are persisted with correct `parts_json`
  - `GET /sessions/{id}/messages` returns messages from DB
- [ ] Manual: Send prompt and observe streamed events via WebSocket
- [ ] Manual: Abort mid-prompt and verify process is killed
- [ ] Manual: Verify analytics token events are emitted for completed prompts
- [ ] Manual: Resume a Claude Code session and verify correct harness type is used
- [ ] Manual: Fork a session and verify harness type is inherited
- [ ] Manual: Verify OpenCode sessions continue to work identically (no regression)

## Implementation Order

```
Phase 1: Architecture Refactor (MUST be done first — establishes the pattern)
  1.  OpenCodeHarnessInstance → own persistence    ← no dependencies
  2.  OpenCodeHarness → pass IServiceScopeFactory  ← depends on 1
  3.  HarnessEventRelay → remove all OpenCode code ← depends on 1
  4.  Update HarnessEventRelay tests + move persistence tests ← depends on 1, 3

Phase 2: Session Tracking
  5.  Database migration (harness_type)            ← no dependencies
  6.  Session entity + orchestrator update         ← depends on 5

Phase 3: Claude Code Infrastructure (can parallel with Phase 2)
  7.  FleetOptions (config)                        ← no dependencies
  8.  ClaudeCodeModels (DTOs)                      ← no dependencies
  9.  HarnessHelpers (shared)                      ← modifies OpenCode slightly
  10. ClaudeCodeProcessManager                     ← depends on 7
  11. ClaudeCodeStdioClient                        ← depends on 8
  12. ClaudeCodeMapper                             ← depends on 8

Phase 4: Assembly
  13. ClaudeCodeHarnessInstance                    ← depends on 10, 11, 12 (instance-owned persistence)
  14. ClaudeCodeHarness                            ← depends on 7, 9, 13
  15. DI Registration                              ← depends on 14

Phase 5: Tests
  16. Tests: ClaudeCodeModels                      ← depends on 8
  17. Tests: ClaudeCodeMapper                      ← depends on 12
  18. Tests: ClaudeCodeHarness                     ← depends on 14
  19. Tests: ClaudeCodeStdioClient                 ← depends on 11
  20. Tests: ClaudeCodeHarnessInstance persistence  ← depends on 13
  21. Tests: Session harness type                  ← depends on 6
  22. Contract test fixture                        ← depends on 12
  23. Verify all existing tests                    ← depends on 15
```

## File Summary

### New files (11)
| File | Layer | Purpose |
|------|-------|---------|
| `src/WeaveFleet.Infrastructure/Migrations/003_add_harness_type.sql` | Infrastructure | Schema migration for harness type tracking |
| `src/WeaveFleet.Infrastructure/Harnesses/HarnessHelpers.cs` | Infrastructure | Shared working directory validation |
| `src/WeaveFleet.Infrastructure/Harnesses/ClaudeCode/ClaudeCodeModels.cs` | Infrastructure | NDJSON DTOs |
| `src/WeaveFleet.Infrastructure/Harnesses/ClaudeCode/ClaudeCodeProcessManager.cs` | Infrastructure | Process-per-prompt lifecycle |
| `src/WeaveFleet.Infrastructure/Harnesses/ClaudeCode/ClaudeCodeStdioClient.cs` | Infrastructure | NDJSON stream reader |
| `src/WeaveFleet.Infrastructure/Harnesses/ClaudeCode/ClaudeCodeMapper.cs` | Infrastructure | DTO → Domain mapping + analytics extraction |
| `src/WeaveFleet.Infrastructure/Harnesses/ClaudeCode/ClaudeCodeHarnessInstance.cs` | Infrastructure | `IHarnessInstance` implementation with instance-owned DB persistence |
| `src/WeaveFleet.Infrastructure/Harnesses/ClaudeCode/ClaudeCodeHarness.cs` | Infrastructure | `IHarness` implementation |
| `tests/contracts/claudecode-to-fleet-messages.json` | Tests | Contract fixture |

### Modified files (7)
| File | Change |
|------|--------|
| `src/WeaveFleet.Infrastructure/Services/HarnessEventRelay.cs` | **MAJOR**: Remove all OpenCode imports, `TryPersistMessageAsync`, `TryPersistPartAsync`, `LogPersistFailed`; add `session_id` to rewrite targets. Becomes pure broadcast service. |
| `src/WeaveFleet.Infrastructure/Harnesses/OpenCode/OpenCodeHarnessInstance.cs` | **MAJOR**: Add `IServiceScopeFactory` + `fleetSessionId` constructor params; add `TryPersistMessageAsync` + `TryPersistPartAsync` private methods (moved from relay); intercept events in `SubscribeAsync` for persistence |
| `src/WeaveFleet.Infrastructure/Harnesses/OpenCode/OpenCodeHarness.cs` | Add `IServiceScopeFactory` constructor param; pass to instance via `SpawnAsync`; delegate `ValidateWorkingDirectory` to `HarnessHelpers` |
| `src/WeaveFleet.Domain/Entities/Session.cs` | Add `HarnessType` property |
| `src/WeaveFleet.Application/Services/SessionOrchestrator.cs` | Store harness type on create; use stored type on resume; inherit on fork |
| `src/WeaveFleet.Application/Configuration/FleetOptions.cs` | Add `ClaudeCodeOptions` nested class + property |
| `src/WeaveFleet.Infrastructure/DependencyInjection.cs` | Register `ClaudeCodeHarness` as `IHarness` singleton |
| `src/WeaveFleet.Api/appsettings.json` | Add `ClaudeCode` configuration section |

### New test files (8)
| File | Coverage |
|------|----------|
| `tests/WeaveFleet.Infrastructure.Tests/Harnesses/OpenCode/OpenCodeHarnessInstancePersistenceTests.cs` | Persistence logic moved from relay (relocated tests) |
| `tests/WeaveFleet.Infrastructure.Tests/Harnesses/ClaudeCode/ClaudeCodeModelsSerializationTests.cs` | DTO deserialization + fallback handling |
| `tests/WeaveFleet.Infrastructure.Tests/Harnesses/ClaudeCode/ClaudeCodeMapperTests.cs` | Domain mapping + analytics extraction |
| `tests/WeaveFleet.Infrastructure.Tests/Harnesses/ClaudeCode/ClaudeCodeHarnessTests.cs` | Factory metadata + capabilities |
| `tests/WeaveFleet.Infrastructure.Tests/Harnesses/ClaudeCode/ClaudeCodeStdioClientTests.cs` | NDJSON stream reading |
| `tests/WeaveFleet.Infrastructure.Tests/Harnesses/ClaudeCode/ClaudeCodeHarnessInstancePersistenceTests.cs` | Database persistence verification |
| `tests/WeaveFleet.Infrastructure.Tests/Harnesses/ClaudeCode/ClaudeCodeToFleetContractTests.cs` | Message mapping contract |

### Modified test files (2)
| File | Change |
|------|--------|
| `tests/WeaveFleet.Infrastructure.Tests/Services/HarnessEventRelayTests.cs` | Remove persistence-specific tests (moved to OpenCode instance tests); add `session_id` rewrite test; keep broadcast/lifecycle tests |
| `tests/WeaveFleet.Application.Tests/Services/SessionOrchestratorTests.cs` | Add harness type tracking tests (create, resume, fork) |

## Risks and Mitigations

| Risk | Impact | Mitigation |
|------|--------|------------|
| OpenCode persistence refactor introduces race condition | Messages lost or duplicated | Fire-and-forget pattern is identical to current relay behavior; instance sees events before relay, so persistence happens earlier (improvement) |
| `OpenCodeHarnessInstance` constructor change breaks DI | Compilation failure | `OpenCodeHarness.SpawnAsync` creates instances manually (not DI), so only `SpawnAsync` needs updating |
| `HarnessEventRelay` tests break after persistence removal | Test failures | Tests are moved, not deleted — same behaviors verified at the instance level |
| Claude Code CLI format changes between versions | Models break | Pin to known version in tests; use tolerant deserialization (`IgnoreUnrecognizedTypeDiscriminators`, skip unknown properties) |
| Process-per-prompt overhead (process spawn latency) | Slow prompt response | Fire-and-forget pattern hides latency from API layer; background pump handles streaming |
| `--resume` flag behavior may differ from expectations | Session continuity breaks | Test with actual Claude Code binary; handle missing `session_id` gracefully |
| `claude auth status` may not exist in all CLI versions | Availability check fails | Fallback to just `claude --version` if auth check fails with unrecognized exit code |
| `JsonNamingPolicy.SnakeCaseLower` may not exist in target framework | Build fails | .NET 8+ includes `SnakeCaseLower`; net10.0 supports it. Fallback: explicit `[JsonPropertyName]` on every property |
| Analytics token extraction differs from OpenCode pattern | Cost data missing or incorrect | `TryExtractTokenEvent` wraps in try/catch returning null on any failure |
| `ResumeSessionAsync` migration may break existing tests | Test failures | Default `harness_type = 'opencode'` ensures backward compatibility |
| Concurrent prompt/persist could race on message ordering | Timestamps out of order | Use `DateTimeOffset.UtcNow` at message creation time; DB orders by timestamp |
| `IServiceScopeFactory` in instance creates scoped service dependencies | Memory leaks if scopes not disposed | Always `using var scope = ...` pattern |
