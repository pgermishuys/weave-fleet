# Cloud Spike: Provider Auth & Container Harness

## TL;DR
> **Summary**: Prove two architectural capabilities: (1) injecting provider credentials into harness instances for headless authentication, and (2) running harnesses inside Podman containers instead of bare OS processes.
> **Estimated Effort**: Medium

## Context

### Original Request
Implement two sequential spikes on the same branch:
- **Spike A**: Store provider credentials and inject them into harness instances (env vars for API-key providers, HTTP API for OAuth providers like GitHub Copilot)
- **Spike B**: Run harness sessions inside Podman containers with the existing `IHarness`/`IHarnessInstance` abstraction

### Key Findings
1. **Environment pipeline exists but unused**: `HarnessSpawnOptions.Environment` flows to `OpenCodeProcessOptions.EnvironmentVariables` which gets applied to `ProcessStartInfo.Environment` in `OpenCodeProcessManager` (lines 117-121). Currently never populated by callers.

2. **OpenCode auth API**: `PUT /auth/:providerID` with body `{"type":"api","key":"..."}` for API keys or `{"type":"oauth","refresh":"...","access":"...","expires":0}` for OAuth. Provider state reloads automatically.

3. **Provider → env var mapping**:
   - `anthropic` → `ANTHROPIC_API_KEY`
   - `openai` → `OPENAI_API_KEY`
   - `google` → `GEMINI_API_KEY` (or `GOOGLE_GENERATIVE_AI_API_KEY`)
   - `xai` → `XAI_API_KEY`
   - `mistral` → `MISTRAL_API_KEY`
   - `groq` → `GROQ_API_KEY`
   - `github-copilot` → requires HTTP API (OAuth), not env var

4. **Harness abstraction is process-agnostic**: `IHarness.SpawnAsync` returns `IHarnessInstance`. Nothing above the infrastructure layer cares whether it's a process or container.

5. **Podman is Docker-compatible**: CLI syntax is identical. Shelling out via `System.Diagnostics.Process` is sufficient for a spike.

6. **Next migration number**: 005 (existing: 001-004)

## Objectives

### Core Objective
Prove that Fleet can:
1. Authenticate harnesses with model providers without interactive `/connect`
2. Run harnesses in containers instead of processes

### Deliverables
- [ ] `ProviderCredential` entity and repository (CRUD for storing API keys/OAuth tokens)
- [ ] `ProviderCredentialService` with env-var mapping logic
- [ ] CRUD endpoints at `/api/credentials`
- [ ] Credential injection in `SessionOrchestrator.CreateSessionAsync` and `ResumeSessionAsync`
- [ ] `IContainerRuntime` abstraction with `PodmanContainerRuntime` implementation
- [ ] `ContainerOpenCodeHarness` and `ContainerOpenCodeHarnessInstance`
- [ ] Containerfile for OpenCode image
- [ ] Config toggle `Fleet:HarnessMode = "container" | "process"`

### Definition of Done
- [ ] `dotnet build` completes with no errors
- [ ] Creating a session with stored Anthropic API key results in the harness having `ANTHROPIC_API_KEY` set
- [ ] Creating a session with stored GitHub Copilot OAuth tokens results in POST to `PUT /auth/github-copilot`
- [ ] With `HarnessMode=container`, `podman run` is invoked instead of `opencode serve` directly

### Guardrails (Must NOT)
- No tests (spike only)
- No credential encryption (plain text in DB)
- No new NuGet packages (shell out to Podman CLI)
- No changes to existing API contracts (sessions, projects, etc.)

## TODOs

### Phase 1: Provider Credential Storage (Domain + Infrastructure)

- [ ] 1 Create ProviderCredential entity
  **What**: Define a simple entity for storing provider credentials. Fields: `Id`, `ProviderId`, `CredentialType` ("api" or "oauth"), `ApiKey`, `AccessToken`, `RefreshToken`, `ExpiresAt`, `CreatedAt`, `UpdatedAt`.
  **Files**: `src/WeaveFleet.Domain/Entities/ProviderCredential.cs`
  **Acceptance**: File exists with sealed class, follows Project.cs pattern

