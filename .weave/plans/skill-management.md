# Skill Management — Catalog + Manifest + Sync Engine

## TL;DR
Replace the current flat-directory skill storage with a manifest-driven system that tracks skill sources (bundled/github/local), syncs skills to harness discovery paths, supports a browsable catalog, and provides update detection — all orchestrated through new backend services and a redesigned Settings > Skills UI.

## Context
- Current skills API: `src/WeaveFleet.Api/Endpoints/SkillEndpoints.cs` — CRUD against `~/.weave/skills/`
- Frontend: `client/src/components/settings/SkillsSection.vue` + `client/src/composables/use-skills.ts`
- Harness registry: `src/WeaveFleet.Application/Harnesses/IHarnessRegistry.cs` — discovers harnesses by type
- OpenCode pool restart: `src/WeaveFleet.Infrastructure/Harnesses/OpenCode/Pooling/PooledOpenCodeInstanceRegistry.cs`
- Harness discovery paths: OpenCode=`~/.config/opencode/skills/<name>/`, Claude Code=`~/.claude/skills/<name>/`
- Skills follow Agent Skills standard: folder with `SKILL.md` (or `prompt.md` currently)
- No manifest exists today; no sync to harness paths; no catalog

## Scope
- In scope: Manifest file, sync engine, bundled skills deployment, catalog fetch/browse, update checking, UI redesign, API extensions, migration from current state
- Out of scope: Skill authoring/editing UI, skill marketplace publishing, per-workspace skill scoping, skill versioning beyond git refs
- Constraints / assumptions:
  - Symlinks preferred (copy fallback on Windows via `RuntimeInformation.IsOSPlatform`)
  - Fleet-managed skills are tagged in manifest; user-managed skills in harness paths are never touched
  - Catalog source is a single GitHub-hosted `catalog.json` (configurable URL)
  - Bundled skills ship in the Fleet binary/distribution and are always deployed on startup

## Objectives
- Declarative manifest (`~/.weave/skills.json`) as source of truth for installed skills
- Automatic sync from manifest to all active harness discovery paths
- Bundled skills always present after Fleet startup
- Browsable catalog with one-click install
- Update detection for GitHub-sourced skills with one-click update
- Zero data loss during migration from current `~/.weave/skills/` directory

## Dependencies and Order
1. Domain models first (manifest, catalog DTOs) — everything depends on these
2. Manifest persistence service — sync engine and API both need it
3. Sync engine — must exist before API endpoints can trigger sync
4. Migration logic — runs on startup, converts existing skills to manifest entries
5. API endpoints — depend on all backend services
6. Frontend — depends on API contracts being stable
7. Bundled skills + startup hook — can be done in parallel with API work

## Tasks

- [x] 1. Define domain models
  - **What**: Create records for `SkillManifest`, `SkillManifestEntry` (name, source type enum [Bundled|GitHub|Local], repo URL, ref/branch, target harnesses list, installed date, last updated), `CatalogEntry`, `SkillSyncStatus`.
  - **Files**: `src/WeaveFleet.Domain/Skills/SkillManifest.cs`, `src/WeaveFleet.Domain/Skills/SkillSource.cs`, `src/WeaveFleet.Domain/Skills/CatalogEntry.cs`
  - **Depends on**: None
  - **Acceptance**:
    - Records compile with no warnings
    - `SkillSource` enum has `Bundled`, `GitHub`, `Local` members
    - `SkillManifestEntry` has: `Name`, `Source`, `RepoUrl?`, `Ref?`, `LocalPath?`, `TargetHarnesses` (string list), `InstalledAt`, `UpdatedAt`

- [x] 2. Implement manifest persistence service
  - **What**: `ISkillManifestStore` with `Load()`, `Save()`, `AddEntry()`, `RemoveEntry()`, `UpdateEntry()`. Reads/writes `~/.weave/skills.json`. Use `System.Text.Json` with source generators.
  - **Files**: `src/WeaveFleet.Application/Skills/ISkillManifestStore.cs`, `src/WeaveFleet.Infrastructure/Skills/JsonSkillManifestStore.cs`
  - **Depends on**: Task 1
  - **Acceptance**:
    - Round-trip serialize/deserialize manifest with all entry types
    - File written atomically (write to temp + rename)
    - Missing file returns empty manifest (not error)

