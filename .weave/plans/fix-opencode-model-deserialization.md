# Fix OpenCode Model Deserialization Crash

## TL;DR
> **Summary**: Remove `required` from `OpenCodeModelRef.ProviderId` and `ModelId` to prevent `JsonException` when OpenCode returns messages with missing/null model fields. Add a try/catch in the orchestrator to convert deserialization failures into proper error responses. Audit all other `required` properties on response DTOs for similar fragility.
> **Estimated Effort**: Short

## Context
### Original Request
When refreshing a browser tab on an active session, the user sees "Failed to load initial messages — scroll up to retry". The server throws:
```
System.Text.Json.JsonException: JSON deserialization for type 'OpenCodeModelRef'
was missing required properties including: 'providerId', 'modelId'.
```

### Key Findings
1. **`OpenCodeModelRef`** (line 134-138 of `OpenCodeModels.cs`) uses `required string` for both `ProviderId` and `ModelId`. When OpenCode sends a message where the `model` object has null/missing fields, `System.Text.Json` throws instead of deserializing.

2. **No downstream readers**: `OpenCodeMapper.ToHarnessMessage()` never accesses `.ProviderId` or `.ModelId` on any `OpenCodeModelRef` from deserialized responses. The mapper only reads `Info.Id`, `Info.Role`, `Info.Time`, and `Parts`. The only code that *writes* to `OpenCodeModelRef` is `OpenCodeHarnessInstance.SendPromptAsync()` (line 107-122), which is the outbound/request path and always provides values.

3. **`OpenCodeProvidersResponse.Default`** is typed `OpenCodeModelRef?` — this is another response-path deserialization of `OpenCodeModelRef` that would also crash if `default` is present but has null fields.

4. **`OpenCodeAgentInfo.Model`** is typed `OpenCodeModelRef?` — same risk on the `/agent` endpoint.

5. **No global exception handler**: `Program.cs` has no `UseExceptionHandler()` or `IExceptionHandler` middleware. Unhandled exceptions from minimal API handlers surface as raw 500 responses with stack traces.

6. **`GetSessionMessagesAsync`** in `SessionOrchestrator.cs` (line 235-246) has no try/catch around the harness call. The `JsonException` propagates as an unhandled 500.

7. **Frontend error path**: `use-message-pagination.ts` line 76 sets `loadError = "Failed to load initial messages"` when the response is not OK (HTTP error). But the current bug produces a 500 with an HTML stack trace, which `response.ok` correctly catches as `false`. The `loadError` string is then shown in `activity-stream-v1.tsx` line 718 as `"{loadOlderError} — scroll up to retry"`. The `loadInitialMessages` error path does NOT set `loadError` on the non-OK response — wait, it DOES (line 76). The `loadAllMessages` path (used on reconnect) does NOT set `loadError` but that's a separate path. The core issue is purely server-side.

8. **Other `required` properties on response DTOs**: The following `required` properties are on types deserialized from OpenCode responses (not request types we construct ourselves):
   - `OpenCodeSessionInfo.Id` — reasonable, sessions should always have IDs
   - `OpenCodeMessageWithParts.Info` — reasonable, messages must have info
   - `OpenCodeModelRef.ProviderId` — **BUG** (this ticket)
   - `OpenCodeModelRef.ModelId` — **BUG** (this ticket)
   - `OpenCodeAgentInfo.Name` — moderate risk (agents should have names, but external API)
   - `OpenCodeProviderInfo.Id` — moderate risk (providers should have IDs, but external API)
   - `OpenCodeProviderModel.Id` — moderate risk (models should have IDs, but external API)
   - `OpenCodeSseEvent.Type` — reasonable, events must have a type to dispatch

## Objectives
### Core Objective
Prevent deserialization crashes when OpenCode returns `model` objects with missing/null `providerId` or `modelId` fields.

