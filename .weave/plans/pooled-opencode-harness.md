# Pooled OpenCode Harness

## TL;DR
> **Summary**: Replace the 1:1 process-per-session model with a pooled `opencode serve` harness that shares processes across Fleet sessions while preserving full session isolation, lazy-started via a feature flag in settings.
> **Estimated Effort**: Large

## Context
### Original Request
Design a pooled OpenCode harness that shares `opencode serve` processes while preserving Fleet session isolation, with lazy start, feature flag, and no frontend API changes.

### Key Findings
1. OpenCode API supports `?directory=` scoping — sessions, events, commands are directory-aware.
2. Current model: `OpenCodeHarnessRuntime.SpawnAsync()` → 1 `OpenCodeProcessManager` + 1 `OpenCodeHttpClient` per Fleet session.
3. SSE stream behavior was verified against local OpenCode `1.15.10`: `GET /event?directory=X` is directory-scoped. `GET /event` only emitted server/control events during probing and did not deliver session events for directory-created sessions. Session events carry `properties.sessionID` plus `properties.info.directory`.
4. Env vars (API keys) are process-wide — sharing only safe within same credential boundary.
5. Frontend uses Fleet Session ID exclusively (`/api/sessions/{sessionId}/...`); no OpenCode session ID or server URL leaks to client.
6. Slash commands route: `SessionOrchestrator.CommandSessionAsync` → `InstanceTracker` → `IHarnessSession.SendCommandAsync` → `OpenCodeHttpClient.SendCommandAsync`. Currently isolated by process; pooled mode must route by OpenCode session ID + directory.
7. Prior feasibility plan exists at `shared-opencode-process-multiplexing.md` — this plan supersedes it with execution detail.

## Objectives
### Core Objective
Share `opencode serve` processes across Fleet sessions (same credential boundary) with zero frontend changes, lazy initialization, and full session isolation.

### Deliverables
- [ ] `PooledOpenCodeInstanceRegistry` — thread-safe singleton managing shared process lifecycle
- [ ] `SseEventDemultiplexer` — routes per-directory SSE streams to N session consumers by sessionId
- [ ] Refactored `OpenCodeHarnessSession` — lease-based, not ownership-based
- [ ] Refactored `OpenCodeHarnessRuntime` — registry-aware spawn/resume with lazy start
- [ ] Feature flag (`PooledOpenCodeHarness`) gating the new path
- [ ] Settings UI toggle for enabling pooled mode
- [ ] Integration + unit tests covering isolation, lifecycle, crash recovery
- [ ] Telemetry/audit logging for pool operations

### Definition of Done
- [ ] `dotnet test` passes all existing + new tests
- [ ] Two Fleet sessions for same user share one OS process (verified by PID in logs)
- [ ] Stopping one session does NOT kill shared process
- [ ] Stopping last session kills process after idle TTL
- [ ] SSE events never leak across Fleet sessions
- [ ] Slash commands route to correct OpenCode session
- [ ] Feature flag OFF → existing per-session behavior unchanged
- [ ] Frontend makes zero changes to session API calls

### Guardrails (Must NOT)
- Must NOT change frontend REST/WS API shape (paths, payloads, topic names)
- Must NOT share processes across different credential sets
- Must NOT break container-mode or cloud-hosted deployment
- Must NOT introduce global SPOF (one crash ≠ all sessions dead without recovery)
- Must NOT expose OpenCode session ID or server URL to frontend
- Must NOT forward SSE events that lack a parseable/bound OpenCode session ID to any Fleet session consumer
- Must NOT retain decrypted credential values in memory beyond transient hash computation
- Must NOT expose pool diagnostics (PIDs, counts) to non-admin users

## TODOs

### Phase 1: Foundation — Feature Flag & Registry (Week 1)

- [x] 1. Add feature flag `PooledOpenCodeHarness`
  **What**: Add a feature flag (settings-driven bool) that gates pooled vs per-session mode. When OFF, all existing code paths remain unchanged. Add to `HarnessSettings` or equivalent config section.
  **Files**: `src/WeaveFleet.Infrastructure/Harnesses/OpenCode/OpenCodeHarness.cs`, `src/WeaveFleet.Domain/Settings/HarnessSettings.cs` (or equivalent)
  **Acceptance**: Flag readable at runtime; defaults to OFF; togglable via settings API

