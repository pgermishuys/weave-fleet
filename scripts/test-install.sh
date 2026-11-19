#!/usr/bin/env bash
# Runs scripts/install.sh against a dummy release served from disk (file://) and checks it installs and ends by
# telling someone what to run next for both kinds of machine: `fleet` (has a screen) and `fleet node up` (none).
set -euo pipefail
export LC_ALL=C

SCRIPT_DIR="$(cd "$(dirname "$0")" && pwd)"
WORK="$(mktemp -d)"
trap 'rm -rf "$WORK"' EXIT

fail() { echo "FAIL: $*" >&2; exit 1; }

case "$(uname -s)" in Darwin) os=osx ;; *) os=linux ;; esac
case "$(uname -m)" in arm64|aarch64) arch=arm64 ;; *) arch=x64 ;; esac
asset="fleet-v0.0.1-${os}-${arch}.tar.gz"

mkdir -p "$WORK/pkg/app" "$WORK/pkg/bin" "$WORK/release"
printf '#!/bin/sh\necho fleet\n' > "$WORK/pkg/bin/fleet"
echo "dummy" > "$WORK/pkg/app/WeaveFleet.Api"
tar -czf "$WORK/release/$asset" -C "$WORK/pkg" app bin
(cd "$WORK/release" && sha256sum "$asset" > "$asset.sha256" 2>/dev/null || shasum -a 256 "$asset" > "$asset.sha256")

mkdir -p "$WORK/home"
url_path="$WORK"; if command -v cygpath >/dev/null 2>&1; then url_path="/$(cygpath -m "$WORK")"; fi
out="$(HOME="$WORK/home" WEAVE_FLEET_VERSION=v0.0.1 WEAVE_FLEET_DOWNLOAD_BASE_URL="file://$url_path/release" \
  WEAVE_FLEET_INSTALL_DIR="$WORK/home/fleet" WEAVE_FLEET_PROFILE_FILE="$WORK/home/profile" \
  sh "$SCRIPT_DIR/install.sh" 2>&1)" || fail "install.sh failed: $out"
echo "$out"

[ -x "$WORK/home/fleet/bin/fleet" ] || fail "fleet wasn't installed"
printf '%s\n' "$out" | grep -qE '^  fleet +a machine you sit at' || fail "no next step for a machine you sit at"
printf '%s\n' "$out" | grep -qE '^  fleet node up +a machine with no screen' || fail "no next step for a machine with no screen"
echo "install.sh ends with the next step for both kinds of machine"
