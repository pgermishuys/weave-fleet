# Auto-Update Mechanism for Weave Fleet

## TL;DR
> **Summary**: Add a self-updating mechanism: backend checks GitHub releases on startup, downloads updates to a staging directory, launcher scripts apply the update on next launch, and the frontend shows update status in Settings.
> **Estimated Effort**: Large

## Context
### Original Request
Implement a complete self-updating pipeline for Weave Fleet — from detecting new versions via the GitHub Releases API, through background download with checksum validation, to applying the update on next app restart via the launcher scripts.

### Key Findings
- **Version source of truth**: `Directory.Build.props` (`0.1.9`), exposed via `FleetInstrumentation.ServiceVersion` (assembly informational version)
- **Existing version endpoint**: `GET /api/version` returns `VersionResponse(Version, Commit)` in `FleetEndpoints.cs`
- **Response records** live in `src/WeaveFleet.Api/Endpoints/ApiResponses.cs` — DTOs are simple `sealed record` types
- **BackgroundService pattern**: Infrastructure layer uses `BackgroundService` base class (e.g. `OutboxDispatchBackgroundService`), registered via `services.AddHostedService<T>()` in `DependencyInjection.cs`
- **Install scripts**: `scripts/install.ps1` and `scripts/install.sh` already handle GitHub release download, checksum validation, archive extraction, and file replacement — can be reused as reference
- **Launcher scripts**: `scripts/launcher.cmd` and `scripts/launcher.sh` are the process entry points; they detect install vs repo layout and exec the .NET binary
- **Settings UI**: `SettingsPage.vue` routes sections via `useSettingsNav()` composable (`SettingsSectionId` union type). The `system` section currently renders `ConfigOverviewSection.vue`
- **Install location**: `~/.weave/fleet/` with `app/`, `bin/`, `VERSION` file
- **GitHub releases repo**: `pgermishuys/fleet-releases`, assets follow pattern `fleet-v{version}-{rid}.{zip|tar.gz}` with per-asset `.sha256` files and a `checksums.txt`
- **Platform RIDs**: `win-x64`, `win-arm64`, `linux-x64`, `osx-arm64`

## Objectives
### Core Objective
Enable Fleet to detect, download, and stage updates automatically, with the launcher applying staged updates on next restart — no manual `fleet update` required.

### Deliverables
- [ ] Backend: `UpdateCheckService` (BackgroundService) that checks GitHub releases API on startup
- [ ] Backend: `UpdateDownloadService` that downloads + validates archives to staging dir
- [ ] Backend: API endpoints for update status and triggering download
- [ ] Frontend: `SystemSection.vue` component showing version + update status
- [ ] Frontend: Global update notification indicator
- [ ] Launcher script changes to detect and apply staged updates
- [ ] Tests for backend update services

### Definition of Done
- [ ] `dotnet build` succeeds with no warnings
- [ ] `GET /api/update/status` returns current version and update availability
- [ ] When a newer release exists, the backend downloads and stages it at `~/.weave/fleet/update/`
- [ ] Launcher scripts detect `~/.weave/fleet/update/` and apply before launching
- [ ] Settings > System shows version and update status in the UI
- [ ] Unit tests pass for version comparison and checksum validation logic

### Guardrails (Must NOT)
- Must NOT block app startup — all update work is async/background
- Must NOT replace binaries while the .NET process is running — only staging
- Must NOT auto-restart the app — user controls when to restart
- Must NOT break the existing `fleet update` manual command
- Must NOT make network calls if running in dev/repo layout (only installed layout)

## TODOs

### Phase 1: Backend — Update Check & State

- [x] 1. **Create update state model and options**
  **What**: Define the update state record and configuration options for the update system.
  **Files**:
    - `src/WeaveFleet.Application/Services/UpdateState.cs` — state record: `UpdateState(UpdateStatus Status, string? LatestVersion, string? DownloadUrl, string? AssetName, string? ReleaseNotes, DateTimeOffset? CheckedAt, string? Error)`; enum `UpdateStatus { UpToDate, Available, Downloading, Staged, Error }`
    - `src/WeaveFleet.Application/Configuration/FleetOptions.cs` — add `UpdateOptions` section: `GitHubRepo` (default `pgermishuys/fleet-releases`), `CheckOnStartup` (default `true`), `StagingDirectory` (default `~/.weave/fleet/update`)
  **Acceptance**: Types compile; `FleetOptions` includes `Update` property

