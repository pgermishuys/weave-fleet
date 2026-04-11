#!/usr/bin/env bash
# deploy/deploy.sh — Deploy Weave Fleet to the cloud host
#
# Contract: deployment script
#   - Builds .NET Release artifacts (unless SKIP_BUILD=1)
#   - Builds client SPA (unless SKIP_BUILD=1)
#   - Copies artifacts to a timestamped release directory on the host
#   - Atomically swaps /opt/fleet/app symlink to new release
#   - Restarts service and runs health check
#   - Auto-rolls back to previous release on health check failure
#   - Prunes releases older than the last 3
#
# Usage:
#   FLEET_HOST=user@fleet.example.com bash deploy/deploy.sh
#
#   ROLLBACK=1  — repoint /opt/fleet/app to the previous release and restart
#   SKIP_BUILD=1 — skip dotnet publish + npm build; use existing publish/ dir
#
# Required environment variables:
#   FLEET_HOST — SSH target (e.g. ubuntu@1.2.3.4)
#
# Optional:
#   FLEET_DOMAIN           — Domain for health check (default: derived from host)
#   DOTNET_CONFIGURATION   — Build configuration (default: Release)
#   SSH_OPTS               — SSH/SCP options. MUST include:
#                              -i <key-file>               (private key)
#                              -o StrictHostKeyChecking=yes (pinned host key)
#                            Example (after writing known_hosts):
#                              SSH_OPTS="-i ~/.ssh/fleet.pem -o StrictHostKeyChecking=yes"
#                            GitHub Actions: automatically set by .github/actions/setup-ssh
#   SKIP_BUILD             — Set to 1 to skip build; publish/ must already exist
#   ROLLBACK               — Set to 1 to roll back to the previous release

set -euo pipefail

FLEET_HOST="${FLEET_HOST:?FLEET_HOST must be set (e.g. ubuntu@1.2.3.4)}"
FLEET_DOMAIN="${FLEET_DOMAIN:-}"
DOTNET_CONFIGURATION="${DOTNET_CONFIGURATION:-Release}"
SKIP_BUILD="${SKIP_BUILD:-0}"
ROLLBACK="${ROLLBACK:-0}"
RELEASES_DIR="/opt/fleet/releases"
APP_LINK="/opt/fleet/app"
SERVICE_NAME="fleet"
HEALTH_URL="${FLEET_DOMAIN:+https://$FLEET_DOMAIN/healthz}"
SSH_OPTS="${SSH_OPTS:-}"

SCRIPT_DIR="$(cd "$(dirname "${BASH_SOURCE[0]}")" && pwd)"
REPO_ROOT="$(cd "$SCRIPT_DIR/.." && pwd)"

# Derive the SSH username from FLEET_HOST (e.g. "ubuntu@1.2.3.4" → "ubuntu")
REMOTE_USER="${FLEET_HOST%@*}"
REMOTE_STAGING_DIR="/home/${REMOTE_USER}/fleet-deploy-staging"

log() { echo "[deploy] $*"; }
err() { echo "[deploy] ERROR: $*" >&2; exit 1; }

ssh_remote() {
  # shellcheck disable=SC2086
  ssh $SSH_OPTS "$FLEET_HOST" "$@"
}

rsync_remote() {
  if [ -n "$SSH_OPTS" ]; then
    rsync -e "ssh $SSH_OPTS" "$@"
  else
    rsync "$@"
  fi
}

dump_journal() {
  log "--- Last 50 lines of fleet journal ---"
  ssh_remote "sudo journalctl -u $SERVICE_NAME -n 50 --no-pager" || true
  log "--- End journal ---"
}

