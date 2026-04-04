# OpenCode Harness Implementation

## TL;DR
> **Summary**: Implement the concrete `IHarness` and `IHarnessInstance` for OpenCode — spawning `opencode serve` processes, communicating via HTTP/SSE, and mapping OpenCode's API types to Weave Fleet domain types.
> **Estimated Effort**: Large

## Context
### Original Request
Build the OpenCode harness — the Infrastructure-layer bridge between Weave Fleet and the OpenCode AI coding agent. The harness must spawn `opencode serve` processes, manage their lifecycle, and communicate via OpenCode's HTTP API (sessions, messages, events, abort, health).

### Key Findings

**Domain contracts are fully defined.** `IHarnessInstance` requires: `SendPromptAsync`, `AbortAsync`, `GetMessagesAsync`, `SubscribeAsync` (returns `IAsyncEnumerable<HarnessEvent>`), `CheckHealthAsync`, `StopAsync`, plus `InstanceId`, `HarnessType`, `Status`, and `IAsyncDisposable`. `IHarness` requires: `Type`, `DisplayName`, `Capabilities`, `CheckAvailabilityAsync`, `SpawnAsync`.

**`SessionOrchestrator` is the only caller.** It calls `harness.SpawnAsync(options, ct)` to get an `IHarnessInstance`, then `instance.SendPromptAsync(text, options, ct)` for prompts. The `InstanceTracker` holds live instances in a `ConcurrentDictionary`. The orchestrator passes `port: 0` to `RegisterInstanceAsync` — port is a harness-internal detail.

**OpenCode's API is workspace-scoped.** All session/message routes go through `WorkspaceRouterMiddleware` which reads a `directory` query parameter (or `x-opencode-directory` header) to scope operations to a project directory. This means we must pass `?directory=<workdir>` on all instance-scoped requests.

**OpenCode prompt format uses `parts` array.** The `POST /session/:id/message` body expects `{ parts: [{ type: "text", text: "..." }], agent?: string, model?: { providerID, modelID } }`. The `sessionID` is in the URL path, not the body. Response is a single streamed JSON object `{ info: AssistantMessage, parts: Part[] }`.

**Two event endpoints exist.** `GET /global/event` (control-plane level) and `GET /event?directory=<dir>` (instance/workspace level). The workspace-scoped `/event` is the one to use — it emits `session.status`, `message.part.delta`, `message.part.updated`, `message.updated`, etc. Format: SSE with `data: <json>` lines where json is `{ type: string, properties: {...} }`.

**Auth is HTTP Basic.** Controlled by `OPENCODE_SERVER_PASSWORD` and `OPENCODE_SERVER_USERNAME` env vars. Default username is `"opencode"` if not set. We should generate a random password per instance.

**Ready signal.** `opencode serve` prints `"opencode server listening on http://{hostname}:{port}"` to stdout when ready. Parse this to know the server is up before making HTTP calls.

**`FleetOptions` currently lacks harness port range.** We need to add `HarnessPortRangeStart` and `HarnessPortRangeEnd` fields (or use ephemeral port allocation — port 0 — and parse the actual port from the ready signal).

**HarnessCapabilities flags.** OpenCode supports: `RequiresInitialPrompt = false`, `SupportsAgents = true`, `SupportsModelSelection = true` (via `GET /provider`), `SupportsCommands = true` (via `POST /session/:id/command`), `SupportsForking = true` (via `POST /session/:id/fork`), `SupportsResume = false` (sessions are server-side; resuming means reconnecting), `SupportsImageAttachments = true` (file parts with mime), `SupportsStreaming = true` (SSE events).

**Build conventions.** .NET 10.0, C# 14, `TreatWarningsAsErrors`, `AnalysisLevel=latest-recommended`, nullable enabled. `sealed` on non-inherited classes. `record` for DTOs. XML doc comments on all public types/members. `IHttpClientFactory` pattern for HTTP clients. Existing test project uses xUnit with `CA1707` suppressed for test naming.

## Objectives
### Core Objective
Deliver a production-quality `OpenCodeHarness` (`IHarness`) and `OpenCodeHarnessInstance` (`IHarnessInstance`) in the Infrastructure layer that fully implement the domain contracts, wired into DI, with comprehensive unit tests.

### Deliverables
- [ ] OpenCode DTO records for all API request/response shapes
- [ ] Typed HTTP client wrapping the full OpenCode API
- [ ] Process manager for spawning/monitoring `opencode serve`
- [ ] Port allocator for thread-safe port management
- [ ] `IHarness` implementation (factory + availability check)
- [ ] `IHarnessInstance` implementation (lifecycle + communication)
- [ ] DI registration wiring
- [ ] `FleetOptions` extension for harness configuration
- [ ] Unit tests for DTOs, port allocator, harness, and instance

### Definition of Done
- [ ] `dotnet build` succeeds with zero warnings
- [ ] `dotnet test` passes all new and existing tests
- [ ] `HarnessRegistry.GetByType("opencode")` returns the OpenCode harness
- [ ] Integration smoke: spawn → health check → create session → send prompt → get messages → abort → stop

