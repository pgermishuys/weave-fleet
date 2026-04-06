# Harness Send Command Support

## TL;DR
> **Summary**: Add `SendCommandAsync` to `IHarnessInstance` so Fleet can execute slash commands (e.g. `/compact`, `/clear`) against agent harnesses, with native support for OpenCode and prompt-delegation for Claude Code.
> **Estimated Effort**: Short

## Context
### Original Request
Add command execution support to the harness system. Harnesses can already **list** commands via `GetCommandsAsync()`, but there's no way to **execute** one. OpenCode has a native `POST /session/:id/command` endpoint. Claude Code has no command concept — commands should be formatted as prompts.

### Key Findings
- **`IHarnessInstance`** (52 lines) defines the harness contract. Has `GetCommandsAsync` but no send/execute method.
- **`HarnessTypes.cs`** (135 lines) has `CommandInfo`, `PromptOptions`, `HarnessCapabilities.SupportsCommands` — but no `CommandOptions` record.
- **`OpenCodeHttpClient`** has `SendPromptAsyncFireAndForget` (lines 112-135) — fire-and-forget POST that returns void. The command endpoint should follow the same pattern exactly.
- **`OpenCodeModels.cs`** has `OpenCodePromptRequest` (lines 364-373) — the command request model will be similar but simpler (no parts, just command + arguments).
- **`OpenCodeHarnessInstance.SendPromptAsync`** (lines 115-170) — the pattern: `EnsureSessionAsync` → build request → call HTTP client → set `_status = Running`. `SendCommandAsync` should mirror this.
- **`ClaudeCodeHarnessInstance.SendPromptAsync`** (lines 116-176) — spawns a CLI process. Command execution should delegate to this with `"/{command} {arguments}"` as the prompt text.
- **`SessionEndpoints.cs`** (line 167-170) has a 501 stub at `POST /api/sessions/{id}/command`.
- **`SessionOrchestrator.PromptSessionAsync`** (lines 255-267) — the pattern for session→instance routing: look up session → get live instance via `GetLiveInstanceAsync` → call method → return `Unit`.
- **`TestHarnessInstance`** (test harness) implements `IHarnessInstance` — must also implement `SendCommandAsync`.
- **`InstanceEndpoints.cs`** has instance-level proxy endpoints for `/commands` (GET), `/agents`, `/models`. An instance-level `POST /command` would be consistent.
- OpenCode API contract: `POST /session/:id/command` with body `{ command, arguments, agent?, model?, messageID? }`. Returns `{ info: Message, parts: Part[] }`. But since we use fire-and-forget (SSE delivers results), we don't need to parse the response — just like `SendPromptAsyncFireAndForget`.

## Objectives
### Core Objective
Enable command execution through the full stack: API → Orchestrator → HarnessInstance → Agent.

### Deliverables
- [ ] `CommandOptions` record in domain layer
- [ ] `SendCommandAsync` method on `IHarnessInstance` interface
- [ ] OpenCode implementation (native HTTP call to `/session/:id/command`)
- [ ] Claude Code implementation (delegate to `SendPromptAsync`)
- [ ] `SessionOrchestrator.CommandSessionAsync` method
- [ ] `POST /api/sessions/{id}/command` endpoint (replace 501 stub)
- [ ] `POST /api/instances/{id}/command` endpoint (instance-level)
- [ ] Test harness implementation

### Definition of Done
- [ ] `dotnet build` succeeds with no errors
- [ ] `dotnet test` passes all existing + new tests
- [ ] `POST /api/sessions/{id}/command` returns 202 for a valid command
- [ ] `POST /api/instances/{id}/command` returns 202 for a valid command

### Guardrails (Must NOT)
- Must NOT change the `SendPromptAsync` signature or behavior
- Must NOT change the SSE event pipeline — commands produce events through the existing stream
- Must NOT add blocking/synchronous command execution — fire-and-forget only
- Must NOT break the existing `GetCommandsAsync` endpoint

## TODOs

### Domain Layer

- [ ] 1. **Add `CommandOptions` record to `HarnessTypes.cs`**
  **What**: Add a new record after `PromptOptions` (line 102) with the shape:
  ```csharp
  public sealed record CommandOptions
  {
      public required string Command { get; init; }
      public string? Arguments { get; init; }
      public string? Agent { get; init; }
      public string? ModelId { get; init; }
  }
  ```
  **Files**: `src/WeaveFleet.Domain/Harnesses/HarnessTypes.cs`
  **Acceptance**: Record compiles, follows same style as `PromptOptions`