# ── Rollback mode ─────────────────────────────────────────────────────────────
if [ "$ROLLBACK" = "1" ]; then
  log "ROLLBACK mode: finding previous release..."
  CURRENT_TARGET="$(ssh_remote "readlink -f $APP_LINK" 2>/dev/null || true)"
  # List all timestamped release dirs, sorted descending; pick the one before current
  PREV_RELEASE="$(ssh_remote "ls -dt ${RELEASES_DIR}/[0-9]*/ 2>/dev/null | grep -v '${CURRENT_TARGET}/' | head -n 1 | sed 's|/$||'" 2>/dev/null || true)"
  if [ -z "$PREV_RELEASE" ]; then
    err "No previous release found to roll back to."
  fi
  log "Rolling back to: $PREV_RELEASE"
  ssh_remote "sudo systemctl stop $SERVICE_NAME || true"
  ssh_remote "sudo ln -sfn $PREV_RELEASE $APP_LINK"
  ssh_remote "sudo systemctl start $SERVICE_NAME"
  log "Rollback complete. Current release: $PREV_RELEASE"
  exit 0
fi

# ── 1. Build .NET application ─────────────────────────────────────────────────
if [ "$SKIP_BUILD" = "1" ]; then
  log "SKIP_BUILD=1 — skipping build steps."
  if [ ! -d "$REPO_ROOT/publish" ] || [ -z "$(ls -A "$REPO_ROOT/publish" 2>/dev/null)" ]; then
    err "SKIP_BUILD=1 but publish/ directory is missing or empty: $REPO_ROOT/publish"
  fi
  log "Using existing publish/ artifacts."
else
  log "Building .NET application ($DOTNET_CONFIGURATION)..."
  cd "$REPO_ROOT"
  dotnet publish src/WeaveFleet.Api/WeaveFleet.Api.csproj \
    --configuration "$DOTNET_CONFIGURATION" \
    --output "$REPO_ROOT/publish" \
    --runtime linux-x64 \
    --self-contained false \
    || err "dotnet publish failed"

  # ── 2. Build client SPA ─────────────────────────────────────────────────────
  log "Building client SPA..."
  if [ -d "$REPO_ROOT/client" ]; then
    cd "$REPO_ROOT/client"
    npm ci
    npm run build
    # Copy built SPA into publish/wwwroot
    rm -rf "$REPO_ROOT/publish/wwwroot"
    cp -r "$REPO_ROOT/client/out" "$REPO_ROOT/publish/wwwroot"
    cd "$REPO_ROOT"
  else
    log "WARNING: client/ directory not found — skipping SPA build"
  fi
fi

# ── 3. Generate release tag ───────────────────────────────────────────────────
RELEASE_TAG="$(date -u +%Y%m%d-%H%M%S)"
RELEASE_DIR="${RELEASES_DIR}/${RELEASE_TAG}"
log "Release tag: $RELEASE_TAG"

# ── 4. Handle migration from legacy flat directory ────────────────────────────
# If /opt/fleet/app is a plain directory (not a symlink), migrate it first.
IS_SYMLINK="$(ssh_remote "test -L $APP_LINK && echo yes || echo no" 2>/dev/null || echo no)"
if [ "$IS_SYMLINK" = "no" ] && ssh_remote "test -d $APP_LINK" 2>/dev/null; then
  log "Migrating legacy /opt/fleet/app directory to $RELEASES_DIR/pre-migration ..."
  ssh_remote "sudo mkdir -p $RELEASES_DIR && sudo mv $APP_LINK ${RELEASES_DIR}/pre-migration && sudo chown -R fleet:fleet ${RELEASES_DIR}/pre-migration"
fi

# ── 5. Create release directory ───────────────────────────────────────────────
log "Creating release directory $RELEASE_DIR ..."
ssh_remote "sudo mkdir -p $RELEASE_DIR && sudo chown fleet:fleet $RELEASE_DIR"

# ── 6. Stop service gracefully ────────────────────────────────────────────────
log "Stopping service on remote host..."
ssh_remote "sudo systemctl stop $SERVICE_NAME || true"

