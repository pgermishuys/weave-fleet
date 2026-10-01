"""Tiny V2 driver: create session, prompt, wait idle, dump messages. Usage: v2.py <port> <dir> '<prompt>' [sessionID]"""
import json, sys, time, base64, urllib.request, urllib.parse
port, directory, text = sys.argv[1], sys.argv[2], sys.argv[3]
sid = sys.argv[4] if len(sys.argv) > 4 else None
AUTH = "Basic " + base64.b64encode(b"opencode:spike").decode()
def call(method, path, body=None):
    req = urllib.request.Request(f"http://127.0.0.1:{port}{path}", method=method,
        data=None if body is None else json.dumps(body).encode(), headers={"content-type": "application/json", "authorization": AUTH})
    try:
        with urllib.request.urlopen(req, timeout=120) as r:
            t = r.read().decode(); return r.status, (json.loads(t) if t else None)
    except urllib.error.HTTPError as e:
        return e.code, e.read().decode()
if not sid:
    import os
    perms = [] if os.environ.get("NOPERM") else [{"action": "*", "resource": "*", "effect": "allow"}]
    s, b = call("POST", "/api/session", {"location": {"directory": directory}, "permissions": perms})
    sid = b["data"]["id"]; print("session", sid)
s, b = call("POST", f"/api/session/{sid}/prompt", {"text": text}); print("prompt", s, str(b)[:200])
time.sleep(1.5)
for i in range(60):
    s, b = call("GET", f"/api/session/{sid}")
    st = (b or {}).get("data", {}).get("status") if isinstance(b, dict) else None
    s2, m = call("GET", f"/api/session/{sid}/message")
    if isinstance(m, dict):
        msgs = m.get("data", [])
        last = msgs[-1] if msgs else {}
        if last.get("role") == "assistant" and last.get("time", {}).get("completed") or (last.get("type") == "assistant" and last.get("time",{}).get("completed")):
            pass
    time.sleep(1)
    if i > 3 and st in (None, "idle"): break
print(json.dumps({"status": st}, indent=0))
open(f"evidence/session-{sid}.json", "w").write(json.dumps(call("GET", f"/api/session/{sid}/message")[1], indent=1))
print("saved evidence/session-%s.json" % sid)
