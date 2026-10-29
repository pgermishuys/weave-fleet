#!/usr/bin/env sh
set -eu

REPO="${WEAVE_FLEET_GITHUB_REPO:-pgermishuys/weave-fleet}"
INSTALL_DIR="${WEAVE_FLEET_INSTALL_DIR:-$HOME/.weave/fleet}"
PROFILE_FILE_OVERRIDE="${WEAVE_FLEET_PROFILE_FILE:-}"
SKIP_PATH_UPDATE="${WEAVE_FLEET_SKIP_PATH_UPDATE:-0}"
CHECKSUMS_NAME="${WEAVE_FLEET_CHECKSUMS_NAME:-checksums.txt}"

WORK_DIR=""

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

download_file() {
  url="$1"
  output_path="$2"

  if command -v curl >/dev/null 2>&1; then
    curl -fsSL "$url" -o "$output_path"
    return 0
  fi

  if command -v wget >/dev/null 2>&1; then
    wget -qO "$output_path" "$url"
    return 0
  fi

  fail "Error: curl or wget is required to download Fleet."
}

try_download_file() {
  url="$1"
  output_path="$2"

  if command -v curl >/dev/null 2>&1; then
    curl -fsSL "$url" -o "$output_path" 2>/dev/null
    return $?
  fi

  if command -v wget >/dev/null 2>&1; then
    wget -qO "$output_path" "$url" 2>/dev/null
    return $?
  fi

  return 1
}

download_text() {
  url="$1"

  if command -v curl >/dev/null 2>&1; then
    curl -fsSL "$url"
    return 0
  fi

  if command -v wget >/dev/null 2>&1; then
    wget -qO- "$url"
    return 0
  fi

  fail "Error: curl or wget is required to download Fleet."
}

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

detect_os() {
  case "$(uname -s)" in
    Darwin)
      printf 'osx\n'
      ;;
    Linux)
      printf 'linux\n'
      ;;
    *)
      fail "Error: unsupported operating system: $(uname -s)"
      ;;
  esac
}

detect_arch() {
  case "$(uname -m)" in
    x86_64|amd64)
      printf 'x64\n'
      ;;
    arm64|aarch64)
      printf 'arm64\n'
      ;;
    *)
      fail "Error: unsupported architecture: $(uname -m)"
      ;;
  esac
}

