# Session Source Architecture for Weave Fleet

## TL;DR
> **Summary**: Introduce a first-class Session Source model that separates source selection from hardcoded repository/directory UI, turns repository into a specialized built-in source, and creates backend/frontend contracts that can later support Slack, Discord, Google Docs, Google Chat, Notion, and GitHub-style context actions.
> **Estimated Effort**: Large

## Context
### Original Request
Create an implementation plan for introducing a Session Source architecture into Weave Fleet. The current New Session dialog hardcodes repository vs directory, but repository is too Git-specific for Fleet. Future plugins may provide Slack/Discord/Google Docs/Google Chat/Notion sources, each supporting actions like “Start session from” and “Add to current session”. Repository should become one specialized source type with custom UI (repo picker, isolation strategy like existing/worktree/clone, branch, etc.). The plan should cover domain concepts, frontend extension model, backend contracts, migration/refactor strategy from current repository scanning and New Session dialog, compatibility with existing workspace/session concepts, and phased implementation slices.

### Key Findings
- `client/src/components/session/new-session-dialog.tsx` hardcodes `SourceMode = "repository" | "directory"`, owns repository-specific picker/state, and directly maps source mode to `directory + isolationStrategy + branch`.
- `client/src/integrations/github/components/create-session-button.tsx` duplicates a second source-selection dialog with its own repository/directory logic, which will get worse as more sources are added.
- `client/src/hooks/use-create-session.ts`, `client/src/lib/api-types.ts`, `src/WeaveFleet.Api/Endpoints/SessionEndpoints.cs`, and `src/WeaveFleet.Application/Services/SessionOrchestrator.cs` only understand session creation in terms of a working directory plus optional isolation/branch/prompt.
- Repository discovery is currently a Fleet-level special case: `src/WeaveFleet.Application/Services/RepositoryService.cs` scans `WorkspaceRootService` roots, while `/api/repositories*` lives in `src/WeaveFleet.Api/Endpoints/FleetEndpoints.cs`.
- Existing plugin seams are already present on the frontend (`client/src/plugins/types.ts`, `client/src/plugins/slots.ts`, `client/src/plugins/context.tsx`) but there is no contribution type for session sources or source-specific launch UI.
- Workspaces are still the execution primitive: `src/WeaveFleet.Domain/Entities/Workspace.cs` and `src/WeaveFleet.Application/Services/WorkspaceService.cs` model `Directory`, `SourceDirectory`, `IsolationStrategy`, and `Branch`; session sidebar grouping in `client/src/lib/workspace-utils.ts` depends on `sourceDirectory ?? workspaceDirectory`.
- Settings language is still Git-biased: `client/src/components/settings/repositories-tab.tsx` manages workspace roots “to scan for git repositories”, even though roots are really local discovery inputs for future source providers.
- E2E and application tests encode the current shape: `tests/WeaveFleet.E2E/Pages/NewSessionDialog.cs` only knows directory mode, and `tests/WeaveFleet.Application.Tests/Services/SessionOrchestratorTests.cs` only exercises legacy create-session contracts.

### Risks
- The biggest modeling trap is conflating **workspace sources** (directory/repository) with **context sources** (Slack thread, Notion page, GitHub issue). The contract must allow both without requiring every source to produce a filesystem directory.
- A big-bang rewrite would likely break existing session creation, sidebar grouping, and workspace rename flows; the rollout needs compatibility shims for the current `directory`-based API and existing workspace rows.
- The repository DTOs already have frontend/backend drift risk; widening the model without a single source catalog contract will create more mismatch.
- If source UI is not host-owned, each integration will keep shipping its own dialog, validation, and action semantics; that duplicates bugs and makes accessibility harder to maintain.
- “Add to current session” is not just “create session with context”; it needs an explicit action path and provenance model so future sources do not tunnel everything through `initialPrompt` ad hoc.
- Frontend plugins and browser code are not a trusted authority for provider identity, capabilities, resolved context, or workspace inputs; if the trust boundary is vague, providers will be able to spoof actions, overpost hidden data, or drift from backend-enforced behavior.
- Non-filesystem and filesystem-producing sources have different security needs; if workspace-producing sources are allowed to influence raw paths, clone destinations, or worktree targets without host canonicalization, the model invites traversal and allowed-root escapes.
- External source content is untrusted input; if `Add to current session` lacks preview, truncation, redaction, and persistence rules, prompt injection and accidental secret/PII retention become part of the core launch model.

