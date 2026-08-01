# OpenAPI Spec Generation & Typed TypeScript Client

## TL;DR
Add OpenAPI spec generation to the ASP.NET Core backend, create an export script, wire up `openapi-typescript` + `openapi-fetch` for a fully typed API client, then migrate all 60+ call sites from the hand-rolled `apiFetch()` to the typed client and create an agent skill for programmatic API interaction.

## Context

Weave Fleet is a .NET 10 minimal API backend (`src/WeaveFleet.Api/`) with a Vue 3 + TypeScript frontend (`client/`). The frontend currently uses a hand-rolled `apiFetch()` wrapper (`client/src/lib/api-client.ts`) with ~860 lines of manually maintained types (`client/src/lib/api-types.ts`). The Foundry project at `C:\source\foundry` already implements the target pattern.

**Key files:**
- `src/WeaveFleet.Api/Program.cs` -- app builder, middleware pipeline
- `src/WeaveFleet.Api/WeaveFleet.Api.csproj` -- has AOT/trimming enabled for Release
- `src/WeaveFleet.Api/Endpoints/EndpointExtensions.cs` -- registers all 25+ endpoint groups
- `src/WeaveFleet.Api/JsonContext.cs` -- source-generated `ApiJsonContext`
- `client/src/lib/api-client.ts` -- `apiFetch()`, `apiUrl()`, `wsUrl()`, CSRF cookie logic
- `client/src/lib/api-types.ts` -- manual request/response types (~860 lines)
- `client/package.json` -- uses `bun` (per AGENTS.md)

**Reference implementation (Foundry):**
- `src/Foundry.Api/Program.cs` lines 27-35 -- `AddOpenApi()` with document transformer
- `src/Foundry.Api/Program.cs` line 136 -- `app.MapOpenApi()`
- `scripts/export-openapi.ps1` -- starts API, downloads spec, kills process
- `src/Foundry.Web/package.json` -- `openapi-typescript` (dev) + `openapi-fetch` (runtime)
- `src/Foundry.Web/src/api/client.ts` -- `createClient<paths>()` wrapper
- `C:\Users\piete\.config\opencode\skills\foundry-api\SKILL.md` -- agent skill pattern

**AOT/Trimming constraint:** The project enables `PublishAot` and `PublishTrimmed` for Release builds. .NET 10's `Microsoft.AspNetCore.OpenApi` supports AOT natively, so no special workarounds are needed. The OpenAPI spec only needs to be exported during development (Debug builds), not in production.

**Client-only types that must survive migration:** Several types in `api-types.ts` are constructed client-side from WebSocket events and are not REST API response shapes. These cannot come from OpenAPI generation and must be moved to a dedicated file rather than deleted:
- `AccumulatedMessage`, `AccumulatedPart`, `AccumulatedTextPart`, `AccumulatedReasoningPart`, `AccumulatedToolPart`, `AccumulatedFilePart` (built client-side from WS events)
- `WebSocketEvent`, `CommittedSessionEvent` (WS protocol types)
- `DelegationDto` (WS-derived)
- Re-exports of `SessionActivityStatus`, `SessionActionCapabilities`, `SessionLifecycleStatus`, `SessionRetentionStatus`, `InstanceStatus` (from `@/lib/types`)

## Scope

- In scope:
  - **Phase 1**: Add OpenAPI generation to the backend, create export script, add `openapi-typescript` + `openapi-fetch`, generate typed schema, create typed client wrapper
  - **Phase 2**: Migrate all `apiFetch()` call sites to the typed client, extract client-only types from `api-types.ts`, delete `api-types.ts`, create weave-fleet-api agent skill

- Out of scope:
  - WebSocket endpoints (not representable in OpenAPI; `wsUrl()` and `use-weave-socket.ts` stay as-is)
  - `apiUrl()` usage in `LoginPage.vue` (URL construction, not a fetch call)
  - `setApiBase()` (kept for multi-backend/testing scenarios)

- Constraints / assumptions:
  - Package manager is `bun`, not npm
  - OpenAPI endpoint should only be mapped in Development environment (not exposed in production/AOT builds)
  - The typed client must support the same CSRF token injection and `credentials: "include"` as the current `apiFetch()`
  - The export script must work on Windows (PowerShell 7+)
  - Each migration batch must leave the codebase in a compiling, test-passing state
  - `api-client.ts` is not deleted -- it retains `apiUrl()`, `wsUrl()`, `setApiBase()`, and the CSRF cookie helper (shared with the typed client)

