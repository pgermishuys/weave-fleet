#!/usr/bin/env bash
# Runs scripts/add-unversioned-installers.sh against dummy files named like v0.47.1's release assets: checks it adds
# the five unversioned installers with matching .sha256 files, leaves every original file untouched, and adds
# nothing when an installer is missing.
set -euo pipefail
export LC_ALL=C

SCRIPT_DIR="$(cd "$(dirname "$0")" && pwd)"
WORK="$(mktemp -d)"
trap 'rm -rf "$WORK"' EXIT

fail() { echo "FAIL: $*" >&2; exit 1; }

make_assets() {
  local dir="$1"
  mkdir -p "$dir"
  for name in \
    Fleet-0.47.1-mac-arm64.dmg Fleet-0.47.1-mac-arm64.zip \
    Fleet-0.47.1-win-x64-setup.exe Fleet-0.47.1-win-arm64-setup.exe \
    Fleet-0.47.1-linux-x86_64.AppImage Fleet-0.47.1-linux-amd64.deb \
    fleet-v0.47.1-linux-x64.tar.gz fleet-v0.47.1-osx-arm64.tar.gz \
    fleet-v0.47.1-win-x64.zip fleet-v0.47.1-win-arm64.zip; do
    echo "contents of $name" > "$dir/$name"
    (cd "$dir" && sha256sum "$name" > "$name.sha256")
  done
  for name in Fleet-0.47.1-mac-arm64.zip.blockmap Fleet-0.47.1-win-x64-setup.exe.blockmap; do
    echo "blockmap" > "$dir/$name"
  done
  printf 'version: 0.47.1\npath: Fleet-0.47.1-win-x64-setup.exe\n' > "$dir/latest.yml"
  printf 'version: 0.47.1\npath: Fleet-0.47.1-mac-arm64.zip\n' > "$dir/latest-mac.yml"
  printf 'version: 0.47.1\npath: Fleet-0.47.1-linux-x86_64.AppImage\n' > "$dir/latest-linux.yml"
  echo "checksums" > "$dir/checksums.txt"
  echo "#!/bin/sh" > "$dir/install.sh"
  echo "# ps1" > "$dir/install.ps1"
}

snapshot() { (cd "$1" && find . -type f | sort | xargs sha256sum); }

# Every installer present: five copies with checksums, originals untouched.
make_assets "$WORK/full"
before="$(snapshot "$WORK/full")"
"$SCRIPT_DIR/add-unversioned-installers.sh" v0.47.1 "$WORK/full" > /dev/null

for name in Fleet-mac-arm64.dmg Fleet-win-x64-setup.exe Fleet-win-arm64-setup.exe \
  Fleet-linux-x86_64.AppImage Fleet-linux-amd64.deb; do
  versioned="Fleet-0.47.1-${name#Fleet-}"
  cmp -s "$WORK/full/$name" "$WORK/full/$versioned" || fail "$name is not a copy of $versioned"
  (cd "$WORK/full" && sha256sum -c --quiet "$name.sha256") || fail "$name.sha256 doesn't match $name"
  grep -q "  $name\$" "$WORK/full/$name.sha256" || fail "$name.sha256 names the wrong file"
done

after="$(snapshot "$WORK/full")"
unchanged="$(comm -23 <(echo "$before" | sort) <(echo "$after" | sort))"
[ -z "$unchanged" ] || fail "original files changed: $unchanged"
added="$(comm -13 <(echo "$before" | awk '{print $2}' | sort) <(echo "$after" | awk '{print $2}' | sort) | tr '\n' ' ')"
expected="./Fleet-linux-amd64.deb ./Fleet-linux-amd64.deb.sha256 ./Fleet-linux-x86_64.AppImage ./Fleet-linux-x86_64.AppImage.sha256 ./Fleet-mac-arm64.dmg ./Fleet-mac-arm64.dmg.sha256 ./Fleet-win-arm64-setup.exe ./Fleet-win-arm64-setup.exe.sha256 ./Fleet-win-x64-setup.exe ./Fleet-win-x64-setup.exe.sha256 "
[ "$added" = "$expected" ] || fail "added files were: $added"

# Running it twice (a re-run of the mirror job) gives the same result.
"$SCRIPT_DIR/add-unversioned-installers.sh" v0.47.1 "$WORK/full" > /dev/null
[ "$(snapshot "$WORK/full")" = "$after" ] || fail "a second run changed the files"

# An installer missing: the script fails and adds nothing.
make_assets "$WORK/missing"
rm "$WORK/missing/Fleet-0.47.1-win-arm64-setup.exe" "$WORK/missing/Fleet-0.47.1-win-arm64-setup.exe.sha256"
before="$(snapshot "$WORK/missing")"
if "$SCRIPT_DIR/add-unversioned-installers.sh" v0.47.1 "$WORK/missing" 2> "$WORK/err"; then
  fail "succeeded with an installer missing"
fi
grep -q "Fleet-0.47.1-win-arm64-setup.exe is not among the release assets" "$WORK/err" || fail "unclear error: $(cat "$WORK/err")"
[ "$(snapshot "$WORK/missing")" = "$before" ] || fail "added files although an installer was missing"

# The wrong tag: nothing matches, nothing added.
make_assets "$WORK/wrongtag"
before="$(snapshot "$WORK/wrongtag")"
if "$SCRIPT_DIR/add-unversioned-installers.sh" v0.47.2 "$WORK/wrongtag" 2> /dev/null; then
  fail "succeeded for a tag whose installers aren't there"
fi
[ "$(snapshot "$WORK/wrongtag")" = "$before" ] || fail "added files for the wrong tag"

echo "add-unversioned-installers.sh: all checks passed"
