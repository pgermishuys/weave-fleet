# Cloud Bootstrap + Deploy Automation

## TL;DR
> **Summary**: Automate the full cloud host lifecycle — from fresh Lightsail instance to running, validated Fleet service — via a `bootstrap.sh` infra orchestrator, a separate first-deploy step, and GitHub Actions workflows. Only Lightsail instance provisioning remains manual.
> **Estimated Effort**: Large

## Context
### Original Request
Fully automate cloud host bootstrap and deployment for Weave Fleet so that the user only needs to:
1. Provision a Lightsail instance (manual)
2. Provide SSH key, host/IP, domain, and secrets
3. Everything else (runtime install, service config, env file, deploy, validation) happens automatically via scripts or GitHub Actions.

### Key Findings

**Current deploy/ inventory:**
- `setup.sh` — installs .NET 10, Caddy, opencode, creates `fleet` user and directory structure. Ends with manual "next steps" instructions.
- `deploy.sh` — builds .NET + SPA locally, rsyncs to host via staging dir, restarts service, runs health check. Requires `FLEET_HOST`, `SSH_OPTS`, optional `FLEET_DOMAIN`.
- `Caddyfile` — hardcoded domain `fleet.tryweave.io`. Must be manually edited and scp'd.
- `fleet.service` — systemd unit. Must be manually scp'd and installed. Uses `EnvironmentFile=-/opt/fleet/data/fleet.env` (the `-` prefix makes it optional — systemd won't fail if missing).
- `README.md` — 8-task manual guide (provision → setup → Caddy → OIDC → deploy → supervision → confinement → backup).

**Gaps between current state and full automation:**
1. **No orchestrator**: `setup.sh` runs on the host but nothing uploads it, runs it, and then continues with service/Caddy install. The user must manually scp + ssh + scp Caddyfile + scp fleet.service + systemctl commands.
2. **No config templating**: Caddyfile has hardcoded domain. fleet.env must be manually created on server.
3. **No fleet.env provisioning**: The env file (auth secrets, cloud mode, data protection path) must be hand-written on the server.
4. **No post-bootstrap validation**: Nothing verifies the server infrastructure is correctly configured after setup.
5. **No GitHub Actions**: CI exists (`ci.yml` with `test`, `client`, `e2e` jobs) but there are no deploy or bootstrap workflows.
6. **No rollback automation**: README describes manual rollback but deploy.sh doesn't preserve prior releases.
7. **deploy.sh uses `--no-restore`**: Will fail on clean CI runners without a prior `dotnet restore`.
8. **ASPNETCORE_ENVIRONMENT=Production** in fleet.service, but no `appsettings.Production.json` exists. All cloud config comes from fleet.env env vars, which is correct and intentional (config-by-environment-file).

**App configuration model (from FleetOptions.cs):**
- `Fleet__Auth__Enabled=true` + Authority/ClientId/ClientSecret/AllowedOrigins → enables OIDC
- `Fleet__Cloud__Enabled=true` + WorkspaceRoot → enables managed workspaces
- `Fleet__DataProtection__KeyPath` → persistent key ring
- `Fleet__DatabasePath` → SQLite path
- Health endpoints: `/healthz`, `/readyz` (public, no auth required)
- Client config: `GET /api/config/client` → returns `{ cloudMode, authEnabled, availableHarnesses }` — **protected under auth group** in `EndpointExtensions.cs`. Unauthenticated requests to `/api/*` return **401** (not redirect), per `OnRedirectToLogin` handler in Program.cs.
- Non-API unauthenticated requests → **302 redirect** to `/auth/login` → OIDC provider.

### Bootstrap / Deploy Boundary (Design Decision)

**Model: bootstrap = infra-only; deploy = app delivery. Always separate.**

Rationale: On a fresh host, `/opt/fleet/app/` does not exist. The fleet.service unit's `ExecStart` points to `/opt/fleet/app/WeaveFleet.Api.dll`. If bootstrap starts the fleet service before deploy, systemd will immediately fail with a missing binary. The `Restart=on-failure` policy would retry endlessly until the 5-second backoff exhausts systemd's burst limit, leaving the service in a failed state.

