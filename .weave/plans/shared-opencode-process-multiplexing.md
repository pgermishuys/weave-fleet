# Shared OpenCode Process Multiplexing

## TL;DR
> **Summary**: Multiplex multiple Fleet harness sessions onto a single shared `opencode serve` process per user (or globally), eliminating per-session process overhead. OpenCode's API already supports this via the `?directory=` query parameter — the barrier is purely in Fleet's harness lifecycle model.
> **Estimated Effort**: Large

## Context
### Original Request
Assess feasibility of multiplexing multiple Fleet/OpenCode harness sessions onto a shared `opencode serve` process (t3code-style).

### Key Findings

1. **OpenCode API already supports multi-directory multiplexing.** Every HTTP endpoint accepts `?directory=<path>` — sessions, prompts, events, agents, providers are all directory-scoped. A single process can serve N directories simultaneously.

2. **Current model: 1 process per Fleet session.** `OpenCodeHarnessRuntime.SpawnAsync()` creates a new `OpenCodeProcessManager` + `OpenCodeHttpClient` per session. Each gets its own ephemeral port, Basic Auth credentials, and OS process.

3. **SSE stream is global per process.** `GET /event?directory=<dir>` returns events for ALL sessions in that directory. The `OpenCodeHarnessSession.SubscribeAsync()` already filters by `_openCodeSessionId`. However, with multiplexing, a single SSE connection would emit events for ALL directories — requiring a demultiplexer.

4. **Environment variables are process-wide.** `ANTHROPIC_API_KEY`, `OPENAI_API_KEY` etc. are set on the process. Sharing a process means all sessions on that process share the same credentials — this is the **primary security constraint**.

5. **`OPENCODE_CONFIG_CONTENT`** is also process-wide. Currently hardcoded to `{"permission":{"question":"allow"}}`. Shared process = shared config.

6. **Port allocation (`PortAllocator`)** becomes unnecessary for shared processes (one port per shared instance instead of per session).

## Architecture Assessment

### Feasibility: ✅ HIGH — with constraints

The OpenCode HTTP API is already designed for multi-session, multi-directory operation. The main work is in Fleet's harness lifecycle layer, not in OpenCode itself.

### Required Changes

| Area | Current | Shared Model |
|------|---------|--------------|
| Process lifecycle | 1 process per session | 1 process per credential-set (or per user) |
| Port allocation | 1 port per session | 1 port per shared instance |
| SSE subscription | 1 stream per session, filtered by sessionId | 1 stream per shared instance, demuxed to N sessions |
| Auth credentials | Per-process env vars | Must be identical across multiplexed sessions |
| Cleanup | Kill process on session stop | Ref-count; kill when last session detaches |

### Lifecycle/Cleanup Model

```
SharedOpenCodeInstance (ref-counted)
├── OpenCodeProcessManager (1 OS process)
├── OpenCodeHttpClient (1 HttpClient, shared)
├── SseEventDemultiplexer (1 SSE stream → N session channels)
└── SessionRef[] (active sessions using this instance)
    ├── OpenCodeHarnessSession A (directory=/project-a)
    ├── OpenCodeHarnessSession B (directory=/project-b)
    └── OpenCodeHarnessSession C (directory=/project-a, different OC session)
```

- **Acquire**: `SpawnAsync` checks if a compatible shared instance exists (same credentials). If yes, attach. If no, spawn new process.
- **Release**: `StopAsync`/`DisposeAsync` decrements ref count. When count hits 0, kill process after grace period.
- **Crash**: Process exit event propagates to ALL attached sessions (not just one).

### Session/Project Directory Isolation

- **Strong isolation via `?directory=` param**: OpenCode scopes sessions, config, and state by directory. Two sessions in different directories are fully isolated at the OpenCode level.
- **Same-directory sessions**: Multiple Fleet sessions targeting the same directory share OpenCode's `.opencode/` state directory. This is already the case today (OpenCode persists sessions to disk). No regression.
- **File system access**: OpenCode agents can read/write files in their directory. No cross-directory access is possible via the API.

### Concurrency Risks