resolve_tag() {
  if [ -n "${WEAVE_FLEET_VERSION:-}" ]; then
    normalize_tag "$WEAVE_FLEET_VERSION"
    return 0
  fi

  release_api_url="${WEAVE_FLEET_RELEASE_API_URL:-https://api.github.com/repos/${REPO}/releases/latest}"
  release_metadata="$(download_text "$release_api_url")"
  release_tag="$(printf '%s' "$release_metadata" | tr -d '\n' | sed -n 's/.*"tag_name"[[:space:]]*:[[:space:]]*"\([^"]*\)".*/\1/p')"

  if [ -z "$release_tag" ]; then
    fail "Error: could not determine the latest Fleet release tag."
  fi

  normalize_tag "$release_tag"
}

resolve_download_base_url() {
  release_tag="$1"

  if [ -n "${WEAVE_FLEET_DOWNLOAD_BASE_URL:-}" ]; then
    printf '%s\n' "$WEAVE_FLEET_DOWNLOAD_BASE_URL"
    return 0
  fi

  printf 'https://github.com/%s/releases/download/%s\n' "$REPO" "$release_tag"
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

extract_hash_from_checksum_file() {
  checksum_path="$1"

  sed -n 's/^\([0-9A-Fa-f][0-9A-Fa-f]*\).*/\1/p' "$checksum_path" | sed -n '1p'
}

extract_hash_from_checksums_manifest() {
  manifest_path="$1"
  asset_name="$2"

  while IFS= read -r line; do
    case "$line" in
      *"$asset_name")
        printf '%s\n' "$line" | sed -n 's/^\([0-9A-Fa-f][0-9A-Fa-f]*\).*/\1/p'
        return 0
        ;;
    esac
  done < "$manifest_path"

  return 1
}

resolve_expected_hash() {
  asset_name="$1"
  download_base_url="$2"
  checksum_file_path="$WORK_DIR/$asset_name.sha256"
  checksums_manifest_path="$WORK_DIR/$CHECKSUMS_NAME"

  if try_download_file "$download_base_url/$asset_name.sha256" "$checksum_file_path"; then
    expected_hash="$(extract_hash_from_checksum_file "$checksum_file_path")"
    if [ -n "$expected_hash" ]; then
      printf '%s\n' "$expected_hash"
      return 0
    fi
  fi

  if try_download_file "$download_base_url/$CHECKSUMS_NAME" "$checksums_manifest_path"; then
    expected_hash="$(extract_hash_from_checksums_manifest "$checksums_manifest_path" "$asset_name" || true)"
    if [ -n "$expected_hash" ]; then
      printf '%s\n' "$expected_hash"
      return 0
    fi
  fi

  fail "Error: could not locate a checksum for $asset_name."
}

ensure_tar_available() {
  if ! command -v tar >/dev/null 2>&1; then
    fail "Error: tar is required to extract the Fleet archive."
  fi
}

find_package_root() {
  extract_dir="$1"

  if [ -d "$extract_dir/app" ] && [ -d "$extract_dir/bin" ]; then
    printf '%s\n' "$extract_dir"
    return 0
  fi

  set -- "$extract_dir"/*
  if [ "$#" -eq 1 ] && [ -d "$1" ] && [ -d "$1/app" ] && [ -d "$1/bin" ]; then
    printf '%s\n' "$1"
    return 0
  fi

  fail "Error: extracted archive did not contain the expected Fleet package layout."
}

select_profile_file() {
  if [ -n "$PROFILE_FILE_OVERRIDE" ]; then
    printf '%s\n' "$PROFILE_FILE_OVERRIDE"
    return 0
  fi

  shell_name="$(basename "${SHELL:-sh}")"
  case "$shell_name" in
    zsh)
      if [ -f "$HOME/.zprofile" ] || [ ! -f "$HOME/.zshrc" ]; then
        printf '%s/.zprofile\n' "$HOME"
      else
        printf '%s/.zshrc\n' "$HOME"
      fi
      ;;
    bash)
      if [ -f "$HOME/.bash_profile" ] || [ ! -f "$HOME/.profile" ]; then
        printf '%s/.bash_profile\n' "$HOME"
      else
        printf '%s/.profile\n' "$HOME"
      fi
      ;;
    *)
      printf '%s/.profile\n' "$HOME"
      ;;
  esac
}

update_path() {
  bin_dir="$1"
  profile_path="$(select_profile_file)"
  path_line="export PATH=\"$bin_dir:\$PATH\""

  if [ "$SKIP_PATH_UPDATE" = "1" ]; then
    log "Skipping PATH update because WEAVE_FLEET_SKIP_PATH_UPDATE=1."
    return 0
  fi

  if [ -f "$profile_path" ] && grep -F "$bin_dir" "$profile_path" >/dev/null 2>&1; then
    log "PATH already includes $bin_dir in $profile_path."
    return 0
  fi

  printf '\n# Fleet\n%s\n' "$path_line" >> "$profile_path"
  log "Added $bin_dir to PATH in $profile_path."
}

main() {
  ensure_tar_available

  rid="$(detect_os)-$(detect_arch)"
  release_tag="$(resolve_tag)"
  download_base_url="$(resolve_download_base_url "$release_tag")"
  asset_name="fleet-${release_tag}-${rid}.tar.gz"

  WORK_DIR="$(mktemp -d "${TMPDIR:-/tmp}/fleet-install.XXXXXX")"
  archive_path="$WORK_DIR/$asset_name"
  extract_dir="$WORK_DIR/extracted"
  mkdir -p "$extract_dir"

  log "Installing Fleet $release_tag for $rid..."
  download_file "$download_base_url/$asset_name" "$archive_path"

  expected_hash="$(resolve_expected_hash "$asset_name" "$download_base_url")"
  actual_hash="$(compute_sha256 "$archive_path")"
  expected_hash_lower="$(printf '%s' "$expected_hash" | tr '[:upper:]' '[:lower:]')"
  actual_hash_lower="$(printf '%s' "$actual_hash" | tr '[:upper:]' '[:lower:]')"

  if [ "$expected_hash_lower" != "$actual_hash_lower" ]; then
    fail "Error: checksum verification failed for $asset_name."
  fi

  tar -xzf "$archive_path" -C "$extract_dir"
  package_root="$(find_package_root "$extract_dir")"

  mkdir -p "$(dirname "$INSTALL_DIR")"
  rm -rf "$INSTALL_DIR"
  mkdir -p "$INSTALL_DIR"
  cp -R "$package_root"/. "$INSTALL_DIR"/

  if [ -f "$INSTALL_DIR/bin/fleet" ]; then
    chmod +x "$INSTALL_DIR/bin/fleet"
  fi

  if [ -f "$INSTALL_DIR/app/WeaveFleet.Api" ]; then
    chmod +x "$INSTALL_DIR/app/WeaveFleet.Api"
  fi

  update_path "$INSTALL_DIR/bin"

  log "Fleet installed to $INSTALL_DIR."
  log "Open a new shell or run: export PATH=\"$INSTALL_DIR/bin:\$PATH\""
  log "Then start it with: fleet"
}

main "$@"
