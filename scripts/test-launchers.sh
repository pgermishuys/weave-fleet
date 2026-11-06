#!/usr/bin/env sh
# Runs scripts/launcher.sh the way an install does (bin/fleet next to app/) against a stub app that records the
# arguments and settings it was started with. Also checks every option the app reads is one the launchers accept,
# or is listed below as app-only: the launchers reject options they don't know, so an option added to the app
# alone can't be used (as happened to --require-token).
set -eu

SCRIPT_DIR="$(cd "$(dirname "$0")" && pwd)"
ROOT_DIR="$(cd "$SCRIPT_DIR/.." && pwd)"

# Options Program.cs reads that people don't pass to `fleet`: dev switches, import-legacy-sessions' own options, and
# --node, which `fleet node` passes.
APP_ONLY_OPTIONS="--harness --transport --import-legacy-sessions --source --node"

WORK="$(mktemp -d)"
trap 'rm -rf "$WORK"' EXIT
mkdir -p "$WORK/fleet/bin" "$WORK/fleet/app" "$WORK/home"
cp "$SCRIPT_DIR/launcher.sh" "$WORK/fleet/bin/fleet"
chmod +x "$WORK/fleet/bin/fleet"
echo "0.0.0-test" > "$WORK/fleet/VERSION"
cat > "$WORK/fleet/app/WeaveFleet.Api" <<'EOF'
#!/bin/sh
{
  echo "args=$*"
  echo "host=${Fleet__Host:-}"
  echo "port=${Fleet__Port:-}"
  echo "require_token=${Fleet__Auth__RequireToken:-}"
  echo "private_networks=${Fleet__PrivateNetworksOnly:-}"
} > "$FLEET_TEST_OUT"
case " $* " in
  *" --node-pair "*) printf 'Pairing link for stub\n\n    http://stub/add-machine#CODE\n' ;;
esac
EOF
chmod +x "$WORK/fleet/app/WeaveFleet.Api"

FAILURES=0
fail() {
  echo "FAIL: $*" >&2
  FAILURES=$((FAILURES + 1))
}

# Runs bin/fleet with a scratch HOME and data dir; the stub's record lands in $WORK/out.
run_fleet() {
  rm -f "$WORK/out"
  env -u Fleet__Auth__RequireToken -u Fleet__PrivateNetworksOnly HOME="$WORK/home" WEAVE_FLEET_DATA_DIR="$WORK/data" FLEET_TEST_OUT="$WORK/out" \
    "$WORK/fleet/bin/fleet" "$@" > "$WORK/stdout" 2>&1
}

expect_started() {
  if [ ! -f "$WORK/out" ]; then
    fail "fleet $* didn't start the app: $(cat "$WORK/stdout")"
    return 1
  fi
}

expect_record() {
  if ! grep -qx "$1" "$WORK/out"; then
    fail "expected '$1' after fleet $2, got: $(tr '\n' ' ' < "$WORK/out")"
  fi
}

# Whether the app was started with --node.
started_as_node() {
  grep '^args=' "$WORK/out" | tr ' ' '\n' | grep -qx -- "--node"
}

# --require-token reaches the app as Fleet:Auth:RequireToken.
run_fleet --port 2113 --require-token || true
if expect_started --port 2113 --require-token; then
  expect_record "require_token=true" "--port 2113 --require-token"
  expect_record "port=2113" "--port 2113 --require-token"
  expect_record "host=127.0.0.1" "--port 2113 --require-token"
fi
# --private-networks reaches the app as Fleet:PrivateNetworksOnly.
run_fleet --port 2113 --private-networks || true
if expect_started --port 2113 --private-networks; then
  expect_record "private_networks=true" "--port 2113 --private-networks"
fi
run_fleet --require-token --host 127.0.0.1 || true
if expect_started --require-token --host 127.0.0.1; then
  expect_record "require_token=true" "--require-token --host 127.0.0.1"
fi

# Without it, the launcher leaves the setting alone.
run_fleet --port 2113 || true
if expect_started --port 2113; then
  expect_record "require_token=" "--port 2113"
fi

# Unknown options still stop before the app starts, so a typo can't start Fleet without the token.
if run_fleet --port 2113 --requre-token; then
  fail "fleet --requre-token should fail"
