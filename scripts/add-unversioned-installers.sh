#!/usr/bin/env bash
# Adds copies of the desktop installers without the version in their names (Fleet-mac-arm64.dmg, ...), each with a
# .sha256, to a folder of release assets. The fleet-releases mirror uploads them so a link like
# https://github.com/pgermishuys/fleet-releases/releases/latest/download/Fleet-mac-arm64.dmg always gets the newest
# installer. The versioned files and the update feeds (latest*.yml) stay as they are: electron-updater only downloads
# what the feeds name.
#
# Fails, adding nothing, if any installer is missing, so a release never goes out with a link that 404s.
#
# Usage: scripts/add-unversioned-installers.sh <tag> <assets-dir>
set -euo pipefail

TAG="$1"
DIR="$2"
VERSION="${TAG#v}"

NAMES=(
  Fleet-mac-arm64.dmg
  Fleet-win-x64-setup.exe
  Fleet-win-arm64-setup.exe
  Fleet-linux-x86_64.AppImage
  Fleet-linux-amd64.deb
)

missing=0
for name in "${NAMES[@]}"; do
  versioned="Fleet-${VERSION}-${name#Fleet-}"
  if [ ! -f "$DIR/$versioned" ]; then
    echo "::error::$versioned is not among the release assets." >&2
    missing=1
  fi
done
[ "$missing" -eq 0 ] || exit 1

for name in "${NAMES[@]}"; do
  versioned="Fleet-${VERSION}-${name#Fleet-}"
  cp "$DIR/$versioned" "$DIR/$name"
  (cd "$DIR" && sha256sum "$name" > "$name.sha256")
  echo "$versioned -> $name"
done
