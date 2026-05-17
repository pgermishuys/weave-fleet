# NuCode Settings Section

## TL;DR
> **Summary**: Add a dedicated NuCode settings section with enable/disable toggle, provider/model configuration, credential status, and connection testing — gating NuCode availability behind explicit opt-in.
> **Estimated Effort**: Large

## Context
### Original Request
NuCode always reports `available: true` (in-process harness). Users need to explicitly opt in, pick an LLM provider, configure a model, and see credential status — all from a new Settings section.

### Key Findings
- **Settings nav**: `SettingsSectionId` union type in `use-settings-nav.ts` + `SettingsNavPanel.vue` items array. Adding a section = add to both + create component + wire in `SettingsPage.vue`.
- **Preferences store**: Pinia `usePreferencesStore` with `get(key, fallback)` / `set(key, value)` backed by `PUT /api/preferences/{key}`. Server-side: `IUserPreferenceRepository.GetAsync(key)`.
- **Availability gating**: `NuCodeHarnessRuntime.CheckAvailabilityAsync()` currently hardcodes `true`. Needs to read a preference. The runtime has no `IUserPreferenceRepository` injected — needs DI change.
- **Provider inference**: `InferProvider(modelId)` in `NuCodeHarnessRuntime` currently defaults everything to "copilot". `ChatClientFactory.Create()` is a hardcoded 3-case switch (anthropic/openai/copilot). Both need to become config-driven.
- **Credential check**: `ResolveRequirements()` maps provider → credential namespace/kind. CredentialsSection stores creds via `PUT /api/credentials`. NuCode settings can read credential summaries (`GET /api/credentials`) to show status.
- **CSS patterns**: Section card = `rounded-card border border-border bg-card-bg p-6 shadow-sm`. Button/input class constants at top of component.

## Objectives
### Core Objective
NuCode is only available when explicitly enabled, and its provider/model configuration lives in a dedicated settings section.

### Deliverables
- [ ] NuCode settings section in the Settings page
- [ ] Enable/disable toggle persisted via preferences
- [ ] Provider selection (Anthropic, OpenAI, Copilot) with model input
- [ ] Credential status indicator (configured/missing) with link to Credentials section
- [ ] Backend availability gating based on enabled preference
- [ ] Connection test endpoint and UI button
- [ ] ChatClientFactory extensibility for custom baseURL providers

### Definition of Done
- [ ] `dotnet test` passes
- [ ] `npm run type-check` passes
- [ ] NuCode does not appear in harness selector when disabled
- [ ] NuCode appears when enabled + configured with valid credentials
- [ ] Connection test returns success/failure for configured provider

### Guardrails (Must NOT)
- Must NOT affect OpenCode or ClaudeCode harness behavior
- Must NOT remove existing provider support (anthropic, openai, copilot)
- Must NOT store credentials in preferences — credentials stay in the credential store
- Must NOT create a generic provider system — this is NuCode-specific

## TODOs

### Phase 1: Backend Foundation

- [x] 1. Inject IUserPreferenceRepository into NuCodeHarnessRuntime
  **What**: Add `IUserPreferenceRepository` to constructor. `CheckAvailabilityAsync` reads preference `nucode.enabled` — returns `Available: false` with reason "NuCode is not enabled. Enable it in Settings → NuCode." when not `"true"`.
  **Files**: `src/WeaveFleet.Infrastructure/Harnesses/NuCode/NuCodeHarnessRuntime.cs`
  **Acceptance**: `CheckAvailabilityAsync` returns false by default; returns true when preference `nucode.enabled` is `"true"`.

- [x] 2. Define NuCode preference keys as constants
  **What**: Create a static class `NuCodePreferenceKeys` with constants: `Enabled = "nucode.enabled"`, `Provider = "nucode.provider"`, `ModelId = "nucode.modelId"`, `BaseUrl = "nucode.baseUrl"`. Use in runtime and API.
  **Files**: `src/WeaveFleet.Infrastructure/Harnesses/NuCode/NuCodePreferenceKeys.cs`
  **Acceptance**: Constants compile and are referenced by runtime and future endpoint code.

