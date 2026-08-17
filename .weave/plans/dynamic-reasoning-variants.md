# Dynamic Reasoning Variants

## TL;DR
Replace hardcoded reasoning effort levels with dynamic variants from OpenCode's provider/model API response, threading `variants` through the full stack.

## Context
OpenCode's `/provider` endpoint returns a `variants` array per model (sibling to `capabilities`, required field). Each variant has `id`, `settings`, `headers`, and `body`. Fleet's `OpenCodeProviderModel` does not deserialize this field, so the data is silently dropped. The EffortToggle currently hardcodes `["low", "medium", "high"]` and uses a prefix-based `supportsReasoning` check. This plan replaces both with data-driven behavior.

## Scope
- In scope: 8 file edits to thread variants from OpenCode response to EffortToggle
- Out of scope: Using variant `headers`/`body`/`settings` (only `id` is needed for the toggle)
- Constraints: The `variants` field is required in OpenCode's schema, so it will always be present. We only need the `id` from each variant entry.

## Objectives
- Deserialize `variants` from OpenCode's provider response
- Thread variant IDs through domain, API, and frontend layers
- EffortToggle dynamically renders available variants instead of hardcoded levels
- Toggle visibility driven by `variants.length > 0` instead of model prefix matching

## Dependencies and Order
1. Backend deserialization (Task 1) -- no dependencies
2. Domain model (Task 2) -- no dependencies, parallel with Task 1
3. Backend mapper (Task 3) -- depends on Tasks 1, 2
4. API response (Task 4) -- depends on Task 2
5. API endpoint (Task 5) -- depends on Tasks 3, 4
6. Frontend type + registry (Task 6) -- no dependencies, parallel with backend
7. EffortToggle dynamic variants (Task 7) -- depends on Task 6
8. Composer wiring (Task 8) -- depends on Tasks 6, 7
9. Verify end-to-end (Task 9) -- depends on all

## Tasks

- [x] 1. Deserialize `variants` on `OpenCodeProviderModel`
  - **What**: Add a `Variants` property to `OpenCodeProviderModel` that deserializes the `variants` array. We only need the `id` from each variant entry. Add a small inner record `OpenCodeModelVariant` with `[JsonPropertyName("id")] public required string Id { get; init; }` and add `[JsonPropertyName("variants")] public IReadOnlyList<OpenCodeModelVariant> Variants { get; init; } = [];` to `OpenCodeProviderModel`.
  - **Files**: `src/WeaveFleet.Infrastructure/Harnesses/OpenCode/OpenCodeModels.cs` (after line ~573)
  - **Depends on**: None
  - **Acceptance**:
    - `OpenCodeProviderModel` has a `Variants` property of type `IReadOnlyList<OpenCodeModelVariant>`
    - `OpenCodeModelVariant` record exists with an `Id` property
    - `dotnet build src/WeaveFleet.Infrastructure` succeeds

- [x] 2. Add `Variants` to `HarnessModel`
  - **What**: Change `HarnessModel` from `(string Id, string Name)` to `(string Id, string Name, IReadOnlyList<string> Variants)`. Use a default value of `[]` to avoid breaking existing construction sites: `public sealed record HarnessModel(string Id, string Name, IReadOnlyList<string> Variants = default!);` with a static empty list fallback, or just make it `IReadOnlyList<string>?` defaulting to null.
  - **Files**: `src/WeaveFleet.Application/Harnesses/HarnessModels.cs` (line ~72)
  - **Depends on**: None
  - **Acceptance**:
    - `HarnessModel` has a `Variants` property
    - `dotnet build src/WeaveFleet.Application` succeeds

- [x] 3. Map variants in `OpenCodeMapper`
  - **What**: Update `ToHarnessProviders` to extract variant IDs: change `new HarnessModel(m.Id, m.Name ?? m.Id)` to `new HarnessModel(m.Id, m.Name ?? m.Id, m.Variants.Select(v => v.Id).ToList())`.
  - **Files**: `src/WeaveFleet.Infrastructure/Harnesses/OpenCode/OpenCodeMapper.cs` (line ~188)
  - **Depends on**: Tasks 1, 2
  - **Acceptance**:
    - Mapper threads variant IDs from `OpenCodeProviderModel.Variants` into `HarnessModel.Variants`
    - `dotnet build src/WeaveFleet.Infrastructure` succeeds

