"""Drive the standalone V2: python3 drive.py new [allowall] | prompt <sid> <text> | msgs <sid> | perms <sid> | reply <sid> <rid> <once|reject>"""
import base64, json, sys, time, urllib.request
BASE = "http://127.0.0.1:5469/"
PROJ = "/home/pgermishuys/.cache/fleet-oc2-references/run/work/proj"
AUTH = "Basic " + base64.b64encode(b"opencode:pw").decode()
def call(method, path, body=None):
    req = urllib.request.Request(BASE + path, data=None if body is None else json.dumps(body).encode(), method=method,
                                 headers={"content-type": "application/json", "authorization": AUTH})
    try:
        with urllib.request.urlopen(req, timeout=120) as r:
            t = r.read().decode(); return r.status, (json.loads(t) if t else None)
    except urllib.error.HTTPError as e:
        return e.code, e.read().decode()
cmd = sys.argv[1]
if cmd == "new":
    body = {"location": {"directory": PROJ}}
    if len(sys.argv) > 2 and sys.argv[2] == "allowall":
        body["permissions"] = [{"action": "*", "resource": "*", "effect": "allow"}]
    s, b = call("POST", "api/session", body); print(b["data"]["id"] if s < 300 else (s, b))
elif cmd == "prompt":
    print(call("POST", f"api/session/{sys.argv[2]}/prompt", {"text": sys.argv[3]}))
elif cmd == "msgs":
    s, b = call("GET", f"api/session/{sys.argv[2]}/message?limit=50"); print(json.dumps(b, indent=1)[:int(sys.argv[3]) if len(sys.argv) > 3 else 20000])
elif cmd == "get":
    s, b = call("GET", sys.argv[2]); print(s, json.dumps(b, indent=1)[:6000])
elif cmd == "post":
    s, b = call("POST", sys.argv[2], json.loads(sys.argv[3])); print(s, json.dumps(b, indent=1)[:3000])
