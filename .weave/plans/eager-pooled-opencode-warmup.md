# Eager Pooled OpenCode Warmup

## TL;DR
> **Summary**: Tighten the eager pooled OpenCode warmup plan so pool isolation is explicitly keyed by user/tenant plus credential environment hash, runtime warmup is server-authenticated and trust-derived, and eager create follows a strict create/rollback sequence that never leaks or persists stale tokens.
> **Estimated Effort**: Large

## Context
### Original Request
Revise `.weave/plans/eager-pooled-opencode-warmup.md` to address a Warp BLOCK covering three gaps: explicit pool partitioning by user/tenant plus env credential hash, a locked-down runtime warmup authz model, and exact eager-create ordering plus rollback behavior and tests.

### Key Findings
1. `PooledOpenCodeInstanceRegistry` is currently documented and keyed around a credential/env hash, not an explicit user/tenant + env credential hash composite, so the plan must call out that tenant identity is part of the authoritative pool key even when two users resolve identical environment dictionaries.
2. `LocalUserContext` always resolves to `local-user` when auth is disabled; that is the only valid exception where per-user pool partitioning collapses to a single effective local owner.
3. `ClaimsUserContext` throws when no authenticated user exists, which means auth-enabled startup warmup cannot safely infer a user and must no-op until a request-scoped, server-authenticated user context is present.
4. `SessionOrchestrator.CreateSessionAsync()` already resolves credentials server-side, creates/canonicalizes the workspace directory, spawns the harness, registers the instance, then persists the Fleet session; the revised plan must make the eager pooled path follow an even stricter order around owner resolution, directory authorization, pooled lease/session creation, DB persist, and cleanup on failure.
5. `OpenCodeHarnessRuntime.ResolvePooledSessionMapping()` already guards resume mappings by `OwnerUserId`, and `ResolveOpenCodeSessionIdAsync()` recreates a missing OpenCode session after crash/restart, so the plan should add stale-token recovery tests rather than invent a second recovery path.
6. Existing endpoint patterns (`HarnessEndpoints`, `AdminEndpoints`, `ConfigEndpoints`) and hosted startup service patterns (`LegacySessionImportStartupService`) give clear homes for a runtime warmup API, startup warmup hosted service, and API tests for auth/no-auth behavior.

## Objectives
### Core Objective
Make pooled OpenCode feel hot by default without weakening tenancy, authz, directory safety, or recovery guarantees.

### Deliverables
- [x] Explicit pool partitioning design keyed by authoritative user/tenant identity plus env credential hash, with the local-mode `local-user` exception documented and tested
- [x] Runtime warmup API plan that is server-authenticated, derives acting user from server auth context, accepts no caller-controlled owner/hash/token/directory fields, and no-ops until preferences and credentials are loaded server-side
- [x] Eager create plan with strict ordering, rollback/cleanup rules, and regression tests for DB failure, ownership failure, and stale-token crash recovery

### Definition of Done
- [x] Plan tasks specify the exact create order `resolve owner -> canonicalize/authorize directory -> acquire lease/create OpenCode session -> persist Fleet session`
- [x] Verification includes focused commands for pool isolation, runtime warmup authz, eager-create rollback, and full solution tests
- [x] The plan explicitly states that resume tokens must not be caller-supplied to warmup APIs, must not be logged, and must not survive failed create paths unless Fleet session persistence succeeds

### Guardrails (Must NOT)
- [x] Must NOT share pooled processes or token state across different users/tenants, even when their resolved env credential hashes are identical
- [x] Must NOT allow runtime warmup callers to supply credential hashes, owner IDs, resume tokens, or workspace directories
- [x] Must NOT perform auth-enabled startup warmup when no authenticated server-side user context exists
- [x] Must NOT persist a Fleet session row before directory authorization and eager OpenCode session creation succeed
- [x] Must NOT leave pooled bindings/leases behind after eager-create failures
- [x] Must NOT log secrets, credential material, or resume tokens
- [x] Must NOT change non-pooled behavior when pooled OpenCode remains disabled

## TODOs

- [x] 1. Re-key pooled instance partitioning to tenant + env credential hash
  **What**: Update the design so the authoritative pooled key is a composite of owner user/tenant identity and the authoritative resolved credential environment hash, not the env hash alone. Document the only exception: when auth is disabled, the effective owner is the deterministic `local-user`, so local mode still produces one pool partition per credential hash under that single local owner. Call out that pooled process reuse and any in-memory token/mapping reuse both follow this same composite boundary.
  **Files**: `src/WeaveFleet.Infrastructure/Harnesses/OpenCode/Pooling/PooledOpenCodeInstanceRegistry.cs`, `src/WeaveFleet.Infrastructure/Harnesses/OpenCode/OpenCodeHarnessRuntime.cs`, `src/WeaveFleet.Infrastructure/Harnesses/OpenCode/Pooling/PooledOpenCodeInstance.cs`, `tests/WeaveFleet.Infrastructure.Tests/Harnesses/OpenCode/Pooling/PooledOpenCodeInstanceRegistryTests.cs`, `tests/WeaveFleet.IntegrationTests/Harnesses/OpenCode/PooledHarnessIsolationTests.cs`
  **Acceptance**: Tests prove two different authenticated users with identical resolved env hashes do not reuse the same pooled process, lease bucket, or token state; local-mode tests prove the intentional `local-user` exception is the only collapse case.

