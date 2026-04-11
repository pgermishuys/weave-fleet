#!/usr/bin/env bash
# deploy/setup.sh — Provision a single-node Weave Fleet host
#
# Contract: single-node host
#   - Linux VM, 2 GB+ RAM
#   - ASP.NET Core 10 runtime
#   - Harness binary (opencode)
#   - TLS reverse proxy (Caddy)
#   - git
#   - Directory structure: /opt/fleet/, /opt/fleet/data/, /opt/fleet/data/keys/, /data/workspaces/
#
# Reference implementation: AWS Lightsail, Ubuntu 22.04 LTS
#
# Usage (run as root or with sudo):
#   curl -sL https://raw.githubusercontent.com/.../deploy/setup.sh | sudo bash
#
# Or after cloning:
#   sudo bash deploy/setup.sh

set -euo pipefail

FLEET_USER="fleet"
FLEET_DIR="/opt/fleet"
FLEET_DATA_DIR="/opt/fleet/data"
FLEET_KEYS_DIR="/opt/fleet/data/keys"
WORKSPACES_DIR="/data/workspaces"
CADDY_LOG_DIR="/var/log/caddy"
DOTNET_VERSION="10.0"
DOTNET_INSTALL_DIR="/usr/share/dotnet"
SWAPFILE_PATH="/swapfile"
SWAPFILE_SIZE_GB="2"

log() { echo "[setup] $*"; }

ensure_swap_if_low_memory() {
  local total_mem_kb
  total_mem_kb="$(awk '/MemTotal/ { print $2 }' /proc/meminfo)"
  local two_gb_kb=$((2 * 1024 * 1024))

  if [ -z "$total_mem_kb" ]; then
    log "Unable to determine system memory; skipping swap auto-provisioning."
    return
  fi

  if [ "$total_mem_kb" -ge "$two_gb_kb" ]; then
    log "System has >= 2 GB RAM; skipping swap auto-provisioning."
    return
  fi

  if swapon --show | grep -q .; then
    log "Swap already enabled; skipping swap auto-provisioning."
    return
  fi

  log "Low-memory host detected (< 2 GB RAM); creating ${SWAPFILE_SIZE_GB} GB swapfile..."

  if [ ! -f "$SWAPFILE_PATH" ]; then
    fallocate -l "${SWAPFILE_SIZE_GB}G" "$SWAPFILE_PATH"
    chmod 600 "$SWAPFILE_PATH"
    mkswap "$SWAPFILE_PATH"
  fi

  swapon "$SWAPFILE_PATH"

  if ! grep -q "^$SWAPFILE_PATH " /etc/fstab; then
    echo "$SWAPFILE_PATH none swap sw 0 0" >> /etc/fstab
  fi

  free -h || true
}

# ── 1. Update packages ────────────────────────────────────────────────────────
log "Updating package lists..."
apt-get update -qq

# ── 2. Install prerequisites ──────────────────────────────────────────────────
log "Installing prerequisites..."
apt-get install -y -qq \
  git \
  curl \
  wget \
  apt-transport-https \
  ca-certificates \
  gnupg \
  debian-keyring \
  debian-archive-keyring

# ── 3. Install ASP.NET Core 10 runtime ────────────────────────────────────────
log "Installing ASP.NET Core $DOTNET_VERSION runtime..."
# Microsoft package feed
wget -q "https://packages.microsoft.com/config/ubuntu/22.04/packages-microsoft-prod.deb" \
  -O /tmp/packages-microsoft-prod.deb
dpkg -i /tmp/packages-microsoft-prod.deb
rm /tmp/packages-microsoft-prod.deb

apt-get update -qq

if apt-cache show "aspnetcore-runtime-$DOTNET_VERSION" >/dev/null 2>&1; then
  log "Installing aspnetcore-runtime-$DOTNET_VERSION from apt..."
  apt-get install -y -qq "aspnetcore-runtime-$DOTNET_VERSION"
else
  log "Package aspnetcore-runtime-$DOTNET_VERSION not available via apt; falling back to dotnet-install.sh"
  mkdir -p "$DOTNET_INSTALL_DIR"
  curl -fsSL https://dot.net/v1/dotnet-install.sh -o /tmp/dotnet-install.sh
  chmod +x /tmp/dotnet-install.sh
  /tmp/dotnet-install.sh --channel "$DOTNET_VERSION" --runtime aspnetcore --install-dir "$DOTNET_INSTALL_DIR"
  ln -sf "$DOTNET_INSTALL_DIR/dotnet" /usr/bin/dotnet
  rm -f /tmp/dotnet-install.sh
