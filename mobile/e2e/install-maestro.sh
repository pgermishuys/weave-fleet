#!/usr/bin/env bash
# Installs the pinned Maestro CLI and fails loudly if it isn't usable. get.maestro.mobile.dev's installer swallows a
# failed download (a 502 from GitHub left no maestro and a green step, then exit 127 in the walk), so fetch the zip
# ourselves, retry, and check `maestro --version` at the end.
set -euo pipefail

MAESTRO_VERSION="${MAESTRO_VERSION:-2.12.1}"
url="https://github.com/mobile-dev-inc/maestro/releases/download/cli-$MAESTRO_VERSION/maestro.zip"
dir="$(mktemp -d)"

for attempt in 1 2 3 4; do
  if curl -fsSL "$url" -o "$dir/maestro.zip" && unzip -tq "$dir/maestro.zip" > /dev/null; then break; fi
  if [ "$attempt" -eq 4 ]; then echo "Maestro $MAESTRO_VERSION download failed"; exit 1; fi
  sleep $((attempt * 10))
done

rm -rf "$HOME/.maestro"
mkdir -p "$HOME/.maestro"
unzip -q "$dir/maestro.zip" -d "$dir/x"
cp -R "$dir"/x/maestro/. "$HOME/.maestro/"
"$HOME/.maestro/bin/maestro" --version
if [ -n "${GITHUB_PATH:-}" ]; then echo "$HOME/.maestro/bin" >> "$GITHUB_PATH"; fi
