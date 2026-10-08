#!/usr/bin/env bash
# Starts a throwaway Fleet for the native-app walk-through: a scripted model (fakellm.py), OpenCode 1, Fleet on
# 127.0.0.1:5431 asking for a key, one session with a reply, and "Ask before changes" on so commands wait for you.
# For CI runners and scratch machines only: it uses $HOME's OpenCode config. Never run it with your real HOME.
# Writes FLEET_URL, PAIR_CODE and SESSION_TITLE to $GITHUB_ENV when set, else prints them.
set -euo pipefail
ROOT=$(cd "$(dirname "$0")/../.." && pwd)
WORK=${FLEET_E2E_DIR:-$(mktemp -d)}
PORT=5431
LLM_PORT=5031
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
  Fleet__AnalyticsEnabled=false Fleet__Update__CheckOnStartup=false ASPNETCORE_ENVIRONMENT=Development \
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
{ echo "FLEET_URL=$BASE"; echo "PAIR_CODE=$CODE"; echo "SESSION_ID=$SESSION"; echo "FLEET_TOKEN=$TOKEN"; echo "FLEET_E2E_DIR=$WORK"; } >> "$out"
echo "Fleet ready: session $SESSION ($status), code $CODE"
