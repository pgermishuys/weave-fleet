#!/usr/bin/env bash
# Runs a command and ends it, with everything it started, if it outlasts a limit, saying why. For CI steps that drive
# a browser. GitHub's timeout-minutes is enforced by the runner, so it only fires while the runner is well: a runner
# that dies mid-step leaves a job that GitHub cancels 20 minutes after it started, with no log. This ends the command
# from inside the step instead, and keeps a heartbeat where a dead runner can't take it with it.
#
#   [WATCHDOG_ALSO=<pattern>] [WATCHDOG_CHECK_RUN=<id>] run-with-watchdog.sh <limit-seconds> <command> [args…]
#
# The command runs in a session, and so a process group, of its own. On a GitHub Mac the step shares one process group
# with the runner (Runner.Listener, Runner.Worker, and the script that started them, whose parent is launchd), and when a
# process exit leaves a group with a stopped member "orphaned", the kernel sends every member SIGHUP (POSIX; XNU's
# orphanpg). Stopping a tree and killing it in turn, as .NET's Process.Kill(entireProcessTree: true) does, can set that
# off, and SIGHUP ends the runner: no timeout fires after that, and GitHub cancels the job 20 minutes after it started,
# with no log. In a group of its own, whatever the command does reaches only its own group, and this never stops
# anything: it kills.
#
# At the limit it prints what the command's processes were doing (with a `sample` of the oldest on macOS), kills its
# whole group and anything it started that left it, and exits 124. A SIGINT or SIGTERM (the runner cancelling the
# step) kills them too. Otherwise it exits with the command's own status.
#
# WATCHDOG_ALSO: a pattern for processes to end with the tree although they've left it, such as a browser that lost its
#   parent. It's read from the environment so it can't match this script's own command line.
# WATCHDOG_CHECK_RUN: the job's own check run (${{ job.check_run_id }}). Every WATCHDOG_BEAT_SECONDS (15) the load,
#   memory and the tree's processes are written into its output with GH_TOKEN, so they outlive the runner. Read them
#   with `gh api repos/<owner>/<repo>/check-runs/<job id> --jq .output.text`.
set -uo pipefail