- [x] 2. Codify verified SSE event scoping
  **What**: Add an integration regression test for the verified OpenCode `1.15.10` behavior: create sessions in two directories, subscribe to `GET /event?directory=dirA` and `GET /event?directory=dirB`, and assert session events are emitted only on the matching directory stream. Also subscribe to `GET /event` and assert it is not relied on for session events. Document that pooled mode must maintain one SSE subscription per active directory per pooled process, not one global process stream.
  **Files**: `tests/WeaveFleet.IntegrationTests/Harnesses/OpenCode/SseEventScopingTests.cs`
  **Acceptance**: Test passes; implementation guidance states demux uses per-directory streams plus `properties.sessionID` filtering; global `/event` is never used as the sole event source for pooled sessions

- [x] 3. Implement `PooledOpenCodeInstanceRegistry`
  **What**: Thread-safe singleton (`ConcurrentDictionary` keyed by credential-hash string). Responsibilities: (a) acquire lease → spawn if needed, increment refcount, return instance handle; (b) release lease → decrement refcount, schedule idle TTL shutdown; (c) handle process crash → notify all lessees, attempt restart. Use `SemaphoreSlim` per key to serialize spawn decisions.
  **Files**: `src/WeaveFleet.Infrastructure/Harnesses/OpenCode/Pooling/PooledOpenCodeInstanceRegistry.cs`, `src/WeaveFleet.Infrastructure/Harnesses/OpenCode/Pooling/PooledOpenCodeInstance.cs`, `src/WeaveFleet.Infrastructure/Harnesses/OpenCode/Pooling/InstanceLease.cs`
  **Acceptance**: Unit tests: concurrent acquire returns same instance; release to zero + TTL triggers shutdown; crash propagates to all lessees

- [x] 4. Implement `SseEventDemultiplexer`
  **What**: Subscribes to one SSE stream per active `(PooledOpenCodeInstance, directory)` pair using `GET /event?directory=X`. Multiple Fleet sessions in the same directory share that directory stream; different directories get separate streams on the same process. Parses events, extracts OpenCode session ID from `properties.sessionID`, and routes to registered `Channel<OpenCodeSseEvent>` per Fleet session consumer. Handles reconnection on stream drop. Consumers register/unregister dynamically; directory stream shuts down when last binding for that directory is removed. **Security**: Events without a parseable OpenCode session ID or without a matching entry in the binding table MUST be dropped — never forwarded to any Fleet session consumer. Unattributable events are logged and metriced for admin diagnostics only.
  **Files**: `src/WeaveFleet.Infrastructure/Harnesses/OpenCode/Pooling/SseEventDemultiplexer.cs`
  **Acceptance**: Unit test: 3 consumers registered across 2 directories, directory streams are created/ref-counted correctly, events with different sessionIds route correctly; unregistered consumer receives nothing; stream reconnect doesn't lose events; events without session ID are dropped and metriced, never forwarded

- [x] 4b. Implement `PoolDemuxBindingTable`
  **What**: Maintain an authoritative binding table mapping OpenCode session ID → (Fleet session ID, user ID, directory, lease generation). On every inbound SSE event, look up the binding table — route only if the event's OC session ID matches a registered live binding with correct lease generation and the event arrived on the expected directory stream. Reject/drop unknown, stale (old generation), directory-mismatched, or otherwise mismatched events. Bindings are added after OpenCode session creation/resume, removed at lease release, and generation-bumped on resume.
  **Files**: `src/WeaveFleet.Infrastructure/Harnesses/OpenCode/Pooling/PoolDemuxBindingTable.cs`
  **Acceptance**: Unit test: event with unknown OC session ID → dropped; event with stale generation → dropped; event with valid binding → routed; binding removed after release → subsequent events dropped

### Phase 2: Session Refactor — Lease Model (Week 2)

- [x] 5. Extract process ownership from `OpenCodeHarnessSession`
  **What**: Currently `OpenCodeHarnessSession` holds `OpenCodeProcessManager` and `OpenCodeHttpClient` directly. Introduce `IOpenCodeInstanceHandle` interface with `HttpClient`, `SendCommandAsync(ocSessionId, ...)`, `SubscribeEvents(ocSessionId)`. Two implementations: `OwnedInstanceHandle` (current behavior, for non-pooled) and `LeasedInstanceHandle` (delegates to pooled instance). Session receives handle via constructor; dispose releases lease.
  **Files**: `src/WeaveFleet.Infrastructure/Harnesses/OpenCode/Pooling/IOpenCodeInstanceHandle.cs`, `src/WeaveFleet.Infrastructure/Harnesses/OpenCode/Pooling/LeasedInstanceHandle.cs`, `src/WeaveFleet.Infrastructure/Harnesses/OpenCode/OwnedInstanceHandle.cs`, `src/WeaveFleet.Infrastructure/Harnesses/OpenCode/OpenCodeHarnessSession.cs`
  **Acceptance**: Existing tests pass with `OwnedInstanceHandle`; session no longer directly references `OpenCodeProcessManager`

