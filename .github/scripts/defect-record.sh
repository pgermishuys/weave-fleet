#!/usr/bin/env bash
# Checks that a fix PR's body carries its defect record. The Defect record workflow runs it on every pull request,
# and again whenever the title or body is edited.
#
#   defect-record.sh "<PR title>" < body.md
#
# A fix PR is one whose title starts with fix: / fix(scope): / fix!: (any case). That's the only signal: branch names
# don't say (Fleet sessions branch as fleet/session-…, UI work as ui/…, and fix/ branches carry test: and feat: PRs
# too) and the repository uses no labels. test:, ci: and the rest aren't checked.
#
# The record is four lines anywhere in the body (bold and list markers optional; HTML comments and fenced code blocks
# don't count, so a template's hints never pass for a filled-in record):
#   Found by:   you | report | bug-check | CI | live check | in passing | review | flake   (detail may follow)
#   Cause:      #123, a commit sha, a PR or commit URL, old, or external                 (detail may follow)
#   Shipped in: the first release with the bug (v0.71.0), or not released                (detail may follow)
#   Guard:      the test, lint rule or CI check that stops it coming back; "none" only with a reason after it
# Exit 0: not a fix PR, or the record is complete. Exit 1: what's missing, and the lines to paste.
set -euo pipefail

title="${1-}"
body="$(cat)"

shopt -s nocasematch
if ! [[ "$title" =~ ^[[:space:]]*fix(\([^\)]*\))?!?: ]]; then
  echo "Not a fix PR (the title doesn't start with fix: or fix(…):), so no defect record is needed."
  exit 0
fi

# Drop carriage returns (bodies edited on github.com use CRLF), HTML comments, fenced code blocks, bold/italic
# markers, and leading list markers or quote marks, so "- **Found by:** CI" reads as "Found by: CI".
clean="$(printf '%s\n' "$body" \
  | perl -0pe 's/\r//g; s/<!--.*?-->//gs' \
  | awk '/^[[:space:]]*(```|~~~)/ { fence = !fence; next } !fence' \
  | sed -E 's/\*\*|__//g; s/^[[:space:]]*(>[[:space:]]*)*([-*+][[:space:]]+)?//')"

# Backticks are fine around any value (`a1b2c3d`), so compare without them.
nb() { printf '%s' "$1" | tr -d '`'; }

# Each check prints what's wrong with a value, or nothing if it's fine.
check_found_by() {
  local v; v="$(nb "$1" | tr '_-' '  ' | tr -s ' ')"
  [[ "$v" =~ ^(you|report|bug check|ci|live check|in passing|review|flake)([^a-z0-9]|$) ]] \
    || echo "isn't one of: you, report, bug-check, CI, live check, in passing, review, flake."
}
check_cause() {
  local v; v="$(nb "$1")"
  [[ "$v" =~ ^(#[0-9]+|[0-9a-f]{7,40}|old|external|https://github\.com/[^/]+/[^/]+/(pull/[0-9]+|commit/[0-9a-f]{7,40}))([^a-z0-9]|$) ]] \
    || echo "should start with the PR that introduced the bug (#123), its commit sha, \"old\" (before anyone can tell) or \"external\" (a dependency, the runner, a harness)."
}
check_shipped_in() {
  local v; v="$(nb "$1")"
  [[ "$v" =~ ^(v?[0-9]+\.[0-9]+\.[0-9]+|not released)([^a-z0-9]|$) ]] \
    || echo "should be the first release that had the bug (v0.71.0) or \"not released\"."
}
check_guard() {
  local v reason; v="$(nb "$1")"
  if [[ "$v" =~ ^none([^a-z0-9]|$) ]]; then
    reason="$(printf '%s' "$v" | sed -E 's/^none[[:space:][:punct:]—–]*//')"
    [ "${#reason}" -ge 10 ] || echo "needs a reason after \"none\", e.g. \"none: the code path it broke is gone\"."
  elif [[ "$v" =~ ^(todo|tbd|n/?a|-+|\?+|\.\.\.|…)$ ]]; then
    echo "is a placeholder: name the test, lint rule or CI check that stops the bug coming back."
  fi
}

# A field is fine if any line "<name>: value" has a good value, so a prose "**Cause:** the store never read the
# error" higher up doesn't hide the record below it. Otherwise the first value found is reported.
problems=()
declare -A value=()
for name in "Found by" "Cause" "Shipped in" "Guard"; do
  check="check_$(tr 'A-Z ' 'a-z_' <<<"$name")"
  first=""; firsterr=""; good=""
  while IFS= read -r v; do
    [ -n "$v" ] || continue
    err="$($check "$v")"
    if [ -z "$err" ]; then good="$v"; break; fi
    [ -n "$first" ] || { first="$v"; firsterr="$err"; }
  done < <(printf '%s\n' "$clean" | grep -i -E "^$name[[:space:]]*:" | sed -E 's/^[^:]*:[[:space:]]*//; s/[[:space:]]+$//' || true)
  if [ -n "$good" ]; then value[$name]="$good"
  elif [ -n "$first" ]; then
    [ "${#first}" -le 80 ] || first="${first:0:77}..."
    problems+=("$name \"$first\" $firsterr")
  else problems+=("$name is missing or empty.")
  fi
done

if [ "${#problems[@]}" -eq 0 ]; then
  echo "Defect record complete."
  printf '  Found by:   %s\n  Cause:      %s\n  Shipped in: %s\n  Guard:      %s\n' "${value[Found by]}" "${value[Cause]}" "${value[Shipped in]}" "${value[Guard]}"
  exit 0
fi

echo "This is a fix PR (\"$title\"), so its body needs a defect record:"
for p in "${problems[@]}"; do echo "  - $p"; done
cat <<'EOF'

Add these four lines to the PR body (edit the description; this check reruns on its own, CI doesn't):

## Defect record
- **Found by:** you | report | bug-check | CI | live check | in passing | review | flake
- **Cause:** #123 (the PR that introduced it), a commit sha, old, or external
- **Shipped in:** v0.71.0 (the first release with the bug), or not released
- **Guard:** the test, lint rule or CI check that stops it coming back (none only with a reason: "none: …")
EOF
exit 1