## Objectives
### Core Objective
Create a source-driven session-launch architecture that preserves today’s workspace/session model, makes repository a specialized built-in source instead of a hardcoded global mode, and opens a clean extension seam for future built-in/plugin-provided sources and actions.

### Deliverables
- [ ] A canonical Session Source domain model covering source kind, supported actions, selection payloads, and resolution results
- [ ] A frontend source extension model that lets built-in/plugins contribute source descriptors and custom source-specific forms to the New Session flow
- [ ] Backend catalog and resolution contracts that support both `Start session from` and `Add to current session` without breaking the existing create-session API immediately
- [ ] A repository source provider that reuses current scanning/detail logic behind the new contracts and keeps custom repo UI for picker/isolation/branch behavior
- [ ] A phased migration plan that removes hardcoded repository/directory branching from core UI while preserving current workspace/session behavior

### Definition of Done
- [ ] `dotnet test`
- [ ] `npm test`
- [ ] Manual smoke test covers: create session from directory, create session from repository/worktree, launch from GitHub context through shared source flow, and add source context to an existing session
- [ ] Existing workspace grouping/renaming still works for legacy and new sessions (`sourceDirectory ?? workspaceDirectory` behavior remains intact or is intentionally replaced with equivalent coverage)

### Guardrails (Must NOT)
- [ ] Do not break the current directory-based launch path while the new source model is being introduced
- [ ] Do not make repository fields part of the shared source contract; repo-only fields must stay source-specific
- [ ] Do not force non-filesystem sources to pretend they are repositories or local directories
- [ ] Do not rewrite workspace/session persistence in a way that invalidates existing rows or sidebar grouping without a migration/backfill path
- [ ] Do not couple the design to GitHub-specific concepts when describing future source/provider support
- [ ] Do not treat any frontend/plugin-supplied source payload as authoritative; the backend source catalog and provider resolution layer must be the sole authority for source id, supported actions, resolved content, provenance, workspace inputs, and capability checks
- [ ] Do not let frontend plugins resolve final session context; frontend can collect user input and render forms, but backend providers must fetch, normalize, redact, and label external content before it reaches session APIs
- [ ] Do not allow free-form provider payloads to reach session creation or add-to-session endpoints; requests must be schema-validated and reject unknown/extra fields
- [ ] Do not allow any source provider to choose arbitrary local filesystem paths; all paths must be host-selected or host-validated after canonicalization and symlink resolution and must remain under allowed workspace roots
- [ ] Do not persist raw source bodies, comments, diffs, tokens, cookies, auth headers, or provider secrets in workspace/session/source-usage records; persist minimal provenance and redacted summaries only when needed
- [ ] Do not inject source content into an existing session without explicit preview, origin labeling, and deterministic size limits
- [ ] Do not merge frontend-declared capabilities over backend capabilities; frontend contributions may augment presentation only for backend-registered source ids
- [ ] Do not render source metadata or source body as trusted HTML

## TODOs

