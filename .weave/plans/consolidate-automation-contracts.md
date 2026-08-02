# Consolidate AutomationContracts into ApiResponses

## TL;DR
Move the 4 records from `Api/Contracts/AutomationContracts.cs` into `Endpoints/ApiResponses.cs`, update two consuming files, and delete the `Contracts/` folder.

## Context
All other endpoint request/response records live in `src/WeaveFleet.Api/Endpoints/ApiResponses.cs`. The `Contracts/` folder contains only `AutomationContracts.cs` with 4 records. Two files import the `WeaveFleet.Api.Contracts` namespace.

## Scope
- In scope: Move records, update usings, delete folder
- Out of scope: Renaming types, changing behaviour
- Constraints: Must not break build

## Objectives
- Eliminate the orphaned `Contracts/` folder
- Consistent location for all API request/response records

## Dependencies and Order
1. Move records first, then update imports, then delete. Order matters because deletion before import update would break the build.

## Tasks

- [ ] 1. Move records into ApiResponses.cs
  - **What**: Copy `CreateAutomationRequest`, `UpdateAutomationRequest`, `AutomationResponse`, `AutomationListResponse` from `AutomationContracts.cs` into `ApiResponses.cs` under a `// ── Automations ──` section comment. Use namespace `WeaveFleet.Api.Endpoints`.
  - **Files**: `src/WeaveFleet.Api/Endpoints/ApiResponses.cs`
  - **Depends on**: None
  - **Acceptance**:
    - All 4 records present in `ApiResponses.cs` under the automations section

- [ ] 2. Update imports in AutomationEndpoints.cs
  - **What**: Remove `using WeaveFleet.Api.Contracts;` (the types are now in the same namespace or accessible via existing usings).
  - **Files**: `src/WeaveFleet.Api/Endpoints/AutomationEndpoints.cs`
  - **Depends on**: Task 1
  - **Acceptance**:
    - No reference to `WeaveFleet.Api.Contracts` in this file

- [ ] 3. Update imports in AutomationEndpointTests.cs
  - **What**: Replace `using WeaveFleet.Api.Contracts;` with `using WeaveFleet.Api.Endpoints;` (if not already present).
  - **Files**: `tests/WeaveFleet.IntegrationTests/Automations/AutomationEndpointTests.cs`
  - **Depends on**: Task 1
  - **Acceptance**:
    - No reference to `WeaveFleet.Api.Contracts` in this file

- [ ] 4. Delete Contracts folder
  - **What**: Delete `src/WeaveFleet.Api/Contracts/AutomationContracts.cs` and the `Contracts/` directory.
  - **Files**: `src/WeaveFleet.Api/Contracts/AutomationContracts.cs`
  - **Depends on**: Tasks 2, 3
  - **Acceptance**:
    - Folder no longer exists

- [ ] 5. Verify build
  - **What**: Run `dotnet build` from solution root.
  - **Depends on**: Task 4
  - **Acceptance**:
    - Build succeeds with zero errors

## Verification
```bash
dotnet build
rg "WeaveFleet\.Api\.Contracts" src/ tests/
# Expected: no matches, build passes
```
