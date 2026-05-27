# Pooled OpenCode Harness Rollout

Pooled OpenCode is opt-in. Production and cloud config set `Fleet:Harness:PooledOpenCodeHarness` to `false`, preserving one OpenCode process per Fleet session unless explicitly enabled.

## Enable

1. Set `Fleet:Harness:PooledOpenCodeHarness=true` by config/env var (`Fleet__Harness__PooledOpenCodeHarness=true`) or via `/api/config`.
2. Optionally tune `Fleet:Harness:PooledOpenCodeIdleTtlSeconds` (default `60`).
3. Restart if changing static deployment config; runtime `/api/config` changes affect new sessions only.

User preference must also be enabled, so global opt-in alone does not force pooling for users that have disabled it.

## Monitor

- Admin endpoint: `GET /api/admin/opencode-pool/health` (alias: `GET /api/admin/opencode/pool`)
  - Local mode: allowed for the local operator.
  - Auth/cloud mode: requires an admin claim (`role=admin`, `roles` containing `admin`, `fleet_admin=true`, or role membership `admin`). Non-admin callers receive `403`.
- Response includes `instanceCount`, `sessionCount`, and per-instance `instanceId`, `sessionCount`, `processId`, `isAvailable`, `isFaulted`, `isDisposed`.
- Metrics: `opencode_pool_instances_active`, `opencode_pool_sessions_per_instance`, `opencode_pool_process_restarts`.
- Logs: pool acquire/release, process spawn/kill, TTL expiry, crash recovery, and credential boundary decisions.

## Rollback

1. Set `Fleet:Harness:PooledOpenCodeHarness=false`.
2. Existing pooled sessions continue until stopped/resumed; new sessions use non-pooled behavior.
3. Stop active pooled sessions or restart the API to dispose the pool immediately.
4. Verify `/api/admin/opencode/pool` reports `instanceCount=0` after active pooled sessions stop and idle TTL expires.
