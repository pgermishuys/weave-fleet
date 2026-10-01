"""V1 driver: new | prompt <sid> <text> | perms"""
import json, sys, urllib.request, urllib.parse
BASE="http://127.0.0.1:5470/"; D="/home/pgermishuys/.cache/fleet-oc2-references/run/work/proj"
def call(m, p, b=None, t=60):
    sep = "&" if "?" in p else "?"
    req=urllib.request.Request(BASE+p+sep+"directory="+urllib.parse.quote(D), data=None if b is None else json.dumps(b).encode(), method=m, headers={"content-type":"application/json"})
    try:
        with urllib.request.urlopen(req, timeout=t) as r: x=r.read().decode(); return r.status, (json.loads(x) if x else None)
    except Exception as e: return "ERR", str(e)[:300]
c=sys.argv[1]
if c=="new":
    body={} if len(sys.argv)<3 else {"permission": json.loads(sys.argv[2])}
    print(call("POST","session",body)[1]["id"])
elif c=="prompt":
    print(str(call("POST",f"session/{sys.argv[2]}/prompt_async",{"parts":[{"type":"text","text":sys.argv[3]}]}))[:300])
elif c=="perms":
    print(json.dumps(call("GET","permission")[1], indent=1)[:3000])
