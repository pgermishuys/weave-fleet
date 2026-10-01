"""Live check: Fleet as OpenCode 2's browser attachment (fleet.sh start first; site on 5453)."""
import json, sys, time, urllib.request
sys.path.insert(0, ".")
import live
live.BASE = "http://127.0.0.1:5451"
live.RUN = RUN = "/home/pgermishuys/source/weave-fleet/.claude/worktrees/spike-oc2-browser/.poc-runtime/run"
ALPHA = f"{RUN}/work/alpha"
call = live.call
def raw(method, path):
    req = urllib.request.Request(live.BASE + path, method=method)
    with urllib.request.urlopen(req, timeout=60) as r: return r.status, r.read().decode()

def turn(sid, text, timeout=120):
    before = len(live.llm_requests())
    s, b = call("POST", f"/api/sessions/{sid}/prompt", {"text": text}); assert s in (200, 202, 204), (s, b)
    deadline = time.time() + timeout
    while time.time() < deadline:
        reqs = [r for r in live.llm_requests()[before:] if live.is_agent(r)]
        # a turn with a tool call ends on the request after the tool result; a plain turn on its first request
        if reqs and (reqs[-1]["messages"][-1]["role"] != "user" or "call tool" not in text and "run code" not in text):
            time.sleep(2); return reqs
        time.sleep(0.5)
    raise SystemExit(f"timed out: {text}")

sid = live.session(ALPHA, "opencode2", "Browser spike")
r = turn(sid, "hello")
first = r[0]
json.dump({"tools": [t["function"]["name"] for t in first["tools"]], "fleet_tools": {t["function"]["name"]: t["function"]["description"] for t in first["tools"] if t["function"]["name"].startswith("fleet_")}}, open("evidence/q4-fleet-session-tools.json", "w"), indent=1)
print("tools offered:", [t["function"]["name"] for t in first["tools"]])
sysmsg = "\n".join(live.text_of(s) for s in first["system"])
print("browser catalog in system prompt:", "browser (" in sysmsg)
open("evidence/q4-fleet-session-system.txt", "w").write(sysmsg)

r = turn(sid, 'call tool fleet_browser_open {"url": "http://127.0.0.1:5453/", "title": "Signup"}')
print("fleet_browser_open ->", json.dumps(r[-1]["messages"][-1].get("content"))[:300])
s, canvases = call("GET", f"/api/sessions/{sid}/canvases")
print("canvases:", json.dumps(canvases)[:400])

s, text = raw("POST", f"/api/spike/sessions/{sid}/browser-attach")
print("attach:", s, text)
r = turn(sid, "run code drive")
msgs = r[-1]["messages"]
open("evidence/q2-fleet-roundtrip-model-sees.json", "w").write(json.dumps(msgs[-3:], indent=1)[:200000])
for m in msgs[-2:]:
    c = m.get("content")
    print(m["role"], (str(c)[:1500] if not isinstance(c, list) else [(p.get("type"), str(p)[:80]) for p in c]))
s, log = raw("GET", f"/api/spike/sessions/{sid}/browser-log")
open("evidence/q2-fleet-attachment.log", "w").write(log if isinstance(log, str) else json.dumps(log))
print(log if isinstance(log, str) else log)
print("SID", sid)
