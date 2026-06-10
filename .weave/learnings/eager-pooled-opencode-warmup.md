# Eager Pooled OpenCode Warmup — Learnings

## Auth-Enabled Smoke Assessment (Task 13/27)

**Date**: 2026-06-10
**Executed by**: Shuttle
**Result**: BLOCKED — live auth-enabled Fleet server not feasible; scripted test equivalents all pass. Checkbox remains `[ ]`.

---

### What the smoke requires

Plan line 85 specifies two scenarios:

1. **Auth-enabled startup no-op**: Start Fleet with `Fleet__Auth__Enabled=true`, confirm startup warmup emits the skip log line and no OpenCode process is spawned.
2. **Two-user warmup partition isolation**: Authenticate as two distinct users with identical credentials and confirm each warmup call routes to a separate pool partition (separate composite key = separate process).

---

### Blocker: OIDC provider required for live auth-enabled Fleet

Running Fleet with `Fleet__Auth__Enabled=true` requires valid OIDC configuration:

- `Fleet__Auth__Authority` — a reachable issuer URL (e.g. Clerk JWKS endpoint)
- `Fleet__Auth__ClientId` — a registered OIDC client
- `Fleet__Auth__ClientSecret` — the corresponding secret

Without these, the OIDC middleware throws on startup (`IDX20803: Unable to obtain configuration from...`). No stub OIDC server is wired up in the repo (checked `aspire/AppHost/Program.cs`, `scripts/`, all test infrastructure). No in-process mock OIDC server exists that would let the app boot and issue real tokens from two user identities.

The `ApiWebApplicationFactory` test harness supports auth-enabled mode by substituting the entire authentication stack with a `TestAuthHandler` or `UnauthorizedAuthHandler` — but this only produces a single fixed test-user identity (`sub=test-user`). Switching identity within one factory instance requires rebuilding the factory with a different `TestAuthHandler`, which ties the test to xUnit process isolation and is not a live-server smoke.

---

### Scripted test equivalents executed — all pass

The two smoke scenarios are fully covered by the existing automated test suite:

**Scenario 1 — Auth-enabled startup no-op:**

| Test | Result |
|------|--------|
| `OpenCodeWarmupHostedServiceTests.auth_enabled_startup_skips_warmup_without_calling_runtime` | ✅ PASS |
| `OpenCodeWarmupHostedServiceTests.auth_enabled_startup_skip_does_not_throw` | ✅ PASS |
| `OpenCodeWarmupHostedServiceTests.auth_enabled_startup_does_not_query_harness_registry_for_runtime` | ✅ PASS |
| `HarnessEndpointsTests.warmup_opencode_auth_enabled_no_user_context_is_no_op` | ✅ PASS |
| `HarnessEndpointsTests.warmup_opencode_auth_enabled_anonymous_request_returns_unauthorized_without_warmup` | ✅ PASS |

Commands run:
```
dotnet test tests/WeaveFleet.Infrastructure.Tests/ --filter "FullyQualifiedName~OpenCodeWarmupHostedServiceTests"
# → Passed: 11, Failed: 0

dotnet test tests/WeaveFleet.Api.Tests/ --filter "FullyQualifiedName~HarnessEndpointsTests"
# → Passed: 2, Failed: 0 (warmup_opencode_auth_enabled_* tests included)
```

**Scenario 2 — Two authenticated users with identical credentials use separate pool partitions:**

| Test | Result |
|------|--------|
| `PooledHarnessIsolationTests.different_authenticated_users_identical_credentials_do_not_share_pooled_process` | ✅ PASS |
| `PooledHarnessIsolationTests.different_users_identical_credentials_resume_on_separate_processes` | ✅ PASS |
| `PooledHarnessIsolationTests.local_mode_all_sessions_share_single_pool_partition_per_credential_hash` | ✅ PASS |

Command run:
```
dotnet test tests/WeaveFleet.IntegrationTests/ --filter "FullyQualifiedName~PooledHarnessIsolationTests"
# → Passed: 16, Failed: 0 (all isolation scenarios including two-user-same-credentials)
```

---

### Why the checkbox is left unchecked

The plan states: *"Do not mark unless actually executed."* The live interactive steps (start a real Fleet server in auth-enabled mode, call the warmup API with two real browser sessions as two different OIDC-authenticated users) cannot be executed because:

- No OIDC provider or stub is available in the dev environment.
- The `ApiWebApplicationFactory` test auth scheme is not a live Fleet server.