limit=${1:?usage: run-with-watchdog.sh <limit-seconds> <command> [args…]}
shift
also=${WATCHDOG_ALSO:-}
check_run=${WATCHDOG_CHECK_RUN:-}
beat_every=${WATCHDOG_BEAT_SECONDS:-15}
[ $# -gt 0 ] || { echo "run-with-watchdog.sh: no command" >&2; exit 2; }
command_line="$*"

descendants() {
  local child
  for child in $(pgrep -P "$1" 2>/dev/null); do
    echo "$child"
    descendants "$child"
  done
}

# Every process to end: the command's group, all the command started, and whatever matches WATCHDOG_ALSO.
targets() {
  { echo "$pid"; pgrep -g "$pid"; descendants "$pid"; [ -n "$also" ] && pgrep -f -- "$also"; } 2>/dev/null \
    | grep -vx "$$" | sort -un
}

end_all() {
  local pids
  pids=$(targets | tr '\n' ' ')
  [ -n "${pids// /}" ] || return 0
  # The group at once, then any that left it. Never SIGSTOP: a stopped member is what makes the kernel send SIGHUP.
  kill -KILL -- "-$pid" 2>/dev/null
  kill -KILL $pids 2>/dev/null
  echo "Killed: $pids"
}

processes() {
  local pids
  pids=$(targets | tr '\n' ',' | sed 's/,$//')
  [ -n "$pids" ] && ps -o pid,ppid,stat,etime,rss,%cpu,command -p "$pids" 2>/dev/null | cut -c1-250
}

# One line: load, free memory, swap and (macOS) the kernel's memory pressure level (1 normal, 2 warning, 4 critical).
vitals() {
  local load free swap pressure
  if [ -r /proc/loadavg ]; then
    load=$(cut -d' ' -f1-3 /proc/loadavg)
    free="$(awk '/MemAvailable/ {printf "%d MB", $2/1024}' /proc/meminfo) available"
    swap="$(awk '/SwapTotal/ {t=$2} /SwapFree/ {f=$2} END {printf "%d MB", (t-f)/1024}' /proc/meminfo) used"
  else
    load=$(sysctl -n vm.loadavg 2>/dev/null | tr -d '{}' | xargs)
    free="$(vm_stat 2>/dev/null | awk -v ps="$(sysctl -n hw.pagesize)" '/Pages free/ {gsub(/\./, "", $3); printf "%d MB", $3*ps/1048576}') free"
    swap="$(sysctl -n vm.swapusage 2>/dev/null | awk '{print $6}') swap used"
    pressure=", pressure $(sysctl -n kern.memorystatus_vm_pressure_level 2>/dev/null)"
  fi
  echo "load $load, $free, $swap${pressure:-}"
}

report() {
  echo "::group::What was running when the watchdog fired"
  processes
  vitals
  if command -v sample > /dev/null; then
    # Where the oldest few were stuck; sample takes about its duration in seconds.
    for p in $(targets | head -3); do
      echo "--- sample $p"
      sample "$p" 1 2>&1 | grep -v '^\s*$' | head -60
    done
  fi
  echo "::endgroup::"
}

# Writes the heartbeat so far into the job's check run; best effort, never fails the step.
beat_history="$(mktemp)"
send_beat() {
  [ -n "$check_run" ] && [ -n "${GH_TOKEN:-}" ] && [ -n "${GITHUB_REPOSITORY:-}" ] || return 0
  local body
  body=$(python3 - "$1" "$beat_history" "$2" <<'PY'
import json, sys
title, history, snapshot = sys.argv[1], open(sys.argv[2]).read(), sys.argv[3]
text = "Heartbeat (run-with-watchdog.sh), every line written while the step ran:\n```\n" + history[-50000:] + "```\n" \
       + "Last look at the processes:\n```\n" + snapshot[-12000:] + "\n```"
print(json.dumps({"output": {"title": title, "summary": history.strip().splitlines()[-1] if history.strip() else title, "text": text}}))
PY
) || return 0
  curl -s -o /dev/null --max-time 10 -X PATCH -H "Authorization: Bearer $GH_TOKEN" -H "Accept: application/vnd.github+json" \
    "${GITHUB_API_URL:-https://api.github.com}/repos/$GITHUB_REPOSITORY/check-runs/$check_run" -d "$body" || true
}
beat() {
  echo "$(date -u +%H:%M:%S) +$((SECONDS - started))s $(vitals); $(targets | wc -l | tr -d ' ') processes" >> "$beat_history"
  send_beat "$1" "$(processes)"
}

started=$SECONDS
# setsid() makes the command the leader of a new session and process group, numbered by its pid. There's no setsid
# command on a Mac, and python3 is on every runner.
python3 -c '
import os, sys
os.setsid()
try:
    os.execvp(sys.argv[1], sys.argv[1:])
except OSError as error:
    print(f"run-with-watchdog.sh: {sys.argv[1]}: {error.strerror}", file=sys.stderr)
    sys.exit(127)
' "$@" &
pid=$!
trap 'echo "run-with-watchdog.sh: cancelled, ending the command"; end_all; beat "Cancelled after $((SECONDS - started)) s: $command_line"; exit 130' INT TERM

next_beat=$SECONDS
while kill -0 "$pid" 2>/dev/null; do
  if [ $((SECONDS - started)) -ge "$limit" ]; then
    echo "::error::$command_line was still running after ${limit} s, so the watchdog ended it and everything it started."
    report
    beat "Ended by the watchdog after ${limit} s: $command_line"
    end_all
    wait "$pid" 2>/dev/null
    exit 124
  fi
  if [ -n "$check_run" ] && [ "$SECONDS" -ge "$next_beat" ]; then
    beat "Running for $((SECONDS - started)) s: $command_line"
    next_beat=$((SECONDS + beat_every))
  fi
  # Short sleeps in the foreground: a background sleep would keep the step's output open after this script ends.
  sleep 1
done
wait "$pid"
status=$?
[ -n "$check_run" ] && beat "Finished in $((SECONDS - started)) s with exit status $status: $command_line"
exit $status