Therefore:
- **`bootstrap.sh`** installs runtimes, templates configs, provisions fleet.env, installs service/Caddy units, and `systemctl enable` (but does NOT `start`) the fleet service. It starts Caddy (which can run independently and begin TLS provisioning while waiting for the backend).
- **`deploy.sh`** delivers app artifacts to `/opt/fleet/app/`, then starts/restarts the fleet service. It is always a separate invocation.
- **The GitHub Actions `bootstrap.yml` workflow** calls `bootstrap.sh` then `deploy.sh` sequentially in the same job, giving a seamless end-to-end experience.
- **For manual usage**, the operator runs `bootstrap.sh` then `deploy.sh` as two commands.

This eliminates the chicken-and-egg problem cleanly: bootstrap never starts fleet, deploy always does.

## Objectives
### Core Objective
Create a fully automated bootstrap-to-validated-deploy pipeline where the only manual step is provisioning the Lightsail instance and providing credentials/secrets.

### Deliverables
- [ ] `deploy/bootstrap.sh` — local orchestrator: uploads/runs setup.sh, templates+installs Caddyfile and fleet.service, provisions fleet.env securely, enables (not starts) fleet, starts Caddy, validates infrastructure
- [ ] `deploy/Caddyfile.template` — parameterized Caddyfile with `${FLEET_DOMAIN}` placeholder
- [ ] Updated `deploy/deploy.sh` — remove `--no-restore`, add timestamped release management with symlink-current pointer, improve health check with journal dump on failure, add `SKIP_BUILD` support
- [ ] `.github/actions/setup-ssh/action.yml` — reusable composite action for pinned SSH host key verification and key setup
- [ ] `.github/workflows/deploy.yml` — deploy workflow gated on CI success
- [ ] `.github/workflows/bootstrap.yml` — manual-dispatch workflow that runs bootstrap then first deploy
- [ ] Updated `deploy/README.md` — rewritten to reflect automated flow

### Definition of Done
- [ ] `bootstrap.sh` succeeds end-to-end on a fresh Ubuntu 22.04 Lightsail instance, leaving fleet service enabled-but-not-started, Caddy running, fleet.env with correct permissions
- [ ] `deploy.sh` (run after bootstrap) delivers artifacts, starts fleet, and health check passes
- [ ] After bootstrap + deploy: `curl -sf https://<domain>/healthz` returns 200
- [ ] After bootstrap + deploy: `curl -sf https://<domain>/api/config/client` returns 401 (confirms auth is enforced)
- [ ] After bootstrap + deploy: SSH validation confirms fleet.env contains `Fleet__Auth__Enabled=true` and `Fleet__Cloud__Enabled=true`
- [ ] GitHub Actions bootstrap workflow (manual dispatch) succeeds with secrets configured
- [ ] GitHub Actions deploy workflow succeeds on push to main, gated behind CI
- [ ] Second deploy creates timestamped release; previous release preserved at `/opt/fleet/releases/<timestamp>/`
- [ ] Rollback: `deploy.sh` supports `ROLLBACK=1` to repoint symlink to previous release
- [ ] `shellcheck deploy/bootstrap.sh deploy/deploy.sh deploy/setup.sh` passes with no errors
- [ ] SSH host key verification uses pinned fingerprint, not TOFU/accept-new