- [ ] 2 Create IProviderCredentialRepository interface
  **What**: Define repository interface with CRUD operations: `GetByProviderIdAsync`, `ListAsync`, `UpsertAsync`, `DeleteAsync`.
  **Files**: `src/WeaveFleet.Domain/Repositories/IProviderCredentialRepository.cs`
  **Acceptance**: Interface follows IProjectRepository pattern

- [ ] 3 Create database migration
  **What**: Add `provider_credentials` table with columns matching entity. Use snake_case column names per existing conventions.
  **Files**: `src/WeaveFleet.Infrastructure/Migrations/005_add_provider_credentials.sql`
  **Acceptance**: Migration file exists with CREATE TABLE statement

- [ ] 4 Create DapperProviderCredentialRepository
  **What**: Implement repository using Dapper. Follow DapperProjectRepository pattern exactly.
  **Files**: `src/WeaveFleet.Infrastructure/Data/Repositories/DapperProviderCredentialRepository.cs`
  **Acceptance**: Sealed class with all interface methods implemented

- [ ] 5 Register repository in DI
  **What**: Add `services.AddScoped<IProviderCredentialRepository, DapperProviderCredentialRepository>();` in DependencyInjection.cs
  **Files**: `src/WeaveFleet.Infrastructure/DependencyInjection.cs`
  **Acceptance**: Repository registered alongside other repositories (line ~50)

### Phase 2: Provider Credential Service (Application)

- [ ] 6 Create ProviderCredentialService
  **What**: Service with CRUD operations, provider-to-env-var mapping, and methods to:
  - `BuildEnvironmentDictionary(IEnumerable<ProviderCredential>)` → `Dictionary<string,string>` (env vars for API-key providers)
  - `GetOAuthCredentials()` → list of credentials requiring HTTP API injection
  **Files**: `src/WeaveFleet.Application/Services/ProviderCredentialService.cs`
  **Acceptance**: Sealed class following ProjectService pattern, includes mapping dictionary:
  ```csharp
  private static readonly Dictionary<string, string> ProviderEnvVars = new()
  {
      ["anthropic"] = "ANTHROPIC_API_KEY",
      ["openai"] = "OPENAI_API_KEY",
      ["google"] = "GEMINI_API_KEY",
      ["xai"] = "XAI_API_KEY",
      ["mistral"] = "MISTRAL_API_KEY",
      ["groq"] = "GROQ_API_KEY",
  };
  ```

- [ ] 7 Register service in DI
  **What**: Add `services.AddScoped<ProviderCredentialService>();` in DependencyInjection.cs
  **Files**: `src/WeaveFleet.Infrastructure/DependencyInjection.cs`
  **Acceptance**: Service registered alongside other application services (line ~58)

### Phase 3: Credential API Endpoints

- [ ] 8 Create ProviderCredentialEndpoints
  **What**: Minimal API endpoints:
  - `GET /api/credentials` — list all (returns providerId + type only, not secrets)
  - `GET /api/credentials/{providerId}` — get one (returns providerId + type only)
  - `PUT /api/credentials/{providerId}` — upsert (accepts apiKey OR accessToken+refreshToken)
  - `DELETE /api/credentials/{providerId}` — delete
  **Files**: `src/WeaveFleet.Api/Endpoints/ProviderCredentialEndpoints.cs`
  **Acceptance**: Follows ProjectEndpoints pattern, uses MapGroup("/api/credentials")

- [ ] 9 Create request/response DTOs
  **What**: Define DTOs inline in the endpoints file (spike — no separate file):
  - `UpsertCredentialRequest`: `ProviderId`, `ApiKey?`, `AccessToken?`, `RefreshToken?`, `ExpiresAt?`
  - `CredentialResponse`: `ProviderId`, `Type`, `CreatedAt`, `UpdatedAt` (no secrets exposed)
  **Files**: `src/WeaveFleet.Api/Endpoints/ProviderCredentialEndpoints.cs`
  **Acceptance**: Records defined at bottom of file, following SessionEndpoints pattern