elif [ -f "$WORK/out" ]; then
  fail "fleet --requre-token started the app"
elif ! grep -q "Unknown command or option: --requre-token" "$WORK/stdout"; then
  fail "fleet --requre-token: unexpected output: $(cat "$WORK/stdout")"
fi

# `fleet node` starts the app with --node and the usual options; plain `fleet` doesn't.
run_fleet node --port 5512 --host 0.0.0.0 || true
if expect_started node --port 5512 --host 0.0.0.0; then
  started_as_node || fail "fleet node didn't pass --node: $(tr '\n' ' ' < "$WORK/out")"
  expect_record "port=5512" "node --port 5512 --host 0.0.0.0"
  expect_record "host=0.0.0.0" "node --port 5512 --host 0.0.0.0"
fi
run_fleet --port 5512 || true
if expect_started --port 5512 && started_as_node; then
  fail "fleet --port 5512 passed --node"
fi

# `node` only works first, and a typo after it still stops before the app starts.
if run_fleet --port 5512 node; then
  fail "fleet --port 5512 node should fail"
fi
if run_fleet node --requre-token; then
  fail "fleet node --requre-token should fail"
elif [ -f "$WORK/out" ]; then
  fail "fleet node --requre-token started the app"
fi

# `fleet node help` explains node mode, and every option it lists is accepted after `fleet node`.
run_fleet node help || fail "fleet node help failed"
if [ -f "$WORK/out" ]; then
  fail "fleet node help started the app"
fi
grep -q "Usage: fleet node" "$WORK/stdout" || fail "fleet node help: unexpected output: $(cat "$WORK/stdout")"
NODE_HELP_OPTIONS="$(grep -o -- '--[a-z][a-z-]*' "$WORK/stdout" | sort -u)"
for option in $NODE_HELP_OPTIONS; do
  case "$option" in
    --print) continue ;;  # install-service only; checked below
    --tailscale|--lan|--ssh-only|--yes) continue ;;  # up only; checked below
    --json) continue ;;  # pair only; checked below
    --port) run_fleet node "$option" 2113 ;;
    --private-networks) run_fleet node "$option" ;;
    --host) run_fleet node "$option" 127.0.0.1 ;;
    --profile) run_fleet node "$option" test ;;
    --data-dir) run_fleet node "$option" "$WORK/data-dir" ;;
    *) fail "fleet node help lists $option, which this test doesn't know how to run"; continue ;;
  esac || true
  expect_started node "$option" && { started_as_node || fail "fleet node $option didn't pass --node"; }
done

# `fleet node pair [--json]` asks the running node for a code instead of starting one; --json only works there.
run_fleet node pair --json --port 5512 || true
if expect_started node pair --json --port 5512; then
  grep '^args=' "$WORK/out" | grep -q -- '--node-pair' || fail "fleet node pair didn't pass --node-pair: $(tr '\n' ' ' < "$WORK/out")"
  grep '^args=' "$WORK/out" | tr ' ' '\n' | grep -qx -- "--json" || fail "fleet node pair --json didn't pass --json: $(tr '\n' ' ' < "$WORK/out")"
  expect_record "port=5512" "node pair --json --port 5512"
fi
run_fleet node pair --port 5512 || true
if expect_started node pair --port 5512 && grep '^args=' "$WORK/out" | tr ' ' '\n' | grep -qx -- "--json"; then
  fail "fleet node pair passed --json on its own"
fi
if run_fleet node --json; then
  fail "fleet node --json should fail"
elif [ -f "$WORK/out" ]; then
  fail "fleet node --json started the app"
fi