- [x] 2. **Create UpdateCheckService (BackgroundService)**
  **What**: A hosted service that runs once on startup (with a short delay to not block app init), calls the GitHub releases API (`https://api.github.com/repos/{repo}/releases/latest`), compares `tag_name` against `FleetInstrumentation.ServiceVersion` using `System.Version` parsing, and updates shared `UpdateStateHolder`. Only runs when in installed layout (check for `VERSION` file existence at `{appDir}/../../VERSION`). Includes retry with exponential backoff on transient failures.
  **Files**:
    - `src/WeaveFleet.Infrastructure/Services/UpdateCheckService.cs` — `sealed partial class UpdateCheckService : BackgroundService`. Inject `IHttpClientFactory`, `ILogger<UpdateCheckService>`, `FleetOptions`, `UpdateStateHolder`. Use `IHttpClientFactory` with a named client `"GitHubApi"` (set `User-Agent` header). Parse JSON response using `JsonSerializerContext` for AOT compat.
    - `src/WeaveFleet.Infrastructure/Services/UpdateStateHolder.cs` — thread-safe singleton holding `UpdateState` with `lock`-based updates and a `Changed` event (or `IObservable<UpdateState>`)
  **Acceptance**: Service starts, queries GitHub API, updates `UpdateStateHolder`. Logs result. Does not run in dev layout.

- [x] 3. **Register update services in DI**
  **What**: Wire up the new services in the DI container.
  **Files**:
    - `src/WeaveFleet.Infrastructure/DependencyInjection.cs` — add `services.AddSingleton<UpdateStateHolder>()`, `services.AddHostedService<UpdateCheckService>()`, `services.AddHttpClient("GitHubApi", ...)` with `User-Agent` and optional `Accept: application/vnd.github+json` header
  **Acceptance**: App starts without errors; `UpdateCheckService` logs on startup

### Phase 2: Backend — Download & Staging

- [x] 4. **Create UpdateDownloadService**
  **What**: Service that downloads the platform-appropriate archive and its `.sha256` checksum to the staging directory (`~/.weave/fleet/update/`). Validates checksum. Writes a `update-manifest.json` file with version, asset name, checksum, and timestamp. Uses `RuntimeInformation` to determine current RID. Downloads are streamed to avoid large memory allocations.
  **Files**:
    - `src/WeaveFleet.Infrastructure/Services/UpdateDownloadService.cs` — `sealed partial class UpdateDownloadService`. Methods: `Task DownloadUpdateAsync(UpdateState state, CancellationToken ct)`. Determine asset name using same logic as `install.ps1` (`fleet-v{version}-{rid}.zip` on Windows, `.tar.gz` on Unix). Download archive + `.sha256` file. Validate SHA256. Write `update-manifest.json`. Update `UpdateStateHolder` to `Staged`.
    - `src/WeaveFleet.Infrastructure/Services/UpdateManifest.cs` — `sealed record UpdateManifest(string Version, string AssetFileName, string Sha256, DateTimeOffset DownloadedAt)`
  **Acceptance**: Given a newer version is available, the archive is downloaded to `~/.weave/fleet/update/`, checksum validated, manifest written

- [x] 5. **Integrate download into UpdateCheckService**
  **What**: After a successful version check finds a newer version, automatically trigger the download via `UpdateDownloadService`. The check service transitions state: `UpToDate` → done, `Available` → calls download → `Staged` or `Error`.
  **Files**:
    - `src/WeaveFleet.Infrastructure/Services/UpdateCheckService.cs` — inject `UpdateDownloadService`, call after version check if update available
    - `src/WeaveFleet.Infrastructure/DependencyInjection.cs` — register `UpdateDownloadService` as singleton
  **Acceptance**: Full flow works end-to-end: check → download → stage

### Phase 3: Backend — API Endpoints

- [x] 6. **Add update status API endpoints**
  **What**: Expose update state to the frontend and allow manual trigger.
  **Files**:
    - `src/WeaveFleet.Api/Endpoints/UpdateEndpoints.cs` — new static class `UpdateEndpoints` with `MapUpdateEndpoints` extension method. Endpoints:
      - `GET /api/update/status` → returns `UpdateStatusResponse(string CurrentVersion, string Status, string? LatestVersion, string? ReleaseNotes, string? CheckedAt, string? Error)` from `UpdateStateHolder`
      - `POST /api/update/check` → manually triggers an update check (calls `UpdateCheckService` logic)
      - `POST /api/update/download` → manually triggers download if state is `Available`
    - `src/WeaveFleet.Api/Endpoints/ApiResponses.cs` — add `UpdateStatusResponse` record
    - `src/WeaveFleet.Api/JsonContext.cs` — add `UpdateStatusResponse` to the `JsonSerializerContext` for AOT
    - `src/WeaveFleet.Api/Program.cs` — call `app.MapUpdateEndpoints()` alongside existing `MapFleetSummaryEndpoints()`
  **Acceptance**: `GET /api/update/status` returns valid JSON; `POST /api/update/check` triggers a fresh check

### Phase 4: Frontend — System Section & Update UI

- [x] 7. **Create update status composable**
  **What**: Vue composable that fetches update status from the API and provides reactive state.
  **Files**:
    - `client/src/composables/use-update-status.ts` — `useUpdateStatus()` composable. Fetches `GET /api/update/status` on mount. Exposes `status`, `currentVersion`, `latestVersion`, `isUpdateAvailable`, `isUpdateStaged`, `checkForUpdate()`, `downloadUpdate()`. Polls every 30s while update is downloading.
  **Acceptance**: Composable returns reactive update state from API