The *behavior* is verified exhaustively by the automated test matrix above. If a real OIDC environment (e.g. Clerk dev tenant or Keycloak container) is available in CI or a staging environment, the live smoke can be executed by:

```bash
# Startup no-op check
Fleet__Auth__Enabled=true \
Fleet__Auth__Authority="https://your-oidc-issuer/.well-known/openid-configuration" \
Fleet__Auth__ClientId="..." \
Fleet__Auth__ClientSecret="..." \
Fleet__DatabasePath="/tmp/smoke-auth.db" \
Fleet__AnalyticsEnabled=false \
Fleet__Port=19999 \
Fleet__Harness__PooledOpenCodeHarness=true \
  dotnet run --project src/WeaveFleet.Api/WeaveFleet.Api.csproj &

# Expected in logs:
# "OpenCode startup warmup skipped: auth-enabled mode has no trusted user context at startup."
# pool health should show instanceCount: 0

# Two-user warmup (requires two valid OIDC tokens for distinct subjects):
curl -X POST http://localhost:19999/api/harnesses/opencode/warmup \
  -H "Cookie: .WeaveFleet.Auth=<user-alice-session>" \
  -H "X-CSRF-Token: <csrf>"
curl -X POST http://localhost:19999/api/harnesses/opencode/warmup \
  -H "Cookie: .WeaveFleet.Auth=<user-bob-session>" \
  -H "X-CSRF-Token: <csrf>"

# Expected: admin pool shows instanceCount: 2, two distinct partitionFingerprints
```

---

### Key findings

1. **`Fleet__Auth__Enabled=true` is the only config gate for startup no-op** — `OpenCodeWarmupHostedService.StartAsync` reads `_options.Auth.Enabled` directly; no other condition is checked before the early return.
2. **Pool key is `ownerIdentity\ncredentialHash`** — confirmed in `PooledOpenCodeInstanceRegistry.BuildCompositeKey`. Two users with the same credential environment produce the same `credentialHash` but different `ownerIdentity`, so the composite key differs and they cannot share a partition.
3. **`ClaimsUserContext` is the runtime user context in auth-enabled mode** — it derives `UserId` from the `sub` claim on the request principal. With no request (startup), there is no principal, and startup warmup correctly skips rather than throwing.

---

## Manual Smoke Test Execution (Task 12/27)

**Date**: 2026-06-10
**Executed by**: Shuttle (automated via safe scripted smoke)
**Result**: PASS — checkbox marked `[x]` in plan line 84

---

### Smoke Test Steps and Evidence

The smoke was run against an ephemeral Fleet instance with:
- `Fleet__Harness__PooledOpenCodeHarness=true`
- `Fleet__Harness__PooledOpenCodeIdleTtlSeconds=60`
- Isolated SQLite DB (no shared state with production `~/Library/Application Support/WeaveFleet/fleet.db`)
- Port 19998 (not the default 3000)
- `opencode` binary at `/Users/pgermishuys/.opencode/bin/opencode` — version 1.17.0

**Step 1 — Fleet started, startup warmup fired**

Log evidence:
```
[PooledOpenCodeInstanceRegistry] Acquiring pooled OpenCode instance for key fingerprint 4fa14df9db23.
[PooledOpenCodeInstanceRegistry] Spawning pooled OpenCode process for key fingerprint 4fa14df9db23; reason: initial_acquire.
[OpenCodeProcessManager] opencode process started: http://127.0.0.1:4096/
[PooledOpenCodeInstanceRegistry] Pooled OpenCode ref-count changed for key fingerprint 4fa14df9db23: 0 -> 1; reason: acquire.
[PooledOpenCodeInstanceRegistry] Releasing pooled OpenCode instance for key fingerprint 4fa14df9db23; mode: IdleTtl.
[PooledOpenCodeInstanceRegistry] Pooled OpenCode ref-count changed for key fingerprint 4fa14df9db23: 1 -> 0; reason: release.
[PooledOpenCodeInstanceRegistry] Scheduled pooled OpenCode idle TTL for key fingerprint 4fa14df9db23; ttl_ms: 60000.
[OpenCodeWarmupHostedService] OpenCode startup warmup completed: pooled process pre-warmed for local owner.
```

**Step 2 — Admin pool health before first prompt**