- [x] 3. Make PrepareRuntimeAsync read provider from preferences
  **What**: Instead of `InferProvider(modelId)`, read `nucode.provider` and `nucode.modelId` preferences. Fall back to current inference logic if preferences are empty (backward compat). Inject `IUserPreferenceRepository` (already done in task 1).
  **Files**: `src/WeaveFleet.Infrastructure/Harnesses/NuCode/NuCodeHarnessRuntime.cs`
  **Acceptance**: When `nucode.provider=anthropic` and `nucode.modelId=claude-sonnet-4-20250514` preferences are set, `PrepareRuntimeAsync` uses them instead of inferring.

- [x] 4. Add NuCode connection test endpoint
  **What**: `POST /api/nucode/test-connection` — reads NuCode preferences + credentials, creates a `ChatClientFactory` instance, sends a minimal completion request (e.g., "ping"), returns `{ success: bool, error?: string, latencyMs: number }`. Reuses `PrepareRuntimeAsync` logic for credential resolution.
  **Files**: `src/WeaveFleet.Api/Endpoints/NuCodeEndpoints.cs`
  **Acceptance**: Endpoint returns 200 with success/failure. Invalid credentials return `{ success: false, error: "..." }`.

- [x] 5. Unit tests for availability gating
  **What**: Test that `CheckAvailabilityAsync` returns false when preference is missing/false, true when "true". Test that `PrepareRuntimeAsync` reads provider/model from preferences.
  **Files**: `tests/WeaveFleet.Infrastructure.Tests/Harnesses/NuCode/NuCodeHarnessRuntimeTests.cs`
  **Acceptance**: Tests pass with `dotnet test --filter NuCode`.

### Phase 2: Settings UI — Section Shell

- [x] 6. Add "nucode" to SettingsSectionId
  **What**: Add `"nucode"` to the `SettingsSectionId` union type.
  **Files**: `client/src/composables/use-settings-nav.ts`
  **Acceptance**: TypeScript compiles with the new section ID.

- [x] 7. Add NuCode nav item to SettingsNavPanel
  **What**: Add `{ id: "nucode", label: "NuCode", icon: Cpu }` (from lucide) to the items array, positioned after "skills" and before "system". Import `Cpu` icon.
  **Files**: `client/src/components/settings/SettingsNavPanel.vue`
  **Acceptance**: NuCode appears in settings nav panel.

- [x] 8. Create NuCodeSection.vue shell
  **What**: Create the section component following existing patterns (card layout, button/input class constants). Initially just the enable/disable toggle. Loads NuCode preferences on mount via `usePreferencesStore`. Toggle writes `nucode.enabled` preference.
  **Files**: `client/src/components/settings/NuCodeSection.vue`
  **Acceptance**: Toggle persists enabled state. Refreshing page preserves state.

- [x] 9. Wire NuCodeSection into SettingsPage
  **What**: Import `NuCodeSection`, add `v-else-if="activeSection === 'nucode'"` route in the template.
  **Files**: `client/src/components/settings/SettingsPage.vue`
  **Acceptance**: Clicking NuCode in nav shows the NuCode section.

### Phase 3: Provider Configuration UI

- [x] 10. Provider selection dropdown
  **What**: Add provider select (Anthropic, OpenAI, GitHub Copilot) to `NuCodeSection.vue`. Writes `nucode.provider` preference on change. Show provider-specific help text (e.g., "Uses GitHub OAuth token" for Copilot, "Requires API key" for others).
  **Files**: `client/src/components/settings/NuCodeSection.vue`
  **Acceptance**: Provider selection persists across page reloads.

- [x] 11. Model ID input with presets
  **What**: Add model input field with preset suggestions per provider. Anthropic: `claude-sonnet-4-20250514`, `claude-opus-4-20250514`. OpenAI: `gpt-4o`, `o3`. Copilot: `claude-sonnet-4-20250514`, `gpt-4o`. Presets are clickable chips that fill the input. Writes `nucode.modelId` preference.
  **Files**: `client/src/components/settings/NuCodeSection.vue`
  **Acceptance**: Model presets populate input. Custom model IDs can be typed. Value persists.

