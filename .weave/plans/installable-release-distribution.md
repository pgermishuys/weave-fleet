# Installable Release Distribution for Weave Fleet

## TL;DR
> **Summary**: Add release packaging, installer scripts, and a `weave-fleet` CLI command so users can install weave-fleet publicly from GitHub Releases with a one-liner (`curl -fsSL https://github.com/pgermishuys/weave-fleet/releases/latest/download/install.sh | sh`) and run it locally. Vanity URL hosting, trimming, and phase 3 distribution work are intentionally deferred.
> **Estimated Effort**: Large

## Context

### Original Request
Make weave-fleet downloadable/installable with the same UX as weave-agent-fleet: one-line installer, stable vanity URL, `weave-fleet` command, checksums, cross-platform support.

### Current Planning Decisions
- **Public distribution channel**: GitHub Releases is the primary install path.
- **Vanity URL**: Deferred / out of scope for now; not a blocker for the current release path.
- **Trimming**: Deferred until a dedicated compatibility follow-up.
- **Phase 3 items**: Deferred; not blockers for the current insiders/public GitHub Releases release path.

### Key Findings
- **weave-fleet** is a .NET 10 backend (`WeaveFleet.Api`) + Vue/Vite SPA (`client/`). It already has CI and a server deploy workflow but zero release packaging.
- **WeaveFleet.Cli** exists but is a bare skeleton (`Program.cs` only).
- **weave-agent-fleet** (the reference) ships as: bundled Node.js binary + Next.js standalone output + shell/cmd launcher scripts, packaged as tarballs/zips per platform, with `install.sh`/`install.ps1` fetching from GitHub Releases, and a `deploy-vanity-url` job copying scripts to gh-pages served via `get.tryweave.io`.
- .NET self-contained publish produces a single directory with the runtime bundled — no need to ship a separate runtime binary (unlike Node.js). This simplifies packaging.
- The Vue SPA is already copied into `wwwroot/` at build time via an MSBuild target, so a published .NET app serves the frontend directly.

### Key Differences from Agent Fleet
| Concern | agent-fleet (Node.js) | weave-fleet (.NET) |
|---|---|---|
| Runtime | Bundled Node.js binary | .NET self-contained publish (runtime included) |
| App payload | Next.js standalone dir | Single published directory |
| Launcher | Shell script calling `node server.js` | Shell script calling the native binary directly |
| Frontend | Built into Next.js standalone | Vue SPA in `wwwroot/` (embedded in publish) |
| Native deps | better-sqlite3 .node addon | Managed by .NET — no native addon wrangling |

## Objectives

### Core Objective
Users can install and run weave-fleet locally on macOS, Linux, and Windows with a single command.

### Deliverables
_Roll-up summary checklist only; reflects the executable tasks and blocked verification items below rather than separate work to perform._

- [ ] Self-contained .NET publish producing per-platform artifacts
- [ ] Launcher scripts (`weave-fleet` shell, `weave-fleet.cmd`)
- [ ] Installer scripts (`install.sh`, `install.ps1`) published on GitHub Releases
- [ ] GitHub Actions release workflow triggered by `v*` tags
- [ ] SHA-256 checksums published with each release
- [ ] `RELEASE.md` documenting the release procedure

### Definition of Done
_Roll-up summary checklist only; remains unchecked until the underlying live-release/runtime verification items below are satisfied._

- [ ] `curl -fsSL https://github.com/pgermishuys/weave-fleet/releases/latest/download/install.sh | sh` installs weave-fleet
- [ ] `weave-fleet` starts the server and serves the UI at `http://localhost:5000`
- [ ] `weave-fleet version` prints the installed version
- [ ] Checksums verify on all platforms

### Guardrails (Must NOT)
- Do NOT require users to install .NET SDK — self-contained only
- Do NOT change the existing cloud deploy workflow
- Do NOT ship Docker images in phase 1 (future consideration)
- Do NOT sign binaries in phase 1 (checksums only)

## Product Goals and Non-Goals