## Objectives

- Backend serves OpenAPI v3.1 spec at `/openapi/v1.json` in Development mode
- Export script reliably captures the spec to `client/openapi.json`
- `bun run generate-api` produces `client/src/api/generated/schema.d.ts`
- New typed client at `client/src/api/client.ts` provides `api.GET()`, `api.POST()`, etc. with full type inference
- All REST API call sites migrated from `apiFetch()` to the typed client
- Manual `api-types.ts` deleted (client-only types relocated)
- Agent skill for programmatic API interaction via PowerShell

## Dependencies and Order

**Phase 1 (Tasks 1-7):**
1. Backend OpenAPI setup must come first (Tasks 1-2) since the export script depends on the `/openapi/v1.json` endpoint.
2. The export script (Task 3) must run before client-side generation.
3. Client packages (Task 4) must be installed before schema generation (Task 5).
4. Schema generation (Task 5) must complete before the typed client wrapper (Task 6).
5. Verification (Task 7) confirms the full Phase 1 pipeline end-to-end.

**Phase 2 (Tasks 8-17):**
6. Extract client-only types (Task 8) before any migration batches, so migrated files can import from the new location.
7. Migration batches (Tasks 9-14) can proceed in any order since each is self-contained, but each must leave the codebase compiling.
8. Delete `api-types.ts` (Task 15) only after all migration batches are complete.
9. Clean up `api-client.ts` (Task 16) after all `apiFetch()` imports are removed.
10. Agent skill (Task 17) can be done any time after Phase 1 is verified.

## Tasks

### Phase 1: Wire Up OpenAPI Generation

- [x] 1. Add OpenAPI services to Program.cs
  - **What**: Add `builder.Services.AddOpenApi()` with a document transformer that sets `info.Title = "Weave Fleet API"` and `info.Version = "v1"`. Follow the Foundry pattern at `src/Foundry.Api/Program.cs` lines 27-35. Place the call after `builder.Services.AddProblemDetails()` (line 383).
  - **Files**: `src/WeaveFleet.Api/Program.cs`
  - **Depends on**: None
  - **Acceptance**:
    - `builder.Services.AddOpenApi(...)` is called with title/version transformer
    - No new NuGet packages needed (.NET 10 includes it in the Web SDK)

- [x] 2. Map the OpenAPI endpoint
  - **What**: Add `app.MapOpenApi()` to the middleware pipeline. Place it after `app.UseAuthorization()` and before `app.MapHealthChecks()`. Wrap it in an environment check so it only runs in Development: `if (app.Environment.IsDevelopment()) app.MapOpenApi();`. This keeps the spec endpoint out of production/AOT builds.
  - **Files**: `src/WeaveFleet.Api/Program.cs`
  - **Depends on**: Task 1
  - **Acceptance**:
    - `app.MapOpenApi()` is called conditionally for Development only
    - Running `dotnet run` and hitting `http://localhost:5001/openapi/v1.json` returns a valid OpenAPI 3.1 JSON document
    - All endpoints with `.WithName()` and `.Produces<T>()` appear in the spec

- [x] 3. Create the export PowerShell script
  - **What**: Create `scripts/export-openapi.ps1` modeled on `C:\source\foundry\scripts\export-openapi.ps1`. The script should: (a) start the API via `dotnet run --project src/WeaveFleet.Api --urls http://localhost:5001`, (b) poll `/openapi/v1.json` until ready (max 60 seconds -- Fleet takes longer to start than Foundry due to migrations), (c) download and save to `client/openapi.json`, (d) print path count and metadata, (e) stop the API process in a `finally` block. Use port 5001 and adjust paths for Fleet's layout.
  - **Files**: `scripts/export-openapi.ps1`
  - **Depends on**: Task 2
  - **Acceptance**:
    - Script creates `client/openapi.json` with valid OpenAPI content
    - Script cleans up the dotnet process even on failure
    - Script prints endpoint count on success

- [x] 4. Install client-side packages
  - **What**: Run `bun add openapi-fetch` and `bun add -d openapi-typescript` in the `client/` directory. Add a `"generate-api"` script to `client/package.json`: `"generate-api": "openapi-typescript openapi.json -o src/api/generated/schema.d.ts"`. Match Foundry's versions: `openapi-fetch@^0.13.3` and `openapi-typescript@^7.4.4`.
  - **Files**: `client/package.json`
  - **Depends on**: None (can run in parallel with Tasks 1-3)
  - **Acceptance**:
    - `openapi-fetch` appears in `dependencies`
    - `openapi-typescript` appears in `devDependencies`
    - `bun run generate-api` script is defined in package.json
    - `bun install` succeeds without errors