- [x] 3. Implement skill sync engine
  - **What**: `ISkillSyncEngine` with `SyncAll()` and `SyncSkill(name)`. For each manifest entry, resolve the skill folder in `~/.weave/skills/<name>/` and create symlinks (or copies) in each target harness discovery path. Track Fleet-managed skills via a `.fleet-managed` marker file in the symlink target. Skip paths that exist but lack the marker (user-managed).
  - **Files**: `src/WeaveFleet.Application/Skills/ISkillSyncEngine.cs`, `src/WeaveFleet.Infrastructure/Skills/SkillSyncEngine.cs`
  - **Depends on**: Task 2
  - **Acceptance**:
    - Creates symlinks on macOS/Linux, copies on Windows
    - Writes `.fleet-managed` marker in each deployed skill folder
    - Does not overwrite folders lacking `.fleet-managed` marker
    - Returns sync status (success/skipped/error per harness per skill)

- [x] 4. Implement GitHub skill fetcher
  - **What**: Service to clone/pull a skill from a GitHub repo (sparse checkout of skill folder or full shallow clone). Stores in `~/.weave/skills/<name>/`. Also provides `CheckForUpdate(entry)` → returns bool + remote ref.
  - **Files**: `src/WeaveFleet.Application/Skills/IGitHubSkillFetcher.cs`, `src/WeaveFleet.Infrastructure/Skills/GitHubSkillFetcher.cs`
  - **Depends on**: Task 1
  - **Acceptance**:
    - Clones repo to local path given a URL + optional ref
    - `CheckForUpdate` compares local HEAD with remote ref without full fetch
    - Handles auth failure gracefully (public repos only for v1)

- [x] 5. Implement catalog service
  - **What**: `ISkillCatalogService` with `FetchCatalog()` → list of `CatalogEntry`. Fetches from a configurable GitHub raw URL (default: a Fleet-maintained repo). Caches locally with 1-hour TTL.
  - **Files**: `src/WeaveFleet.Application/Skills/ISkillCatalogService.cs`, `src/WeaveFleet.Infrastructure/Skills/GitHubSkillCatalogService.cs`
  - **Depends on**: Task 1
  - **Acceptance**:
    - Returns parsed catalog entries from remote JSON
    - Caches to `~/.weave/catalog-cache.json` with timestamp
    - Returns cached data on network failure (with staleness indicator)

- [x] 6. Implement bundled skills deployment
  - **What**: On startup, read bundled skills from embedded resources or a known directory in the distribution. Add them to manifest as `Source=Bundled` if not already present. Run sync.
  - **Files**: `src/WeaveFleet.Infrastructure/Skills/BundledSkillsHostedService.cs`
  - **Depends on**: Tasks 2, 3
  - **Acceptance**:
    - Bundled skills appear in manifest after first startup
    - Bundled skills are synced to all harness paths
    - Removing a bundled skill from manifest is overridden on next startup (always re-added)

- [x] 7. Migration from existing ~/.weave/skills/
  - **What**: On first startup when `skills.json` doesn't exist but `~/.weave/skills/` has entries, generate manifest entries with `Source=Local` for each existing skill folder. Target all registered harnesses.
  - **Files**: `src/WeaveFleet.Infrastructure/Skills/SkillManifestMigrator.cs` (called from `BundledSkillsHostedService` or its own hosted service)
  - **Depends on**: Tasks 2, 3
  - **Acceptance**:
    - Existing skills preserved in manifest as Local source
    - Original folders untouched
    - Migration runs only once (idempotent — skips if manifest exists)

- [x] 8. Harness restart integration
  - **What**: After sync completes, trigger pool restart for affected harnesses so they pick up new skills. Use existing `PooledOpenCodeInstanceRegistry` crash-restart mechanism or a dedicated "soft restart" if available.
  - **Files**: `src/WeaveFleet.Infrastructure/Skills/SkillSyncEngine.cs` (extend), possibly `src/WeaveFleet.Infrastructure/Harnesses/OpenCode/Pooling/PooledOpenCodeInstanceRegistry.cs`
  - **Depends on**: Task 3
  - **Acceptance**:
    - After skill install/remove/update, pooled OpenCode sessions are recycled
    - Non-pooled (active user sessions) are NOT force-restarted (only idle pool slots)

