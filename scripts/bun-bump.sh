#!/usr/bin/env bash
# Moves Fleet's Bun pin to a new release of oven-sh/bun, once its checksums are verified against Bun's signing key.
#
# Usage: scripts/bun-bump.sh [version]      (default: oven-sh/bun's latest non-prerelease release)
#
# What it does:
#   - downloads that release's SHASUMS256.txt.asc and verifies it against ONLY bun/bun-release-signing-key.asc
#     (a temporary GNUPGHOME, and the signer's fingerprint is checked), then takes the six sha256 values from the verified text;
#   - rewrites bun/bun.json (version and assets; oldestSafe is kept and note is cleared: a person decides those);
#   - replaces the SHASUMS256 fixture, rewrites .bun-version, moves @types/bun (only if that version exists on npm)
#     and refreshes mods/host/bun.lock.
# It never marks a version unsafe. With GITHUB_OUTPUT set it writes bumped=true|false (and version, previous).
#
# Needs: curl, jq, gpg; bun for the lockfile (skipped, with a warning, when it isn't installed).
#
# The signing key, bun/bun-release-signing-key.asc: "Robobun <robobun@oven.sh>", ed25519,
# fingerprint F3DCC08A8572C0749B3E18888EAB4D40A7B22B59. Established from three places that agree:
#   - oven-sh/bun packages/bun-release/scripts/upload-assets.ts (verifiedKeys, "robobun@oven.sh");
#   - oven-sh/bun dockerhub/alpine/Dockerfile (inlines this key and runs gpg --assert-signer on this fingerprint);
#   - keys.openpgp.org, which serves the same key by that fingerprint.
# It verifies the real SHASUMS256.txt.asc of bun-v1.4.2 and bun-v1.4.3.
set -euo pipefail

SIGNING_FPR="F3DCC08A8572C0749B3E18888EAB4D40A7B22B59"
BUN_REPO="oven-sh/bun"
ROOT="$(cd "$(dirname "${BASH_SOURCE[0]}")/.." && pwd)"
MANIFEST="$ROOT/bun/bun.json"
KEY="$ROOT/bun/bun-release-signing-key.asc"
FIXTURES="$ROOT/tests/WeaveFleet.Infrastructure.Tests/Fixtures/Bun"
HOST="$ROOT/mods/host"

# The assets Fleet downloads, per runtime id. Keep in step with BunManifest.AssetNames
# (src/WeaveFleet.Application/Runtimes/BunManifest.cs): a manifest must name exactly these.
RIDS=(linux-x64 linux-arm64 osx-x64 osx-arm64 win-x64 win-arm64)
declare -A ASSET_NAMES=(
  [linux-x64]="bun-linux-x64-baseline.zip"
  [linux-arm64]="bun-linux-aarch64.zip"
  [osx-x64]="bun-darwin-x64-baseline.zip"
  [osx-arm64]="bun-darwin-aarch64.zip"
  [win-x64]="bun-windows-x64-baseline.zip"
  [win-arm64]="bun-windows-aarch64.zip"
)

output() { if [ -n "${GITHUB_OUTPUT:-}" ]; then echo "$1" >> "$GITHUB_OUTPUT"; fi; }
fail() { echo "bun-bump: $*" >&2; exit 1; }

for tool in curl jq gpg; do command -v "$tool" > /dev/null || fail "$tool is required"; done

previous="$(jq -r .version "$MANIFEST")"

target="${1:-}"
if [ -z "$target" ]; then
  if [ -n "${GH_TOKEN:-}" ]; then
    tag="$(gh api "repos/$BUN_REPO/releases/latest" --jq .tag_name)"
  else
    tag="$(curl -fsSL "https://api.github.com/repos/$BUN_REPO/releases/latest" | jq -r .tag_name)"
  fi
  [[ "$tag" == bun-v* ]] || fail "unexpected latest release tag '$tag'"
  target="${tag#bun-v}"
fi
[[ "$target" =~ ^[0-9]+\.[0-9]+\.[0-9]+$ ]] || fail "'$target' is not a plain X.Y.Z version"

newest="$(printf '%s\n%s\n' "$previous" "$target" | sort -V | tail -n 1)"
if [ "$target" = "$previous" ] || [ "$newest" != "$target" ]; then
  echo "Bun $target is not newer than bun/bun.json's $previous; nothing to do."
  output "bumped=false"
  exit 0
fi

work="$(mktemp -d)"
trap 'rm -rf "$work"' EXIT

# Verify against only the pinned key.
export GNUPGHOME="$work/gnupg"
mkdir -m 700 "$GNUPGHOME"
gpg --batch --quiet --import "$KEY" 2> /dev/null
fprs="$(gpg --batch --with-colons --list-keys | awk -F: '$1=="fpr"{print $10}' | sort -u)"
printf '%s\n' "$fprs" | grep -qx "$SIGNING_FPR" || fail "$KEY does not hold $SIGNING_FPR"

