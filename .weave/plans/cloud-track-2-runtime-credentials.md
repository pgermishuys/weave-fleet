# Cloud Track 2: Runtime Credential Store + Session Bootstrap

## TL;DR
> **Summary**: Users store encrypted credentials in a generic, multi-credential store keyed by namespace/provider and kind — not by env var names. At session start, `SessionOrchestrator` loads the user's stored credentials (an opaque bag of `UserCredential` records) and passes them to `IHarness.PrepareRuntimeAsync()`. The harness owns **all** credential interpretation: requirement resolution, selection, materialization into runtime artifacts (env vars, config files, etc.), and pre-flight validation. The orchestrator receives back a readiness result (success or a list of product-level validation errors) — it never knows what credentials the harness needs or how they map to runtime mechanisms. Harness/model combinations that support built-in access work without stored credentials. Infrastructure deployment is framed around replaceable contracts (single-node host, TLS reverse proxy, process supervision, persistent key ring, runtime confinement) with Lightsail/Caddy/systemd as the reference implementation.
> **Estimated Effort**: Medium

## Context

### Original Request
Track 2 of the cloud MVP execution plan: runtime credential store + session bootstrap.

### Architectural Rule
Nothing above `IHarness` should know harness-specific auth, credential interpretation, runtime bootstrap, or runtime substrate details:
- The application layer (orchestrator, API, frontend) deals only in generic user credentials and product-level capability concepts.
- Credential requirement resolution, credential-to-requirement matching, and runtime materialization are **harness-internal** concerns — they live behind the `IHarness` boundary.
- `SessionOrchestrator` loads the user's credential bag generically, then delegates all runtime preparation to `IHarness`, receiving back either a readiness confirmation or product-level validation errors.
- Changing runtime substrate (in-process, subprocess, container, remote cloud) requires a new harness/runtime implementation — not cross-layer changes to the orchestrator, API, or frontend.
- The frontend stays product-level/capability-level — no wording that implies subprocess, env vars, or other runtime mechanisms.

### Key Findings
- `HarnessSpawnOptions.Environment` and `HarnessResumeOptions.Environment` are already wired through to `ProcessStartInfo.Environment` (via `OpenCodeProcessOptions.EnvironmentVariables`) but are never populated with user credentials.
- `FileIntegrationStore` stores plaintext JSON in `~/.weave/integrations.json` — cloud mode needs encrypted, user-scoped, multi-credential storage via ASP.NET Core Data Protection.
- Each harness already declares a `HarnessCapabilities` record, but credential requirements are inherently a function of harness + model selection — not harness alone. This knowledge belongs inside the harness, not in an application-layer resolver.
- The `IHarness` interface exposes `CheckAvailabilityAsync` — the same pattern (harness knows its own readiness requirements) applies to credential readiness.
- Credential materialization is not one-size-fits-all: some harnesses need env vars (`ANTHROPIC_API_KEY`), others may need config/auth files, future harnesses may use token exchange. This is a harness implementation detail — the orchestrator should not participate in or depend on it.
- Stored credentials should be identified by domain concepts (namespace/provider, kind, label) rather than env var names. The mapping from a stored credential to a runtime artifact (e.g. env var name) belongs entirely inside the harness.
- Storage and harness-level interpretation are two distinct concerns: the store manages CRUD + encryption at the application layer; the harness interprets, selects, validates, and materializes credentials at the infrastructure layer.
- Infrastructure tasks (host provisioning, TLS, supervision, key ring persistence) are independent of backend code and can start immediately.

