#!/usr/bin/env bash
# Pairs once, then walks every screen in light and in dark. Usage: walk-all.sh <android|ios> <out-dir> [ios-udid]
# Needs FLEET_URL, PAIR_CODE, FLEET_TOKEN, SESSION_ID and SCRATCH_HOME (start-fleet.sh) and a running emulator or
# simulator with the app installed.
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
# Maestro sometimes exits mid-command with no error of its own; say how it ended, and whether the kernel killed it.
maestro_run() {
  local args=(); [ "$PLATFORM" = ios ] && args=(--device "$UDID")
  local code=0
  maestro "${args[@]}" test -e FLEET_URL="$FLEET_URL" -e PAIR_CODE="$PAIR_CODE" "${@}" || code=$?
  if [ "$code" -ne 0 ] && [ "$(uname)" = Linux ]; then
    echo "maestro exited with $code"
    free -m
    sudo -n dmesg 2>/dev/null | grep -iE "out of memory|killed process" | tail -5
    [ "$PLATFORM" = android ] && adb devices
  fi
  return "$code"
}

# The emulator can drop to "device offline" for a moment just after boot, which kills Maestro's device server
# mid-command. Wait for adb to see it booted, and pair once more if the first try dies; pair starts from a cleared app.
ready() {
  [ "$PLATFORM" = ios ] && return 0
  adb wait-for-device
  for _ in $(seq 60); do [ "$(adb shell getprop sys.boot_completed 2>/dev/null | tr -d '\r')" = 1 ] && adb shell pm path io.tryweave.fleet > /dev/null 2>&1 && break; sleep 2; done
  # The emulator runner presses a key to unlock as soon as boot completes. When the launcher is still loading, that key
  # times out and "Pixel Launcher isn't responding" sits over every app for the rest of the run, so no flow sees the
  # app. With error dialogs hidden Android restarts a hung app instead; a dialog already up goes with its process.
  adb shell settings put global hide_error_dialogs 1
  local anr
  for anr in $(adb shell dumpsys window windows | tr -d '\r' | grep -o 'Window{.* Application Not Responding: [A-Za-z0-9._:]*}' | sed 's/.*: //; s/}$//' | sort -u); do
    echo "closing \"$anr isn't responding\""
    adb shell am force-stop "$anr"
  done
  # A device that dropped offline comes back without its adb reverse, and the app can't reach Fleet: set it up again.
  adb reverse "tcp:${FLEET_URL##*:}" "tcp:${FLEET_URL##*:}" > /dev/null
  return 0
}

# The emulator sometimes drops its adb link, at any point and now and then twice in a run: the adb server reads "connection
# terminated" from the emulator at the moment adbd fails to write to it, and reconnects within a second. Maestro
# doesn't, so it dies mid-command, with "device offline" or with no error at all. The link is the adb server's
# connection to the emulator's adb port; a new one means it dropped. The adb server's log goes with the shots.
adb_link() { ss -tnH '( dport = :5555 )' 2>/dev/null | awk '{print $4}'; }
[ "$PLATFORM" = android ] && trap 'cp "${TMPDIR:-/tmp}/adb.$(id -u).log" "$OUT/adb-server.log" 2>/dev/null' EXIT

# A walk cut off mid-turn leaves its session with a command or a page waiting: the next try gets a new session, and the
# old one goes, so the list still has one "Fix pagination off-by-one".
fresh_session() {
  local auth=(-H "Authorization: Bearer $FLEET_TOKEN")
  curl -fsS -X POST "${auth[@]}" "$FLEET_URL/api/sessions/$SESSION_ID/abort" > /dev/null || true
  curl -fsS -X DELETE "${auth[@]}" "$FLEET_URL/api/sessions/$SESSION_ID" > /dev/null || true
  SESSION_ID=$(bash new-session.sh)
}

ready
# iOS's "slide to type" introduction can rise over the keyboard on any keyboard show, and Maestro then reads it instead
# of the conversation (run 40913188161 failed at "Going with…" with the reply on screen). Mark it shown, and swiping off.
if [ "$PLATFORM" = ios ]; then
  xcrun simctl spawn "$UDID" defaults write com.apple.keyboard.preferences DidShowContinuousPathIntroduction -bool true
  xcrun simctl spawn "$UDID" defaults write com.apple.keyboard.preferences KeyboardContinuousPathEnabled -bool false
  xcrun simctl spawn "$UDID" defaults read com.apple.keyboard.preferences
fi
appearance light
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
  ready
  # No clearState: the second walk relaunches the paired app in the other scheme. A walk that fails before its first
  # screenshot sent nothing yet, so it can run again as it is; one the emulator cut off later runs again on a fresh
  # session, where it's run 1. Two more tries at most.
  dir="$OUT/$PLATFORM-$scheme"; run=$i
  for try in 1 2 3; do
    link=$([ "$PLATFORM" = android ] && adb_link)
    maestro_run -e RUN=$run --test-output-dir "$dir" flows/walk.yaml && break
    if [ "$try" = 3 ]; then
      FAILED=1
    elif [ -z "$(find "$dir" -name '*.png' 2>/dev/null)" ]; then
      echo "walk $scheme failed before it started; once more"
    elif [ "$PLATFORM" = android ] && [ "$(adb_link)" != "$link" ]; then
      echo "the emulator dropped its adb link mid-walk; walk $scheme once more, on a fresh session"
      fresh_session
      dir="$OUT/$PLATFORM-$scheme-try$((try + 1))"; run=1
    else
      FAILED=1; break
    fi
    sleep 5
    ready
  done
done
find "$OUT" -name "*.png" | sort
exit $FAILED