fi

dotnet --info

# ── 4. Install Caddy (TLS reverse proxy) ──────────────────────────────────────
log "Installing Caddy..."
curl -1sLf 'https://dl.cloudsmith.io/public/caddy/stable/gpg.key' \
  | gpg --batch --yes --dearmor -o /usr/share/keyrings/caddy-stable-archive-keyring.gpg
curl -1sLf 'https://dl.cloudsmith.io/public/caddy/stable/debian.deb.txt' \
  | tee /etc/apt/sources.list.d/caddy-stable.list
apt-get update -qq
apt-get install -y -qq caddy
caddy version

# ── 5. Install opencode harness binary ───────────────────────────────────────
log "Installing opencode harness binary..."
ensure_swap_if_low_memory
# Install via npm (requires Node.js) or download pre-built binary
if command -v npm &>/dev/null; then
  npm install -g opencode-ai
else
  # Install Node.js LTS first, then opencode
  curl -fsSL https://deb.nodesource.com/setup_lts.x | bash -
  apt-get install -y -qq nodejs
  npm install -g opencode-ai
fi
opencode --version || true

# ── 6. Create service account ─────────────────────────────────────────────────
log "Creating service account '$FLEET_USER'..."
FLEET_HOME="/home/$FLEET_USER"
if ! id "$FLEET_USER" &>/dev/null; then
  useradd --system --create-home --home-dir "$FLEET_HOME" --shell /bin/false "$FLEET_USER"
fi
mkdir -p "$FLEET_HOME"
chown "$FLEET_USER:$FLEET_USER" "$FLEET_HOME"
chmod 700 "$FLEET_HOME"

# ── 7. Create directory structure ─────────────────────────────────────────────
log "Creating directory structure..."
mkdir -p "$FLEET_DIR"
mkdir -p "$FLEET_DATA_DIR"
mkdir -p "$FLEET_KEYS_DIR"
mkdir -p "$WORKSPACES_DIR"
mkdir -p "$CADDY_LOG_DIR"

# Ownership: fleet service account owns /opt/fleet and /data/workspaces
chown -R "$FLEET_USER:$FLEET_USER" "$FLEET_DIR"
chown -R "$FLEET_USER:$FLEET_USER" "$WORKSPACES_DIR"

# Caddy access log directory must be writable by the caddy service account.
chown -R caddy:caddy "$CADDY_LOG_DIR"
chmod 755 "$CADDY_LOG_DIR"
touch "$CADDY_LOG_DIR/fleet-access.log"
chown caddy:caddy "$CADDY_LOG_DIR/fleet-access.log"
chmod 644 "$CADDY_LOG_DIR/fleet-access.log"

# Key ring: only fleet service account can read keys
chmod 700 "$FLEET_KEYS_DIR"

log "Directory structure:"
ls -la "$FLEET_DIR"
ls -la "$WORKSPACES_DIR"

# ── 8. Verify installations ────────────────────────────────────────────────────
log "Verifying installations..."
dotnet --list-runtimes
caddy version
git --version
opencode --version || echo "WARNING: opencode not found on PATH"
if id "$FLEET_USER" &>/dev/null; then
  su -s /bin/sh -c 'HOME="$1" opencode --version' "$FLEET_USER" -- "$FLEET_HOME" \
    || echo "WARNING: opencode failed for fleet service account"
fi

log "Setup complete."
log "Next steps (automated via bootstrap.sh):"
log "  1. Run deploy/bootstrap.sh — installs Caddyfile, fleet.service, fleet.env, enables fleet, starts Caddy"
log "  2. Run deploy/deploy.sh    — builds and delivers the application, starts fleet service"
log ""
log "Manual equivalent:"
log "  1. Install fleet.service: sudo install -m 644 fleet.service /etc/systemd/system/fleet.service"
log "  2. Install Caddyfile:     sudo install -m 644 Caddyfile /etc/caddy/Caddyfile"
log "  3. Enable services:       sudo systemctl daemon-reload && sudo systemctl enable fleet"
log "  4. Start Caddy:           sudo systemctl enable --now caddy"
log "  5. Deploy application:    bash deploy/deploy.sh (do NOT start fleet before deploying)"
