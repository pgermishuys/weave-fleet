# Harness Conformance Tests (NuCode + OpenCode)

## TL;DR
> **Summary**: Build a shared conformance test suite with abstract test classes that validate `IHarnessSession` against both `NuCodeHarnessSession` (via ScriptedChatClient) and `OpenCodeHarnessSession` (via FakeLlmServer), with NuCode-specific gap tests for known missing features.
> **Estimated Effort**: Large

## Context
### Original Request
Create a conformance test suite that validates both NuCode and OpenCode `IHarnessSession` implementations against the same contract, using abstract test classes with two concrete fixtures. NuCode gaps are surfaced as failing tests.

### Key Findings
- `NuCodeHarnessSession` is in-process: built via `AddNuCode()` DI, takes `IChatClient` — can be faked with `ScriptedChatClient`
- `OpenCodeHarnessSession` is out-of-process: spawns an `opencode` binary, communicates via HTTP/SSE — needs a `FakeLlmServer` to control LLM responses
- `OpenCodeHarnessRuntime.SpawnAsync` sets env vars (`ANTHROPIC_API_KEY`) and starts the process with Basic Auth on a random port
- `IHarnessSession` has 15 methods/properties: InstanceId, ProcessId, ResumeToken, HarnessType, Status, StopAsync, DeleteAsync, SendPromptAsync, SendCommandAsync, AbortAsync, AnswerQuestionAsync, RejectQuestionAsync, GetMessagesAsync, SubscribeAsync, CheckHealthAsync, GetAgentsAsync, GetCommandsAsync, GetProvidersAsync
- Known NuCode gaps: `SendCommandAsync` no-op, `GetCommandsAsync` returns `[]`, `AnswerQuestionAsync`/`RejectQuestionAsync` throw `NotSupportedException`, `PromptOptions.ProviderId`/`ModelId` ignored, no session.created/session.updated events, no forking
- `NuCodeMapper` maps only text, reasoning, tool, file, step-finish parts — others return `null`
- Existing `TestScenario`/`TestScenarioBuilder` in `WeaveFleet.TestHarness` shows the queue-based scripted response pattern
- `NuCode.Tests.MockLspServer` shows the repo pattern for fake servers (standalone process, minimal)
- Central package management via `Directory.Packages.props` — xUnit v3 (`xunit.v3.core.mtp-v2` 3.2.2), Shouldly 4.2.1

## Objectives
### Core Objective
Verify every `IHarnessSession` method behaves correctly on both NuCode and OpenCode, with NuCode gaps explicitly tested and marked as failing.

### Deliverables
- [ ] New project `tests/FakeLlmServer/` — fake Anthropic API server
- [ ] New project `tests/NuCode.ConformanceTests/` — shared abstract tests + NuCode/OpenCode concrete fixtures
- [ ] Abstract conformance test base class covering all conformant `IHarnessSession` surface
- [ ] NuCode fixture with `ScriptedChatClient`
- [ ] OpenCode fixture with `FakeLlmServer` + opencode process
- [ ] NuCode-specific gap tests for known missing features
- [ ] Solution file updated to include both new projects

### Definition of Done
- [ ] `dotnet test tests/NuCode.ConformanceTests/ --filter "Category=NuCode&Gap!=model-selection&Gap!=question-tool&Gap!=commands&Gap!=forking&Gap!=advanced-parts&Gap!=session-events"` — all pass
- [ ] `dotnet test tests/NuCode.ConformanceTests/ --filter "Category=OpenCode"` — all shared conformance tests pass
- [ ] `dotnet test tests/NuCode.ConformanceTests/ --filter "Gap=model-selection|Gap=question-tool|Gap=commands|Gap=forking|Gap=advanced-parts|Gap=session-events"` — all fail (expected)

### Guardrails (Must NOT)
- Must NOT test through the Fleet API layer — test `IHarnessSession` directly
- Must NOT require real API keys or network access (FakeLlmServer is in-process)
- Must NOT duplicate NuCode internal unit tests (those exist in `NuCode.Tests`)
- Must NOT use xUnit v2 — use `xunit.v3.core.mtp-v2`
- Must NOT put gap tests in the shared abstract base — gaps are NuCode-specific

## TODOs

### Phase 1: FakeLlmServer + Scaffolding

