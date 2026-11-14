#!/usr/bin/env bash
# Tests scripts/release-ci-gate.sh against a fake `gh` that answers from fixture files, in a throwaway git repository:
# green, red, cancelled, still running then green or red, never ran, a "CI passed" job that failed, and an API that
# keeps failing. Needs no network and no token.
set -euo pipefail
export LC_ALL=C

SCRIPT_DIR="$(cd "$(dirname "$0")" && pwd)"
GATE="$SCRIPT_DIR/release-ci-gate.sh"
WORK="$(mktemp -d)"
trap 'rm -rf "$WORK"' EXIT

fail() { echo "FAIL: $*" >&2; exit 1; }

# The fake gh. `gh api <path> …` answers runs?head_sha=<sha> from runs-<sha> and runs/<id>/jobs from jobs-<id>
# (already in the gate's --jq output shape). A file with ".seq" lines separated by "---" answers one block per call
# and then repeats its last. A file named api-down makes every call fail.
mkdir -p "$WORK/bin" "$WORK/fixtures"
cat > "$WORK/bin/gh" <<'SH'
#!/usr/bin/env bash
set -euo pipefail
dir="$FAKE_GH_DIR"
echo "$*" >> "$dir/calls"
[ -e "$dir/api-down" ] && { echo "HTTP 502" >&2; exit 1; }
path="$2"
case "$path" in
  *"/runs?head_sha="*) sha="${path#*head_sha=}"; key="runs-${sha%%&*}" ;;
  *"/jobs?"*) id="${path#*/runs/}"; key="jobs-${id%%/*}" ;;
  *) echo "fake gh: unexpected $path" >&2; exit 2 ;;
esac
if [ -f "$dir/$key.seq" ]; then
  n=$(( $(cat "$dir/$key.count" 2>/dev/null || echo 0) + 1 ))
  echo "$n" > "$dir/$key.count"
  blocks=$(grep -c '^---$' "$dir/$key.seq" || true)
  [ "$n" -gt $(( blocks + 1 )) ] && n=$(( blocks + 1 ))
  awk -v want="$n" 'BEGIN{b=1} /^---$/{b++; next} b==want' "$dir/$key.seq"
elif [ -f "$dir/$key" ]; then
  cat "$dir/$key"
fi
SH
chmod +x "$WORK/bin/gh"

# A repository: a code commit, then a docs-only commit on top of it.
REPO="$WORK/repo"
git init -q "$REPO"
mkdir -p "$REPO/src"
echo one > "$REPO/src/app.cs"
git -C "$REPO" add -A && git -C "$REPO" -c user.name=t -c user.email=t@t commit -q -m code
CODE=$(git -C "$REPO" rev-parse HEAD)
echo notes > "$REPO/README.md"
git -C "$REPO" add -A && git -C "$REPO" -c user.name=t -c user.email=t@t commit -q -m docs
DOCS=$(git -C "$REPO" rev-parse HEAD)

case_no=0
# run_gate <name> <sha> <expected exit> <expected output fragment>; fixtures are written by the caller first.
run_gate() {
  local name="$1" sha="$2" want_exit="$3" want_text="$4" got_exit=0
  case_no=$((case_no + 1))
  (cd "$REPO" && PATH="$WORK/bin:$PATH" FAKE_GH_DIR="$WORK/fixtures" GH_REPO=acme/fleet \
    GATE_POLL_SECONDS=0 GATE_RETRY_SECONDS=0 GATE_NO_RUN_GRACE_SECONDS="${GRACE:-0}" GATE_TIMEOUT_SECONDS="${TIMEOUT:-60}" \
    GITHUB_STEP_SUMMARY="$WORK/summary" GITHUB_RUN_ID=777 bash "$GATE" "$sha" v9.9.9) > "$WORK/out" 2>&1 || got_exit=$?
  if [ "$got_exit" != "$want_exit" ]; then
    cat "$WORK/out" >&2
    fail "$name: exit $got_exit, expected $want_exit"
  fi
  grep -qF -- "$want_text" "$WORK/out" || { cat "$WORK/out" >&2; fail "$name: output lacks '$want_text'"; }
  # Every block says how to resume this very release run.
  if [ "$want_exit" = 1 ] && ! grep -q "HTTP 502" "$WORK/out"; then
    grep -qF "gh run rerun 777 --repo acme/fleet --failed" "$WORK/out" || { cat "$WORK/out" >&2; fail "$name: no resume command"; }
    grep -qF "Don't move, delete or re-push the tag" "$WORK/out" || fail "$name: doesn't say to leave the tag"
  fi
  echo "ok $case_no - $name"
}
reset() { rm -rf "$WORK/fixtures"; mkdir -p "$WORK/fixtures"; }
fx() { printf '%s\n' "$2" > "$WORK/fixtures/$1"; }

