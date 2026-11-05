#!/usr/bin/env sh
set -e

SCRIPT_DIR="$(cd "$(dirname "$0")" && pwd)"
ROOT_DIR="$(cd "$SCRIPT_DIR/.." && pwd)"

PACKAGE_APP_DIR="$ROOT_DIR/app"
PACKAGE_BIN="$PACKAGE_APP_DIR/WeaveFleet.Api"
PACKAGE_CONTENT_ROOT="$PACKAGE_APP_DIR"
REPO_APP_DIR="$ROOT_DIR/src/WeaveFleet.Api/bin/Release/net10.0"
REPO_BIN="$REPO_APP_DIR/WeaveFleet.Api"
REPO_CONTENT_ROOT="$ROOT_DIR/src/WeaveFleet.Api"
VERSION_FILE="$ROOT_DIR/VERSION"
DEV_VERSION_FILE="$ROOT_DIR/Directory.Build.props"
TRIAL_FILE="$ROOT_DIR/update-trial.json"
FAILED_FILE="$ROOT_DIR/update-failed.json"
PREV_DB_DIR="$ROOT_DIR/update.prev"
INSTALL_SCRIPT_URL="${WEAVE_FLEET_INSTALL_SCRIPT_URL:-https://github.com/pgermishuys/weave-fleet/releases/latest/download/install.sh}"

# ── Apply staged update (if any) ─────────────────────────────────────────────
apply_staged_update() {
  UPDATE_DIR="$ROOT_DIR/update"
  MANIFEST="$UPDATE_DIR/update-manifest.json"

  if [ ! -f "$MANIFEST" ]; then
    return 0
  fi

  # Parse version and asset filename from the JSON manifest using basic shell tools.
  # NOTE: The JSON is typically minified (single line), so we must check each field
  # independently rather than using a case statement (which only matches the first pattern).
  UPDATE_VERSION=""
  ASSET_FILE=""
  while IFS= read -r line; do
    case "$line" in *'"version"'*)
      UPDATE_VERSION="$(printf '%s' "$line" | sed 's/.*"version"[[:space:]]*:[[:space:]]*"\([^"]*\)".*/\1/')"
    ;; esac
    case "$line" in *'"assetFileName"'*)
      ASSET_FILE="$(printf '%s' "$line" | sed 's/.*"assetFileName"[[:space:]]*:[[:space:]]*"\([^"]*\)".*/\1/')"
    ;; esac
  done < "$MANIFEST"

  if [ -z "$UPDATE_VERSION" ] || [ -z "$ASSET_FILE" ]; then
    echo "Warning: update manifest is malformed — skipping update." >&2
    rm -rf "$UPDATE_DIR"
    return 0
  fi

  ARCHIVE="$UPDATE_DIR/$ASSET_FILE"
  if [ ! -f "$ARCHIVE" ]; then
    echo "Warning: update archive '$ASSET_FILE' not found — skipping update." >&2
    rm -rf "$UPDATE_DIR"
    return 0
  fi

  echo "Applying Fleet update to v${UPDATE_VERSION}..."

  # Back up existing app dir.
  APP_BAK="$ROOT_DIR/app.bak"
  rm -rf "$APP_BAK"
  cp -a "$ROOT_DIR/app" "$APP_BAK"

  # Extract the archive over the app dir.
  EXTRACT_TMP="$UPDATE_DIR/extract_tmp"
  rm -rf "$EXTRACT_TMP"
  mkdir -p "$EXTRACT_TMP"

  case "$ASSET_FILE" in
    *.tar.gz)
      tar -xzf "$ARCHIVE" -C "$EXTRACT_TMP"
      ;;
    *.zip)
      unzip -q "$ARCHIVE" -d "$EXTRACT_TMP"
      ;;
    *)
      echo "Warning: unknown archive format '$ASSET_FILE' — skipping update." >&2
      rm -rf "$EXTRACT_TMP" "$APP_BAK"
      rm -rf "$UPDATE_DIR"
      return 0
      ;;
  esac

  # The archive contains a top-level directory (e.g. fleet-v0.2.0-linux-x64/).
  # Find the extracted root.
  EXTRACTED_ROOT=""
  for d in "$EXTRACT_TMP"/*/; do
    if [ -d "$d" ]; then
      EXTRACTED_ROOT="$d"
      break
    fi
  done

  if [ -z "$EXTRACTED_ROOT" ] || [ ! -d "${EXTRACTED_ROOT}app" ]; then
    echo "Warning: expected 'app/' directory in archive — skipping update." >&2
    rm -rf "$EXTRACT_TMP"
    cp -a "$APP_BAK/." "$ROOT_DIR/app/"
    rm -rf "$APP_BAK" "$UPDATE_DIR"
    return 0
  fi

  # Keep what is being replaced, so a version that doesn't start can be undone (see rollback_update).
  rm -rf "$ROOT_DIR/app.prev" "$ROOT_DIR/bin.prev" "$ROOT_DIR/VERSION.prev" "$PREV_DB_DIR"
  mv "$APP_BAK" "$ROOT_DIR/app.prev"
  [ -d "$ROOT_DIR/bin" ] && cp -a "$ROOT_DIR/bin" "$ROOT_DIR/bin.prev"
  [ -f "$VERSION_FILE" ] && cp "$VERSION_FILE" "$ROOT_DIR/VERSION.prev"
  PREV_VERSION="$(sed -n '1p' "$VERSION_FILE" 2>/dev/null || true)"

  # Replace app dir.
  rm -rf "$ROOT_DIR/app"
  cp -a "${EXTRACTED_ROOT}app" "$ROOT_DIR/app"

  # Replace bin dir (updates the launcher itself).
  if [ -d "${EXTRACTED_ROOT}bin" ]; then
    rm -rf "$ROOT_DIR/bin"
    cp -a "${EXTRACTED_ROOT}bin" "$ROOT_DIR/bin"
    chmod +x "$ROOT_DIR/bin/"* 2>/dev/null || true
  fi

  # Update VERSION file.
  printf '%s\n' "$UPDATE_VERSION" > "$ROOT_DIR/VERSION"

  # The new version is on trial until it says it started (the app deletes this file); run_app watches it.
  printf '{"from":"%s","to":"%s","startedAt":"%s"}\n' "$PREV_VERSION" "$UPDATE_VERSION" "$(date -u +%Y-%m-%dT%H:%M:%SZ)" > "$TRIAL_FILE"

  # Clean up.
  rm -rf "$UPDATE_DIR"

  echo "Fleet updated to v${UPDATE_VERSION}."
}

APP_DIR=""
APP_BIN=""
APP_CONTENT_ROOT=""
INSTALL_LAYOUT=0

if [ -x "$PACKAGE_BIN" ]; then
  APP_DIR="$PACKAGE_APP_DIR"
  APP_BIN="$PACKAGE_BIN"
  APP_CONTENT_ROOT="$PACKAGE_CONTENT_ROOT"
  INSTALL_LAYOUT=1
elif [ -x "$REPO_BIN" ]; then
  APP_DIR="$REPO_APP_DIR"
  APP_BIN="$REPO_BIN"
  APP_CONTENT_ROOT="$REPO_CONTENT_ROOT"
else
  echo "Error: Fleet binary not found." >&2
  echo "Expected one of:" >&2
  echo "  $PACKAGE_BIN" >&2
  echo "  $REPO_BIN" >&2
  echo "Build or publish Fleet first." >&2
  exit 1
fi

# Apply staged update after layout detection, only for installed packages.
if [ "$INSTALL_LAYOUT" -eq 1 ]; then
  apply_staged_update
fi

read_version() {
  if [ -f "$VERSION_FILE" ]; then
    sed -n '1p' "$VERSION_FILE"
    return
  fi

  if [ -f "$DEV_VERSION_FILE" ]; then
    VERSION_LINE="$(sed -n 's/.*<Version>\([^<]*\)<\/Version>.*/\1/p' "$DEV_VERSION_FILE" | sed -n '1p')"
    if [ -n "$VERSION_LINE" ]; then
      printf '%s\n' "$VERSION_LINE"
      return
    fi
  fi

  printf 'unknown\n'
}

