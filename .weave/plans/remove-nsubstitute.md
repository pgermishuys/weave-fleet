# Remove NSubstitute: Full Migration to Hand-Crafted Fakes

## TL;DR
> **Summary**: Replace all 225 `Substitute.For<>` calls across 26 test files with in-memory fakes, shared test utilities, and structural refactors. Eliminate the NSubstitute package entirely.
> **Estimated Effort**: Large

## Context
### Original Request
Migrate away from NSubstitute completely. Replace all mock usage with real implementations or hand-crafted fakes. Establish a policy that discourages mocking going forward.

### Key Findings

**Mock distribution by category:**
- Repository interfaces: 129 mocks (57%) — 12 interfaces, all CRUD-shaped with `Dictionary<string, T>` semantics
- Service/Infrastructure: 41 mocks (18%) — `IEventBroadcaster` (17), `ICredentialStore` (10), `IServiceScopeFactory` (7), `IOutboxDispatcher` (4), `ICredentialProtector` (2), `IPluginStateStore` (1)
- Harness interfaces: 25 mocks (11%) — `IHarnessSession` (7), `IHarnessRuntime` (6), `IHarness` (6), `IHarnessRegistry` (6)
- Analytics & Context: 10 mocks (4%) — `IAnalyticsCollector` (6), `IUserContext` (4)
- System/Framework: 10 mocks (4%) — `IServiceProvider` (3), `IServiceScope` (3), `IDbConnection` (2), `IDbTransaction` (2)

**Existing fakes already in the codebase:**
- `TestUserContext` — duplicated in 4 test projects (Api, Application, Infrastructure, E2E)
- `FakeHarness` / `FakeHarnessRuntime` — private in `HarnessRegistryTests.cs`
- `FakeInstance` (IHarnessSession) — private in `HarnessEventRelayTests.cs`
- `FakeSseHttpMessageHandler` — private in `OpenCodeHarnessSessionPersistenceTests.cs`
- `FakeGitHubHandler` — private in `GitHubSessionSourceProviderTests.cs`
- `StubConnectionFactory` — private in persistence tests (duplicated)
- `TestHttpClientFactory` — private in multiple test files

**Critical pattern: SessionOrchestrator constructor bloat.** The `SessionOrchestrator` takes 17 dependencies. Tests for `SessionService`, `SessionDelegationsEndpointTests`, `SessionLifecycleEndpointTests`, and `MultiTenancyTests` all construct it with 12-17 mocked repositories — the same boilerplate repeated 6+ times. This is the single biggest source of mock proliferation.

**IServiceScopeFactory mocking is a code smell.** `HarnessEventRelayTests` manually wires `IServiceProvider` → `IServiceScope` → `IServiceScopeFactory` to resolve `ISessionRepository`. The persistence tests already solved this correctly by using a real `ServiceCollection`.

**Repository interfaces are pure CRUD.** All 12 repository interfaces follow the same pattern: Insert, GetById, List, Update, Delete. In-memory `Dictionary<string, T>` fakes are the right approach — they're simple, fast, and test real behavior.

**The codebase already has real SQLite integration tests** in `WeaveFleet.Infrastructure.Tests/Data/Repositories/` using `TestDbHelper.CreateSharedDbAsync()`. These test the Dapper repositories against real SQLite. The in-memory fakes are for Application-layer tests that shouldn't depend on SQLite.

## Objectives
### Core Objective
Eliminate NSubstitute from all test projects and establish hand-crafted fakes as the standard testing pattern.

### Deliverables
- [x] Shared test utilities project (`WeaveFleet.Testing`) with reusable fakes
  - [x] In-memory fake implementations for all 12 repository interfaces
  - [x] Fake implementations for all 6 service/infrastructure interfaces
  - [x] Fake implementations for all 4 harness interfaces
  - [x] `FakeAnalyticsCollector` and consolidated `TestUserContext`
  - [x] `SessionOrchestratorBuilder` test helper to eliminate constructor boilerplate
  - [x] All 26 test files migrated away from NSubstitute
  - [x] NSubstitute package removed from all `.csproj` files and `Directory.Packages.props`
  - [x] Testing policy documented in `tests/Directory.Build.props` as comments

### Definition of Done
- [x] `dotnet build -c Release` succeeds with zero warnings
- [x] `dotnet test` passes all tests across all test projects
- [x] `grep -r "NSubstitute" tests/ src/` returns zero results
- [x] `grep -r "Substitute.For" tests/` returns zero results

### Guardrails (Must NOT)
- Must NOT change any production code (src/) — only test code
- Must NOT change test behavior — each test must verify the same thing it verified before
- Must NOT introduce a different mocking library
- Must NOT use `IDbConnection`/`IDbTransaction` mocks — use `StubConnectionFactory` with real or no-op implementations
- Must NOT create fakes that silently swallow errors — fakes should throw on unexpected calls where appropriate

## TODOs

### Phase 0: Foundation — Shared Test Utilities Project

- [x] 1. Create `WeaveFleet.Testing` shared project
  **What**: Create a new project `tests/WeaveFleet.Testing/WeaveFleet.Testing.csproj` that all test projects reference. This project holds all shared fakes, builders, and test helpers. It references `WeaveFleet.Application` (which transitively brings in `WeaveFleet.Domain`). Add the new project to `WeaveFleet.slnx` under the `/tests/` folder.
  **Files**:
    - `tests/WeaveFleet.Testing/WeaveFleet.Testing.csproj`
    - `tests/WeaveFleet.Api.Tests/WeaveFleet.Api.Tests.csproj` (add ProjectReference)
    - `tests/WeaveFleet.Application.Tests/WeaveFleet.Application.Tests.csproj` (add ProjectReference)
    - `tests/WeaveFleet.Infrastructure.Tests/WeaveFleet.Infrastructure.Tests.csproj` (add ProjectReference)
    - `WeaveFleet.slnx` (add `<Project Path="tests/WeaveFleet.Testing/WeaveFleet.Testing.csproj" />` to `/tests/` folder)
  **Acceptance**: `dotnet build tests/WeaveFleet.Testing/` succeeds; all test projects can reference it; `dotnet build` at solution root succeeds.

