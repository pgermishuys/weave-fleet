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
INSTALL_SCRIPT_URL="${WEAVE_FLEET_INSTALL_SCRIPT_URL:-https://github.com/pgermishuys/fleet-releases/releases/latest/download/install.sh}"

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

  # Clean up.
  rm -rf "$APP_BAK" "$UPDATE_DIR"

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
  echo "Usage: fleet node [--port <port>] [--host <host>] [--data-dir <path>] [--profile <name>]"
  echo "       fleet node install-service [--port <port>] [--host <host>] [--data-dir <path>] [--profile <name>] [--print]"
  echo "       fleet node uninstall-service"
  echo ""
  echo "Starts Fleet as a node: the API without the web app. Every request needs the access token,"
  echo "even from this machine. Add the node to another Fleet in Settings > Machines > Add a machine."
  echo ""
  echo "Commands:"
  echo "  install-service     Keep the node running as you: now, when you log in, and if it stops"
  echo "                      (a systemd user service on Linux, a LaunchAgent on macOS)"
  echo "  uninstall-service   Stop the node and remove that service"
  echo ""
  echo "Options:"
  echo "  --port <port>       Override the server port"
  echo "  --host <host>       Override the bind host (0.0.0.0 lets other machines connect)"
  echo "  --data-dir <path>   Override the data directory (default: ~/.weave)"
  echo "  --profile <name>    Use a profile-specific data directory"
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

install_service() {
  PLATFORM="$(service_platform)" || exit 1
  if [ "$PLATFORM" = linux ]; then
    TARGET="$(systemd_unit_path)"
  else
    TARGET="$(launchd_plist_path)"
  fi

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
  PLATFORM="$(service_platform)" || exit 1
  if [ "$PLATFORM" = linux ]; then
    TARGET="$(systemd_unit_path)"
  else
    TARGET="$(launchd_plist_path)"
  fi
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

# `fleet node …` takes the same server options; it only has to come first.
NODE=0
SERVICE_ACTION=""
if [ "${1:-}" = "node" ]; then
  NODE=1
  shift
  case "${1:-}" in
    install-service|uninstall-service)
      SERVICE_ACTION="$1"
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

if [ -n "$SERVICE_ACTION" ]; then
  if [ "$SERVICE_ACTION" = "uninstall-service" ]; then
    if [ -n "$PORT_OVERRIDE$HOST_OVERRIDE$DATA_DIR_OVERRIDE$PROFILE_NAME" ]; then
      echo "Error: uninstall-service does not accept options." >&2
      exit 1
    fi
    uninstall_service
    exit 0
  fi
  # The service gets what this run resolved, so it starts the same node whatever its own environment holds.
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
if [ "$NODE" -eq 1 ]; then
  EXTRA_ARGS="$EXTRA_ARGS --node"
  echo "Fleet v${VERSION} starting as a node on ${LISTEN_URL}"
else
  echo "Fleet v${VERSION} starting on ${LISTEN_URL}"
fi
exec "$APP_BIN" --urls "$LISTEN_URL" --contentRoot "$APP_CONTENT_ROOT" $EXTRA_ARGS
