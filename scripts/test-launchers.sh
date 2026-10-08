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
    --port) run_fleet node "$option" 2113 ;;
    --host) run_fleet node "$option" 127.0.0.1 ;;
    --profile) run_fleet node "$option" test ;;
    --data-dir) run_fleet node "$option" "$WORK/data-dir" ;;
    *) fail "fleet node help lists $option, which this test doesn't know how to run"; continue ;;
  esac || true
  expect_started node "$option" && { started_as_node || fail "fleet node $option didn't pass --node"; }
done

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
