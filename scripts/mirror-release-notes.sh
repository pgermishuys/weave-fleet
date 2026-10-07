#!/usr/bin/env bash
# Writes the notes for a fleet-releases mirror release: weave-fleet's release notes for the tag, then a link back to it.
# The app's and the CLI's "What's new" links open the mirror, so the mirror has to say what's new.
#
# Usage: scripts/mirror-release-notes.sh <tag> <output-file>
set -euo pipefail

TAG="$1"
OUT="$2"
SOURCE_REPO="${SOURCE_REPO:-pgermishuys/weave-fleet}"

body="$(gh release view "$TAG" --repo "$SOURCE_REPO" --json body --jq .body)"
if [ -z "$body" ]; then
  body="Release $TAG"
fi

printf '%s\n\n---\nRelease notes from https://github.com/%s/releases/tag/%s\n' "$body" "$SOURCE_REPO" "$TAG" > "$OUT"
