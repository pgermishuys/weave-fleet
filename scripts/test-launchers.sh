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
} > "$FLEET_TEST_OUT"
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
  env -u Fleet__Auth__RequireToken HOME="$WORK/home" WEAVE_FLEET_DATA_DIR="$WORK/data" FLEET_TEST_OUT="$WORK/out" \
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
    --port) run_fleet node "$option" 2113 ;;
    --host) run_fleet node "$option" 127.0.0.1 ;;
    --profile) run_fleet node "$option" test ;;
    --data-dir) run_fleet node "$option" "$WORK/data-dir" ;;
    *) fail "fleet node help lists $option, which this test doesn't know how to run"; continue ;;
  esac || true
  expect_started node "$option" && { started_as_node || fail "fleet node $option didn't pass --node"; }
done

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

# No call ever named the user's own fleet.service.
grep -qE '(^| )fleet\.service' "$WORK/calls.all" && fail "a call named fleet.service: $(cat "$WORK/calls.all")"

# Every option `fleet help` lists is accepted.
run_fleet help || fail "fleet help failed"
HELP_OPTIONS="$(grep -o -- '--[a-z][a-z-]*' "$WORK/stdout" | sort -u)"
for option in $HELP_OPTIONS; do
  case "$option" in
    --require-token) run_fleet "$option" ;;
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

if [ "$FAILURES" -gt 0 ]; then
  echo "$FAILURES launcher check(s) failed." >&2
  exit 1
fi
echo "Launcher checks passed."