show_help() {
  VERSION="$(read_version)"
  echo "Fleet v${VERSION}"
  echo ""
  echo "Usage: fleet [command] [--port <port>] [--host <host>] [--data-dir <path>] [--profile <name>] [--require-token]"
  echo ""
  echo "Commands:"
  echo "  (none)       Start the Fleet server"
  echo "  node         Start a node: the API without the web app (see 'fleet node help')"
  echo "  version      Print the installed version"
  echo "  update       Update to the latest version"
  echo "  uninstall    Remove Fleet"
  echo "  help         Show this help message"
  echo "  import-legacy-sessions  Import sessions from a legacy database"
  echo ""
echo "Options when starting the server:"
echo "  --port <port>       Override the server port"
echo "  --host <host>       Override the bind host"
echo "  --data-dir <path>   Override the data directory (default: ~/.weave)"
echo "  --profile <name>    Use a profile-specific data directory"
echo "  --require-token     Ask for the access token even over loopback (behind tailscale serve)"
  echo ""
echo "Environment variables:"
echo "  WEAVE_FLEET_PORT                Server port (default: 6262)"
echo "  WEAVE_FLEET_HOST                Bind host (default: 127.0.0.1)"
echo "  WEAVE_FLEET_DATA_DIR            Data directory (default: ~/.weave)"
echo "  Fleet__DatabasePath             SQLite database path override"
  echo "  Fleet__AnalyticsDatabasePath    Analytics database path override"
  echo "  Fleet__DataProtection__KeyPath  Data protection key directory override"
}