# install-service and uninstall-service, against stand-ins for systemctl, loginctl and launchctl that record their
# calls. A uname that says Darwin turns the same launcher to its macOS branch.
mkdir -p "$WORK/stubs" "$WORK/macos" "$WORK/home/.config/systemd/user"
printf '#!/bin/sh\necho "systemctl $*" | tee -a "$FLEET_TEST_CALLS.all" >> "$FLEET_TEST_CALLS"\n' > "$WORK/stubs/systemctl"
printf '#!/bin/sh\necho no\n' > "$WORK/stubs/loginctl"
printf '#!/bin/sh\necho "launchctl $*" | tee -a "$FLEET_TEST_CALLS.all" >> "$FLEET_TEST_CALLS"\n[ "$1" = print ] && [ ! -f "$FLEET_TEST_LOADED" ] && exit 1\nexit 0\n' > "$WORK/stubs/launchctl"
printf '#!/bin/sh\necho Darwin\n' > "$WORK/macos/uname"
chmod +x "$WORK/stubs/"* "$WORK/macos/uname"
# The user's own Fleet service, which install-service must never touch.
echo "# the user's own Fleet" > "$WORK/home/.config/systemd/user/fleet.service"
UNIT="$WORK/home/.config/systemd/user/fleet-node.service"
PLIST="$WORK/home/Library/LaunchAgents/io.tryweave.fleet-node.plist"

# Runs bin/fleet for a service command on "linux" or "macos"; calls land in $WORK/calls, output in $WORK/stdout.
run_service() {
  platform=$1
  shift
  rm -f "$WORK/out" "$WORK/calls"
  stub_path="$WORK/stubs:$PATH"
  [ "$platform" = macos ] && stub_path="$WORK/macos:$stub_path"
  (cd "$WORK" && env -u XDG_CONFIG_HOME HOME="$WORK/home" PATH="$stub_path" WEAVE_FLEET_DATA_DIR="$WORK/data" \
    FLEET_TEST_OUT="$WORK/out" FLEET_TEST_CALLS="$WORK/calls" FLEET_TEST_LOADED="$WORK/loaded" \
    "$WORK/fleet/bin/fleet" "$@" > "$WORK/stdout" 2>&1)
}

expect_output() {
  case "$(cat "$WORK/stdout")" in
    *"$1"*) ;;
    *) fail "$2: expected '$1' in: $(cat "$WORK/stdout")" ;;
  esac
}

expect_calls() {
  actual="$(cat "$WORK/calls" 2>/dev/null || true)"
  [ "$actual" = "$1" ] || fail "$2: expected calls [$1], got [$actual]"
}

# --print shows the unit it would write and the commands it would run, and changes nothing.
run_service linux node install-service --port 5512 --host 0.0.0.0 --data-dir "node data/a%b" --print || fail "install-service --print failed: $(cat "$WORK/stdout")"
expect_output "would write $UNIT" "install-service --print"
expect_output "ExecStart=\"$WORK/fleet/bin/fleet\" \"node\" \"--port\" \"5512\" \"--host\" \"0.0.0.0\" \"--data-dir\" \"$WORK/node data/a%%b\"" "install-service --print"
expect_output "RestartPreventExitStatus=75" "install-service --print"
expect_output "systemctl --user enable fleet-node.service" "install-service --print"
expect_output "Nothing was changed." "install-service --print"
[ -f "$UNIT" ] && fail "install-service --print wrote $UNIT"
[ -f "$WORK/out" ] && fail "install-service --print started the app"
expect_calls "" "install-service --print"

# The macOS --print: a LaunchAgent that runs the launcher as a node, loaded into this user's session.
run_service macos node install-service --port 5512 --host 0.0.0.0 --data-dir "$WORK/a&b" --print || fail "macOS install-service --print failed: $(cat "$WORK/stdout")"
expect_output "would write $PLIST" "macOS install-service --print"
expect_output "<string>io.tryweave.fleet-node</string>" "macOS install-service --print"
expect_output "    <string>$WORK/fleet/bin/fleet</string>
    <string>node</string>
    <string>--port</string>
    <string>5512</string>
    <string>--host</string>
    <string>0.0.0.0</string>
    <string>--data-dir</string>
    <string>$WORK/a&amp;b</string>" "macOS install-service --print"
expect_output "<key>KeepAlive</key>" "macOS install-service --print"
expect_output "launchctl bootstrap gui/$(id -u) $PLIST" "macOS install-service --print"
[ -f "$PLIST" ] && fail "macOS install-service --print wrote $PLIST"
expect_calls "" "macOS install-service --print"

# --print belongs to install-service, and uninstall-service takes no options.
run_service linux node --print && fail "fleet node --print should fail"
run_service linux node uninstall-service --port 5512 && fail "fleet node uninstall-service --port should fail"