### Deliverables
- [x] Fix `OpenCodeModelRef` to tolerate null fields
- [x] Add resilience in the orchestrator so deserialization errors return structured errors
- [x] Add tests for the null/missing model ref case
- [x] Audit and soften other fragile `required` properties on response DTOs

### Definition of Done
- [x] `dotnet test` passes (all projects)
- [ ] Manually verified: refreshing a browser tab on an active session loads messages (no error banner)
- [x] A JSON payload with `"model": {}` or `"model": {"providerId": null}` deserializes without throwing

### Guardrails (Must NOT)
- Do NOT remove `required` from request DTOs we construct ourselves (`OpenCodePromptRequest`, `OpenCodePromptTextPart`, etc.) — those are the write path and `required` catches programmer errors
- Do NOT change the `OpenCodeMapper` output shape (existing contract tests must still pass)
- Do NOT add null-forgiving operators (`!`) to paper over the issue

## TODOs

- [x] 1. **Make `OpenCodeModelRef` fields nullable and non-required**
  **What**: Change `ProviderId` and `ModelId` from `required string` to `string?` with defaults of `null`.
  **Files**: `src/WeaveFleet.Infrastructure/Harnesses/OpenCode/OpenCodeModels.cs` (lines 134-138)
  **Change**:
  ```csharp
  internal sealed record OpenCodeModelRef
  {
      [JsonPropertyName("providerId")] public string? ProviderId { get; init; }
      [JsonPropertyName("modelId")] public string? ModelId { get; init; }
  }
  ```
  **Acceptance**: `JsonSerializer.Deserialize<OpenCodeModelRef>("{}")` returns an instance with null fields instead of throwing.

- [x] 2. **Fix the `OpenCodeHarnessInstance` write path to remain explicit**
  **What**: Since `required` is removed from `OpenCodeModelRef`, the write path in `SendPromptAsync` (line 107-122) still compiles fine — it always provides both values. No code change needed, but verify compilation is clean with no warnings.
  **Files**: `src/WeaveFleet.Infrastructure/Harnesses/OpenCode/OpenCodeHarnessInstance.cs` (verify only)
  **Acceptance**: No new compiler warnings on `OpenCodeHarnessInstance.cs`.

- [x] 3. **Soften other fragile `required` properties on response-path DTOs**
  **What**: Change the following from `required string` to `string?` (or `string` with a default) since they come from an external API we don't control:
  - `OpenCodeAgentInfo.Name` → `string? Name` (line 411) — agents without a name should be tolerated
  - `OpenCodeProviderInfo.Id` → keep `required` (Id is critical for provider identity; a provider without an Id is useless and indicates a broken API)
  - `OpenCodeProviderModel.Id` → keep `required` (same reasoning)
  - `OpenCodeSseEvent.Type` → keep `required` (event type is the discriminator, without it the event is meaningless)
  - `OpenCodeSessionInfo.Id` → keep `required` (session without Id is useless)
  - `OpenCodeMessageWithParts.Info` → keep `required` (message without info is useless)
  **Files**: `src/WeaveFleet.Infrastructure/Harnesses/OpenCode/OpenCodeModels.cs` (line 411)
  **Acceptance**: Compiles cleanly. Existing tests pass.

- [x] 4. **Update `OpenCodeMapper.ToHarnessAgents` to handle nullable Name**
  **What**: If `OpenCodeAgentInfo.Name` becomes nullable, the mapper at line 112 (`new HarnessAgent(a.Name, ...)`) needs a null guard. Use `a.Name ?? string.Empty` or filter out agents with null names.
  **Files**: `src/WeaveFleet.Infrastructure/Harnesses/OpenCode/OpenCodeMapper.cs` (line 112)
  **Acceptance**: Mapper doesn't throw when an agent has a null name.