show_node_help() {
  VERSION="$(read_version)"
  echo "Fleet v${VERSION}"
  echo ""
  echo "Usage: fleet node up [--tailscale | --lan | --ssh-only] [--yes] [--port <port>] [--data-dir <path>] [--profile <name>]"
  echo "       fleet node [--port <port>] [--host <host>] [--data-dir <path>] [--profile <name>]"
  echo "       fleet node install-service [--port <port>] [--host <host>] [--data-dir <path>] [--profile <name>] [--print]"
  echo "       fleet node uninstall-service"
  echo "       fleet node pair [--port <port>] [--data-dir <path>] [--profile <name>] [--json]"
  echo ""
  echo "Starts Fleet as a node: the API without the web app. Every request needs the access token,"
  echo "even from this machine. Run 'fleet node up' to set it up, then add it to another Fleet in Settings > Machines > Add a machine."
  echo ""
  echo "Commands:"
  echo "  up                  Set this machine up: ask how other machines reach it, keep it running in the background,"
  echo "                      and print a pairing link. Run it again to change the choice or get a new link."
  echo "  install-service     Keep the node running as you: now, when you log in, and if it stops"
  echo "                      (a systemd user service on Linux, a LaunchAgent on macOS)"
  echo "  uninstall-service   Stop the node and remove that service"
  echo "  pair                Print a one-time code (10 minutes, works once) and this node's address, so another"
  echo "                      Fleet can add it without the master token. Run it while the node is running."
  echo ""
  echo "Options:"
  echo "  --port <port>       Override the server port"
  echo "  --host <host>       Override the bind host (0.0.0.0 lets other machines connect)"
  echo "  --data-dir <path>   Override the data directory (default: ~/.weave)"
  echo "  --profile <name>    Use a profile-specific data directory"
  echo "  --tailscale         With up: listen on this machine's Tailscale address"
  echo "  --lan               With up: listen on every network this machine is on"
  echo "  --ssh-only          With up: listen on this machine only; other machines connect over SSH"
  echo "  --yes               With up: don't ask. Uses the choice above, else what is set up, else Tailscale when it's up, else --ssh-only"
  echo "  --json              With pair: print the code and addresses as one line of JSON, for another Fleet over SSH"
  echo "  --print             With install-service: show what it would write and run, and change nothing"
}

# ── fleet node install-service / uninstall-service ───────────────────────────
# The node runs as you, so harness sign-ins, git credentials and repositories in your home keep working. It's
# always named fleet-node and never touches a fleet.service you run yourself. It starts this launcher, not the app,
# so staged updates still apply when it restarts.
SERVICE_NAME="fleet-node"
LAUNCHD_LABEL="io.tryweave.fleet-node"

# Quotes one argument for a systemd unit, so specifiers (%) and variables ($) are taken literally.
systemd_quote() {
  printf '"%s"' "$(printf '%s' "$1" | sed -e 's/\\/\\\\/g' -e 's/"/\\"/g' -e 's/%/%%/g' -e 's/\$/$$/g')"
}

xml_escape() {
  printf '%s' "$1" | sed -e 's/&/\&amp;/g' -e 's/</\&lt;/g' -e 's/>/\&gt;/g' -e 's/"/\&quot;/g'
}

systemd_unit_path() {
  printf '%s\n' "${XDG_CONFIG_HOME:-$HOME/.config}/systemd/user/$SERVICE_NAME.service"
}

launchd_plist_path() {
  printf '%s\n' "$HOME/Library/LaunchAgents/$LAUNCHD_LABEL.plist"
}

# $SERVICE_ARGS holds what the service runs, one argument per line.
render_systemd_unit() {
  EXEC_LINE=""
  while IFS= read -r arg; do
    EXEC_LINE="$EXEC_LINE${EXEC_LINE:+ }$(systemd_quote "$arg")"
  done <<ARGS
$SERVICE_ARGS
ARGS
  cat <<UNIT
# Weave Fleet node, kept running by systemd for this user. Written by "fleet node install-service";
# "fleet node uninstall-service" stops it and removes this file. Logs: journalctl --user -u $SERVICE_NAME

[Unit]
Description=Weave Fleet node
After=network-online.target
Wants=network-online.target

[Service]
Type=simple
ExecStart=$EXEC_LINE
Restart=always
RestartSec=5
# 75: another Fleet already uses this data directory, so starting again won't help.
RestartPreventExitStatus=75
WorkingDirectory=%h
# The PATH install-service ran with, so agents find the same tools as your shell.
Environment=$(systemd_quote "PATH=$PATH")

[Install]
WantedBy=default.target
UNIT
}

render_launchd_plist() {
  cat <<PLIST
<?xml version="1.0" encoding="UTF-8"?>
<!DOCTYPE plist PUBLIC "-//Apple//DTD PLIST 1.0//EN" "http://www.apple.com/DTDs/PropertyList-1.0.dtd">
<!-- Weave Fleet node, kept running by launchd. Written by "fleet node install-service";
     "fleet node uninstall-service" stops it and removes this file. -->
<plist version="1.0">
<dict>
  <key>Label</key>
  <string>$LAUNCHD_LABEL</string>
  <key>ProgramArguments</key>
  <array>
PLIST
  while IFS= read -r arg; do
    printf '    <string>%s</string>\n' "$(xml_escape "$arg")"
  done <<ARGS
$SERVICE_ARGS
ARGS
  cat <<PLIST
  </array>
  <key>RunAtLoad</key>
  <true/>
  <key>KeepAlive</key>
  <true/>
  <key>WorkingDirectory</key>
  <string>$(xml_escape "$HOME")</string>
  <key>EnvironmentVariables</key>
  <dict>
    <key>PATH</key>
    <string>$(xml_escape "$PATH")</string>
  </dict>
  <key>StandardOutPath</key>
  <string>$(xml_escape "$HOME/Library/Logs/$SERVICE_NAME.log")</string>
  <key>StandardErrorPath</key>
  <string>$(xml_escape "$HOME/Library/Logs/$SERVICE_NAME.log")</string>
</dict>
</plist>
PLIST
}

service_platform() {
  case "$(uname -s)" in
    Linux) echo linux ;;
    Darwin) echo macos ;;
    *) echo "Error: fleet node install-service works on Linux (systemd) and macOS. On Windows, use fleet.cmd." >&2; exit 1 ;;
  esac
}