- [x] 5. Generate the TypeScript schema
  - **What**: Run the full pipeline: `pwsh scripts/export-openapi.ps1` then `cd client && bun run generate-api`. This creates `client/src/api/generated/schema.d.ts`. Add `client/openapi.json` to `.gitignore` (it is a build artifact), but commit `client/src/api/generated/schema.d.ts` (developers need it without running the API).
  - **Files**: `client/src/api/generated/schema.d.ts` (generated), `client/openapi.json` (generated, gitignored), `.gitignore`
  - **Depends on**: Tasks 2, 3, 4
  - **Acceptance**:
    - `client/src/api/generated/schema.d.ts` exists and exports a `paths` type
    - The `paths` type contains entries for known endpoints (e.g. `/api/sessions`, `/api/fleet/summary`)
    - `client/openapi.json` is listed in `.gitignore`
    - `bunx vue-tsc --noEmit` passes in `client/`

- [x] 6. Create the typed API client wrapper
  - **What**: Create `client/src/api/client.ts` modeled on Foundry's `src/Foundry.Web/src/api/client.ts`, but with Fleet-specific middleware for CSRF tokens and credentials. Use `openapi-fetch`'s `createClient<paths>()` with a custom `fetch` implementation that replicates the CSRF and credentials logic from `api-client.ts`. Import `getApiBase` (or inline the same logic) for `baseUrl`. Re-export `paths` for convenience. The client must: (a) read the CSRF cookie and attach `X-CSRF-Token` header on mutating requests, (b) set `credentials: "include"`, (c) use the same base URL resolution as `apiFetch()`.
  - **Files**: `client/src/api/client.ts`
  - **Depends on**: Task 5
  - **Acceptance**:
    - `import { api } from '@/api/client'` works
    - `api.GET('/api/sessions')` type-checks and returns typed `data`/`error`
    - CSRF token is attached to POST/PUT/DELETE requests
    - `credentials: "include"` is set on all requests
    - `bunx vue-tsc --noEmit` passes

- [x] 7. Verify Phase 1 end-to-end
  - **What**: Run the full verification sequence: (a) `dotnet build src/WeaveFleet.Api` succeeds, (b) `cd client && bun run typecheck` passes, (c) `cd client && bun run test` passes, (d) manually start the API and confirm `/openapi/v1.json` returns valid JSON with all expected paths.
  - **Depends on**: Tasks 1-6
  - **Acceptance**:
    - Backend builds without errors
    - Frontend typechecks without errors
    - Frontend tests pass
    - OpenAPI spec contains paths for sessions, projects, fleet summary, credentials, and other endpoint groups

### Phase 2: Migrate Call Sites & Agent Skill

