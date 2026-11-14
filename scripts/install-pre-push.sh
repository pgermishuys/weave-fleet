#!/usr/bin/env bash
# Installs scripts/pre-push-check.sh as a pre-push check for this clone and every worktree of it. Run it from any
# checkout of the repository:
#
#   bash scripts/install-pre-push.sh
#
# It appends one line to the clone's pre-push hook (the hooks folder git uses, so core.hooksPath is respected),
# keeps everything already in the hook, and creates the hook if there's none. Running it again changes nothing.
# It doesn't touch git config. The line runs the check from the checkout being pushed, so a worktree whose branch
# predates the check just skips it. To remove it, delete the line that ends in "# weave-fleet pre-push check".
set -euo pipefail

marker="# weave-fleet pre-push check"
line='f="$(git rev-parse --show-toplevel)/scripts/pre-push-check.sh"; [ ! -f "$f" ] || bash "$f" "$@" || exit $? '"$marker"

hooks="$(git rev-parse --path-format=absolute --git-path hooks)"
hook="$hooks/pre-push"
mkdir -p "$hooks"

if [ -f "$hook" ] && grep -qF "$marker" "$hook"; then
  echo "The pre-push check is already in $hook"
  exit 0
fi

if [ -f "$hook" ]; then
  # A hook that ends by exec-ing a command never reaches a line after it.
  if grep -qE '^[[:space:]]*exec[[:space:]]+[^<>[:space:]]' "$hook"; then
    echo "$hook ends in exec, so a line added after it would never run. Add this line before the exec instead:" >&2
    echo "  $line" >&2
    exit 1
  fi
  # Keep the last line whole if the file doesn't end in a newline.
  [ -z "$(tail -c 1 "$hook")" ] || echo >>"$hook"
else
  printf '#!/bin/sh\n' >"$hook"
fi
printf '%s\n' "$line" >>"$hook"
chmod +x "$hook"
echo "Added the pre-push check to $hook"