# Sets PLATFORM and TARGET, the file the service lives in.
set_service_target() {
  PLATFORM="$(service_platform)" || exit 1
  if [ "$PLATFORM" = linux ]; then
    TARGET="$(systemd_unit_path)"
  else
    TARGET="$(launchd_plist_path)"
  fi
}

# The service gets what this run resolved, so it starts the same node whatever its own environment holds.
set_service_args() {
  case "$DATA_DIR" in
    /*) ;;
    *) DATA_DIR="$PWD/$DATA_DIR" ;;
  esac
  SERVICE_ARGS="$SCRIPT_DIR/$(basename "$0")
node
--port
$PORT
--host
$HOST
--data-dir
$DATA_DIR"
}

install_service() {
  set_service_target

  if [ "$PRINT_ONLY" -eq 1 ]; then
    echo "fleet node install-service would write $TARGET:"
    echo ""
    if [ "$PLATFORM" = linux ]; then
      render_systemd_unit
      echo ""
      echo "and run:"
      echo "  systemctl --user daemon-reload"
      echo "  systemctl --user enable $SERVICE_NAME.service"
      echo "  systemctl --user restart $SERVICE_NAME.service"
    else
      render_launchd_plist
      echo ""
      echo "and run:"
      echo "  launchctl bootout gui/$(id -u)/$LAUNCHD_LABEL   (only if it's already loaded)"
      echo "  launchctl bootstrap gui/$(id -u) $TARGET"
    fi
    echo ""
    echo "Nothing was changed."
    return 0
  fi

  if [ "$INSTALL_LAYOUT" -ne 1 ]; then
    echo "Error: install-service is only supported from an installed package layout." >&2
    exit 1
  fi
  if [ "$PLATFORM" = linux ] && ! command -v systemctl >/dev/null 2>&1; then
    echo "Error: systemctl wasn't found. install-service needs systemd on Linux." >&2
    exit 1
  fi

  UPDATING=0
  [ -f "$TARGET" ] && UPDATING=1
  # A node can't share a data directory with another running Fleet: it would stop at once with exit code 75.
  if [ "$UPDATING" -eq 0 ] && [ -f "$DATA_DIR/fleet.instance.json" ]; then
    echo "Warning: a Fleet may already be running from $DATA_DIR. A node needs its own data directory;" >&2
    echo "if it doesn't start, install it again with --profile node." >&2
  fi

  mkdir -p "$(dirname "$TARGET")"
  if [ "$PLATFORM" = linux ]; then
    render_systemd_unit > "$TARGET"
    systemctl --user daemon-reload
    systemctl --user enable "$SERVICE_NAME.service"
    systemctl --user restart "$SERVICE_NAME.service"
  else
    mkdir -p "$HOME/Library/Logs"
    render_launchd_plist > "$TARGET"
    if launchctl print "gui/$(id -u)/$LAUNCHD_LABEL" >/dev/null 2>&1; then
      launchctl bootout "gui/$(id -u)/$LAUNCHD_LABEL"
    fi
    launchctl bootstrap "gui/$(id -u)" "$TARGET"
  fi

  echo ""
  if [ "$UPDATING" -eq 1 ]; then
    echo "Updated the $SERVICE_NAME service and restarted it."
  else
    echo "Installed the $SERVICE_NAME service. It runs now, when you log in, and again if it stops."
  fi
  echo "  Service file:   $TARGET"
  echo "  Data and token: $DATA_DIR (the token is in fleet.machine.json)"
  if [ "$PLATFORM" = linux ]; then
    echo "  Status:         systemctl --user status $SERVICE_NAME"
    echo "  Logs:           journalctl --user -u $SERVICE_NAME"
    if [ "$(loginctl show-user "$(id -un)" -p Linger --value 2>/dev/null)" != "yes" ]; then
      echo ""
      echo "It stops when you log out. To keep it running on a machine nobody logs in to, run:"
      echo "  loginctl enable-linger $(id -un)"
    fi
  else
    echo "  Status:         launchctl print gui/$(id -u)/$LAUNCHD_LABEL"
    echo "  Logs:           $HOME/Library/Logs/$SERVICE_NAME.log"
  fi
  echo ""
  echo "To undo: fleet node uninstall-service"
}

uninstall_service() {
  set_service_target
  if [ ! -f "$TARGET" ]; then
    echo "There's no $SERVICE_NAME service to remove."
    return 0
  fi

  if [ "$PLATFORM" = linux ]; then
    systemctl --user disable --now "$SERVICE_NAME.service"
    rm -f "$TARGET"
    systemctl --user daemon-reload
  else
    if launchctl print "gui/$(id -u)/$LAUNCHD_LABEL" >/dev/null 2>&1; then
      launchctl bootout "gui/$(id -u)/$LAUNCHD_LABEL"
    fi
    rm -f "$TARGET"
  fi
  echo "Stopped the $SERVICE_NAME service and removed $TARGET."
  echo "Its data stays where it was. To start it again: fleet node install-service"
}

# ── fleet node up ─────────────────────────────────────────────────────────────
# Asks how other machines should reach this one, installs or updates the service with that bind, waits until the node
# answers and prints a pairing link. It runs the same install-service as above, so there is one way to write the service.
tailscale_ip() {
  for cli in tailscale /Applications/Tailscale.app/Contents/MacOS/Tailscale; do
    if command -v "$cli" >/dev/null 2>&1; then
      "$cli" ip -4 2>/dev/null | head -n 1
      return 0
    fi
  done
}

# The host the installed service listens on, if there is one.
installed_host() {
  [ -f "$TARGET" ] || return 0
  if [ "$PLATFORM" = linux ]; then
    sed -n 's/.*"--host" "\([^"]*\)".*/\1/p' "$TARGET"
  else
    grep -A1 '<string>--host</string>' "$TARGET" | sed -n 's/.*<string>\([^<]*\)<\/string>.*/\1/p' | tail -n 1
  fi
}