- [x] 1. **FakeLlmServer project scaffolding**
  **What**: Create the `FakeLlmServer` project — a minimal ASP.NET Core `WebApplication` that fakes the Anthropic Messages API with SSE streaming responses. Queue-based: each `POST /v1/messages` dequeues the next scripted response. Control endpoint `POST /_control/enqueue` to add responses via HTTP.
  **Files**: `tests/FakeLlmServer/FakeLlmServer.csproj`, `tests/FakeLlmServer/ScriptedResponseQueue.cs`, `tests/FakeLlmServer/AnthropicEndpoints.cs`, `tests/FakeLlmServer/FakeLlmServerFixture.cs`, `tests/FakeLlmServer/Program.cs`, `WeaveFleet.slnx`
  **Acceptance**: `dotnet build tests/FakeLlmServer/` succeeds

  `FakeLlmServer.csproj`:
  - Target `net10.0`, `Exe` output type
  - Reference `Microsoft.AspNetCore.App` framework

  `ScriptedResponseQueue.cs`:
  - Thread-safe `ConcurrentQueue<ScriptedLlmResponse>` wrapper
  - `ScriptedLlmResponse` record: `string Text`, `List<ToolCall>? ToolCalls`, `int InputTokens`, `int OutputTokens`
  - `ToolCall` record: `string Id`, `string Name`, `string InputJson`
  - `Enqueue(ScriptedLlmResponse)` and `TryDequeue(out ScriptedLlmResponse)` methods

  `AnthropicEndpoints.cs`:
  - `MapAnthropicEndpoints(this WebApplication app)` extension
  - `POST /v1/messages` — dequeues next response, streams SSE events:
    - `event: message_start` with `message` object (id, type, role, model, usage)
    - `event: content_block_start` for each content block (text or tool_use)
    - `event: content_block_delta` with text deltas (chunk the text into ~20 char pieces)
    - `event: content_block_stop`
    - `event: message_delta` with `stop_reason` and `usage`
    - `event: message_stop`
  - Returns 500 if queue is empty (no scripted response available)
  - `POST /_control/enqueue` — accepts `ScriptedLlmResponse` JSON, adds to queue

  `FakeLlmServerFixture.cs`:
  - Starts `WebApplication` in-process on `http://127.0.0.1:0` (random port)
  - Exposes `Uri BaseUrl` property
  - Exposes `ScriptedResponseQueue Queue` for direct enqueue (no HTTP needed from test code)
  - `IAsyncDisposable` — stops the server

  `Program.cs`:
  - Minimal entry point for standalone process mode (not used in tests but keeps it runnable)

- [x] 2. **Conformance test project scaffolding**
  **What**: Create the test project with folder structure, csproj, global usings, and fixture interface
  **Files**: `tests/NuCode.ConformanceTests/NuCode.ConformanceTests.csproj`, `tests/NuCode.ConformanceTests/GlobalUsings.cs`, `tests/NuCode.ConformanceTests/Abstractions/IHarnessSessionFixture.cs`, `WeaveFleet.slnx`
  **Acceptance**: `dotnet build tests/NuCode.ConformanceTests/` succeeds

  `NuCode.ConformanceTests.csproj`:
  - References: `WeaveFleet.Infrastructure`, `NuCode`, `FakeLlmServer`
  - Packages: `xunit.v3.core.mtp-v2`, `Shouldly`, `Microsoft.Extensions.DependencyInjection`, `Microsoft.NET.Test.Sdk`

  `IHarnessSessionFixture.cs`:
  ```csharp
  public interface IHarnessSessionFixture : IAsyncDisposable
  {
      Task<IHarnessSession> CreateSessionAsync(string workingDirectory);
      void EnqueueResponse(ScriptedLlmResponse response);
  }
  ```
  This abstraction lets the same test code work against both harnesses — the test says "when the LLM returns X, the harness should expose Y".

### Phase 2: NuCode Fixture + ScriptedChatClient

- [x] 3. **ScriptedChatClient**
  **What**: Fake `IChatClient` that returns scripted responses from a queue. Supports streaming via `GetStreamingResponseAsync`. Translates `ScriptedLlmResponse` (text, tool calls) into `ChatResponse`/`StreamingChatCompletionUpdate` objects.
  **Files**: `tests/NuCode.ConformanceTests/Fakes/ScriptedChatClient.cs`
  **Acceptance**: Unit-level: can enqueue a text response and get it back via `GetStreamingResponseAsync`

  Key design:
  - Wraps a `ConcurrentQueue<ScriptedLlmResponse>`
  - `GetStreamingResponseAsync` dequeues next response, yields `StreamingChatCompletionUpdate` items (text deltas, tool call chunks)
  - `GetResponseAsync` dequeues and returns complete `ChatResponse`
  - Throws `InvalidOperationException` if queue is empty