### Guardrails (Must NOT)
- Must NOT require any interactive input (all scripts fully noninteractive)
- Must NOT store secrets in version control (SSH keys, auth secrets only in GitHub Secrets or env vars)
- Must NOT change the app's C# code or configuration model — this is purely deploy/infra automation
- Must NOT break the existing `setup.sh` standalone usage (bootstrap.sh wraps it, doesn't replace it)
- Must NOT use `set -x`, `echo`, or shell interpolation that could leak secrets into logs
- Must NOT use SSH `StrictHostKeyChecking=accept-new` or `StrictHostKeyChecking=no` in production workflows

## TODOs

### Phase 1: Bootstrap Orchestrator (core value — do this first)

- [x] 1. Create `deploy/Caddyfile.template`
  **What**: Copy current Caddyfile, replace the hardcoded domain `fleet.tryweave.io` with `${FLEET_DOMAIN}`. Keep all security headers and logging config. The template uses `envsubst`-compatible `${VAR}` syntax. Keep the original `deploy/Caddyfile` as-is (it serves as documentation and can be used for manual setup).
  **Files**: `deploy/Caddyfile.template`
  **Acceptance**: `FLEET_DOMAIN=example.com envsubst '${FLEET_DOMAIN}' < deploy/Caddyfile.template` produces valid Caddyfile with `example.com` as the site address; no other `$` references are substituted

- [x] 2. Create `deploy/bootstrap.sh` orchestrator
  **What**: Single script that automates the full infrastructure bootstrap sequence. Called from the operator's local machine (or CI). Requires env vars: `FLEET_HOST`, `FLEET_DOMAIN`, `SSH_OPTS` (optional), plus `Fleet__Auth__Authority`, `Fleet__Auth__ClientId`, `Fleet__Auth__ClientSecret` for secrets. Steps:

    1. **Validate** required env vars are set (fail fast with clear error messages): `FLEET_HOST`, `FLEET_DOMAIN`, `Fleet__Auth__Authority`, `Fleet__Auth__ClientId`, `Fleet__Auth__ClientSecret`
    2. **Upload** `deploy/setup.sh` to remote host via `scp $SSH_OPTS`
    3. **Execute** `setup.sh` on remote host via `ssh $SSH_OPTS` (as sudo)
    4. **Render Caddyfile** from template: run `envsubst '${FLEET_DOMAIN}'` locally to produce rendered Caddyfile, then upload via scp to a temp location, then `sudo install -m 644 -o root -g root` to `/etc/caddy/Caddyfile`
    5. **Upload fleet.service**: scp to temp location, then `sudo install -m 644 -o root -g root` to `/etc/systemd/system/fleet.service`
    6. **Provision fleet.env securely** (see TODO #3 for detailed secret handling)
    7. **Create release directory structure**: `sudo mkdir -p /opt/fleet/releases && sudo chown fleet:fleet /opt/fleet/releases`
    8. **Enable services**: `sudo systemctl daemon-reload && sudo systemctl enable fleet` (enable only — do NOT start fleet, app binary doesn't exist yet)
    9. **Start Caddy**: `sudo systemctl enable --now caddy` (Caddy can start independently and begin TLS provisioning)
    10. **Run infrastructure validation** (see TODO #4)
    11. Print: "Bootstrap complete. Run deploy.sh to deliver the application."

  **Files**: `deploy/bootstrap.sh`
  **Acceptance**: Script runs end-to-end on a fresh Ubuntu 22.04 instance; fleet service is `enabled` but `inactive`; Caddy is `active`; fleet.env exists with correct permissions

- [x] 3. Implement secure fleet.env provisioning in `bootstrap.sh`
  **What**: Generate and transfer fleet.env without leaking secrets through shell expansion, debug output, or process listing. Procedure:

    1. **Disable tracing**: Ensure `set +x` is active before any secret handling. Never use `echo` with secret values.
    2. **Build env file locally** into a temp file using `cat <<'ENVEOF' > "$tmpfile"` (single-quoted heredoc delimiter prevents shell expansion). Then append variable values using `printf '%s=%s\n' "KEY" "$VALUE" >> "$tmpfile"` for each secret — `printf` with explicit format string avoids injection.
    3. **Set local permissions**: `chmod 600 "$tmpfile"` immediately after creation.
    4. **Transfer via scp**: `scp $SSH_OPTS "$tmpfile" "$FLEET_HOST:~/fleet.env.tmp"`
    5. **Atomic install on remote**: `ssh $SSH_OPTS "$FLEET_HOST" "sudo install -m 600 -o fleet -g fleet ~/fleet.env.tmp /opt/fleet/data/fleet.env && rm -f ~/fleet.env.tmp"`
    6. **Cleanup local temp**: `rm -f "$tmpfile"` in a trap handler to ensure cleanup even on script failure.

  The fleet.env must contain:
  ```
  Fleet__Auth__Enabled=true
  Fleet__Auth__Authority=<from env>
  Fleet__Auth__ClientId=<from env>
  Fleet__Auth__ClientSecret=<from env>
  Fleet__Auth__AllowedOrigins__0=https://<FLEET_DOMAIN>
  Fleet__Cloud__Enabled=true
  Fleet__Cloud__WorkspaceRoot=/data/workspaces
  Fleet__DataProtection__KeyPath=/opt/fleet/data/keys
  Fleet__DatabasePath=/opt/fleet/data/weave-fleet.db
  ```

  Note: `ASPNETCORE_ENVIRONMENT` and `ASPNETCORE_URLS` are already set in fleet.service via `Environment=` directives and should NOT be duplicated in fleet.env (systemd `Environment=` takes precedence over `EnvironmentFile=` for the same key — keeping them in the unit file makes them visible and auditable).

  **Files**: `deploy/bootstrap.sh` (secure env provisioning section)
  **Acceptance**: `fleet.env` is created with mode 600, owner fleet:fleet; no secrets appear in script output or process listing; temp files are cleaned up on success and failure (trap handler)

- [x] 4. Add infrastructure validation to `bootstrap.sh`
  **What**: After bootstrap (before deploy), validate that the infrastructure is correctly configured. This validation does NOT require the app to be running — it validates the infra layer only.

  Validation checks (all via SSH to the remote host):
    1. **Service unit installed**: `systemctl is-enabled fleet` → `enabled`
    2. **Caddy running**: `systemctl is-active caddy` → `active`
    3. **fleet.env exists with correct permissions**: `stat -c '%a %U:%G' /opt/fleet/data/fleet.env` → `600 fleet:fleet`
    4. **fleet.env contains required keys**: `grep -qc 'Fleet__Auth__Enabled=true' /opt/fleet/data/fleet.env && grep -qc 'Fleet__Cloud__Enabled=true' /opt/fleet/data/fleet.env` (verifies presence of keys without reading secret values)
    5. **Directory structure**: `/opt/fleet/data/keys/` exists with mode 700, `/data/workspaces/` exists, `/opt/fleet/releases/` exists
    6. **Runtime check**: `dotnet --list-runtimes | grep -q 'Microsoft.AspNetCore.App 10'`
    7. **Caddy TLS readiness** (optional, may not be ready yet): attempt `curl -sf --max-time 5 https://$FLEET_DOMAIN/` from the runner — if it connects (even to a 502), TLS is provisioned. Log result but don't fail on this (Caddy may still be provisioning the cert).

  Print summary: "Infrastructure validated. Fleet service: enabled (not started). Caddy: active. fleet.env: secure. Ready for first deploy."

  **Files**: `deploy/bootstrap.sh` (validation section at the end)
  **Acceptance**: Bootstrap fails with clear error and nonzero exit if any check in steps 1-6 fails; step 7 is informational only

### Phase 2: Deploy Script Improvements

- [x] 5. Remove `--no-restore` from `deploy/deploy.sh`
  **What**: Remove the `--no-restore` flag from the `dotnet publish` command. On CI runners (clean checkout, no prior build), this flag causes publish to fail because NuGet packages haven't been restored. Without the flag, `dotnet publish` implicitly restores. This is the only change in this task.
  **Files**: `deploy/deploy.sh`
  **Acceptance**: `deploy.sh` works on a clean checkout (CI runner) without prior `dotnet restore`

- [x] 6. Replace destructive rollback with timestamped release management in `deploy/deploy.sh`
  **What**: Replace the current direct-write to `/opt/fleet/app/` with a timestamped release directory and a `current` symlink. This ensures the last-known-good release is never destroyed, even on a failed deploy.

  **Directory layout**:
  ```
  /opt/fleet/releases/
    20260411-143022/          ← timestamped release dirs
    20260411-150515/
  /opt/fleet/app -> /opt/fleet/releases/20260411-150515/   ← symlink
  ```

  **Deploy sequence** (replaces current steps 4-6):
    1. Generate release tag: `RELEASE_TAG=$(date -u +%Y%m%d-%H%M%S)`
    2. Create release dir: `ssh_remote "sudo mkdir -p /opt/fleet/releases/$RELEASE_TAG && sudo chown fleet:fleet /opt/fleet/releases/$RELEASE_TAG"`
    3. Rsync artifacts into staging dir (unchanged), then promote into `/opt/fleet/releases/$RELEASE_TAG/`
    4. Stop fleet service: `ssh_remote "sudo systemctl stop fleet || true"`
    5. Atomic symlink swap: `ssh_remote "sudo ln -sfn /opt/fleet/releases/$RELEASE_TAG /opt/fleet/app"` (`ln -sfn` is atomic on Linux for symlink replacement)
    6. Start fleet service
    7. **On health check failure**: auto-rollback to previous release by relinking to the prior directory and restarting. Log the failure clearly.
    8. **Prune old releases**: keep last 3 releases, remove older ones. `ssh_remote "ls -dt /opt/fleet/releases/*/ | tail -n +4 | xargs sudo rm -rf"`

  **Migration from current layout**: If `/opt/fleet/app` is currently a real directory (not a symlink), the first deploy under the new scheme should:
    1. Move existing `/opt/fleet/app` to `/opt/fleet/releases/pre-migration/`
    2. Create the symlink

  **Rollback support**: `ROLLBACK=1 bash deploy/deploy.sh` lists available releases and repoints the symlink to the previous one, then restarts.

  **Files**: `deploy/deploy.sh`
  **Acceptance**: After deploy, `/opt/fleet/app` is a symlink to a timestamped release dir; previous release dir still exists; failed deploy auto-rollbacks to previous release

- [x] 7. Improve `deploy/deploy.sh` health check and add `SKIP_BUILD`
  **What**:
    1. Increase health check timeout from 30s to 60s
    2. If health check fails, automatically dump last 50 lines of journal (`ssh_remote "sudo journalctl -u fleet -n 50 --no-pager"`) before triggering rollback/exit — aids debugging
    3. Add `SKIP_BUILD=1` env var support: when set, skip the `dotnet publish` and `npm ci/build` steps entirely, assuming `$REPO_ROOT/publish/` already contains built artifacts. This enables the bootstrap workflow and CI to build once and deploy without rebuilding.
  **Files**: `deploy/deploy.sh`
  **Acceptance**: Failed health check outputs journal logs; `SKIP_BUILD=1` skips build steps; `SKIP_BUILD=1` fails with clear error if `publish/` directory is empty or missing

### Phase 3: GitHub Actions Workflows

- [x] 8. Create `.github/actions/setup-ssh/action.yml` — reusable composite action with pinned host key
  **What**: Both deploy and bootstrap workflows need SSH key setup with proper host verification. Extract to a reusable composite action.

  **Inputs**:
    - `ssh-key` (required) — private SSH key content (from GitHub Secret)
    - `ssh-host` (required) — hostname or IP to connect to (extracted from FLEET_HOST)
    - `ssh-host-public-key` (required) — the server's public key or fingerprint for verification (from GitHub Secret `FLEET_SSH_HOST_PUBLIC_KEY`)

  **Steps**:
    1. Create `~/.ssh/` with mode 700
    2. Write SSH private key to `~/.ssh/fleet-deploy-key` with mode 600 (use `$GITHUB_WORKSPACE` tmp approach or direct write — never echo the key)
    3. Write the pinned host public key to `~/.ssh/known_hosts` using the provided `ssh-host-public-key` value. Format: `<host> <key-type> <key-data>`. This is pre-pinned — no `ssh-keyscan` at runtime, no TOFU.
    4. Set `StrictHostKeyChecking=yes` (the default, but be explicit)

  **Outputs**:
    - `ssh-opts`: `-i ~/.ssh/fleet-deploy-key -o StrictHostKeyChecking=yes`

  **Post step** (cleanup):
    - `rm -f ~/.ssh/fleet-deploy-key`
    - Remove the pinned entry from known_hosts (leave runner clean)

  **Required GitHub Secret**:
    - `FLEET_SSH_HOST_PUBLIC_KEY` — the server's SSH public host key, obtained once during initial provisioning via `ssh-keyscan <host>` and stored as a secret. Must be updated if the server is reprovisioned.

  **Files**: `.github/actions/setup-ssh/action.yml`
  **Acceptance**: SSH connection succeeds with pinned key; connection fails if server key changes (MITM protection); private key is cleaned up after workflow completes

- [x] 9. Create `.github/workflows/deploy.yml`
  **What**: GitHub Actions workflow for deploying to the cloud host.

  **Triggers**:
    - `workflow_run`: triggered after the `CI` workflow completes on `main` branch, only if CI succeeded. This is the gating mechanism — deploy never runs unless all CI jobs (test, client, e2e) pass.
    - `workflow_dispatch`: manual trigger (bypasses CI gate — for emergency deploys). Requires explicit confirmation input.

  **Gating model**:
    - The `workflow_run` trigger with `types: [completed]` and a `if: github.event.workflow_run.conclusion == 'success'` condition on the job ensures deploy only runs after CI passes.
    - For `workflow_dispatch`, the workflow runs immediately (operator accepts responsibility for skipping CI).
    - The workflow uses a GitHub Environment (`production`) with optional protection rules (manual approval, etc.) for additional gating.

  **Required GitHub Secrets**:
    - `FLEET_SSH_KEY` — private SSH key for the deploy user
    - `FLEET_SSH_HOST_PUBLIC_KEY` — pinned server host public key
    - `FLEET_HOST` — SSH target (e.g., `ubuntu@1.2.3.4`)
    - `FLEET_DOMAIN` — domain name (e.g., `fleet.tryweave.io`)

  **Job steps**:
    1. Checkout code (for `workflow_dispatch`: current ref; for `workflow_run`: the triggering commit SHA via `github.event.workflow_run.head_sha`)
    2. Setup .NET 10 SDK
    3. Setup Node.js 22
    4. Use `setup-ssh` composite action with pinned host key
    5. Run `deploy/deploy.sh` with env vars from secrets: `FLEET_HOST`, `FLEET_DOMAIN`, `SSH_OPTS` (from action output)
    6. SSH key cleanup (handled by composite action post step)

  **Files**: `.github/workflows/deploy.yml`
  **Acceptance**: Push to main → CI passes → deploy triggers automatically; manual dispatch works; SSH key is never logged; deploy fails if server host key doesn't match pinned key

- [x] 10. Create `.github/workflows/bootstrap.yml`
  **What**: GitHub Actions workflow for one-time bootstrap of a new host. Manual dispatch only (`workflow_dispatch`).

  **Inputs** (workflow_dispatch):
    - `host` (required) — SSH target (e.g., `ubuntu@1.2.3.4`)
    - `domain` (required) — domain name
    - `host_public_key` (required) — server's SSH host public key (since this is a new server, the operator must provide it at dispatch time; it should also be saved to `FLEET_SSH_HOST_PUBLIC_KEY` secret for future deploys)

  **Required GitHub Secrets**:
    - `FLEET_SSH_KEY`
    - `FLEET_AUTH_AUTHORITY`
    - `FLEET_AUTH_CLIENT_ID`
    - `FLEET_AUTH_CLIENT_SECRET`

  **Job steps**:
    1. Checkout code
    2. Setup .NET 10 SDK + Node.js 22
    3. Use `setup-ssh` composite action with host public key from dispatch input
    4. Run `deploy/bootstrap.sh` with env vars from secrets + inputs
    5. Run `deploy/deploy.sh` (first deploy — delivers app + starts fleet service)
    6. **Post-deploy validation**: SSH to host and verify:
       - `systemctl is-active fleet` → `active`
       - `curl -sf http://127.0.0.1:8080/healthz` (loopback, bypasses TLS) → `Healthy`
       - `curl -so /dev/null -w '%{http_code}' http://127.0.0.1:8080/api/config/client` → `401` (proves auth is enforced; the 401 for `/api/*` is hardcoded in Program.cs `OnRedirectToLogin`)
    7. SSH key cleanup (handled by composite action post step)

  **Files**: `.github/workflows/bootstrap.yml`
  **Acceptance**: Manual dispatch with host+domain+host_public_key inputs bootstraps and deploys end-to-end; post-deploy validation confirms cloud+auth mode without needing authenticated access

### Phase 4: Documentation

- [x] 11. Rewrite `deploy/README.md` for automated flow
  **What**: Restructure the README to reflect the new automated workflow:
    - **Quick Start**: 5 steps (provision instance → get host public key via `ssh-keyscan` → set DNS → configure GitHub Secrets → run bootstrap workflow)
    - **GitHub Secrets Reference**: Table of all required secrets (`FLEET_SSH_KEY`, `FLEET_SSH_HOST_PUBLIC_KEY`, `FLEET_HOST`, `FLEET_DOMAIN`, `FLEET_AUTH_AUTHORITY`, `FLEET_AUTH_CLIENT_ID`, `FLEET_AUTH_CLIENT_SECRET`) with descriptions and how to obtain each
    - **Manual Bootstrap**: How to run `bootstrap.sh` then `deploy.sh` locally
    - **Deploying**: Auto-deploy on push to main (gated on CI), manual dispatch
    - **Release Management**: Timestamped releases, symlink pointer, automatic pruning
    - **Rollback**: `ROLLBACK=1 deploy.sh` or manual symlink swap
    - **SSH Host Key Pinning**: How to obtain and update the host public key
    - **Architecture**: Keep the contracts table, confinement model, backup/restore sections
    - **Troubleshooting**: Common issues (TLS cert delay, service won't start, auth misconfigured, host key mismatch)

  Preserve the existing detailed sections (confinement, backup/restore, environment variables) but update Task 2-6 to reference automated scripts instead of manual scp/ssh commands.
  **Files**: `deploy/README.md`
  **Acceptance**: README accurately describes the automated workflow; no references to manual scp commands for routine operations; secrets reference table is complete

## Verification
- [ ] All existing CI tests pass (no C# code changes, so this is a sanity check)
- [ ] `shellcheck deploy/bootstrap.sh deploy/deploy.sh deploy/setup.sh` passes with no errors
- [ ] `bootstrap.sh` runs end-to-end on a fresh Ubuntu 22.04 Lightsail instance: fleet enabled-not-started, Caddy active, fleet.env 600/fleet:fleet
- [ ] `deploy.sh` (after bootstrap) delivers app, starts fleet, health check passes
- [ ] After bootstrap + deploy: `curl -sf https://<domain>/healthz` returns 200
- [ ] After bootstrap + deploy: `curl -so /dev/null -w '%{http_code}' https://<domain>/api/config/client` returns 401 (auth enforced)
- [ ] After bootstrap + deploy: SSH verification confirms `fleet.env` contains `Fleet__Auth__Enabled=true` and `Fleet__Cloud__Enabled=true`
- [ ] After second deploy: `/opt/fleet/app` is a symlink; previous release directory preserved under `/opt/fleet/releases/`
- [ ] Deploy with health check failure: auto-rollback to previous release, journal dumped
- [ ] GitHub Actions deploy workflow triggers only after CI workflow succeeds on main
- [ ] Bootstrap workflow fails with clear error if required secrets are missing
- [ ] SSH host key verification rejects connections when server key doesn't match pinned key
- [ ] No secrets appear in any script output, CI logs, or process listings

## Risks and Open Questions

### Risks
1. **TLS cert provisioning delay**: Caddy needs DNS to point to the server before it can issue a Let's Encrypt cert. Bootstrap starts Caddy but the cert may not be ready until after deploy. Mitigation: deploy.sh health check uses 60s timeout with retries; bootstrap validates Caddy is running but treats TLS as informational.
2. **SSH host key rotation**: If the Lightsail instance is reprovisioned, the host key changes and deploys will fail. Mitigation: documented procedure to update `FLEET_SSH_HOST_PUBLIC_KEY` secret; bootstrap workflow accepts key as dispatch input.
3. **fleet.env content drift**: If FleetOptions gains new required fields, bootstrap.sh's env generation won't know about them. Mitigation: deploy.sh health check catches runtime failures; bootstrap validates key presence in fleet.env.
4. **Symlink migration**: First deploy under new scheme must handle existing `/opt/fleet/app` being a real directory. Mitigation: deploy.sh detects and migrates on first run (see TODO #6).
5. **workflow_run trigger**: The `workflow_run` event has known quirks — it uses the default branch's workflow file, not the PR branch. This is fine since deploy only triggers on main. But it means deploy workflow changes must be merged to main before they take effect.
6. **Secret size limits**: GitHub Secrets have a 64KB limit. SSH keys and host public keys are well within this. No concern.

### Open Questions
1. **appsettings.Production.json**: Currently doesn't exist. Should we create one with cloud defaults so fleet.env only needs secrets? **Recommendation**: Out of scope — the current fleet.env approach works and keeps all config in one place. Can be revisited later.
2. **Multiple environments**: Should deploy.yml support staging vs production? **Recommendation**: Out of scope for v1. Single host is the current model. Can add environment matrix later.
3. **Caddy config updates after bootstrap**: If the Caddyfile template changes, there's no automated way to update it. **Recommendation**: Add an optional `UPDATE_CADDY=1` flag to deploy.sh in a future iteration. For now, re-run bootstrap or manually update.

## Recommended First Slice

**Phase 1 (TODOs 1-4)** is the highest-value first slice. It:
- Eliminates the biggest manual toil (5+ scp/ssh commands → 1 `bootstrap.sh` invocation)
- Templates the Caddyfile (eliminates manual editing)
- Auto-provisions fleet.env with proper secret handling (eliminates manual file creation and security risk)
- Validates infrastructure state (eliminates "did I forget a step?" anxiety)
- Establishes the bootstrap/deploy boundary clearly (bootstrap = infra, deploy = app)
- Unblocks Phase 3 (GitHub Actions workflows just call these scripts)
- Can be tested immediately against a real Lightsail instance

Phase 1 does NOT require any changes to deploy.sh — it works with the existing deploy script. The deploy improvements (Phase 2) can follow independently.

Estimated effort for Phase 1: ~3-4 hours of implementation + 1 hour of manual testing against a real instance.