# Linux: install writes fleet-node.service (a relative data dir made absolute) and starts it; installing again updates
# it; uninstall stops and removes it. fleet.service is never named, and its file never changes.
run_service linux node install-service --port 5512 --data-dir node-data || fail "install-service failed: $(cat "$WORK/stdout")"
grep -qF "\"--data-dir\" \"$WORK/node-data\"" "$UNIT" 2>/dev/null || fail "install-service didn't write the unit with an absolute data dir: $(cat "$UNIT" 2>/dev/null)"
expect_calls "systemctl --user daemon-reload
systemctl --user enable fleet-node.service
systemctl --user restart fleet-node.service" "install-service"
expect_output "Installed the fleet-node service" "install-service"
expect_output "loginctl enable-linger" "install-service"
expect_output "To undo: fleet node uninstall-service" "install-service"
run_service linux node install-service --port 5512 --data-dir node-data || fail "install-service again failed"
expect_output "Updated the fleet-node service" "install-service again"
run_service linux node uninstall-service || fail "uninstall-service failed: $(cat "$WORK/stdout")"
[ -f "$UNIT" ] && fail "uninstall-service left $UNIT"
expect_calls "systemctl --user disable --now fleet-node.service
systemctl --user daemon-reload" "uninstall-service"
run_service linux node uninstall-service || fail "uninstall-service with nothing installed failed"
expect_output "There's no fleet-node service to remove." "uninstall-service with nothing installed"
[ "$(cat "$WORK/home/.config/systemd/user/fleet.service")" = "# the user's own Fleet" ] || fail "fleet.service changed"

# macOS: install writes the LaunchAgent and bootstraps it; once loaded, installing again boots the old one out first.
run_service macos node install-service --port 5512 || fail "macOS install-service failed: $(cat "$WORK/stdout")"
[ -f "$PLIST" ] || fail "macOS install-service didn't write $PLIST"
expect_calls "launchctl print gui/$(id -u)/io.tryweave.fleet-node
launchctl bootstrap gui/$(id -u) $PLIST" "macOS install-service"
touch "$WORK/loaded"
run_service macos node install-service --port 5512 || fail "macOS install-service again failed"
expect_calls "launchctl print gui/$(id -u)/io.tryweave.fleet-node
launchctl bootout gui/$(id -u)/io.tryweave.fleet-node
launchctl bootstrap gui/$(id -u) $PLIST" "macOS install-service again"
run_service macos node uninstall-service || fail "macOS uninstall-service failed: $(cat "$WORK/stdout")"
[ -f "$PLIST" ] && fail "macOS uninstall-service left $PLIST"

# fleet node up: the choice becomes the host of the service, and it prints the link the node gives. tailscale is a
# stand-in that prints an address, or fails like a signed-out one.
printf '#!/bin/sh\n[ -f "$FLEET_TEST_TAILSCALE_DOWN" ] && exit 1\necho 100.64.90.72\n' > "$WORK/stubs/tailscale"
chmod +x "$WORK/stubs/tailscale"
rm -f "$UNIT"

run_service linux node up --lan --port 5512 --data-dir node-data || fail "node up --lan failed: $(cat "$WORK/stdout")"
grep -qF '"--host" "0.0.0.0"' "$UNIT" 2>/dev/null || fail "node up --lan didn't write host 0.0.0.0: $(cat "$UNIT" 2>/dev/null)"
expect_output "✓ Listening on 0.0.0.0:5512" "node up --lan"
expect_output "✓ Runs in the background, starts at login" "node up --lan"
expect_output "http://stub/add-machine#CODE" "node up --lan"
expect_calls "systemctl --user daemon-reload
systemctl --user enable fleet-node.service
systemctl --user restart fleet-node.service" "node up --lan"

# Same choice again: nothing is rewritten or restarted, and a fresh link prints.
run_service linux node up --lan --port 5512 --data-dir node-data || fail "node up again failed: $(cat "$WORK/stdout")"
expect_output "✓ Already running on 0.0.0.0:5512" "node up again"
expect_output "http://stub/add-machine#CODE" "node up again"
expect_calls "" "node up again"

