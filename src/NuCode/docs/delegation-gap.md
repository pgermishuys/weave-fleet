# Delegation Gap: TaskTool Not Registered

## Status: Deferred (not planned)

## Summary

`TaskTool` exists (`src/NuCode/Tools/TaskTool.cs`, 262 lines, fully implemented) but is
**intentionally not registered** in `RegisterBuiltInTools`. NuCode cannot delegate work
to sub-agents via the `task` tool.

## Why It Can't Be Registered Today

### 1. Scoped vs Singleton Mismatch

`TaskTool` depends on **scoped** services:
- `ISessionProcessor` (scoped)
- `ICompactionService` (scoped)

The tool registry (`IToolRegistry`) is **singleton** and tools are created once during
DI setup via `RegisterBuiltInTools(IToolRegistry, IServiceProvider)`. Resolving scoped
services from the root `IServiceProvider` is invalid.

### 2. Delegation Tracking Requires Fleet DB

Even if `TaskTool` were registered, delegation events (`delegation.created`) are emitted
by `NuCodeHarnessSession` via `IServiceScopeFactory` -> `DelegationService`, which
requires:
- `IDelegationRepository` (Fleet DB)
- `IEventBroadcaster`
- `IUserContext`
- `SessionActivityWriteService`
- `SessionActivityTracker`

These are `WeaveFleet.Application` services registered as scoped in the Fleet host,
not available in standalone NuCode usage.

### 3. SessionOrchestrator Dependency Chain

`SessionOrchestrator` (which manages child Fleet sessions for delegation) has 20+
constructor dependencies spanning repositories, services, and infrastructure concerns.
This is Fleet orchestration, not harness-level functionality.

## Architecture Decision

Delegation is handled at the **Fleet orchestrator layer**, not the harness layer:
- Fleet's `SessionOrchestrator.ForkSessionAsync` creates child sessions
- Each child session gets its own `NuCodeHarnessSession` instance
- The parent session doesn't need to know about child sessions directly

NuCode's `TaskTool` was designed for standalone NuCode usage (outside Fleet) where
a single process manages parent and child sessions. In the Fleet model, this
responsibility belongs to `SessionOrchestrator`.

## Conformance Test Coverage

Two gap tests document this in `tests/NuCode.ConformanceTests/NuCode/Gaps/DelegationGapTests.cs`:
1. `TaskToolCall_CreatesDelegation` — verifies task tool call produces ToolUsePart (fails)
2. `ChildSession_EventsRouteToCorrectFleetSession` — verifies delegation.created event (fails)

Both are expected failures and serve as documentation.
