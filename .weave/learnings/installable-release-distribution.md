# Learnings: Installable Release Distribution

## Task 2: Add version property to Directory.Build.props
- **Discrepancy**: The plan asked only for `<Version>0.1.0</Version>`, but verification surfaced .NET's four-part assembly/file version formatting (`0.1.0.0`) and commit-suffixed product version behavior.
- **Resolution**: Verified that the shared `<Version>` property is enough to satisfy the acceptance criteria for published assembly metadata in phase 1.
- **Suggestion**: Future versioning tasks should explicitly call out whether separate `AssemblyVersion` and `FileVersion` properties are desired or whether `Version` alone is the intended source of truth.

## Task 3: Create launcher scripts
- **Discrepancy**: The initial launcher task description did not mention that repo/dev verification layout needs a different content root from the packaged layout, and it left the default port ambiguous versus the plan's later recommended default of 5000.
- **Resolution**: Updated both launchers to distinguish packaged vs repo content roots, force aligned listen URLs via `--urls`, and use port 5000 by default.
- **Suggestion**: Launcher tasks should explicitly state the expected default port and whether scripts must support both installed-package and repo/dev execution modes.

## Task 4: Create installer scripts
- **Discrepancy**: The task text said installers should download a tarball from GitHub Releases, but the Windows packaging strategy elsewhere in the plan specifies `.zip` artifacts for `win-x64`.
- **Resolution**: Implemented Unix installer support for `.tar.gz` and Windows installer support for `.zip` with a `.tar.gz` fallback, plus checksum verification from either per-asset `.sha256` files or a merged `checksums.txt` manifest.
- **Suggestion**: Installer tasks should explicitly map expected archive formats per platform and mention whether test hooks/env overrides are acceptable for mock-release verification.

## Task 5: Create packaging script
- **Discrepancy**: The task description focused on `scripts/package.sh` producing a Unix tarball, but the referenced file list also included `scripts/package.ps1` without stating its expected Windows archive behavior.
- **Resolution**: Implemented `package.sh` for `.tar.gz` packaging and `package.ps1` with RID-aware behavior so Windows packaging emits `.zip` while non-Windows can still emit `.tar.gz`.
- **Suggestion**: Packaging tasks should spell out the expected output format for every referenced script, especially when cross-platform parity is required.

## Task 6: Create GitHub Actions release workflow
- **Discrepancy**: The plan's workflow task did not explicitly require a smoke test or clarify whether the client build should follow the repo's existing Bun-based CI pattern versus the npm lockfile/packageManager metadata.
- **Resolution**: Added package smoke tests before upload and used the repo's existing `client/package-lock.json` plus `packageManager: npm` metadata for release workflow dependency installation/build steps.
- **Suggestion**: Release-workflow tasks should explicitly call out required smoke-test coverage and whether release builds must mirror CI's package manager commands exactly or may rely on the lockfile/packageManager source of truth.

## Task 7: Create vanity URL deployment job
- **Discrepancy**: The plan assumed `get.tryweave.io` could be updated from this repo, but the actual Pages-backed repo/branch and required cross-repo deployment credentials are not available here.
- **Resolution**: Added guarded workflow scaffolding that can publish `fleet.sh` once `VANITY_URL_TARGET_REPOSITORY`, optional `VANITY_URL_TARGET_BRANCH`, and `VANITY_URL_DEPLOY_TOKEN` are configured; left the task unchecked because end-to-end verification is externally blocked.
- **Suggestion**: Plans that depend on vanity URL hosting should state the owning repo/branch and required secrets as explicit prerequisites before implementation begins.

## Task 8: Create RELEASE.md
- **Discrepancy**: The release-doc task could not fully mirror the ideal install story because the vanity URL step remains externally blocked and conditional.
- **Resolution**: Documented the current GitHub Releases-based release flow as the primary path and called out the vanity URL deployment as guarded/conditional on external configuration.
- **Suggestion**: Documentation tasks should explicitly state when a referenced workflow step is optional or externally gated so the runbook can distinguish mandatory verification from conditional verification.

