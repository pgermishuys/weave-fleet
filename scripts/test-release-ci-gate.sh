#!/usr/bin/env bash
# Tests scripts/release-ci-gate.sh against a fake `gh` that answers from fixture files, in a throwaway git repository:
# green, red, cancelled, still running then green or red, never ran, a "CI passed" job that failed, an API that keeps
# failing, and the tag checks a real tag push gets (tag = Directory.Build.props' version, commit on main) next to a dry
# run that skips them. Needs no network and no token.
set -euo pipefail
export LC_ALL=C

SCRIPT_DIR="$(cd "$(dirname "$0")" && pwd)"
GATE="$SCRIPT_DIR/release-ci-gate.sh"
WORK="$(mktemp -d)"
trap 'rm -rf "$WORK"' EXIT

fail() { echo "FAIL: $*" >&2; exit 1; }

# The fake gh. `gh api <path> …` answers runs?head_sha=<sha> from runs-<sha>, runs/<id>/jobs from jobs-<id> and
# compare/main...<sha> from compare-<sha> (already in the gate's --jq output shape). A file with ".seq" lines separated by "---" answers one block per call
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
  *"/compare/"*) key="compare-${path##*...}" ;;
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

# A repository: a commit with no Directory.Build.props, a code commit that sets the version to 9.9.9, then a docs-only
# commit on top of it.
REPO="$WORK/repo"
git init -q "$REPO"
git -C "$REPO" -c user.name=t -c user.email=t@t commit -q --allow-empty -m root
ROOT=$(git -C "$REPO" rev-parse HEAD)
mkdir -p "$REPO/src"
printf '<Project>\n  <PropertyGroup>\n    <Version>9.9.9</Version>\n  </PropertyGroup>\n</Project>\n' > "$REPO/Directory.Build.props"
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
    GATE_CHECK_TAG="${CHECK:-false}" GITHUB_STEP_SUMMARY="$WORK/summary" GITHUB_RUN_ID=777 \
    bash "$GATE" "$sha" "${TAG:-v9.9.9}") > "$WORK/out" 2>&1 || got_exit=$?
  if [ "$got_exit" != "$want_exit" ]; then
    cat "$WORK/out" >&2
    fail "$name: exit $got_exit, expected $want_exit"
  fi
  grep -qF -- "$want_text" "$WORK/out" || { cat "$WORK/out" >&2; fail "$name: output lacks '$want_text'"; }
  # Every CI block says how to resume this very release run. (A wrong tag is fixed by a new tag instead.)
  if [ "$want_exit" = 1 ] && [ -z "${TAG_BLOCK:-}" ] && ! grep -q "HTTP 502" "$WORK/out"; then
    grep -qF "gh run rerun 777 --repo acme/fleet --failed" "$WORK/out" || { cat "$WORK/out" >&2; fail "$name: no resume command"; }
    grep -qF "Don't move, delete or re-push the tag" "$WORK/out" || fail "$name: doesn't say to leave the tag"
  fi
  echo "ok $case_no - $name"
}
reset() { rm -rf "$WORK/fixtures" "$WORK/summary"; mkdir -p "$WORK/fixtures"; }
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
echo "ok $((case_no += 1)) - the API is tried 5 times"

# The tag checks, as a real tag push runs them.
reset; fx "runs-$CODE" "30 completed success push"; fx "compare-$CODE" "identical"
CHECK=true run_gate "tag push: tag matches the version, commit is main, CI green: passes" "$CODE" 0 "v9.9.9 is 9.9.9 on $CODE, which is on main."

reset; fx "runs-$DOCS" "31 completed success push"; fx "compare-$DOCS" "behind"
CHECK=true run_gate "tag push: a commit main has moved past is on main" "$DOCS" 0 "actions/runs/31"

reset; fx "runs-$CODE" "32 completed success push"; fx "compare-$CODE" "identical"
TAG=v9.9.10 TAG_BLOCK=1 CHECK=true run_gate "tag push: a tag that isn't the commit's version is blocked" "$CODE" 1 \
  "The tag v9.9.10 doesn't match the version on its commit: Directory.Build.props at $CODE says 9.9.9."
grep -qF "git push origin :refs/tags/v9.9.10" "$WORK/out" || fail "version mismatch: no command to remove the tag"
grep -q "head_sha" "$WORK/fixtures/calls" 2>/dev/null && fail "version mismatch: still waited for CI"
echo "ok $((case_no += 1)) - a wrong tag is blocked before waiting for CI"

reset; fx "runs-$ROOT" "33 completed success push"; fx "compare-$ROOT" "behind"
TAG_BLOCK=1 CHECK=true run_gate "tag push: a commit with no Directory.Build.props is blocked" "$ROOT" 1 "has no Directory.Build.props"

for status in ahead diverged; do
  reset; fx "runs-$CODE" "34 completed success push"; fx "compare-$CODE" "$status"
  TAG_BLOCK=1 CHECK=true run_gate "tag push: a commit that isn't on main ($status) is blocked" "$CODE" 1 \
    "$CODE (v9.9.9) isn't on main (compared with main: $status). Releases come only from main."
  grep -qF "Tag the release commit on main" "$WORK/out" || fail "off main: no recovery"
done

reset; fx "runs-$CODE" "35 completed success push"; fx "compare-$CODE" "diverged"
TAG=v1.2.3 run_gate "dry run: skips both tag checks, says so, and passes on green CI" "$CODE" 0 "Dry run: skipped the tag checks"
grep -qF "Dry run: skipped the tag checks" "$WORK/summary" || fail "dry run: the summary doesn't say the tag checks were skipped"
grep -q "/compare/" "$WORK/fixtures/calls" && fail "dry run: still asked whether the commit is on main"
echo "ok $((case_no += 1)) - a dry run doesn't ask GitHub whether the commit is on main"

echo "All release CI gate tests passed."