- [x] 1. Phase 1 — Define the domain model and compatibility boundary
  **What**: Introduce explicit source concepts before touching UI: `SessionSourceDescriptor`, `SessionSourceKind` (`workspace`, `context`, `hybrid`), `SessionSourceAction` (`start-session`, `add-to-session`), `SessionSourceSelection`, and a backend-only `ResolvedSessionSource`/`ResolvedSessionInput` result split into `WorkspaceIntent?`, `ContextEnvelope?`, and `ProvenanceRecord`. `SessionSourceSelection` is client intent only; resolution is server-computed and authoritative. Make repository one provider implementation, not a global enum branch. Preserve a translation path from the current `directory + isolationStrategy + branch` request into the new model so old callers keep working during rollout. Make stable source identity mandatory in contracts (`providerId + sourceType + actionId` or equivalent versioned key).
  **Files**: `src/WeaveFleet.Application/SessionSources/ISessionSourceProvider.cs`, `src/WeaveFleet.Application/SessionSources/SessionSourceContracts.cs`, `src/WeaveFleet.Application/Services/SessionSourceResolutionService.cs`, `src/WeaveFleet.Application/Services/SessionOrchestrator.cs`, `src/WeaveFleet.Api/Endpoints/SessionEndpoints.cs`
  **Acceptance**: The shared contracts can express directory, repository, and future Slack/Notion-style sources without embedding repo-only fields; the legacy POST `/api/sessions` shape still has a documented translation path; backend rejects unknown fields, forged provider ids, unsupported actions, and any client-supplied resolved content.

- [x] 2. Phase 1 — Persist source provenance without breaking workspace/session compatibility
  **What**: Add source provenance fields in a backwards-compatible way so Fleet can remember where a workspace/session came from without changing how existing rows behave. Store workspace-origin metadata for workspace-producing sources and add a lightweight source-usage record for session actions so `Add to current session` has somewhere to land. Keep existing `Workspace.Directory`, `SourceDirectory`, `IsolationStrategy`, and `Branch` semantics intact so sidebar grouping and workspace rename flows continue to work. Persist only minimal provenance by default (provider id, canonical resource id/URL, title, timestamps, redacted summary/checksum when needed), never raw fetched bodies/comments/diffs/secrets.
  **Files**: `src/WeaveFleet.Infrastructure/Migrations/009_add_session_source_metadata.sql`, `src/WeaveFleet.Domain/Entities/Workspace.cs`, `src/WeaveFleet.Domain/Entities/SessionSourceUsage.cs`, `src/WeaveFleet.Domain/Repositories/IWorkspaceRepository.cs`, `src/WeaveFleet.Domain/Repositories/ISessionSourceUsageRepository.cs`, `src/WeaveFleet.Infrastructure/Data/Repositories/DapperWorkspaceRepository.cs`, `src/WeaveFleet.Infrastructure/Data/Repositories/DapperSessionSourceUsageRepository.cs`
  **Acceptance**: Existing workspaces/sessions still load with null source metadata, new launches record origin/action details, workspace grouping behavior remains unchanged for old and new rows, and no secrets/PII-heavy raw source payloads are persisted unless an explicit later design adds redaction and approval semantics.

- [x] 3. Phase 2 — Extract repository behavior into a repository source provider
  **What**: Move the current repository scanning/detail behavior behind a repository-specific source provider. The provider should advertise source-specific fields and supported isolation options (`existing`, `worktree`, later `clone`) while continuing to use workspace roots for local discovery. Keep repository-specific logic in provider code and custom UI, not in the shared source contract. All workspace paths, worktree destinations, and clone destinations must be allocated or validated by the host after canonicalization and symlink-aware allowed-root checks; provider inputs can request intent, not choose final paths.
  **Files**: `src/WeaveFleet.Application/Services/RepositoryService.cs`, `src/WeaveFleet.Infrastructure/SessionSources/RepositorySessionSourceProvider.cs`, `src/WeaveFleet.Application/Services/WorkspaceRootService.cs`, `src/WeaveFleet.Api/Endpoints/FleetEndpoints.cs`, `src/WeaveFleet.Api/Endpoints/SessionSourceEndpoints.cs`
  **Acceptance**: Repository catalog data and detail lookups come from the repository source provider, repository-specific options are discoverable through the new source catalog without hardcoding them in the core dialog, and traversal/root-escape/symlink-escape attempts are rejected.