| Risk | Severity | Mitigation |
|------|----------|------------|
| SSE event routing to wrong session | High | Demux by `sessionId` field in SSE events (already done per-session today) |
| Shared process crash affects all sessions | Medium | Already a risk with containers; ref-count cleanup + auto-restart |
| Race in ref-count acquire/release | Medium | `SemaphoreSlim` or `lock` around instance registry |
| Memory pressure from many sessions in one process | Low | OpenCode is Go — low per-session overhead; monitor RSS |
| Concurrent prompts saturating one process | Low | OpenCode handles concurrency internally; LLM API is the bottleneck |

### Security/Auth Boundaries

- **Critical constraint**: All sessions on a shared process use the same API keys (env vars are process-scoped).
- **Implication**: Multiplexing is only safe for sessions belonging to the **same user** (same credential set).
- **Multi-user scenario**: Each user gets their own shared instance (keyed by user ID or credential hash).
- **Basic Auth to OpenCode**: Can remain per-instance (all sessions on that instance share the same creds to talk to the process).

### Performance Impact

| Metric | Per-Session Model | Shared Model |
|--------|-------------------|--------------|
| Process count | N | 1 per user |
| Memory (RSS) | ~80-120MB × N | ~80-120MB + marginal per session |
| Startup latency | ~2-5s per session | ~0ms for 2nd+ session |
| Port consumption | N ports | 1 port per user |
| SSE connections | N | 1 per shared instance |

**Expected improvement**: 10-50x reduction in process count for active users with multiple sessions.

### Migration Strategy

1. **Phase 1 — Introduce `SharedOpenCodeInstanceRegistry`** (singleton, keyed by credential-hash): manages process lifecycle, ref-counting, SSE demux.
2. **Phase 2 — Modify `OpenCodeHarnessRuntime.SpawnAsync/ResumeAsync`**: check registry before spawning. Attach to existing instance if compatible.
3. **Phase 3 — Extract `OpenCodeHarnessSession` from process ownership**: session no longer owns `OpenCodeProcessManager`. It holds a lease on a shared instance.
4. **Phase 4 — SSE demultiplexer**: single background SSE reader per shared instance, routing events to per-session `Channel<T>` based on directory + sessionId.
5. **Phase 5 — Deprecate `PortAllocator` for shared mode** (keep for fallback/container mode).

### Files/Classes Likely Impacted

| File | Change |
|------|--------|
| `src/WeaveFleet.Infrastructure/Harnesses/OpenCode/OpenCodeHarnessRuntime.cs` | Spawn/Resume logic → registry lookup |
| `src/WeaveFleet.Infrastructure/Harnesses/OpenCode/OpenCodeHarnessSession.cs` | Remove process ownership; hold shared instance lease |
| `src/WeaveFleet.Infrastructure/Harnesses/OpenCode/OpenCodeProcessManager.cs` | Becomes internal to shared instance; no longer 1:1 with session |
| `src/WeaveFleet.Infrastructure/Harnesses/OpenCode/OpenCodeHttpClient.cs` | Shared across sessions (already stateless per-request) |
| `src/WeaveFleet.Infrastructure/Harnesses/OpenCode/PortAllocator.cs` | Reduced usage; 1 port per shared instance |
| **NEW**: `SharedOpenCodeInstanceRegistry.cs` | Singleton registry: keyed by credential-hash, ref-counted |
| **NEW**: `SseEventDemultiplexer.cs` | Routes SSE events from one stream to N session consumers |
| `src/WeaveFleet.Infrastructure/Harnesses/OpenCode/OpenCodeHarness.cs` | DI registration changes |

### Open Questions

1. **Should the shared instance key be user-ID or credential-hash?** User-ID is simpler but breaks if a user changes API keys mid-session. Credential-hash is more correct but requires re-spawning on key rotation.

2. **Grace period on last-session detach?** Kill immediately or keep alive for N seconds anticipating new sessions? (t3code keeps processes alive with a TTL.)

3. **Does OpenCode's `GET /event` SSE endpoint emit events for ALL directories or only the one in `?directory=`?** If directory-scoped, we need one SSE stream per directory (still better than per-session, but not a single global stream). This needs empirical verification.