- [x] 6. Refactor `OpenCodeHarnessRuntime.SpawnAsync` for pooled mode
  **What**: When feature flag ON: (a) compute credential-hash from env vars; (b) call `PooledOpenCodeInstanceRegistry.AcquireAsync(credentialHash, directory, cancellationToken)`; (c) create OpenCode session via `POST /session?directory=X`; (d) store mapping: FleetSessionId → (PooledInstanceKey, OpenCodeSessionId, Directory); (e) return `OpenCodeHarnessSession` with `LeasedInstanceHandle`. Lazy start: don't acquire until first message/command (not on Fleet session create).
  **Files**: `src/WeaveFleet.Infrastructure/Harnesses/OpenCode/OpenCodeHarnessRuntime.cs`
  **Acceptance**: SpawnAsync with flag ON reuses existing pooled instance; second call same creds → same PID

- [x] 7. Refactor `OpenCodeHarnessRuntime.ResumeAsync` for pooled mode
  **What**: On resume: (a) look up stored mapping (FleetSessionId → OpenCodeSessionId + Directory + CredentialHash); (b) **verify ownership — Fleet session must belong to the requesting user** (user-scoped mapping); (c) acquire lease from registry (may spawn new process if old one died); (d) verify OpenCode session still exists via `GET /session/{ocSessionId}?directory=X`; (e) if gone, create new OC session and replay/import. Handle "instance not found after crash" by re-spawning.
  **Files**: `src/WeaveFleet.Infrastructure/Harnesses/OpenCode/OpenCodeHarnessRuntime.cs`
  **Acceptance**: Resume after process crash re-spawns and reconnects; resume with live process reattaches; resume by non-owner user is rejected

- [x] 8. Command routing with explicit OpenCode session ID
  **What**: `OpenCodeHttpClient.SendCommandAsync` already uses `POST /session/{ocSessionId}/command?directory=X`; pooled mode must ensure the `ocSessionId` is resolved from the backend binding for the Fleet session, never from frontend input. Because probing showed `GET /session/{ocSessionId}?directory=wrongDir` can still return the session by ID, do not treat the `directory` query parameter alone as an isolation boundary. Add a backend guard that verifies `(FleetSessionId, UserId, OpenCodeSessionId, Directory, LeaseGeneration)` in `PoolDemuxBindingTable` before sending the command.
  **Files**: `src/WeaveFleet.Infrastructure/Harnesses/OpenCode/OpenCodeHttpClient.cs`
  **Acceptance**: Command sent to wrong OC session ID returns error; correct routing verified in integration test

### Phase 3: Lifecycle, Crash Handling & Isolation (Week 3)

- [x] 9. Process lifecycle: ref-count + idle TTL
  **What**: When last lease released, start idle timer (configurable, default 60s). If no new lease acquired within TTL, kill process. If new lease acquired, cancel timer. On explicit `StopAsync` of last session, kill immediately (no TTL). Configurable via settings.
  **Files**: `src/WeaveFleet.Infrastructure/Harnesses/OpenCode/Pooling/PooledOpenCodeInstanceRegistry.cs`
  **Acceptance**: Unit test: last release → process alive for TTL → then killed; new acquire during TTL cancels shutdown

- [x] 10. Crash handling & automatic recovery
  **What**: `PooledOpenCodeInstance` monitors process exit. On unexpected exit: (a) mark instance as faulted; (b) notify all active lessees via callback/event; (c) lessees transition to "disconnected" state (surfaced to Fleet session as harness error); (d) next operation (message/command) triggers re-acquire from registry (spawns new process); (e) OC sessions are re-created; **(f) SSE subscription must be re-established BEFORE unblocking any pending operations** to avoid event loss window. Rate-limit restarts (max 3 in 60s → give up).
  **Files**: `src/WeaveFleet.Infrastructure/Harnesses/OpenCode/Pooling/PooledOpenCodeInstance.cs`, `src/WeaveFleet.Infrastructure/Harnesses/OpenCode/OpenCodeHarnessSession.cs`
  **Acceptance**: Kill process externally → all sessions recover on next operation; rapid crash loop → sessions get permanent error; SSE subscription confirmed active before first post-recovery message sent

- [x] 11. Working directory isolation enforcement
  **What**: Add validation layer: every HTTP request from `LeasedInstanceHandle` asserts that the `?directory=` param matches the session's configured directory. Log and reject mismatches. This prevents bugs where a session accidentally queries another directory's data.
  **Files**: `src/WeaveFleet.Infrastructure/Harnesses/OpenCode/Pooling/LeasedInstanceHandle.cs`
  **Acceptance**: Unit test: request with wrong directory throws; correct directory passes

