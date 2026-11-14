#!/usr/bin/env bash
# Checks which checks pre-push-check.sh picks for a push, against fixed cases. Each case makes a commit in a
# throwaway repository, feeds the script the ref line git's pre-push hook would, and compares `--plan`'s list.
# The Launchers job runs it on Linux and on Windows (Git Bash).
set -uo pipefail
here="$(cd "$(dirname "$0")" && pwd)"
check="$here/pre-push-check.sh"
work="$(mktemp -d "${TMPDIR:-/tmp}/pre-push-test.XXXXXX")"
trap 'rm -rf "$work"' EXIT
cd "$work" || exit 1

zero=0000000000000000000000000000000000000000
g() { git -c user.name=test -c user.email=test@example.com -c commit.gpgsign=false "$@"; }
put() { for f in "$@"; do mkdir -p "$(dirname "$f")"; echo "$RANDOM $f" >>"$f"; done; }
commit() { put "$@"; g add -A && g commit -q --no-verify -m "change $*"; }

g init -q -b main .
put client/src/lib/foo.ts client/src/lib/__tests__/foo.test.ts client/src/lib/bar.ts \
  client/src/components/Bar.vue client/src/components/__tests__/Bar.render.test.ts \
  client/src/components/__tests__/Bar.keys.test.ts client/src/components/__tests__/Other.test.ts \
  client/vite.config.ts client/src/types.d.ts \
  src/WeaveFleet.Application/WeaveFleet.Application.csproj src/WeaveFleet.Application/Foo.cs \
  tests/WeaveFleet.Api.Tests/WeaveFleet.Api.Tests.csproj tests/WeaveFleet.Api.Tests/Sub/Deep/X.cs \
  mobile/src/app.tsx docs/machines.md README.md Directory.Packages.props
for i in $(seq 1 31); do put "client/src/many/__tests__/m$i.test.ts"; done
g add -A && g commit -q --no-verify -m base
g update-ref refs/remotes/origin/main HEAD

fail=0
n=0
client="client-design-lint client-typecheck"
phone="phone-tokens phone-typecheck phone-tests"
app=dotnet-build:src/WeaveFleet.Application/WeaveFleet.Application.csproj
api=dotnet-build:tests/WeaveFleet.Api.Tests/WeaveFleet.Api.Tests.csproj

# expect <name> <expected checks> <ref lines…>: runs --plan with the ref lines on stdin.
expect() {
  local name="$1" want="$2"; shift 2
  n=$((n + 1))
  local got
  got="$(printf '%s\n' "$@" | bash "$check" --plan origin 2>/dev/null | sort | tr '\n' ' ' | sed 's/ $//')"
  want="$(tr ' ' '\n' <<<"$want" | awk NF | sort | tr '\n' ' ' | sed 's/ $//')"
  if [ "$got" != "$want" ]; then
    echo "::error::pre-push-check.sh, $name: runs [$got], expected [$want]"; fail=1
  fi
}
# branch <name> <files…>: a new branch off main with one commit changing the files; prints nothing.
branch() { local b="$1"; shift; g checkout -q -B "$b" main && commit "$@"; }
new_branch_line() { echo "refs/heads/$1 $(git rev-parse HEAD) refs/heads/$1 $zero"; }

branch client-only client/src/lib/foo.ts
expect "client-only" "client-eslint $client client-tests $phone" "$(new_branch_line client-only)"

branch client-vue client/src/components/Bar.vue
expect "a .vue file runs its __tests__ files" "client-eslint $client client-tests $phone" "$(new_branch_line client-vue)"

branch client-no-tests client/src/lib/bar.ts
expect "a file with no tests of its own" "client-eslint $client $phone" "$(new_branch_line client-no-tests)"

branch client-config client/vite.config.ts
expect "client/ outside src/" "client-eslint $client" "$(new_branch_line client-config)"

branch client-dts client/src/types.d.ts
expect "a .d.ts has no tests" "client-eslint $client $phone" "$(new_branch_line client-dts)"

branch client-test client/src/components/__tests__/Other.test.ts
expect "a changed test runs" "client-eslint $client client-tests $phone" "$(new_branch_line client-test)"

