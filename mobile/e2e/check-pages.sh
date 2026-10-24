#!/usr/bin/env bash
# API-level check of "pages from agents" against the scratch Fleet from start-fleet.sh: the scripted model shows
# pages/paging.html with waitForChoice, the page serves with Fleet's script and CSP, the pick is sent, the agent carries on.
# Needs FLEET_URL, FLEET_TOKEN and SESSION_ID.
set -euo pipefail
: "${FLEET_URL:?}" "${FLEET_TOKEN:?}" "${SESSION_ID:?}"
api() { curl -fsS -H "Authorization: Bearer $FLEET_TOKEN" -H "content-type: application/json" "$@"; }
py() { python3 -c "$@"; }
S=$FLEET_URL/api/sessions/$SESSION_ID

api -X POST "$S/prompt" -d "{\"text\":\"show-page please\",\"agent\":null,\"model\":null,\"attachments\":null,\"userMessageId\":null,\"effort\":null,\"correlationId\":\"check-$(date +%s)\"}" > /dev/null

WAITING=
for _ in $(seq 90); do
  WAITING=$(api "$S/canvases" | py '
import json,sys
for c in json.load(sys.stdin):
    st = c.get("state") or {}
    ch = st.get("choice") or {}
    if ch.get("status") == "waiting":
        print(json.dumps({"pageId": st.get("pageId"), "entry": st.get("entry"), "callId": ch.get("callId")})); break
' || true)
  [ -n "$WAITING" ] && break; sleep 1
done
[ -n "$WAITING" ] || { echo "FAIL: no page canvas waiting for a pick"; api "$S/canvases" | head -c 2000; exit 1; }
echo "waiting page: $WAITING"
field() { echo "$WAITING" | py "import json,sys;print(json.load(sys.stdin)['$1'])"; }
PAGE_ID=$(field pageId); ENTRY=$(field entry); CALL_ID=$(field callId)

# The page is served without a sign-in (the phone's WebView carries none).
HEADERS=$(mktemp); BODY=$(mktemp)
CODE=$(curl -sS -D "$HEADERS" -o "$BODY" -w '%{http_code}' "$FLEET_URL/pages/$PAGE_ID/$ENTRY")
[ "$CODE" = 200 ] || { echo "FAIL: page returned $CODE"; cat "$HEADERS"; exit 1; }
grep -q "fleet-page" "$BODY" || { echo "FAIL: page has no injected fleet-page script"; exit 1; }
grep -qi "^content-security-policy:" "$HEADERS" || { echo "FAIL: page has no CSP header"; cat "$HEADERS"; exit 1; }
grep -q "data-fleet-choice" "$BODY" || { echo "FAIL: page lost its options"; exit 1; }
echo "page served: 200, script injected, CSP set"
rm -f "$HEADERS" "$BODY"

api -X POST "$S/page-choices/$CALL_ID/send" -d '{"value":"paged","summary":"Paged table"}' > /dev/null
echo "pick sent"

for _ in $(seq 90); do
  STATUS=$(api "$S" | py "import json,sys;print(json.load(sys.stdin).get('activityStatus'))")
  if [ "$STATUS" = idle ]; then
    LAST=$(api "$S/messages" | py '
import json,sys
d = json.load(sys.stdin)
ms = d if isinstance(d, list) else d.get("messages", [])
t = ""
for m in ms:
    info = m.get("info", m)
    if info.get("role") != "assistant": continue
    x = " ".join(p.get("text", "") for p in m.get("parts", []) if p.get("type") == "text")
    if x: t = x
print(t)')
    if echo "$LAST" | grep -q "Going with"; then echo "PASS: $LAST"; exit 0; fi
  fi
  sleep 1
done
echo "FAIL: no \"Going with\" reply (status $STATUS)"; exit 1