- [ ] 2. **Add `SendCommandAsync` to `IHarnessInstance`**
  **What**: Add after `SendPromptAsync` (line 30):
  ```csharp
  /// <summary>Execute a slash command on the agent.</summary>
  Task SendCommandAsync(CommandOptions options, CancellationToken ct);
  ```
  **Files**: `src/WeaveFleet.Domain/Harnesses/IHarnessInstance.cs`
  **Acceptance**: Interface compiles; all implementors will need updating (next steps)

### Infrastructure — OpenCode

- [ ] 3. **Add `OpenCodeCommandRequest` model to `OpenCodeModels.cs`**
  **What**: Add after the `OpenCodePromptRequest` section (~line 373) a new request model:
  ```csharp
  /// <summary>Request body for POST /session/:id/command.</summary>
  internal sealed record OpenCodeCommandRequest
  {
      [JsonPropertyName("command")] public required string Command { get; init; }
      [JsonPropertyName("arguments")] public string? Arguments { get; init; }
      [JsonPropertyName("agent")] public string? Agent { get; init; }
      [JsonPropertyName("model")] public OpenCodeModelRefRequest? Model { get; init; }
      [JsonPropertyName("messageID")] public string? MessageId { get; init; }
  }
  ```
  Note: Uses `JsonPropertyName` attributes consistent with all other models in the file. The `model` field uses `OpenCodeModelRefRequest` (uppercase D casing for requests) matching the existing pattern for write-path model refs.
  **Files**: `src/WeaveFleet.Infrastructure/Harnesses/OpenCode/OpenCodeModels.cs`
  **Acceptance**: Model compiles, follows established naming/attribute conventions

- [ ] 4. **Add `SendCommandAsync` to `OpenCodeHttpClient`**
  **What**: Add a new method after `SendPromptAsyncFireAndForget` (line 135), following the same fire-and-forget pattern:
  ```csharp
  /// <summary>POST /session/{sessionId}/command?directory={directory} — fire and forget.</summary>
  public async Task SendCommandAsync(
      string sessionId,
      OpenCodeCommandRequest request,
      string directory,
      CancellationToken ct)
  {
      var url = BuildUrl($"/session/{Uri.EscapeDataString(sessionId)}/command", directory);
      LogRequest(_logger, $"POST {url}", null);

      var content = new StringContent(
          JsonSerializer.Serialize(request, OpenCodeJsonOptions.Default),
          Encoding.UTF8,
          "application/json");

      using var response = await _httpClient.PostAsync(url, content, ct).ConfigureAwait(false);
      LogResponse(_logger, (int)response.StatusCode, url, null);

      if (!response.IsSuccessStatusCode)
      {
          LogRequestFailed(_logger, (int)response.StatusCode, url, null);
          response.EnsureSuccessStatusCode();
      }
  }
  ```
  This is an exact structural copy of `SendPromptAsyncFireAndForget` (lines 113-135) with the URL changed to `/command`.
  **Files**: `src/WeaveFleet.Infrastructure/Harnesses/OpenCode/OpenCodeHttpClient.cs`
  **Acceptance**: Method compiles, follows same pattern as `SendPromptAsyncFireAndForget`

- [ ] 5. **Implement `SendCommandAsync` in `OpenCodeHarnessInstance`**
  **What**: Add a new method after `SendPromptAsync` (line 170). Follow the same pattern:
  1. Call `EnsureSessionAsync(ct)`
  2. Build `OpenCodeModelRefRequest` from `options.ModelId` (same slash-split logic as `SendPromptAsync` lines 137-153)
  3. Build `OpenCodeCommandRequest` from `CommandOptions`
  4. Call `_httpClient.SendCommandAsync(_openCodeSessionId!, request, _workingDirectory, ct)`
  5. Set `_status = HarnessInstanceStatus.Running`

  Also add a `LoggerMessage` field for the new method:
  ```csharp
  private static readonly Action<ILogger, string, Exception?> LogSendCommand =
      LoggerMessage.Define<string>(LogLevel.Debug, new EventId(7, "SendCommand"),
          "Sending command to OpenCode instance {InstanceId}.");
  ```
  Use EventId 7 (next available after the existing 1-6).
  **Files**: `src/WeaveFleet.Infrastructure/Harnesses/OpenCode/OpenCodeHarnessInstance.cs`
  **Acceptance**: Method compiles, mirrors `SendPromptAsync` structure