4. **Container mode compatibility**: The cloud-hosting plan (`cloud-spike-provider-auth-and-containers.md`) envisions one container per session. Shared-process mode is orthogonal — it applies to local/bare-metal deployments. Both modes should coexist behind a strategy pattern.

5. **OpenCode process memory growth**: Does a long-running `opencode serve` process leak memory over many sessions? Need to profile.

6. **Config divergence**: If different sessions need different `OPENCODE_CONFIG_CONTENT` (e.g., different permission sets), they cannot share a process. Current code hardcodes the same config for all sessions, so this is not a problem today.

## Objectives
### Core Objective
Reduce per-session OS process overhead by sharing a single `opencode serve` process across compatible Fleet sessions.

### Deliverables
- [ ] `SharedOpenCodeInstanceRegistry` — singleton managing shared process lifecycle
- [ ] `SseEventDemultiplexer` — routes one SSE stream to N session consumers
- [ ] Modified `OpenCodeHarnessRuntime` — registry-aware spawn/resume
- [ ] Modified `OpenCodeHarnessSession` — lease-based (not ownership-based) process relationship
- [ ] Integration tests verifying multi-session multiplexing
- [ ] Fallback to per-session mode when credentials differ or container mode is active

### Definition of Done
- [ ] Two Fleet sessions for the same user share one `opencode serve` process
- [ ] Stopping one session does not kill the shared process (ref-count > 0)
- [ ] Stopping the last session kills the shared process
- [ ] SSE events route correctly to the owning session
- [ ] No credential leakage between users

### Guardrails (Must NOT)
- Must NOT break container-mode deployment path
- Must NOT share processes across users with different credentials
- Must NOT introduce a single point of failure that crashes all sessions server-wide

## TODOs

- [ ] 1. Verify SSE directory scoping
  **What**: Empirically test whether `GET /event?directory=X` returns events only for directory X or globally. This determines whether we need 1 SSE stream per directory or 1 per process.
  **Acceptance**: Documented behavior with test evidence

- [ ] 2. Implement `SharedOpenCodeInstanceRegistry`
  **What**: Thread-safe singleton keyed by credential-hash. Manages spawn, ref-count acquire/release, grace-period shutdown.
  **Files**: `src/WeaveFleet.Infrastructure/Harnesses/OpenCode/SharedOpenCodeInstanceRegistry.cs`
  **Acceptance**: Unit tests for acquire/release/expiry lifecycle

- [ ] 3. Implement `SseEventDemultiplexer`
  **What**: Reads one SSE stream, routes events to per-session `Channel<OpenCodeSseEvent>` based on directory + sessionId.
  **Files**: `src/WeaveFleet.Infrastructure/Harnesses/OpenCode/SseEventDemultiplexer.cs`
  **Acceptance**: Unit test: 2 sessions receive only their own events

- [ ] 4. Refactor `OpenCodeHarnessSession` to lease model
  **What**: Remove `OpenCodeProcessManager` ownership. Accept a shared instance lease. Release on stop/dispose.
  **Files**: `src/WeaveFleet.Infrastructure/Harnesses/OpenCode/OpenCodeHarnessSession.cs`
  **Acceptance**: Session stop decrements ref-count; does not kill process if others active

- [ ] 5. Refactor `OpenCodeHarnessRuntime.SpawnAsync/ResumeAsync`
  **What**: Check registry for compatible instance before spawning. Create new only if none exists.
  **Files**: `src/WeaveFleet.Infrastructure/Harnesses/OpenCode/OpenCodeHarnessRuntime.cs`
  **Acceptance**: Second spawn for same user reuses existing process (verified by PID)

- [ ] 6. Integration tests
  **What**: Multi-session scenarios: same user/same dir, same user/different dir, different users.
  **Files**: `tests/WeaveFleet.IntegrationTests/Harnesses/SharedOpenCodeInstanceTests.cs`
  **Acceptance**: All scenarios pass; process count matches expectations

## Verification
- [ ] All existing harness tests pass (no regression)
- [ ] New integration tests pass for shared-instance scenarios
- [ ] Memory/process count reduced under multi-session load
- [ ] Container mode still spawns isolated processes