- [x] 12. Credentials/env boundary enforcement
  **What**: Registry key is SHA256 hash of the **full authoritative env dictionary** produced by runtime preparation (not glob patterns like `*_API_KEY`). The hash is computed from sorted key-value pairs of the complete resolved environment. Decrypted credential values are used only transiently for hashing/comparison — they MUST NOT be retained in plaintext after hash computation. Document the in-memory threat model: values exist only on stack/short-lived locals, zeroed after use where runtime permits. Two sessions with different credential sets NEVER share a process. Add explicit check in `AcquireAsync`. Log when a new process is spawned due to credential mismatch.
  **Files**: `src/WeaveFleet.Infrastructure/Harnesses/OpenCode/Pooling/PooledOpenCodeInstanceRegistry.cs`, `src/WeaveFleet.Infrastructure/Harnesses/OpenCode/Pooling/CredentialHasher.cs`
  **Acceptance**: Unit test: different API keys → different instances; same keys → same instance; no plaintext credentials retained in CredentialHasher after hash returned; threat model documented in code comments

- [x] 13. Stop/Delete/Resume semantics
  **What**: Define and implement: (a) **Stop session** → release lease, OC session remains on disk (resumable); (b) **Delete session** → release lease + `DELETE /session/{ocSessionId}?directory=X`; (c) **Resume session** → acquire lease + verify OC session exists. Ensure Fleet's `SessionOrchestrator` maps these correctly through the harness interface.
  **Files**: `src/WeaveFleet.Infrastructure/Harnesses/OpenCode/OpenCodeHarnessSession.cs`, `src/WeaveFleet.Infrastructure/Harnesses/OpenCode/OpenCodeHarnessRuntime.cs`
  **Acceptance**: Stop → resume works; delete → resume creates new OC session; stop doesn't kill shared process

### Phase 4: Observability, Settings & Tests (Week 4)

- [x] 14. Telemetry & audit logging
  **What**: Add structured logging for: pool acquire/release, process spawn/kill, ref-count changes, crash events, TTL expirations, credential boundary decisions. Add metrics: `opencode_pool_instances_active`, `opencode_pool_sessions_per_instance`, `opencode_pool_process_restarts`. Use existing telemetry infrastructure.
  **Files**: `src/WeaveFleet.Infrastructure/Harnesses/OpenCode/Pooling/PooledOpenCodeInstanceRegistry.cs`, `src/WeaveFleet.Infrastructure/Harnesses/OpenCode/Pooling/PooledOpenCodeInstance.cs`
  **Acceptance**: Logs visible in dev; metrics exported; no PII in logs

- [x] 15. Settings UI toggle
  **What**: Add toggle in Weave settings (harness section) for "Pooled OpenCode Mode" (on/off). Changing setting takes effect for new sessions only (existing sessions continue in their current mode). Store in user settings.
  **Files**: Frontend settings component (minimal change — add toggle), `src/WeaveFleet.Api/Controllers/SettingsController.cs` (if needed)
  **Acceptance**: Toggle visible; changing it persists; new sessions respect the setting

- [x] 16. Lazy start implementation
  **What**: When pooled mode enabled, `SpawnAsync` does NOT immediately start a process. Instead, it returns a session in "pending" state. First `SendMessageAsync` or `SendCommandAsync` triggers `AcquireAsync` on the registry (which spawns if needed). This avoids process overhead for sessions that are created but never used. **At lazy acquire time, re-validate credentials/settings** — do not use stale cached values from session creation time.
  **Files**: `src/WeaveFleet.Infrastructure/Harnesses/OpenCode/OpenCodeHarnessSession.cs`, `src/WeaveFleet.Infrastructure/Harnesses/OpenCode/Pooling/LeasedInstanceHandle.cs`
  **Acceptance**: Creating a Fleet session with pooled mode → no OS process spawned; sending first message → process spawned; second session → reuses process; credential rotation between create and first message → uses fresh credentials

- [x] 17. Integration tests — full isolation matrix
  **What**: Test scenarios: (a) 2 sessions same user same dir — share process, events isolated; (b) 2 sessions same user different dir — share process, events isolated; (c) 2 sessions different creds — separate processes; (d) session stop + resume; (e) process crash + recovery; (f) slash command routing correctness; (g) concurrent message sends don't cross-contaminate.
  **Files**: `tests/WeaveFleet.IntegrationTests/Harnesses/OpenCode/PooledHarnessIsolationTests.cs`
  **Acceptance**: All scenarios pass; no flaky tests

