# NuCode In-Process Harness Import

## TL;DR
> **Summary**: Import NuCode source into WeaveFleet and create a `NuCodeHarness` implementing `IHarness`/`IHarnessRuntime`/`IHarnessSession`, making NuCode an in-process alternative to spawning opencode as a child process.
> **Estimated Effort**: Large

## Context
### Original Request
Import NuCode as an in-process harness into WeaveFleet so it can run AI agent sessions without spawning external processes.

### Key Findings
- **Harness pattern is well-established**: `IHarness` (descriptor singleton), `IHarnessRuntime` (spawn/resume factory), `IHarnessSession` (running session). Registered via `IEnumerable<IHarness>` + `IEnumerable<IHarnessRuntime>` in `DependencyInjection.cs`.
- **OpenCode harness** is the reference implementation (~1200 LOC across 11 files). It spawns a child process, communicates via HTTP/SSE, and maps events through `OpenCodeMapper`.
- **NuCode is DI-first**: `services.AddNuCode(options => { ... })` registers all services. Key entrypoints: `ISessionService` (session lifecycle), `ISessionProcessor` (agent loop), `INuCodeEventBus` (events), `INuCodeAgentFactory` (agent creation).
- **NuCode needs `IChatClient`** — consumer-provided. WeaveFleet resolves credentials in `PrepareRuntimeAsync`, so the harness must create `IChatClient` from resolved API keys.
- **Event type alignment is strong**: NuCode events (`session.created`, `message.updated`, `message.part.updated`, `message.part.delta`) use the same string types as WeaveFleet's `EventTypes`. The bridge is mostly structural.
- **NuCode `MessagePart` types** map closely to WeaveFleet's `MessagePart` types but are different classes (different namespaces, different shapes). A `NuCodeMapper` is needed.
- **NuCode `TaskTool`** creates child sessions via `ISessionService.CreateChildSessionAsync`. It runs the agent loop inline (blocking). For delegation tracking, the harness must detect child session creation events.
- **NuCode packages** not in WeaveFleet's `Directory.Packages.props`: `Microsoft.Agents.AI` (1.0.0-rc4), `Microsoft.Agents.AI.Abstractions`, `Microsoft.Extensions.AI` (10.4.1), `Microsoft.Extensions.AI.Abstractions`, `Microsoft.Extensions.Options`, `Microsoft.Extensions.FileSystemGlobbing`, `Ulid`, `ReverseMarkdown`, `ModelContextProtocol`.
- **NuCode `QuestionTool`** blocks on `IQuestionService.AskAsync`. In orchestrator mode, must auto-deny or disable.
- **NuCode `TaskTool`** is not registered in `NuCodeServiceCollectionExtensions.RegisterBuiltInTools` — it requires scoped services (`ISessionProcessor`, `ICompactionService`, `IChatClient`). It's created differently.

## Objectives
### Core Objective
Make NuCode available as an in-process harness type (`"nucode"`) alongside opencode and claude-code.

### Deliverables
- [ ] NuCode source code imported into `src/NuCode/` within the WeaveFleet solution
- [ ] `NuCodeHarness` — descriptor implementing `IHarness`
- [ ] `NuCodeHarnessRuntime` — implementing `IHarnessRuntime` with credential → `IChatClient` resolution
- [ ] `NuCodeHarnessSession` — implementing `IHarnessSession` with event bridging and message mapping
- [ ] `NuCodeMapper` — maps NuCode events/messages to WeaveFleet domain types
- [ ] DI registration in `DependencyInjection.cs`
- [ ] LLM provider packages for `IChatClient` creation (Anthropic, OpenAI)

### Definition of Done
- [ ] `dotnet build` succeeds with NuCode project in solution
- [ ] NuCode harness appears in `GET /api/harnesses` with `available: true`
- [ ] A session can be spawned with harness type `"nucode"`, send a prompt, and receive streamed events
- [ ] Message history loads correctly in the UI
- [ ] Delegation (TaskTool child sessions) creates WeaveFleet `Delegation` entities

### Guardrails (Must NOT)
- Must NOT break existing OpenCode or ClaudeCode harness functionality
- Must NOT modify NuCode's public API surface (keep it importable)
- Must NOT introduce circular project references
- Must NOT hardcode LLM credentials — must flow through `PrepareRuntimeAsync`

## TODOs

### Phase 1: Import & Compile