- [x] 5. **Add try/catch in `SessionOrchestrator.GetSessionMessagesAsync`**
  **What**: Wrap the `instanceResult.Value.GetMessagesAsync(query, ct)` call in a try/catch for `JsonException` (and more broadly `HttpRequestException`). On failure, return a `FleetError` with a descriptive message rather than letting the exception propagate as an unhandled 500.
  **Files**: `src/WeaveFleet.Application/Services/SessionOrchestrator.cs` (lines 235-246)
  **Change**:
  ```csharp
  public async Task<Result<MessagePage>> GetSessionMessagesAsync(
      string id, MessageQuery? query = null, CancellationToken ct = default)
  {
      var instanceResult = await GetLiveInstanceAsync(id);
      if (instanceResult.IsFailure)
          return instanceResult.Error;

      try
      {
          var page = await instanceResult.Value.GetMessagesAsync(query, ct);
          return Result.Success(page);
      }
      catch (Exception ex) when (ex is not OperationCanceledException)
      {
          LogGetMessagesFailed(ex, id);
          return FleetError.Unexpected;
      }
  }
  ```
  Also add a `LogGetMessagesFailed` partial method.
  **Acceptance**: A deserialization error from the harness returns a structured error result, not a 500 stack trace.

- [x] 6. **Add serialization test: `OpenCodeModelRef` with missing fields**
  **What**: Add a test in `OpenCodeModelsSerializationTests.cs` that deserializes an `OpenCodeModelRef` with missing/null `providerId` and `modelId` and asserts it succeeds.
  **Files**: `tests/WeaveFleet.Infrastructure.Tests/Harnesses/OpenCode/OpenCodeModelsSerializationTests.cs`
  **Tests to add**:
  - `ModelRef_Deserializes_WithMissingFields` — input `{}`, asserts `ProviderId` and `ModelId` are null
  - `ModelRef_Deserializes_WithNullFields` — input `{"providerId":null,"modelId":null}`, asserts both are null
  - `ModelRef_Deserializes_WithPartialFields` — input `{"providerId":"openai"}`, asserts `ProviderId` is "openai" and `ModelId` is null
  **Acceptance**: All three tests pass.

- [x] 7. **Add serialization test: `UserMessage` with empty model object**
  **What**: Add a test that deserializes a full `OpenCodeMessageWithParts` containing a user message where `"model": {}` — the exact payload shape that triggers the bug.
  **Files**: `tests/WeaveFleet.Infrastructure.Tests/Harnesses/OpenCode/OpenCodeModelsSerializationTests.cs`
  **Test**:
  ```
  UserMessage_WithEmptyModelObject_Deserializes
  ```
  Input JSON: a `MessageInfo` with `"role":"user"` and `"model":{}`. Assert deserialization succeeds and `Model.ProviderId` / `Model.ModelId` are null.
  **Acceptance**: Test passes — no `JsonException`.

- [x] 8. **Add contract test case for user message with empty model**
  **What**: Add a new case to `tests/contracts/opencode-to-fleet-messages.json` with a user message that has `"model": {}`. This ensures the full pipeline (deserialize → map → serialize) handles the case.
  **Files**: `tests/contracts/opencode-to-fleet-messages.json`
  **Acceptance**: `OpenCodeToFleetContractTests.All_Message_Cases_Match_Expected_Fleet_Shape` passes with the new case.

- [x] 9. **Add mapper test: message with null model ref fields maps correctly**
  **What**: Add a test in `OpenCodeMapperTests.cs` that maps a `OpenCodeMessageWithParts` with a `UserMessage` containing `Model = new OpenCodeModelRef()` (both fields null). The mapper should produce a valid `HarnessMessage` — it doesn't read model fields, so this just confirms no NPE.
  **Files**: `tests/WeaveFleet.Infrastructure.Tests/Harnesses/OpenCode/OpenCodeMapperTests.cs`
  **Acceptance**: Test passes.

## Verification
- [x] `dotnet build` compiles with no new warnings
- [x] `dotnet test` — all existing tests pass
- [x] New serialization tests pass (ModelRef with missing/null/partial fields)
- [x] New contract test case passes
- [x] New mapper test passes
- [ ] Manual smoke test: refresh browser tab on active session → messages load (no error banner)
