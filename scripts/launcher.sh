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
  UPDATE_VERSION=""
  ASSET_FILE=""
  while IFS= read -r line; do
    case "$line" in
      *'"version"'*)
        UPDATE_VERSION="$(printf '%s' "$line" | sed 's/.*"version"[[:space:]]*:[[:space:]]*"\([^"]*\)".*/\1/')"
        ;;
      *'"assetFileName"'*)
        ASSET_FILE="$(printf '%s' "$line" | sed 's/.*"assetFileName"[[:space:]]*:[[:space:]]*"\([^"]*\)".*/\1/')"
        ;;
    esac
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
  echo "Usage: fleet [command] [--port <port>] [--host <host>] [--data-dir <path>] [--profile <name>]"
  echo ""
  echo "Commands:"
  echo "  (none)       Start the Fleet server"
  echo "  version      Print the installed version"
  echo "  update       Update to the latest version"
  echo "  uninstall    Remove Fleet"
  echo "  help         Show this help message"
  echo ""
echo "Options when starting the server:"
echo "  --port <port>       Override the server port"
echo "  --host <host>       Override the bind host"
echo "  --data-dir <path>   Override the data directory (default: ~/.weave)"
echo "  --profile <name>    Use a profile-specific data directory"
  echo ""
echo "Environment variables:"
echo "  WEAVE_FLEET_PORT                Server port (default: 5000)"
echo "  WEAVE_FLEET_HOST                Bind host (default: 127.0.0.1)"
echo "  WEAVE_FLEET_DATA_DIR            Data directory (default: ~/.weave)"
echo "  Fleet__DatabasePath             SQLite database path override"
  echo "  Fleet__AnalyticsDatabasePath    Analytics database path override"
  echo "  Fleet__DataProtection__KeyPath  Data protection key directory override"
}

PORT_OVERRIDE=""
HOST_OVERRIDE=""
DATA_DIR_OVERRIDE=""
PROFILE_NAME=""

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
      show_help
      exit 0
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
PORT="${PORT_OVERRIDE:-${WEAVE_FLEET_PORT:-5000}}"
HOST="${HOST_OVERRIDE:-${WEAVE_FLEET_HOST:-127.0.0.1}}"
LISTEN_URL="http://${HOST}:${PORT}"
DATA_DIR="${DATA_DIR_OVERRIDE:-${WEAVE_FLEET_DATA_DIR:-${HOME}/.weave}}"
if [ -n "$PROFILE_NAME" ]; then
  DATA_DIR="$DATA_DIR/profiles/$PROFILE_NAME"
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

echo "Fleet v${VERSION} starting on ${LISTEN_URL}"
exec "$APP_BIN" --urls "$LISTEN_URL" --contentRoot "$APP_CONTENT_ROOT"
