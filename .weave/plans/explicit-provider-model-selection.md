# Explicit Provider/Model Selection Contract

## TL;DR
> **Summary**: Stop hardcoding `openrouter` into model IDs at the API boundary. Carry `providerID` + `modelID` as separate fields through the full stack so the frontend round-trips exactly what OpenCode expects.
> **Estimated Effort**: Medium

## Context
### Original Request
Fix the model selection contract so provider and model are carried explicitly instead of baking `openrouter/` prefixes into composite model ID strings.

### Key Findings
1. **`InstanceEndpoints.cs:34-38`** — The `/models` endpoint synthesizes `openrouter/{provider}/{model}` composite IDs. This is the root of the smell.
2. **`use-models.ts:14-23`** — `toModelOptions()` flattens providers into `ModelOption[]` using `model.id` as the key, **discarding `provider.id`**. The `provider` field stores only the display name.
3. **`use-send-prompt.ts:166-170`** — `BackendSendPromptRequest` sends `model?: string` (a single composite string).
4. **`use-send-command.ts:9-14`** — Same: `model?: string`.
5. **`SessionEndpoints.cs:190`** — API receives `req.Model` as a single string, passes into `PromptOptions.ModelId`.
6. **`OpenCodeHarnessSession.cs:160-174`** — `SendPromptAsync` **re-splits** `ModelId` on `/` to reconstruct `{ providerID, modelID }` for the OpenCode prompt endpoint.
7. **`OpenCodeHarnessSession.cs:199`** — `SendCommandAsync` passes model as a plain `"provider/model"` string (OpenCode CommandInput schema requires this). So the command path legitimately needs a composed string.
8. **`HarnessTypes.cs:132-137`** — Domain `PromptOptions` has only `ModelId` (single string). `CommandOptions` also has only `ModelId`.
9. **`OpenCodeModels.cs:224-230`** — `OpenCodeModelRefRequest` already has `ProviderId` + `ModelId` — the correct shape for prompts.
10. **`AvailableProvider` TS type** already returns `{ id, name, models: { id, name }[] }` — the data is there, just discarded.

### Current Data Flow (Prompt Path)
```
API /models → { providers: [{ id: "openrouter", models: [{ id: "openrouter/anthropic/claude-opus-4.6" }] }] }
                                                              ↑ hardcoded prefix
  → Frontend use-models.ts → ModelOption { id: "openrouter/anthropic/claude-opus-4.6" }
                                            ↑ provider.id discarded
  → Frontend sends POST /prompt { model: "openrouter/anthropic/claude-opus-4.6" }
  → API → PromptOptions { ModelId: "openrouter/anthropic/claude-opus-4.6" }
  → OpenCodeHarnessSession splits on first "/" → { providerID: "openrouter", modelID: "anthropic/claude-opus-4.6" }
```

### Desired Data Flow
```
API /models → { providers: [{ id: "openrouter", models: [{ id: "anthropic/claude-opus-4.6" }] }] }
                                                              ↑ raw from harness
  → Frontend → ModelOption { id: "anthropic/claude-opus-4.6", providerId: "openrouter" }
  → Frontend sends POST /prompt { model: { providerID: "openrouter", modelID: "anthropic/claude-opus-4.6" } }
  → API → PromptOptions { ProviderId: "openrouter", ModelId: "anthropic/claude-opus-4.6" }
  → OpenCodeHarnessSession passes directly → { providerID: "openrouter", modelID: "anthropic/claude-opus-4.6" }
```

## Objectives
### Core Objective
Carry provider ID and model ID as separate values through the entire stack, eliminating the `openrouter` prefix hack and the split-on-slash reconstruction.

### Deliverables
- [x] API `/models` endpoint returns raw provider/model IDs (no prefix rewriting)
- [x] API prompt/command endpoints accept `model: { providerID, modelID }` (with backward compat for plain string)
- [x] Domain `PromptOptions`/`CommandOptions` carry separate `ProviderId` + `ModelId`
- [x] Frontend `ModelOption` carries `providerId` alongside `id`
- [x] Frontend sends structured `{ providerID, modelID }` in prompt/command requests
- [x] OpenCode harness session uses the separate fields directly (no split hack)

### Definition of Done
- [x] `dotnet test` passes for all test projects
- [x] `npm run build` in `client/` succeeds
- [x] `npm run test` in `client/` passes
- [x] No occurrence of `"openrouter/"` prefix construction in `InstanceEndpoints.cs`
- [x] No `IndexOf('/')` splitting in `OpenCodeHarnessSession.SendPromptAsync`

### Guardrails (Must NOT)
- Must NOT break ClaudeCode harness (which also implements `IHarnessSession`)
- Must NOT break the OpenCode command path (still needs composed `"provider/model"` at the harness boundary)
- Must NOT break existing sessions/analytics DB schema (those already store `provider_id` separately)

## TODOs

- [x] 1. **Add `ProviderId` to domain options**
  **What**: Add `ProviderId` property to `PromptOptions` and `CommandOptions`. The existing `ModelId` keeps its meaning (just the model part). Update `CommandOptions.Validate()` if needed.
  **Files**: `src/WeaveFleet.Domain/Harnesses/HarnessTypes.cs`
  **Acceptance**: Compiles; existing tests still pass.