- [x] 8. Extract client-only types from api-types.ts
  - **What**: Create `client/src/lib/client-types.ts` and move all types that are constructed client-side (not REST API response shapes) out of `api-types.ts`. These types cannot come from OpenAPI generation. Move the following types and their re-exports:
    - `AccumulatedTextPart`, `AccumulatedReasoningPart`, `AccumulatedToolPart`, `AccumulatedFilePart`, `AccumulatedPart`, `AccumulatedMessage`
    - `WebSocketEvent`, `CommittedSessionEvent`
    - `DelegationDto`
    - The re-exports of `SessionActivityStatus`, `SessionActionCapabilities`, `SessionLifecycleStatus`, `SessionRetentionStatus`, `InstanceStatus`
    
    Then update all ~30 files that import these types to use `@/lib/client-types` instead of `@/lib/api-types`. Leave the REST API types in `api-types.ts` for now -- they get deleted later.
  - **Files**:
    - `client/src/lib/client-types.ts` (new)
    - `client/src/lib/api-types.ts` (remove moved types)
    - All files importing the moved types (see list below)
  - **Depends on**: Task 7
  - **Acceptance**:
    - `client/src/lib/client-types.ts` exists with all client-only types
    - No file imports client-only types from `@/lib/api-types`
    - `bun run typecheck` passes
    - `bun run test` passes

  **Files importing client-only types from `@/lib/api-types` (update to `@/lib/client-types`):**
  - `client/src/lib/domain-event-reducer.ts` -- `AccumulatedMessage`, `DelegationDto`
  - `client/src/lib/event-state.ts` -- `AccumulatedMessage`, `AccumulatedReasoningPart`, `AccumulatedTextPart`, `AccumulatedToolPart`, `AccumulatedFilePart`
  - `client/src/lib/delegation-state.ts` -- `DelegationDto`
  - `client/src/lib/session-snapshot.ts` -- `DelegationDto`
  - `client/src/lib/session-cache.ts` -- `AccumulatedMessage`, `DelegationDto`
  - `client/src/lib/pagination-utils.ts` -- `AccumulatedMessage`, `AccumulatedPart`
  - `client/src/lib/pr-utils.ts` -- `AccumulatedMessage`
  - `client/src/lib/todo-utils.ts` -- `AccumulatedMessage`
  - `client/src/lib/question-types.ts` -- `AccumulatedToolPart`
  - `client/src/lib/__tests__/domain-event-reducer.test.ts` -- `DelegationDto`
  - `client/src/composables/use-activity-filter.ts` -- `AccumulatedMessage`, `AccumulatedPart`
  - `client/src/composables/use-active-questions.ts` -- `AccumulatedMessage`, `AccumulatedToolPart`
  - `client/src/composables/use-session-events.ts` -- `CommittedSessionEvent`, `DelegationDto`, `WebSocketEvent`
  - `client/src/composables/use-session-events-switch.ts` -- `AccumulatedMessage`, `DelegationDto`
  - `client/src/composables/use-session-stream.ts` -- `AccumulatedMessage`, `DelegationDto`
  - `client/src/composables/use-message-pagination.ts` -- `AccumulatedMessage`
  - `client/src/composables/use-send-prompt.ts` -- `AccumulatedMessage`, `ImageAttachment`
  - `client/src/composables/use-draft-attachments.ts` -- `ImageAttachment`
  - `client/src/plugins/builtin/smart-links/composables/use-smart-links.ts` -- `AccumulatedMessage`
  - `client/src/components/session/ActivityStream.vue` -- `AccumulatedMessage`, `AccumulatedPart`, `AccumulatedToolPart`, `AccumulatedFilePart`
  - `client/src/components/session/activity-stream-tool-card.ts` -- `AccumulatedToolPart`
  - `client/src/components/session/MessageBubble.vue` -- `AccumulatedToolPart`
  - `client/src/components/session/QuestionCard.vue` -- `AccumulatedToolPart`
  - `client/src/components/session/Composer.vue` -- `ImageAttachment`

- [x] 9. Migrate batch: Sessions & Session Actions
  - **What**: Replace `apiFetch()` calls with typed `api.GET()` / `api.POST()` / `api.PUT()` / `api.DELETE()` calls. Remove manual type annotations that are now inferred from the schema. Update imports from `@/lib/api-types` to `@/api/client` for any API response types that now come from OpenAPI.

    Pattern for each file: (a) replace `import { apiFetch } from "@/lib/api-client"` with `import { api } from "@/api/client"`, (b) replace `const response = await apiFetch("/api/sessions", { method: "GET" }); const data = await response.json() as SessionListItem[]` with `const { data, error } = await api.GET("/api/sessions")`, (c) add error handling for `error` if not already present.
  - **Files**:
    - `client/src/composables/use-sessions.ts` -- list/get sessions
    - `client/src/composables/use-session-actions.ts` -- create, fork, resume, archive, delete, rename, move sessions
    - `client/src/composables/use-session-events.ts` -- fetch committed events and delegations (the REST calls, not WS)
    - `client/src/composables/use-session-detail-context.ts` -- type-only import of `SessionListItem`, `ResumeSessionResponse`
    - `client/src/composables/use-message-pagination.ts` -- fetch older messages
    - `client/src/composables/use-send-prompt.ts` -- POST prompt
    - `client/src/composables/use-send-command.ts` -- POST command
    - `client/src/composables/use-question-answer.ts` -- POST answer
    - `client/src/composables/use-diffs.ts` -- GET session diffs
    - `client/src/composables/use-rename-workspace.ts` -- PUT workspace name
    - `client/src/stores/sessions.ts` -- type-only import of `SessionListItem`
  - **Depends on**: Task 8
  - **Acceptance**:
    - No `apiFetch` imports remain in the listed files
    - All API calls use `api.GET()` / `api.POST()` etc.
    - `bun run typecheck` passes
    - `bun run test` passes