- [ ] 10 Register endpoints in EndpointExtensions
  **What**: Add `app.MapProviderCredentialEndpoints();` call
  **Files**: `src/WeaveFleet.Api/Endpoints/EndpointExtensions.cs`
  **Acceptance**: New line added after existing endpoint registrations

### Phase 4: Credential Injection into Harness Spawn

- [ ] 4.1 Add SetProviderAuthAsync to OpenCodeHttpClient
  **What**: New method `SetProviderAuthAsync(string providerId, object payload, CancellationToken)` that calls `PUT /auth/{providerId}` with JSON body. For API keys: `{"type":"api","key":"..."}`. For OAuth: `{"type":"oauth","refresh":"...","access":"...","expires":0}`.
  **Files**: `src/WeaveFleet.Infrastructure/Harnesses/OpenCode/OpenCodeHttpClient.cs`
  **Acceptance**: Method added after GetProvidersAsync (~line 220), follows existing POST pattern

- [ ] 4.2 Add SetProviderAuthAsync DTOs
  **What**: Add request classes for auth API:
  ```csharp
  internal sealed record OpenCodeApiKeyAuthRequest(string Type, string Key);
  internal sealed record OpenCodeOAuthAuthRequest(string Type, string Refresh, string Access, long Expires);
  ```
  **Files**: `src/WeaveFleet.Infrastructure/Harnesses/OpenCode/OpenCodeModels.cs`
  **Acceptance**: Records added to existing models file

- [ ] 4.3 Inject ProviderCredentialService into SessionOrchestrator
  **What**: Add `ProviderCredentialService` parameter to primary constructor
  **Files**: `src/WeaveFleet.Application/Services/SessionOrchestrator.cs`
  **Acceptance**: Parameter added to constructor (line ~27), field available for use

- [ ] 4.4 Modify CreateSessionAsync for credential injection
  **What**: Before spawning harness:
  1. Call `providerCredentialService.ListAsync()` to get all stored credentials
  2. Build env var dict via `BuildEnvironmentDictionary()` → pass to `HarnessSpawnOptions.Environment`
  3. After spawn + health check (before return), get OAuth credentials and call `SetProviderAuthAsync` for each
  
  Note: This creates coupling between orchestrator and OpenCodeHttpClient. For the spike, cast `harnessInstance` to `OpenCodeHarnessInstance` to access the HTTP client, OR expose a method on the instance. Simplest: add `InjectProviderAuthAsync` method to `OpenCodeHarnessInstance` that delegates to the HTTP client.
  **Files**: `src/WeaveFleet.Application/Services/SessionOrchestrator.cs`, `src/WeaveFleet.Infrastructure/Harnesses/OpenCode/OpenCodeHarnessInstance.cs`
  **Acceptance**: Env vars populated, OAuth auth injected post-spawn

- [ ] 4.5 Modify ResumeSessionAsync for credential injection
  **What**: Same pattern as CreateSessionAsync — build env dict before spawn, inject OAuth after health check
  **Files**: `src/WeaveFleet.Application/Services/SessionOrchestrator.cs`
  **Acceptance**: Resume path also injects credentials

- [ ] 4.6 Add InjectProviderAuthAsync to IHarnessInstance
  **What**: Add optional method to interface (spike — acceptable to make it harness-specific):
  ```csharp
  Task InjectProviderAuthAsync(string providerId, string credentialType, string? apiKey, string? accessToken, string? refreshToken, CancellationToken ct);
  ```
  Or simpler: Just expose it on OpenCodeHarnessInstance and cast in orchestrator.
  **Files**: `src/WeaveFleet.Domain/Harnesses/IHarnessInstance.cs` (or skip interface, implement only on OpenCodeHarnessInstance)
  **Acceptance**: Method callable after spawn to inject OAuth credentials

### Phase 5: Podman Container Harness