- [x] 4. **NuCode fixture**
  **What**: `NuCodeFixture` implements `IHarnessSessionFixture`. Mirrors `NuCodeHarnessRuntime.SpawnAsync` — builds `ServiceCollection` with `AddNuCode()`, registers `ScriptedChatClient`, builds `ServiceProvider`, constructs `NuCodeHarnessSession`.
  **Files**: `tests/NuCode.ConformanceTests/NuCode/NuCodeFixture.cs`
  **Acceptance**: Can create a `NuCodeHarnessSession` that processes a scripted prompt and returns to idle

  Implementation:
  - `CreateSessionAsync(workingDirectory)`:
    - `new ServiceCollection().AddNuCode(o => o.WorkingDirectory = workingDirectory)`
    - Register `ScriptedChatClient` as `IChatClient`
    - Register `DenyAllQuestionService` as `IQuestionService`
    - Mock `IServiceScopeFactory` (NSubstitute or manual stub — return no-op scope)
    - Build provider, construct `NuCodeHarnessSession` with test values
  - `EnqueueResponse(ScriptedLlmResponse)` — delegates to `ScriptedChatClient.Enqueue()`
  - `DisposeAsync` — disposes the service provider

### Phase 3: OpenCode Fixture

- [x] 5. **OpenCode fixture**
  **What**: `OpenCodeFixture` implements `IHarnessSessionFixture`. Starts `FakeLlmServerFixture`, configures opencode to use it as the Anthropic base URL, spawns the opencode process, constructs `OpenCodeHarnessSession`.
  **Files**: `tests/NuCode.ConformanceTests/OpenCode/OpenCodeFixture.cs`
  **Acceptance**: Can create an `OpenCodeHarnessSession` that processes a scripted prompt via FakeLlmServer and returns to idle

  Implementation:
  - `CreateSessionAsync(workingDirectory)`:
    - Start `FakeLlmServerFixture` (in-process, random port)
    - Set env vars: `ANTHROPIC_API_KEY=fake-key`, `ANTHROPIC_BASE_URL=http://127.0.0.1:{port}`
    - Use `OpenCodeProcessManager` to start opencode process (same pattern as `OpenCodeHarnessRuntime.SpawnAsync`)
    - Create `OpenCodeHttpClient` with Basic Auth
    - Health-check with retries
    - Construct `OpenCodeHarnessSession`
  - `EnqueueResponse(ScriptedLlmResponse)` — delegates to `FakeLlmServerFixture.Queue.Enqueue()`
  - `DisposeAsync` — dispose session, stop process, stop FakeLlmServer

  Note: This fixture requires `opencode` binary on PATH. Tests using this fixture should be skipped (via `Skip` attribute or custom fact) if opencode is not available.

### Phase 4: Shared Conformance Tests (Abstract Base)

- [x] 6. **Abstract conformance base class**
  **What**: `HarnessConformanceBase<TFixture>` — abstract xUnit test class parameterized by fixture type. Contains all shared conformance test methods. Concrete classes (`NuCodeConformanceTests`, `OpenCodeConformanceTests`) inherit and provide the fixture.
  **Files**: `tests/NuCode.ConformanceTests/Abstractions/HarnessConformanceBase.cs`
  **Acceptance**: Compiles; no tests run directly from this class

  Pattern:
  ```csharp
  public abstract class HarnessConformanceBase : IAsyncLifetime
  {
      protected abstract IHarnessSessionFixture CreateFixture();
      private IHarnessSessionFixture _fixture;
      private IHarnessSession _session;
      private string _workDir;

      public async ValueTask InitializeAsync()
      {
          _workDir = Path.Combine(Path.GetTempPath(), $"conformance-{Guid.NewGuid():N}");
          Directory.CreateDirectory(_workDir);
          _fixture = CreateFixture();
          _session = await _fixture.CreateSessionAsync(_workDir);
      }

      public async ValueTask DisposeAsync()
      {
          await _session.DisposeAsync();
          await _fixture.DisposeAsync();
          Directory.Delete(_workDir, true);
      }
  }
  ```

- [x] 7. **Core properties and health tests**
  **What**: Add test methods to `HarnessConformanceBase` for properties and health check
  **Files**: `tests/NuCode.ConformanceTests/Abstractions/HarnessConformanceBase.cs`
  **Acceptance**: Tests pass on both NuCode and OpenCode fixtures

  Test methods:
  - `InstanceId_IsNotEmpty`
  - `HarnessType_IsNotEmpty`
  - `Status_IsIdle_Initially`
  - `CheckHealthAsync_ReturnsHealthy`