### Goals
1. Local-first install experience matching agent-fleet UX
2. Cross-platform: macOS (arm64, x64), Linux (x64, arm64), Windows (x64)
3. Single binary + launcher — no runtime prerequisites
4. Version pinning (`WEAVE_VERSION=x.y.z`)
5. Self-update command (`weave-fleet update`)

### Non-Goals
- Homebrew/apt/winget packages (phase 2+)
- Docker distribution (phase 2+)
- Code signing / notarization (phase 2+)
- Auto-update daemon
- Framework-dependent publish (always self-contained)

## Target Install UX

### macOS / Linux
```bash
curl -fsSL https://github.com/pgermishuys/weave-fleet/releases/latest/download/install.sh | sh
# or with version pinning:
WEAVE_VERSION=0.1.0 curl -fsSL https://github.com/pgermishuys/weave-fleet/releases/latest/download/install.sh | sh
```

### Windows (PowerShell)
```powershell
irm https://github.com/pgermishuys/weave-fleet/releases/latest/download/install.ps1 | iex
```

### Post-install
```bash
weave-fleet          # start server
weave-fleet version  # print version
weave-fleet update   # re-run installer for latest
weave-fleet uninstall
```

## Artifact Strategy

**Decision: .NET self-contained, single-directory publish (not single-file).**

Rationale:
- `dotnet publish -r <rid> --self-contained -p:PublishSingleFile=false` produces a directory with the runtime + app.
- Single-file publish has known issues with some native libraries and startup time; directory publish is more reliable.
- The Vue SPA is embedded in `wwwroot/` via the existing MSBuild copy target — no separate frontend artifact needed.
- Trimming (`PublishTrimmed=true`) can reduce size but risks breaking reflection-heavy code (ASP.NET). Defer to phase 2 with testing.

### RIDs (Runtime Identifiers)
| Platform | RID | Archive |
|---|---|---|
| macOS arm64 | `osx-arm64` | `.tar.gz` |
| Linux x64 | `linux-x64` | `.tar.gz` |
| Windows x64 | `win-x64` | `.zip` |

## CLI / Launcher Strategy

