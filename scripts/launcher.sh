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
  echo "Usage: fleet [command] [--port <port>] [--profile <name>]"
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
  echo "  --profile <name>    Use a profile-specific data directory"
  echo ""
  echo "Environment variables:"
  echo "  WEAVE_FLEET_PORT                Server port (default: 5000)"
  echo "  WEAVE_FLEET_HOST                Bind host (default: 127.0.0.1)"
  echo "  Fleet__DatabasePath             SQLite database path override"
  echo "  Fleet__AnalyticsDatabasePath    Analytics database path override"
  echo "  Fleet__DataProtection__KeyPath  Data protection key directory override"
}

PORT_OVERRIDE=""
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
HOST="${WEAVE_FLEET_HOST:-127.0.0.1}"
LISTEN_URL="http://${HOST}:${PORT}"
DATA_DIR="${HOME}/.weave"
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