### Guardrails (Must NOT)
- Must NOT modify Domain or Application layer interfaces (they are stable)
- Must NOT add OpenCode-specific logic to `SessionOrchestrator`
- Must NOT use synchronous I/O or blocking calls
- Must NOT expose OpenCode internals through domain types (all mapping happens in Infrastructure)
- Must NOT skip `CancellationToken` propagation on any async method

## TODOs

### Phase 1: Configuration & Foundation

- [x] 1. **Extend FleetOptions for harness configuration**
  **What**: Add harness-related configuration properties to `FleetOptions` for port range and process settings.
  **Files**: `src/WeaveFleet.Application/Configuration/FleetOptions.cs`
  **Details**:
  - Add `HarnessPortRangeStart` (default: `10000`)
  - Add `HarnessPortRangeEnd` (default: `10999`)
  - Add `HarnessStartupTimeoutSeconds` (default: `30`)
  - Add `HarnessShutdownTimeoutSeconds` (default: `10`)
  - All with XML doc comments
  **Acceptance**: `FleetOptions` compiles with new properties, defaults are sensible

- [x] 2. **Create OpenCode DTO models**
  **What**: C# record types mapping every OpenCode API JSON shape needed by the harness.
  **Files**: `src/WeaveFleet.Infrastructure/Harnesses/OpenCode/OpenCodeModels.cs`
  **Details**:
  Records to create (all in namespace `WeaveFleet.Infrastructure.Harnesses.OpenCode`):
  
  ```
  // Health
  OpenCodeHealthResponse { Healthy, Version }
  
  // Session
  OpenCodeSessionInfo { Id, Slug, ProjectId, Directory, Title, Version, Time, ParentId?, WorkspaceId?, Summary?, Share?, Permission?, Revert? }
  OpenCodeSessionTime { Created, Updated, Compacting?, Archived? }
  OpenCodeCreateSessionRequest { ParentId?, Title?, Permission?, WorkspaceId? }
  
  // Messages — discriminated union on "role"
  OpenCodeMessageWithParts { Info, Parts }
  [JsonPolymorphic(TypeDiscriminatorPropertyName = "role")]
  OpenCodeMessageInfo (abstract base)
    OpenCodeUserMessage : OpenCodeMessageInfo { Id, SessionId, Role, Time, Agent, Model, System?, Format?, Tools?, Variant? }
    OpenCodeAssistantMessage : OpenCodeMessageInfo { Id, SessionId, Role, Time, ParentId, ModelId, ProviderId, Agent, Path, Cost, Tokens, Mode, Error?, Summary?, Finish?, Variant? }
  OpenCodeMessageTime { Created, Completed? }
  OpenCodeModelRef { ProviderId, ModelId }
  OpenCodeMessagePath { Cwd, Root }
  OpenCodeTokenUsage { Input, Output, Reasoning, Cache, Total? }
  OpenCodeCacheTokens { Read, Write }
  
  // Message Parts — discriminated union on "type"
  [JsonPolymorphic(TypeDiscriminatorPropertyName = "type")]
  OpenCodeMessagePart (abstract base)
    OpenCodeTextPart { Id, SessionId, MessageId, Text, Synthetic?, Ignored?, Time?, Metadata? }
    OpenCodeToolPart { Id, SessionId, MessageId, CallId, Tool, State, Metadata? }
    OpenCodeReasoningPart { ... }
    OpenCodeStepStartPart { ... }
    OpenCodeStepFinishPart { ... }
    OpenCodeFilePart { ... }
    OpenCodeAgentPart { ... }
    OpenCodeSubtaskPart { ... }
    OpenCodeSnapshotPart { ... }
    OpenCodePatchPart { ... }
    OpenCodeRetryPart { ... }
    OpenCodeCompactionPart { ... }
  
  // Tool state — discriminated union on "status"
  [JsonPolymorphic(TypeDiscriminatorPropertyName = "status")]
  OpenCodeToolState (abstract base)
    OpenCodeToolPending, OpenCodeToolRunning, OpenCodeToolCompleted, OpenCodeToolError
  
  // Prompt request
  OpenCodePromptRequest { Parts, Agent?, Model?, NoReply?, Format?, System?, Variant? }
  [JsonPolymorphic(TypeDiscriminatorPropertyName = "type")]
  OpenCodePromptPart (abstract base)
    OpenCodePromptTextPart { Text, Id?, Synthetic?, Ignored? }
    OpenCodePromptFilePart { Mime, Url, Id?, Filename? }
    OpenCodePromptAgentPart { Name, Id? }
    OpenCodePromptSubtaskPart { Prompt, Description, Agent, Id?, Model?, Command? }
  
  // Session status — discriminated union on "type"
  [JsonPolymorphic(TypeDiscriminatorPropertyName = "type")]
  OpenCodeSessionStatus (abstract base)
    OpenCodeIdleStatus, OpenCodeBusyStatus, OpenCodeRetryStatus
  
  // Agents / Providers
  OpenCodeAgentInfo { Name, Description?, Mode?, Hidden?, Model?, ... }
  OpenCodeProviderInfo { Id, Name, Models }
  OpenCodeProviderModel { Id, Name, Capabilities?, ... }
  OpenCodeProvidersResponse { All, Default, Connected }
  
  // SSE Events
  OpenCodeSseEvent { Type, Properties }  (uses JsonElement for Properties)
  // Specific event payloads parsed on demand from Properties
  
  // Fork request
  OpenCodeForkRequest { MessageId? }
  ```
  
  **Serialization rules**:
  - Use `[JsonPropertyName("camelCase")]` on every property (OpenCode uses camelCase JSON)
  - Use `[JsonPolymorphic]` + `[JsonDerivedType]` for discriminated unions
  - Use `JsonElement` for loosely-typed fields (error objects, metadata dictionaries)
  - Define a shared `static readonly JsonSerializerOptions` with `PropertyNamingPolicy = JsonNamingPolicy.CamelCase`, `DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull`
  - All records should be `sealed` (except abstract base records for polymorphism)
  - Use `long` for timestamps (milliseconds since epoch)
  - Use `double` for cost/token counts (OpenCode uses JS `number` which is a double)
  
  **Acceptance**: All DTO records compile; JSON round-trip tests pass (Phase 4)

