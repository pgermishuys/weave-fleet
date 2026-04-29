#!/usr/bin/env sh
set -eu

SCRIPT_DIR="$(CDPATH= cd -- "$(dirname -- "$0")" && pwd)"
ROOT_DIR="$(CDPATH= cd -- "$SCRIPT_DIR/.." && pwd)"
DEFAULT_OUTPUT_DIR="$ROOT_DIR"
DEFAULT_PACKAGE_NAME="fleet"

WORK_DIR=""

usage() {
  printf '%s\n' "Usage: $(basename "$0") --publish-dir <path> --rid <rid> [--output-dir <path>] [--version <version>] [--package-name <name>]"
}

log() {
  printf '%s\n' "$*"
}

fail() {
  printf '%s\n' "$*" >&2
  exit 1
}

cleanup() {
  if [ -n "$WORK_DIR" ] && [ -d "$WORK_DIR" ]; then
    rm -rf "$WORK_DIR"
  fi
}

trap cleanup EXIT INT TERM

normalize_tag() {
  case "$1" in
    v*)
      printf '%s\n' "$1"
      ;;
    *)
      printf 'v%s\n' "$1"
      ;;
  esac
}

read_version_from_props() {
  version_line="$(sed -n 's/.*<Version>\([^<]*\)<\/Version>.*/\1/p' "$ROOT_DIR/Directory.Build.props" | sed -n '1p')"
  if [ -z "$version_line" ]; then
    fail "Error: could not determine version from $ROOT_DIR/Directory.Build.props."
  fi

  printf '%s\n' "$version_line"
}

compute_sha256() {
  file_path="$1"

  if command -v sha256sum >/dev/null 2>&1; then
    sha256sum "$file_path" | sed 's/[[:space:]].*$//'
    return 0
  fi

  if command -v shasum >/dev/null 2>&1; then
    shasum -a 256 "$file_path" | sed 's/[[:space:]].*$//'
    return 0
  fi

  if command -v openssl >/dev/null 2>&1; then
    openssl dgst -sha256 "$file_path" | sed 's/^.*= //'
    return 0
  fi

  fail "Error: no SHA-256 tool found (expected sha256sum, shasum, or openssl)."
}

require_command() {
  command_name="$1"
  if ! command -v "$command_name" >/dev/null 2>&1; then
    fail "Error: required command not found: $command_name"
  fi
}

require_non_empty_directory() {
  directory_path="$1"

  if [ ! -d "$directory_path" ]; then
    fail "Error: publish directory does not exist: $directory_path"
  fi

  if [ -z "$(ls -A "$directory_path")" ]; then
    fail "Error: publish directory is empty: $directory_path"
  fi
}

PUBLISH_DIR=""
RID=""
OUTPUT_DIR="$DEFAULT_OUTPUT_DIR"
VERSION=""
PACKAGE_NAME="$DEFAULT_PACKAGE_NAME"

while [ "$#" -gt 0 ]; do
  case "$1" in
    --publish-dir)
      [ "$#" -ge 2 ] || fail "Error: --publish-dir requires a value."
      PUBLISH_DIR="$2"
      shift 2
      ;;
    --rid)
      [ "$#" -ge 2 ] || fail "Error: --rid requires a value."
      RID="$2"
      shift 2
      ;;
    --output-dir)
      [ "$#" -ge 2 ] || fail "Error: --output-dir requires a value."
      OUTPUT_DIR="$2"
      shift 2
      ;;
    --version)
      [ "$#" -ge 2 ] || fail "Error: --version requires a value."
      VERSION="$2"
      shift 2
      ;;
    --package-name)
      [ "$#" -ge 2 ] || fail "Error: --package-name requires a value."
      PACKAGE_NAME="$2"
      shift 2
      ;;
    -h|--help)
      usage
      exit 0
      ;;
    *)
      usage >&2
      fail "Error: unknown argument: $1"
      ;;
  esac
done

[ -n "$PUBLISH_DIR" ] || fail "Error: --publish-dir is required."
[ -n "$RID" ] || fail "Error: --rid is required."

require_command tar
require_non_empty_directory "$PUBLISH_DIR"
mkdir -p "$OUTPUT_DIR"

if [ -z "$VERSION" ]; then
  VERSION="$(read_version_from_props)"
fi

RELEASE_TAG="$(normalize_tag "$VERSION")"
ASSET_BASE_NAME="${PACKAGE_NAME}-${RELEASE_TAG}-${RID}"
ARCHIVE_NAME="${ASSET_BASE_NAME}.tar.gz"
ARCHIVE_PATH="$OUTPUT_DIR/$ARCHIVE_NAME"
CHECKSUM_PATH="${ARCHIVE_PATH}.sha256"

WORK_DIR="$(mktemp -d "${TMPDIR:-/tmp}/fleet-package.XXXXXX")"
PACKAGE_ROOT="$WORK_DIR/$ASSET_BASE_NAME"
PACKAGE_BIN_DIR="$PACKAGE_ROOT/bin"
PACKAGE_APP_DIR="$PACKAGE_ROOT/app"

mkdir -p "$PACKAGE_BIN_DIR" "$PACKAGE_APP_DIR"

cp "$SCRIPT_DIR/launcher.sh" "$PACKAGE_BIN_DIR/fleet"
chmod +x "$PACKAGE_BIN_DIR/fleet"

cp -R "$PUBLISH_DIR"/. "$PACKAGE_APP_DIR"/
if [ -f "$PACKAGE_APP_DIR/WeaveFleet.Api" ]; then
  chmod +x "$PACKAGE_APP_DIR/WeaveFleet.Api"
fi

printf '%s\n' "$VERSION" > "$PACKAGE_ROOT/VERSION"

rm -f "$ARCHIVE_PATH" "$CHECKSUM_PATH"
tar -czf "$ARCHIVE_PATH" -C "$WORK_DIR" "$ASSET_BASE_NAME"

sha256_value="$(compute_sha256 "$ARCHIVE_PATH")"
printf '%s  %s\n' "$sha256_value" "$ARCHIVE_NAME" > "$CHECKSUM_PATH"

log "Created $ARCHIVE_PATH"
log "Created $CHECKSUM_PATH"