# Asks the node for a pairing link: its output on success, nothing while it isn't answering.
ask_node_for_link() {
  "$APP_BIN" --node-pair --urls "$LISTEN_URL" --contentRoot "$APP_CONTENT_ROOT" 2>/dev/null
}

node_up_choose() {
  TAILSCALE_IP="$(tailscale_ip)"
  CURRENT_HOST="$(installed_host)"
  RECOMMENDED=ssh-only
  [ -n "$TAILSCALE_IP" ] && RECOMMENDED=tailscale
  case "$CURRENT_HOST" in
    "") ;;
    0.0.0.0) RECOMMENDED=lan ;;
    127.0.0.1) RECOMMENDED=ssh-only ;;
    "$TAILSCALE_IP") RECOMMENDED=tailscale ;;
  esac
  [ -n "$UP_CHOICE" ] && return 0
  if [ "$ASSUME_YES" -eq 1 ]; then
    UP_CHOICE="$RECOMMENDED"
    return 0
  fi
  if [ ! -t 0 ]; then
    echo "Error: fleet node up needs a choice when it can't ask. Add --tailscale, --lan or --ssh-only." >&2
    exit 1
  fi

  NAME="$(hostname 2>/dev/null | sed 's/\..*//')"
  echo "Set up ${NAME:-this machine} as a Fleet machine"
  echo "$(service_platform_name) · Fleet $VERSION"
  echo ""
  echo "How should your other machines reach ${NAME:-this machine}?"
  tailscale_note="only your devices can reach it"
  [ -n "$TAILSCALE_IP" ] || tailscale_note="needs Tailscale installed and signed in"
  # "(current)" marks what is installed; "(recommended)" Tailscale on a machine with nothing installed yet.
  mark_tailscale=""
  [ -n "$TAILSCALE_IP" ] && [ -z "$CURRENT_HOST" ] && mark_tailscale="  (recommended)"
  mark_lan=""
  mark_ssh=""
  case "$CURRENT_HOST" in
    "") ;;
    0.0.0.0) mark_lan="  (current)" ;;
    127.0.0.1) mark_ssh="  (current)" ;;
    "$TAILSCALE_IP") mark_tailscale="  (current)" ;;
  esac
  printf '  1) Tailscale        %s%s\n' "$tailscale_note" "$mark_tailscale"
  printf '  2) Local network    anyone on it can try a code%s\n' "$mark_lan"
  printf '  3) Only over SSH    listens on this machine only%s\n' "$mark_ssh"
  case "$RECOMMENDED" in tailscale) default=1 ;; lan) default=2 ;; *) default=3 ;; esac
  while :; do
    printf 'Choose 1-3 [%s]: ' "$default"
    read -r answer || { echo "" >&2; exit 1; }
    case "${answer:-$default}" in
      1) UP_CHOICE=tailscale; break ;;
      2) UP_CHOICE=lan; break ;;
      3) UP_CHOICE=ssh-only; break ;;
    esac
  done
  echo ""
}

service_platform_name() {
  case "$(uname -s)" in
    Darwin) echo macOS ;;
    *) uname -s ;;
  esac
}

node_up() {
  set_service_target
  node_up_choose
  case "$UP_CHOICE" in
    tailscale)
      if [ -z "$TAILSCALE_IP" ]; then
        if [ -z "$(command -v tailscale)" ] && [ ! -x /Applications/Tailscale.app/Contents/MacOS/Tailscale ]; then
          echo "Error: Tailscale isn't installed here. Install it from tailscale.com, run tailscale up, then fleet node up again." >&2
        else
          echo "Error: Tailscale isn't signed in. Run tailscale up, then fleet node up again." >&2
        fi
        exit 1
      fi
      HOST="$TAILSCALE_IP"
      ;;
    lan) HOST=0.0.0.0 ;;
    *) HOST=127.0.0.1 ;;
  esac
  LISTEN_URL="http://${HOST}:${PORT}"
  export ASPNETCORE_URLS="$LISTEN_URL" URLS="$LISTEN_URL" Fleet__Host="$HOST"
  set_service_args

  LINK_OUTPUT=""
  [ "$CURRENT_HOST" = "$HOST" ] && LINK_OUTPUT="$(ask_node_for_link)"
  if [ -n "$LINK_OUTPUT" ]; then
    echo "✓ Already running on ${HOST}:${PORT}"
  else
    install_service >/dev/null
    echo "✓ Listening on ${HOST}:${PORT}"
    echo "✓ Runs in the background, starts at login"
    attempts=0
    while LINK_OUTPUT="$(ask_node_for_link)"; [ -z "$LINK_OUTPUT" ]; do
      attempts=$((attempts + 1))
      if [ "$attempts" -ge 30 ]; then
        echo "Error: the machine didn't answer in 30 seconds. Look at the log: $(service_log_hint)" >&2
        exit 1
      fi
      sleep 1
    done
  fi
  echo "✓ Master token stays on this machine"
  echo ""
  echo "$LINK_OUTPUT"
  if [ "$UP_CHOICE" = ssh-only ]; then
    echo ""
    echo "From the Fleet you work from, open Settings → Machines → Add a machine → Over SSH."
  fi
}