- [x] 2. Add runtime warmup API with an explicit server-trust model
  **What**: Add a dedicated runtime warmup endpoint/service path for pooled OpenCode that uses the server-authenticated `IUserContext`/request principal to derive the acting user and loads preferences plus decrypted credentials on the server before attempting warmup. The API contract must accept no caller-supplied credential hash, owner ID, resume token, or workspace directory. In auth-enabled mode, requests with no authenticated user context must return the defined no-op/unauthorized behavior instead of attempting warmup. In local mode, the endpoint may resolve to `local-user` via server-side context only.
  **Files**: `src/WeaveFleet.Api/Endpoints/HarnessEndpoints.cs`, `src/WeaveFleet.Infrastructure/Harnesses/OpenCode/OpenCodeHarnessRuntime.cs`, `src/WeaveFleet.Infrastructure/Harnesses/OpenCode/OpenCodeFeatureFlagProvider.cs`, `src/WeaveFleet.Application/Services/ICredentialStore.cs`, `tests/WeaveFleet.Api.Tests/Endpoints/HarnessEndpointsTests.cs`, `tests/WeaveFleet.Infrastructure.Tests/Harnesses/OpenCode/OpenCodeHarnessTests.cs`
  **Acceptance**: Tests cover authenticated warmup success, anonymous auth-enabled requests, auth-enabled startup/no-user-context no-op, and the absence of any caller-controlled owner/hash/token/directory parameters in the API contract.

- [x] 3. Add best-effort startup warmup with auth-enabled no-user-context skip
  **What**: Keep startup warmup best-effort and local-mode only. The hosted service should run after startup recovery, resolve the deterministic local owner server-side, load preferences/credentials server-side, and warm only the pooled process. In auth-enabled mode, it must no-op because there is no trusted acting user at startup. The task should explicitly connect startup warmup and runtime warmup so post-login/post-preference warmup covers hosted/cloud scenarios.
  **Files**: `src/WeaveFleet.Infrastructure/Services/OpenCodeWarmupHostedService.cs`, `src/WeaveFleet.Infrastructure/DependencyInjection.cs`, `tests/WeaveFleet.Infrastructure.Tests/Services/OpenCodeWarmupHostedServiceTests.cs`
  **Acceptance**: Tests prove local startup warms once when eligible, auth-enabled startup with no user context skips cleanly, and startup failures do not block app boot.

- [x] 4. Enforce eager create ordering before Fleet session persistence
  **What**: Refine the create-session plan so the eager pooled path follows this exact sequence: (a) resolve authoritative owner user, (b) canonicalize and authorize the requested workspace directory against allowed roots/ownership rules, (c) acquire the pooled lease and create the OpenCode session for that exact directory, including binding-table registration, then (d) persist the Fleet instance/session rows with the eager resume token. Make the task explicit that warm scratch directories are never reused as real workspace directories and that ownership/directory validation failures happen before any lease or OpenCode session creation.
  **Files**: `src/WeaveFleet.Application/Services/SessionOrchestrator.cs`, `src/WeaveFleet.Application/Services/WorkspaceRootService.cs`, `src/WeaveFleet.Infrastructure/Harnesses/OpenCode/OpenCodeHarnessRuntime.cs`, `src/WeaveFleet.Infrastructure/Harnesses/OpenCode/OpenCodeHarnessSession.cs`, `src/WeaveFleet.Infrastructure/Harnesses/OpenCode/Pooling/LeasedInstanceHandle.cs`, `src/WeaveFleet.Infrastructure/Harnesses/OpenCode/Pooling/PoolDemuxBindingTable.cs`, `tests/WeaveFleet.Application.Tests/Services/SessionOrchestratorTests.cs`, `tests/WeaveFleet.IntegrationTests/Sessions/AutoActivationTests.cs`
  **Acceptance**: The implementation plan and tests assert the exact ordering above, reject unauthorized/out-of-root directories before pool acquisition, and persist `HarnessResumeToken` on the initial Fleet session insert only after eager OpenCode session creation succeeds.