### Infrastructure — Claude Code

- [ ] 6. **Implement `SendCommandAsync` in `ClaudeCodeHarnessInstance`**
  **What**: Add after `SendPromptAsync` (line 176). Claude Code has no native command concept, so format the command as a prompt and delegate:
  ```csharp
  /// <inheritdoc />
  public Task SendCommandAsync(CommandOptions options, CancellationToken ct)
  {
      var promptText = string.IsNullOrWhiteSpace(options.Arguments)
          ? $"/{options.Command}"
          : $"/{options.Command} {options.Arguments}";

      var promptOptions = options.Agent is not null || options.ModelId is not null
          ? new PromptOptions { Agent = options.Agent, ModelId = options.ModelId }
          : null;

      return SendPromptAsync(promptText, promptOptions, ct);
  }
  ```
  **Files**: `src/WeaveFleet.Infrastructure/Harnesses/ClaudeCode/ClaudeCodeHarnessInstance.cs`
  **Acceptance**: Method compiles, delegates to `SendPromptAsync` with formatted slash-command text

### Infrastructure — Test Harness & Test Fakes

- [ ] 7. **Implement `SendCommandAsync` in `TestHarnessInstance`**
  **What**: Add after `SendPromptAsync` (line 90). The test harness should delegate to `SendPromptAsync` with formatted text (same as Claude Code approach), so test scenarios that enqueue prompt responses also work for commands:
  ```csharp
  /// <inheritdoc/>
  public Task SendCommandAsync(CommandOptions options, CancellationToken ct)
  {
      var text = string.IsNullOrWhiteSpace(options.Arguments)
          ? $"/{options.Command}"
          : $"/{options.Command} {options.Arguments}";

      var promptOptions = options.Agent is not null || options.ModelId is not null
          ? new PromptOptions { Agent = options.Agent, ModelId = options.ModelId }
          : null;

      return SendPromptAsync(text, promptOptions, ct);
  }
  ```
  **Files**: `tests/WeaveFleet.TestHarness/TestHarnessInstance.cs`
  **Acceptance**: Compiles, reuses existing prompt response queuing mechanism

- [ ] 7b. **Implement `SendCommandAsync` in `FakeInstance` (test fake)**
  **What**: The `FakeInstance` class in `HarnessEventRelayTests.cs` is a hand-rolled `IHarnessInstance` used in relay tests. Add a no-op implementation:
  ```csharp
  public Task SendCommandAsync(CommandOptions options, CancellationToken ct) => Task.CompletedTask;
  ```
  **Files**: `tests/WeaveFleet.Infrastructure.Tests/Services/HarnessEventRelayTests.cs`
  **Acceptance**: Compiles, no-op (test fake only needs to satisfy the interface)

### Application Layer — Orchestrator

- [ ] 8. **Add `CommandSessionAsync` to `SessionOrchestrator`**
  **What**: Add after `PromptSessionAsync` (line 267). Follow the exact same pattern:
  ```csharp
  public async Task<Result<Unit>> CommandSessionAsync(
      string id,
      CommandOptions options,
      CancellationToken ct = default)
  {
      var instanceResult = await GetLiveInstanceAsync(id);
      if (instanceResult.IsFailure)
          return instanceResult.Error;

      await instanceResult.Value.SendCommandAsync(options, ct);
      return Unit.Value;
  }
  ```
  **Files**: `src/WeaveFleet.Application/Services/SessionOrchestrator.cs`
  **Acceptance**: Method compiles, follows same `GetLiveInstanceAsync` → call → return pattern as `PromptSessionAsync`

### API Layer

- [ ] 9. **Add `SendCommandApiRequest` record to `SessionEndpoints.cs`**
  **What**: Add after `ForkSessionApiRequest` (line 281):
  ```csharp
  internal sealed record SendCommandApiRequest(
      string Command,
      string? Arguments,
      string? Agent,
      string? Model);
  ```
  **Files**: `src/WeaveFleet.Api/Endpoints/SessionEndpoints.cs`
  **Acceptance**: Record compiles, follows existing naming pattern