base="https://github.com/$BUN_REPO/releases/download/bun-v$target"
curl -fsSL -o "$work/SHASUMS256.txt.asc" "$base/SHASUMS256.txt.asc" || fail "could not download $base/SHASUMS256.txt.asc"
status="$(gpg --batch --status-fd 1 --assert-signer "$SIGNING_FPR" --output "$work/verified.txt" \
  --verify "$work/SHASUMS256.txt.asc" 2> /dev/null)" || fail "SHASUMS256.txt.asc for bun-v$target does not verify against $SIGNING_FPR"
echo "$status" | grep -q "^\[GNUPG:\] VALIDSIG $SIGNING_FPR " || fail "bun-v$target was not signed by $SIGNING_FPR"
[ -s "$work/verified.txt" ] || fail "verification produced no text"
echo "Verified SHASUMS256.txt.asc of bun-v$target against $SIGNING_FPR."

# Sizes (bytes) come from the release API and are NOT security-relevant: the sha256 values below are the
# only integrity check, and they come solely from the signature-verified text.
if [ -n "${GH_TOKEN:-}" ]; then
  release_json="$(gh api "repos/$BUN_REPO/releases/tags/bun-v$target")" || fail "could not read the bun-v$target release"
else
  release_json="$(curl -fsSL "https://api.github.com/repos/$BUN_REPO/releases/tags/bun-v$target")" || fail "could not read the bun-v$target release"
fi

declare -A SHAS SIZES
for rid in "${RIDS[@]}"; do
  name="${ASSET_NAMES[$rid]}"
  sha="$(awk -v n="$name" '$2==n || $2=="*"n {print $1}' "$work/verified.txt")"
  [[ "$sha" =~ ^[0-9a-f]{64}$ ]] || fail "no valid sha256 for $name ($rid) in the verified SHASUMS256.txt"
  SHAS[$rid]="$sha"
  size="$(jq -r --arg n "$name" '[.assets[] | select(.name == $n) | .size] | first // empty' <<< "$release_json")"
  [[ "$size" =~ ^[1-9][0-9]*$ ]] || fail "no positive size for $name ($rid) in the bun-v$target release"
  SIZES[$rid]="$size"
done

oldest_safe="$(jq -r .oldestSafe "$MANIFEST")"
{
  echo '{'
  echo '  "schema": 1,'
  echo "  \"version\": \"$target\","
  echo "  \"oldestSafe\": \"$oldest_safe\","
  echo '  "note": null,'
  echo '  "assets": {'
  last="${RIDS[${#RIDS[@]}-1]}"
  for rid in "${RIDS[@]}"; do
    comma=","; [ "$rid" = "$last" ] && comma=""
    printf '    "%s": { "name": "%s", "sha256": "%s", "size": %s }%s\n' "$rid" "${ASSET_NAMES[$rid]}" "${SHAS[$rid]}" "${SIZES[$rid]}" "$comma"
  done
  echo '  }'
  echo '}'
} > "$work/bun.json"
jq -e . "$work/bun.json" > /dev/null || fail "generated manifest is not valid JSON"
cp "$work/bun.json" "$MANIFEST"

# Fixture: the verified text, named for the new version.
find "$FIXTURES" -maxdepth 1 -name 'bun-v*-SHASUMS256.txt' -delete
cp "$work/verified.txt" "$FIXTURES/bun-v$target-SHASUMS256.txt"

printf '%s\n' "$target" > "$ROOT/.bun-version"

# @types/bun, only when that version is on npm.
types_note="@types/bun left as is"
if curl -fsS -o /dev/null "https://registry.npmjs.org/@types/bun/$target" 2> /dev/null; then
  sed -i -E "s|(\"@types/bun\": \")[^\"]+(\")|\1$target\2|" "$HOST/package.json"
  types_note="@types/bun -> $target"
else
  echo "warning: @types/bun $target is not on npm yet; leaving mods/host/package.json as is." >&2
fi

if command -v bun > /dev/null; then
  (cd "$HOST" && bun install)
else
  echo "warning: bun is not installed; mods/host/bun.lock was not refreshed." >&2
  types_note="$types_note, bun.lock NOT refreshed"
fi

echo
echo "Bun $previous -> $target"
echo "  bun/bun.json: version and assets (sha256, size) updated; oldestSafe kept at $oldest_safe; note cleared"
for rid in "${RIDS[@]}"; do echo "    $rid  ${SHAS[$rid]}  ${SIZES[$rid]} bytes"; done
echo "  fixture: bun-v$target-SHASUMS256.txt; .bun-version: $target; $types_note"
echo "  A person decides oldestSafe and note."

output "bumped=true"
output "version=$target"
output "previous=$previous"
