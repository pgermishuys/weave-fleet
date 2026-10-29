#!/usr/bin/env bash
# Makes the walk's session on the scratch Fleet ("Fix pagination off-by-one" in demo-app, with its first reply) and
# prints its id once that reply is in. Needs FLEET_URL, FLEET_TOKEN and SCRATCH_HOME (start-fleet.sh writes them).
set -euo pipefail
api() { curl -fsS -H "Authorization: Bearer $FLEET_TOKEN" -H "content-type: application/json" "$@"; }
SESSION=$(api -X POST "$FLEET_URL/api/sessions" -d "{\"directory\":\"$SCRATCH_HOME/src/demo-app\",\"title\":\"Fix pagination off-by-one\",\"isolationStrategy\":\"existing\",\"harnessType\":\"opencode\",\"initialPrompt\":\"The pagination test fails on the last page. Can you look?\"}" \
  | python3 -c "import json,sys;print(json.load(sys.stdin)['session']['id'])")
for _ in $(seq 90); do
  status=$(api "$FLEET_URL/api/sessions/$SESSION" | python3 -c "import json,sys;print(json.load(sys.stdin).get('activityStatus'))")
  [ "$status" = "idle" ] && break; sleep 1
done
echo "$SESSION"