- [x] 2. **Remove openrouter prefix from `/models` endpoint**
  **What**: In `InstanceEndpoints.cs`, stop prefixing model IDs with `openrouter/`. Return raw `p.Id` and `m.Id` from the harness. Remove the `prefixOpenCodeProvider` flag entirely — just return `{ id: p.Id, models: [{ id: m.Id }] }` for all harness types.
  **Files**: `src/WeaveFleet.Api/Endpoints/InstanceEndpoints.cs`
  **Acceptance**: The composed `openrouter/...` IDs no longer appear in the response.

- [x] 3. **Accept structured model in prompt/command API requests**
  **What**: Update `SendPromptApiRequest` and `SendCommandApiRequest` to accept `model` as either a string (backward compat) or `{ providerID, modelID }` object. Map into `PromptOptions`/`CommandOptions` with separate `ProviderId` + `ModelId`. Use a custom JSON converter or a union type approach.
  **Files**: `src/WeaveFleet.Api/Endpoints/SessionEndpoints.cs` (request types and mapping)
  **Acceptance**: `POST /prompt { model: { providerID: "openrouter", modelID: "anthropic/claude-opus-4.6" } }` works. Old `{ model: "openrouter/anthropic/claude-opus-4.6" }` still works (split on first `/`).

- [x] 4. **Update OpenCode harness to use separate provider/model fields**
  **What**: In `OpenCodeHarnessSession.SendPromptAsync`, use `options.ProviderId` and `options.ModelId` directly to construct `OpenCodeModelRefRequest` instead of splitting on `/`. In `SendCommandAsync`, compose `"{providerId}/{modelId}"` for the command path (OpenCode requires a flat string there).
  **Files**: `src/WeaveFleet.Infrastructure/Harnesses/OpenCode/OpenCodeHarnessSession.cs`
  **Acceptance**: No `IndexOf('/')` splitting in `SendPromptAsync`. Command path still works.

- [x] 5. **Update ClaudeCode harness if needed**
  **What**: Check `ClaudeCodeHarnessSession.SendPromptAsync` and `SendCommandAsync` for model handling. If it also splits on `/`, update similarly. If it doesn't use model selection at all, no change needed.
  **Files**: `src/WeaveFleet.Infrastructure/Harnesses/ClaudeCode/ClaudeCodeHarnessSession.cs`
  **Acceptance**: ClaudeCode harness still compiles and passes tests.

- [x] 6. **Frontend: carry `providerId` in `ModelOption`**
  **What**: Add `providerId: string` to `ModelOption`. Update `toModelOptions()` to set it from `provider.id` instead of `provider.name`. The `id` field should remain `model.id` (raw, no prefix).
  **Files**: `client/src/composables/use-models.ts`
  **Acceptance**: `ModelOption` objects have correct `providerId` values.

- [x] 7. **Frontend: send structured model in prompt requests**
  **What**: Change `BackendSendPromptRequest.model` from `string` to `{ providerID: string; modelID: string }`. In `useSendPrompt`, resolve the `ModelOption` and send `{ providerID: model.providerId, modelID: model.id }`.
  **Files**: `client/src/composables/use-send-prompt.ts`, `client/src/lib/api-types.ts`
  **Acceptance**: Network request payload has `model: { providerID: "...", modelID: "..." }`.

- [x] 8. **Frontend: send structured model in command requests**
  **What**: Same as above for `BackendSendCommandRequest` in `useSendCommand`. Also update `use-message-queue.ts` which already uses `{ providerID, modelID }` shape — align all paths.
  **Files**: `client/src/composables/use-send-command.ts`, `client/src/composables/use-message-queue.ts`
  **Acceptance**: Command requests carry structured model.

- [x] 9. **Update backend tests**
  **What**: Update `InstanceEndpointTenantIsolationTests` — the model ID assertions (lines 158-162) need to reflect raw IDs without `openrouter/` prefix. Update any tests that construct `PromptOptions` or `CommandOptions` with model IDs. Add a test for backward-compat string model parsing.
  **Files**: `tests/WeaveFleet.Api.Tests/Endpoints/InstanceEndpointTenantIsolationTests.cs`, `tests/WeaveFleet.Infrastructure.Tests/Harnesses/OpenCode/OpenCodeMapperTests.cs`
  **Acceptance**: All backend tests pass.

- [x] 10. **Update frontend tests**
  **What**: Update any tests that mock model data or assert on model IDs. Check `Composer.test.ts` ModelSelector mock and any snapshot tests.
  **Files**: `client/src/components/__tests__/Composer.test.ts` (and any other affected test files)
  **Acceptance**: `npm run test` passes.

## Verification
- [x] `dotnet test` — all projects pass
- [x] `npm run build && npm run test` in `client/` — passes
- [x] No `"openrouter/"` string construction in `InstanceEndpoints.cs`
- [x] No `IndexOf('/')` model splitting in `OpenCodeHarnessSession.SendPromptAsync`
- [ ] E2E test with model selection works (manual or E2E suite)
- [x] Backward compat: old `{ model: "provider/model" }` string format still accepted by API
