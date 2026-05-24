# NuCode Provider Registry & Credential Abstraction

## TL;DR
> **Summary**: Replace hardcoded provider switch statements with a data-driven provider registry in NuCode, add a credential storage abstraction, and support 30+ LLM providers with varying auth mechanisms.
> **Estimated Effort**: Large

## Context
### Original Request
Build a provider registry, credential abstraction, auth flow system, and data-driven chat client factory so NuCode can support 30+ providers without hardcoded switches, while remaining portable (no Fleet dependencies).

### Key Findings
- **4 hardcoded switch statements** duplicate provider knowledge: `ChatClientFactory.Create()`, `NuCodeHarnessRuntime.ResolveRequirements()`, `NuCodeHarnessRuntime.InferProvider()`, `NuCodeConnectionTester.ResolveCredentialLookup()`
- All current providers (anthropic, openai, copilot, custom) use the same OpenAI SDK client — just different endpoints/credentials. This pattern holds for ~90% of target providers.
- `NuCodeLaunchArtifacts` has provider-specific fields (`GitHubToken`) that should be generalized
- NuCode core (`src/NuCode/`) has zero knowledge of providers — all provider logic lives in `src/WeaveFleet.Infrastructure/Harnesses/NuCode/`
- `ProviderConfig` already exists in `src/NuCode/Configuration/` but is just config-file options (apiKey, baseUrl, timeout) — not a provider definition
- `CopilotTokenService` is in Infrastructure and depends on `IHttpClientFactory` — needs to move to NuCode with an abstraction
- Fleet's `ICredentialStore` returns `UserCredential` domain entities — NuCode can't depend on this
- The `NuCodeJsonContext` for AOT serialization lives in `WeaveFleet.Infrastructure/JsonContext.cs`

## Objectives
### Core Objective
NuCode owns its provider model. Fleet is just a host that implements storage and surfaces UI.

### Deliverables
- [ ] Provider registry with definitions for all 30+ providers
- [ ] `INuCodeCredentialStore` interface in NuCode, Fleet adapter implementation
- [ ] Auth flow abstraction (API key, OAuth device, OAuth browser, AWS, service account, none)
- [ ] Data-driven `ChatClientFactory` — no switch statements
- [ ] Token lifecycle management (Copilot token exchange, OAuth refresh)
- [ ] Fleet API endpoints for provider management
- [ ] Migration from hardcoded to registry-based (zero breaking changes)

### Definition of Done
- [ ] `dotnet build` succeeds
- [ ] `dotnet test` passes all existing + new tests
- [ ] Existing anthropic/openai/copilot/custom providers work identically
- [ ] Adding a new API-key provider requires zero code changes (just a registry entry)
- [ ] NuCode project has no references to WeaveFleet.Domain or WeaveFleet.Application