- [x] 4. Phase 2 — Add backend source catalog and action contracts
  **What**: Introduce a dedicated source catalog endpoint so the client can discover available sources, capabilities, and action support from the backend. Evolve session APIs to accept a source-based payload for `start-session`, and add a dedicated secured action path for `add-to-session` that resolves source payload into a previewable `ContextEnvelope` for an existing session. Keep temporary adapters so current `/api/repositories*` and legacy create-session callers still function during migration. Backend catalog data is authoritative; frontend contributions may only attach presentation metadata to backend-registered source ids.
  **Files**: `src/WeaveFleet.Api/Endpoints/SessionSourceEndpoints.cs`, `src/WeaveFleet.Api/Endpoints/SessionEndpoints.cs`, `src/WeaveFleet.Application/Services/SessionSourceResolutionService.cs`, `src/WeaveFleet.Application/Services/SessionOrchestrator.cs`, `client/src/lib/api-types.ts`, `client/src/hooks/use-create-session.ts`
  **Acceptance**: The API can represent both `start-session` and `add-to-session`; old create-session requests still work; non-workspace sources can resolve into prompt/context without pretending to be a repository; add-to-session uses backend resolution, explicit confirmation, origin labeling, and size-limited/truncated context envelopes.

- [x] 5. Phase 3 — Add a frontend session-source extension seam
  **What**: Extend the existing built-in plugin model with a `sessionSources` contribution type and add a host-owned source registry/hook layer. Source descriptors should include label, kind, supported actions, required stable backend catalog key, and a custom form renderer contract. This keeps the New Session host generic while still allowing repository to render a specialized picker and future sources to contribute their own UI. Frontend plugins are presentation helpers only: they can render forms and collect user intent, but cannot declare authoritative capabilities, resolved context, or workspace destinations.
  **Files**: `client/src/plugins/types.ts`, `client/src/plugins/slots.ts`, `client/src/session-sources/types.ts`, `client/src/session-sources/registry.ts`, `client/src/session-sources/use-session-sources.ts`, `client/src/plugins/context.tsx`
  **Acceptance**: Built-in/plugins can register source contributions without editing `new-session-dialog.tsx`, the registry can merge frontend-contributed UI with backend-discovered capabilities/catalog data, and frontend-declared capabilities cannot elevate or override backend enforcement.

- [x] 6. Phase 3 — Refactor the New Session dialog into a source host
  **What**: Replace the hardcoded repository/directory mode branches with a source picker that renders the selected source’s custom form and action set. Extract current directory and repository UI into dedicated source form components, preserve default-directory behavior used by Fleet/sidebar/command-palette callers, and keep repository custom UI for repo picker, isolation strategy, branch naming, and future clone support.
  **Files**: `client/src/components/session/new-session-dialog.tsx`, `client/src/components/session/sources/source-picker.tsx`, `client/src/components/session/sources/directory-source-form.tsx`, `client/src/components/session/sources/repository-source-form.tsx`, `client/src/components/layout/fleet-panel.tsx`, `client/src/components/layout/sidebar-workspace-item.tsx`, `client/src/components/commands/session-commands.tsx`
  **Acceptance**: The core dialog no longer contains source-specific repository business logic; directory and repository launches still work; adding a new source only requires registering a new source contribution; any add-to-session flow previews untrusted content before submission and never renders provider content as trusted HTML.

- [x] 7. Phase 4 — Migrate GitHub/context flows onto the shared source-action model
  **What**: First remove the current context-flow drift by mapping GitHub’s existing context launch onto the new backend-resolved source path rather than browser-authored final context. Then remove the GitHub-specific duplicate launch dialog and route GitHub through the shared source/action host. Reframe the current `ContextSource` integration contract as one source input among many, then support both `Start session from` and `Add to current session` through common actions. This phase should leave GitHub-specific content resolution on the backend/provider side while eliminating duplicated source-selection UI.
  **Files**: `client/src/integrations/github/components/create-session-button.tsx`, `client/src/integrations/types.ts`, `client/src/plugins/types.ts`, `client/src/hooks/use-create-session.ts`, `client/src/hooks/use-add-source-to-session.ts`, `src/WeaveFleet.Api/Endpoints/SessionEndpoints.cs`, `src/WeaveFleet.Application/Services/SessionSourceResolutionService.cs`
  **Acceptance**: GitHub no longer owns its own repo/directory dialog, shared source actions drive both launch and augmentation flows, the same contracts can be reused by future Slack/Notion/Docs providers, and add-to-session persists provenance, shows a visible session artifact/message, and defines retry/failure behavior.