- [x] 8. **SendPromptAsync tests**
  **What**: Add test methods for sending prompts, status transitions, message creation
  **Files**: `tests/NuCode.ConformanceTests/Abstractions/HarnessConformanceBase.cs`
  **Acceptance**: Tests pass on both fixtures

  Test methods:
  - `SendPromptAsync_TransitionsToRunning_ThenBackToIdle`
  - `SendPromptAsync_CreatesUserAndAssistantMessages`
  - `ResumeToken_IsPopulated_AfterFirstPrompt`

- [x] 9. **AbortAsync tests**
  **What**: Test abort cancels in-flight prompt
  **Files**: `tests/NuCode.ConformanceTests/Abstractions/HarnessConformanceBase.cs`
  **Acceptance**: Tests pass on both fixtures

  Test methods:
  - `AbortAsync_SetsStatusToIdle`

- [x] 10. **GetMessagesAsync tests**
  **What**: Test message retrieval and pagination
  **Files**: `tests/NuCode.ConformanceTests/Abstractions/HarnessConformanceBase.cs`
  **Acceptance**: Tests pass on both fixtures

  Test methods:
  - `GetMessagesAsync_ReturnsEmptyPage_BeforeAnyPrompt`
  - `GetMessagesAsync_ReturnsMessages_AfterPrompt`
  - `GetMessagesAsync_MessagesHaveCorrectRoles`

- [x] 11. **StopAsync and DeleteAsync tests**
  **What**: Test session lifecycle termination
  **Files**: `tests/NuCode.ConformanceTests/Abstractions/HarnessConformanceBase.cs`
  **Acceptance**: Tests pass on both fixtures

  Test methods:
  - `StopAsync_SetsStatusToStopped`
  - `DeleteAsync_SetsStatusToStopped`

- [x] 12. **Message parts tests**
  **What**: Test that text, tool use parts are correctly mapped
  **Files**: `tests/NuCode.ConformanceTests/Abstractions/HarnessConformanceBase.cs`
  **Acceptance**: Tests pass on both fixtures

  Test methods:
  - `TextPart_IsMappedCorrectly` — enqueue text response, verify TextPart in message
  - `ToolUsePart_IsMappedCorrectly` — enqueue tool call response, verify ToolPart in message

- [x] 13. **Event streaming tests**
  **What**: Test that `SubscribeAsync` emits expected events during prompt processing
  **Files**: `tests/NuCode.ConformanceTests/Abstractions/HarnessConformanceBase.cs`
  **Acceptance**: Tests pass on both fixtures

  Test methods:
  - `SubscribeAsync_EmitsSessionBusy_WhenPromptStarts`
  - `SubscribeAsync_EmitsSessionIdle_WhenPromptCompletes`
  - `SubscribeAsync_EmitsMessageCreated_ForUserMessage`
  - `SubscribeAsync_EmitsPartUpdated_ForParts`
  - `SubscribeAsync_EventsHaveCorrectSessionId`

- [x] 14. **Configuration tests**
  **What**: Test agent and provider listing
  **Files**: `tests/NuCode.ConformanceTests/Abstractions/HarnessConformanceBase.cs`
  **Acceptance**: Tests pass on both fixtures

  Test methods:
  - `GetAgentsAsync_ReturnsAtLeastOneAgent`
  - `GetProvidersAsync_ReturnsAtLeastOneProvider`

- [x] 15. **NuCode concrete test class**
  **What**: Inherits `HarnessConformanceBase`, provides `NuCodeFixture`
  **Files**: `tests/NuCode.ConformanceTests/NuCode/NuCodeConformanceTests.cs`
  **Acceptance**: All shared tests pass with NuCode fixture

- [x] 16. **OpenCode concrete test class**
  **What**: Inherits `HarnessConformanceBase`, provides `OpenCodeFixture`
  **Files**: `tests/NuCode.ConformanceTests/OpenCode/OpenCodeConformanceTests.cs`
  **Acceptance**: All shared tests pass with OpenCode fixture (when opencode binary available)

### Phase 5: NuCode Gap Tests

