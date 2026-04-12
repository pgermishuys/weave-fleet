#!/usr/bin/env bash
# deploy/bootstrap.sh — Automate full infrastructure bootstrap for a Weave Fleet cloud host.
#
# Boundary: bootstrap = infra-only. This script installs runtimes, templates configs,
# provisions fleet.env, enables services — but does NOT start fleet (app binary doesn't
# exist yet). Run deploy.sh afterwards to deliver app artifacts and start the service.
#
# Usage (local):
#   FLEET_HOST=ubuntu@<ip> \
#   FLEET_DOMAIN=fleet.example.com \
#   Fleet__Auth__Authority=https://... \
#   Fleet__Auth__ClientId=<id> \
#   Fleet__Auth__ClientSecret=<secret> \
#   [SSH_OPTS="-i path/to/key.pem"] \
#   bash deploy/bootstrap.sh
#
# Usage (GitHub Actions):
#   Called from .github/workflows/bootstrap.yml — env vars injected from secrets.
#
# Required env vars:
#   FLEET_HOST                  SSH target, e.g. ubuntu@1.2.3.4
#   FLEET_DOMAIN                Public domain name, e.g. fleet.example.com
#   Fleet__Auth__Authority      OIDC issuer URL
#   Fleet__Auth__ClientId       OIDC client ID
#   Fleet__Auth__ClientSecret   OIDC client secret (sensitive)
#
# Optional env vars:
#   SSH_OPTS                    Extra SSH/SCP args, e.g. "-i path/to/key.pem"
#   FLEET_ALLOWED_ORIGINS       Comma-separated extra allowed origins (default: https://$FLEET_DOMAIN)

set -euo pipefail

# ── Helpers ───────────────────────────────────────────────────────────────────

SCRIPT_DIR="$(cd "$(dirname "${BASH_SOURCE[0]}")" && pwd)"
REPO_ROOT="$(cd "$SCRIPT_DIR/.." && pwd)"

log()  { echo "[bootstrap] $*"; }
err()  { echo "[bootstrap] ERROR: $*" >&2; exit 1; }
warn() { echo "[bootstrap] WARNING: $*" >&2; }

ssh_remote() {
  # shellcheck disable=SC2086
  ssh ${SSH_OPTS:-} "$FLEET_HOST" "$@"
}

scp_to_remote() {
  local src="$1" dst="$2"
  # shellcheck disable=SC2086
  scp ${SSH_OPTS:-} "$src" "$FLEET_HOST:$dst"
}

# ── Validate required env vars ────────────────────────────────────────────────

log "Validating required environment variables..."

: "${FLEET_HOST:?FLEET_HOST must be set (e.g. ubuntu@1.2.3.4)}"
: "${FLEET_DOMAIN:?FLEET_DOMAIN must be set (e.g. fleet.example.com)}"
: "${Fleet__Auth__Authority:?Fleet__Auth__Authority must be set (OIDC issuer URL)}"
: "${Fleet__Auth__ClientId:?Fleet__Auth__ClientId must be set}"
: "${Fleet__Auth__ClientSecret:?Fleet__Auth__ClientSecret must be set}"

log "FLEET_HOST:   $FLEET_HOST"
log "FLEET_DOMAIN: $FLEET_DOMAIN"
log "Auth:         authority set, client ID set, secret set (not logged)"

# ── Temp file cleanup trap ────────────────────────────────────────────────────

TMPFILES=()
cleanup() {
  for f in "${TMPFILES[@]:-}"; do
    rm -f "$f" 2>/dev/null || true
  done
}
trap cleanup EXIT INT TERM

# ── Step 1: Upload and run setup.sh ──────────────────────────────────────────

log "Uploading setup.sh to remote host..."
scp_to_remote "$SCRIPT_DIR/setup.sh" "~/fleet-setup.sh"

log "Running setup.sh on remote host (this may take several minutes)..."
ssh_remote "chmod +x ~/fleet-setup.sh && sudo bash ~/fleet-setup.sh && rm -f ~/fleet-setup.sh"

# ── Step 2: Render and install Caddyfile ─────────────────────────────────────

log "Rendering Caddyfile from template..."

CADDY_RENDERED="$(mktemp)"
TMPFILES+=("$CADDY_RENDERED")

FLEET_DOMAIN="$FLEET_DOMAIN" envsubst '${FLEET_DOMAIN}' \
  < "$SCRIPT_DIR/Caddyfile.template" \
  > "$CADDY_RENDERED"

log "Uploading rendered Caddyfile..."
scp_to_remote "$CADDY_RENDERED" "~/Caddyfile.tmp"
ssh_remote "sudo install -m 644 -o root -g root ~/Caddyfile.tmp /etc/caddy/Caddyfile && rm -f ~/Caddyfile.tmp && sudo caddy validate --config /etc/caddy/Caddyfile"
log "Caddyfile installed at /etc/caddy/Caddyfile"