g checkout -q -B client-delete main && g rm -q client/src/lib/bar.ts && g commit -q --no-verify -m delete
expect "a deleted client file isn't linted" "$client $phone" "$(new_branch_line client-delete)"

branch client-many client/src/many/__tests__/m{1..31}.test.ts
expect "more than 30 test files are left to CI" "client-eslint $client $phone" "$(new_branch_line client-many)"

branch phone-only mobile/src/app.tsx
expect "phone-only" "$phone" "$(new_branch_line phone-only)"

branch server-only src/WeaveFleet.Application/Foo.cs
expect "server-only" "$app" "$(new_branch_line server-only)"

branch server-nested tests/WeaveFleet.Api.Tests/Sub/Deep/X.cs
expect "a file deep in a test project" "$api" "$(new_branch_line server-nested)"

branch server-new-file src/WeaveFleet.Application/New/Thing.cs
expect "a new file in a new folder" "$app" "$(new_branch_line server-new-file)"

branch build-props Directory.Packages.props
expect "a repo-wide build file builds the solution" "$client $phone dotnet-build:WeaveFleet.slnx" "$(new_branch_line build-props)"

branch tests-props tests/Directory.Build.props
expect "a nested Directory.Build.props builds the solution" "dotnet-build:WeaveFleet.slnx" "$(new_branch_line tests-props)"

branch docs-only docs/machines.md README.md mockups/x/a.png .weave/plans/p.md
expect "docs-only" "" "$(new_branch_line docs-only)"

branch mixed client/src/lib/foo.ts src/WeaveFleet.Application/Foo.cs docs/machines.md
expect "mixed" "client-eslint $client client-tests $phone $app" "$(new_branch_line mixed)"

branch rule .github/scripts/ci-changes.sh
expect "CI's rule runs the client and phone checks and its tests (no project changed: nothing to build)" "$client $phone ci-changes-tests" \
  "$(new_branch_line rule)"

branch itself scripts/pre-push-check.sh
expect "this script runs its own tests" "pre-push-tests" "$(new_branch_line itself)"

# A branch that moves forward: only what changed since the remote's commit.
branch forward client/src/lib/foo.ts
before="$(git rev-parse HEAD)"
commit docs/machines.md
expect "a fast-forward push checks only the new commits" "" "refs/heads/forward $(git rev-parse HEAD) refs/heads/forward $before"

# A force push (the remote's commit isn't an ancestor): what the branch changes from main.
branch rebased client/src/lib/foo.ts
old="$(git rev-parse HEAD)"
g commit -q --no-verify --amend -m "amended"
expect "a force push checks the branch against main" "client-eslint $client client-tests $phone" \
  "refs/heads/rebased $(git rev-parse HEAD) refs/heads/rebased $old"

# A remote commit this clone hasn't fetched: the branch against main.
branch unfetched src/WeaveFleet.Application/Foo.cs
expect "an unknown remote commit checks the branch against main" "$app" \
  "refs/heads/unfetched $(git rev-parse HEAD) refs/heads/unfetched 1234567890123456789012345678901234567890"

expect "deleting a branch checks nothing" "" "(delete) $zero refs/heads/old $(git rev-parse main)"
expect "a tag checks nothing" "" "refs/tags/v1 $(git rev-parse HEAD) refs/tags/v1 $zero"
expect "a commit that isn't checked out isn't checked" "" "refs/heads/other $(git rev-parse main) refs/heads/other $zero"
expect "no ref lines check nothing" ""

# Pushing two branches: the checked-out one counts, the other is noted.
branch two src/WeaveFleet.Application/Foo.cs
expect "two refs, one checked out" "$app" "$(new_branch_line two)" "refs/heads/main $(git rev-parse main) refs/heads/main $zero"

# Without origin/main a new branch can't be compared, so everything runs.
branch no-main client/src/lib/foo.ts
g update-ref -d refs/remotes/origin/main
expect "no origin/main runs everything" "$client $phone dotnet-build:WeaveFleet.slnx" "$(new_branch_line no-main)"

[ "$fail" -eq 0 ] && echo "pre-push-check.sh: $n cases pass"
exit "$fail"
