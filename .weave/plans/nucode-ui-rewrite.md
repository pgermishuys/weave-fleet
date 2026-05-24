# NuCode Settings Panel — Dynamic Provider UI Rewrite

## TL;DR
> **Summary**: Replace hardcoded 4-provider NuCode settings with dynamic provider listing from the new backend registry API, supporting 35+ providers with per-provider credential management and OAuth device flow.
> **Estimated Effort**: Medium

## Context
### Original Request
Rewrite NuCodeSettingsPanel.vue to consume the new data-driven provider registry API instead of hardcoding 4 providers.

### Key Findings
- `NuCodeSettingsPanel.vue` (382 lines) hardcodes `PROVIDERS` array with copilot/anthropic/openai/custom
- Credentials checked via generic `GET /api/credentials` with namespace/kind matching
- Connection test uses non-provider-specific `POST /api/nucode/test-connection`
- `NuCodeSection.vue` checks `nucode.baseUrl` preference for readiness — this preference is being removed
- Existing composable pattern (`use-credentials.ts`) provides good template: `ref` state, `apiFetch`, `readonly` returns
- `api-types.ts` has no NuCode provider types yet
- Preferences store used for `nucode.provider`, `nucode.modelId`, `nucode.baseUrl`

## Objectives
### Core Objective
Dynamic provider UI that scales to 35+ providers without hardcoded config.

### Deliverables
- [ ] TypeScript types for provider API responses
- [ ] `useNuCodeProviders` composable for provider data + actions
- [ ] Rewritten `NuCodeSettingsPanel.vue` with dynamic rendering
- [ ] Updated `NuCodeSection.vue` readiness logic (no more `nucode.baseUrl`)

### Definition of Done
- [ ] `npm run type-check` passes
- [ ] `npm run lint` passes
- [ ] Provider dropdown renders grouped by category
- [ ] Credential fields render dynamically from provider metadata
- [ ] Connection test uses per-provider endpoint
- [ ] OAuth device flow works for copilot provider

### Guardrails (Must NOT)
- No new npm dependencies
- Do not change backend API contracts
- Do not remove `nucode.provider` or `nucode.modelId` preferences
- Do not touch the generic credentials system (`/api/credentials`)

## TODOs

- [x] 1. Add provider types to api-types.ts
  **What**: Add `NuCodeProvider`, `NuCodeCredentialField`, `NuCodeProviderCategory`, `NuCodeStoreCredentialsRequest`, `NuCodeTestConnectionResponse`, and `NuCodeDeviceCodeResponse` types matching the backend API shapes.
  **Files**: `client/src/lib/api-types.ts`
  **Acceptance**: Types compile; `NuCodeProvider` has fields: `id`, `name`, `category`, `authMechanism`, `credentialFields`, `defaultModels`, `defaultBaseUrl`, `isConfigured`

- [x] 2. Create useNuCodeProviders composable
  **What**: New composable that encapsulates all provider API interactions:
  - `providers` — reactive list from `GET /api/nucode/providers`
  - `fetchProviders()` — load/refresh provider list
  - `storeCredentials(providerId, fields)` — `PUT /api/nucode/providers/{id}/credentials`
  - `deleteCredentials(providerId)` — `DELETE /api/nucode/providers/{id}/credentials`
  - `testConnection(providerId)` — `POST /api/nucode/providers/{id}/test`
  - `storeConfig(providerId, config)` — `PUT /api/nucode/providers/{id}/config`
  - `startDeviceAuth(providerId)` — `POST /api/nucode/providers/{id}/auth/device`
  - `isLoading`, `error` refs
  
  Follow the pattern from `use-credentials.ts`: `shallowRef` for loading/error, `ref` for data, `readonly` returns.
  **Files**: `client/src/composables/use-nucode-providers.ts`
  **Acceptance**: Composable exports typed return interface; all 6 API methods implemented with error handling