- [ ] 10. **Replace 501 stub with real implementation in `SessionEndpoints.cs`**
  **What**: Replace the stub at lines 167-170 with:
  ```csharp
  group.MapPost("/{id}/command", async (string id, SendCommandApiRequest req, SessionOrchestrator orchestrator) =>
  {
      var options = new CommandOptions
      {
          Command = req.Command,
          Arguments = req.Arguments,
          Agent = req.Agent,
          ModelId = req.Model,
      };
      var result = await orchestrator.CommandSessionAsync(id, options);
      return result.Match(_ => Results.Accepted(), err => err.ToSessionApiResult());
  })
  .WithName("SendSessionCommand");
  ```
  Returns **202 Accepted** (fire-and-forget; results stream via SSE).
  **Files**: `src/WeaveFleet.Api/Endpoints/SessionEndpoints.cs`
  **Acceptance**: Endpoint returns 202 for valid requests, proper error codes for invalid ones

- [ ] 11. **Add `POST /api/instances/{id}/command` to `InstanceEndpoints.cs`**
  **What**: Add after the existing `/commands` GET endpoint (line 52), a direct instance-level command endpoint:
  ```csharp
  group.MapPost("/command", async (string id, SendCommandApiRequest req, InstanceTracker tracker, CancellationToken ct) =>
  {
      var instance = tracker.Get(id);
      if (instance is null)
          return Results.NotFound(new { error = $"Instance '{id}' not found or not running." });

      var options = new CommandOptions
      {
          Command = req.Command,
          Arguments = req.Arguments,
          Agent = req.Agent,
          ModelId = req.Model,
      };
      await instance.SendCommandAsync(options, ct);
      return Results.Accepted();
  })
  .WithName("SendInstanceCommand");
  ```
  This needs `using WeaveFleet.Domain.Harnesses;` at the top of the file (for `CommandOptions`). The `SendCommandApiRequest` record is in `SessionEndpoints.cs` — either reference it there (same namespace `WeaveFleet.Api.Endpoints`; both are `internal`) or extract to a shared location. Since both files are in the same namespace and assembly, the `internal` record is visible.
  **Files**: `src/WeaveFleet.Api/Endpoints/InstanceEndpoints.cs`
  **Acceptance**: Endpoint returns 202 for valid requests, 404 for unknown instances

## Implementation Order

1. **TODO 1** (CommandOptions record) — no dependencies
2. **TODO 2** (IHarnessInstance.SendCommandAsync) — depends on TODO 1
3. **TODOs 3-7b** (all implementations) — depend on TODO 2; can be done in parallel
4. **TODO 8** (SessionOrchestrator) — depends on TODO 2
5. **TODOs 9-11** (API endpoints) — depend on TODO 8

In practice, TODOs 1-2 should be done first, then everything else can be done in a single pass since the compiler will enforce correctness.

## Verification
- [ ] `dotnet build` succeeds across the entire solution
- [ ] `dotnet test` passes — all existing tests continue to work
- [ ] The `TestHarnessInstance` compiles (proves interface contract satisfied)
- [ ] Manual verification: `POST /api/sessions/{id}/command` with `{"command":"compact"}` returns 202 (with a running OpenCode session)
- [ ] Manual verification: `POST /api/instances/{id}/command` with `{"command":"compact"}` returns 202
- [ ] No regressions in existing prompt, abort, or event streaming flows

## Potential Pitfalls
1. **OpenCode command endpoint URL**: Verify the actual OpenCode endpoint is `/session/:id/command` (not `/command`). The context states it is session-scoped.
2. **Fire-and-forget semantics**: The OpenCode command endpoint may return a response body (`{ info, parts }`), but we intentionally ignore it — results arrive via SSE. If OpenCode returns 4xx for invalid commands, `EnsureSuccessStatusCode()` will throw, which the orchestrator's caller handles.
3. **Claude Code formatting**: Commands formatted as `/{command} {arguments}` assume Claude Code passes them through to the agent as-is. This is the standard convention; if Claude Code strips the leading slash, adjust the format.
4. **Shared request record**: `SendCommandApiRequest` is defined in `SessionEndpoints.cs` but used in `InstanceEndpoints.cs`. Both are `internal` in the same assembly/namespace — this works. If it causes issues, extract to a shared file or define a duplicate.
