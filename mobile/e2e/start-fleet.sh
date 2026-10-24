#!/usr/bin/env bash
# Starts a throwaway Fleet for the native-app walk-through: a scripted model (fakellm.py), OpenCode 1, Fleet on
# 127.0.0.1:5431 asking for a key, one session with a reply, and "Ask before changes" on so commands wait for you.
# OpenCode is the version ci.yml pins (installed by the workflow). Everything Fleet and OpenCode write goes under a
# fresh scratch HOME made here, never the caller's: Fleet deletes and rewrites ~/.weave/fleet.db.
# Writes FLEET_URL, PAIR_CODE and SCRATCH_HOME, SESSION_ID and FLEET_E2E_DIR to $GITHUB_ENV when set, else prints them.
set -euo pipefail
ROOT=$(cd "$(dirname "$0")/../.." && pwd)
WORK=${FLEET_E2E_DIR:-$(mktemp -d)}
PORT=5431
LLM_PORT=5031
command -v opencode > /dev/null || { echo "opencode isn't on PATH"; exit 1; }
[ -z "${OPENCODE_VERSION:-}" ] || opencode --version | grep -qF "$OPENCODE_VERSION" || { echo "opencode isn't $OPENCODE_VERSION"; exit 1; }
# opencode may live under the real HOME; keep its folder on PATH, then move to the scratch one.
OC_DIR=$(dirname "$(command -v opencode)")
REAL_HOME=$HOME
export HOME=$(mktemp -d "${RUNNER_TEMP:-${TMPDIR:-/tmp}}/fleet-e2e-home.XXXXXX")
[ "$HOME" != "$REAL_HOME" ] || { echo "scratch HOME equals the real one"; exit 1; }
export PATH="$OC_DIR:$PATH"
# GitHub's runners point XDG_CONFIG_HOME (and friends) at the real home; OpenCode would read its config from there and
# run a real model instead of the scripted one.
export XDG_CONFIG_HOME="$HOME/.config" XDG_DATA_HOME="$HOME/.local/share" XDG_CACHE_HOME="$HOME/.cache" XDG_STATE_HOME="$HOME/.local/state"
unset OPENCODE_CONFIG OPENCODE_CONFIG_DIR OPENCODE_CONFIG_CONTENT
# dotnet keeps its first-run files and NuGet cache under HOME; the built Fleet doesn't need either.
export DOTNET_CLI_HOME="$HOME" DOTNET_NOLOGO=1 DOTNET_SKIP_FIRST_TIME_EXPERIENCE=1
mkdir -p "$WORK/fleet" "$HOME/.config/opencode" "$HOME/src/demo-app"

cat > "$HOME/.config/opencode/opencode.json" <<JSON
{
  "\$schema": "https://opencode.ai/config.json",
  "model": "fakellm/fake-model",
  "provider": { "fakellm": { "npm": "@ai-sdk/openai-compatible", "options": { "baseURL": "http://127.0.0.1:$LLM_PORT/v1", "apiKey": "x" }, "models": { "fake-model": { "name": "Fake" } } } }
}
JSON

python3 "$ROOT/mobile/e2e/fakellm.py" $LLM_PORT "$WORK/fakellm.log" > /dev/null 2>&1 &

( cd "$HOME/src/demo-app" && git init -q && echo "# demo" > README.md && git add . && git -c user.email=e2e@example.com -c user.name=e2e commit -qm init )

# Fleet's build output sits under a runtime folder (bin/Debug/net10.0/<rid>/) on some platforms.
DLL=$(find "$ROOT/src/WeaveFleet.Api/bin/Debug" -name WeaveFleet.Api.dll -not -path "*/ref/*" | head -1)
Fleet__Host=127.0.0.1 Fleet__Port=$PORT Fleet__Auth__RequireToken=true Fleet__DatabasePath="$WORK/fleet/fleet.db" \
  Fleet__AnalyticsEnabled=false Fleet__Update__CheckOnStartup=false ASPNETCORE_ENVIRONMENT=Production \
  dotnet "$DLL" > "$WORK/fleet.log" 2>&1 &
for _ in $(seq 120); do grep -q "Now listening" "$WORK/fleet.log" 2>/dev/null && break; sleep 1; done
grep -q "Now listening" "$WORK/fleet.log" || { tail -50 "$WORK/fleet.log"; exit 1; }

TOKEN=$(python3 -c "import json,sys;print(json.load(open(sys.argv[1]))['accessToken'])" "$WORK/fleet/fleet.machine.json")
api() { curl -fsS -H "Authorization: Bearer $TOKEN" -H "content-type: application/json" "$@"; }
BASE=http://127.0.0.1:$PORT

api -X POST $BASE/api/workspace-roots -d "{\"path\":\"$HOME/src\"}" > /dev/null
SESSION=$(api -X POST $BASE/api/sessions -d "{\"directory\":\"$HOME/src/demo-app\",\"title\":\"Fix pagination off-by-one\",\"isolationStrategy\":\"existing\",\"harnessType\":\"opencode\",\"initialPrompt\":\"The pagination test fails on the last page. Can you look?\"}" \
  | python3 -c "import json,sys;print(json.load(sys.stdin)['session']['id'])")
for _ in $(seq 90); do
  status=$(api $BASE/api/sessions/$SESSION | python3 -c "import json,sys;print(json.load(sys.stdin).get('activityStatus'))")
  [ "$status" = "idle" ] && break; sleep 1
done
api -X PUT $BASE/api/preferences/PermissionLevel -d '{"value":"ask"}' > /dev/null
CODE=$(api -X POST $BASE/api/machine/pairing -d "{\"baseUrl\":\"$BASE\"}" | python3 -c "import json,sys;print(json.load(sys.stdin)['manualCode'])")

out=${GITHUB_ENV:-/dev/stdout}
{ echo "FLEET_URL=$BASE"; echo "PAIR_CODE=$CODE"; echo "SCRATCH_HOME=$HOME"; echo "SESSION_ID=$SESSION"; echo "FLEET_TOKEN=$TOKEN"; echo "FLEET_E2E_DIR=$WORK"; } >> "$out"
echo "Fleet ready: session $SESSION ($status), code $CODE"