### Naming
- Command: `weave-fleet`
- Install dir: `~/.weave/weave-fleet/` (distinct from agent-fleet's `~/.weave/fleet/`)
- Binary in: `~/.weave/weave-fleet/bin/weave-fleet` (shell script that execs the .NET binary)

### Launcher Design
The launcher is a thin shell script (like agent-fleet's `launcher.sh`) that:
1. Resolves install dir relative to itself
2. Sets environment variables (`ASPNETCORE_URLS`, `ASPNETCORE_ENVIRONMENT=Production`)
3. Execs the .NET binary (`WeaveFleet.Api`)
4. Handles subcommands: `version`, `update`, `uninstall`, `help`

Unlike agent-fleet, we don't need a bundled Node.js binary — the .NET self-contained publish includes everything.

### WeaveFleet.Cli Consideration
The existing `WeaveFleet.Cli` project could eventually become the launcher (compiled .NET binary with subcommands). For phase 1, use shell scripts to match agent-fleet's pattern and ship faster. Phase 2 can migrate to a compiled CLI if needed.

## Versioning / Source of Truth

**Decision: Version lives in `Directory.Build.props` as `<Version>` property.**

- All .NET projects inherit it automatically.
- Release workflow reads it or derives from git tag (`v0.1.0` → `0.1.0`).
- A `VERSION` file is written into the package at build time (for the launcher's `version` subcommand).
- Tag format: `v{major}.{minor}.{patch}` (e.g., `v0.1.0`).

## TODOs

### Phase 1: Minimal Shippable Install (First Slice)

- [x] 1. Add publish configuration to WeaveFleet.Api
  **What**: Add self-contained publish properties to the API project. Set the output to include the Vue SPA in wwwroot.
  **Files**: `src/WeaveFleet.Api/WeaveFleet.Api.csproj`
  **Acceptance**: `dotnet publish src/WeaveFleet.Api -r osx-arm64 --self-contained -c Release` produces a working directory with wwwroot/ containing the SPA.

- [x] 2. Add version property to Directory.Build.props
  **What**: Add `<Version>0.1.0</Version>` as the single source of truth for versioning.
  **Files**: `Directory.Build.props`
  **Acceptance**: `dotnet publish` output assemblies report version 0.1.0.

- [x] 3. Create launcher scripts
  **What**: Create `scripts/launcher.sh` (macOS/Linux) and `scripts/launcher.cmd` (Windows) that wrap the .NET binary with subcommands (version, update, uninstall, help) and environment setup. Mirror agent-fleet's launcher pattern but exec the .NET binary directly instead of `node server.js`.
  **Files**: `scripts/launcher.sh`, `scripts/launcher.cmd`
  **Acceptance**: `./scripts/launcher.sh version` prints the version. `./scripts/launcher.sh` starts the server.

- [x] 4. Create installer scripts
  **What**: Create `scripts/install.sh` and `scripts/install.ps1` mirroring agent-fleet's installers. Detect platform/arch, download tarball from GitHub Releases, verify checksum, extract to `~/.weave/weave-fleet/`, add `bin/` to PATH.
  **Files**: `scripts/install.sh`, `scripts/install.ps1`
  **Acceptance**: Running `sh scripts/install.sh` with a mock release installs correctly. Script passes shellcheck.

- [x] 5. Create packaging script
  **What**: Create `scripts/package.sh` that takes a published .NET output directory and assembles the final tarball: copies launcher to `bin/weave-fleet`, copies published app to `app/`, writes `VERSION` file, creates `.tar.gz` with checksum.
  **Files**: `scripts/package.sh`, `scripts/package.ps1`
  **Acceptance**: Script produces `weave-fleet-v0.1.0-osx-arm64.tar.gz` and `.sha256` from a publish directory.

- [x] 6. Create GitHub Actions release workflow
  **What**: Create `.github/workflows/release.yml` triggered on `v*` tags. Matrix build across 5 RIDs: build Vue client, dotnet publish self-contained, run package script, upload artifacts. Release job: merge checksums, create GitHub Release with all artifacts + installer scripts.
  **Files**: `.github/workflows/release.yml`
  **Acceptance**: Pushing a `v0.1.0` tag triggers the workflow and produces a GitHub Release with 5 platform archives + checksums.txt + install.sh + install.ps1.

- [ ] 7. Vanity URL deployment _(deferred / out of scope for now)_
  **What**: Do not treat `get.tryweave.io/fleet.sh` as part of the current release path. Revisit only after the public GitHub Releases flow is stable and there is an explicit hosting owner/contract.
  **Files**: None required for the current public release path.
  **Acceptance**: Deferred. GitHub Releases remains the primary install path until this is explicitly re-scoped.

- [x] 8. Create RELEASE.md
  **What**: Document the release procedure: version bump in Directory.Build.props, tag, push, verify. Mirror agent-fleet's RELEASE.md adapted for .NET.
  **Files**: `RELEASE.md`
  **Acceptance**: Document exists and covers version files, release order, verification checklist.

### Phase 2: Polish and Hardening

- [x] 9. Add --port and --profile flags to launcher
  **What**: Extend launcher scripts to parse `--port` and `--profile` flags, mapping to `ASPNETCORE_URLS` and a profile-specific data directory.
  **Files**: `scripts/launcher.sh`, `scripts/launcher.cmd`
  **Acceptance**: `weave-fleet --port 8080` starts on port 8080.

- [ ] 10. Add trimming with compatibility testing _(deferred)_
  **What**: Keep the public GitHub Releases path on non-trimmed self-contained publishes for now. Revisit `PublishTrimmed=true` only in a dedicated follow-up that remediates current trim-safety/accessibility warnings (`RDG012`, `IL2026`, `IL2091`).
  **Files**: `src/WeaveFleet.Api/WeaveFleet.Api.csproj`
  **Acceptance**: Deferred. Trimming is intentionally out of scope for the current release path.

- [x] 11. Add notify-website job
  **What**: Dispatch event to weave-website repo to update fleet version, mirroring agent-fleet's pattern.
  **Files**: `.github/workflows/release.yml`
  **Acceptance**: Website dispatch fires on release.

### Phase 3: Future (Deferred)

These items are intentionally deferred and are not blockers for the current public GitHub Releases distribution path.

- [ ] 12. Code signing and notarization (macOS, Windows) _(deferred)_
- [ ] 13. Homebrew tap / apt repo / winget manifest _(deferred)_
- [ ] 14. Docker image distribution _(deferred)_
- [ ] 15. Migrate launcher to compiled WeaveFleet.Cli _(deferred)_

## Verification

- [ ] `curl -fsSL https://github.com/pgermishuys/weave-fleet/releases/latest/download/install.sh | sh` installs successfully on macOS arm64 _(primary public install path; live verification still depends on a real published release)_
- [x] `weave-fleet` starts server, UI accessible at http://localhost:5000
- [x] `weave-fleet version` prints correct version
- [x] `weave-fleet update` re-installs latest
- [x] `weave-fleet uninstall` removes installation
- [x] Checksums match on all 5 platform artifacts
- [ ] Windows install via PowerShell works _(partially verified only: installer/package layout works under `pwsh`, but native Windows runtime execution remains blocked on access to a real Windows host or CI release run)_
- [ ] Release workflow completes end-to-end on a test tag _(blocked on creating and observing a real tagged GitHub Actions release run in this repository/environment; workflow wiring and smoke-test steps exist, but no live tag execution evidence is available here)_

## Risks, Unknowns, and Recommended Defaults

### Risks
| Risk | Impact | Mitigation |
|---|---|---|
| .NET 10 is preview (SDK 10.0.100) | Users can't build from source easily | Self-contained publish eliminates this — no SDK needed |
| Large binary size (~80-150MB self-contained) | Slow downloads | Accept for the current public release path; revisit trimming only in a dedicated follow-up |
| macOS Gatekeeper blocks unsigned binary | Users get security warning | Document `xattr -d com.apple.quarantine` workaround; signing in phase 3 |
| No vanity URL yet | Install command is longer | Use GitHub Releases directly; revisit a vanity URL only if it becomes worth the extra hosting/deploy surface area |

### Unknowns
- **Vanity URL routing**: Deferred. No routing/hosting decision is required for the current GitHub Releases-based distribution plan.
- **Default port**: Agent-fleet uses 3000. Weave-fleet's API likely uses 5000 (ASP.NET default). **Recommend**: Default to 5000 to avoid conflicts when both are running.
- **Data directory**: `~/.weave/weave-fleet/` for install, `~/.weave/` for data (DB, workspaces). Confirm no collision with agent-fleet's `~/.weave/fleet.db`.

### Recommended Defaults
| Decision | Default | Rationale |
|---|---|---|
| Publish mode | Self-contained, not trimmed, not single-file | Maximum compatibility |
| Public install channel | GitHub Releases installer assets | Simplest public distribution path today |
| Install dir | `~/.weave/weave-fleet/` | Distinct from agent-fleet |
| Default port | 5000 | ASP.NET convention, avoids 3000 conflict |
| Version source | `Directory.Build.props` `<Version>` | .NET convention |
| Launcher | Shell scripts (not compiled CLI) | Ship faster, iterate |
| Archive format | tar.gz (Unix), zip (Windows) | Matches agent-fleet |
| Checksum | SHA-256 per-artifact + merged checksums.txt | Matches agent-fleet |

## Recommended First Slice

**Ship tasks 1–6 and 8 only.** This gives:
- `dotnet publish` producing self-contained artifacts
- Launcher scripts with basic subcommands
- Installer scripts that fetch from GitHub Releases
- A release workflow that builds, packages, and publishes
- Release documentation for the public GitHub Releases path

Keep vanity URL (task 7), trimming (task 10), and phase 3 items deferred — users install via:
```bash
curl -fsSL https://github.com/pgermishuys/weave-fleet/releases/latest/download/install.sh | sh
```

That GitHub Releases path is the primary public install flow for now.
