# Prevent Orphaned Child Sessions

## TL;DR
> **Summary**: Add cross-platform process group management (Unix `setpgid` / Windows Job Objects), graceful shutdown hooks, and startup orphan killing so child processes never outlive the Fleet server.
> **Estimated Effort**: Medium

## Context
### Original Request
The Fleet API server spawns child processes (`opencode serve`, `claude -p ...`) but has no mechanism to clean them up if the server dies or shuts down. This leads to orphaned processes consuming resources. The crash-safety plan (`.weave/plans/crash-safety.md`) explicitly deferred this as out of scope.

### Key Findings

1. **Both process managers follow identical patterns**: `OpenCodeProcessManager` (line 180: `_process.Start()`) and `ClaudeCodeProcessManager` (line 161: `_process.Start()`) both call `Process.Start()` with no post-start process group assignment. Both have identical `StopAsync` methods with platform-branching (`Kill(entireProcessTree: true/false)`).

2. **PID is already in the domain model but not persisted**: `Instance.Pid` exists (`int?`) and `InstanceService.RegisterInstanceAsync` accepts `pid` — but `SessionOrchestrator.CreateSessionAsync` always passes `pid: null` (line 218). The `OpenCodeProcessManager` exposes `ProcessId` but it's never plumbed through to the DB.

3. **`HarnessHelpers.cs` exists as the shared utility location**: Already in `WeaveFleet.Infrastructure/Harnesses/` with `internal static` methods. This is the natural home for process group utilities.

4. **Startup recovery already exists**: `Program.cs` lines 199-208 call `InstanceService.MarkAllStoppedAsync()` and `MarkAllNonTerminalSessionsStoppedAsync()`. This is where orphan killing should be added — after marking DB records stopped but using the PIDs from those records.

5. **`InstanceTracker.GetAll()` returns all live sessions**: Returns `IReadOnlyDictionary<string, IHarnessSession>`. Each `IHarnessSession` has `StopAsync(CancellationToken)`. This is the iteration target for the graceful shutdown hook.

6. **`IHarnessSession` interface** is in `WeaveFleet.Domain/Harnesses/` — Application layer only sees this abstraction. Process groups stay entirely in Infrastructure.

7. **`IInstanceRepository.GetRunningAsync()`** already exists — returns instances with `status = 'running'`. Can be used at startup to find stale PIDs before marking them stopped.

8. **No `IHostedLifecycleService` or `IHostApplicationLifetime` usage** exists for shutdown hooks currently. `HarnessEventRelay` is a `BackgroundService` but doesn't stop instances.

## Objectives
### Core Objective
Ensure all child processes spawned by the Fleet server are cleaned up on both graceful shutdown and crash/kill scenarios.

### Deliverables
- [x] Cross-platform process group utility in Infrastructure layer
- [x] Both process managers use process groups after `Process.Start()`
- [x] Graceful shutdown hook stops all tracked instances
- [x] PIDs persisted to DB during instance registration
- [x] Startup orphan detection and killing using persisted PIDs

### Definition of Done
- [x] `dotnet build src/WeaveFleet.Api` succeeds with no warnings
- [x] `dotnet test` passes all existing tests
- [x] New unit tests for `ProcessGroupHelper` pass
- [ ] Manual test: kill Fleet server → child processes are also killed (Unix via process group)

### Guardrails (Must NOT)
- Must NOT expose process group concepts in Application or Domain layers
- Must NOT change `IHarnessSession` interface
- Must NOT kill PIDs without checking they still belong to the expected process (PID reuse safety)
- Must NOT block startup if orphan killing fails (best-effort, log and continue)

## TODOs

- [x] 1. **Create `ProcessGroupHelper` utility**
  **What**: Add a new `internal static class ProcessGroupHelper` with two methods: `AssignToProcessGroup(Process process)` for post-Start process group assignment, and `KillProcessGroup(int pid)` for killing an entire group. On Unix: P/Invoke `setpgid(pid, pid)` after `Process.Start()` to make the child its own process group leader, then `killpg(pgid, SIGTERM)` for group kill. On Windows: create a Job Object with `JOB_OBJECT_LIMIT_KILL_ON_JOB_CLOSE`, assign the process to it, and return the Job Object handle (must be held alive). Use `[LibraryImport]` (source-generated P/Invoke) for all native calls.
  **Files**: `src/WeaveFleet.Infrastructure/Harnesses/ProcessGroupHelper.cs`
  **Acceptance**: Compiles on all platforms. Unix path uses `setpgid`/`killpg`. Windows path uses Job Objects with `JOB_OBJECT_LIMIT_KILL_ON_JOB_CLOSE`.

- [x] 2. **Integrate process groups into `OpenCodeProcessManager`**
  **What**: After `_process.Start()` (line 180), call `ProcessGroupHelper.AssignToProcessGroup(_process)`. In `StopAsync`, replace the platform-branching `Kill` logic with `ProcessGroupHelper.KillProcessGroup(_process.Id)` followed by `WaitForExitAsync` with timeout, then fallback to `Kill(entireProcessTree: true)`. Store the Windows Job Object handle (if returned) and dispose it in `DisposeAsync`.
  **Files**: `src/WeaveFleet.Infrastructure/Harnesses/OpenCode/OpenCodeProcessManager.cs`
  **Acceptance**: Process is assigned to its own group immediately after start. Stop kills the entire group.

- [x] 3. **Integrate process groups into `ClaudeCodeProcessManager`**
  **What**: Same pattern as TODO 2. After `_process.Start()` (line 161), call `ProcessGroupHelper.AssignToProcessGroup(_process)`. Update `StopAsync` to use group kill. Store and dispose Job Object handle on Windows.
  **Files**: `src/WeaveFleet.Infrastructure/Harnesses/ClaudeCode/ClaudeCodeProcessManager.cs`
  **Acceptance**: Process is assigned to its own group immediately after start. Stop kills the entire group.