- [x] 8. Phase 4 — Rename and reshape source-related settings/admin UX
  **What**: Separate “workspace roots” from “session sources” in the settings vocabulary. Keep local filesystem discovery settings focused on allowed roots and repository scanning, while plugin/integration settings continue to live in the plugin-backed integrations area. Update copy so Fleet no longer presents repositories as the universal creation model.
  **Files**: `client/src/components/settings/repositories-tab.tsx`, `client/src/app/settings/page.tsx`, `src/WeaveFleet.Application/Services/WorkspaceRootService.cs`, `src/WeaveFleet.Api/Endpoints/WorkspaceRootEndpoints.cs`
  **Acceptance**: Settings copy reflects roots/local discovery vs sources/plugins clearly, and users can still manage local roots without the product implying that every source is a repository.

- [x] 9. Phase 5 — Add regression coverage and rollout gates
  **What**: Add tests around source resolution, legacy request compatibility, repo-source behavior, and source-host rendering. Update E2E page objects for the new source picker, and add a phased rollout strategy that ships directory + repository first, then migrates GitHub/shared context, then enables additional source providers. Include explicit risk checks for workspace grouping, rename flows, add-to-session semantics, forged provider/action payloads, root-escape attempts, oversized source content, and plugin trust-boundary regressions. Ship each phase behind feature flags with a documented migration window and rollback path.
  **Files**: `tests/WeaveFleet.Application.Tests/Services/SessionOrchestratorTests.cs`, `tests/WeaveFleet.Application.Tests/Services/SessionSourceResolutionServiceTests.cs`, `tests/WeaveFleet.E2E/Pages/NewSessionDialog.cs`, `client/src/components/session/__tests__/new-session-dialog.test.tsx`, `client/src/session-sources/__tests__/use-session-sources.test.ts`, `.weave/plans/session-source-architecture.md`
  **Acceptance**: Tests cover legacy and new payloads, repository and directory sources, GitHub handoff, and add-to-session behavior; forged provider/action payloads are rejected; `../` and symlink root escapes are rejected; oversized/HTML/prompt-injection source payloads are truncated and labeled; rollout slices are documented, feature-flagged, and can be enabled without a big-bang cutover.

## Rollout slices
- **Slice 1 — Default on:** ship directory + repository session sources plus legacy create-session translation together; keep legacy `directory/isolationStrategy/branch` clients supported for at least one migration window.
- **Slice 2 — Default on after smoke test:** migrate GitHub launch/add flows to backend-resolved source actions and verify preview/confirmation behavior on a live session before broad release.
- **Slice 3 — Future providers:** enable additional context-only and hybrid providers only after they implement backend authority, preview/truncation, provenance persistence, and allowed-root enforcement where applicable.

## Rollback path
- Revert frontend callers to the legacy create-session payload while keeping the backend translation path intact.
- Disable GitHub shared-action entry points before removing backend source providers; legacy directory/repository launches remain available.
- Preserve workspace/source provenance columns and source-usage rows as backward-compatible metadata during rollback.

## Verification
- [x] All tests pass
- [x] No regressions
- [x] `dotnet test tests/WeaveFleet.Application.Tests`
- [x] `npm test`
- [ ] Manual smoke test verifies: New Session shows source-driven UI, repository source still supports repo picker/worktree/branch, directory source still works from Fleet/sidebar/command palette, and GitHub source can both start a new session and add context to the current session
- [ ] Legacy clients posting `directory`, `isolationStrategy`, and `branch` still create sessions successfully during the migration window