service_log_hint() {
  if [ "$PLATFORM" = linux ]; then
    echo "journalctl --user -u $SERVICE_NAME"
  else
    echo "$HOME/Library/Logs/$SERVICE_NAME.log"
  fi
}

# `fleet node …` takes the same server options; it only has to come first.
NODE=0
NODE_PAIR=0
NODE_PAIR_JSON=""
NODE_UP=0
UP_CHOICE=""
ASSUME_YES=0
SERVICE_ACTION=""
if [ "${1:-}" = "node" ]; then
  NODE=1
  shift
  case "${1:-}" in
    install-service|uninstall-service)
      SERVICE_ACTION="$1"
      shift
      ;;
    pair)
      NODE_PAIR=1
      shift
      ;;
    up)
      NODE_UP=1
      shift
      ;;
  esac
fi
PRINT_ONLY=0

PORT_OVERRIDE=""
HOST_OVERRIDE=""
DATA_DIR_OVERRIDE=""
PROFILE_NAME=""
REQUIRE_TOKEN=0
EXTRA_ARGS=""

while [ "$#" -gt 0 ]; do
  case "$1" in
    version|--version|-v)
      if [ "$#" -ne 1 ]; then
        echo "Error: version does not accept additional arguments." >&2
        exit 1
      fi
      read_version
      exit 0
      ;;
    update)
      if [ "$#" -ne 1 ]; then
        echo "Error: update does not accept additional arguments." >&2
        exit 1
      fi
      echo "Updating Fleet..."
      if command -v curl >/dev/null 2>&1; then
        exec sh -c "curl -fsSL ${INSTALL_SCRIPT_URL} | sh"
      fi
      if command -v wget >/dev/null 2>&1; then
        exec sh -c "wget -qO- ${INSTALL_SCRIPT_URL} | sh"
      fi
      echo "Error: curl or wget is required to update." >&2
      exit 1
      ;;
    uninstall)
      if [ "$#" -ne 1 ]; then
        echo "Error: uninstall does not accept additional arguments." >&2
        exit 1
      fi
      if [ "$INSTALL_LAYOUT" -ne 1 ]; then
        echo "Error: uninstall is only supported from an installed package layout." >&2
        exit 1
      fi
      echo "Removing Fleet from $ROOT_DIR..."
      rm -rf "$ROOT_DIR"
      echo "Done. Remove any PATH entry that points at $ROOT_DIR/bin if needed."
      exit 0
      ;;
    help|--help|-h)
      if [ "$#" -ne 1 ]; then
        echo "Error: help does not accept additional arguments." >&2
        exit 1
      fi
      if [ "$NODE" -eq 1 ]; then
        show_node_help
      else
        show_help
      fi
      exit 0
      ;;
    import-legacy-sessions)
      shift
      EXTRA_ARGS="--import-legacy-sessions"
      while [ "$#" -gt 0 ]; do
        case "$1" in
          --source)
            if [ "$#" -lt 2 ]; then
              echo "Error: --source requires a value." >&2
              exit 1
            fi
            EXTRA_ARGS="$EXTRA_ARGS --source $2"
            shift 2
            ;;
          *)
            echo "Unknown option for import-legacy-sessions: $1" >&2
            exit 1
            ;;
        esac
      done
      break
      ;;
    --port)
      if [ "$#" -lt 2 ]; then
        echo "Error: --port requires a value." >&2
        exit 1
      fi
      PORT_OVERRIDE="$2"
      shift 2
      continue
      ;;
    --port=*)
      PORT_OVERRIDE="${1#--port=}"
      ;;
    --host)
      if [ "$#" -lt 2 ]; then
        echo "Error: --host requires a value." >&2
        exit 1
      fi
      HOST_OVERRIDE="$2"
      shift 2
      continue
      ;;
    --host=*)
      HOST_OVERRIDE="${1#--host=}"
      ;;
    --data-dir)
      if [ "$#" -lt 2 ]; then
        echo "Error: --data-dir requires a value." >&2
        exit 1
      fi
      DATA_DIR_OVERRIDE="$2"
      shift 2
      continue
      ;;
    --data-dir=*)
      DATA_DIR_OVERRIDE="${1#--data-dir=}"
      ;;
    --profile)
      if [ "$#" -lt 2 ]; then
        echo "Error: --profile requires a value." >&2
        exit 1
      fi
      PROFILE_NAME="$2"
      shift 2
      continue
      ;;
    --profile=*)
      PROFILE_NAME="${1#--profile=}"
      ;;
    --require-token)
      REQUIRE_TOKEN=1
      ;;
    --json)
      if [ "$NODE_PAIR" -ne 1 ]; then
        echo "Error: --json only works with fleet node pair." >&2
        exit 1
      fi
      NODE_PAIR_JSON="--json"
      ;;
    --tailscale|--lan|--ssh-only)
      if [ "$NODE_UP" -ne 1 ]; then
        echo "Error: $1 only works with fleet node up." >&2
        exit 1
      fi
      if [ -n "$UP_CHOICE" ] && [ "$UP_CHOICE" != "${1#--}" ]; then
        echo "Error: choose one of --tailscale, --lan and --ssh-only." >&2
        exit 1
      fi
      UP_CHOICE="${1#--}"
      ;;
    --yes|-y)
      if [ "$NODE_UP" -ne 1 ]; then
        echo "Error: $1 only works with fleet node up." >&2
        exit 1
      fi
      ASSUME_YES=1
      ;;
    --print)
      if [ "$SERVICE_ACTION" != "install-service" ]; then
        echo "Error: --print only works with fleet node install-service." >&2
        exit 1
      fi
      PRINT_ONLY=1
      ;;
    *)
      echo "Unknown command or option: $1" >&2
      echo "Run 'fleet help' for usage." >&2
      exit 1
      ;;
  esac

  shift