### Guardrails (Must NOT)
- NuCode must NOT reference EF Core, ASP.NET Data Protection, or any Fleet assembly
- Must NOT break existing credential storage — migration must be backward-compatible
- Must NOT remove the existing `ICredentialStore` — Fleet still uses it for non-NuCode credentials
- Must NOT add provider SDK packages (everything goes through OpenAI SDK's compatible endpoint)

## TODOs

### Phase 1: Provider Registry & Definitions (NuCode core)

- [x] 1. Define provider model types
  **What**: Create the core types that describe a provider: `ProviderDefinition`, `AuthMechanism` enum, `CredentialField`, `ProviderEndpoint`. A `ProviderDefinition` is a static description — ID, display name, auth mechanism, required credential fields, default endpoint, whether baseURL is customizable, known model prefixes for inference.
  **Files**: `src/NuCode/Providers/ProviderDefinition.cs`, `src/NuCode/Providers/AuthMechanism.cs`, `src/NuCode/Providers/CredentialField.cs`
  **Acceptance**: Types compile, no external dependencies beyond `System.*`

  Key types:
  ```
  enum AuthMechanism { ApiKey, OAuthDevice, OAuthBrowser, AwsCredentialChain, ServiceAccountFile, None, Custom }
  
  record CredentialField(string Key, string DisplayName, bool Required, bool IsSecret, string? HelpText)
  // e.g. Key="apiKey", Key="resourceName", Key="projectId"
  
  record ProviderDefinition {
    string Id, string DisplayName, string? Description,
    AuthMechanism AuthMechanism,
    IReadOnlyList<CredentialField> CredentialFields,
    string? DefaultEndpoint,
    bool SupportsCustomBaseUrl,
    IReadOnlyList<string> ModelPrefixes,  // for InferProvider
    bool IsOpenAICompatible  // almost all are
  }
  ```

- [x] 2. Create provider registry
  **What**: `IProviderRegistry` interface and `ProviderRegistry` implementation. Holds all provider definitions. Supports lookup by ID, listing all, and inferring provider from model ID (replaces `InferProvider`). Populated at startup from a built-in catalog + optional user-registered custom providers.
  **Files**: `src/NuCode/Providers/IProviderRegistry.cs`, `src/NuCode/Providers/ProviderRegistry.cs`
  **Acceptance**: Registry can resolve all 4 current providers + infer provider from model name

- [x] 3. Create built-in provider catalog
  **What**: Static class `BuiltInProviders` that returns all ~30 provider definitions. Group by auth mechanism. Start with the 4 existing providers fully defined, then add all API-key providers (trivial — just ID + endpoint + "apiKey" credential field), then the complex ones.
  **Files**: `src/NuCode/Providers/BuiltInProviders.cs`
  **Acceptance**: All providers from the requirements list are defined

- [x] 4. Register provider services in DI
  **What**: Add `IProviderRegistry` registration to `NuCodeServiceCollectionExtensions.AddNuCode()`.
  **Files**: `src/NuCode/NuCodeServiceCollectionExtensions.cs`
  **Acceptance**: `IProviderRegistry` is resolvable from NuCode's service provider

### Phase 2: Credential Storage Abstraction (NuCode core)

- [x] 5. Define NuCode credential store interface
  **What**: `INuCodeCredentialStore` — NuCode's own credential storage contract. Operations: `GetAsync(providerId, fieldKey)`, `SetAsync(providerId, fieldKey, value)`, `DeleteAsync(providerId, fieldKey)`, `GetAllForProviderAsync(providerId)`, `ListConfiguredProvidersAsync()`. Returns simple DTOs, not domain entities. Values are always decrypted when returned (encryption is the host's concern).
  **Files**: `src/NuCode/Providers/INuCodeCredentialStore.cs`, `src/NuCode/Providers/StoredCredential.cs`
  **Acceptance**: Interface compiles, no Fleet dependencies

  Key types:
  ```
  record StoredCredential(string ProviderId, string FieldKey, string Value, DateTimeOffset? ExpiresAt, string? DisplayHint)
  
  interface INuCodeCredentialStore {
    Task<StoredCredential?> GetAsync(string providerId, string fieldKey, CancellationToken ct);
    Task<IReadOnlyList<StoredCredential>> GetAllForProviderAsync(string providerId, CancellationToken ct);
    Task SetAsync(string providerId, string fieldKey, string value, DateTimeOffset? expiresAt, CancellationToken ct);
    Task DeleteAsync(string providerId, string fieldKey, CancellationToken ct);
    Task DeleteAllForProviderAsync(string providerId, CancellationToken ct);
    Task<IReadOnlyList<string>> ListConfiguredProviderIdsAsync(CancellationToken ct);
  }
  ```

- [x] 6. Implement Fleet adapter for INuCodeCredentialStore
  **What**: `FleetNuCodeCredentialStore` in Infrastructure that adapts Fleet's `IUserCredentialRepository` to `INuCodeCredentialStore`. Maps `providerId`→namespace, `fieldKey`→kind. Uses existing encryption infrastructure. Registered in Fleet's DI as the `INuCodeCredentialStore` implementation.
  **Files**: `src/WeaveFleet.Infrastructure/Harnesses/NuCode/FleetNuCodeCredentialStore.cs`
  **Acceptance**: Existing credentials (anthropic/api-key, openai/api-key, github/oauth-access-token) are readable through the new interface

### Phase 3: Auth Flow Abstraction (NuCode core)

