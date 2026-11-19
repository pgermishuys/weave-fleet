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

# ---- --join '<address>#CODE': install, start the machine with no screen, join the Fleet ----
# bin/fleet here is a stand-in that records what it was asked and answers the way the real one does: `node up` prints its
# steps and then a pairing link, `node join` succeeds for the right code and says what to do for an expired or used one.
mkdir -p "$WORK/jpkg/app" "$WORK/jpkg/bin" "$WORK/jrelease"
cat > "$WORK/jpkg/bin/fleet" <<'STUB'
#!/bin/sh
echo "$*" >> "$FLEET_TEST_CALLS"
case "$1 $2" in
  "node up")
    echo "Checking what atlas has"
    echo "✓ Listening on Tailscale (100.64.0.9) and your local network, port 6262"
    echo "✓ Runs in the background, starts at login"
    echo "✓ Master token stays on this machine"
    echo ""
    echo "Pairing link for atlas"
    echo "    http://100.64.0.9:6262/add-machine#PAIR-CODE0"
    ;;
  "node join")
    case "$3" in
      *"#J8RT-4WXN") echo "✓ Added to studio" ;;
      *"#USED-CODE0") echo "Couldn't join: This join code has expired or was already used. Make a new one in Add a machine → On a new server." >&2; exit 1 ;;
    esac
    ;;
esac
STUB
echo "dummy" > "$WORK/jpkg/app/WeaveFleet.Api"
tar -czf "$WORK/jrelease/$asset" -C "$WORK/jpkg" app bin
(cd "$WORK/jrelease" && sha256sum "$asset" > "$asset.sha256" 2>/dev/null || shasum -a 256 "$asset" > "$asset.sha256")

run_join() {
  rm -rf "$WORK/jhome"; mkdir -p "$WORK/jhome"; : > "$WORK/calls"
  HOME="$WORK/jhome" FLEET_TEST_CALLS="$WORK/calls" WEAVE_FLEET_VERSION=v0.0.1 WEAVE_FLEET_DOWNLOAD_BASE_URL="file://$url_path/jrelease" \
    WEAVE_FLEET_INSTALL_DIR="$WORK/jhome/fleet" WEAVE_FLEET_PROFILE_FILE="$WORK/jhome/profile" \
    sh "$SCRIPT_DIR/install.sh" "$@" 2>&1
}

# The good line: installs, starts the machine, joins, and prints each step in that order.
out="$(run_join --join 'http://100.64.0.5:6262,http://192.168.1.20:6262#J8RT-4WXN')" || fail "install.sh --join failed: $out"
echo "$out"
[ -x "$WORK/jhome/fleet/bin/fleet" ] || fail "--join didn't install fleet"
[ "$(sed -n 1p "$WORK/calls")" = "node up --yes" ] || fail "--join didn't start the machine first: $(cat "$WORK/calls")"
[ "$(sed -n 2p "$WORK/calls")" = "node join http://100.64.0.5:6262,http://192.168.1.20:6262#J8RT-4WXN" ] || fail "--join didn't join with the line as given: $(cat "$WORK/calls")"
[ "$(wc -l < "$WORK/calls" | tr -d ' ')" = 2 ] || fail "--join ran something else: $(cat "$WORK/calls")"
printf '%s\n' "$out" | grep -q '^✓ Listening on Tailscale' || fail "--join didn't print the machine starting"
printf '%s\n' "$out" | grep -q '^✓ Added to studio' || fail "--join didn't print that it joined"
printf '%s\n' "$out" | grep -q 'add-machine#PAIR-CODE0' && fail "--join printed a pairing link nobody needs"
printf '%s\n' "$out" | grep -q '^Next:' && fail "--join also printed the next steps for a plain install"
# --join=LINE is the same thing.
out="$(run_join '--join=studio.ts.net:6262#J8RT-4WXN')" || fail "install.sh --join=LINE failed: $out"
grep -qx 'node join studio.ts.net:6262#J8RT-4WXN' "$WORK/calls" || fail "--join=LINE didn't join: $(cat "$WORK/calls")"
echo "install.sh --join installs, starts the machine and joins"

# A code that expired or was used: Fleet's reason is shown, the exit is not zero, and the next step is named.
if out="$(run_join --join 'http://100.64.0.5:6262#USED-CODE0')"; then
  fail "a used code should fail install.sh --join"
fi
printf '%s\n' "$out" | grep -q 'expired or was already used' || fail "a used code didn't say so: $out"
printf '%s\n' "$out" | grep -q "node join '<address>#CODE'" || fail "a used code didn't say how to join once there is a new line: $out"
echo "install.sh --join says when the code was used or expired"

# Things that aren't a join line stop before anything is downloaded.
for bad in "" "J8RT-4WXN" "http://100.64.0.5:6262" "http://100.64.0.5:6262#" "#J8RT-4WXN"; do
  if out="$(WEAVE_FLEET_DOWNLOAD_BASE_URL=file:///nonexistent HOME="$WORK/jhome" sh "$SCRIPT_DIR/install.sh" --join "$bad" 2>&1)"; then
    fail "--join '$bad' should fail"
  fi
  printf '%s\n' "$out" | grep -q "isn't a join line" || fail "--join '$bad' didn't say what a join line looks like: $out"
done
if out="$(HOME="$WORK/jhome" sh "$SCRIPT_DIR/install.sh" --join 2>&1)"; then fail "--join with nothing after it should fail"; fi
printf '%s\n' "$out" | grep -q 'needs the line' || fail "--join with nothing after it: $out"
if out="$(HOME="$WORK/jhome" sh "$SCRIPT_DIR/install.sh" --jion x 2>&1)"; then fail "an unknown option should fail"; fi
printf '%s\n' "$out" | grep -q 'unknown option: --jion' || fail "an unknown option: $out"
echo "install.sh --join turns down a line that isn't one before downloading"