- [x] 2. Consolidate `TestUserContext` into shared project
  **What**: Move `TestUserContext` from the 4 duplicated locations into `WeaveFleet.Testing/Fakes/TestUserContext.cs`. Make it `public sealed`. Delete the 4 copies. Update `using` statements in all consuming files.
  **Files**:
    - `tests/WeaveFleet.Testing/Fakes/TestUserContext.cs` (create)
    - `tests/WeaveFleet.Api.Tests/TestUserContext.cs` (delete)
    - `tests/WeaveFleet.Application.Tests/TestUserContext.cs` (delete)
    - `tests/WeaveFleet.Infrastructure.Tests/TestUserContext.cs` (delete)
    - `tests/WeaveFleet.E2E/TestUserContext.cs` (delete — E2E project also references Testing)
  **Acceptance**: All tests compile and pass; `grep -r "class TestUserContext" tests/` returns exactly 1 result in `WeaveFleet.Testing`.

### Phase 1: Easy Wins — Simple Interfaces (10 mocks eliminated)

- [x] 3. Create `FakeAnalyticsCollector`
  **What**: Implement `IAnalyticsCollector` as a no-op collector that records accepted events in lists for assertion. The interface has only 2 void methods (`AcceptTokenEvent`, `AcceptSessionSnapshot`) — trivial to fake.
  **Files**:
    - `tests/WeaveFleet.Testing/Fakes/FakeAnalyticsCollector.cs`
  **Acceptance**: Fake compiles; has `TokenEvents` and `SessionSnapshots` list properties for test assertions.

- [x] 4. Create `FakeCredentialProtector`
  **What**: Implement `ICredentialProtector` with identity-transform semantics (prefix with `ENC:` on encrypt, strip on decrypt). This matches the existing inline stub in `CredentialStoreTests.cs`.
  **Files**:
    - `tests/WeaveFleet.Testing/Fakes/FakeCredentialProtector.cs`
  **Acceptance**: `Encrypt("foo")` returns `"ENC:foo"`; `Decrypt("ENC:foo")` returns `"foo"`.

- [x] 5. Create `FakeOutboxDispatcher`
  **What**: Implement `IOutboxDispatcher` as a no-op that records notification calls. Single method: `NotifyNewMessagesAsync`. Track call count for assertions.
  **Files**:
    - `tests/WeaveFleet.Testing/Fakes/FakeOutboxDispatcher.cs`
  **Acceptance**: Fake compiles; has `NotificationCount` property.

- [x] 6. Create `FakePluginStateStore`
  **What**: Implement `IPluginStateStore` backed by a `Dictionary<(string pluginId, string userId), JsonObject>`. 4 methods: Get, Set, Remove, GetAll.
  **Files**:
    - `tests/WeaveFleet.Testing/Fakes/FakePluginStateStore.cs`
  **Acceptance**: Round-trip Set/Get works; Remove clears; GetAll returns all for user.

### Phase 2: Repository Fakes — The Big Win (129 mocks eliminated)