- [x] 10. Migrate batch: Projects & Fleet Summary
  - **What**: Same migration pattern as Task 9.
  - **Files**:
    - `client/src/composables/use-projects.ts` -- CRUD projects, reorder
    - `client/src/composables/use-fleet-summary.ts` -- GET fleet summary
    - `client/src/composables/use-config.ts` -- GET client config
    - `client/src/stores/app-shell.ts` -- type-only import of `ClientConfigResponse`, `UserMeResponse`
  - **Depends on**: Task 8
  - **Acceptance**:
    - No `apiFetch` imports remain in the listed files
    - `bun run typecheck` passes
    - `bun run test` passes

- [x] 11. Migrate batch: Instance, Harness & Autocomplete
  - **What**: Same migration pattern. These composables talk to instance-scoped endpoints (`/api/instances/{id}/...`).
  - **Files**:
    - `client/src/composables/use-agents.ts` -- GET agents
    - `client/src/composables/use-autocomplete.ts` -- GET commands, agents
    - `client/src/composables/use-commands.ts` -- list commands
    - `client/src/composables/use-models.ts` -- GET models/providers
    - `client/src/composables/use-harnesses.ts` -- GET harness info
    - `client/src/composables/use-find-files.ts` -- GET file search
    - `client/src/composables/use-enabled-harnesses.ts` -- GET enabled harnesses
  - **Depends on**: Task 8
  - **Acceptance**:
    - No `apiFetch` imports remain in the listed files
    - `bun run typecheck` passes
    - `bun run test` passes

- [x] 12. Migrate batch: Settings, Credentials, Preferences & NuCode
  - **What**: Same migration pattern. Covers settings-related API calls.
  - **Files**:
    - `client/src/composables/use-credentials.ts` -- CRUD credentials
    - `client/src/composables/use-nucode-providers.ts` -- GET/PUT/POST nucode providers
    - `client/src/stores/preferences.ts` -- GET/PUT preferences
    - `client/src/composables/use-integrations.ts` -- GET integrations, plugin catalog
    - `client/src/composables/use-update-status.ts` -- GET update status
    - `client/src/composables/use-skills.ts` -- GET skills
    - `client/src/composables/use-key-files.ts` -- GET key files
    - `client/src/composables/use-available-tools.ts` -- GET available tools
    - `client/src/components/settings/GeneralSection.vue` -- workspace roots
    - `client/src/components/settings/WorkspaceSection.vue` -- workspace roots CRUD
    - `client/src/components/settings/CredentialsSection.vue` -- credentials UI
    - `client/src/components/onboarding/OnboardingWizard.vue` -- store credentials during onboarding
  - **Depends on**: Task 8
  - **Acceptance**:
    - No `apiFetch` imports remain in the listed files
    - `bun run typecheck` passes
    - `bun run test` passes

- [x] 13. Migrate batch: Repositories, Directories & Workspaces
  - **What**: Same migration pattern.
  - **Files**:
    - `client/src/composables/use-repositories.ts` -- GET/POST repositories
    - `client/src/composables/use-repository-info.ts` -- GET repo info
    - `client/src/composables/use-repository-detail.ts` -- GET repo detail
    - `client/src/composables/use-worktrees.ts` -- GET worktrees
    - `client/src/composables/use-directory-browser.ts` -- GET directories
    - `client/src/composables/use-open-directory.ts` -- POST open directory
    - `client/src/composables/use-open-file.ts` -- POST open file
    - `client/src/composables/use-pr-status.ts` -- GET PR status
    - `client/src/components/pages/RepositoriesPage.vue` -- repo list + refresh
  - **Depends on**: Task 8
  - **Acceptance**:
    - No `apiFetch` imports remain in the listed files
    - `bun run typecheck` passes
    - `bun run test` passes

