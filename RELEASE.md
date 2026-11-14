# Releasing Weave Fleet

This repository releases installable Weave Fleet builds from Git tags. The release workflow is `.github/workflows/release.yml` and runs when you push a tag named `v*`.

GitHub Releases is the primary public distribution channel. The supported installer entrypoints are the release-hosted assets:

- `https://github.com/pgermishuys/weave-fleet/releases/latest/download/install.sh`
- `https://github.com/pgermishuys/weave-fleet/releases/latest/download/install.ps1`

## Version source of truth

- Edit **only** the root `Directory.Build.props` `<Version>` value.
- Use plain package version format there, for example `0.1.0`.
- The Git tag must be the same version with a `v` prefix, for example `v0.1.0`.
- Packaged `VERSION` files are generated during packaging. Do not edit them manually.

## Before you tag

1. Make sure the release commit is on `main`.
2. Update `Directory.Build.props`:

   ```xml
   <Version>0.1.0</Version>
   ```

3. Commit and push that version bump.
4. Confirm the tag you plan to create matches the file version exactly:

   - `Directory.Build.props` = `0.1.0`
   - Git tag = `v0.1.0`

## Release order

1. **Bump the version** in `Directory.Build.props`.
2. **Merge/push the release commit** to `main`.
3. **Create the release tag** from that commit:

   ```bash
   git tag -a v0.1.0 -m "v0.1.0"
   ```

4. **Push the tag**:

   ```bash
   git push origin v0.1.0
   ```

5. **Wait for the `Release` GitHub Actions workflow** to finish. Its first job, **CI passed on this commit**, waits
   for CI (`ci.yml`) on the tagged commit before anything is built: pushing the tag right after the version bump is
   fine, the release just starts about 8 minutes later, once that commit's CI run is green.
6. **Verify the GitHub Release** and release-hosted installer endpoints.
7. **Optional: write a highlight.** Edit the release on `pgermishuys/weave-fleet` and put a sentence or two above
   "What's Changed", saying what's worth knowing about this version. Fleet's What's new card (shown once after an
   update) leads with the first line of it, and Settings → System → What's new shows all of it. A known issue goes
   there too: lines starting "Known issue" are shown in Settings but never used as the highlight. Without a highlight,
   the card lists the first new and fixed changes. Tests, refactors, docs, CI and other behind-the-scenes changes are
   only counted, never listed.

## Trying a release without releasing (dry run)

Run the **Release** workflow by hand on any branch, for example to try a change to `release.yml`:

```bash
gh workflow run ci.yml --ref <branch>       # the dry run checks CI on the commit too
gh workflow run release.yml --ref <branch>  # optional: -f version=0.72.0 -f compare_with=v0.72.0
```

It runs every job a tag would: the CI check, the four platform builds with their smoke tests, the desktop installers
with theirs, and the publish job's assembly (merged update feeds, `checksums.txt`, the unversioned installers, the
install scripts). Then it compares that set with the latest published release, or the one given in `compare_with`:
the same asset names once versions are lined up, each package within 80–125% of its size, and the files each package
contains. It writes the comparison to the run's summary. The version stamped on the packages is the one in
`Directory.Build.props` unless you give another.

It publishes nothing. Creating the GitHub Release, the vanity URL deploy and the website notification run only for a
pushed `v*` tag. The packages are left as the run's artifacts for 7 days.

## A release needs CI to have passed on its commit

The Release workflow builds and publishes nothing until `ci.yml` has passed on the exact commit the tag points at
(`scripts/release-ci-gate.sh`, tested by `scripts/test-release-ci-gate.sh`):

- It counts the commit's `push` run (the one main gets) and any `workflow_dispatch` run on it. Pull request runs don't
  count: they test the PR merged into main, not this commit. Once `ci.yml` has a **CI passed** job, that job must have
  passed too.
- While a run is still going, it waits, for up to an hour.
- A red or cancelled run blocks the release at once. So does a commit `ci.yml` never ran on, after 5 minutes for the
  run to show up.
- `ci.yml` skips pushes that only change `**/*.md` or `.weave/**`. A tag on such a commit is judged by the nearest
  commit before it that CI ran on, as long as everything since then is in those paths.