- [x] 4. **Persist PIDs during instance registration**
  **What**: Plumb the actual PID from process managers through to `InstanceService.RegisterInstanceAsync`. In `OpenCodeHarnessRuntime.SpawnAsync`, after starting the process, pass `_processManager.ProcessId` when constructing the session or registering the instance. In `SessionOrchestrator.CreateSessionAsync`, pass the PID from the harness session to `RegisterInstanceAsync` instead of `null`. This requires adding a `ProcessId` property to `IHarnessSession` — **wait, guardrail says no interface changes**. Instead: after `harnessRuntime.SpawnAsync` returns, the orchestrator already has `harnessInstance.InstanceId`. The PID should be persisted by the runtime itself (Infrastructure layer) before returning from `SpawnAsync`, or the `InstanceService.RegisterInstanceAsync` call in the orchestrator should receive the PID. Since the orchestrator already passes `pid: null`, the simplest fix is to have the harness runtime update the instance PID after registration via a new `IInstanceRepository.UpdatePidAsync(string id, int pid)` method — but that crosses layers. **Better approach**: Have each process manager expose `ProcessId` (already done), and have the harness runtime pass it through `RegisterInstanceAsync` by adding a `Pid` property to `HarnessSpawnResult` or similar. Actually, the simplest approach: add `int? ProcessId { get; }` to `IHarnessSession` — it's already there conceptually (both sessions expose it on their concrete types). The domain interface can have it as a read-only property without leaking process group details.
  **Files**: `src/WeaveFleet.Domain/Harnesses/IHarnessSession.cs` (add `int? ProcessId { get; }`), `src/WeaveFleet.Infrastructure/Harnesses/OpenCode/OpenCodeHarnessSession.cs`, `src/WeaveFleet.Infrastructure/Harnesses/ClaudeCode/ClaudeCodeHarnessSession.cs`, `src/WeaveFleet.Application/Services/SessionOrchestrator.cs` (pass `harnessInstance.ProcessId` instead of `null`)
  **Acceptance**: After creating a session, the `instances` table row has a non-null `pid` value.

- [x] 5. **Add `IInstanceRepository.GetRunningWithPidsAsync()` method**
  **What**: Add a repository method that returns all instances with `status = 'running'` and a non-null PID. This is used by startup orphan detection. Can reuse `GetRunningAsync()` if it already returns the PID field (check — `Instance.Pid` exists, so it should). If `GetRunningAsync` already maps `Pid`, no new method is needed — just use it directly.
  **Files**: Verify `src/WeaveFleet.Infrastructure/Data/Repositories/DapperInstanceRepository.cs` maps `Pid` in `GetRunningAsync`.
  **Acceptance**: `GetRunningAsync()` returns instances with populated `Pid` values.

- [x] 6. **Add graceful shutdown hosted service**
  **What**: Create `GracefulShutdownService : IHostedLifecycleService` (or use `IHostApplicationLifetime.ApplicationStopping`). On `StoppingAsync`, iterate `InstanceTracker.GetAll()`, call `StopAsync` on each with a 5-second per-instance timeout, log failures, continue. Register in `Program.cs` via `builder.Services.AddHostedService<GracefulShutdownService>()` or wire up `IHostApplicationLifetime` in `Program.cs` directly. The API-layer approach (wiring in `Program.cs`) is simpler and avoids a new class.
  **Files**: `src/WeaveFleet.Api/Program.cs`
  **Acceptance**: On `Ctrl+C` / SIGTERM, all tracked instances are stopped before the process exits. Verify via logs.

- [x] 7. **Add startup orphan killing**
  **What**: In `Program.cs`, after the existing recovery block (lines 199-208), add orphan killing: before calling `MarkAllStoppedAsync()`, call `GetRunningAsync()` to get instances with PIDs, then for each PID attempt to kill it (best-effort). Use `Process.GetProcessById(pid)` wrapped in try/catch — if the process doesn't exist, skip. If it exists, check the process name or start time to guard against PID reuse (e.g., verify process name contains "opencode" or "claude"). Call `Kill(entireProcessTree: true)`. Log each kill attempt. This must happen BEFORE `MarkAllStoppedAsync` so we still have the running instance records.
  **Files**: `src/WeaveFleet.Api/Program.cs`
  **Acceptance**: On startup after a crash, orphaned child processes from the previous run are killed. PID reuse is guarded against.

- [x] 8. **Unit tests for `ProcessGroupHelper`**
  **What**: Test that `AssignToProcessGroup` doesn't throw for a real process (spawn a `sleep` / `timeout` process, assign, verify no exception). Test that `KillProcessGroup` terminates the process. Platform-conditional tests using `[SkipOnPlatform]` or `RuntimeInformation` checks.
  **Files**: `tests/WeaveFleet.Infrastructure.Tests/Harnesses/ProcessGroupHelperTests.cs`
  **Acceptance**: Tests pass on the CI platform (macOS). Windows tests can be skipped if CI is macOS-only.

## Verification
- [ ] `dotnet build src/WeaveFleet.Api` succeeds with zero warnings
- [ ] `dotnet test` passes all existing and new tests
- [ ] Manual test: start Fleet, create a session, verify child process PID is in DB
- [ ] Manual test: `kill -9 <fleet-pid>` → verify child processes are killed (via process group on Unix)
- [ ] Manual test: restart Fleet after crash → verify startup logs show orphan killing
- [ ] Manual test: graceful `Ctrl+C` → verify all instances stopped in logs before exit
