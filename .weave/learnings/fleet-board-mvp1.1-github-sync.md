# Learnings: fleet-board-mvp1.1-github-sync

## Task 3: BoardSource Repository Methods
- **Discrepancy**: The plan referenced `src/WeaveFleet.Infrastructure/Repositories/BoardRepository.cs`, but the actual repository implementation lives at `src/WeaveFleet.Infrastructure/Data/Repositories/BoardRepository.cs`.
- **Resolution**: Updated the real repository implementation file and added integration tests in `tests/WeaveFleet.Infrastructure.Tests/Data/Repositories/BoardRepositoryTests.cs` to verify CRUD behavior.
- **Suggestion**: Reference the concrete repository path used in the current solution structure and include infrastructure test coverage files when acceptance requires behavior verification.

## Task 4: Board Sync Service
- **Discrepancy**: The plan listed only the interface and service files, but the implementation also required DI registration and supporting test updates to make the new service resolvable and verifiable.
- **Resolution**: Added the service registration in `src/WeaveFleet.Infrastructure/DependencyInjection.cs` and updated `tests/WeaveFleet.Infrastructure.Tests/Services/BoardSyncServiceTests.cs` alongside the service implementation.
- **Suggestion**: Include dependency-injection touchpoints and expected verification test files in service-oriented plan tasks.

## Task 5: Source & Sync API Endpoints
- **Discrepancy**: The plan scoped the task to `BoardEndpoints.cs`, but endpoint integration required API test updates and a test host DI override hook.
- **Resolution**: Updated `tests/WeaveFleet.Api.Tests/Endpoints/BoardEndpointTests.cs` and `tests/WeaveFleet.Api.Tests/Infrastructure/ApiWebApplicationFactory.cs` in addition to `BoardEndpoints.cs`.
- **Suggestion**: For endpoint tasks, include expected API test files and any test-host infrastructure that may need extension.

## Task 6: Backend Sync Tests
- **Discrepancy**: The plan referenced `tests/WeaveFleet.Tests/Services/BoardSyncServiceTests.cs`, but the actual backend sync tests live in `tests/WeaveFleet.Infrastructure.Tests/Services/BoardSyncServiceTests.cs`.
- **Resolution**: Updated the real infrastructure test file and expanded it to cover the required sync scenarios with focused tests.
- **Suggestion**: Reference the actual test project path for infrastructure service coverage and align acceptance wording with implemented behavior where title is also source-owned.

## Task 7: Frontend Source Configuration UI
- **Discrepancy**: The plan listed only `client/src/components/board/BoardSourceConfig.vue`, but the UI integration also required `KanbanBoard.vue`, API client additions, and a dedicated component test.
- **Resolution**: Added the new source config component, composed it into `client/src/components/board/KanbanBoard.vue`, extended `client/src/lib/board-api.ts`, and added `client/src/components/__tests__/BoardSourceConfig.test.ts`.
- **Suggestion**: Frontend UI tasks should include the likely composition surface, API client touchpoints, and expected test files when the feature is not standalone.

## Task 8: Frontend Sync Button & Feedback
- **Discrepancy**: The plan scoped the task to `KanbanBoard.vue` and `board.ts`, but implementation also required extending `client/src/lib/board-api.ts` and updating frontend tests.
- **Resolution**: Added sync API types/helpers in `client/src/lib/board-api.ts`, implemented store/UI sync behavior, and updated `client/src/stores/__tests__/board.test.ts`, `client/src/components/__tests__/KanbanBoard.test.ts`, and `client/src/components/__tests__/BoardSourceConfig.test.ts`.
- **Suggestion**: For frontend mutation features, include API client and verification test files in the plan alongside the component and store files.

## Task 9: Frontend Synced Card Treatment
- **Discrepancy**: The plan scoped the task to `client/src/components/board/KanbanCard.vue`, but verification required a dedicated card component test file.
- **Resolution**: Updated `client/src/components/board/KanbanCard.vue` and added/updated `client/src/components/__tests__/KanbanCard.test.ts` to cover manual, synced, and stale card presentation.
- **Suggestion**: Component presentation tasks should include the direct component test file when acceptance depends on visual/state distinctions.

## Task 10: Frontend API Client Extensions
- **Discrepancy**: The required API client extensions were already implemented during earlier frontend tasks, so this task became a verification-and-coverage step rather than a code change in the listed file.
- **Resolution**: Verified `client/src/lib/board-api.ts` already exposes typed source CRUD and sync helpers, and added `client/src/lib/__tests__/board-api.test.ts` to lock the contract down.
- **Suggestion**: When later plan tasks depend on earlier supporting work, either reference that dependency explicitly or collapse duplicated deliverables to avoid redundant implementation tasks.

## Task 11: Frontend Tests
- **Discrepancy**: The plan described source management store logic and sync flow, but earlier tasks had already added substantial component/API coverage; the remaining gap was narrower store-level sync verification.
- **Resolution**: Extended `client/src/stores/__tests__/board.test.ts` to verify lane/card refresh after sync and failure-path behavior while preserving existing state.
- **Suggestion**: Split frontend test tasks by layer (store, component, API client) or list the intended missing assertions to reduce overlap with earlier UI tasks.

## Task 15: Removing a source does not delete its cards
- **Discrepancy**: The implementation already preserved cards on source deletion, but the existing integration test did not explicitly prove that cards still remained after deleting the source.
- **Resolution**: Extended `tests/WeaveFleet.Api.Tests/Endpoints/BoardEndpointTests.cs` with post-delete card assertions to make the DoD demonstrable.
- **Suggestion**: Definition-of-done verification tasks should specify when explicit proof in tests is required, even if code inspection already suggests the behavior.