# ── Step 3: Install fleet.service ────────────────────────────────────────────

log "Installing fleet.service..."
scp_to_remote "$SCRIPT_DIR/fleet.service" "~/fleet.service.tmp"
ssh_remote "sudo install -m 644 -o root -g root ~/fleet.service.tmp /etc/systemd/system/fleet.service && rm -f ~/fleet.service.tmp"
log "fleet.service installed at /etc/systemd/system/fleet.service"

# ── Step 4: Provision fleet.env securely ─────────────────────────────────────

"$SCRIPT_DIR/provision-fleet-env.sh"

# ── Step 5: Create release directory structure ────────────────────────────────

log "Creating release directory structure..."
ssh_remote "sudo mkdir -p /opt/fleet/releases && sudo chown fleet:fleet /opt/fleet/releases"

# ── Step 6: Enable services ───────────────────────────────────────────────────

log "Reloading systemd and enabling fleet service..."
ssh_remote "sudo systemctl daemon-reload && sudo systemctl enable fleet"
log "fleet service enabled (not started — run deploy.sh to deliver app and start service)"

log "Starting or reloading Caddy (TLS provisioning begins now — DNS must already point to this host)..."
ssh_remote "sudo systemctl enable caddy && sudo systemctl restart caddy"

# ── Step 7: Infrastructure validation ────────────────────────────────────────

log "Validating infrastructure..."

VALIDATION_FAILED=0

validate() {
  local desc="$1" cmd="$2"
  if ssh_remote "$cmd" > /dev/null 2>&1; then
    log "  ✓ $desc"
  else
    log "  ✗ FAILED: $desc"
    VALIDATION_FAILED=1
  fi
}

# 1. Fleet service enabled
validate "fleet service is enabled" \
  "systemctl is-enabled fleet"

# 2. Caddy active
validate "caddy is active" \
  "systemctl is-active caddy"

# 3. fleet.env exists with correct permissions
validate "fleet.env exists with mode 600" \
  "stat -c '%a' /opt/fleet/data/fleet.env | grep -qx '600'"

# 4. fleet.env owner is fleet:fleet
validate "fleet.env owned by fleet:fleet" \
  "stat -c '%U:%G' /opt/fleet/data/fleet.env | grep -qx 'fleet:fleet'"

# 5. fleet.env contains required keys (no values leaked)
validate "fleet.env contains Fleet__Auth__Enabled=true" \
  "sudo grep -q 'Fleet__Auth__Enabled=true' /opt/fleet/data/fleet.env"

validate "fleet.env contains Fleet__Cloud__Enabled=true" \
  "sudo grep -q 'Fleet__Cloud__Enabled=true' /opt/fleet/data/fleet.env"

# 6. Directory structure
validate "/opt/fleet/data/keys exists with mode 700" \
  "stat -c '%a' /opt/fleet/data/keys | grep -qx '700'"

validate "/data/workspaces exists" \
  "test -d /data/workspaces"

validate "/opt/fleet/releases exists" \
  "test -d /opt/fleet/releases"

# 7. ASP.NET Core runtime installed
validate "ASP.NET Core 10 runtime installed" \
  "dotnet --list-runtimes | grep -q 'Microsoft.AspNetCore.App 10'"

# 8. Caddy TLS readiness (informational only — cert may still be provisioning)
log "Checking Caddy TLS readiness (informational, may not be ready yet)..."
if curl -sf --max-time 10 "https://$FLEET_DOMAIN/" > /dev/null 2>&1; then
  log "  ✓ TLS: https://$FLEET_DOMAIN/ is reachable (Caddy cert provisioned)"
else
  warn "  ~ TLS: https://$FLEET_DOMAIN/ not yet reachable — Caddy may still be provisioning cert"
  warn "    This is normal. DNS must point to this host and cert provisioning can take up to 60s."
fi

if [ "$VALIDATION_FAILED" -ne 0 ]; then
  err "Infrastructure validation failed. Review errors above before running deploy.sh."
fi

# ── Done ──────────────────────────────────────────────────────────────────────

log ""
log "Bootstrap complete."
log "Infrastructure validated. Summary:"
log "  fleet service:  enabled (not started)"
log "  Caddy:          active"
log "  fleet.env:      /opt/fleet/data/fleet.env (600, fleet:fleet)"
log "  releases dir:   /opt/fleet/releases"
log ""
log "Next step: run deploy.sh to deliver application artifacts and start fleet."
log "  FLEET_HOST=$FLEET_HOST FLEET_DOMAIN=$FLEET_DOMAIN \\"
log "  SSH_OPTS=\"\${SSH_OPTS:-}\" bash deploy/deploy.sh"