# A different choice changes the service.
run_service linux node up --tailscale --port 5512 --data-dir node-data || fail "node up --tailscale failed: $(cat "$WORK/stdout")"
grep -qF '"--host" "0.0.0.0"' "$UNIT" 2>/dev/null || fail "node up --tailscale didn't write host 0.0.0.0: $(cat "$UNIT" 2>/dev/null)"
grep -qF '"--private-networks"' "$UNIT" 2>/dev/null || fail "node up --tailscale didn't write --private-networks: $(cat "$UNIT" 2>/dev/null)"
expect_output "✓ Listening on Tailscale (100.64.90.72) and your local network, port 5512" "node up --tailscale"
expect_calls "systemctl --user daemon-reload
systemctl --user enable fleet-node.service
systemctl --user restart fleet-node.service" "node up --tailscale after --lan"
run_service linux node up --tailscale --port 5512 --data-dir node-data || fail "node up --tailscale again failed: $(cat "$WORK/stdout")"
expect_output "✓ Already running on 0.0.0.0:5512" "node up --tailscale again"
expect_calls "" "node up --tailscale again"
# An older install listens on the Tailscale address alone: it counts as Tailscale, and is rewritten to the new form.
sed -i 's/"--host" "0.0.0.0"/"--host" "100.64.90.72"/; /"--private-networks"/d' "$UNIT"
run_service linux node up --yes --port 5512 --data-dir node-data || fail "node up --yes on an old Tailscale install failed: $(cat "$WORK/stdout")"
expect_output "✓ Listening on Tailscale (100.64.90.72) and your local network, port 5512" "node up --yes on an old Tailscale install"
grep -qF '"--private-networks"' "$UNIT" 2>/dev/null || fail "node up --yes didn't rewrite the old Tailscale install: $(cat "$UNIT" 2>/dev/null)"
# Back to the local network: no restriction.
run_service linux node up --lan --port 5512 --data-dir node-data || fail "node up --lan after Tailscale failed: $(cat "$WORK/stdout")"
grep -qF '"--private-networks"' "$UNIT" 2>/dev/null && fail "node up --lan kept --private-networks: $(cat "$UNIT")"
expect_output "✓ Listening on 0.0.0.0:5512" "node up --lan after Tailscale"
run_service linux node up --ssh-only --port 5512 --data-dir node-data || fail "node up --ssh-only failed: $(cat "$WORK/stdout")"
grep -qF '"--host" "127.0.0.1"' "$UNIT" 2>/dev/null || fail "node up --ssh-only didn't write loopback: $(cat "$UNIT" 2>/dev/null)"
expect_output "Over SSH" "node up --ssh-only"

# --yes keeps what is set up. On a fresh machine it takes Tailscale when that is up, and Only over SSH when it isn't.
# Without a choice and a terminal, it stops.
run_service linux node up --yes --port 5512 --data-dir node-data || fail "node up --yes on top of a service failed"
expect_output "✓ Already running on 127.0.0.1:5512" "node up --yes on top of a service"
rm -f "$UNIT"
run_service linux node up --yes --port 5512 --data-dir node-data || fail "node up --yes failed: $(cat "$WORK/stdout")"
expect_output "✓ Listening on Tailscale (100.64.90.72) and your local network, port 5512" "node up --yes"
grep -qF '"--private-networks"' "$UNIT" 2>/dev/null || fail "node up --yes didn't write --private-networks: $(cat "$UNIT" 2>/dev/null)"
touch "$WORK/tailscale-down"
(cd "$WORK" && env FLEET_TEST_TAILSCALE_DOWN="$WORK/tailscale-down" HOME="$WORK/home" PATH="$WORK/stubs:$PATH" WEAVE_FLEET_DATA_DIR="$WORK/data" \
  FLEET_TEST_OUT="$WORK/out" FLEET_TEST_CALLS="$WORK/calls" "$WORK/fleet/bin/fleet" node up --tailscale --port 5512 > "$WORK/stdout" 2>&1) && fail "node up --tailscale should fail when Tailscale is signed out"