- [ ] 5.1 Add ContainerOptions to FleetOptions
  **What**: Add configuration section:
  ```csharp
  public sealed class ContainerOptions
  {
      public string ImageName { get; set; } = "localhost/opencode-harness:latest";
      public string Runtime { get; set; } = "podman"; // or "docker"
  }
  ```
  Add property to FleetOptions: `public ContainerOptions Container { get; set; } = new();`
  Add HarnessMode: `public string HarnessMode { get; set; } = "process";` // "process" or "container"
  **Files**: `src/WeaveFleet.Application/Configuration/FleetOptions.cs`
  **Acceptance**: New options available, defaults to "process" mode

- [ ] 5.2 Create IContainerRuntime interface
  **What**: Define abstraction for container operations:
  ```csharp
  public interface IContainerRuntime
  {
      Task<ContainerInfo> RunAsync(ContainerRunOptions options, CancellationToken ct);
      Task StopAsync(string containerId, TimeSpan timeout, CancellationToken ct);
      Task<bool> IsRunningAsync(string containerId, CancellationToken ct);
  }
  
  public sealed record ContainerRunOptions
  {
      public required string Image { get; init; }
      public required string Name { get; init; }
      public required string WorkingDirectory { get; init; }
      public int InternalPort { get; init; } = 8080;
      public IReadOnlyDictionary<string, string> Environment { get; init; } = new Dictionary<string, string>();
  }
  
  public sealed record ContainerInfo
  {
      public required string ContainerId { get; init; }
      public required string Hostname { get; init; }
      public required int MappedPort { get; init; }
      public required Uri BaseUrl { get; init; }
  }
  ```
  **Files**: `src/WeaveFleet.Application/Harnesses/IContainerRuntime.cs`
  **Acceptance**: Interface defined in Application layer

- [ ] 5.3 Create PodmanContainerRuntime
  **What**: Implementation that shells out to `podman` CLI:
  - `RunAsync`: Execute `podman run -d --name {name} -v {workdir}:/workspace -p 0:{internalPort} -e ENV=VAL... {image}`
  - Parse output to get container ID, then `podman port {id}` to get mapped port
  - Build BaseUrl from localhost + mapped port
  - `StopAsync`: Execute `podman stop -t {timeout} {id}` then `podman rm {id}`
  - `IsRunningAsync`: Execute `podman inspect --format '{{.State.Running}}' {id}`
  **Files**: `src/WeaveFleet.Infrastructure/Harnesses/Container/PodmanContainerRuntime.cs`
  **Acceptance**: Sealed class, uses Process.Start pattern from OpenCodeProcessManager

- [ ] 5.4 Create ContainerOpenCodeHarness
  **What**: IHarness implementation that spawns containers:
  - Same Type/DisplayName/Capabilities as OpenCodeHarness
  - `SpawnAsync`: Use IContainerRuntime to start container, create HttpClient pointing at container, health check, return ContainerOpenCodeHarnessInstance
  - `ResumeAsync`: Same pattern (containers are ephemeral — resume creates new container)
  **Files**: `src/WeaveFleet.Infrastructure/Harnesses/Container/ContainerOpenCodeHarness.cs`
  **Acceptance**: Sealed class implementing IHarness, follows OpenCodeHarness structure

- [ ] 5.5 Create ContainerOpenCodeHarnessInstance
  **What**: IHarnessInstance implementation for container-based sessions:
  - Wraps OpenCodeHttpClient (same HTTP interface)
  - `StopAsync`: Calls IContainerRuntime.StopAsync
  - `CheckHealthAsync`: Calls HTTP client health check
  - Other methods delegate to OpenCodeHttpClient (same as OpenCodeHarnessInstance)
  **Files**: `src/WeaveFleet.Infrastructure/Harnesses/Container/ContainerOpenCodeHarnessInstance.cs`
  **Acceptance**: Sealed class implementing IHarnessInstance, reuses OpenCodeHttpClient