- [x] 14. Migrate batch: Analytics, Plugins, Auth, GitHub & Remaining
  - **What**: Same migration pattern. This is the catch-all for remaining files.
  - **Files**:
    - `client/src/composables/use-analytics-summary.ts` -- GET analytics summary
    - `client/src/composables/use-analytics-daily.ts` -- GET daily analytics
    - `client/src/composables/use-analytics-sessions.ts` -- GET session analytics
    - `client/src/composables/use-analytics-models.ts` -- GET model analytics
    - `client/src/plugins/builtin/github/composables/use-github-auth.ts` -- device code flow
    - `client/src/plugins/builtin/github/composables/use-github-repos.ts` -- GET repos
    - `client/src/plugins/builtin/github/composables/use-github-issues.ts` -- GET issues
    - `client/src/plugins/builtin/github/composables/use-github-pulls.ts` -- GET pulls
    - `client/src/plugins/builtin/github/composables/use-github-metadata.ts` -- GET metadata
    - `client/src/plugins/builtin/github/composables/use-github-bookmarks.ts` -- GET bookmarks
    - `client/src/plugins/builtin/smart-links/composables/use-smart-links.ts` -- GET/POST smart links (the `apiFetch` part)
    - `client/src/plugins/builtin/smart-links/SmartLinksPanel.vue` -- smart links UI
    - `client/src/plugins/builtin/smart-links/SmartLinkItem.vue` -- individual smart link
    - `client/src/plugins/builtin/smart-links/providers/github-smart-link-provider.ts` -- GitHub link resolution
    - `client/src/lib/board-api.ts` -- board operations
    - `client/src/lib/track-action.ts` -- analytics/telemetry tracking
    - `client/src/components/auth/AuthGate.vue` -- GET /api/auth/me
    - `client/src/components/layout/IconRail.vue` -- GET plugin catalog
    - `client/src/components/pages/GitHubIssuePage.vue` -- GET issue detail
    - `client/src/components/pages/GitHubPullRequestPage.vue` -- GET PR detail
    - `client/src/components/pages/GitHubWorkItemDetailPage.vue` -- GET work item detail
    - `client/src/components/session/SessionDetailPanel.vue` -- session detail actions
    - `client/src/components/sessions/SessionsPanel.vue` -- type-only import
  - **Depends on**: Task 8
  - **Acceptance**:
    - No `apiFetch` imports remain in any file except `api-client.ts` itself
    - `bun run typecheck` passes
    - `bun run test` passes

- [x] 15. Migrate tests that import apiFetch or api-types
  - **What**: Update test files that mock `apiFetch` or import from `api-types`. Tests that mock `apiFetch` need to mock the `api` client instead. Tests that only import types need their import paths updated.
  - **Files**:
    - `client/src/lib/__tests__/domain-event-reducer.test.ts` -- mocks `apiFetch`, imports `DelegationDto`
    - `client/src/plugins/builtin/smart-links/__tests__/SmartLinksPanel.test.ts` -- mocks `apiFetch`
    - `client/src/plugins/builtin/smart-links/__tests__/github-smart-link-provider.test.ts` -- mocks `apiFetch`
    - `client/src/components/__tests__/SessionOriginBadge.test.ts` -- type-only import
    - `client/src/components/__tests__/SessionItem.test.ts` -- type-only import
    - `client/src/components/__tests__/Composer.test.ts` -- type-only import
    - `client/src/stores/__tests__/sessions.test.ts` -- type-only import
    - `client/src/composables/__tests__/use-enabled-harnesses.test.ts` -- type-only import
    - `client/src/composables/__tests__/use-projects.test.ts` -- type-only import
    - `client/src/composables/__tests__/use-session-actions.test.ts` -- type-only import + mocks `apiFetch`
    - `client/src/composables/__tests__/use-sessions.test.ts` -- type-only import
    - `client/src/components/settings/__tests__/HarnessesSection.test.ts` -- type-only import
    - `client/src/components/session/__tests__/activity-stream-utils.test.ts` -- type-only import
    - `client/src/components/session/__tests__/SessionDetailPanel.files-changed.test.ts` -- type-only import
  - **Depends on**: Tasks 9-14
  - **Acceptance**:
    - No test imports from `@/lib/api-types`
    - No test mocks `apiFetch` (they mock `api` or `@/api/client` instead)
    - `bun run test` passes

- [x] 16. Delete api-types.ts and clean up api-client.ts
  - **What**: (a) Delete `client/src/lib/api-types.ts` entirely. At this point, all REST API types come from `@/api/client` (OpenAPI-generated) and all client-only types live in `@/lib/client-types.ts`. (b) Remove the `apiFetch()` function from `client/src/lib/api-client.ts`. Keep `apiUrl()`, `wsUrl()`, `setApiBase()`, and the CSRF cookie helper (which is used by the typed client too). (c) Grep the entire `client/src/` tree for any remaining references to `api-types` or `apiFetch` and fix them.
  - **Files**:
    - `client/src/lib/api-types.ts` (delete)
    - `client/src/lib/api-client.ts` (remove `apiFetch`, keep utilities)
  - **Depends on**: Task 15
  - **Acceptance**:
    - `client/src/lib/api-types.ts` does not exist
    - No file imports from `@/lib/api-types`
    - `apiFetch` is not exported from `api-client.ts`
    - `apiUrl()`, `wsUrl()`, `setApiBase()` still work
    - `bun run typecheck` passes
    - `bun run test` passes