# ── 7. Copy artifacts to staging, then promote into release dir ───────────────
log "Copying artifacts to $FLEET_HOST:$REMOTE_STAGING_DIR ..."
ssh_remote "mkdir -p $REMOTE_STAGING_DIR"
rsync_remote -az --delete \
  --exclude '*.db' \
  --exclude '*.db-wal' \
  --exclude '*.db-shm' \
  "$REPO_ROOT/publish/" \
  "$FLEET_HOST:$REMOTE_STAGING_DIR/"

log "Promoting staged artifacts into $RELEASE_DIR ..."
ssh_remote "sudo rsync -a --delete $REMOTE_STAGING_DIR/ $RELEASE_DIR/ && sudo chown -R fleet:fleet $RELEASE_DIR"

# ── 8. Atomic symlink swap ────────────────────────────────────────────────────
log "Pointing $APP_LINK → $RELEASE_DIR ..."
# Capture previous release for rollback reference
PREV_RELEASE="$(ssh_remote "readlink -f $APP_LINK 2>/dev/null || true" || true)"
ssh_remote "sudo ln -sfn $RELEASE_DIR $APP_LINK"

# ── 9. Start service ──────────────────────────────────────────────────────────
log "Starting service..."
ssh_remote "sudo systemctl start $SERVICE_NAME"

# Wait for service to be active (up to 30s)
WAIT=0
while [ "$WAIT" -lt 30 ]; do
  if ssh_remote "systemctl is-active $SERVICE_NAME" >/dev/null 2>&1; then
    log "Service is active."
    break
  fi
  sleep 2
  WAIT=$((WAIT + 2))
done

if ! ssh_remote "systemctl is-active $SERVICE_NAME" >/dev/null 2>&1; then
  dump_journal
  # Roll back if we have a previous release
  if [ -n "$PREV_RELEASE" ] && [ "$PREV_RELEASE" != "$RELEASE_DIR" ]; then
    log "Rolling back to $PREV_RELEASE ..."
    ssh_remote "sudo ln -sfn $PREV_RELEASE $APP_LINK && sudo systemctl start $SERVICE_NAME || true"
  fi
  err "Service failed to start within 30s."
fi

# ── 10. Health check ──────────────────────────────────────────────────────────
if [ -n "$HEALTH_URL" ]; then
  log "Running health check: $HEALTH_URL"
  WAIT=0
  HEALTH_PASSED=0
  while [ "$WAIT" -lt 60 ]; do
    if curl -sf --max-time 5 "$HEALTH_URL" >/dev/null 2>&1; then
      HEALTH_PASSED=1
      break
    fi
    sleep 3
    WAIT=$((WAIT + 3))
  done

  if [ "$HEALTH_PASSED" = "0" ]; then
    log "Health check failed after 60s."
    dump_journal
    # Auto-rollback to previous release
    if [ -n "$PREV_RELEASE" ] && [ "$PREV_RELEASE" != "$RELEASE_DIR" ]; then
      log "Auto-rolling back to $PREV_RELEASE ..."
      ssh_remote "sudo systemctl stop $SERVICE_NAME || true"
      ssh_remote "sudo ln -sfn $PREV_RELEASE $APP_LINK"
      ssh_remote "sudo systemctl start $SERVICE_NAME || true"
      log "Rollback complete. Previous release is now active."
    fi
    err "Health check failed: $HEALTH_URL"
  fi

  log "Health check passed."
else
  log "No FLEET_DOMAIN set — skipping HTTP health check."
  log "Manually verify: curl https://<your-domain>/healthz"
fi

# ── 11. Prune old releases (keep last 3) ──────────────────────────────────────
log "Pruning old releases (keeping last 3)..."
ssh_remote "ls -dt ${RELEASES_DIR}/[0-9]*/ 2>/dev/null | tail -n +4 | sed 's|/$||' | xargs -r sudo rm -rf" || true

log "Deploy complete. Active release: $RELEASE_DIR"