- [ ] 5.6 Create Containerfile for OpenCode image
  **What**: Simple Containerfile that:
  - Starts from a Node.js base (opencode is a Node app)
  - Installs opencode globally via npm
  - Sets WORKDIR to /workspace
  - Exposes port 8080
  - CMD runs `opencode serve --port 8080 --hostname 0.0.0.0`
  **Files**: `docker/opencode-harness/Containerfile`
  **Acceptance**: File exists, can be built with `podman build -t opencode-harness:latest docker/opencode-harness`

- [ ] 5.7 Conditional DI registration based on HarnessMode
  **What**: In DependencyInjection.cs, register either OpenCodeHarness or ContainerOpenCodeHarness based on `options.HarnessMode`:
  ```csharp
  if (options.HarnessMode == "container")
  {
      services.AddSingleton<IContainerRuntime, PodmanContainerRuntime>();
      services.AddSingleton<IHarness, ContainerOpenCodeHarness>();
  }
  else
  {
      services.AddSingleton<IHarness, OpenCodeHarness>();
  }
  ```
  Note: Keep ClaudeCodeHarness registration unconditional (it's stdio-based, doesn't need containerization for this spike).
  **Files**: `src/WeaveFleet.Infrastructure/DependencyInjection.cs`
  **Acceptance**: DI switches between process and container harness based on config

### Phase 6: Verification

- [ ] 6.1 Build verification
  **What**: Run `dotnet build` from solution root and verify no errors
  **Acceptance**: Exit code 0, no compilation errors

- [ ] 6.2 Document created files
  **What**: List all files created/modified as final step
  **Acceptance**: Complete inventory of changes

## Verification

- [ ] `dotnet build src/WeaveFleet.Api` completes with no errors
- [ ] No regressions to existing functionality
- [ ] New endpoints respond (manual curl test):
  - `PUT /api/credentials/anthropic` with `{"apiKey":"test"}` → 200
  - `GET /api/credentials` → lists stored credential
- [ ] Config toggle works: Setting `Fleet:HarnessMode=container` changes which IHarness is resolved

## Files Summary

### New Files (Spike A — Provider Auth)
- `src/WeaveFleet.Domain/Entities/ProviderCredential.cs`
- `src/WeaveFleet.Domain/Repositories/IProviderCredentialRepository.cs`
- `src/WeaveFleet.Infrastructure/Migrations/005_add_provider_credentials.sql`
- `src/WeaveFleet.Infrastructure/Data/Repositories/DapperProviderCredentialRepository.cs`
- `src/WeaveFleet.Application/Services/ProviderCredentialService.cs`
- `src/WeaveFleet.Api/Endpoints/ProviderCredentialEndpoints.cs`

### New Files (Spike B — Container Harness)
- `src/WeaveFleet.Application/Harnesses/IContainerRuntime.cs`
- `src/WeaveFleet.Infrastructure/Harnesses/Container/PodmanContainerRuntime.cs`
- `src/WeaveFleet.Infrastructure/Harnesses/Container/ContainerOpenCodeHarness.cs`
- `src/WeaveFleet.Infrastructure/Harnesses/Container/ContainerOpenCodeHarnessInstance.cs`
- `docker/opencode-harness/Containerfile`

### Modified Files
- `src/WeaveFleet.Infrastructure/DependencyInjection.cs` — register new services + conditional harness
- `src/WeaveFleet.Application/Configuration/FleetOptions.cs` — add ContainerOptions + HarnessMode
- `src/WeaveFleet.Api/Endpoints/EndpointExtensions.cs` — register credential endpoints
- `src/WeaveFleet.Application/Services/SessionOrchestrator.cs` — inject credentials on spawn/resume
- `src/WeaveFleet.Infrastructure/Harnesses/OpenCode/OpenCodeHttpClient.cs` — add SetProviderAuthAsync
- `src/WeaveFleet.Infrastructure/Harnesses/OpenCode/OpenCodeModels.cs` — add auth request DTOs
- `src/WeaveFleet.Infrastructure/Harnesses/OpenCode/OpenCodeHarnessInstance.cs` — add InjectProviderAuthAsync