`GET /api/admin/opencode/pool` response:
```json
{
  "instanceCount": 1,
  "sessionCount": 0,
  "warmCount": 1,
  "activeCount": 0,
  "instances": [{
    "instanceId": "opencode-pool-adba4f758ce5473ca1511bfa53756e8f",
    "sessionCount": 0,
    "processId": 29890,
    "isAvailable": true,
    "isFaulted": false,
    "isDisposed": false,
    "partitionFingerprint": "4fa14df9db23",
    "isWarm": true
  }]
}
```

Confirms: `warmCount: 1`, `isWarm: true`, one warm `local-user` partition before any prompt.

**Step 3 — Create session**

`POST /api/sessions` with `harnessType: "opencode"` and a valid workspace directory:
```json
{
  "session": {
    "id": "afc43094-9578-47e6-99a8-3c67c05d51ba",
    "harnessType": "opencode",
    "runtimeMode": "automatic",
    "harnessResumeToken": "ses_14e11cd61ffe74y8oNiCQnAw0j",
    ...
  }
}
```

`harnessResumeToken` is present immediately on the initial insert.

**Step 4 — Pool health after session creation**

Pool transitions: `warmCount: 0`, `activeCount: 1` — same instance (same PID 29890), now actively leased.

**Step 5 — DB verification**

```sql
SELECT id, harness_type, runtime_mode, harness_resume_token, lifecycle_status FROM sessions ORDER BY created_at DESC LIMIT 1;
-- afc43094-...|opencode|automatic|ses_14e11cd61ffe74y8oNiCQnAw0j|
```

`harness_resume_token` = `ses_14e11cd61ffe74y8oNiCQnAw0j` persisted on initial row insert.

---

### Key Findings

1. **Warmup proceeds without stored API credentials** — `WarmupPooledInstanceAsync` calls `PrepareRuntimeAsync` with `ModelId = null`, which calls `ResolveRequirements(null)` → returns `[]` (no credential requirements), so `RuntimePreparation.Ready` is returned with an empty env dict. This means the pooled process warms with no API key environment variables. The process starts successfully because opencode does not require credentials for mere startup (the HTTP server comes up regardless).

2. **`PooledOpenCodeHarness` preference is checked by user ID** — The feature flag provider queries the `user_preferences` table filtered by user ID. On a fresh smoke DB, this returns the default from `FleetOptions.Harness.PooledOpenCodeHarness` (which we set to `true` via env var). The user preference DB value overrides the config value when present.

3. **Warmup is genuinely best-effort** — If the opencode binary is missing or fails to start, the exception is caught by `OpenCodeWarmupHostedService.StartAsync`, logged as Warning, and app boot continues normally. Only the background opencode process would be absent; no health check endpoint fails.

4. **Idle TTL controls process lifetime** — After the lease is immediately released by warmup, the idle-TTL timer starts. With `PooledOpenCodeIdleTtlSeconds=60`, the opencode process stays alive for 60 seconds without an active session, then the registry cleanly disposes it.

5. **Safe scripted execution** — The full smoke can be run without interactive steps using: start Fleet in background, poll `/readyz`, call `/api/admin/opencode/pool` and `/api/sessions`, kill Fleet, verify no lingering processes.

---

### Commands Used

```bash
# Start Fleet with pooled mode on ephemeral port/DB
Fleet__DatabasePath="$SMOKE_DB" \
Fleet__AnalyticsEnabled="false" \
Fleet__Port="19998" \
Fleet__Harness__PooledOpenCodeHarness="true" \
Fleet__Harness__PooledOpenCodeIdleTtlSeconds="60" \
  dotnet run --project src/WeaveFleet.Api/WeaveFleet.Api.csproj --no-build &

# Wait for readyz
curl -sf "http://localhost:19998/readyz"

# Check pool health
curl -sf "http://localhost:19998/api/admin/opencode/pool" \
  -H "Authorization: Bearer $TOKEN"

# Seed workspace root
curl -sf -X POST "http://localhost:19998/api/workspace-roots" \
  -H "Authorization: Bearer $TOKEN" \
  -H "Content-Type: application/json" \
  -d '{"path": "/tmp/smoke-workdir"}'

# Create session
curl -sf -X POST "http://localhost:19998/api/sessions" \
  -H "Authorization: Bearer $TOKEN" \
  -H "Content-Type: application/json" \
  -d '{"title":"Smoke","directory":"/tmp/smoke-workdir","harnessType":"opencode"}'

# Verify in DB
sqlite3 "$SMOKE_DB" \
  "SELECT id, harness_type, runtime_mode, harness_resume_token FROM sessions ORDER BY created_at DESC LIMIT 1"

# Kill Fleet
kill $FLEET_PID
```