done

if [ -n "$PORT_OVERRIDE" ]; then
  case "$PORT_OVERRIDE" in
    *[!0-9]*|"")
      echo "Error: --port must be a numeric value." >&2
      exit 1
      ;;
  esac
fi

if [ -n "$PROFILE_NAME" ]; then
  case "$PROFILE_NAME" in
    *[!A-Za-z0-9._-]*|"")
      echo "Error: --profile may only contain letters, numbers, dots, underscores, and hyphens." >&2
      exit 1
      ;;
  esac
fi

VERSION="$(read_version)"
PORT="${PORT_OVERRIDE:-${WEAVE_FLEET_PORT:-6262}}"
HOST="${HOST_OVERRIDE:-${WEAVE_FLEET_HOST:-127.0.0.1}}"
LISTEN_URL="http://${HOST}:${PORT}"
DATA_DIR="${DATA_DIR_OVERRIDE:-${WEAVE_FLEET_DATA_DIR:-${HOME}/.weave}}"
if [ -n "$PROFILE_NAME" ]; then
  DATA_DIR="$DATA_DIR/profiles/$PROFILE_NAME"
fi

if [ "$NODE_UP" -eq 1 ] && [ -n "$HOST_OVERRIDE" ]; then
  echo "Error: fleet node up picks the host from your choice. Use --tailscale, --lan or --ssh-only." >&2
  exit 1
fi

if [ -n "$SERVICE_ACTION" ]; then
  if [ "$SERVICE_ACTION" = "uninstall-service" ]; then
    if [ -n "$PORT_OVERRIDE$HOST_OVERRIDE$DATA_DIR_OVERRIDE$PROFILE_NAME" ]; then
      echo "Error: uninstall-service does not accept options." >&2
      exit 1
    fi
    uninstall_service
    exit 0
  fi
  set_service_args
  install_service
  exit 0
fi

DB_PATH_DEFAULT="$DATA_DIR/fleet.db"
ANALYTICS_DB_PATH_DEFAULT="$DATA_DIR/fleet-analytics.db"
KEY_DIR_DEFAULT="$DATA_DIR/fleet-keys"

mkdir -p "$DATA_DIR"
mkdir -p "$KEY_DIR_DEFAULT"

export ASPNETCORE_ENVIRONMENT=Production
export ASPNETCORE_URLS="$LISTEN_URL"
export URLS="$LISTEN_URL"
export ASPNETCORE_CONTENTROOT="$APP_CONTENT_ROOT"
export Fleet__Host="$HOST"
export Fleet__Port="$PORT"
export Fleet__DatabasePath="${Fleet__DatabasePath:-$DB_PATH_DEFAULT}"
export Fleet__AnalyticsDatabasePath="${Fleet__AnalyticsDatabasePath:-$ANALYTICS_DB_PATH_DEFAULT}"
export Fleet__DataProtection__KeyPath="${Fleet__DataProtection__KeyPath:-$KEY_DIR_DEFAULT}"
if [ "$REQUIRE_TOKEN" -eq 1 ]; then
  export Fleet__Auth__RequireToken=true
fi
if [ "$NODE_UP" -eq 1 ]; then
  node_up
  exit 0
fi
if [ "$NODE_PAIR" -eq 1 ]; then
  # Asks the node already running with these settings for a code, prints it and stops.
  exec "$APP_BIN" --node-pair $NODE_PAIR_JSON --urls "$LISTEN_URL" --contentRoot "$APP_CONTENT_ROOT"
fi
if [ "$NODE" -eq 1 ]; then
  EXTRA_ARGS="$EXTRA_ARGS --node"
  echo "Fleet v${VERSION} starting as a node on ${LISTEN_URL}"
else
  echo "Fleet v${VERSION} starting on ${LISTEN_URL}"
fi

if [ "$INSTALL_LAYOUT" -ne 1 ]; then
  exec "$APP_BIN" --urls "$LISTEN_URL" --contentRoot "$APP_CONTENT_ROOT" $EXTRA_ARGS
fi