- [x] 17. **GAP: Model selection ignored**
  **What**: Test that `PromptOptions.ProviderId`/`ModelId` are respected (they aren't in NuCode)
  **Files**: `tests/NuCode.ConformanceTests/NuCode/Gaps/ModelSelectionGapTests.cs`
  **Acceptance**: Tests FAIL (marked `[Trait("Gap", "model-selection")]`)

  Test methods:
  - `SendPromptAsync_WithDifferentModelId_UsesRequestedModel`
  - `SendPromptAsync_WithDifferentProviderId_UsesRequestedProvider`

- [x] 18. **GAP: Question tool**
  **What**: Test that `AnswerQuestionAsync`/`RejectQuestionAsync` work (they throw in NuCode)
  **Files**: `tests/NuCode.ConformanceTests/NuCode/Gaps/QuestionToolGapTests.cs`
  **Acceptance**: Tests FAIL (marked `[Trait("Gap", "question-tool")]`)

  Test methods:
  - `AnswerQuestionAsync_DoesNotThrow`
  - `RejectQuestionAsync_DoesNotThrow`

- [x] 19. **GAP: Commands**
  **What**: Test that `GetCommandsAsync` returns commands and `SendCommandAsync` executes them
  **Files**: `tests/NuCode.ConformanceTests/NuCode/Gaps/CommandsGapTests.cs`
  **Acceptance**: Tests FAIL (marked `[Trait("Gap", "commands")]`)

  Test methods:
  - `GetCommandsAsync_ReturnsAvailableCommands`
  - `SendCommandAsync_ExecutesCommand`

- [x] 20. **GAP: Session forking**
  **What**: Test that session forking is supported
  **Files**: `tests/NuCode.ConformanceTests/NuCode/Gaps/ForkingGapTests.cs`
  **Acceptance**: Tests FAIL (marked `[Trait("Gap", "forking")]`)

  Test methods:
  - `ForkSession_CreatesIndependentCopy`

- [x] 21. **GAP: Advanced message parts**
  **What**: Test that agent, subtask, snapshot, patch parts are emitted (NuCode doesn't produce them)
  **Files**: `tests/NuCode.ConformanceTests/NuCode/Gaps/AdvancedPartGapTests.cs`
  **Acceptance**: Tests FAIL (marked `[Trait("Gap", "advanced-parts")]`)

  Test methods:
  - `AgentPart_IsEmitted_WhenDelegating`
  - `SubtaskPart_IsEmitted_ForChildSessions`
  - `PatchPart_IsEmitted_OnEdit`

- [x] 22. **~~GAP~~ FILLED: Session events**
  **What**: NuCode now emits `session.created` and `session.updated` events when a NuCode session is first created. Implemented in `NuCodeHarnessSession.SendPromptAsync` — emits both events after `CreateSessionAsync`. Added `NuCodeSessionUpdatedPayload` with `Title` field. Gap tests removed; tests promoted to shared conformance base.
  **Files**: `src/WeaveFleet.Infrastructure/Harnesses/NuCode/NuCodeHarnessSession.cs`, `src/WeaveFleet.Infrastructure/JsonContext.cs`, `tests/NuCode.ConformanceTests/Abstractions/HarnessConformanceBase.cs`
  **Acceptance**: Tests PASS as shared conformance tests (23 total)

### Phase 6: Delegation Tests

- [x] 23. **Delegation and child sessions**
  **What**: Test that task tool delegation creates child sessions and routes events correctly. Investigation revealed that `TaskTool` is **not registered** in `RegisterBuiltInTools`, so delegation cannot be triggered through the normal agent loop. Additionally, delegation tracking requires `DelegationService`/`SessionOrchestrator` which need Fleet DB access not available in test fixtures. Implemented as a **gap test** documenting these limitations.
  **Files**: `tests/NuCode.ConformanceTests/NuCode/Gaps/DelegationGapTests.cs`
  **Acceptance**: Tests FAIL (marked `[Trait("Gap", "delegation")]`)

  Test methods:
  - `TaskToolCall_CreatesDelegation`
  - `ChildSession_EventsRouteToCorrectFleetSession`

  Note: If too complex, add as NuCode-only test first, then extend to shared. Mark as stretch goal.

## Verification
- [x] `dotnet build tests/FakeLlmServer/` compiles without errors
- [x] `dotnet build tests/NuCode.ConformanceTests/` compiles without errors
- [x] Shared conformance tests pass on NuCode fixture (21/21)
- [ ] Shared conformance tests pass on OpenCode fixture (when opencode binary available)
- [x] Gap tests are clearly documented with `[Trait("Gap", "...")]` and expected failure reason (12/12 fail as expected)
- [x] No tests require real API keys or network access
- [x] Test projects follow repo conventions (xUnit v3, Shouldly, same Directory.Build.props)