- [x] 5. Define rollback and crash-recovery cleanup for post-session-create failures
  **What**: Specify the failure path after an OpenCode session has been created but before Fleet session persistence completes: perform best-effort delete of the OpenCode session, remove any binding-table association, release the pooled lease, and avoid persisting or logging the token. Reuse the existing resume/recreate machinery for crash recovery, but add explicit stale-token tests showing that after a crash/restart Fleet does not keep reusing a dead token and instead rehydrates to a fresh live OpenCode session mapping.
  **Files**: `src/WeaveFleet.Infrastructure/Harnesses/OpenCode/OpenCodeHarnessRuntime.cs`, `src/WeaveFleet.Infrastructure/Harnesses/OpenCode/OpenCodeHarnessSession.cs`, `src/WeaveFleet.Infrastructure/Harnesses/OpenCode/Pooling/LeasedInstanceHandle.cs`, `src/WeaveFleet.Infrastructure/Harnesses/OpenCode/Pooling/PoolDemuxBindingTable.cs`, `tests/WeaveFleet.Infrastructure.Tests/Harnesses/OpenCode/OpenCodeHarnessTests.cs`, `tests/WeaveFleet.Application.Tests/Services/SessionOrchestratorTests.cs`, `tests/WeaveFleet.IntegrationTests/Harnesses/OpenCode/PooledHarnessIsolationTests.cs`, `tests/WeaveFleet.IntegrationTests/Sessions/AutoActivationTests.cs`
  **Acceptance**: Tests cover DB insert failure after OpenCode session creation, ownership/directory validation failure before OpenCode session creation, and stale-token reuse prevention after crash/recovery; logs/assertions confirm no resume token is emitted in failure telemetry.

- [x] 6. Update observability and admin diagnostics without leaking trust inputs
  **What**: Extend admin-only pool diagnostics and logs so operators can distinguish partitions by safe fingerprints, warm-held vs actively leased instances, warmup skips due to no trusted user context, eager-create rollback cleanup, and stale-token crash recovery. Keep user identifiers and credential hashes redacted/fingerprinted, and never expose resume tokens.
  **Files**: `src/WeaveFleet.Infrastructure/Harnesses/OpenCode/Pooling/PoolHealthCheck.cs`, `src/WeaveFleet.Api/Endpoints/AdminEndpoints.cs`, `src/WeaveFleet.Api/JsonContext.cs`, `tests/WeaveFleet.Api.Tests/Endpoints/AdminEndpointsTests.cs`, `tests/WeaveFleet.Infrastructure.Tests/Harnesses/OpenCode/Pooling/PooledOpenCodeInstanceRegistryTests.cs`
  **Acceptance**: Admin diagnostics expose safe partition fingerprints and warm/active counts, while tests confirm non-admin callers cannot access diagnostics and failure/warmup logs do not include tokens or raw credential material.

- [x] 7. Run focused verification matrix before rollout
  **What**: Execute the smallest useful verification matrix first (pool partitioning, warmup API authz, eager-create rollback/crash recovery), then the broader solution suites. Capture any gaps between unit, API, and integration coverage before rollout.
  **Acceptance**: All focused test commands pass before the full solution run, and each Warp BLOCK requirement is mapped to at least one named test suite.

## Verification
- [x] `dotnet test tests/WeaveFleet.Infrastructure.Tests/WeaveFleet.Infrastructure.Tests.csproj --filter "FullyQualifiedName~PooledOpenCodeInstanceRegistryTests|FullyQualifiedName~OpenCodeHarnessTests|FullyQualifiedName~OpenCodeWarmupHostedServiceTests"`
- [x] `dotnet test tests/WeaveFleet.Api.Tests/WeaveFleet.Api.Tests.csproj --filter "FullyQualifiedName~HarnessEndpointsTests|FullyQualifiedName~AdminEndpointsTests"`
- [x] `dotnet test tests/WeaveFleet.Application.Tests/WeaveFleet.Application.Tests.csproj --filter "FullyQualifiedName~SessionOrchestratorTests"`
- [x] `dotnet test tests/WeaveFleet.IntegrationTests/WeaveFleet.IntegrationTests.csproj --filter "FullyQualifiedName~PooledHarnessIsolationTests|FullyQualifiedName~AutoActivationTests"`
- [x] `dotnet test WeaveFleet.slnx`
- [x] Manual smoke: enable pooled OpenCode in local mode, start Fleet, confirm admin pool health shows one warm local-user partition before first prompt, then create a session and verify `harness_resume_token` exists immediately
- [x] Manual smoke: in auth-enabled mode, start Fleet without an authenticated request context and confirm startup warmup no-ops; then authenticate as two distinct users with equivalent credentials and confirm each warms/creates against separate pool partitions _(blocked for live execution: no reachable OIDC provider/stub in repo; scripted equivalents passed and blocker documented in learnings)_
