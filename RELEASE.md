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

5. **Wait for the `Release` GitHub Actions workflow** to finish.
6. **Verify the GitHub Release** and release-hosted installer endpoints.

## What the workflow publishes

For each tag, the workflow builds and publishes self-contained artifacts for:

- `linux-x64`
- `linux-arm64`
- `osx-x64`
- `osx-arm64`
- `win-x64`

It then publishes a GitHub Release containing:

- 5 platform archives (`.tar.gz` on Unix, `.zip` on Windows)
- per-asset `.sha256` files
- merged `checksums.txt`
- `install.sh`
- `install.ps1`

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
- [ ] GitHub Actions `Release` workflow succeeded.
- [ ] All 5 build matrix jobs succeeded.
- [ ] The GitHub Release exists for the tag.
- [ ] The release includes all 5 platform archives.
- [ ] The release includes all per-asset checksum files and `checksums.txt`.
- [ ] The release includes `install.sh` and `install.ps1`.
- [ ] Packaged smoke tests passed in the workflow.
- [ ] A downloaded package reports the expected version with `weave-fleet version`.
- [ ] A downloaded package starts and serves `/healthz` successfully.

## Recommended post-release spot checks

### Unix/macOS

```bash
curl -fsSL https://github.com/pgermishuys/weave-fleet/releases/latest/download/install.sh | sh
export PATH="$HOME/.weave/weave-fleet/bin:$PATH"
weave-fleet version
weave-fleet
curl -fsS http://127.0.0.1:5000/healthz
```

### Windows PowerShell

```powershell
irm https://github.com/pgermishuys/weave-fleet/releases/latest/download/install.ps1 | iex
weave-fleet version
weave-fleet
Invoke-WebRequest -UseBasicParsing http://127.0.0.1:5000/healthz
```

## Vanity URL status

Vanity URL hosting is deferred and is **not required** for the current release process.

Do not treat `get.tryweave.io/fleet.sh` as a release blocker. Until a separate hosting owner/contract is chosen, GitHub Releases remains the only documented install path.

## Failure recovery

- If the workflow fails before publishing the GitHub Release, fix the issue and re-run or push a corrected tag only if necessary.
- If the tag version does not match `Directory.Build.props`, delete the incorrect tag locally/remotely, fix the version, and create the correct tag.
- Do not hand-edit generated release assets; rebuild them through the workflow.