reset; fx "runs-$CODE" "11 completed success push"
run_gate "green push run passes" "$CODE" 0 "ci.yml passed on $CODE: https://github.com/acme/fleet/actions/runs/11"

reset; fx "runs-$CODE" "12 completed success push"; fx jobs-12 "success"
run_gate "green run with a green CI passed job passes" "$CODE" 0 "actions/runs/12"

reset; fx "runs-$CODE" "13 completed success push"; fx jobs-13 "failure"
run_gate "green run whose CI passed job failed is blocked" "$CODE" 1 "its \"CI passed\" job is failure"

reset; fx "runs-$CODE" "14 completed failure push"
run_gate "red run is blocked at once" "$CODE" 1 "ci.yml didn't pass on $CODE (v9.9.9)"
grep -qF "gh run view 14 --repo acme/fleet --log-failed" "$WORK/out" || fail "red run: no log command"
grep -qF "gh run rerun 14 --repo acme/fleet --failed" "$WORK/out" || fail "red run: no re-run command"

reset; fx "runs-$CODE" "15 completed cancelled push"
run_gate "cancelled run is blocked" "$CODE" 1 "run 15 (push): cancelled"
grep -qF "gh workflow run ci.yml --repo acme/fleet --ref v9.9.9" "$WORK/out" || fail "cancelled run: no command to run CI on the tag"

reset; fx "runs-$CODE" "$(printf '16 completed failure push\n17 completed success workflow_dispatch')"
run_gate "a red run and a green re-run by dispatch passes" "$CODE" 0 "actions/runs/17"

reset; printf '18 queued none push\n---\n18 in_progress none push\n---\n18 completed success push\n' > "$WORK/fixtures/runs-$CODE.seq"
run_gate "waits while CI runs, then passes" "$CODE" 0 "actions/runs/18"
grep -qF "is still running" "$WORK/out" || fail "in progress: never said it was waiting"

reset; printf '19 in_progress none push\n---\n19 completed failure push\n' > "$WORK/fixtures/runs-$CODE.seq"
run_gate "waits while CI runs, then blocks on red" "$CODE" 1 "run 19 (push): failure"

reset; fx "runs-$CODE" "20 in_progress none push"
TIMEOUT=0 run_gate "gives up on a run that never finishes" "$CODE" 1 "still running after 0s"
grep -qF "gh run watch 20 --repo acme/fleet --exit-status" "$WORK/out" || fail "still running: no watch command"

reset
run_gate "a commit CI never ran on is blocked" "$CODE" 1 "ci.yml never ran on $CODE (v9.9.9)"
grep -qF "gh workflow run ci.yml --repo acme/fleet --ref v9.9.9" "$WORK/out" || fail "never ran: no command to run CI on the tag"

reset; printf '\n---\n21 completed success push\n' > "$WORK/fixtures/runs-$CODE.seq"
GRACE=60 run_gate "waits for a run that hasn't started yet" "$CODE" 0 "actions/runs/21"

reset; fx "runs-$CODE" "22 completed success push"
run_gate "a docs-only commit needs its own run: its parent's green one doesn't count" "$DOCS" 1 "ci.yml never ran on $DOCS"

reset; touch "$WORK/fixtures/api-down"
run_gate "an API that keeps failing blocks, never reads as no runs" "$CODE" 1 "HTTP 502"
calls=$(grep -c "^api " "$WORK/fixtures/calls")
[ "$calls" -eq 5 ] || fail "API down: expected 5 attempts, got $calls"
echo "ok $((case_no + 1)) - the API is tried 5 times"

echo "All release CI gate tests passed."