When it blocks a release, the job's log says why and gives the exact commands, with the run ids filled in. Nothing
has been built or published at that point. Don't move, delete or re-push the tag, and don't publish anything by hand.

- **CI failed.** Read why: `gh run view <ci run id> --log-failed`.
  - A check failed because of the code: this version can't ship. Fix it on main in a PR, then bump the version and tag
    that. Leave the old tag as it is: it was never released.
  - A known flaky test or a broken runner (lost runner, network, timeout): `gh run rerun <ci run id> --failed`, and
    wait for it to finish green (`gh run watch <ci run id> --exit-status`).
- **CI was cancelled** (usually a newer push to main) **or never ran:** run it on the tag,
  `gh workflow run ci.yml --ref vX.Y.Z`, and wait for it to finish green
  (`gh run list --workflow ci.yml --commit <sha>`, then `gh run watch <id> --exit-status`).
- **CI was still running after an hour:** wait for it with `gh run watch <id> --exit-status`. If it's stuck, cancel it
  and run CI on the tag as above.

Once CI is green on the commit, let the release carry on: `gh run rerun <release run id> --failed`. That re-runs the
check, then builds and publishes as usual.

## What the workflow publishes

For each tag, the workflow builds and publishes self-contained artifacts for:

- `linux-x64`
- `osx-arm64`
- `win-x64`
- `win-arm64`

It also builds the desktop app (`desktop/`, through `.github/workflows/desktop-packages.yml`) with the same AOT
server inside, and smoke-tests each packaged app before anything is published.

It then publishes a GitHub Release containing:

- 4 platform archives (`.tar.gz` on Unix, `.zip` on Windows)
- desktop installers: `Fleet-<version>-linux-x86_64.AppImage`, `Fleet-<version>-linux-amd64.deb`,
  `Fleet-<version>-mac-arm64.dmg` (and `.zip`), `Fleet-<version>-win-x64-setup.exe`, `Fleet-<version>-win-arm64-setup.exe`
- a copy of each installer without the version, with its own `.sha256`, so
  these links always get the newest one (`scripts/add-unversioned-installers.sh`; the update feeds keep pointing at the
  versioned files):
  - `https://github.com/pgermishuys/weave-fleet/releases/latest/download/Fleet-mac-arm64.dmg`
  - `https://github.com/pgermishuys/weave-fleet/releases/latest/download/Fleet-win-x64-setup.exe`
  - `https://github.com/pgermishuys/weave-fleet/releases/latest/download/Fleet-win-arm64-setup.exe`
  - `https://github.com/pgermishuys/weave-fleet/releases/latest/download/Fleet-linux-x86_64.AppImage`
  - `https://github.com/pgermishuys/weave-fleet/releases/latest/download/Fleet-linux-amd64.deb`
- the desktop app's update feed: `latest.yml` (Windows, x64 and arm64 merged), `latest-mac.yml`, `latest-linux.yml`,
  and `.blockmap` files for smaller downloads
- per-asset `.sha256` files
- merged `checksums.txt`
- `install.sh`
- `install.ps1`