- [x] 9. Rewrite API endpoints
  - **What**: Replace current `SkillEndpoints.cs` with manifest-aware endpoints:
    - `GET /api/skills` — list from manifest (with sync status per harness)
    - `GET /api/skills/catalog` — return catalog entries
    - `POST /api/skills/install` — install from catalog/URL/local, add to manifest, sync
    - `POST /api/skills/{name}/update` — pull latest, update manifest, sync
    - `DELETE /api/skills/{name}` — remove from manifest, remove symlinks, sync
    - `GET /api/skills/{name}/update-check` — check if update available
    - `GET /api/skills/manifest` — raw manifest read
  - **Files**: `src/WeaveFleet.Api/Endpoints/SkillEndpoints.cs` (rewrite)
  - **Depends on**: Tasks 2, 3, 4, 5
  - **Acceptance**:
    - All endpoints return proper HTTP status codes
    - Install triggers sync + harness restart
    - Path traversal protections maintained
    - OpenAPI schema generated (for client codegen)

- [x] 10. Register services in DI
  - **What**: Wire up all new services in `DependencyInjection.cs`. Register hosted service for bundled skills + migration.
  - **Files**: `src/WeaveFleet.Infrastructure/DependencyInjection.cs`
  - **Depends on**: Tasks 2–9
  - **Acceptance**:
    - App starts without DI resolution errors
    - Hosted service runs on startup

- [x] 11. Update frontend API types
  - **What**: Regenerate OpenAPI client types after backend changes. Add new types for manifest entries, catalog entries, sync status.
  - **Files**: `client/src/api/generated/schema.d.ts` (regenerated)
  - **Depends on**: Task 9
  - **Acceptance**:
    - `bun run generate` succeeds
    - New endpoint types present in generated schema

- [x] 12. Implement `use-skills` composable v2
  - **What**: Rewrite composable to support: installed skills (from manifest), catalog browsing, update checking, install/remove/update actions. Separate concerns into `use-skill-catalog.ts` and keep `use-skills.ts` for installed skills.
  - **Files**: `client/src/composables/use-skills.ts` (rewrite), `client/src/composables/use-skill-catalog.ts` (new)
  - **Depends on**: Task 11
  - **Acceptance**:
    - Installed skills show source type, sync status, update availability
    - Catalog data fetched and cached in composable
    - Install/update/remove trigger API calls and refresh state

- [x] 13. Redesign SkillsSection UI
  - **What**: Replace single-section UI with tabbed layout: **Installed** (skills with update badges, remove buttons, sync status indicators), **Catalog** (browsable grid with install buttons), **Custom** (URL/path install form — existing functionality). Use existing shadcn-vue Tabs component.
  - **Files**: `client/src/components/settings/SkillsSection.vue` (rewrite), `client/src/components/settings/skills/InstalledSkillsTab.vue` (new), `client/src/components/settings/skills/CatalogTab.vue` (new), `client/src/components/settings/skills/CustomInstallTab.vue` (new)
  - **Depends on**: Task 12
  - **Acceptance**:
    - Three tabs render correctly
    - Installed tab shows update badge when available
    - Catalog tab shows available skills with "Install" / "Installed" state
    - Custom tab preserves existing URL install flow
    - Remove confirmation before deletion

- [x] 14. Write backend unit tests
  - **What**: Test manifest store (round-trip, atomic write, migration), sync engine (symlink creation, marker detection, skip user-managed), catalog service (cache TTL, offline fallback), GitHub fetcher (mock).
  - **Files**: `tests/WeaveFleet.Infrastructure.Tests/Skills/JsonSkillManifestStoreTests.cs`, `tests/WeaveFleet.Infrastructure.Tests/Skills/SkillSyncEngineTests.cs`, `tests/WeaveFleet.Infrastructure.Tests/Skills/GitHubSkillCatalogServiceTests.cs`
  - **Depends on**: Tasks 2–5
  - **Acceptance**:
    - All tests pass
    - Sync engine tests verify symlink vs copy based on platform
    - Manifest store tests verify atomic write (no partial writes)

- [x] 15. Write frontend component tests
  - **What**: Unit tests for the rewritten composables and smoke tests for the new tab components.
  - **Files**: `client/src/composables/__tests__/use-skills.spec.ts`, `client/src/composables/__tests__/use-skill-catalog.spec.ts`
  - **Depends on**: Tasks 12, 13
  - **Acceptance**:
    - `bun run test` passes
    - Covers install, remove, update, catalog fetch flows

## Verification
1. `dotnet build src/WeaveFleet.Api` — no errors
2. `dotnet test` — all new and existing tests pass
3. `cd client && bun run build && bun run test` — frontend builds and tests pass
4. Manual: Start Fleet → verify `~/.weave/skills.json` created with bundled entries → verify symlinks in `~/.config/opencode/skills/`
5. Manual: Install from catalog → skill appears in manifest + harness path → pooled session recycled
6. Manual: Remove skill → symlinks removed → harness path cleaned