- [x] 7. Define auth flow interfaces
  **What**: `IAuthFlow` base interface with method `Task<AuthFlowResult> ExecuteAsync(ProviderDefinition provider, INuCodeCredentialStore store, CancellationToken ct)`. Concrete flow types: `ApiKeyAuthFlow` (no-op — credentials already stored by UI), `OAuthDeviceAuthFlow` (needs `IHttpClientFactory` abstraction), `NoAuthFlow`. `AuthFlowResult` is a discriminated union: `Success | NeedsUserAction(instructions) | Failed(error)`.
  **Files**: `src/NuCode/Providers/Auth/IAuthFlow.cs`, `src/NuCode/Providers/Auth/AuthFlowResult.cs`, `src/NuCode/Providers/Auth/ApiKeyAuthFlow.cs`, `src/NuCode/Providers/Auth/NoAuthFlow.cs`
  **Acceptance**: Types compile

- [x] 8. Move Copilot token exchange to NuCode
  **What**: Move `CopilotTokenService` logic into NuCode as `OAuthDeviceTokenExchange` (or keep name). It needs HTTP — introduce `INuCodeHttpClient` (simple wrapper: `Task<HttpResponseMessage> SendAsync(HttpRequestMessage, CancellationToken)`) so NuCode doesn't depend on `IHttpClientFactory`. Fleet implements it. The Copilot device flow initiation also moves here.
  **Files**: `src/NuCode/Providers/Auth/INuCodeHttpClient.cs`, `src/NuCode/Providers/Auth/CopilotTokenExchange.cs`, `src/NuCode/Providers/Auth/OAuthDeviceFlow.cs`
  **Acceptance**: Copilot token exchange works through the new abstraction

### Phase 4: Data-Driven Chat Client Factory

- [x] 9. Create data-driven ChatClientFactory
  **What**: Replace the switch-based `ChatClientFactory` with one that uses `ProviderDefinition` to create clients. Since ~all providers are OpenAI-compatible, the logic is: look up provider → get endpoint URL (default or custom) → get API key from credential store → create `OpenAIClient` with endpoint + key → `GetChatClient(modelId)`. Special cases (Copilot token exchange) handled by checking `AuthMechanism`. The factory moves to NuCode as `IChatClientFactory` interface + `NuCodeChatClientFactory` implementation.
  **Files**: `src/NuCode/Providers/IChatClientFactory.cs`, `src/NuCode/Providers/NuCodeChatClientFactory.cs`
  **Acceptance**: All 4 existing providers create identical clients as before

- [x] 10. Update NuCodeLaunchArtifacts
  **What**: Replace provider-specific fields with a generic credential bag. `NuCodeLaunchArtifacts` becomes: `ProviderId`, `ModelId`, `Credentials: IReadOnlyDictionary<string, string>` (fieldKey→value), `ProviderOptions: IReadOnlyDictionary<string, string>?` (baseUrl, resourceName, etc.). Remove `ApiKey`, `GitHubToken`, `BaseUrl` fields.
  **Files**: `src/WeaveFleet.Infrastructure/Harnesses/NuCode/NuCodeLaunchArtifacts.cs`
  **Acceptance**: Existing spawn flow works with new shape

### Phase 5: Migrate Runtime & Connection Tester

- [x] 11. Migrate NuCodeHarnessRuntime to use registry
  **What**: Replace `ResolveRequirements()` switch with `IProviderRegistry.GetById()` → `CredentialFields`. Replace `InferProvider()` with `IProviderRegistry.InferFromModelId()`. Replace `ChatClientFactory.Create()` with `IChatClientFactory.CreateAsync()`. Copilot token exchange now happens inside the factory.
  **Files**: `src/WeaveFleet.Infrastructure/Harnesses/NuCode/NuCodeHarnessRuntime.cs`
  **Acceptance**: Existing tests pass, no switch statements remain

- [x] 12. Migrate NuCodeConnectionTester to use registry
  **What**: Replace `ResolveCredentialLookup()` switch with registry lookup. Use `IChatClientFactory` instead of `ChatClientFactory.Create()`.
  **Files**: `src/WeaveFleet.Infrastructure/Harnesses/NuCode/NuCodeConnectionTester.cs`
  **Acceptance**: Connection test works for all 4 existing providers