- [x] 1. Add NuCode package versions to central package management
  **What**: Add all NuCode dependencies not already in `Directory.Packages.props`: `Microsoft.Agents.AI`, `Microsoft.Agents.AI.Abstractions`, `Microsoft.Extensions.AI`, `Microsoft.Extensions.AI.Abstractions`, `Microsoft.Extensions.Options`, `Microsoft.Extensions.FileSystemGlobbing`, `Ulid`, `ReverseMarkdown`, `ModelContextProtocol`. Also add LLM provider packages: `Microsoft.Extensions.AI.OpenAI`, `Anthropic` (or `Anthropic.SDK`).
  **Files**: `Directory.Packages.props`
  **Acceptance**: File contains all required `<PackageVersion>` entries with compatible versions

- [x] 2. Copy NuCode source into the solution
  **What**: Copy `D:\repos\damianh\NuCode\src\NuCode\` to `src/NuCode/` in the WeaveFleet repo. Update `NuCode.csproj` to remove `MinVer` package reference (not needed in-tree) and ensure all `<PackageReference>` elements omit `Version` attributes (central package management). Add `<TargetFramework>net10.0</TargetFramework>` if not already present.
  **Files**: `src/NuCode/` (entire directory), `src/NuCode/NuCode.csproj`
  **Acceptance**: `dotnet build src/NuCode/NuCode.csproj` succeeds

- [x] 3. Add NuCode project to the solution
  **What**: Add NuCode project to `WeaveFleet.slnx` under the `/src/` folder.
  **Files**: `WeaveFleet.slnx`
  **Acceptance**: `dotnet build WeaveFleet.slnx` succeeds

### Phase 2: Harness Shell

- [x] 4. Create `NuCodeHarness` descriptor
  **What**: Create `NuCodeHarness : IHarness` in the Infrastructure project's `Harnesses/NuCode/` folder. Type: `"nucode"`, DisplayName: `"NuCode"`. Capabilities: `RequiresInitialPrompt = true` (NuCode needs a prompt to start the agent loop), `SupportsAgents = true`, `SupportsModelSelection = true`, `SupportsCommands = false`, `SupportsForking = false`, `SupportsResume = true`, `SupportsImageAttachments = false` (for now), `SupportsStreaming = true`, `SupportsDelegation = true`.
  **Files**: `src/WeaveFleet.Infrastructure/Harnesses/NuCode/NuCodeHarness.cs`
  **Acceptance**: Class compiles and implements `IHarness`

- [x] 5. Create `NuCodeHarnessRuntime` with availability check and credential resolution
  **What**: Create `NuCodeHarnessRuntime : IHarnessRuntime`. `CheckAvailabilityAsync` always returns `Available = true` (in-process, no binary needed). `PrepareRuntimeAsync` resolves credentials same as OpenCode (reuse the same `provider/model` → credential namespace mapping). Create `NuCodeLaunchArtifacts : RuntimeLaunchArtifacts` to carry the resolved API key + provider info. `SpawnAsync` creates a scoped DI container with NuCode services, injects the `IChatClient` built from the API key, and returns a `NuCodeHarnessSession`. `ResumeAsync` loads an existing NuCode session by ID and resumes.
  **Files**: `src/WeaveFleet.Infrastructure/Harnesses/NuCode/NuCodeHarnessRuntime.cs`, `src/WeaveFleet.Infrastructure/Harnesses/NuCode/NuCodeLaunchArtifacts.cs`
  **Acceptance**: Class compiles, `SpawnAsync` creates a session that transitions to `Idle` status

- [x] 6. Create `NuCodeHarnessSession` shell
  **What**: Create `NuCodeHarnessSession : IHarnessSession`. Constructor takes the NuCode DI scope, `ISessionService`, `INuCodeEventBus`, and session metadata. Implement `SendPromptAsync` to create a user message + invoke the agent loop via `ISessionProcessor`. Implement `StopAsync`/`DisposeAsync` to dispose the DI scope. Implement `AbortAsync` with a `CancellationTokenSource`. Stub `GetMessagesAsync`, `SubscribeAsync`, `GetAgentsAsync`, `GetCommandsAsync`, `GetProvidersAsync`. `ProcessId` returns `null` (in-process). `HarnessType` returns `"nucode"`.
  **Files**: `src/WeaveFleet.Infrastructure/Harnesses/NuCode/NuCodeHarnessSession.cs`
  **Acceptance**: A session can be spawned and `SendPromptAsync` triggers the NuCode agent loop

- [x] 7. Create `IChatClientFactory` for building `IChatClient` from credentials
  **What**: Create a small factory/helper that takes a provider ID (anthropic/openai) and API key, and returns a configured `IChatClient`. For Anthropic: use the Anthropic SDK's `IChatClient` implementation. For OpenAI: use `Microsoft.Extensions.AI.OpenAI`. This isolates LLM SDK dependencies.
  **Files**: `src/WeaveFleet.Infrastructure/Harnesses/NuCode/ChatClientFactory.cs`
  **Acceptance**: Factory creates working `IChatClient` for both providers

- [x] 8. Add project reference and DI registration
  **What**: Add `<ProjectReference>` from `WeaveFleet.Infrastructure` to `NuCode`. Register `NuCodeHarness` and `NuCodeHarnessRuntime` as singletons in `DependencyInjection.cs`, following the OpenCode pattern.
  **Files**: `src/WeaveFleet.Infrastructure/WeaveFleet.Infrastructure.csproj`, `src/WeaveFleet.Infrastructure/DependencyInjection.cs`
  **Acceptance**: NuCode harness appears in `GET /api/harnesses` response

### Phase 3: Event Bridge

- [x] 9. Create `NuCodeMapper` for event mapping
  **What**: Create `NuCodeMapper` static class. Map `NuCodeEvent` → `HarnessEvent`: serialize the event properties to `JsonElement` for the `Payload` field. Map event types 1:1 (they already use the same string constants). Handle `message.part.delta` events for streaming text to the UI. Handle `session.created`/`session.updated` for status changes.
  **Files**: `src/WeaveFleet.Infrastructure/Harnesses/NuCode/NuCodeMapper.cs`
  **Acceptance**: All NuCode event types produce valid `HarnessEvent` instances

- [x] 10. Implement `SubscribeAsync` with event bus bridge
  **What**: In `NuCodeHarnessSession.SubscribeAsync`, subscribe to the session-scoped `INuCodeEventBus` using `SubscribeAll`. Use a `Channel<HarnessEvent>` to bridge the synchronous callback to the `IAsyncEnumerable<HarnessEvent>` return type. Map each `NuCodeEvent` through `NuCodeMapper.ToHarnessEvent`. Emit `session.idle` when the agent loop completes (agent returns `ProcessResult.Stop`).
  **Files**: `src/WeaveFleet.Infrastructure/Harnesses/NuCode/NuCodeHarnessSession.cs`
  **Acceptance**: Real-time events stream to the UI during prompt processing

### Phase 4: Message History

- [x] 11. Implement message mapping in `NuCodeMapper`
  **What**: Add `ToHarnessMessage` and `ToHarnessMessages` methods to `NuCodeMapper`. Map NuCode `MessageWithParts` → `HarnessMessage`. Map part types: `NuCode.Sessions.TextPart` → `WeaveFleet.Domain.Harnesses.TextPart`, `ToolPart` → `ToolUsePart` (map `ToolCallState` to `ToolUseState`), `ReasoningPart` → `ReasoningPart`, `FilePart` → `FilePart`, `StepFinishPart` → `StepFinishPart` (with token/cost data). Map `UserMessage` role → `"user"`, `AssistantMessage` → `"assistant"`.
  **Files**: `src/WeaveFleet.Infrastructure/Harnesses/NuCode/NuCodeMapper.cs`
  **Acceptance**: `GetMessagesAsync` returns correctly mapped messages with all part types

- [x] 12. Implement `GetMessagesAsync`
  **What**: In `NuCodeHarnessSession`, call `ISessionService.GetMessagesAsync` with the NuCode session ID, then map through `NuCodeMapper.ToHarnessMessages`. Support `MessageQuery.Limit` and `Before` parameters.
  **Files**: `src/WeaveFleet.Infrastructure/Harnesses/NuCode/NuCodeHarnessSession.cs`
  **Acceptance**: Conversation history displays correctly in the UI

- [x] 13. Implement `GetAgentsAsync` and `GetProvidersAsync`
  **What**: `GetAgentsAsync` returns NuCode's built-in agent profiles mapped to `AgentInfo` (build, plan, explore, general — filter out hidden agents like compaction/title/summary). `GetProvidersAsync` returns the provider/model that was configured for this session. `GetCommandsAsync` returns empty list (NuCode doesn't have slash commands).
  **Files**: `src/WeaveFleet.Infrastructure/Harnesses/NuCode/NuCodeHarnessSession.cs`
  **Acceptance**: Agent selector and provider selector work in the UI

### Phase 5: Credential & Model Resolution

- [x] 14. Wire `PrepareRuntimeAsync` credential flow end-to-end
  **What**: Ensure the full flow works: user selects model → `PrepareRuntimeAsync` resolves API key → `NuCodeLaunchArtifacts` carries key + provider + model → `SpawnAsync` uses `ChatClientFactory` to build `IChatClient` with the correct model → NuCode agent uses it. Support model override per-prompt via `PromptOptions.ModelId` (requires creating a new `IChatClient` or reconfiguring).
  **Files**: `src/WeaveFleet.Infrastructure/Harnesses/NuCode/NuCodeHarnessRuntime.cs`, `src/WeaveFleet.Infrastructure/Harnesses/NuCode/NuCodeHarnessSession.cs`
  **Acceptance**: Sessions work with both Anthropic and OpenAI API keys from user credentials

- [x] 15. Handle `QuestionTool` in orchestrator context
  **What**: Configure NuCode's `IQuestionService` to auto-deny all questions when running as a harness. Either provide a custom `IQuestionService` implementation that immediately returns a denial, or configure permission rules to deny the `question` tool. Prefer the permission rules approach for consistency.
  **Files**: `src/WeaveFleet.Infrastructure/Harnesses/NuCode/NuCodeHarnessRuntime.cs` (or a new `DenyAllQuestionService.cs`)
  **Acceptance**: Agent doesn't block on questions; receives a denial response and continues

### Phase 6: Sub-agent Delegation

- [x] 16. Bridge NuCode child sessions to WeaveFleet delegations
  **What**: Subscribe to `SessionEvents.Created` on the `INuCodeEventBus`. When a child session is created (has a `parentId`), call `SessionOrchestrator.EnsureDelegatedChildSessionAsync` and `DelegationService.HandleDelegationDetectedAsync` — same pattern as `OpenCodeHarnessSession.TryEmitDelegationAsync`. Detect delegation completion by subscribing to tool completion events for `task` tool calls.
  **Files**: `src/WeaveFleet.Infrastructure/Harnesses/NuCode/NuCodeHarnessSession.cs`
  **Acceptance**: Child sessions from `TaskTool` appear as delegations in the WeaveFleet UI

- [x] 17. Route child session events to child fleet sessions
  **What**: In `SubscribeAsync`, detect events with a session ID different from the parent NuCode session. Look up the child fleet session ID (same as `OpenCodeHarnessSession.TryResolveChildFleetSessionIdAsync`). Set `FleetSessionId` on the `HarnessEvent` so `HarnessEventRelay` routes events to the correct child session.
  **Files**: `src/WeaveFleet.Infrastructure/Harnesses/NuCode/NuCodeHarnessSession.cs`
  **Acceptance**: Child session events stream correctly to their own fleet sessions

### Phase 7: Analytics & Token Tracking

- [x] 18. Extract token/cost data from NuCode events
  **What**: Map NuCode's `StepFinishPart` (which contains `TokenUsage` and `Cost`) to `TokenEventData` for the analytics pipeline. Subscribe to `message.part.updated` events for `step-finish` parts and emit `TokenEventData` via `IAnalyticsCollector.AcceptTokenEvent`.
  **Files**: `src/WeaveFleet.Infrastructure/Harnesses/NuCode/NuCodeHarnessSession.cs`, `src/WeaveFleet.Infrastructure/Harnesses/NuCode/NuCodeMapper.cs`
  **Acceptance**: Token usage and costs appear in the analytics dashboard for NuCode sessions

## Verification
- [ ] `dotnet build WeaveFleet.slnx` succeeds with no warnings from NuCode integration
- [ ] All existing tests pass (`dotnet test`)
- [ ] NuCode harness listed as available in harness registry endpoint
- [ ] Can create a session, send a prompt, and see streaming response
- [ ] Message history loads with correct parts (text, tool, reasoning)
- [ ] Delegation tracking works for TaskTool child sessions
- [ ] No regressions in OpenCode or ClaudeCode harness functionality