expect_output "Run tailscale up, then fleet node up again" "node up --tailscale signed out"
run_service linux node up --port 5512 < /dev/null && fail "node up without a choice or a terminal should fail"
expect_output "Add --tailscale, --lan or --ssh-only" "node up without a choice"
# A data directory a plain Fleet already runs from: stop before anything is written or run.
rm -f "$UNIT"
mkdir -p "$WORK/shared-data"
echo '{}' > "$WORK/shared-data/fleet.instance.json"
run_service linux node up --lan --port 5512 --data-dir shared-data && fail "node up on a plain Fleet's data directory should fail"
expect_output "belongs to the Fleet you open in a browser. Run fleet node up --profile node" "node up on a plain Fleet's data directory"
[ -f "$UNIT" ] && fail "node up on a plain Fleet's data directory wrote the service"
expect_calls "" "node up on a plain Fleet's data directory"
run_service linux node up --lan --ssh-only && fail "node up --lan --ssh-only should fail"
run_service linux node up --lan --host 0.0.0.0 && fail "node up --host should fail"
run_service linux node --lan && fail "fleet node --lan should fail"

# The first run's menu without Tailscale names nothing as current or recommended. (Needs a terminal; script's -c is
# Linux only.)
if [ "$(uname -s)" = Linux ] && command -v script >/dev/null 2>&1; then
  touch "$WORK/tailscale-down"
  rm -f "$UNIT"
  (cd "$WORK" && echo 3 | env FLEET_TEST_TAILSCALE_DOWN="$WORK/tailscale-down" HOME="$WORK/home" PATH="$WORK/stubs:$PATH" WEAVE_FLEET_DATA_DIR="$WORK/data" \
    FLEET_TEST_OUT="$WORK/out" FLEET_TEST_CALLS="$WORK/calls" script -qec "$WORK/fleet/bin/fleet node up --port 5512" /dev/null > "$WORK/stdout" 2>&1) || fail "node up menu failed: $(cat "$WORK/stdout")"
  expect_output "3) Only over SSH" "node up menu without Tailscale"
  case "$(cat "$WORK/stdout")" in
    *"(current)"*|*"(recommended)"*) fail "node up menu on a first run without Tailscale has a label: $(cat "$WORK/stdout")" ;;
  esac
  rm -f "$UNIT" "$WORK/tailscale-down"
fi

# macOS: the LaunchAgent gets the same host.
run_service macos node up --lan --port 5512 || fail "macOS node up failed: $(cat "$WORK/stdout")"
grep -q '<string>0.0.0.0</string>' "$PLIST" 2>/dev/null || fail "macOS node up didn't write host 0.0.0.0"
run_service macos node up --lan --port 5512 || fail "macOS node up again failed"
expect_output "✓ Already running on 0.0.0.0:5512" "macOS node up again"
rm -f "$PLIST" "$UNIT" "$WORK/loaded"

# No call ever named the user's own fleet.service.
grep -qE '(^| )fleet\.service' "$WORK/calls.all" && fail "a call named fleet.service: $(cat "$WORK/calls.all")"

# Every option `fleet help` lists is accepted.
run_fleet help || fail "fleet help failed"
HELP_OPTIONS="$(grep -o -- '--[a-z][a-z-]*' "$WORK/stdout" | sort -u)"
for option in $HELP_OPTIONS; do
  case "$option" in
    --require-token|--private-networks) run_fleet "$option" ;;
    --port) run_fleet "$option" 2113 ;;
    --host) run_fleet "$option" 127.0.0.1 ;;
    --profile) run_fleet "$option" test ;;
    --data-dir) run_fleet "$option" "$WORK/data-dir" ;;
    *) fail "fleet help lists $option, which this test doesn't know how to run"; continue ;;
  esac || true
  expect_started "$option" || true
done

# Every option the app reads is accepted by both launchers, or is app-only.
APP_OPTIONS="$(grep -o 'args\[i\] is "--[a-z-]*"' "$ROOT_DIR/src/WeaveFleet.Api/Program.cs" | grep -o -- '--[a-z-]*' | sort -u)"
[ -n "$APP_OPTIONS" ] || fail "found no options in src/WeaveFleet.Api/Program.cs; did the parsing move?"
for option in $APP_OPTIONS; do
  case " $APP_ONLY_OPTIONS " in *" $option "*) continue ;; esac
  grep -q -- "    $option)" "$SCRIPT_DIR/launcher.sh" || fail "the app reads $option but scripts/launcher.sh rejects it"
  grep -q -- "\"$option\"" "$SCRIPT_DIR/launcher.cmd" || fail "the app reads $option but scripts/launcher.cmd rejects it"