### Phase 2: HTTP Client & Process Management

- [x] 3. **Create OpenCode HTTP client**
  **What**: Typed HTTP client wrapping all OpenCode API endpoints needed by the harness.
  **Files**: `src/WeaveFleet.Infrastructure/Harnesses/OpenCode/OpenCodeHttpClient.cs`
  **Details**:
  ```csharp
  public sealed class OpenCodeHttpClient(HttpClient httpClient, ILogger<OpenCodeHttpClient> logger)
  ```
  
  Methods:
  - `CheckHealthAsync(CancellationToken ct)` → `OpenCodeHealthResponse`
    - `GET /global/health`
  - `CreateSessionAsync(OpenCodeCreateSessionRequest? request, string directory, CancellationToken ct)` → `OpenCodeSessionInfo`
    - `POST /session?directory={directory}`
  - `GetSessionAsync(string sessionId, string directory, CancellationToken ct)` → `OpenCodeSessionInfo`
    - `GET /session/{sessionId}?directory={directory}`
  - `ListSessionsAsync(string directory, CancellationToken ct)` → `IReadOnlyList<OpenCodeSessionInfo>`
    - `GET /session?directory={directory}`
  - `DeleteSessionAsync(string sessionId, string directory, CancellationToken ct)` → `bool`
    - `DELETE /session/{sessionId}?directory={directory}`
  - `SendMessageAsync(string sessionId, OpenCodePromptRequest request, string directory, CancellationToken ct)` → `OpenCodeMessageWithParts`
    - `POST /session/{sessionId}/message?directory={directory}`
    - Response is a single streamed JSON object; read the full response body and deserialize
  - `SendPromptAsyncFireAndForget(string sessionId, OpenCodePromptRequest request, string directory, CancellationToken ct)` → void
    - `POST /session/{sessionId}/prompt_async?directory={directory}`
    - Expects 204 No Content
  - `GetMessagesAsync(string sessionId, string directory, int? limit, string? before, CancellationToken ct)` → `IReadOnlyList<OpenCodeMessageWithParts>`
    - `GET /session/{sessionId}/message?directory={directory}[&limit=N][&before=cursor]`
    - Parse `X-Next-Cursor` header for pagination
  - `AbortAsync(string sessionId, string directory, CancellationToken ct)` → `bool`
    - `POST /session/{sessionId}/abort?directory={directory}`
  - `ForkSessionAsync(string sessionId, OpenCodeForkRequest request, string directory, CancellationToken ct)` → `OpenCodeSessionInfo`
    - `POST /session/{sessionId}/fork?directory={directory}`
  - `GetAgentsAsync(string directory, CancellationToken ct)` → `IReadOnlyList<OpenCodeAgentInfo>`
    - `GET /agent?directory={directory}`
  - `GetProvidersAsync(string directory, CancellationToken ct)` → `OpenCodeProvidersResponse`
    - `GET /provider?directory={directory}`
  - `GetSessionStatusAsync(string directory, CancellationToken ct)` → `Dictionary<string, OpenCodeSessionStatus>`
    - `GET /session/status?directory={directory}`
  - `SubscribeToEventsAsync(string directory, CancellationToken ct)` → `IAsyncEnumerable<OpenCodeSseEvent>`
    - `GET /event?directory={directory}` with `HttpCompletionOption.ResponseHeadersRead`
    - Parse SSE stream: read lines, find `data: <json>` lines, deserialize to `OpenCodeSseEvent`
    - Handle heartbeats (log at trace level, don't yield)
    - Handle `server.connected` (log, don't yield)
    - Reconnect on disconnect (with backoff) unless cancellation requested
  
  **Key patterns**:
  - Use `HttpRequestMessage` for all requests to set proper headers
  - Set `Content-Type: application/json` on POST/PUT bodies
  - All methods log at `Debug` level for request/response, `Warning` for errors
  - Throw `HttpRequestException` for non-success status codes with meaningful messages
  - The `HttpClient.BaseAddress` is set at registration time per-instance; this client is **not singleton** — a new named client is created per harness instance
  - Pass `directory` as a query parameter on all workspace-scoped endpoints (everything under `/session`, `/agent`, `/provider`, `/event`)
  
  **Acceptance**: All methods compile; can be tested with mocked `HttpMessageHandler`

- [x] 4. **Create Process Manager**
  **What**: Encapsulates spawning and managing the `opencode serve` process.
  **Files**: `src/WeaveFleet.Infrastructure/Harnesses/OpenCode/OpenCodeProcessManager.cs`
  **Details**:
  ```csharp
  public sealed class OpenCodeProcessManager : IAsyncDisposable
  ```
  
  Constructor parameters:
  - `ILogger<OpenCodeProcessManager> logger`
  
  Methods:
  - `StartAsync(OpenCodeProcessOptions options, CancellationToken ct)` → `OpenCodeProcessInfo`
    - `OpenCodeProcessOptions`: record with `Port`, `Hostname`, `WorkingDirectory`, `Password`, `Username`, `EnvironmentVariables`, `StartupTimeout`
    - `OpenCodeProcessInfo`: record with `ProcessId`, `Port`, `Hostname`, `BaseUrl`
    - Spawn `Process` with:
      - `FileName`: `"opencode"` (resolved from PATH)
      - `Arguments`: `"serve --hostname {hostname} --port {port}"`
      - `WorkingDirectory`: from options
      - `Environment`: set `OPENCODE_SERVER_PASSWORD`, `OPENCODE_SERVER_USERNAME`, plus any additional env vars from options
      - `RedirectStandardOutput = true`, `RedirectStandardError = true`, `UseShellExecute = false`
    - Parse stdout for ready signal: `"opencode server listening on http://{hostname}:{port}"`
      - The actual port might differ from requested if port 0 was used (or port auto-fallback behavior)
      - Parse the actual URL from the output
    - Use `TaskCompletionSource<OpenCodeProcessInfo>` with timeout for the ready signal
    - Monitor stderr for error output (log at Warning level)
    - If process exits before ready signal, throw with stderr content
  - `StopAsync(TimeSpan timeout)` → `void`
    - Send graceful signal (kill the process tree on Windows)
    - Wait for exit with timeout
    - Force kill if timeout exceeded
    - Log exit code
  - `IsRunning` property → `bool`
    - Check `!process.HasExited`
  - `ProcessId` property → `int?`
  - `BaseUrl` property → `Uri?`
  
  **Events**:
  - `event EventHandler<int>? ProcessExited` — fired when the process exits unexpectedly
  
  **Lifecycle**:
  - `DisposeAsync` calls `StopAsync` with a short timeout
  - Tracks whether already disposed
  
  **Platform notes**:
  - On Windows, `Process.Kill(entireProcessTree: true)` for cleanup
  - On Linux/macOS, send SIGTERM then SIGKILL after timeout (but since the ready signal parsing is the same, keep cross-platform via `Process` API)
  
  **Acceptance**: Compiles; process lifecycle logic is testable via abstraction

- [x] 5. **Create Port Allocator**
  **What**: Thread-safe allocator that hands out unique ports from a configured range.
  **Files**: `src/WeaveFleet.Infrastructure/Harnesses/OpenCode/PortAllocator.cs`
  **Details**:
  ```csharp
  public sealed class PortAllocator
  ```
  
  Constructor: `PortAllocator(int rangeStart, int rangeEnd)`
  
  Methods:
  - `AllocatePort()` → `int`
    - Thread-safe (use `lock` or `ConcurrentDictionary`)
    - Returns next available port from range
    - Throws `InvalidOperationException("Port range exhausted: all {count} ports in {start}-{end} are allocated")` if none available
  - `ReleasePort(int port)` → `void`
    - Returns port to the available pool
    - Log warning if port was not in the allocated set
  - `AllocatedCount` property → `int`
  - `AvailableCount` property → `int`
  
  **Implementation**: Use `HashSet<int>` for allocated ports with a `lock`. Start allocation from range start, track a "next" pointer that wraps around. When wrapping, scan for first available.
  
  **Alternative consideration**: Use port 0 (ephemeral) and parse actual port from OpenCode's stdout. This is simpler and avoids port conflicts with other software. **Recommendation: Support both modes.** Default to ephemeral (port 0), but allow explicit range via `FleetOptions`. The process manager parses the actual port from stdout regardless. The port allocator is only needed if a range is explicitly configured.
  
  **Acceptance**: Thread-safety verified in tests; allocate/release cycle works

### Phase 3: Harness & Instance Implementation

- [x] 6. **Create OpenCode Harness (IHarness implementation)**
  **What**: The factory that checks availability and spawns instances.
  **Files**: `src/WeaveFleet.Infrastructure/Harnesses/OpenCode/OpenCodeHarness.cs`
  **Details**:
  ```csharp
  public sealed class OpenCodeHarness(
      IHttpClientFactory httpClientFactory,
      PortAllocator portAllocator,
      FleetOptions options,
      ILogger<OpenCodeHarness> logger) : IHarness
  ```
  
  Properties:
  - `Type` → `"opencode"`
  - `DisplayName` → `"OpenCode"`
  - `Capabilities` → `new HarnessCapabilities { RequiresInitialPrompt = false, SupportsAgents = true, SupportsModelSelection = true, SupportsCommands = true, SupportsForking = true, SupportsResume = false, SupportsImageAttachments = true, SupportsStreaming = true }`
  
  Methods:
  - `CheckAvailabilityAsync(ct)`:
    - Try to find `opencode` on PATH using `Process.Start` with `which opencode` (Unix) or `where opencode` (Windows)
    - Alternatively, try `opencode --version` and check exit code
    - Return `HarnessAvailability(true, null)` if found
    - Return `HarnessAvailability(false, "opencode binary not found on PATH")` if not
    - Catch exceptions, return not available with reason
  
  - `SpawnAsync(HarnessSpawnOptions options, CancellationToken ct)`:
    1. Generate a unique instance ID: `$"opencode-{Guid.NewGuid():N}"`
    2. Allocate port (from allocator or use 0 for ephemeral)
    3. Generate random password: `Convert.ToBase64String(RandomNumberGenerator.GetBytes(32))`
    4. Create `OpenCodeProcessManager` and call `StartAsync` with:
       - Port (allocated or 0)
       - Hostname: `"127.0.0.1"`
       - WorkingDirectory: `options.WorkingDirectory`
       - Password: generated
       - Username: `"opencode"`
       - Env vars: merge `options.Environment`
       - Startup timeout: from `FleetOptions.HarnessStartupTimeoutSeconds`
    5. Get actual port from `OpenCodeProcessInfo.Port`
    6. Create named `HttpClient` via factory, configure base address + Basic Auth header
    7. Create `OpenCodeHttpClient` wrapping the `HttpClient`
    8. Verify health via `httpClient.CheckHealthAsync(ct)` — retry up to 3 times with 500ms delay (process may need a moment after ready signal)
    9. Create and return `OpenCodeHarnessInstance`
    10. If any step fails, clean up (stop process, release port)
  
  **Error handling**:
  - Wrap all in try/catch; on failure, dispose process manager, release port, rethrow with context
  - Log at `Information` level on successful spawn, `Error` on failure
  
  **Acceptance**: Compiles; can be unit-tested with mocked dependencies

- [x] 7. **Create OpenCode Harness Instance (IHarnessInstance implementation)**
  **What**: Wraps a running OpenCode process + HTTP client for a single session.
  **Files**: `src/WeaveFleet.Infrastructure/Harnesses/OpenCode/OpenCodeHarnessInstance.cs`
  **Details**:
  ```csharp
  public sealed class OpenCodeHarnessInstance : IHarnessInstance
  ```
  
  Constructor/fields:
  - `string instanceId`
  - `OpenCodeHttpClient httpClient`
  - `OpenCodeProcessManager processManager`
  - `PortAllocator portAllocator`
  - `int allocatedPort`
  - `string workingDirectory`
  - `string? openCodeSessionId` — the OpenCode-side session ID (created lazily on first prompt)
  - `ILogger<OpenCodeHarnessInstance> logger`
  - `HarnessInstanceStatus _status` field
  - `readonly SemaphoreSlim _sessionLock` — protects lazy session creation
  
  Properties:
  - `InstanceId` → from constructor
  - `HarnessType` → `"opencode"`
  - `Status` → `_status` (updated on lifecycle events)
  
  Methods:
  
  - `SendPromptAsync(string text, PromptOptions? options, CancellationToken ct)`:
    1. Ensure OpenCode session exists (lazy create):
       ```
       if (_openCodeSessionId is null)
       {
           await _sessionLock.WaitAsync(ct);
           try {
               if (_openCodeSessionId is null) {
                   var session = await _httpClient.CreateSessionAsync(null, _workingDirectory, ct);
                   _openCodeSessionId = session.Id;
               }
           } finally { _sessionLock.Release(); }
       }
       ```
    2. Build `OpenCodePromptRequest`:
       - `Parts = [new OpenCodePromptTextPart { Text = text }]`
       - If `options?.Agent` is set, map to `Agent` field
       - If `options?.ModelId` is set, parse "provider/model" format or use as-is
       - If `options?.Attachments` is set, add `OpenCodePromptFilePart` entries with data URLs
    3. Call `_httpClient.SendPromptAsyncFireAndForget(sessionId, request, directory, ct)` — **use the async/fire-and-forget endpoint** since the orchestrator doesn't await the response; events arrive via SSE
    4. Update `_status = Running`
    5. Log at `Debug` level
  
  - `GetMessagesAsync(CancellationToken ct)`:
    1. If no OpenCode session exists, return empty list
    2. Call `_httpClient.GetMessagesAsync(sessionId, directory, null, null, ct)`
    3. Map `IReadOnlyList<OpenCodeMessageWithParts>` → `IReadOnlyList<HarnessMessage>` using mapper (Task 8)
    4. Return mapped list
  
  - `SubscribeAsync(CancellationToken ct)` → `IAsyncEnumerable<HarnessEvent>`:
    1. Call `_httpClient.SubscribeToEventsAsync(directory, ct)`
    2. Map each `OpenCodeSseEvent` → `HarnessEvent`:
       - `Type` = event type string (e.g. `"session.status"`, `"message.part.delta"`)
       - `SessionId` = extract from properties if present, else use instance's OpenCode session ID
       - `Timestamp` = `DateTimeOffset.UtcNow` (SSE events don't carry a timestamp field)
       - `Payload` = serialize `Properties` as `JsonElement`
    3. Filter: skip `server.heartbeat` and `server.connected` (already handled in HTTP client)
    4. Yield mapped events
  
  - `AbortAsync(CancellationToken ct)`:
    1. If no OpenCode session, no-op
    2. Call `_httpClient.AbortAsync(sessionId, directory, ct)`
    3. Log at `Information` level
  
  - `CheckHealthAsync(CancellationToken ct)`:
    1. If process manager reports not running: return `new HealthCheckResult(false, "Process exited")`
    2. Try `_httpClient.CheckHealthAsync(ct)`
    3. Return `new HealthCheckResult(true, $"v{response.Version}")`
    4. On exception: return `new HealthCheckResult(false, ex.Message)`
  
  - `StopAsync(CancellationToken ct)`:
    1. Set `_status = Stopping`
    2. If OpenCode session exists, try to delete it (best effort, catch exceptions)
    3. Call `processManager.StopAsync(shutdownTimeout)`
    4. Set `_status = Stopped`
    5. Release port via `portAllocator.ReleasePort(allocatedPort)`
    6. Log at `Information` level
  
  - `DisposeAsync()`:
    1. If not already stopped, call `StopAsync` with a short timeout
    2. Dispose `processManager`
    3. Dispose `_sessionLock`
    4. Dispose `httpClient`'s underlying `HttpClient` (if owned)
  
  **Process crash handling**:
  - Subscribe to `processManager.ProcessExited` in constructor
  - On unexpected exit: set `_status = Error`, log at `Error` level
  
  **Acceptance**: Compiles; all interface methods implemented; lifecycle states correct

- [x] 8. **Create DTO → Domain mapper**
  **What**: Static mapping methods from OpenCode DTOs to Weave Fleet domain types.
  **Files**: `src/WeaveFleet.Infrastructure/Harnesses/OpenCode/OpenCodeMapper.cs`
  **Details**:
  ```csharp
  internal static class OpenCodeMapper
  ```
  
  Methods:
  - `ToHarnessMessage(OpenCodeMessageWithParts msg)` → `HarnessMessage`
    - `Id` = `msg.Info.Id`
    - `Role` = `msg.Info.Role` (from the polymorphic type)
    - `Content` = Extract text from parts: concatenate all `TextPart.Text` values, or for assistant messages, concatenate text parts; for tool parts include tool name + status summary
    - `Timestamp` = Convert `msg.Info.Time.Created` (ms since epoch) to `DateTimeOffset`
  
  - `ToHarnessMessages(IReadOnlyList<OpenCodeMessageWithParts> msgs)` → `IReadOnlyList<HarnessMessage>`
    - Map each via `ToHarnessMessage`
  
  - `ToHarnessEvent(OpenCodeSseEvent evt, string? sessionId)` → `HarnessEvent`
    - `Type` = `evt.Type`
    - `SessionId` = extract from properties or fall back to provided sessionId
    - `Timestamp` = `DateTimeOffset.UtcNow`
    - `Payload` = serialize properties to `JsonElement`
  
  - `ToHarnessAgents(IReadOnlyList<OpenCodeAgentInfo> agents)` → `IReadOnlyList<HarnessAgent>`
    - Map: `Name = agent.Name`, `Description = agent.Description`, `Mode = agent.Mode`
  
  - `ToHarnessProviders(OpenCodeProvidersResponse response)` → `IReadOnlyList<HarnessProvider>`
    - Map each provider + its models
  
  - `DateTimeOffsetFromUnixMs(long ms)` → `DateTimeOffset`
    - `DateTimeOffset.FromUnixTimeMilliseconds(ms)`
  
  **Acceptance**: Mapper compiles; mapping logic is straightforward and testable

### Phase 4: DI Wiring

- [x] 9. **Register OpenCode harness in DI**
  **What**: Wire all OpenCode harness types into the DI container.
  **Files**: `src/WeaveFleet.Infrastructure/DependencyInjection.cs`
  **Details**:
  Add to `AddFleetInfrastructure`:
  ```csharp
  // OpenCode harness — all singletons to avoid captive dependency with HarnessRegistry
  services.AddSingleton(new PortAllocator(options.HarnessPortRangeStart, options.HarnessPortRangeEnd));
  services.AddHttpClient<OpenCodeHttpClient>();  // registers IHttpClientFactory + typed client
  services.AddSingleton<IHarness, OpenCodeHarness>();
  ```
  
  **Important notes**:
  - `OpenCodeHarness` must be singleton (HarnessRegistry is singleton, would create captive dependency otherwise) — this is explicitly stated in the existing code comments
  - `OpenCodeHttpClient` registered via `AddHttpClient<T>()` sets up the typed client factory. But since each harness *instance* needs its own `HttpClient` with a unique base address + auth, the harness should use `IHttpClientFactory.CreateClient("OpenCode")` and configure per-instance. Register a named client:
    ```csharp
    services.AddHttpClient("OpenCode");
    ```
    Then in `OpenCodeHarness.SpawnAsync`, create client via `httpClientFactory.CreateClient("OpenCode")` and configure base address + auth headers manually.
  - Add `using WeaveFleet.Infrastructure.Harnesses.OpenCode;` to the file
  
  **Acceptance**: App starts; `IHarnessRegistry.GetByType("opencode")` returns the OpenCode harness

### Phase 5: Tests

- [x] 10. **OpenCode DTO serialization tests**
  **What**: Verify JSON round-trip for all critical DTO types.
  **Files**: `tests/WeaveFleet.Infrastructure.Tests/Harnesses/OpenCode/OpenCodeModelsSerializationTests.cs`
  **Details**:
  Test cases:
  - `HealthResponse_RoundTrips` — serialize/deserialize `OpenCodeHealthResponse`
  - `SessionInfo_RoundTrips` — `OpenCodeSessionInfo` with all optional fields null, then with all populated
  - `UserMessage_Deserializes` — JSON with `"role": "user"` deserializes to `OpenCodeUserMessage`
  - `AssistantMessage_Deserializes` — JSON with `"role": "assistant"` deserializes to `OpenCodeAssistantMessage`
  - `MessageParts_TextPart_Deserializes` — `"type": "text"` → `OpenCodeTextPart`
  - `MessageParts_ToolPart_Deserializes` — `"type": "tool"` with nested tool state
  - `ToolState_Discriminated_Deserializes` — each status variant
  - `PromptRequest_Serializes` — verify `OpenCodePromptRequest` serializes with correct camelCase keys and discriminated union types
  - `SseEvent_Deserializes` — parse raw SSE data JSON to `OpenCodeSseEvent`
  - `SessionStatus_Discriminated_Deserializes` — idle, busy, retry variants
  - `ProvidersResponse_Deserializes` — with nested models
  
  **Patterns**: Use raw JSON string literals, deserialize, assert properties. Then serialize back and verify key fields.
  
  **Acceptance**: All tests pass; confirms serialization attributes are correct

- [x] 11. **Port allocator tests**
  **What**: Verify thread-safe allocation and edge cases.
  **Files**: `tests/WeaveFleet.Infrastructure.Tests/Harnesses/OpenCode/PortAllocatorTests.cs`
  **Details**:
  Test cases:
  - `AllocatePort_ReturnsPortInRange`
  - `AllocatePort_NeverReturnsSamePortTwice`
  - `AllocatePort_ThrowsWhenExhausted` — small range (e.g., 3 ports), allocate all, assert throws
  - `ReleasePort_AllowsReallocation`
  - `ConcurrentAllocation_NoCollisions` — spawn N tasks allocating from a range of N, verify all unique
  - `AllocatedCount_TracksCorrectly`
  - `AvailableCount_TracksCorrectly`
  
  **Acceptance**: All tests pass; thread-safety verified

- [x] 12. **OpenCode harness tests**
  **What**: Test `OpenCodeHarness` capability reporting and availability check.
  **Files**: `tests/WeaveFleet.Infrastructure.Tests/Harnesses/OpenCode/OpenCodeHarnessTests.cs`
  **Details**:
  Test cases:
  - `Type_ReturnsOpenCode` — asserts `harness.Type == "opencode"`
  - `DisplayName_ReturnsOpenCode` — asserts `harness.DisplayName == "OpenCode"`
  - `Capabilities_ReportsCorrectFlags` — verify each capability bool
  - `CheckAvailability_WhenBinaryExists_ReturnsAvailable` — would need environment setup; mark with `[Trait("Category", "Integration")]` or test the logic path with a mock
  - `CheckAvailability_WhenBinaryMissing_ReturnsNotAvailable` — similar
  
  **Mocking approach**: Use a simple wrapper interface for process invocation, or test the capability/metadata properties directly (which don't need mocks). Availability check can be tested as integration test.
  
  **Acceptance**: Tests pass; harness metadata is correct

- [x] 13. **OpenCode mapper tests**
  **What**: Verify DTO → domain type mapping.
  **Files**: `tests/WeaveFleet.Infrastructure.Tests/Harnesses/OpenCode/OpenCodeMapperTests.cs`
  **Details**:
  Test cases:
  - `ToHarnessMessage_UserMessage_MapsCorrectly`
  - `ToHarnessMessage_AssistantMessage_MapsCorrectly`
  - `ToHarnessMessage_ExtractsTextFromParts`
  - `ToHarnessEvent_MapsTypeAndPayload`
  - `ToHarnessAgents_MapsNameAndDescription`
  - `DateTimeOffsetFromUnixMs_ConvertsCorrectly`
  
  **Acceptance**: All tests pass; mapping logic verified

## Verification
- [x] `dotnet build src/WeaveFleet.Infrastructure/WeaveFleet.Infrastructure.csproj` — zero warnings
- [x] `dotnet test tests/WeaveFleet.Infrastructure.Tests/WeaveFleet.Infrastructure.Tests.csproj` — all tests pass
- [x] `dotnet build` (full solution) — zero warnings, no regressions
- [x] `dotnet test` (full solution) — all tests pass including existing `HarnessRegistryTests`
- [ ] Manual smoke test: verify `HarnessRegistry` resolves OpenCode harness at runtime

## Architecture Diagram

```
SessionOrchestrator (Application)
        │
        ▼
   IHarness.SpawnAsync()
        │
        ▼
┌──────────────────────────────────────────────────────────┐
│  OpenCodeHarness (Infrastructure)                        │
│  - CheckAvailabilityAsync: `opencode --version`          │
│  - SpawnAsync:                                           │
│    1. PortAllocator.AllocatePort()                       │
│    2. OpenCodeProcessManager.StartAsync()                │
│    3. Create HttpClient (base URL + Basic Auth)          │
│    4. Health check                                       │
│    5. Return OpenCodeHarnessInstance                     │
└──────────────────────────────────────────────────────────┘
        │
        ▼
┌──────────────────────────────────────────────────────────┐
│  OpenCodeHarnessInstance (implements IHarnessInstance)    │
│  - Owns: ProcessManager, HttpClient, OpenCode session    │
│  - SendPromptAsync → POST /session/:id/prompt_async      │
│  - GetMessagesAsync → GET /session/:id/message           │
│  - SubscribeAsync → GET /event (SSE)                     │
│  - AbortAsync → POST /session/:id/abort                  │
│  - CheckHealthAsync → GET /global/health                 │
│  - StopAsync → kill process, release port                │
└──────────────────────────────────────────────────────────┘
        │                           │
        ▼                           ▼
  OpenCodeHttpClient         OpenCodeProcessManager
  (HTTP API wrapper)         (Process lifecycle)
        │
        ▼
   opencode serve (Bun process on 127.0.0.1:{port})
```

## File Summary

| # | File | Action | Depends On |
|---|------|--------|------------|
| 1 | `src/WeaveFleet.Application/Configuration/FleetOptions.cs` | Modify | — |
| 2 | `src/WeaveFleet.Infrastructure/Harnesses/OpenCode/OpenCodeModels.cs` | Create | — |
| 3 | `src/WeaveFleet.Infrastructure/Harnesses/OpenCode/OpenCodeHttpClient.cs` | Create | 2 |
| 4 | `src/WeaveFleet.Infrastructure/Harnesses/OpenCode/OpenCodeProcessManager.cs` | Create | — |
| 5 | `src/WeaveFleet.Infrastructure/Harnesses/OpenCode/PortAllocator.cs` | Create | — |
| 6 | `src/WeaveFleet.Infrastructure/Harnesses/OpenCode/OpenCodeHarness.cs` | Create | 1, 3, 4, 5 |
| 7 | `src/WeaveFleet.Infrastructure/Harnesses/OpenCode/OpenCodeHarnessInstance.cs` | Create | 3, 4, 5, 8 |
| 8 | `src/WeaveFleet.Infrastructure/Harnesses/OpenCode/OpenCodeMapper.cs` | Create | 2 |
| 9 | `src/WeaveFleet.Infrastructure/DependencyInjection.cs` | Modify | 5, 6 |
| 10 | `tests/WeaveFleet.Infrastructure.Tests/Harnesses/OpenCode/OpenCodeModelsSerializationTests.cs` | Create | 2 |
| 11 | `tests/WeaveFleet.Infrastructure.Tests/Harnesses/OpenCode/PortAllocatorTests.cs` | Create | 5 |
| 12 | `tests/WeaveFleet.Infrastructure.Tests/Harnesses/OpenCode/OpenCodeHarnessTests.cs` | Create | 6 |
| 13 | `tests/WeaveFleet.Infrastructure.Tests/Harnesses/OpenCode/OpenCodeMapperTests.cs` | Create | 8 |

## Implementation Order

Execute in this order to minimize churn:

1. **Task 1** (FleetOptions) — no dependencies
2. **Task 2** (DTOs) — no dependencies, unlocks everything else
3. **Task 5** (PortAllocator) — standalone
4. **Task 4** (ProcessManager) — standalone
5. **Task 3** (HttpClient) — needs DTOs
6. **Task 8** (Mapper) — needs DTOs
7. **Task 6** (Harness) — needs HttpClient, ProcessManager, PortAllocator
8. **Task 7** (Instance) — needs HttpClient, ProcessManager, PortAllocator, Mapper
9. **Task 9** (DI wiring) — needs Harness, PortAllocator
10. **Tasks 10-13** (Tests) — can be written alongside their implementation tasks

## Pitfalls & Mitigations

| Risk | Mitigation |
|------|------------|
| Port conflicts with other software | Default to ephemeral port (0) and parse actual port from stdout; explicit range is opt-in |
| `opencode serve` slow startup on first run | Configurable timeout (`HarnessStartupTimeoutSeconds`); log progress |
| Process zombies on crash | Register process exit handler; `DisposeAsync` always attempts kill; use `Process.Kill(entireProcessTree: true)` |
| SSE stream disconnects | Reconnect with exponential backoff in `SubscribeToEventsAsync`; yield `HarnessEvent` with type `"connection.lost"` on disconnect |
| `System.Text.Json` polymorphic deserialization with camelCase | Test thoroughly in Task 10; use `[JsonPropertyName]` explicitly on all properties since `JsonPolymorphic` discriminator must match exact JSON key |
| `TreatWarningsAsErrors` blocking build | Use `required` on necessary properties; suppress only with justification; test build early |
| OpenCode workspace routing requires `directory` param | Pass `?directory=<workdir>` on all workspace-scoped requests; HTTP client methods take `directory` parameter |
| `HttpClient` lifetime management | Use `IHttpClientFactory` named client pattern; create per-instance, dispose with instance |
