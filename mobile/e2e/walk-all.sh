#!/usr/bin/env bash
# Pairs once, then walks every screen in light and in dark. Usage: walk-all.sh <android|ios> <out-dir> [ios-udid]
# Needs FLEET_URL and PAIR_CODE (start-fleet.sh) and a running emulator or simulator with the app installed.
# Screenshots land in <out-dir>/<platform>-light and <platform>-dark. Both walks run even if the first fails.
set -uo pipefail
PLATFORM=$1; OUT=$2; UDID=${3:-}
cd "$(dirname "$0")"
mkdir -p "$OUT"
FAILED=0

appearance() {
  if [ "$PLATFORM" = ios ]; then xcrun simctl ui "$UDID" appearance "$1"
  else
    adb shell cmd uimode night "$([ "$1" = dark ] && echo yes || echo no)"
    # Switching recreates the running activity; a walk that launches into that dies before its first command.
    adb shell am force-stop io.tryweave.fleet
  fi
}
maestro_run() {
  local args=(); [ "$PLATFORM" = ios ] && args=(--device "$UDID")
  maestro "${args[@]}" test -e FLEET_URL="$FLEET_URL" -e PAIR_CODE="$PAIR_CODE" "${@}"
}

# The emulator can drop to "device offline" for a moment just after boot, which kills Maestro's device server
# mid-command. Wait for adb to see it booted, and pair once more if the first try dies; pair starts from a cleared app.
ready() {
  [ "$PLATFORM" = ios ] && return 0
  adb wait-for-device
  for _ in $(seq 60); do [ "$(adb shell getprop sys.boot_completed 2>/dev/null | tr -d '\r')" = 1 ] && adb shell pm path io.tryweave.fleet > /dev/null 2>&1 && return 0; sleep 2; done
}

appearance light
ready
if ! maestro_run --test-output-dir "$OUT/pair" flows/pair.yaml; then
  echo "pair failed; once more"
  sleep 10
  ready
  maestro_run --test-output-dir "$OUT/pair-again" flows/pair.yaml || { echo "pair failed"; exit 1; }
fi
for i in 1 2; do
  scheme=$([ "$i" = 1 ] && echo light || echo dark)
  appearance "$scheme"
  sleep 3
  # No clearState: the second walk relaunches the paired app in the other scheme. A walk that fails before its first
  # screenshot sent nothing yet, so it can run again as it is.
  dir="$OUT/$PLATFORM-$scheme"
  if ! maestro_run -e RUN=$i --test-output-dir "$dir" flows/walk.yaml; then
    if [ -z "$(find "$dir" -name '*.png' 2>/dev/null)" ]; then
      echo "walk $scheme failed before it started; once more"
      sleep 5
      maestro_run -e RUN=$i --test-output-dir "$dir" flows/walk.yaml || FAILED=1
    else
      FAILED=1
    fi
  fi
done
find "$OUT" -name "*.png" | sort
exit $FAILED