- [x] 4. Add `Variants` to `InstanceModelItem`
  - **What**: Change `InstanceModelItem` from `(string Id, string Name)` to include variants: `public sealed record InstanceModelItem(string Id, string Name, IReadOnlyList<string>? Variants = null);`. Use null default so existing construction sites don't break.
  - **Files**: `src/WeaveFleet.Api/Endpoints/ApiResponses.cs` (line ~124)
  - **Depends on**: Task 2
  - **Acceptance**:
    - `InstanceModelItem` has a `Variants` property
    - `dotnet build src/WeaveFleet.Api` succeeds

- [x] 5. Thread variants in API endpoint
  - **What**: Update the `InstanceModelItem` construction in `SessionEndpoints.cs` to pass variants: change `new InstanceModelItem(m.Id, m.Name ?? m.Id)` to `new InstanceModelItem(m.Id, m.Name ?? m.Id, m.Variants)`.
  - **Files**: `src/WeaveFleet.Api/Endpoints/SessionEndpoints.cs` (line ~488)
  - **Depends on**: Tasks 3, 4
  - **Acceptance**:
    - `InstanceModelItem` is constructed with variants from `HarnessModel`
    - `dotnet build src/WeaveFleet.Api` succeeds

- [x] 6. Add `variants` to frontend model type
  - **What**: Add `variants?: string[]` to `ProviderModelInfo` in `provider-registry.ts`. Check any other type used for API model responses (search for the type that deserializes the models endpoint response) and add `variants` there too.
  - **Files**: `client/src/lib/provider-registry.ts` (line ~11), and any API response type for models
  - **Depends on**: None
  - **Acceptance**:
    - `ProviderModelInfo` has `variants?: string[]`
    - `bunx vue-tsc --noEmit` passes

- [x] 7. Make EffortToggle accept dynamic variants
  - **What**: Add an optional `variants` prop (`string[]`) to EffortToggle. When provided, use it instead of the hardcoded `["low", "medium", "high"]` for cycling. Keep the hardcoded list as fallback when prop is not provided. Update the dot count to match `variants.length`. Update `EffortLevel` type if needed (it may need to become `string` if variants are arbitrary).
  - **Files**: `client/src/components/session/EffortToggle.vue`, possibly `client/src/composables/use-draft-state.ts` (if `EffortLevel` type needs widening)
  - **Depends on**: Task 6
  - **Acceptance**:
    - EffortToggle accepts a `variants` prop
    - When provided, cycles through those variants instead of hardcoded ones
    - Dot count matches number of variants
    - `bunx vue-tsc --noEmit` passes

- [x] 8. Replace hardcoded `supportsReasoning` in Composer
  - **What**: Remove the hardcoded `reasoningPrefixes` array and `supportsReasoning` computed. Replace with a lookup of the selected model's `variants` from the provider/model data. Show EffortToggle when the model has `variants.length > 0`. Pass the model's variants to EffortToggle as a prop.
  - **Files**: `client/src/components/session/Composer.vue`
  - **Depends on**: Tasks 6, 7
  - **Acceptance**:
    - No hardcoded model prefix list remains
    - Toggle visibility is driven by model variants data
    - Variants are passed to EffortToggle
    - `bun run build` and `bunx vue-tsc --noEmit` pass

- [x] 9. Verify end-to-end
  - **What**: Build backend, build frontend, run type-check and tests.
  - **Depends on**: All
  - **Acceptance**:
    - `dotnet build src/WeaveFleet.Api` succeeds
    - `bun run build` succeeds (from client/)
    - `bunx vue-tsc --noEmit` passes (from client/)
    - `dotnet test tests/WeaveFleet.Api.Tests` passes
    - `bun run test` passes (from client/)

## Verification
```bash
# Backend
dotnet build src/WeaveFleet.Api
dotnet build src/WeaveFleet.Domain
dotnet build src/WeaveFleet.Infrastructure

# Frontend
cd client && bun run build && bunx vue-tsc --noEmit

# Tests
dotnet test tests/WeaveFleet.Api.Tests
cd client && bun run test
```
All commands pass with zero errors.
