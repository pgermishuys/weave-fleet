#!/usr/bin/env bash
# The UI crawl in one command: builds the client in mock mode (no API, no database), serves the build with
# `vite preview`, visits every screen in screens.mjs in every variant, and writes the report.
#
#   scripts/ui-crawl/run.sh [--set=nightly|themes] [--only=<screen id substring>] [--variants=dark-phone,…] [--workers=3]
#   scripts/ui-crawl/run.sh --self-test
#
# --set=themes crawls the other eight themes on the desktop instead of the five nightly variants, against its own
# baseline, axe-baseline-themes.json.
#
# Output in scripts/ui-crawl/out/ (or $UI_CRAWL_OUT): crawl.json, summary.json, summary.md, report.html, shots/.
# --self-test crawls SELF_TEST_SCREENS instead (a mock-only route that throws while it renders) and exits 1 unless the
# gates catch it (self-test.mjs).
# Exits 1 when a gate fails (page errors, a screen showing an error page, console errors, failed requests, sideways
# overflow at 390 px, or an axe rule above its count in the set's baseline). UI_CRAWL_UPDATE_BASELINE=1 writes this run's axe counts as the baseline.
# Environment: UI_CRAWL_PORT (4173), UI_CRAWL_SKIP_BUILD=1 to serve the last build again (UI_CRAWL_DIST=<a crawl's
# out/dist> to serve another crawl's build), UI_CRAWL_PREVIOUS=<an
# earlier summary.json> to compare with. Needs client/node_modules (bun install in client/) and Playwright's
# Chromium (bunx playwright install chromium, from this folder).
set -euo pipefail

here="$(cd "$(dirname "${BASH_SOURCE[0]}")" && pwd)"
client="$(cd "$here/../../client" && pwd)"
out="${UI_CRAWL_OUT:-$here/out}"
port="${UI_CRAWL_PORT:-4173}"
mkdir -p "$out"
out="$(cd "$out" && pwd)"
dist="${UI_CRAWL_DIST:-$out/dist}"
set_flag=--set=nightly
self_test=
for a in "$@"; do case "$a" in --set=*) set_flag="$a" ;; --self-test) self_test=1 ;; esac; done

[ -d "$client/node_modules" ] || { echo "client/node_modules is missing: run bun install in client/ first" >&2; exit 2; }
if [ ! -d "$here/node_modules" ]; then
  (cd "$here" && bun install --frozen-lockfile)
fi

vite=(node "$client/node_modules/vite/bin/vite.js")
if [ -z "${UI_CRAWL_SKIP_BUILD:-}" ]; then
  # Its own outDir, so a crawl never replaces client/dist (which a local Fleet may be serving).
  (cd "$client" && "${vite[@]}" build --mode mock --outDir "$dist" --emptyOutDir --logLevel warn \
    && "${vite[@]}" build -c vite.sw.config.ts --outDir "$dist" --logLevel warn)
fi

if (exec 3<>"/dev/tcp/127.0.0.1/$port") 2>/dev/null; then
  echo "Port $port is already in use: set UI_CRAWL_PORT" >&2
  exit 2
fi
(cd "$client" && exec "${vite[@]}" preview --mode mock --outDir "$dist" --host 127.0.0.1 --port "$port" --strictPort) \
  > "$out/preview.log" 2>&1 &
preview=$!
trap 'kill "$preview" 2>/dev/null || true' EXIT
for _ in $(seq 1 60); do
  curl -fsS -o /dev/null "http://127.0.0.1:$port/healthz" 2>/dev/null && break
  kill -0 "$preview" 2>/dev/null || { cat "$out/preview.log" >&2; exit 2; }
  sleep 0.5
done

rm -rf "$out/shots"
node "$here/crawl.mjs" --base="http://127.0.0.1:$port" --out="$out" "$@"
if [ -n "$self_test" ]; then
  # Not exec: the EXIT trap has to stop the preview server.
  node "$here/self-test.mjs" --out="$out"
  exit 0
fi
node "$here/report.mjs" --out="$out" "$set_flag" ${UI_CRAWL_PREVIOUS:+--previous="$UI_CRAWL_PREVIOUS"} \
  ${UI_CRAWL_PREVIOUS_LABEL:+--previous-label="$UI_CRAWL_PREVIOUS_LABEL"} ${UI_CRAWL_UPDATE_BASELINE:+--update-baseline} --gate