- [x] 18. Unit tests — registry, demux, lifecycle
  **What**: Comprehensive unit tests for `PooledOpenCodeInstanceRegistry`, `SseEventDemultiplexer`, `PoolDemuxBindingTable`, `CredentialHasher`, `LeasedInstanceHandle`, idle TTL, crash recovery, ref-counting edge cases (double-release, acquire-after-dispose).
  **Files**: `tests/WeaveFleet.UnitTests/Harnesses/OpenCode/Pooling/`
  **Acceptance**: >90% branch coverage on pooling classes

- [x] 18b. Security-focused tests
  **What**: Dedicated test cases for Warp security audit findings: (a) SSE events without parseable session ID → dropped, never forwarded, metric incremented; (b) double lease release → idempotent, no crash or negative refcount; (c) credential rotation mid-session → lazy acquire picks up new creds, old pool instance not reused; (d) concurrent stop + crash race → no deadlock, session reaches terminal state; (e) resume by non-owner user → rejected; (f) stale binding generation event → dropped.
  **Files**: `tests/WeaveFleet.UnitTests/Harnesses/OpenCode/Pooling/SecurityAuditTests.cs`
  **Acceptance**: All 6 scenarios pass; no flaky behavior under concurrent execution

- [x] 19. Rollout: feature flag defaults & documentation
  **What**: Feature flag defaults to OFF in production. Add internal documentation on how to enable, monitor, and rollback. Add health check endpoint that reports pool status (instance count, session count, process PIDs) — **admin-only or fully redacted for non-admin callers**.
  **Files**: `src/WeaveFleet.Infrastructure/Harnesses/OpenCode/Pooling/PoolHealthCheck.cs`, config files
  **Acceptance**: Default deploy has no behavior change; explicit opt-in works; health check returns pool state; non-admin request returns redacted/403 response

### Phase 5: Hardening & Cleanup (Week 5)

- [x] 20. Port exhaustion mitigation
  **What**: With pooling, port usage drops from N to ~1 per credential set. Verify `PortAllocator` still works correctly for non-pooled mode. Add metric for port pool utilization. Consider removing port allocation entirely for pooled instances (single port per instance).
  **Files**: `src/WeaveFleet.Infrastructure/Harnesses/OpenCode/PortAllocator.cs`
  **Acceptance**: Port count under load matches expected (1 per pool instance, not per session)

- [x] 21. Early child event drop race mitigation
  **What**: In pooled mode, the directory-scoped SSE subscription for the session's directory must be established BEFORE the first message is sent (to avoid missing early events). `LeasedInstanceHandle` must register the binding and ensure `SseEventDemultiplexer` has an active `GET /event?directory=X` stream before returning from acquire. Add ordering guarantee.
  **Files**: `src/WeaveFleet.Infrastructure/Harnesses/OpenCode/Pooling/LeasedInstanceHandle.cs`, `src/WeaveFleet.Infrastructure/Harnesses/OpenCode/Pooling/SseEventDemultiplexer.cs`
  **Acceptance**: Integration test: first message events are never dropped

- [x] 22. Backward compatibility — ensure non-pooled mode unchanged
  **What**: Run full existing test suite with feature flag OFF. Verify zero behavioral changes. Add explicit regression test that asserts per-session process isolation when flag is OFF.
  **Files**: `tests/WeaveFleet.IntegrationTests/Harnesses/OpenCode/NonPooledRegressionTests.cs`
  **Acceptance**: All pre-existing tests pass; new regression test passes

## Verification
- [x] `dotnet test` — all unit + integration tests green
- [x] Feature flag OFF → existing behavior verified by regression tests
- [x] Feature flag ON → pooled behavior verified by isolation matrix tests
- [x] No frontend changes required (verified by unchanged API contracts)
- [x] Telemetry/metrics visible in dev environment
- [x] Process count under 5-session load: 1 (pooled) vs 5 (non-pooled)
- [x] Crash recovery: kill process → all sessions recover within 5s
- [x] Slash commands route correctly in pooled mode (verified by integration test)
- [x] Security audit: unattributable SSE events never reach Fleet consumers (verified by SecurityAuditTests)
- [x] Security audit: no plaintext credentials retained after hash computation (verified by CredentialHasher tests)
- [x] Security audit: binding table rejects stale/unknown/mismatched events (verified by PoolDemuxBindingTable tests)
- [x] Security audit: pool diagnostics endpoint returns 403/redacted for non-admin
- [x] Security audit: resume verifies user ownership