## Plan/docs alignment update: public GitHub Releases path
- **Discrepancy**: Earlier plan/docs language still treated the vanity URL as part of the primary install/verification path and described trimming plus phase 3 work as active blockers, which no longer matched the decided release posture.
- **Resolution**: Updated the plan and release doc so GitHub Releases is the explicit primary public install channel, vanity URL work is deferred/out of scope, trimming is intentionally deferred, and phase 3 items are deferred rather than blockers for the current release path.
- **Suggestion**: When distribution decisions change, update the plan TL;DR, install examples, verification checklist, and deferred-work sections together so docs do not leave old blockers or deprecated install commands behind.

## Task 9: Add --port and --profile flags to launcher
- **Discrepancy**: The plan specified `--profile` only as mapping to a profile-specific data directory but did not define the concrete profile path shape or input validation rules.
- **Resolution**: Added profile-specific data roots under `~/.weave/profiles/<name>` (and the Windows equivalent), numeric port validation, and safe profile-name validation alongside the existing launcher environment setup.
- **Suggestion**: Future launcher-option tasks should define the exact data-root convention and acceptable flag value formats to reduce guesswork.

## Task 10: Add trimming with compatibility testing
- **Discrepancy**: The plan treated trim enablement as a single-project-file task, but the first trimmed publish attempt fails due to trim-unsafe code and request-type accessibility issues across multiple source files outside `src/WeaveFleet.Api/WeaveFleet.Api.csproj`.
- **Resolution**: Left the project file unchanged and captured evidence that trimmed publish currently fails with `RDG012`, `IL2026`, and `IL2091`, so no honest trimmed artifact or size-reduction validation is possible yet.
- **Suggestion**: Split trimming into a dedicated compatibility remediation plan spanning the affected endpoint/request/serialization/DI files before re-attempting `PublishTrimmed=true`.

## Task 11: Add notify-website job
- **Discrepancy**: End-to-end verification of the website notification depends on an external repo and token, which the plan did not call out as an execution prerequisite even though the event contract lives outside this repository.
- **Resolution**: Added a guarded `notify-website` job using the same `fleet-version-update` repository dispatch contract consumed by `pgermishuys/weave-website`, and verified the external workflow listens for that event type.
- **Suggestion**: Cross-repo notification tasks should explicitly name the target repository, event type, and required secret so local execution can distinguish code wiring from external-runtime verification.

## Task 12: Code signing and notarization (macOS, Windows)
- **Discrepancy**: The plan provided only a title for signing/notarization and omitted the concrete files, packaging targets, verification criteria, and external credentials/services required to implement it.
- **Resolution**: Treated the task as blocked, documented the exact missing prerequisites in `RELEASE.md`, and avoided adding guessed CI wiring or secret names.
- **Suggestion**: Signing tasks should specify the artifact types to sign, the CI trust model (certs vs remote signing), the expected secret inventory, and the verification command/check before implementation starts.

## Task 13: Homebrew tap / apt repo / winget manifest
- **Discrepancy**: The plan listed package-registry publishing only as a title and did not define the owning tap/repo/manifests, package identifiers, signing requirements, or verification workflow.
- **Resolution**: Treated the task as externally blocked and documented the missing registry prerequisites in `RELEASE.md` instead of guessing external publishing automation.
- **Suggestion**: Package-registry tasks should enumerate each target registry's ownership model, package identity, trust/signing requirements, and acceptance verification commands up front.

## Task 14: Docker image distribution
- **Discrepancy**: The plan listed Docker distribution only as a title and omitted the registry target, image contract, runtime support expectations, credentials, and verification flow needed to implement it safely.
- **Resolution**: Treated the task as blocked and documented the missing Docker distribution prerequisites in `RELEASE.md` rather than inventing a container publishing strategy.
- **Suggestion**: Docker tasks should define the target registry, image/tag naming scheme, supported runtime configuration, and acceptance commands before execution starts.