- [x] 17. Create weave-fleet-api agent skill
  - **What**: Create a new skill at `C:\Users\piete\.config\opencode\skills\weave-fleet-api\` modeled on the Foundry API skill at `C:\Users\piete\.config\opencode\skills\foundry-api\SKILL.md`. The skill should include:
    - `SKILL.md` with skill metadata, context, available actions, and usage examples
    - `scripts/weave-fleet-api.ps1` PowerShell helper script for programmatic API interaction
    
    The SKILL.md should reference:
    - Repo root: `C:\source\weave-fleet`
    - OpenAPI export: `client/openapi.json`
    - Runtime OpenAPI URL: `http://localhost:5001/openapi/v1.json`
    - Default API base URL: `http://localhost:5001`
    
    The PowerShell script should support actions matching the main endpoint groups: sessions (list, get, create, resume, fork, archive, delete), projects (list, create, update, delete, reorder), fleet summary, config, repositories, credentials, harnesses, analytics, health, and openapi schema retrieval.
    
    Register the skill in `C:\Users\piete\.config\opencode\agents.json` (or equivalent config) if needed.
  - **Files**:
    - `C:\Users\piete\.config\opencode\skills\weave-fleet-api\SKILL.md` (new)
    - `C:\Users\piete\.config\opencode\skills\weave-fleet-api\scripts\weave-fleet-api.ps1` (new)
  - **Depends on**: Task 7
  - **Acceptance**:
    - `SKILL.md` follows the Foundry skill structure (metadata, context, available actions, usage examples, important notes)
    - PowerShell script validates GUID parameters, outputs JSON, handles errors
    - Script supports at least: `health`, `openapi`, `list-sessions`, `get-session`, `create-session`, `list-projects`, `get-fleet-summary`
    - Running `weave-fleet-api.ps1 -Action health` against a running API returns success

- [x] 18. Final verification
  - **What**: Run the complete verification suite for both phases.
  - **Depends on**: Tasks 16, 17
  - **Acceptance**:
    - `dotnet build src/WeaveFleet.Api` succeeds
    - `cd client && bun run typecheck` passes
    - `cd client && bun run test` passes
    - `cd client && bun run lint` passes
    - No file in `client/src/` imports from `@/lib/api-types`
    - No file in `client/src/` imports `apiFetch` from `@/lib/api-client`
    - `grep -r "api-types" client/src/` returns zero results (excluding node_modules)
    - Agent skill script returns valid JSON for `health` and `openapi` actions

## Verification

```powershell
# Phase 1
dotnet build src/WeaveFleet.Api
pwsh scripts/export-openapi.ps1
cd client
bun run generate-api
bun run typecheck
bun run test

# Phase 2 (after all migrations)
bun run typecheck
bun run test
bun run lint

# Confirm no stale imports remain
# (PowerShell)
$staleApiTypes = Select-String -Path "client/src/**/*.ts","client/src/**/*.vue" -Pattern "from ['\`"]@/lib/api-types" -Recurse
$staleApiFetch = Select-String -Path "client/src/**/*.ts","client/src/**/*.vue" -Pattern "import.*apiFetch.*from" -Recurse
if ($staleApiTypes) { Write-Error "Stale api-types imports found: $($staleApiTypes.Count)" }
if ($staleApiFetch) { Write-Error "Stale apiFetch imports found: $($staleApiFetch.Count)" }

# Agent skill smoke test
& "C:\Users\piete\.config\opencode\skills\weave-fleet-api\scripts\weave-fleet-api.ps1" -Action health
& "C:\Users\piete\.config\opencode\skills\weave-fleet-api\scripts\weave-fleet-api.ps1" -Action openapi
```

All commands exit 0. No stale imports of `api-types` or `apiFetch` remain. The generated `schema.d.ts` contains a `paths` interface with entries matching the ~25+ endpoint groups registered in `EndpointExtensions.cs`.