- [x] 8. **Create SystemSection.vue component**
  **What**: New settings section showing app version, commit, and update status. Replace the current `ConfigOverviewSection` rendering for the `system` nav item with a composite that includes both system info and config overview.
  **Files**:
    - `client/src/components/settings/SystemSection.vue` — shows: current version (from `useUpdateStatus`), commit hash, update status badge (`Up to date` / `Update available: v{x.y.z}` / `Update staged — restart to apply` / `Checking...`), "Check for updates" button, and "Download update" button (when available). Styled consistently with existing sections (rounded-card pattern).
    - `client/src/components/settings/SettingsPage.vue` — import `SystemSection`, render it for `activeSection === 'system'` (replace or augment existing `ConfigOverviewSection` — render both `SystemSection` then `ConfigOverviewSection` in sequence)
  **Acceptance**: Settings > System shows version info and update status

- [x] 9. **Add global update notification indicator**
  **What**: A subtle indicator (e.g. dot badge on the Settings nav item, or a thin banner at the top of the app) when an update is staged and ready to apply on restart.
  **Files**:
    - `client/src/components/settings/SettingsNavPanel.vue` — add a small dot/badge next to the "System" nav item when `isUpdateAvailable || isUpdateStaged` is true (use `useUpdateStatus` composable)
  **Acceptance**: When an update is staged, user sees a visual indicator without navigating to Settings

### Phase 5: Launcher — Apply Staged Updates

- [x] 10. **Add update application logic to launcher.sh**
  **What**: Before launching the app binary, check if `$ROOT_DIR/update/update-manifest.json` exists. If so: (1) read version from manifest, (2) log "Applying Fleet update to v{version}...", (3) backup current `app/` to `app.bak/`, (4) extract the staged archive over `app/`, (5) update `VERSION` file, (6) remove `update/` directory, (7) remove `app.bak/` on success. On failure, restore from `app.bak/`.
  **Files**:
    - `scripts/launcher.sh` — add `apply_staged_update()` function called between layout detection and arg parsing. Uses `tar` for extraction, `sha256sum`/`shasum` for validation, `jq` or simple `grep`/`sed` for reading JSON manifest. Keep it POSIX-compatible (no bash-isms).
  **Acceptance**: On Unix, a staged update in `~/.weave/fleet/update/` is applied before the app starts; the old version is cleaned up

- [x] 11. **Add update application logic to launcher.cmd**
  **What**: Same logic as launcher.sh but for Windows batch script. Before starting the app, check for `%ROOT_DIR%\update\update-manifest.json`. If present, apply update using PowerShell one-liner for JSON parsing and `tar`/`Expand-Archive` for extraction.
  **Files**:
    - `scripts/launcher.cmd` — add `:apply_staged_update` subroutine called before `:parse_args`. Uses PowerShell inline for JSON parsing (`powershell -NoProfile -Command "(Get-Content ... | ConvertFrom-Json).Version"`). Uses `Expand-Archive` or `tar` for extraction. Backs up `app\` to `app.bak\`, restores on failure.
  **Acceptance**: On Windows, a staged update in `~/.weave/fleet/update/` is applied before the app starts

### Phase 6: Testing

- [x] 12. **Unit tests for version comparison and update logic**
  **What**: Test the version comparison logic (semver parsing, pre-release handling), checksum validation, RID detection, and state transitions.
  **Files**:
    - `tests/WeaveFleet.Tests/Services/UpdateCheckServiceTests.cs` — test version comparison: same version → UpToDate, newer version → Available, malformed version → handled gracefully. Test RID asset name generation for each platform.
    - `tests/WeaveFleet.Tests/Services/UpdateDownloadServiceTests.cs` — test checksum validation (matching hash → success, mismatched hash → Error state). Test manifest serialization/deserialization.
  **Acceptance**: All tests pass with `dotnet test`

- [x] 13. **Integration test for update status endpoint**
  **What**: Test the API endpoint returns correct state.
  **Files**:
    - `tests/WeaveFleet.Tests/Endpoints/UpdateEndpointTests.cs` — use `WebApplicationFactory` pattern (if established in the test project) or minimal endpoint testing. Seed `UpdateStateHolder` with known state, verify `GET /api/update/status` returns expected JSON.
  **Acceptance**: Endpoint test passes

## Verification
- [ ] `dotnet build src/WeaveFleet.Api` succeeds with no warnings
- [ ] `dotnet test` — all existing and new tests pass
- [ ] Manual test: start app with older version, verify `GET /api/update/status` shows `Available`
- [ ] Manual test: verify archive downloaded to `~/.weave/fleet/update/` with valid checksum
- [ ] Manual test: restart app via launcher, verify update applied and new version reported
- [ ] Manual test: Settings > System shows correct version and update status
- [ ] Verify `fleet update` manual command still works unchanged