## Task 16: Verification - direct GitHub Releases install flow
- **Discrepancy**: The plan's verification checklist assumed a live vanity URL and live GitHub release, but several verification items can be proven locally only against GitHub-Releases-shaped mock assets while others remain externally blocked.
- **Resolution**: Verified the direct GitHub Releases path locally (release build/tests clean, package checksums valid, Unix install/version/health working, Windows package/install layout working) and treated live release/tag/website/vanity execution as external blockers. A transient E2E timeout during one run proved unrelated because the targeted test and the full release-mode suite passed on rerun without code changes.
- **Suggestion**: Verification sections should split local/mock verification from live-release verification explicitly so transient CI/test flakiness and external deployment prerequisites are easier to classify.

## Task 17: Verification - update and uninstall behavior
- **Discrepancy**: The installed launcher hard-coded the live GitHub install URL, which made safe local/mock verification of `weave-fleet update` impossible even though the underlying installer flow supported overrides.
- **Resolution**: Added `WEAVE_FLEET_INSTALL_SCRIPT_URL` overrides to both launchers, then locally verified Unix `update` and `uninstall` against a GitHub-Releases-shaped mock source; Windows PowerShell install layout was verified but full runtime remains blocked on a non-Windows host.
- **Suggestion**: Launcher tasks should expose test-friendly override seams for networked self-update behavior so update flows can be validated without requiring a live release.

## Task 10 retry: trim remediation scope
- **Discrepancy**: Re-attempting trim enablement confirmed the problem is broader than the API project and cannot be solved safely with a narrow csproj tweak.
- **Resolution**: Captured concrete cross-project blockers spanning `WeaveFleet.Application` and `WeaveFleet.Infrastructure`, including widespread `IL2026` JSON serialization sites, `RDG012` minimal API request-type accessibility issues, and `IL2091` generic DI annotation gaps.
- **Suggestion**: Treat trimming as a dedicated follow-up effort that introduces source-generated JSON contexts / explicit `JsonTypeInfo` plumbing plus trim-safe DI and minimal API fixes across the named files before re-enabling `PublishTrimmed=true`.

## Task 13: Homebrew tap / apt repo / winget manifest
- **Discrepancy**: The plan again provided only a title, but package-registry publishing depends on external repositories, package identities, signing/trust setup, and verification contracts that are not defined in this repo.
- **Resolution**: Treated the task as blocked, documented the exact missing Homebrew/apt/winget prerequisites in `RELEASE.md`, and avoided adding guessed registry workflows, manifests, or ownership assumptions.
- **Suggestion**: Registry-distribution tasks should name the owning tap/repo/manifests, required credentials/signing assets, package identifiers, and the exact publish/verification commands before implementation starts.

## Task 14: Docker image distribution
- **Discrepancy**: The plan again provided only a title, but Docker publishing depends on an explicit container build contract, target registry ownership, credentials, runtime support expectations, and verification steps that are not defined in this repo.
- **Resolution**: Treated the task as blocked, documented the exact Docker image prerequisites in `RELEASE.md`, and avoided adding a guessed `Dockerfile`, registry workflow, or image tags.
- **Suggestion**: Docker-distribution tasks should specify the container build strategy, target registry/repository, tagging scheme, auth model, runtime contract (ports/volumes/env), and required verification commands before implementation starts.

## Task 15: Migrate launcher to compiled WeaveFleet.Cli
- **Discrepancy**: The plan again provided only a title, but migrating from the current shell/cmd launchers to `WeaveFleet.Cli` would change the launcher contract, package layout, release workflow, smoke tests, and potentially the runtime architecture without defining any of those details.
- **Resolution**: Treated the task as blocked, documented the exact missing CLI/runtime/packaging prerequisites in `RELEASE.md`, and avoided guessing a command surface or partially replacing the working script launchers.
- **Suggestion**: Compiled-launcher tasks should explicitly define command parity, process model, packaged entrypoints per platform, compatibility expectations for existing install paths, and end-to-end verification before implementation starts.
