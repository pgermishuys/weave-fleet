#!/usr/bin/env bash
# Weave Fleet's pre-push check: the cheap failures, caught in about a minute before a push instead of several
# minutes later in CI. Most red PR runs are a type error, a lint error or a build error a local check finds.
#
# git's pre-push hook calls it with the remote's name and URL as arguments and one line per pushed ref on stdin
# (`<local ref> <local sha> <remote ref> <remote sha>`). scripts/install-pre-push.sh appends the call to
# .git/hooks/pre-push and keeps whatever is already there (the Entire CLI's hook, say). To chain it from a hook of
# your own, add this line at the end (git passes the ref list on stdin, so the hooks before it must leave stdin
# for it, as Entire's does):
#
#   f="$(git rev-parse --show-toplevel)/scripts/pre-push-check.sh"; [ ! -f "$f" ] || bash "$f" "$@" || exit $?
#
# What a push changes: for a branch that moves forward, the files changed since the remote's commit; for a new
# branch, a force push or a remote commit this clone hasn't fetched, what the branch changes from main (its
# merge-base with <remote>/main). Deleting a branch and pushing a tag check nothing. The paths go through CI's own
# rule (.github/scripts/ci-changes.sh), and a check runs when CI would run the job it stands in for:
#
#   client   eslint on the changed files, the design lint, vue-tsc (incremental: its cache is in
#            client/node_modules/.cache), and the vitest files of the changed files (a changed test, or the tests
#            named after a changed file; more than 30 are left to CI)
#   phone    the app's tokens check, tsc and tests
#   dotnet   dotnet build of each project a changed file belongs to (the solution when a Directory.*, global.json,
#            .editorconfig, NuGet.config or the .slnx changes); analyzers and code style included
#   scripts  ci-changes.sh's and this script's own tests, when they change
#
# Left to CI because each takes minutes: eslint and vitest over everything, dotnet test, E2E, AOT, Desktop.
#
# It reads the working tree, so it checks a ref only when the pushed commit is the one checked out. Two pushes on
# one machine take turns (flock on a file under ~/.cache), and each heavy command runs under a memory cap
# (systemd-run --user --scope) where those exist; without them (Windows' Git Bash, macOS) it runs them as they are.
# It never installs anything: missing client/ or mobile/ node_modules skip those checks with a note.
# `git push --no-verify` skips it.
#
#   pre-push-check.sh [<remote> [<url>]] < ref lines    run the checks
#   pre-push-check.sh --plan [<remote>] < ref lines     print the checks it would run, one per line, and run none
#                                                       (missing tools don't count; the tests use this)
set -uo pipefail

plan=0
if [ "${1:-}" = "--plan" ]; then plan=1; shift; fi
remote="${1:-origin}"

here="$(cd "$(dirname "$0")" && pwd)"
classifier="$here/../.github/scripts/ci-changes.sh"
root="$(git rev-parse --show-toplevel 2>/dev/null)" || { echo "pre-push check: not in a git working tree, skipped" >&2; exit 0; }
cd "$root" || exit 0

say() { echo "$*" >&2; }
is_zero() { case "$1" in *[!0]*) return 1 ;; *) return 0 ;; esac; }
now() { if [ -n "${EPOCHREALTIME:-}" ]; then echo "${EPOCHREALTIME/,/.}"; else date +%s; fi; }
since() { awk -v a="$1" -v b="$(now)" 'BEGIN { printf "%.1fs", b - a }'; }

# ── What the push changes ────────────────────────────────────────────────────────────────────────────────────────
main_ref=""
for r in "refs/remotes/$remote/main" refs/remotes/origin/main; do
  if git rev-parse -q --verify "$r^{commit}" >/dev/null; then main_ref="$r"; break; fi
done
head="$(git rev-parse -q --verify 'HEAD^{commit}' 2>/dev/null || true)"

