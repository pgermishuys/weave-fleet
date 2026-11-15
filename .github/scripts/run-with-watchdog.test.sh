#!/usr/bin/env bash
# Checks run-with-watchdog.sh on the runner it guards: the command runs in a process group of its own, its exit status
# comes back, and at the limit, or when cancelled, it ends the command with everything it started, including a process
# that left its group. On a GitHub Mac, a watchdog that stopped and killed processes in the runner's own group took the
# runner with it, and this step never finished.
set -uo pipefail
here="$(cd "$(dirname "$0")" && pwd)"
dog="$here/run-with-watchdog.sh"
marker="watchdog-test-$$-$RANDOM"
log="$(mktemp)"
failed=0
fail() { echo "FAIL: $*"; failed=1; }
left() { pgrep -f -- "$marker" | grep -vx "$$" || true; }
# Each process holds the marker after its sleep, so bash can't exec the sleep in its place and drop the marker.

# The command's own status comes back.
"$dog" 10 bash -c 'exit 3' > /dev/null; [ $? -eq 3 ] || fail "exit status 3 wasn't passed through"
"$dog" 10 true > /dev/null || fail "a command that succeeds failed"
"$dog" 10 no-such-command-here > /dev/null 2>&1; [ $? -eq 127 ] || fail "a command that doesn't exist didn't exit 127"

# The command's process group isn't this one (on a Mac, the runner's), so nothing it does reaches the runner.
mine=$(ps -o pgid= -p $$ | tr -d ' ')
theirs=$("$dog" 10 bash -c 'ps -o pgid= -p $$' | tr -d ' ')
[ -n "$theirs" ] && [ "$theirs" != "$mine" ] || fail "the command ran in this process group ($mine), not one of its own ($theirs)"

# A process that leaves the command's group and its parent, as a daemon would: only WATCHDOG_ALSO finds it.
tree="$(mktemp)"
cat > "$tree" <<EOF
( python3 -c 'import os, sys; os.setsid(); os.execvp("bash", ["bash", "-c", "sleep 600; : $marker escaped"])' & )
( bash -c 'sleep 600; : $marker orphan' & )
bash -c 'sleep 600; : $marker child' &
sleep 600
EOF

# At the limit: everything it started is gone, a process that left the tree too, and it says why.
start=$SECONDS
WATCHDOG_ALSO="$marker" "$dog" 2 bash "$tree" > "$log" 2>&1
status=$?
took=$((SECONDS - start))
[ $status -eq 124 ] || fail "a command past its limit exited $status, not 124"
[ $took -le 15 ] || fail "ending a command 2 s past its limit took ${took} s"
grep -q '::error::' "$log" || fail "nothing said why the command was ended"
[ -z "$(left)" ] || { fail "left running: $(left | tr '\n' ' ')"; pkill -KILL -f -- "$marker"; }

# Cancelled (the runner's step timeout sends SIGINT, then SIGTERM): it ends the command too.
# It waits for the command's child itself: the watchdog's own command line holds the marker too, so waiting for any
# match sent the TERM as soon as the watchdog started.
"$dog" 60 bash -c "bash -c 'sleep 600; : $marker cancelled' & sleep 600; : $marker" > "$log" 2>&1 &
dog_pid=$!
child() { pgrep -f -- "^bash -c sleep 600; : $marker cancelled\$" || true; }
for _ in $(seq 1 50); do [ -n "$(child)" ] && break; sleep 0.1; done
[ -n "$(child)" ] || fail "the command to cancel didn't start its child within 5 s"
kill -TERM "$dog_pid"
wait "$dog_pid"; status=$?
[ $status -eq 130 ] || fail "a cancelled watchdog exited $status, not 130"
[ -z "$(left)" ] || { fail "left running after cancel: $(left | tr '\n' ' ')"; pkill -KILL -f -- "$marker"; }

# The heartbeat: written into the check run while the command runs, and once more with how it ended. A stand-in for
# GitHub's API records the title of each PATCH it's sent.
api_dir="$(mktemp -d)"
python3 -c '
import http.server, json, os, sys
out = sys.argv[1]
class H(http.server.BaseHTTPRequestHandler):
    def do_PATCH(self):
        body = json.loads(self.rfile.read(int(self.headers["Content-Length"])))
        with open(os.path.join(out, "beats"), "a") as f:
            f.write(self.path + " " + body["output"]["title"] + "\n")
        self.send_response(200); self.end_headers(); self.wfile.write(b"{}")
    def log_message(self, *a): pass
s = http.server.HTTPServer(("127.0.0.1", 0), H)
open(os.path.join(out, "port.tmp"), "w").write(str(s.server_address[1]))
os.rename(os.path.join(out, "port.tmp"), os.path.join(out, "port"))
s.serve_forever()
' "$api_dir" > /dev/null 2>&1 &
api_pid=$!
for _ in $(seq 1 50); do [ -s "$api_dir/port" ] && break; sleep 0.1; done
beat_env=(GITHUB_API_URL="http://127.0.0.1:$(cat "$api_dir/port")" GITHUB_REPOSITORY=owner/repo GH_TOKEN=token
  WATCHDOG_CHECK_RUN=42 WATCHDOG_BEAT_SECONDS=1)
env "${beat_env[@]}" "$dog" 10 sleep 3 > /dev/null 2>&1 || fail "a command with a heartbeat failed"
beats=$(grep -c "^/repos/owner/repo/check-runs/42 Running for" "$api_dir/beats" 2>/dev/null)
[ "${beats:-0}" -ge 2 ] || fail "${beats:-0} heartbeats while a 3 s command ran, not 2 or more"
tail -1 "$api_dir/beats" 2>/dev/null | grep -q " Finished in [0-9]* s with exit status 0: sleep 3$" \
  || fail "the last heartbeat didn't say how the command ended"
: > "$api_dir/beats"
env "${beat_env[@]}" "$dog" 2 sleep 30 > /dev/null 2>&1
tail -1 "$api_dir/beats" | grep -q " Ended by the watchdog after 2 s: sleep 30$" \
  || fail "the last heartbeat didn't say the watchdog ended the command"
kill "$api_pid" 2>/dev/null
rm -rf "$api_dir"

rm -f "$log" "$tree"
[ $failed -eq 0 ] && echo "run-with-watchdog.sh: all cases pass"
exit $failed