# ── Run the app, and restart it into an update when it asks ──────────────────
# An installed Fleet runs as a child so it can restart itself: exit code 85 means "restart to update", and the launcher
# starts itself again, so the launcher that comes with the update applies it. Any other exit code is passed on as it is.
# A version that was just applied is on trial: if it exits before deleting update-trial.json, or doesn't within
# FLEET_UPDATE_TRIAL_SECONDS, the launcher puts the old version back (and its database files) and starts that.
TRIAL_SECONDS="${FLEET_UPDATE_TRIAL_SECONDS:-120}"
export FLEET_LAUNCHER_RESTART=85

json_field() {
  sed -n "s/.*\"$1\"[[:space:]]*:[[:space:]]*\"\([^\"]*\)\".*/\1/p" "$2" | sed -n '1p'
}

# The database files as they were before the new version first ran (and may have migrated them).
backup_database() {
  if [ -f "$TRIAL_FILE" ] && [ ! -f "$PREV_DB_DIR/.copied" ]; then
    mkdir -p "$PREV_DB_DIR"
    for suffix in "" "-wal" "-shm"; do
      [ -f "$Fleet__DatabasePath$suffix" ] && cp -p "$Fleet__DatabasePath$suffix" "$PREV_DB_DIR/fleet.db$suffix"
    done
    : > "$PREV_DB_DIR/.copied"
  fi
  return 0
}

rollback_update() {
  trial_from="$(json_field from "$TRIAL_FILE")"
  trial_to="$(json_field to "$TRIAL_FILE")"
  echo "Fleet v${trial_to} didn't start properly ($1). Going back to v${trial_from}." >&2
  if [ -d "$ROOT_DIR/app.prev" ]; then rm -rf "$ROOT_DIR/app"; mv "$ROOT_DIR/app.prev" "$ROOT_DIR/app"; fi
  if [ -d "$ROOT_DIR/bin.prev" ]; then rm -rf "$ROOT_DIR/bin"; mv "$ROOT_DIR/bin.prev" "$ROOT_DIR/bin"; fi
  if [ -f "$ROOT_DIR/VERSION.prev" ]; then mv "$ROOT_DIR/VERSION.prev" "$VERSION_FILE"; fi
  if [ -f "$PREV_DB_DIR/.copied" ]; then
    for suffix in "" "-wal" "-shm"; do
      if [ -f "$PREV_DB_DIR/fleet.db$suffix" ]; then
        cp -p "$PREV_DB_DIR/fleet.db$suffix" "$Fleet__DatabasePath$suffix"
      else
        rm -f "$Fleet__DatabasePath$suffix"
      fi
    done
  fi
  printf '{"from":"%s","to":"%s","reason":"%s","at":"%s"}\n' "$trial_from" "$trial_to" "$1" "$(date -u +%Y-%m-%dT%H:%M:%SZ)" > "$FAILED_FILE"
  rm -f "$TRIAL_FILE"
}

# Stops the app when it hasn't confirmed the trial in time.
trial_watchdog() {
  elapsed=0
  while [ "$elapsed" -lt "$TRIAL_SECONDS" ]; do
    sleep 1
    elapsed=$((elapsed + 1))
    [ -f "$TRIAL_FILE" ] || return 0
    kill -0 "$APP_PID" 2>/dev/null || return 0
  done
  : > "$PREV_DB_DIR/.timeout"
  kill -TERM "$APP_PID" 2>/dev/null
  sleep 10
  kill -KILL "$APP_PID" 2>/dev/null
  return 0
}

# What it takes to start this launcher again the way this run was started.
restart_launcher() {
  if [ "$NODE" -eq 1 ]; then set -- node; else set --; fi
  set -- "$@" --port "$PORT" --host "$HOST" --data-dir "$DATA_DIR"
  if [ "$REQUIRE_TOKEN" -eq 1 ]; then set -- "$@" --require-token; fi
  echo "Restarting Fleet to finish updating..."
  exec "$SCRIPT_DIR/$(basename "$0")" "$@"
}

APP_PID=""
SIGNALLED=0
# Stopping the launcher stops the app, and the launcher waits for it to finish.
trap 'SIGNALLED=1; [ -z "$APP_PID" ] || kill -TERM "$APP_PID" 2>/dev/null' TERM INT

while true; do
  backup_database
  rm -f "$PREV_DB_DIR/.timeout"
  "$APP_BIN" --urls "$LISTEN_URL" --contentRoot "$APP_CONTENT_ROOT" $EXTRA_ARGS &
  APP_PID=$!
  WATCHDOG=""
  if [ -f "$TRIAL_FILE" ]; then
    trial_watchdog &
    WATCHDOG=$!
  fi

  APP_CODE=0
  wait "$APP_PID" || APP_CODE=$?
  # A signal ends the first wait early; the app is still shutting down.
  while kill -0 "$APP_PID" 2>/dev/null; do
    wait "$APP_PID" || APP_CODE=$?
  done
  [ -z "$WATCHDOG" ] || kill "$WATCHDOG" 2>/dev/null || true
  APP_PID=""

  if [ -f "$TRIAL_FILE" ] && [ "$SIGNALLED" -eq 0 ]; then
    if [ -f "$PREV_DB_DIR/.timeout" ]; then
      rollback_update "it wasn't ready after ${TRIAL_SECONDS} seconds"
    else
      rollback_update "it exited with code ${APP_CODE} before it was ready"
    fi
    continue
  fi
  if [ "$APP_CODE" -eq 85 ] && [ "$SIGNALLED" -eq 0 ]; then
    restart_launcher
  fi
  exit "$APP_CODE"
done