Strategy: All repository fakes use `Dictionary<string, TEntity>` as backing store. Each fake implements the full interface. For methods that take `IDbConnection`/`IDbTransaction` parameters, the fake ignores those parameters (they're for transactional batching in the real Dapper implementation). Fakes are NOT thread-safe — tests are single-threaded.

- [x] 7. Create `InMemorySessionRepository`
  **What**: Implement `ISessionRepository` backed by `Dictionary<string, Session>`. This is the most complex repository (21 mocks, ~35 methods). Key behaviors: `InsertAsync` adds to dictionary; `GetByIdAsync` looks up by key; `ListAsync` filters/paginates the values; `UpdateStatusAsync` mutates the stored entity; `DeleteAsync` removes; `GetForInstanceAsync` filters by `InstanceId`; `CountAsync` counts with optional status filter; `GetStatusCountsAsync` counts active/idle; `IncrementTokensAsync` adds to running totals. The `IDbConnection`/`IDbTransaction` overloads delegate to the parameterless versions.
  **Files**:
    - `tests/WeaveFleet.Testing/Fakes/Repositories/InMemorySessionRepository.cs`
  **Acceptance**: All methods implemented; basic round-trip test (insert → get → list → update → delete) passes.

- [x] 8. Create `InMemoryDelegationRepository`
  **What**: Implement `IDelegationRepository` backed by `Dictionary<string, Delegation>`. Key lookups: by ID, by parent session ID, by child session ID, by parent tool call ID. Status and child session updates mutate stored entities.
  **Files**:
    - `tests/WeaveFleet.Testing/Fakes/Repositories/InMemoryDelegationRepository.cs`
  **Acceptance**: All 10 methods implemented; lookups by parent/child/tool-call work correctly.

- [x] 9. Create `InMemoryWorkspaceRootRepository`
  **What**: Implement `IWorkspaceRootRepository` — simplest repository (4 methods: Insert, List, Delete, GetByPath).
  **Files**:
    - `tests/WeaveFleet.Testing/Fakes/Repositories/InMemoryWorkspaceRootRepository.cs`
  **Acceptance**: All 4 methods implemented.

- [x] 10. Create `InMemoryWorkspaceRepository`
  **What**: Implement `IWorkspaceRepository` — 7 methods including `GetByDirectoryAsync` which filters by directory + isolation strategy.
  **Files**:
    - `tests/WeaveFleet.Testing/Fakes/Repositories/InMemoryWorkspaceRepository.cs`
  **Acceptance**: All 7 methods implemented; `GetByDirectoryAsync` filters correctly.

- [x] 11. Create `InMemoryMessageRepository`
  **What**: Implement `IMessageRepository` — upsert semantics (insert or replace by composite key `(id, sessionId)`), cursor-based pagination, count, and batch operations.
  **Files**:
    - `tests/WeaveFleet.Testing/Fakes/Repositories/InMemoryMessageRepository.cs`
  **Acceptance**: Upsert replaces existing; pagination returns correct order; batch upsert works.

- [x] 12. Create `InMemoryProjectRepository`
  **What**: Implement `IProjectRepository` — CRUD plus `GetScratchProjectAsync` (filter by `Type == "scratch"`), `ReorderAsync`, `GetSessionCountAsync`, `MoveSessionsToProjectAsync`.
  **Files**:
    - `tests/WeaveFleet.Testing/Fakes/Repositories/InMemoryProjectRepository.cs`
  **Acceptance**: All 8 methods implemented; scratch project lookup works.

- [x] 13. Create remaining simple repository fakes
  **What**: Create fakes for `ISessionSourceUsageRepository` (2 methods), `ISessionCallbackRepository` (6 methods), `IInstanceRepository` (7 methods), `IOutboxRepository` (5 methods), `IUserRepository` (4 methods), `IUserCredentialRepository` (11 methods). These are all straightforward CRUD with dictionary backing.
  **Files**:
    - `tests/WeaveFleet.Testing/Fakes/Repositories/InMemorySessionSourceUsageRepository.cs`
    - `tests/WeaveFleet.Testing/Fakes/Repositories/InMemorySessionCallbackRepository.cs`
    - `tests/WeaveFleet.Testing/Fakes/Repositories/InMemoryInstanceRepository.cs`
    - `tests/WeaveFleet.Testing/Fakes/Repositories/InMemoryOutboxRepository.cs`
    - `tests/WeaveFleet.Testing/Fakes/Repositories/InMemoryUserRepository.cs`
    - `tests/WeaveFleet.Testing/Fakes/Repositories/InMemoryUserCredentialRepository.cs`
  **Acceptance**: All interfaces fully implemented; basic round-trip works for each.

### Phase 3: Service & Harness Fakes (66 mocks eliminated)

- [x] 14. Create `FakeEventBroadcaster`
  **What**: Implement `IEventBroadcaster` that records all broadcast calls in a `List<BroadcastRecord>` for assertion. `SubscribeAsync` returns an empty async enumerable by default. The record type captures `(Topic, Type, Payload, SequenceNumber, UserId)`. This replaces 17 mocks — the most-mocked service interface.

  Must include an `Action<string, string, object, string?, CancellationToken>? OnBroadcast` callback property, invoked inside every `BroadcastAsync` overload AFTER recording to `Broadcasts`. This supports the `TaskCompletionSource` signaling pattern used in `HarnessEventRelayTests`, where tests need to be notified asynchronously when a broadcast occurs:

  ```csharp
  var signal = new TaskCompletionSource<(string, string)>();
  fakeBroadcaster.OnBroadcast = (topic, type, _, _, _) =>
      signal.TrySetResult((topic, type));
  ```

  **Files**:
    - `tests/WeaveFleet.Testing/Fakes/FakeEventBroadcaster.cs`
  **Acceptance**: `BroadcastAsync` records calls; `OnBroadcast` callback fires after recording; tests can assert on `Broadcasts.Count`, topic, type, and payload.

- [x] 15. Create `FakeCredentialStore`
  **What**: Implement `ICredentialStore` backed by a list of credentials. `StoreCredentialAsync` adds/updates; `ListCredentialsAsync` returns metadata-only summaries; `DeleteCredentialAsync` removes; `GetDecryptedCredentialsAsync` returns the stored credentials (values already in plaintext in the fake). Default: returns empty lists.
  **Files**:
    - `tests/WeaveFleet.Testing/Fakes/FakeCredentialStore.cs`
  **Acceptance**: Store/List/Delete/GetDecrypted round-trip works; default returns empty.

- [x] 16. Promote harness fakes to shared project
  **What**: Move `FakeHarness`, `FakeHarnessRuntime`, and `FakeInstance` (IHarnessSession) from their private test-class locations into `WeaveFleet.Testing/Fakes/`. Make them `public sealed`.

  **`FakeHarnessRuntime`** must support:
  - Configurable availability (`Available` bool, `Reason` string) for `CheckAvailabilityAsync`
  - `SpawnBehavior` property (`Func<HarnessSpawnOptions, CancellationToken, Task<IHarnessSession>>?`) — already planned
  - `ResumeBehavior` property (`Func<HarnessResumeOptions, CancellationToken, Task<IHarnessSession>>?`) for `.ResumeAsync(...).Returns(...)` migration
  - `PreparationResult` property (`RuntimePreparation?`) for configurable `PrepareRuntimeAsync` return value (defaults to `RuntimePreparation.Ready`)
  - `SpawnCalls` list (`List<HarnessSpawnOptions>`) — records every `SpawnAsync` invocation for `Received(1).SpawnAsync(...)` assertions
  - `ResumeCalls` list (`List<HarnessResumeOptions>`) — records every `ResumeAsync` invocation for `DidNotReceive().ResumeAsync(...)` assertions

  **`FakeHarnessSession`** must support:
  - Configurable `InstanceId`, `HarnessType`, `Status`, and `ResumeToken` properties
  - Event emission via a `Channel<HarnessEvent>` — `Emit(HarnessEvent)` and `Complete()` methods (matching existing `FakeInstance` pattern)
  - `SendPromptCalls` list (`List<(string Text, PromptOptions? Options)>`) — records every `SendPromptAsync` invocation
  - `SendCommandCalls` list (`List<CommandOptions>`) — records every `SendCommandAsync` invocation
  - `StopCalled` bool flag — set to `true` when `StopAsync` is called
  - `DeleteCalled` bool flag — set to `true` when `DeleteAsync` is called
  - `AbortCalled` bool flag — set to `true` when `AbortAsync` is called
  - `GetMessagesBehavior` property (`Func<MessageQuery?, CancellationToken, Task<MessagePage>>?`) — for configuring `.GetMessagesAsync(...).ThrowsAsync(...)` or custom return values

  **Files**:
    - `tests/WeaveFleet.Testing/Fakes/FakeHarness.cs`
    - `tests/WeaveFleet.Testing/Fakes/FakeHarnessRuntime.cs`
    - `tests/WeaveFleet.Testing/Fakes/FakeHarnessSession.cs`
  **Acceptance**: All three compile; `FakeHarnessSession` supports `Emit()` and `Complete()` for event streaming tests; `FakeHarnessRuntime.SpawnCalls` records invocations; `FakeHarnessSession.SendPromptCalls` records invocations.

- [x] 17. Create `FakeHarnessRegistry`
  **What**: Implement `IHarnessRegistry` backed by lists of `IHarness` and `IHarnessRuntime`. `GetByType` does case-insensitive lookup. `GetAvailabilityAsync` aggregates availability from runtimes. This is a thin wrapper — the real `HarnessRegistry` is already simple, but we need a fake for Application-layer tests that don't reference Infrastructure.
  **Files**:
    - `tests/WeaveFleet.Testing/Fakes/FakeHarnessRegistry.cs`
  **Acceptance**: `GetByType`, `GetRuntimeByType`, `GetAll`, `GetAvailabilityAsync` all work correctly.

### Phase 4: Eliminate IServiceScopeFactory Mocking (10 mocks eliminated)

- [x] 18. Create `TestServiceScopeFactory` helper
  **What**: Create a helper that builds a real `IServiceScopeFactory` from a `ServiceCollection`. This replaces the manual `IServiceProvider` → `IServiceScope` → `IServiceScopeFactory` mock chains in `HarnessEventRelayTests`. The persistence tests already do this correctly — standardize the pattern. The helper takes an `Action<ServiceCollection>` to configure services.
  **Files**:
    - `tests/WeaveFleet.Testing/Fakes/TestServiceScopeFactory.cs`
  **Acceptance**: `TestServiceScopeFactory.Create(services => { services.AddSingleton<IFoo>(fake); })` returns a working `IServiceScopeFactory`.

- [x] 19. Create `FakeDbConnectionFactory` and `FakeDbConnection`
  **What**: Create a fake `IDbConnectionFactory` that returns a `FakeDbConnection`. The `FakeDbConnection` implements `IDbConnection` with no-op operations and returns a `FakeDbTransaction` from `BeginTransaction()`. This replaces the 2 `Substitute.For<IDbConnection>()` and 2 `Substitute.For<IDbTransaction>()` calls. The existing `StubConnectionFactory` pattern (duplicated in 2 test files) should be consolidated here.
  **Files**:
    - `tests/WeaveFleet.Testing/Fakes/FakeDbConnectionFactory.cs`
  **Acceptance**: `CreateConnection()` returns a connection; `BeginTransaction()` returns a transaction; `Commit()`/`Rollback()` are no-ops.

### Phase 5: SessionOrchestrator Builder — Eliminate Boilerplate

- [x] 20. Create `SessionOrchestratorBuilder`
  **What**: Create a test builder that constructs `SessionOrchestrator` with all dependencies defaulting to in-memory fakes. Individual dependencies can be overridden via fluent methods. This eliminates the 12-17 line mock setup blocks duplicated in `SessionOrchestratorTests`, `SessionServiceTests`, `MultiTenancyTests`, `SessionDelegationsEndpointTests`, and `SessionLifecycleEndpointTests`. The builder exposes the fakes so tests can seed data and assert on them.

  ```csharp
  // Example usage:
  var builder = new SessionOrchestratorBuilder();
  builder.SessionRepository.Seed(session);
  builder.HarnessRegistry.Register(new FakeHarness("opencode", "OpenCode"));
  var orchestrator = builder.Build();
  ```

  **Files**:
    - `tests/WeaveFleet.Testing/Builders/SessionOrchestratorBuilder.cs`
  **Acceptance**: `Build()` returns a working `SessionOrchestrator`; all fakes are accessible for seeding/assertion; default build succeeds without any configuration.

### Phase 6: Migrate Test Files

Each sub-task migrates one or more test files. The pattern for each migration:
1. Replace `Substitute.For<T>()` with the corresponding fake from `WeaveFleet.Testing`
2. Replace `.Returns(value)` setups with fake seeding (e.g., `fakeRepo.Seed(entity)`)
3. Replace `.Received(n).Method()` assertions with fake inspection (e.g., `fakeRepo.InsertedEntities.Count.ShouldBe(1)`)
4. Remove `using NSubstitute;` and `using NSubstitute.ExceptionExtensions;`
5. Verify tests still pass

**Application.Tests (11 files, 105 mocks):**

- [x] 21. Migrate `CredentialStoreTests.cs`
  **What**: Replace `IUserCredentialRepository` and `ICredentialProtector` mocks with `InMemoryUserCredentialRepository` and `FakeCredentialProtector`. This test heavily uses `.Received()` assertions — convert to inspecting the fake's stored state. The `StubUserContext` private class can be replaced with `TestUserContext`.
  **Files**:
    - `tests/WeaveFleet.Application.Tests/Services/CredentialStoreTests.cs`
  **Acceptance**: All 7 tests pass; no NSubstitute references remain.

- [x] 22. Migrate `DelegationServiceTests.cs`
  **What**: Replace `IDelegationRepository` and `IEventBroadcaster` mocks with in-memory fakes. Convert `.Received()` broadcast assertions to `FakeEventBroadcaster.Broadcasts` list inspection. Convert `.DidNotReceive()` to count assertions.
  **Files**:
    - `tests/WeaveFleet.Application.Tests/Services/DelegationServiceTests.cs`
  **Acceptance**: All 5 tests pass; no NSubstitute references remain.

- [x] 23. Migrate `ProjectServiceTests.cs`
  **What**: Replace `IProjectRepository` and `ISessionRepository` mocks with in-memory fakes. Seed data via `fakeRepo.Seed()` instead of `.Returns()`.
  **Files**:
    - `tests/WeaveFleet.Application.Tests/Services/ProjectServiceTests.cs`
  **Acceptance**: All 7 tests pass; no NSubstitute references remain.

- [x] 24. Migrate `UserServiceTests.cs`
  **What**: Replace `IUserRepository`, `ICredentialStore`, and `ISessionRepository` mocks with fakes.
  **Files**:
    - `tests/WeaveFleet.Application.Tests/Services/UserServiceTests.cs`
  **Acceptance**: All 2 tests pass; no NSubstitute references remain.

- [x] 25. Migrate `SessionSourceResolutionServiceTests.cs`
  **What**: Replace `IWorkspaceRootRepository` mock with `InMemoryWorkspaceRootRepository`. Seed workspace roots directly.
  **Files**:
    - `tests/WeaveFleet.Application.Tests/Services/SessionSourceResolutionServiceTests.cs`
  **Acceptance**: All 5 tests pass; no NSubstitute references remain.

- [x] 26. Migrate `RepositoryServiceTests.cs`
  **What**: Replace `IWorkspaceRootRepository` mocks (4 instances, one per test method) with a shared `InMemoryWorkspaceRootRepository`. The tests already use real `ServiceCollection` — just swap the mock registration.
  **Files**:
    - `tests/WeaveFleet.Application.Tests/Services/RepositoryServiceTests.cs`
  **Acceptance**: All 4 tests pass; no NSubstitute references remain.

- [x] 27. Migrate `ManagedWorkspaceTests.cs`
  **What**: Replace `IWorkspaceRepository` mocks (4 instances) with `InMemoryWorkspaceRepository`. These tests only use the mock as a sink — they never query it — so the fake just needs to accept inserts.
  **Files**:
    - `tests/WeaveFleet.Application.Tests/Services/ManagedWorkspaceTests.cs`
  **Acceptance**: All 4 tests pass; no NSubstitute references remain.

- [x] 28. Migrate `SessionServiceTests.cs`
  **What**: Replace all 13 mocks with fakes. Use `SessionOrchestratorBuilder` to construct the orchestrator. Seed session data into `InMemorySessionRepository`.
  **Files**:
    - `tests/WeaveFleet.Application.Tests/Services/SessionServiceTests.cs`
  **Acceptance**: All 10 tests pass; no NSubstitute references remain.

- [x] 29. Migrate `SessionOrchestratorTests.cs`
  **What**: The largest single file (18 mocks as fields, 1041 lines). Use `SessionOrchestratorBuilder`. Replace all `.Returns()` setups with fake seeding. Replace all `.Received()` assertions with fake state inspection. The `ConfigureHarnessAndScratchProject()` helper becomes builder configuration. This is the hardest migration — take care with the `.ThrowsAsync()` test (line 166) which needs the fake to be configured to throw.
  **Files**:
    - `tests/WeaveFleet.Application.Tests/Services/SessionOrchestratorTests.cs`
  **Acceptance**: All tests pass; no NSubstitute references remain.

- [x] 30. Migrate `SessionOrchestratorCredentialTests.cs`
  **What**: Replace all 16 mocks with fakes via `SessionOrchestratorBuilder`. Similar pattern to `SessionOrchestratorTests`.
  **Files**:
    - `tests/WeaveFleet.Application.Tests/Services/SessionOrchestratorCredentialTests.cs`
  **Acceptance**: All tests pass; no NSubstitute references remain.

- [x] 31. Migrate `MultiTenancyTests.cs`
  **What**: Replace all mocks (2 test methods, each with 15+ mocks) with `SessionOrchestratorBuilder`. This test constructs the orchestrator inline in each test — the builder will dramatically reduce boilerplate.
  **Files**:
    - `tests/WeaveFleet.Application.Tests/Services/MultiTenancyTests.cs`
  **Acceptance**: All 3 tests pass; no NSubstitute references remain.

**Infrastructure.Tests (13 files, 75 mocks):**

- [x] 32. Migrate `HarnessEventRelayTests.cs`
  **What**: Replace the `BuildDependencies()` helper that creates 5 mocks (`IEventBroadcaster`, `ISessionRepository`, `IServiceProvider`, `IServiceScope`, `IServiceScopeFactory`) with fakes. Use `FakeEventBroadcaster` with the `OnBroadcast` callback for the `TaskCompletionSource` signaling pattern. Use `TestServiceScopeFactory` to build a real scope factory with `InMemorySessionRepository` registered. The `FakeInstance` is already a hand-crafted fake — replace it with the promoted `FakeHarnessSession`.
  **Files**:
    - `tests/WeaveFleet.Infrastructure.Tests/Services/HarnessEventRelayTests.cs`
  **Acceptance**: All tests pass; no NSubstitute references remain.

- [x] 33. Migrate `OpenCodeHarnessSessionPersistenceTests.cs`
  **What**: The largest infrastructure test file (1611 lines, ~50 mocks). The `BuildPersistenceDependencies()` helper already uses a real `ServiceCollection` — replace the `Substitute.For<>` calls within it with fakes. The delegation test at line 1065 constructs a full orchestrator — use `SessionOrchestratorBuilder`. Replace `IDbConnection`/`IDbTransaction` mocks with `FakeDbConnectionFactory`.
  **Files**:
    - `tests/WeaveFleet.Infrastructure.Tests/Harnesses/OpenCode/OpenCodeHarnessSessionPersistenceTests.cs`
  **Acceptance**: All tests pass; no NSubstitute references remain.

- [x] 34. Migrate `ClaudeCodeHarnessSessionPersistenceTests.cs`
  **What**: Same pattern as OpenCode persistence tests. Replace `BuildPersistenceDependencies()` and `BuildFullPersistenceDependencies()` helpers with fakes.
  **Files**:
    - `tests/WeaveFleet.Infrastructure.Tests/Harnesses/ClaudeCode/ClaudeCodeHarnessSessionPersistenceTests.cs`
  **Acceptance**: All tests pass; no NSubstitute references remain.

- [x] 35. Migrate harness runtime tests (4 files)
  **What**: Replace `Substitute.For<IServiceScopeFactory>()` in `OpenCodeHarnessTests.cs`, `OpenCodeHarnessPreparationTests.cs`, `ClaudeCodeHarnessTests.cs`, and `ClaudeCodeHarnessPreparationTests.cs`. These each have exactly 1 mock — the `IServiceScopeFactory` passed to the harness runtime constructor. Use `TestServiceScopeFactory.CreateEmpty()` (a scope factory with no registrations — these tests never resolve from it).
  **Files**:
    - `tests/WeaveFleet.Infrastructure.Tests/Harnesses/OpenCode/OpenCodeHarnessTests.cs`
    - `tests/WeaveFleet.Infrastructure.Tests/Harnesses/OpenCode/OpenCodeHarnessPreparationTests.cs`
    - `tests/WeaveFleet.Infrastructure.Tests/Harnesses/ClaudeCode/ClaudeCodeHarnessTests.cs`
    - `tests/WeaveFleet.Infrastructure.Tests/Harnesses/ClaudeCode/ClaudeCodeHarnessPreparationTests.cs`
  **Acceptance**: All tests pass; no NSubstitute references remain in these 4 files.

- [x] 36. Migrate `HarnessRegistryTests.cs`
  **What**: The private `FakeHarness` and `FakeHarnessRuntime` are already hand-crafted fakes. Replace them with the shared versions from `WeaveFleet.Testing`. Remove the private class definitions.
  **Files**:
    - `tests/WeaveFleet.Infrastructure.Tests/Harnesses/HarnessRegistryTests.cs`
  **Acceptance**: All tests pass; private fake classes removed.

- [x] 37. Migrate `GitHubSessionSourceProviderTests.cs`
  **What**: Replace `IPluginStateStore`, `IUserCredentialRepository`, `ICredentialProtector`, and `IUserContext` mocks with fakes. The `FakeGitHubHandler` and `TestHttpClientFactory` are already hand-crafted — keep them as private test helpers (they're test-specific HTTP fakes, not general-purpose).
  **Files**:
    - `tests/WeaveFleet.Infrastructure.Tests/SessionSources/GitHubSessionSourceProviderTests.cs`
  **Acceptance**: All tests pass; no NSubstitute references remain.

- [x] 38. Migrate `RepositorySessionSourceProviderTests.cs`
  **What**: Replace `IWorkspaceRootRepository` mock with `InMemoryWorkspaceRootRepository`. The tests already use real `ServiceCollection` — just swap the registration.
  **Files**:
    - `tests/WeaveFleet.Infrastructure.Tests/SessionSources/RepositorySessionSourceProviderTests.cs`
  **Acceptance**: All tests pass; no NSubstitute references remain.

- [x] 39. Migrate `AnalyticsRepositoryTests.cs`
  **What**: Replace 3× `Substitute.For<IUserContext>()` with `new TestUserContext(...)`. The file contains two test classes: `AnalyticsRepositoryTests` (1 mock for `"local-user"`) and `AnalyticsRepositoryTenantIsolationTests` (2 mocks for `"user-a"` and `"user-b"`). These are trivial replacements — the mocks only configure `UserId` and nothing else. The `TestUserContext` constructor already accepts a `userId` parameter.
  **Files**:
    - `tests/WeaveFleet.Infrastructure.Tests/Analytics/AnalyticsRepositoryTests.cs`
  **Acceptance**: All tests pass; `using NSubstitute;` removed; no `Substitute.For` references remain.

- [x] 40. Migrate `AnalyticsWriterServiceTests.cs`
  **What**: Replace 4 mocks: `ISessionRepository` (field-level), `IServiceScope`, `IServiceProvider`, and `IServiceScopeFactory` (all in `InitializeAsync`). Use `TestServiceScopeFactory` with `InMemorySessionRepository` registered, replacing the manual `IServiceProvider` → `IServiceScope` → `IServiceScopeFactory` mock chain. The test at line 213 uses `_sessionRepo.Received(1).IncrementTokensAsync(...)` — convert to inspecting `InMemorySessionRepository`'s call-tracking (the `IncrementTokensAsyncCalls` list or equivalent inspection API on the fake).
  **Files**:
    - `tests/WeaveFleet.Infrastructure.Tests/Analytics/AnalyticsWriterServiceTests.cs`
  **Acceptance**: All tests pass; `using NSubstitute;` removed; no `Substitute.For` or `.Received()` references remain.

**Api.Tests (2 files, 52 mocks):**

- [x] 41. Migrate `SessionDelegationsEndpointTests.cs`
  **What**: Replace `BuildSessionRepository()` and `BuildSessionOrchestrator()` helpers with fakes and `SessionOrchestratorBuilder`. The `BuildSessionOrchestrator()` method creates 15 mocks — the builder eliminates all of them.
  **Files**:
    - `tests/WeaveFleet.Api.Tests/Endpoints/SessionDelegationsEndpointTests.cs`
  **Acceptance**: All 2 tests pass; no NSubstitute references remain.

- [x] 42. Migrate `SessionLifecycleEndpointTests.cs`
  **What**: Replace `BuildSessionService()` helpers (2 overloads) with fakes and `SessionOrchestratorBuilder`. Same pattern as delegations tests.
  **Files**:
    - `tests/WeaveFleet.Api.Tests/Endpoints/SessionLifecycleEndpointTests.cs`
  **Acceptance**: All 6 tests pass; no NSubstitute references remain.

### Phase 7: Cleanup & Policy

- [x] 43. Remove NSubstitute from all project files
  **What**: Remove `<PackageReference Include="NSubstitute" />` from all 3 test `.csproj` files. Remove `<PackageVersion Include="NSubstitute" ... />` from `Directory.Packages.props`. Remove the `<!-- Testing Mocks -->` comment.
  **Files**:
    - `tests/WeaveFleet.Api.Tests/WeaveFleet.Api.Tests.csproj`
    - `tests/WeaveFleet.Application.Tests/WeaveFleet.Application.Tests.csproj`
    - `tests/WeaveFleet.Infrastructure.Tests/WeaveFleet.Infrastructure.Tests.csproj`
    - `Directory.Packages.props`
  **Acceptance**: `dotnet restore` succeeds; `grep -r "NSubstitute" *.csproj Directory.Packages.props` returns zero results.

- [x] 44. Final verification
  **What**: Run full test suite in Release mode. Verify zero NSubstitute references anywhere in the codebase. Verify all tests pass.
  **Acceptance**: `dotnet build -c Release` succeeds; `dotnet test -c Release` passes all tests; `grep -rn "NSubstitute\|Substitute\.For\|Arg\.Any<\|Arg\.Is<\|\.ThrowsAsync(\|\.DidNotReceive()" tests/ --include="*.cs"` returns zero results.

## Design Decisions

### Why In-Memory Fakes Over SQLite for Application-Layer Tests?

The codebase already has real SQLite integration tests in `WeaveFleet.Infrastructure.Tests/Data/Repositories/`. Those test the Dapper SQL. Application-layer tests should NOT depend on SQLite — they test business logic, not data access. In-memory `Dictionary<string, T>` fakes are:
- **Faster**: No database setup/teardown
- **Simpler**: No migration runner, no connection management
- **More focused**: Tests verify business logic, not SQL correctness
- **Portable**: No native SQLite dependency in Application.Tests

### Fake Design Patterns

Each repository fake follows this pattern:

```csharp
public sealed class InMemorySessionRepository : ISessionRepository
{
    private readonly Dictionary<string, Session> _store = new();

    // Seeding API for tests
    public void Seed(Session session) => _store[session.Id] = session;
    public void Seed(params Session[] sessions) { foreach (var s in sessions) Seed(s); }

    // Inspection API for assertions
    public IReadOnlyList<Session> All => _store.Values.ToList();

    // Interface implementation
    public Task InsertAsync(Session session)
    {
        _store[session.Id] = session;
        return Task.CompletedTask;
    }

    // IDbConnection/IDbTransaction overloads delegate to parameterless version
    public Task InsertAsync(IDbConnection connection, IDbTransaction? transaction, Session session)
        => InsertAsync(session);

    public Task<Session?> GetByIdAsync(string id)
        => Task.FromResult(_store.GetValueOrDefault(id));

    // ... etc
}
```

Each service fake follows this pattern:

```csharp
public sealed class FakeEventBroadcaster : IEventBroadcaster
{
    public List<BroadcastRecord> Broadcasts { get; } = [];

    /// <summary>
    /// Optional callback invoked after every broadcast is recorded.
    /// Supports TaskCompletionSource signaling in async tests (e.g., HarnessEventRelayTests).
    /// </summary>
    public Action<string, string, object, string?, CancellationToken>? OnBroadcast { get; set; }

    public Task BroadcastAsync(string topic, string type, object payload)
    {
        Broadcasts.Add(new(topic, type, payload, null, null));
        OnBroadcast?.Invoke(topic, type, payload, null, CancellationToken.None);
        return Task.CompletedTask;
    }

    public Task BroadcastAsync(string topic, string type, object payload, string? userId, CancellationToken ct)
    {
        Broadcasts.Add(new(topic, type, payload, null, userId));
        OnBroadcast?.Invoke(topic, type, payload, userId, ct);
        return Task.CompletedTask;
    }

    // ... other overloads follow same pattern

    public sealed record BroadcastRecord(string Topic, string Type, object Payload, long? SequenceNumber, string? UserId);
}
```

### Handling `.Received()` Assertion Migration

NSubstitute `.Received(n).Method(args)` assertions need to be converted. Strategy per pattern:

| NSubstitute Pattern | Fake Equivalent |
|---|---|
| `repo.Received(1).InsertAsync(Arg.Is<T>(predicate))` | `fake.All.Count(predicate).ShouldBe(1)` or `fake.InsertedItems.ShouldContain(predicate)` |
| `repo.DidNotReceive().InsertAsync(Arg.Any<T>())` | `fake.All.ShouldBeEmpty()` or track inserts separately |
| `broadcaster.Received(1).BroadcastAsync(topic, type, ...)` | `fake.Broadcasts.Count(b => b.Topic == topic).ShouldBe(1)` |
| `repo.Received(1).DeleteAsync("id")` | Track deletes in a `DeletedIds` list |

For fakes where we need to distinguish "seeded" data from "inserted during test" data, add an `Inserted` list that only captures items added via `InsertAsync` (not `Seed`).

### Handling `.ThrowsAsync()` Migration

For tests like `SessionOrchestratorTests.CreateSessionAsync_WhenSpawnThrows_ReturnsUnexpectedError`, the `FakeHarnessRuntime` needs configurable behaviors and call-tracking:

```csharp
public sealed class FakeHarnessRuntime : IHarnessRuntime
{
    public string HarnessType { get; set; } = "opencode";

    // Configurable behaviors
    public bool Available { get; set; } = true;
    public string? AvailabilityReason { get; set; }
    public RuntimePreparation? PreparationResult { get; set; } // defaults to Ready
    public Func<HarnessSpawnOptions, CancellationToken, Task<IHarnessSession>>? SpawnBehavior { get; set; }
    public Func<HarnessResumeOptions, CancellationToken, Task<IHarnessSession>>? ResumeBehavior { get; set; }
    public FakeHarnessSession DefaultSession { get; set; } = new("inst-1");

    // Call-tracking for assertions
    public List<HarnessSpawnOptions> SpawnCalls { get; } = [];
    public List<HarnessResumeOptions> ResumeCalls { get; } = [];

    public Task<IHarnessSession> SpawnAsync(HarnessSpawnOptions options, CancellationToken ct)
    {
        SpawnCalls.Add(options);
        return SpawnBehavior?.Invoke(options, ct)
               ?? Task.FromResult<IHarnessSession>(DefaultSession);
    }

    public Task<IHarnessSession> ResumeAsync(HarnessResumeOptions options, CancellationToken ct)
    {
        ResumeCalls.Add(options);
        return ResumeBehavior?.Invoke(options, ct)
               ?? Task.FromResult<IHarnessSession>(DefaultSession);
    }

    // ... CheckAvailabilityAsync, PrepareRuntimeAsync
}
```

Tests configure: `fake.SpawnBehavior = (_, _) => throw new InvalidOperationException("process failed");`

The `FakeHarnessSession` follows the same call-tracking pattern:

```csharp
public sealed class FakeHarnessSession : IHarnessSession
{
    private readonly Channel<HarnessEvent> _channel = Channel.CreateUnbounded<HarnessEvent>();

    public FakeHarnessSession(string instanceId) { InstanceId = instanceId; }

    // Configurable properties
    public string InstanceId { get; }
    public string HarnessType { get; set; } = "opencode";
    public string? ResumeToken { get; set; }
    public HarnessSessionStatus Status { get; set; } = HarnessSessionStatus.Running;

    // Call-tracking for assertions
    public List<(string Text, PromptOptions? Options)> SendPromptCalls { get; } = [];
    public List<CommandOptions> SendCommandCalls { get; } = [];
    public bool StopCalled { get; private set; }
    public bool DeleteCalled { get; private set; }
    public bool AbortCalled { get; private set; }

    // Configurable behaviors
    public Func<MessageQuery?, CancellationToken, Task<MessagePage>>? GetMessagesBehavior { get; set; }

    // Event emission (for streaming tests)
    public void Emit(HarnessEvent evt) => _channel.Writer.TryWrite(evt);
    public void Complete() => _channel.Writer.Complete();

    public Task SendPromptAsync(string text, PromptOptions? options, CancellationToken ct)
    {
        SendPromptCalls.Add((text, options));
        return Task.CompletedTask;
    }

    public Task StopAsync(CancellationToken ct) { StopCalled = true; return Task.CompletedTask; }
    public Task DeleteAsync(CancellationToken ct) { DeleteCalled = true; return Task.CompletedTask; }
    public Task AbortAsync(CancellationToken ct) { AbortCalled = true; return Task.CompletedTask; }

    public Task<MessagePage> GetMessagesAsync(MessageQuery? query, CancellationToken ct)
        => GetMessagesBehavior?.Invoke(query, ct)
           ?? Task.FromResult(new MessagePage([], false));

    // ... SubscribeAsync, other methods
}
```

### IServiceScopeFactory: Why Not Refactor Production Code?

The `IServiceScopeFactory` mocking in `HarnessEventRelayTests` is a code smell — the relay resolves `ISessionRepository` from a scope at runtime. The correct fix would be to inject `ISessionRepository` directly. However, this plan's guardrail is "no production code changes." The `TestServiceScopeFactory` helper is a pragmatic workaround. A follow-up task should refactor the relay to accept its dependencies directly.

## Mocking Policy (Going Forward)

Add this comment block to `tests/Directory.Build.props`:

```xml
<!--
  TESTING POLICY: No mocking libraries allowed.

  This codebase uses hand-crafted fakes in WeaveFleet.Testing for all test doubles.
  Mocking libraries (NSubstitute, Moq, FakeItEasy, etc.) are prohibited.

  When to use fakes:
  - Repository interfaces → InMemory* fakes in WeaveFleet.Testing/Fakes/Repositories/
  - Service interfaces → Fake* classes in WeaveFleet.Testing/Fakes/
  - HTTP dependencies → Custom HttpMessageHandler subclasses (test-local)

  When to use real implementations:
  - Value objects, DTOs, entities → always use real instances
  - Services with no I/O → use the real implementation directly
  - SQLite repositories → use TestDbHelper.CreateSharedDbAsync() (Infrastructure.Tests only)

  When mocking is acceptable (rare exceptions):
  - Never. If you need a test double, create a fake.
  - If a fake would be unreasonably complex (50+ methods, deep state machine),
    consider whether the interface is too large and should be split.
-->
```

## Verification
- [x] `dotnet build -c Release` succeeds with zero warnings
- [x] `dotnet test -c Release` passes all tests in all projects
- [x] `grep -rn "NSubstitute" tests/ src/ --include="*.cs" --include="*.csproj"` returns zero results
- [x] `grep -rn "Substitute\.For" tests/ --include="*.cs"` returns zero results
- [x] `grep -rn "Arg\.Any<\|Arg\.Is<\|\.DidNotReceive()\|\.ThrowsAsync(" tests/ --include="*.cs"` returns zero results (NSubstitute-specific APIs)
- [x] No test behavior has changed — same assertions, same coverage
- [x] `WeaveFleet.Testing` project builds independently
- [x] `WeaveFleet.slnx` includes `WeaveFleet.Testing` in the `/tests/` folder