### Relationship to Other Tracks
- **Depends on Track 1**: `IUserContext`, `UserId`-scoped repositories, managed workspace creation, `FleetOptions.Auth/Cloud`, auth pipeline, cloud appsettings, `GET /api/config/client`, `SessionOrchestrator` user-scoping
- **Track 3** depends on this track for: harness-level credential preparation on shared-session resume (owner's credentials passed to `IHarness.PrepareRuntimeAsync()`), onboarding completion state, and live infrastructure for E2E verification

## Objectives

### Core Objective
A signed-in user can store one or more encrypted credentials (identified by namespace/provider, kind, and user-chosen label), and when starting a session the orchestrator loads the user's credential bag and passes it to the harness, which internally resolves what it needs, validates availability, materializes runtime artifacts, and either proceeds or returns product-level validation errors — with credentials encrypted at rest and never exposed in API responses or logs. Harness/model combinations that do not require user-supplied credentials work without stored credentials.

### Deliverables
- [ ] `UserCredential` entity + repository + migration (multi-credential per user, keyed by namespace/kind/label — not env var names)
- [ ] `ICredentialProtector` with Data Protection implementation
- [ ] `ICredentialStore` — store, delete, list credentials for a user (pure storage, no env-var or runtime concerns)
- [ ] `IHarness.PrepareRuntimeAsync()` — harness-boundary method that accepts user credentials + launch context, performs all credential interpretation/validation/materialization internally, returns a `RuntimePreparation` (success with opaque launch artifacts, or product-level validation errors)
- [ ] Harness-internal credential interpretation: requirement resolution, selection, and materialization live inside each `IHarness` implementation (not as application-layer services)
- [ ] Credential API endpoints (`GET /api/credentials`, `PUT/DELETE /api/credentials/{id}`)
- [ ] `SessionOrchestrator` integration: load user credentials generically → call `IHarness.PrepareRuntimeAsync()` → use returned launch artifacts or surface validation errors
- [ ] Pre-flight check: missing credentials surface as product-level validation errors from the harness, never as harness-specific requirement details in the orchestrator
- [ ] ASP.NET Core Data Protection key persistence (configurable path)
- [ ] Frontend credential settings page (generic, capability-level — "API keys" not "environment variables")
- [ ] Onboarding wizard (welcome → connect credentials → ready, with skip if harness supports built-in access)
- [ ] Cloud-mode session creation UI (no directory picker)
- [ ] Login/logout UI chrome
- [ ] Onboarding status API extension on `GET /api/user/me`
- [ ] Infrastructure deployment using replaceable contracts (reference: Lightsail/Caddy/systemd)
- [ ] Deployment script and cloud backup/rollback notes

### Non-Goals
- Session sharing model and collaborative access (Track 3)
- Container-based harness isolation (post-MVP)
- PostgreSQL migration (post-MVP)
- Rate limiting or quotas (post-MVP)
- Token exchange, OAuth relay, or vault-backed credential injection (post-MVP — harness internals are the seam)
- Automated credential rotation or expiry management
- Multi-credential-per-requirement selection UI (post-MVP — harness-internal selection uses deterministic first-match for MVP)
- Application-layer services that interpret harness-specific credential requirements (e.g. no `ICredentialRequirementResolver`, `ICredentialSelectionPolicy`, or `IRuntimeBootstrapper` as application-layer interfaces)

### Definition of Done
- [x] `dotnet build` succeeds with zero errors
- [x] `dotnet test` passes — all existing and new tests
- [ ] Cloud mode: user stores one or more credentials → starts session → harness receives and internally materializes credentials
- [ ] Cloud mode: user with no stored credentials → session starts successfully when harness/model does not require them
- [ ] Cloud mode: user with no stored credentials → clear product-level validation error when harness/model requires them
- [ ] Cloud mode: credentials encrypted at rest (Data Protection)
- [ ] Cloud mode: API responses never contain plaintext credential values (only `displayHint` + metadata)
- [ ] Cloud mode: credential values never appear in logs or telemetry
- [ ] Cloud mode: onboarding wizard guides new user through credential storage and first session
- [ ] Cloud mode: onboarding completion does not require storing credentials if the default harness supports built-in access
- [ ] Cloud mode: session creation UI hides directory picker
- [ ] Cloud mode: `SessionOrchestrator` has no knowledge of credential namespaces, kinds, env var names, or runtime materialization
- [ ] Infrastructure: HTTPS terminates at TLS reverse proxy with valid certificate
- [ ] Infrastructure: Fleet restarts after deploy, health checks pass
- [ ] Infrastructure: Data Protection key ring persisted across restarts
- [ ] Infrastructure: hosted runtime confinement prevents reads of Fleet DB, key ring, deployment secrets, and other users' workspaces
- [ ] Infrastructure: deployment rollback procedure documented

### Guardrails (Must NOT)
- Must NOT expose credential values in any API response
- Must NOT log credential values or bearer tokens
- Must NOT break local mode
- Must NOT require containers or PostgreSQL
- Must NOT introduce multi-instance deployment requirements
- Must NOT implement session sharing (Track 3)
- Must NOT hard-code provider-specific logic (e.g. "anthropic") outside of harness implementations
- Must NOT assume one credential per provider — the store is a generic bag of user secrets
- Must NOT fail session creation when the harness does not require user-supplied credentials
- Must NOT allow shared-session resume to use participant's credentials instead of owner's (prepare the seam; Track 3 enforces)
- Must NOT use env var names as the primary identity for stored credentials — use namespace/kind instead
- Must NOT have the credential store build runtime env-var dictionaries — that responsibility belongs inside the harness
- Must NOT have `SessionOrchestrator` or any application-layer service interpret credential requirements, perform credential-to-requirement matching, or build runtime artifacts (env vars, config files, etc.)
- Must NOT expose harness-specific credential requirement types (namespaces, kinds, env var mappings) through the `IHarness` interface — the harness accepts a credential bag and returns a product-level result
- Must NOT use runtime-mechanism-specific terminology (env vars, process, subprocess, container) in frontend or API wording — use capability-level language instead

## TODOs

### Phase 1: Infrastructure Deployment Contracts (can begin in parallel with Track 1)

Infrastructure is described here as a set of **deployment contracts** — capabilities the host environment must provide. The reference implementation uses Lightsail/Caddy/systemd, but any stack satisfying the contracts is valid.

**Contracts:**
| Contract | Requirement | Reference Impl |
|----------|-------------|----------------|
| Single-node host | Linux VM, 2 GB+ RAM, .NET 10 runtime, harness binary, git | AWS Lightsail, Ubuntu 22.04 LTS |
| TLS reverse proxy | HTTPS termination with auto-renewable certificate, proxy to localhost app port | Caddy with automatic HTTPS |
| Process supervision | Auto-restart, journal/structured logging, low-privilege service account, resource limits | systemd unit file |
| Persistent key ring | Directory for ASP.NET Core Data Protection XML keys, survives restarts/deploys | `/opt/fleet/data/keys/` |
| Runtime confinement | Harness processes cannot read Fleet DB, key ring, deployment secrets, or other users' workspaces | Restricted service account / transient unit / filesystem ACLs |

- [x] 1. Provision host environment
  **What**: Provision a single-node host satisfying the host contract. Reference: AWS Lightsail (Ubuntu 22.04 LTS, 2 GB+ RAM), static IP, security group (ports 80, 443, 22), SSH key access.
  **Acceptance**: Host accessible via SSH, static IP assigned, required ports open.

- [x] 2. Install runtime dependencies
  **What**: Install .NET 10 runtime, harness binary (opencode), TLS reverse proxy (Caddy), git. Create directory structure: `/opt/fleet/`, `/opt/fleet/data/`, `/opt/fleet/data/keys/`, `/data/workspaces/`. Reference: `deploy/setup.sh`.
  **Files**: `deploy/setup.sh`
  **Acceptance**: All tools functional. Directories exist with correct ownership/permissions.

- [x] 3. Configure TLS reverse proxy
  **What**: Configure TLS termination with automatic HTTPS. Proxy `https://<domain>` → `http://127.0.0.1:8080`. Reference: Caddy with `deploy/Caddyfile`.
  **Files**: `deploy/Caddyfile`
  **Acceptance**: `https://<domain>/healthz` returns 200 with valid TLS certificate.

- [x] 4. Configure identity provider
  **What**: Create Clerk application (or equivalent OIDC provider). Configure OIDC URLs, claims, invite-only mode. Record authority/client ID/secret for backend config. Create initial invites.
  **Acceptance**: IdP dashboard configured. Invite-only enabled. Test user invited.

- [x] 5. Create deployment script
  **What**: Build .NET Release, build client SPA, copy artifacts to host, restart supervised service. Graceful stop before restart.
  **Files**: `deploy/deploy.sh`
  **Acceptance**: Script updates running instance. Health check passes after deploy.

- [x] 6. Configure process supervision
  **What**: Service definition: working directory, env vars, auto-restart, structured logging, low-privilege service account, no-new-privileges, disabled core dumps, resource limits. Reference: systemd unit file.
  **Files**: `deploy/fleet.service`
  **Acceptance**: Service starts, auto-restarts on failure. Not running as root.

- [x] 7. Define runtime confinement model
  **What**: Document and configure confinement for hosted session processes. Separate restricted runtime identity or transient unit model. Deny reads to `/opt/fleet/data`, key ring, deployment secrets, other users' workspaces. Write access limited to managed workspace + temp.
  **Files**: `deploy/README.md`
  **Acceptance**: Confinement model documented and manually verifiable.

- [x] 8. Document backup, restore, and rollback
  **What**: Document backup/restore for SQLite DB, Data Protection keys. Rollback procedure for failed deploys. Post-deploy smoke test checklist.
  **Files**: `deploy/README.md`
  **Acceptance**: Backup, restore, rollback procedures documented.

### Phase 2: Credential Store + API (requires Track 1 Phase 1 complete)

- [x] 9. Create UserCredential entity
  **What**: Domain entity: `Id` (GUID), `UserId`, `Namespace` (provider/system identifier, e.g. `"anthropic"`, `"openai"`, `"custom"`), `Kind` (credential type within the namespace, e.g. `"api-key"`, `"oauth-token"`), `Label` (user-chosen display name, e.g. "My Anthropic Key", "Work OpenAI"), `EncryptedValue`, `DisplayHint` (last 4 chars of plaintext), `Metadata` (nullable JSON string for extensible key-value data), `CreatedAt`, `UpdatedAt`. **No unique constraint on `(UserId, Namespace, Kind)` — users may store multiple credentials with the same namespace/kind** (e.g. two Anthropic API keys for different projects). Unique constraint on `(UserId, Label)` to prevent ambiguous display names.
  **Files**: `src/WeaveFleet.Domain/Entities/UserCredential.cs`
  **Acceptance**: Sealed class following entity pattern. Multiple credentials per user supported. No env-var names as primary identity.

- [x] 10. Create IUserCredentialRepository and Dapper implementation
  **What**: `GetByIdAsync`, `ListByUserAsync`, `ListByUserAndNamespaceAsync(userId, namespace)`, `ListByUserNamespaceAndKindAsync(userId, namespace, kind)`, `UpsertAsync`, `DeleteAsync`. User-scoped queries via `IUserContext`.
  **Files**: `src/WeaveFleet.Domain/Repositories/IUserCredentialRepository.cs`, `src/WeaveFleet.Infrastructure/Data/Repositories/DapperUserCredentialRepository.cs`
  **Acceptance**: CRUD works. User-scoped. Multiple credentials per namespace/kind supported.

- [x] 11. Create user_credentials migration
  **What**: `CREATE TABLE user_credentials` with all columns including `namespace`, `kind`, `metadata`. `UNIQUE(user_id, label)`. Index on `(user_id, namespace, kind)`. Index on `user_id`.
  **Files**: `src/WeaveFleet.Infrastructure/Migrations/012_add_user_credentials.sql`
  **Acceptance**: Migration runs cleanly on fresh and existing databases.

- [x] 12. Create ICredentialProtector and Data Protection implementation
  **What**: Application-layer interface: `Encrypt(plaintext) → ciphertext`, `Decrypt(ciphertext) → plaintext`. Infrastructure implementation using `IDataProtectionProvider.CreateProtector("UserCredentials")`.
  **Files**: `src/WeaveFleet.Application/Services/ICredentialProtector.cs`, `src/WeaveFleet.Infrastructure/Services/DataProtectionCredentialProtector.cs`
  **Acceptance**: Round-trip works. Ciphertext ≠ plaintext. Survives restart with persisted key ring.

- [x] 13. Configure Data Protection key persistence
  **What**: `builder.Services.AddDataProtection().PersistKeysToFileSystem(...)` in `Program.cs`. Path configurable via `FleetOptions` (e.g. `Fleet:DataProtection:KeyPath`). Ensures encryption survives restarts and deploys.
  **Files**: `src/WeaveFleet.Api/Program.cs`, `src/WeaveFleet.Application/Configuration/FleetOptions.cs`
  **Acceptance**: Keys persisted to configured path. Encrypted credentials survive process restart.

- [x] 14. Create ICredentialStore service
  **What**: Application-layer service. Pure storage concern — no env-var building, no runtime dictionaries, no credential interpretation. `ListCredentialsAsync()` → user's stored credentials with metadata (no values). `StoreCredentialAsync(label, namespace, kind, value, metadata?)` → encrypt and store. `DeleteCredentialAsync(id)` → remove. `GetDecryptedCredentialsAsync(userId)` → returns all decrypted credential records for the user (used by orchestrator to build the credential bag passed to the harness). The store returns full `UserCredential` records (with decrypted value) — it does not interpret, filter-by-requirement, or transform them.
  **Files**: `src/WeaveFleet.Application/Services/ICredentialStore.cs`, `src/WeaveFleet.Application/Services/CredentialStore.cs`
  **Acceptance**: Correct storage/retrieval. Keys encrypted at rest. Store does not build env-var dictionaries, does not filter by requirement, does not know about harness-specific needs.

- [x] 15. Create credential API endpoints
  **What**: `GET /api/credentials` — list user's stored credentials with metadata (label, namespace, kind, displayHint, createdAt — no values). `PUT /api/credentials` — store new credential (label, namespace, kind, value, metadata?). `PUT /api/credentials/{id}` — update existing credential value/metadata. `DELETE /api/credentials/{id}` — remove. Credential values NEVER appear in responses. API wording is capability-level: "API keys", "credentials" — not "environment variables" or "runtime configuration".
  **Files**: `src/WeaveFleet.Api/Endpoints/CredentialEndpoints.cs`, `src/WeaveFleet.Api/Endpoints/EndpointExtensions.cs`
  **Acceptance**: CRUD works. No plaintext credential values in any response. No runtime-mechanism-specific terminology in API contracts.

- [x] 16. Register credential storage services in DI
  **What**: Register `IUserCredentialRepository`, `DapperUserCredentialRepository`, `ICredentialStore`, `CredentialStore`, `ICredentialProtector`, `DataProtectionCredentialProtector`, Data Protection services.
  **Files**: `src/WeaveFleet.Infrastructure/DependencyInjection.cs`
  **Acceptance**: All storage services resolvable. `dotnet build` succeeds.

- [x] 17. Write credential store and API tests
  **What**: Unit: encrypt/decrypt round-trip, credential storage/retrieval with namespace/kind filtering. Integration: store/list/delete via API, update credential value. Security: response never contains plaintext values.
  **Files**: `tests/WeaveFleet.Application.Tests/Services/CredentialStoreTests.cs`, `tests/WeaveFleet.Api.Tests/Endpoints/CredentialEndpointTests.cs`
  **Acceptance**: All tests pass.

### Phase 3: Harness Runtime Preparation + Orchestrator Integration (requires Track 1 Phase 2 complete)

- [x] 18. Define RuntimePreparation types and extend IHarness with PrepareRuntimeAsync
  **What**: Add `PrepareRuntimeAsync(RuntimePreparationContext context, CancellationToken ct) → RuntimePreparation` to `IHarness`. Types:
  - `RuntimePreparationContext`: `UserId` (string), `UserCredentials` (IReadOnlyList\<UserCredential\> — opaque bag from the credential store), `ModelId` (string?, from the session request if known), `WorkingDirectory` (string).
  - `RuntimePreparation`: a discriminated result — either `Ready` (with an opaque `RuntimeLaunchArtifacts` record the orchestrator passes through to `SpawnAsync`/`ResumeAsync` without inspecting) or `NotReady` (with `IReadOnlyList<RuntimePreparationError>`).
  - `RuntimeLaunchArtifacts`: an opaque sealed record. The orchestrator treats it as a black box — it passes it through to the spawn/resume options but never reads its contents. The harness implementation defines what's inside (env vars, config file paths, etc.).
  - `RuntimePreparationError`: product-level error record: `Code` (e.g. `"MissingCredential"`), `Message` (user-facing, e.g. "An Anthropic API key is required to use this model"), `Guidance` (optional actionable hint, e.g. "Add an API key in Settings → Credentials"). No harness-internal details (namespaces, kinds, env var names) leak through this type.
  The orchestrator never inspects `RuntimeLaunchArtifacts` — it only checks readiness and forwards artifacts. The harness owns the entire interpretation pipeline internally.
  **Files**: `src/WeaveFleet.Application/Harnesses/IHarness.cs`, `src/WeaveFleet.Application/Harnesses/HarnessModels.cs`
  **Acceptance**: `IHarness` has `PrepareRuntimeAsync`. Types compile. Orchestrator can call it without knowing credential specifics. `RuntimeLaunchArtifacts` is opaque to the application layer.

- [x] 19. Add RuntimeLaunchArtifacts to HarnessSpawnOptions and HarnessResumeOptions
  **What**: Add an optional `RuntimeLaunchArtifacts? LaunchArtifacts` property to both `HarnessSpawnOptions` and `HarnessResumeOptions`. Remove the `Environment` property from both — it is replaced by the opaque `LaunchArtifacts`. Each harness implementation unpacks its own `RuntimeLaunchArtifacts` subclass in `SpawnAsync`/`ResumeAsync` to extract env vars, config paths, or whatever it needs. The orchestrator passes the artifacts through without interpretation. **Migration note**: existing callers that set `Environment` (currently none in production — it's always an empty dict) must switch to `LaunchArtifacts`. Test harnesses updated accordingly.
  **Files**: `src/WeaveFleet.Application/Harnesses/HarnessModels.cs`, `tests/WeaveFleet.TestHarness/TestHarness.cs`, `tests/WeaveFleet.Infrastructure.Tests/Harnesses/HarnessRegistryTests.cs`
  **Acceptance**: `HarnessSpawnOptions.Environment` and `HarnessResumeOptions.Environment` removed. `LaunchArtifacts` property present. All existing tests compile and pass. Orchestrator never populates env vars directly.

- [x] 20. Implement PrepareRuntimeAsync in OpenCodeHarness
  **What**: Inside `OpenCodeHarness`, implement the full credential interpretation pipeline as private/internal harness logic:
  1. **Resolve requirements**: based on model ID (e.g. `anthropic/*` → needs namespace `"anthropic"`, kind `"api-key"`; `openai/*` → needs `"openai"`, `"api-key"`; unknown → no requirements). This is a private method — not an application-layer interface.
  2. **Select credentials**: from the provided `UserCredentials` bag, find matching credentials by namespace/kind. Deterministic first-match by creation order. This is a private method.
  3. **Validate**: if any required credential is missing, return `RuntimePreparation.NotReady(...)` with product-level error messages (e.g. "An Anthropic API key is required"). The error messages use capability-level language, not env-var names.
  4. **Materialize**: build a concrete `RuntimeLaunchArtifacts` subclass containing env var dict, any config file paths, etc. — all harness-internal. E.g., namespace `"anthropic"` + kind `"api-key"` → env var `ANTHROPIC_API_KEY`.
  In `SpawnAsync`/`ResumeAsync`, unpack `LaunchArtifacts` and apply env vars to `ProcessStartInfo`. If `LaunchArtifacts` is null (local mode, no cloud credentials), proceed with empty env (existing behavior).
  **Files**: `src/WeaveFleet.Infrastructure/Harnesses/OpenCode/OpenCodeHarness.cs`
  **Acceptance**: `PrepareRuntimeAsync` resolves, selects, validates, materializes — all internally. Returns `Ready` with artifacts when credentials satisfied. Returns `NotReady` with product-level errors when missing. `SpawnAsync`/`ResumeAsync` consume artifacts. No credential-interpretation logic exists outside this harness.

- [x] 21. Implement PrepareRuntimeAsync in ClaudeCodeHarness
  **What**: `ClaudeCodeHarness.PrepareRuntimeAsync()` always returns `RuntimePreparation.Ready(...)` with empty/no-op artifacts — Claude Code uses built-in auth (`claude auth`), so no user-supplied credentials are needed. In `SpawnAsync`/`ResumeAsync`, if `LaunchArtifacts` is present, unpack (no-op for this harness).
  **Files**: `src/WeaveFleet.Infrastructure/Harnesses/ClaudeCode/ClaudeCodeHarness.cs`
  **Acceptance**: Always returns `Ready`. No credentials required. Existing behavior unchanged.

- [x] 22. Implement PrepareRuntimeAsync in TestHarness
  **What**: `TestHarness.PrepareRuntimeAsync()` returns `Ready` with no-op artifacts. Update `SpawnAsync`/`ResumeAsync` to accept `LaunchArtifacts`.
  **Files**: `tests/WeaveFleet.TestHarness/TestHarness.cs`
  **Acceptance**: Test harness compiles and works with new interface. All test infrastructure tests pass.

- [x] 23. Integrate credential loading + harness preparation into SessionOrchestrator
  **What**: In `CreateSessionAsync` and `ResumeSessionAsync`, after resolving the harness but before calling `SpawnAsync`/`ResumeAsync`:
  1. **Load user credentials**: `ICredentialStore.GetDecryptedCredentialsAsync(userId)` → opaque bag of `UserCredential` records. The orchestrator does not inspect, filter, or interpret these.
  2. **Call harness preparation**: `harness.PrepareRuntimeAsync(new RuntimePreparationContext { UserId, UserCredentials, ModelId, WorkingDirectory })` → `RuntimePreparation`.
  3. **Check readiness**: If `NotReady` → return `FleetError.ValidationError("Session.NotReady", ...)` with the product-level error messages from `RuntimePreparation.Errors`. The orchestrator passes these through verbatim — it does not construct or interpret them.
  4. **Pass artifacts through**: If `Ready` → set `HarnessSpawnOptions.LaunchArtifacts` / `HarnessResumeOptions.LaunchArtifacts` to the returned artifacts (opaque pass-through).
  For resume: load credentials using **session owner's** userId. Ensure decrypted values never logged.
  The orchestrator has **zero knowledge** of what credentials the harness needs, how they map to runtime mechanisms, or what the artifacts contain. It is a generic load-and-delegate pattern.
  **Files**: `src/WeaveFleet.Application/Services/SessionOrchestrator.cs`
  **Acceptance**: Orchestrator calls `PrepareRuntimeAsync` and passes artifacts through. No credential-interpretation logic in orchestrator. Missing credentials → clear product-level error. No requirements → session starts. Resume uses owner credentials. Values never in logs.

- [x] 24. Write harness preparation and orchestrator integration tests
  **What**:
  - **Harness-level unit tests**: `OpenCodeHarness.PrepareRuntimeAsync` returns correct readiness for various model/credential combinations. Returns `NotReady` with product-level errors when Anthropic model selected and no Anthropic credential stored. Returns `Ready` with artifacts when credentials present. `ClaudeCodeHarness` always returns `Ready`.
  - **Orchestrator integration tests**: Session fails with product-level validation error when harness returns `NotReady`. Session starts when harness returns `Ready`. Session starts when harness needs no credentials. Resume uses owner credentials. Orchestrator never inspects `LaunchArtifacts` contents.
  **Files**: `tests/WeaveFleet.Infrastructure.Tests/Harnesses/OpenCode/OpenCodeHarnessPreparationTests.cs`, `tests/WeaveFleet.Infrastructure.Tests/Harnesses/ClaudeCode/ClaudeCodeHarnessPreparationTests.cs`, `tests/WeaveFleet.Application.Tests/Services/SessionOrchestratorCredentialTests.cs`
  **Acceptance**: All tests pass. No test asserts harness-internal credential details from the orchestrator layer.

### Phase 4: Onboarding & UI (requires Phases 2-3 complete)

- [x] 25. Create onboarding status API
  **What**: Extend `GET /api/user/me` with `onboardingStatus`: `{ completed, hasStoredCredentials, hasCreatedSession }`. Track in `User.OnboardingCompletedAt`. Onboarding completion does NOT require stored credentials — a user can complete onboarding by dismissing/skipping the credential step if their default harness supports built-in access (determined by calling `harness.PrepareRuntimeAsync()` with an empty credential bag — if `Ready`, credentials are optional). `completed` is set when the user explicitly finishes the wizard, regardless of whether credentials were stored.
  **Files**: `src/WeaveFleet.Api/Endpoints/UserEndpoints.cs`, `src/WeaveFleet.Application/Services/UserService.cs`
  **Acceptance**: New users → `completed: false`. After completing wizard (with or without credentials) → `completed: true`. `hasStoredCredentials` is informational only — not a gate for completion.

- [x] 26. Create frontend credential settings page
  **What**: Generic credential management page. Credential cards: label, provider, type, connected/hint badge, add/remove. Value input is password field — values never displayed after storage. No hard-coded provider list — the UI is a generic credential bag. Provider/type fields can suggest common values but accept arbitrary values. **Terminology**: use "provider" and "API key" (capability-level), not "namespace", "environment variable", or other runtime terms. The frontend never references env vars, processes, or containers.
  **Files**: `client/src/app/settings/credentials/page.tsx`, `client/src/components/settings/credential-card.tsx`
  **Acceptance**: Users can add/remove/update credentials through UI. Multiple credentials supported. No runtime-mechanism terminology in UI.

- [x] 27. Create frontend onboarding wizard
  **What**: Full-screen wizard: Welcome → Connect API Keys (optional — skippable if the default harness supports built-in access, determined via onboarding status API or config endpoint) → Ready (CTA to start session). Shows when `onboardingStatus.completed === false`. Dismissible. Completing the wizard (including skipping credentials) marks onboarding done. **Terminology**: "Connect your API keys" / "Some tools include built-in access" — never "configure environment variables" or "runtime bootstrap".
  **Files**: `client/src/components/onboarding/onboarding-wizard.tsx`, `client/src/components/onboarding/credential-step.tsx`, `client/src/components/onboarding/welcome-step.tsx`, `client/src/components/onboarding/ready-step.tsx`
  **Acceptance**: New user sees wizard. Completing stores credentials (or skips) and marks onboarding done. Skip path works when harness supports built-in access.

- [x] 28. Simplify session creation UI for cloud mode
  **What**: Cloud mode "New Session" dialog: no directory picker. Show: Title, Harness type (default opencode), Initial prompt. Frontend detects cloud mode via `GET /api/config/client`. Server-side guard from Track 1 still enforces. If session creation returns a validation error (from harness preparation), show the product-level error message with guidance (e.g. "Add an API key in Settings → Credentials"). Frontend does not interpret error codes to determine what kind of credential is missing — it displays the harness-provided message verbatim.
  **Files**: `client/src/components/sessions/` (session creation dialog)
  **Acceptance**: Cloud: no directory input visible. Session creates with managed workspace. Harness validation errors displayed with guidance. Local: unchanged.

- [x] 29. Add login/logout UI chrome
  **What**: User avatar/email in header. Logout button. Graceful token expiry handling.
  **Files**: `client/src/components/layout/header.tsx`
  **Acceptance**: User sees identity. Can log out. Expired session redirects to login.

### Phase 5: Infrastructure Verification (requires Phases 1-4 and Track 1 complete)

- [x] 30. Verify end-to-end infrastructure
  **What**: Manual verification against all deployment contracts: access `https://<domain>`, redirect to IdP login, sign in, verify cookie session, verify API returns 200, verify health endpoints, verify SQLite on persistent disk, verify workspace root writable, verify Data Protection keys survive restart, verify hosted runtime confinement, verify credential store/retrieve cycle, verify harness preparation produces correct runtime behavior, verify session without credentials when harness supports built-in access.
  **Acceptance**: Full HTTPS → Auth → Credential → Session path working on live infrastructure.

## Dependencies

### This track depends on
- **Track 1 Phase 1** (TODOs 1-19 in Track 1): `IUserContext`, `FleetOptions.Auth/Cloud`, auth pipeline, `GET /api/user/me`, `appsettings.Cloud.json`, `GET /api/config/client`
- **Track 1 Phase 2** (TODOs 20-29 in Track 1): `UserId`-scoped repositories, managed workspace creation, `SessionOrchestrator` user-scoping (credential loading + harness preparation modifies orchestrator after Track 1 user-scoping changes land)

### Infrastructure parallelism
- Phase 1 of this track (TODOs 1-8) can begin **immediately** — no dependency on Track 1 code
- Phase 2 (TODOs 9-17: storage + API) requires Track 1 Phase 1 complete (`IUserContext`, auth pipeline)
- Phase 3 (TODOs 18-24: harness preparation + orchestrator integration) requires **Track 1 Phase 2** complete (`SessionOrchestrator` user-scoping must be landed before credential loading + harness preparation modifies orchestrator)
- Phase 4 (TODOs 25-29: onboarding + UI) requires Phases 2-3 complete
- Phase 5 (TODO 30) requires all phases of both tracks complete

### Tracks that depend on this
- **Track 3** depends on: `IHarness.PrepareRuntimeAsync()` for owner-credential enforcement on shared-session resume (Track 3 loads owner's credentials via the credential store and passes them to `PrepareRuntimeAsync()` — same generic pattern), onboarding completion state, and live infrastructure for E2E verification

## Verification

- [x] `dotnet build` succeeds with zero errors
- [x] `dotnet test` — all existing and new tests pass
- [ ] Cloud mode: store credential → start session → harness internally materializes credentials
- [ ] Cloud mode: multiple credentials with same namespace/kind → harness uses deterministic first-match (harness-internal concern, tested at harness level)
- [ ] Cloud mode: session starts without credentials when harness returns `Ready` with empty credential bag
- [ ] Cloud mode: session fails with product-level error when harness returns `NotReady`
- [ ] Cloud mode: `SessionOrchestrator` has zero references to credential namespaces, kinds, env var names, or materialization logic
- [ ] Cloud mode: credentials encrypted at rest
- [ ] Cloud mode: no plaintext credential values in API responses or logs
- [ ] Cloud mode: onboarding wizard works end-to-end (including skip path when credentials not required)
- [ ] Cloud mode: session creation UI hides directory picker
- [ ] Cloud mode: frontend uses only capability-level terminology (no env vars, processes, or containers)
- [ ] Infrastructure: HTTPS with valid TLS via reverse proxy
- [ ] Infrastructure: Data Protection keys survive restart
- [ ] Infrastructure: runtime confinement verified
- [ ] Infrastructure: deployment rollback documented
- [ ] Local mode: unchanged behavior — no regressions
- [ ] Architectural: grep `SessionOrchestrator.cs` for credential namespace/kind/env-var references → zero matches

## Risks

| Risk | Likelihood | Impact | Mitigation |
|------|-----------|--------|------------|
| Data Protection key loss | Low | Critical | Keys on persistent disk. Include in backup strategy. Document recovery procedure. |
| Harness binary compatibility on Linux | Low | Medium | Test on target OS during TODO 2. Pin known-good version. |
| Disk space from managed workspaces | Low | Medium | Scratch workspaces are small in MVP. Add cleanup cron as fast-follow. |
| Credential loading timing — orchestrator modified by both tracks | Medium | Medium | Track 1 adds user-scoping first (Phase 2); this track adds credential loading + harness preparation second (Phase 3). Clear merge order enforced by dependency sequencing. |
| First-match ambiguity with multiple credentials | Low | Low | MVP uses deterministic first-match (by creation order) inside the harness. Post-MVP: add credential selection in session creation UI. |
| Model ID not always known at session creation time | Medium | Low | Harness treats null/unknown model as "no model-specific requirements" — falls back to harness-level defaults. Session proceeds; if the harness itself fails due to missing auth, the error surfaces at runtime (same as local mode). |
| IHarness interface expansion adds method to all implementations | Low | Low | Only 3 implementations (OpenCode, ClaudeCode, TestHarness). ClaudeCode and TestHarness have trivial `Ready` implementations. The pattern mirrors existing `CheckAvailabilityAsync`. |
