# Learnings: Fleet Board MVP 1 — Core Board & Card Management

## Task 1: Database Migration
- **Discrepancy**: The plan's broader referenced backend/test paths assume `src/WeaveFleet.Infrastructure/Repositories/`, `tests/WeaveFleet.Tests/Repositories/`, and `tests/WeaveFleet.Tests/Endpoints/`, but the actual repo uses `src/WeaveFleet.Infrastructure/Data/Repositories/`, `tests/WeaveFleet.Infrastructure.Tests/Data/Repositories/`, and `tests/WeaveFleet.Api.Tests/Endpoints/`.
- **Resolution**: Kept the migration in the planned migrations folder and noted the real repository/test locations for subsequent tasks.
- **Suggestion**: Update future board plans to reference the actual repository and test project paths to reduce delegation ambiguity.

## Task 4: Repository Implementation
- **Discrepancy**: The plan specified `src/WeaveFleet.Infrastructure/Repositories/BoardRepository.cs`, but the actual repository convention in this codebase is `src/WeaveFleet.Infrastructure/Data/Repositories/`.
- **Resolution**: Implemented the repository at `src/WeaveFleet.Infrastructure/Data/Repositories/BoardRepository.cs` and registered it from `DependencyInjection.cs`.
- **Suggestion**: Update the plan's repository file path to the actual `Data/Repositories` location.

## Task 5: API Endpoints
- **Discrepancy**: The plan listed only endpoint files, but verifying “all endpoints callable and returning correct status codes” required adding endpoint integration tests in the actual API test project.
- **Resolution**: Added `tests/WeaveFleet.Api.Tests/Endpoints/BoardEndpointTests.cs` alongside `BoardEndpoints.cs` and endpoint registration changes.
- **Suggestion**: Include the actual API test project path and expected endpoint verification coverage in future plans.

## Task 7: Frontend API Client
- **Discrepancy**: Frontend verification tooling (`vue-tsc`, `eslint`, `vite`, `vitest`) was unavailable in the environment, so client verification could not rely on local frontend commands.
- **Resolution**: Verified the new API client against the implemented backend endpoint surface and existing frontend request utility patterns.
- **Suggestion**: Ensure frontend toolchain dependencies are installed in the workspace before frontend-plan execution begins.

## Task 8: Frontend Store Rewrite
- **Discrepancy**: The existing board UI still depends on legacy session/group/filter projections, while the MVP 1 store must become board/lane/card-centric.
- **Resolution**: Rewrote the store around persistent board/lane/card state and kept compatibility computed projections so the UI can transition in the next task without breaking immediately.
- **Suggestion**: Future plans should call out transitional compatibility requirements when replacing mock feature stores that already drive live UI.

## Task 9: Frontend Kanban UI
- **Discrepancy**: The plan listed only the three board component files, but the changed `KanbanCard` prop contract also required updating its component test to keep coverage aligned.
- **Resolution**: Updated the three board components and adjusted `client/src/components/__tests__/KanbanCard.test.ts` to the new `BoardCard` contract.
- **Suggestion**: Future UI plans should mention affected component test files when public props/events are expected to change.

## Task 10: Frontend Tests
- **Discrepancy**: Verifying lane-management and card-creation interactions required an additional board-level component test file beyond the plan's listed store test file.
- **Resolution**: Updated `client/src/stores/__tests__/board.test.ts` and added `client/src/components/__tests__/KanbanBoard.test.ts` with mocked board API coverage.
- **Suggestion**: Explicitly include store and component test targets in future frontend plan steps when interaction coverage is required.