done

# ── Restarting into an update, and going back when it doesn't start ─────────────────────────────────────────────────
# A fresh install per scenario: the old app (0.0.0-test) stages 0.0.1-test and exits 85; the launcher starts itself
# again, applies it, and runs the new app, which behaves as the scenario says.
make_install() {
  root=$1
  new_behaviour=$2
  rm -rf "$root" "$WORK/rel"
  mkdir -p "$root/bin" "$root/app" "$WORK/rel/pkg/fleet-v0.0.1-test/app" "$WORK/rel/pkg/fleet-v0.0.1-test/bin"
  cp "$SCRIPT_DIR/launcher.sh" "$root/bin/fleet"
  cp "$SCRIPT_DIR/launcher.sh" "$WORK/rel/pkg/fleet-v0.0.1-test/bin/fleet"
  echo "0.0.0-test" > "$root/VERSION"
  # Both apps log how they were started; the old one stages the release, once, and asks for a restart.
  cat > "$root/app/WeaveFleet.Api" <<'APP'
#!/bin/sh
ROOT="$(cd "$(dirname "$0")/.." && pwd)"
echo "old restart=$FLEET_LAUNCHER_RESTART" >> "$FLEET_TEST_LOG"
if [ "$FLEET_TEST_STAGE" = 1 ] && [ ! -f "$ROOT/staged-once" ]; then
  touch "$ROOT/staged-once"
  echo "old-database" > "$Fleet__DatabasePath"
  mkdir -p "$ROOT/update"
  cp "$FLEET_TEST_RELEASE" "$ROOT/update/"
  printf '{"version":"0.0.1-test","assetFileName":"release.tar.gz"}\n' > "$ROOT/update/update-manifest.json"
  exit 85
fi
exit 0
APP
  cat > "$WORK/rel/pkg/fleet-v0.0.1-test/app/WeaveFleet.Api" <<APP
#!/bin/sh
ROOT="\$(cd "\$(dirname "\$0")/.." && pwd)"
echo "new restart=\$FLEET_LAUNCHER_RESTART" >> "\$FLEET_TEST_LOG"
case "$new_behaviour" in
  ok) rm -f "\$ROOT/update-trial.json"; exit 0 ;;
  crash) echo "migrated" > "\$Fleet__DatabasePath"; exit 1 ;;
  hang) exec sleep 300 ;;
esac
APP
  chmod +x "$root/app/WeaveFleet.Api" "$root/bin/fleet" "$WORK/rel/pkg/fleet-v0.0.1-test/app/WeaveFleet.Api" "$WORK/rel/pkg/fleet-v0.0.1-test/bin/fleet"
  echo "0.0.1-test" > "$WORK/rel/pkg/fleet-v0.0.1-test/VERSION"
  tar -czf "$WORK/rel/release.tar.gz" -C "$WORK/rel/pkg" fleet-v0.0.1-test
}

run_install() {
  rm -f "$WORK/log"
  install_root=$1
  shift
  env HOME="$WORK/home" WEAVE_FLEET_DATA_DIR="$WORK/updata" FLEET_TEST_LOG="$WORK/log" FLEET_TEST_STAGE=1 \
    FLEET_TEST_RELEASE="$WORK/rel/release.tar.gz" FLEET_UPDATE_TRIAL_SECONDS="${TRIAL_SECONDS_FOR_TEST:-120}" \
    "$install_root/bin/fleet" "$@" > "$WORK/stdout" 2>&1
}

expect_log() {
  [ "$(cat "$WORK/log" 2>/dev/null)" = "$1" ] || fail "$2: expected the app to start as [$(echo "$1" | tr '\n' ',')], got [$(tr '\n' ',' < "$WORK/log" 2>/dev/null)]"
}