- [x] 13. Delete old ChatClientFactory
  **What**: Remove `src/WeaveFleet.Infrastructure/Harnesses/NuCode/ChatClientFactory.cs` (replaced by NuCode's `NuCodeChatClientFactory`). Remove `CopilotTokenService.cs` (moved to NuCode). Clean up `NuCodeJsonContext` entries if needed.
  **Files**: `src/WeaveFleet.Infrastructure/Harnesses/NuCode/ChatClientFactory.cs`, `src/WeaveFleet.Infrastructure/Harnesses/NuCode/CopilotTokenService.cs`
  **Acceptance**: Build succeeds, no dead code

### Phase 6: Fleet API Endpoints

- [x] 14. Add provider listing endpoint
  **What**: `GET /api/nucode/providers` — returns all providers from registry with their status (connected/not-connected/needs-config). Status is determined by checking `INuCodeCredentialStore` for required credential fields.
  **Files**: `src/WeaveFleet.Api/Endpoints/NuCodeEndpoints.cs`
  **Acceptance**: Endpoint returns all providers with correct status

- [x] 15. Add provider credential management endpoints
  **What**: `GET /api/nucode/providers/{id}` — provider detail + status. `PUT /api/nucode/providers/{id}/credentials` — store credentials for a provider. `DELETE /api/nucode/providers/{id}/credentials` — disconnect a provider. `POST /api/nucode/providers/{id}/test` — test connection (replaces current test endpoint). `POST /api/nucode/providers/{id}/auth/initiate` — start OAuth device flow (returns device code + URL).
  **Files**: `src/WeaveFleet.Api/Endpoints/NuCodeEndpoints.cs`
  **Acceptance**: Can store API key for a new provider via API, test it, and use it in a session

- [x] 16. Add provider configuration endpoint
  **What**: `PUT /api/nucode/providers/{id}/config` — store provider-specific options (baseUrl, resourceName, etc.) as user preferences with key pattern `nucode.provider.{id}.{optionKey}`.
  **Files**: `src/WeaveFleet.Api/Endpoints/NuCodeEndpoints.cs`
  **Acceptance**: Can configure Azure OpenAI resource name via API

### Phase 7: Tests

- [x] 17. Unit tests for provider registry
  **What**: Test provider lookup, model inference, built-in catalog completeness, custom provider registration.
  **Files**: `tests/NuCode.Tests/Providers/ProviderRegistryTests.cs`
  **Acceptance**: All tests pass

- [x] 18. Unit tests for NuCodeChatClientFactory
  **What**: Test client creation for each auth mechanism type. Use mock credential store. Verify correct endpoint/key combinations.
  **Files**: `tests/NuCode.Tests/Providers/ChatClientFactoryTests.cs`
  **Acceptance**: All tests pass

- [x] 19. Unit tests for FleetNuCodeCredentialStore adapter
  **What**: Test mapping between NuCode's provider/fieldKey model and Fleet's namespace/kind model. Test round-trip store/retrieve.
  **Files**: `tests/WeaveFleet.Infrastructure.Tests/Harnesses/NuCode/FleetNuCodeCredentialStoreTests.cs`
  **Acceptance**: All tests pass

- [x] 20. Integration test for provider endpoint flow
  **What**: Test the full flow: list providers → store credential → test connection → use in session. Use a mock/local provider (Ollama-style no-auth endpoint).
  **Files**: `tests/WeaveFleet.Api.Tests/NuCodeProviderEndpointTests.cs`
  **Acceptance**: Full flow works end-to-end

## Verification
- [ ] `dotnet build` succeeds for entire solution
- [ ] `dotnet test` passes all existing + new tests
- [ ] No switch statements remain in provider/credential resolution code
- [ ] NuCode.csproj has no ProjectReference to WeaveFleet.* projects
- [ ] Existing anthropic/openai/copilot/custom providers produce identical behavior
- [ ] Adding DeepSeek (API key provider) requires only a `BuiltInProviders` entry — no other code changes