The desktop packages take their version from the tag; `desktop/package.json`'s version only matters in development.
To build the installers without releasing, run the **Desktop packages** workflow by hand: they come out as workflow
artifacts. To try the whole release, run a [dry run](#trying-a-release-without-releasing-dry-run).

### Desktop installers are unsigned for now

- **macOS:** the app is ad-hoc signed, not notarised. The first time, macOS says it can't verify the developer: open
  System Settings → Privacy & Security and choose **Open Anyway**. It can't install updates itself: it says when a new
  version is out and links to it.
- **Windows:** SmartScreen shows "Windows protected your PC": choose **More info → Run anyway**. Updates download in
  the background and install when Fleet restarts.
- **Linux:** nothing to do. The AppImage needs FUSE 2 (`libfuse2`) and updates itself like Windows; the `.deb`
  installs to `/opt/Fleet` and says when a new version is out.

The app reads its update feed from this repository's releases.

Signing isn't wired yet (Phase 3 of `.weave/plans/desktop-app.md`). It will need a Developer ID certificate and App
Store Connect API key for macOS, and Azure Trusted Signing for Windows, as t3code's `docs/operations/release.md`
describes.

## Deferred work status

The following items are intentionally deferred and are **not blockers** for the current public GitHub Releases release path:

- vanity URL hosting
- trimming
- code signing / notarization
- Homebrew / apt / winget publishing
- Docker image distribution
- compiled `WeaveFleet.Cli` launcher migration

## Code signing / notarization status

Code signing and notarization are **not wired yet**. The current release workflow publishes **unsigned** macOS and Windows artifacts.

This task is currently blocked on missing external prerequisites and one unresolved packaging decision:

- **macOS packaging decision is still open**: the workflow currently ships macOS builds as `.tar.gz`, but Apple notarization expects a notarizable container/workflow (`.zip`, `.pkg`, `.dmg`, or app bundle). We need an explicit decision on the signed/notarized macOS distribution format before changing the workflow.
- **Apple signing credentials are not available in this repo/environment**: we need a Developer ID Application certificate, the Apple Team ID, and a CI-safe notarization auth method (`notarytool` keychain profile on a trusted runner, or App Store Connect API key / Apple ID + app-specific password secrets).
- **macOS signing scope is unspecified**: for the self-contained .NET publish output, we need to decide exactly which binaries are signed and how verification is enforced (`codesign`, notarization submission, and post-submit validation).
- **Windows signing credentials are not available in this repo/environment**: we need the chosen signing mechanism (PFX certificate vs remote signing service), the required secrets/auth, and a timestamp authority URL.
- **Windows signing scope is unspecified**: we need an explicit rule for which shipped files are signed (at minimum the native executable, and possibly additional `.dll`/native payload files if required by the chosen policy).

Until those prerequisites exist, the safe release posture is to keep shipping checksummed unsigned artifacts and document the limitation explicitly rather than guess at incomplete signing automation.

## Homebrew / apt / winget status

Homebrew tap publishing, apt repository publishing, and winget manifest submission are **not wired yet**.

This task is currently blocked on missing external ownership, packaging, and trust prerequisites:

- **Owning destinations are unspecified**: this repo does not identify the Homebrew tap repository, apt repository host/distribution/component, or the winget manifest ownership/submission path.
- **Package identity is unspecified**: we still need the final formula/package identifiers, display names, channels, and any repository layout conventions those external registries require.
- **Registry credentials are not available in this repo/environment**: publishing would require external repo access and, for apt, repository-signing material such as the GPG key and release metadata process.
- **Artifact contract is not fully defined for those registries**: we need an explicit mapping from the existing GitHub Release assets to Homebrew bottles/formula URLs, apt package metadata, and winget installer/manifests.
- **Verification expectations are unspecified**: we need the exact success checks for each registry (for example `brew install`, `apt install`, and `winget install` against the real publishing targets).

Until those prerequisites exist, the safe next step is documentation only. When the external contracts are available, registry publish jobs should consume the already-produced GitHub Release assets rather than guess at new packaging ownership from this repository alone.

## Docker image distribution status

Docker image distribution is **not wired yet**.

This task is currently blocked on missing image-definition, publishing, and runtime-contract prerequisites:

- **The container artifact itself is undefined**: this repo does not currently contain a `Dockerfile`, compose file, or an agreed multi-stage build contract for the .NET API plus built Vue assets.
- **The target registry is unspecified**: we do not have an approved publishing destination such as GHCR, Docker Hub, or another registry, nor the final image name, namespace/owner, or tagging rules.
- **Registry credentials are not available in this repo/environment**: any publish workflow would require explicit ownership plus CI secrets or federated auth configuration.
- **Runtime expectations are unspecified**: we still need the supported container mode (local dev only vs production-supported), exposed port contract, persistent volume/data-path contract, and environment-variable contract.
- **Image verification expectations are unspecified**: we need the exact acceptance checks for image build, image pull, container startup, health verification, and multi-arch support before adding release automation.

Until those prerequisites exist, the safe next step is documentation only. When Docker distribution is explicitly defined, it should build from the existing release packaging conventions rather than guessing registry ownership, tags, credentials, or production support posture.

## Compiled WeaveFleet.Cli launcher migration status

Migrating the launcher from the current shell/cmd wrappers to a compiled `WeaveFleet.Cli` is **not safely executable yet** from the information in this repo alone.

This task is currently blocked on missing product and packaging prerequisites:

- **The launcher contract is underspecified**: the plan provides only the title. It does not define the exact compiled CLI command surface, subcommand names, help text, exit-code behavior, or whether full parity with today's `version`, `update`, `uninstall`, default start, `--port`, and `--profile` behavior is required.
- **The runtime model is unspecified**: `src/WeaveFleet.Cli` is still a placeholder and the repo does not define whether the compiled CLI should host the server in-process, invoke `WeaveFleet.Api` as a child process, or remain a thin packaged wrapper around published artifacts.
- **The package layout contract would change**: current packaging and release automation explicitly copy `scripts/launcher.sh` / `scripts/launcher.cmd` into `bin/` and smoke-test those launchers. Replacing them requires an explicit decision on artifact names, per-platform entrypoints, update/uninstall mechanics, and whether scripts remain as compatibility shims.
- **Repo/dev versus installed-package behavior is not defined for the compiled CLI**: the existing launchers intentionally support both published-package layout and repo release-build layout. A compiled replacement needs an explicit content-root and binary-discovery contract for both modes.
- **Verification requirements are missing**: current acceptance and workflow smoke tests cover script launchers only. A compiled migration needs explicit replacement verification for Unix and Windows launch, version reporting, health checks, update flow, uninstall flow, and release packaging.

Safe next step before implementation:

1. Define the `WeaveFleet.Cli` command contract and required parity with existing launcher behavior.
2. Choose the runtime architecture (`WeaveFleet.Api` child process vs in-process hosting).
3. Define the packaged entrypoint contract for Unix and Windows, including whether scripts remain as shims.
4. Update release/package/test acceptance criteria to match the chosen launcher architecture.

## Verification checklist

After the tag is pushed, verify all of the following:

- [ ] `Directory.Build.props` contains the intended version.
- [ ] The pushed tag matches that version (`vX.Y.Z` vs `X.Y.Z`).
- [ ] GitHub Actions `Release` workflow succeeded, and its **CI passed on this commit** job links a green CI run.
- [ ] All build matrix jobs succeeded.
- [ ] The GitHub Release exists for the tag.
- [ ] The release includes all 4 platform archives.
- [ ] The release includes all per-asset checksum files and `checksums.txt`.
- [ ] The release includes `install.sh` and `install.ps1`.
- [ ] Packaged smoke tests passed in the workflow.
- [ ] A downloaded package reports the expected version with `fleet version`.
- [ ] A downloaded package starts and serves `/healthz` successfully.

## Recommended post-release spot checks

### Unix/macOS

```bash
curl -fsSL https://github.com/pgermishuys/weave-fleet/releases/latest/download/install.sh | sh
export PATH="$HOME/.weave/fleet/bin:$PATH"
fleet version
fleet
curl -fsS http://127.0.0.1:6262/healthz
```

### Windows PowerShell

```powershell
irm https://github.com/pgermishuys/weave-fleet/releases/latest/download/install.ps1 | iex
fleet version
fleet
Invoke-WebRequest -UseBasicParsing http://127.0.0.1:6262/healthz
```

## Vanity URL status

Vanity URL hosting is deferred and is **not required** for the current release process.

Do not treat `get.tryweave.io/fleet.sh` as a release blocker. Until a separate hosting owner/contract is chosen, GitHub Releases remains the only documented install path.

## Failure recovery

- If the release was blocked because CI didn't pass on its commit, see
  [A release needs CI to have passed on its commit](#a-release-needs-ci-to-have-passed-on-its-commit).
- If the workflow fails before publishing the GitHub Release, fix the issue and re-run or push a corrected tag only if necessary.
- If the tag version does not match `Directory.Build.props`, delete the incorrect tag locally/remotely, fix the version, and create the correct tag.
- Do not hand-edit generated release assets; rebuild them through the workflow.