# The new version starts, confirms, and stays: restarted once, onto the new version, with the launcher's exit code.
make_install "$WORK/up" ok
mkdir -p "$WORK/updata"
run_install "$WORK/up" --port 2113 || fail "restart into an update: exit code $?: $(cat "$WORK/stdout")"
expect_log "old restart=85
new restart=85" "restart into an update"
[ "$(cat "$WORK/up/VERSION")" = "0.0.1-test" ] || fail "restart into an update: VERSION is $(cat "$WORK/up/VERSION")"
[ -f "$WORK/up/update-trial.json" ] && fail "restart into an update: the trial was never confirmed"
[ -f "$WORK/up/update-failed.json" ] && fail "restart into an update: reported a failure"
[ -d "$WORK/up/app.prev" ] && [ "$(cat "$WORK/up/VERSION.prev")" = "0.0.0-test" ] || fail "restart into an update: the old version wasn't kept"

# The new version exits before it is ready: the old version, its database and a note about it come back, once.
make_install "$WORK/up" crash
rm -rf "$WORK/updata"; mkdir -p "$WORK/updata"
run_install "$WORK/up" --port 2113 || fail "failed update: exit code $?: $(cat "$WORK/stdout")"
expect_log "old restart=85
new restart=85
old restart=85" "failed update"
[ "$(cat "$WORK/up/VERSION")" = "0.0.0-test" ] || fail "failed update: VERSION is $(cat "$WORK/up/VERSION")"
[ "$(cat "$WORK/updata/fleet.db")" = "old-database" ] || fail "failed update: the database is $(cat "$WORK/updata/fleet.db")"
grep -q '"from":"0.0.0-test","to":"0.0.1-test","reason":"it exited with code 1 before it was ready"' "$WORK/up/update-failed.json" \
  || fail "failed update: update-failed.json is $(cat "$WORK/up/update-failed.json" 2>/dev/null)"
[ -f "$WORK/up/update-trial.json" ] && fail "failed update: the trial file stayed"
[ -f "$WORK/up/update/update-manifest.json" ] && fail "failed update: the update was staged again"

# The new version never says it started: the launcher stops it after FLEET_UPDATE_TRIAL_SECONDS and goes back.
make_install "$WORK/up" hang
rm -rf "$WORK/updata"; mkdir -p "$WORK/updata"
TRIAL_SECONDS_FOR_TEST=2 run_install "$WORK/up" --port 2113 || fail "update that hangs: exit code $?: $(cat "$WORK/stdout")"
[ "$(cat "$WORK/up/VERSION")" = "0.0.0-test" ] || fail "update that hangs: VERSION is $(cat "$WORK/up/VERSION")"
grep -q 'wasn.t ready after 2 seconds' "$WORK/up/update-failed.json" || fail "update that hangs: update-failed.json is $(cat "$WORK/up/update-failed.json" 2>/dev/null)"

# Stopping the launcher stops the app, and the launcher leaves with the app's exit code.
cat > "$WORK/up/app/WeaveFleet.Api" <<'APP'
#!/bin/sh
trap 'echo stopped > "$FLEET_TEST_LOG"; exit 3' TERM
echo started > "$FLEET_TEST_LOG"
while true; do sleep 1; done
APP
rm -f "$WORK/log"
env HOME="$WORK/home" WEAVE_FLEET_DATA_DIR="$WORK/updata" FLEET_TEST_LOG="$WORK/log" "$WORK/up/bin/fleet" --port 2113 > "$WORK/stdout" 2>&1 &
LAUNCHER_PID=$!
for _ in 1 2 3 4 5 6 7 8 9 10; do [ "$(cat "$WORK/log" 2>/dev/null)" = started ] && break; sleep 1; done
kill -TERM "$LAUNCHER_PID"
STOP_CODE=0
wait "$LAUNCHER_PID" || STOP_CODE=$?
[ "$(cat "$WORK/log")" = stopped ] || fail "stopping the launcher didn't stop the app: $(cat "$WORK/log")"
[ "$STOP_CODE" -eq 3 ] || fail "stopping the launcher: exit code $STOP_CODE, expected the app's 3"

if [ "$FAILURES" -gt 0 ]; then
  echo "$FAILURES launcher check(s) failed." >&2
  exit 1
fi
echo "Launcher checks passed."