- [x] 12. Credential status indicator
  **What**: Fetch `GET /api/credentials` on mount. Check if a credential matching the selected provider's namespace/kind exists. Show green "Configured" badge or amber "Missing — Add in Credentials" link. For Copilot, check `github/oauth-access-token`. For Anthropic, check `anthropic/api-key`. For OpenAI, check `openai/api-key`. Link navigates to credentials section via `setActiveSection('credentials')`.
  **Files**: `client/src/components/settings/NuCodeSection.vue`
  **Acceptance**: Status updates reactively when provider changes. Missing credentials show warning with link.

- [x] 13. Overall readiness status
  **What**: Computed status at top of section: "Ready" (green, all configured), "Missing credentials" (amber), "Not configured" (grey, no provider selected). Shown as a status badge next to the section header.
  **Files**: `client/src/components/settings/NuCodeSection.vue`
  **Acceptance**: Status reflects actual configuration state accurately.

### Phase 4: Connection Test

- [x] 14. Connection test button and UX
  **What**: "Test Connection" button (disabled when not fully configured). Calls `POST /api/nucode/test-connection`. Shows spinner during test, then success (green check + latency) or failure (red error message). Auto-clears result after 10 seconds.
  **Files**: `client/src/components/settings/NuCodeSection.vue`
  **Acceptance**: Test succeeds with valid credentials. Test fails gracefully with clear error for invalid credentials.

### Phase 5: Extensibility

- [x] 15. Custom baseURL support in ChatClientFactory
  **What**: Add optional `baseUrl` parameter to `ChatClientFactory.Create()`. When provided, set `OpenAIClientOptions.Endpoint` to the custom URL. This enables proxies (Helicone), self-hosted endpoints, and OpenAI-compatible providers (Ollama, OpenRouter, etc.).
  **Files**: `src/WeaveFleet.Infrastructure/Harnesses/NuCode/ChatClientFactory.cs`
  **Acceptance**: `ChatClientFactory.Create("openai", "model", "key", baseUrl: "http://localhost:11434/v1")` creates client pointed at custom endpoint.

- [x] 16. Wire baseURL preference through runtime
  **What**: Read `nucode.baseUrl` preference in `PrepareRuntimeAsync`. Pass to `NuCodeLaunchArtifacts` (add `BaseUrl` field). Pass through to `ChatClientFactory.Create()` in `SpawnAsync`.
  **Files**: `src/WeaveFleet.Infrastructure/Harnesses/NuCode/NuCodeHarnessRuntime.cs`, `src/WeaveFleet.Infrastructure/Harnesses/NuCode/NuCodeLaunchArtifacts.cs` (or wherever the record is defined)
  **Acceptance**: Setting baseURL preference causes NuCode to use custom endpoint.

- [x] 17. BaseURL input in settings UI
  **What**: Optional "Custom endpoint URL" field in NuCodeSection, shown below provider/model. Collapsed by default with "Advanced" toggle. Writes `nucode.baseUrl` preference. Placeholder shows provider default URL.
  **Files**: `client/src/components/settings/NuCodeSection.vue`
  **Acceptance**: Custom baseURL persists and is used by connection test.

- [x] 18. Add "custom" provider option
  **What**: Add "Custom (OpenAI-compatible)" as a 4th provider option. When selected, baseURL becomes required, API key optional (for local models like Ollama). Provider value: `"custom"`. In `ChatClientFactory`, `"custom"` case uses OpenAI client with the provided baseURL.
  **Files**: `client/src/components/settings/NuCodeSection.vue`, `src/WeaveFleet.Infrastructure/Harnesses/NuCode/ChatClientFactory.cs`
  **Acceptance**: User can configure Ollama at `http://localhost:11434/v1` with no API key and test connection.

## Verification
- [ ] `dotnet test` — all tests pass including new NuCode preference/availability tests
- [ ] `npm run type-check` — no TypeScript errors
- [ ] `npm run lint` — no lint errors
- [ ] Manual: disable NuCode → not in harness selector; enable + configure → appears and works
- [ ] Manual: connection test succeeds with valid credentials, fails gracefully with invalid
- [ ] No regressions: OpenCode and ClaudeCode harnesses unaffected