paths=""
everything=0
checked=0
while read -r lref lsha rref rsha _; do
  [ -n "${lref:-}" ] && [ -n "${rsha:-}" ] || continue
  if is_zero "$lsha"; then say "pre-push check: $rref is deleted, nothing to check"; continue; fi
  case "$rref" in
    refs/heads/*) ;;
    *) say "pre-push check: $rref isn't a branch, nothing to check"; continue ;;
  esac
  if [ "$lsha" != "$head" ]; then
    say "pre-push check: $rref gets ${lsha:0:10}, which isn't checked out (HEAD is ${head:0:10}): not checked here, CI will"
    continue
  fi
  base=""
  if ! is_zero "$rsha" && git cat-file -e "$rsha^{commit}" 2>/dev/null && git merge-base --is-ancestor "$rsha" "$lsha"; then
    base="$rsha"
  elif [ -n "$main_ref" ]; then
    base="$(git merge-base "$main_ref" "$lsha" 2>/dev/null || true)"
  fi
  checked=1
  if [ -z "$base" ]; then
    say "pre-push check: can't tell what $rref changes (no $remote/main to compare with), so every check runs"
    everything=1
    continue
  fi
  paths="$paths$(git diff --name-only --no-renames "$base" "$lsha")"$'\n'
done
paths="$(printf '%s' "$paths" | awk 'NF && !seen[$0]++')"

[ "$checked" -eq 1 ] || exit 0
if [ "$everything" -eq 0 ] && [ -z "$paths" ]; then say "pre-push check: no files changed, nothing to check"; exit 0; fi
count=0; [ -z "$paths" ] || count="$(printf '%s\n' "$paths" | wc -l | tr -d ' ')"

if [ ! -f "$classifier" ]; then say "pre-push check: $classifier is missing, skipped"; exit 0; fi
if [ "$everything" -eq 1 ]; then groups="$(bash "$classifier" --all 2>&1)"; else groups="$(printf '%s\n' "$paths" | bash "$classifier" 2>&1)"; fi \
  || { say "pre-push check: CI's rule failed, skipped: $groups"; exit 0; }
on() { printf '%s\n' "$groups" | grep -qx "$1=true"; }
changed() { printf '%s\n' "$paths" | grep -E "$1"; }

# ── Which checks ─────────────────────────────────────────────────────────────────────────────────────────────────
checks=()      # check names, in the order they run
notes=()       # checks that don't run, and why
lint_files=()  # client/-relative files for eslint
test_files=()  # client/-relative test files for vitest
projects=()    # project (or solution) files for dotnet build

if on client; then
  while IFS= read -r p; do
    [ -f "$p" ] && lint_files+=("${p#client/}")
  done < <(changed '^client/.*\.(ts|tsx|mts|cts|js|mjs|cjs|vue)$')
  [ "${#lint_files[@]}" -eq 0 ] || checks+=(client-eslint)
  checks+=(client-design-lint client-typecheck)

  tests="$(
    changed '^client/.*\.(ts|tsx|vue)$' | while IFS= read -r p; do
      case "$p" in *.d.ts) continue ;; esac
      if echo "$p" | grep -qE '\.(test|spec)\.[cm]?[jt]sx?$'; then [ -f "$p" ] && echo "$p"; continue; fi
      dir="$(dirname "$p")"; file="$(basename "$p")"; stem="${file%.*}"
      for t in "$dir/$stem".test.* "$dir/$stem".spec.* "$dir/$stem".*.test.* \
               "$dir/__tests__/$stem".test.* "$dir/__tests__/$stem".spec.* "$dir/__tests__/$stem".*.test.*; do
        [ -f "$t" ] && echo "$t" | grep -E '\.(test|spec)\.[cm]?[jt]sx?$'
      done
    done | awk 'NF && !seen[$0]++'
  )"
  while IFS= read -r t; do [ -n "$t" ] && test_files+=("${t#client/}"); done <<<"$tests"
  if [ "${#test_files[@]}" -gt 30 ]; then
    notes+=("client tests: ${#test_files[@]} test files go with the changed files, more than 30: left to CI")
  elif [ "${#test_files[@]}" -gt 0 ]; then
    checks+=(client-tests)
  fi
fi

if on phone; then checks+=(phone-tokens phone-typecheck phone-tests); fi

if on dotnet; then
  if [ "$everything" -eq 1 ] || changed '(^|/)Directory\.[^/]*$|^global\.json$|^[^/]*\.slnx$|^NuGet\.config$|^\.editorconfig$' >/dev/null; then
    projects=(WeaveFleet.slnx)
  else
    found="$(
      changed '^(src|tests|aspire)/' | while IFS= read -r p; do
        d="$(dirname "$p")"
        while [ "$d" != "." ] && [ "$d" != "/" ]; do
          if [ -d "$d" ]; then
            c="$(ls "$d"/*.csproj 2>/dev/null | head -n 1)"
            if [ -n "$c" ]; then echo "$c"; break; fi
          fi
          d="$(dirname "$d")"
        done
      done | sort -u
    )"
    while IFS= read -r c; do [ -n "$c" ] && projects+=("$c"); done <<<"$found"
  fi
  for c in ${projects[@]+"${projects[@]}"}; do checks+=("dotnet-build:$c"); done
fi

if changed '^\.github/scripts/ci-changes' >/dev/null; then checks+=(ci-changes-tests); fi
if changed '^scripts/(pre-push-check|test-pre-push-check|install-pre-push)\.sh$' >/dev/null; then checks+=(pre-push-tests); fi

if [ "$plan" -eq 1 ]; then
  for c in ${checks[@]+"${checks[@]}"}; do echo "$c"; done
  exit 0
fi

# Tools: a missing one skips its checks, never installs.
if [ -z "$(command -v bun 2>/dev/null)" ] && [ -x "$HOME/.bun/bin/bun" ]; then PATH="$HOME/.bun/bin:$PATH"; fi
has_bun=0; command -v bun >/dev/null 2>&1 && has_bun=1
skip_unless() { # skip_unless <prefix> <reason> <condition…>
  local prefix="$1" reason="$2"; shift 2
  "$@" >/dev/null 2>&1 && return 0
  local kept=() c dropped=0
  for c in ${checks[@]+"${checks[@]}"}; do
    case "$c" in "$prefix"*) dropped=1 ;; *) kept+=("$c") ;; esac
  done
  checks=(${kept[@]+"${kept[@]}"})
  [ "$dropped" -eq 0 ] || notes+=("$reason")
}
skip_unless client- "client checks: client/node_modules is missing (cd client && bun install), skipped" \
  test "$has_bun" -eq 1 -a -f client/node_modules/vue-tsc/package.json -a -f client/node_modules/eslint/package.json
skip_unless phone- "phone checks: mobile/node_modules or client/node_modules is missing (bun install in each), skipped" \
  test "$has_bun" -eq 1 -a -f mobile/node_modules/typescript/package.json -a -f client/node_modules/vue/package.json
skip_unless dotnet- "dotnet build: dotnet isn't on PATH, skipped" command -v dotnet

jobs="$(printf '%s\n' "$groups" | sed -n 's/=true$//p' | tr '\n' ' ' | sed 's/ $//')"
say "pre-push check: $count changed files; CI would run: ${jobs:-nothing}"
for n in ${notes[@]+"${notes[@]}"}; do say "  skip  $n"; done
if [ "${#checks[@]}" -eq 0 ]; then say "pre-push check: nothing here to check before the push"; exit 0; fi
if [ -n "$(git status --porcelain --untracked-files=no 2>/dev/null)" ]; then
  say "  note  the working tree has uncommitted changes, and the checks read them too"
fi

# ── Running them ─────────────────────────────────────────────────────────────────────────────────────────────────
cache="${XDG_CACHE_HOME:-$HOME/.cache}/weave-fleet/pre-push"
mkdir -p "$cache"

# One push's checks at a time on this machine. The lock's descriptor is closed for every command (9>&-), so a
# build server a command leaves behind can't hold it.
if command -v flock >/dev/null 2>&1; then
  exec 9>"$cache/lock"
  if ! flock -n 9; then
    say "  wait  another push's checks are running on this machine"
    flock 9
  fi
fi

capped=0
if command -v systemd-run >/dev/null 2>&1 && systemd-run --user --scope -q -p MemoryMax=64M -- true >/dev/null 2>&1 9>&-; then capped=1; fi
run_capped() { # run_capped <memory> <command…>
  local mem="$1"; shift
  if [ "$capped" -eq 1 ]; then systemd-run --user --scope -q -p MemoryMax="$mem" -p MemorySwapMax=0 -- "$@"
  else "$@"; fi
}

node_opts="${NODE_OPTIONS:-}"
# Node 25 and later turn on Web Storage, which replaces jsdom's localStorage in vitest.
if command -v node >/dev/null 2>&1 && node -e 'process.exit(+process.versions.node.split(".")[0] >= 25 ? 0 : 1)' 2>/dev/null; then
  test_node_opts="$node_opts --no-experimental-webstorage"
else
  test_node_opts="$node_opts"
fi

started="$(now)"
failed=0
run_check() { # run_check <name> <label> <dir> <memory> <rerun command> <command…>
  local name="$1" label="$2" dir="$3" mem="$4" rerun="$5"; shift 5
  local log="$cache/${name//[:\/]/_}.log" t rc
  t="$(now)"
  (cd "$dir" && run_capped "$mem" "$@") </dev/null >"$log" 2>&1 9>&-
  rc=$?
  if [ "$rc" -eq 0 ]; then
    say "$(printf '  ok    %-50s %8s' "$label" "$(since "$t")")"
    return 0
  fi
  failed=$((failed + 1))
  say "$(printf '  FAIL  %-50s %8s' "$label" "$(since "$t")")"
  tail -n 40 "$log" | sed 's/^/        /' >&2
  [ "$rc" -ne 137 ] || say "        (killed: it went over its $mem memory cap)"
  say "        full log: $log"
  say "        rerun:    $rerun"
}

for c in "${checks[@]}"; do
  case "$c" in
    client-eslint)
      run_check "$c" "client eslint (${#lint_files[@]} changed files)" client 2500M \
        "(cd client && bun x eslint --no-warn-ignored ${lint_files[*]})" \
        bun x eslint --no-warn-ignored "${lint_files[@]}" ;;
    client-design-lint)
      run_check "$c" "client design lint" client 1000M \
        "(cd client && bun run lint:design)" \
        bun scripts/lint-design-system.ts ;;
    client-typecheck)
      mkdir -p client/node_modules/.cache/pre-push
      run_check "$c" "client typecheck (vue-tsc)" client 4000M \
        "(cd client && bun x vue-tsc --noEmit)" \
        env NODE_OPTIONS="$node_opts --max-old-space-size=3500" bun x vue-tsc --noEmit --incremental \
          --tsBuildInfoFile node_modules/.cache/pre-push/vue-tsc.tsbuildinfo ;;
    client-tests)
      run_check "$c" "client tests (${#test_files[@]} files)" client 2500M \
        "(cd client && bun x vitest run ${test_files[*]})" \
        env NODE_OPTIONS="$test_node_opts" bun x vitest run --maxWorkers=2 "${test_files[@]}" ;;
    phone-tokens)
      run_check "$c" "phone tokens match the client" mobile 1000M \
        "(cd mobile && bun run tokens:check)" bun scripts/tokens.ts --check ;;
    phone-typecheck)
      run_check "$c" "phone typecheck (tsc)" mobile 2500M \
        "(cd mobile && bun x tsc --noEmit)" bun x tsc --noEmit ;;
    phone-tests)
      run_check "$c" "phone tests" mobile 2000M \
        "(cd mobile && bun test src scripts)" bun test src scripts ;;
    dotnet-build:*)
      p="${c#dotnet-build:}"
      run_check "$c" "dotnet build $(basename "$p")" . 3000M \
        "dotnet build $p -p:SkipFrontendBuild=true" \
        dotnet build "$p" -nologo -v:q -p:SkipFrontendBuild=true ;;
    ci-changes-tests)
      run_check "$c" "ci-changes.sh tests" . 500M "bash .github/scripts/ci-changes.test.sh" \
        bash .github/scripts/ci-changes.test.sh ;;
    pre-push-tests)
      run_check "$c" "pre-push-check.sh tests" . 500M "bash scripts/test-pre-push-check.sh" \
        bash scripts/test-pre-push-check.sh ;;
  esac
done

if [ "$failed" -gt 0 ]; then
  say "pre-push check: $failed of ${#checks[@]} checks failed ($(since "$started")). The push is stopped; fix them and push again."
  say "pre-push check: \`git push --no-verify\` pushes without these checks."
  exit 1
fi
say "pre-push check: ${#checks[@]} checks passed ($(since "$started"))"