- [x] 3. Rewrite NuCodeSettingsPanel.vue — provider selection
  **What**: Replace hardcoded `PROVIDERS` array and `<select>` with dynamic provider dropdown grouped by category. Use `<optgroup>` elements for each category (cloud, self-hosted, gateway, specialized). Show configured status (checkmark icon) next to provider names. Remove the `Provider` type alias and `ProviderOption` interface. Load providers via `useNuCodeProviders()` on mount.
  **Files**: `client/src/components/settings/NuCodeSettingsPanel.vue`
  **Acceptance**: Dropdown renders all providers from API grouped by category; selecting a provider updates `nucode.provider` preference

- [x] 4. Rewrite NuCodeSettingsPanel.vue — dynamic credential fields
  **What**: Replace the static "Credential status" card with a dynamic credential form. For each field in `selectedProvider.credentialFields`, render an `<input>` (type=password for `isSecret`, text otherwise) with the field's `label` and `placeholder`. Add Save and Disconnect buttons that call `storeCredentials` / `deleteCredentials`. For providers with `authMechanism: "none"`, show "No credentials required" message. For `authMechanism: "oauth-device"`, show a "Connect with GitHub" button instead of input fields.
  **Files**: `client/src/components/settings/NuCodeSettingsPanel.vue`
  **Acceptance**: Credential fields render dynamically; Save persists via PUT endpoint; Disconnect calls DELETE endpoint; "none" auth shows no fields

- [x] 5. Rewrite NuCodeSettingsPanel.vue — OAuth device flow
  **What**: Add device flow UI for copilot-type providers (`authMechanism: "oauth-device"`). On "Connect" click, call `startDeviceAuth(providerId)`. Display the `userCode` and `verificationUri` with a "Copy code" button. Poll using the existing GitHub device flow polling pattern (reuse `DeviceCodeResponse`/`PollResponse` types from api-types.ts if compatible, or add new ones). Show success/error states. After successful auth, refresh provider list to update `isConfigured`.
  **Files**: `client/src/components/settings/NuCodeSettingsPanel.vue`
  **Acceptance**: Device flow launches, shows code + URL, polls for completion, updates configured status on success

- [x] 6. Rewrite NuCodeSettingsPanel.vue — connection test + model selection
  **What**: Update connection test to use `POST /api/nucode/providers/{id}/test` instead of generic endpoint. Keep model input + preset buttons, but source `defaultModels` from the selected provider's API data. Remove `baseUrl` state and `onBaseUrlChange` — baseUrl is now a config field managed via `PUT /api/nucode/providers/{id}/config` (render it as part of the dynamic credential fields if the provider has a non-secret config field like `config:baseUrl`). Remove the "Advanced" collapsible section.
  **Files**: `client/src/components/settings/NuCodeSettingsPanel.vue`
  **Acceptance**: Test button calls per-provider endpoint; model presets come from API; no more `nucode.baseUrl` preference writes

- [x] 7. Update NuCodeSection.vue readiness logic
  **What**: Remove `baseUrl` computed property and its use in `readinessStatus`. Update readiness to check `isConfigured` from the provider data (via a prop or shared composable state) instead of checking baseUrl. Simplify: if provider is selected and `isConfigured` is true and `modelId` is set → "ready". If provider selected but not configured → "missing-credentials". Otherwise → "not-configured".
  **Files**: `client/src/components/settings/NuCodeSection.vue`
  **Acceptance**: No reference to `nucode.baseUrl`; readiness badge reflects provider's `isConfigured` status

- [x] 8. Verify build and lint
  **What**: Run `npm run type-check` and `npm run lint` to ensure no type errors or lint violations. Fix any issues.
  **Acceptance**: Both commands exit 0

## Verification
- [ ] `npm run type-check` passes
- [ ] `npm run lint` passes
- [ ] No references to `nucode.baseUrl` preference remain in modified files
- [ ] No hardcoded provider arrays remain in NuCodeSettingsPanel.vue
- [ ] Provider dropdown renders with category grouping
- [ ] Credential fields render dynamically per provider
- [ ] Connection test uses per-provider endpoint
