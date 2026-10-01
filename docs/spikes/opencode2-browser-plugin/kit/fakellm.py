"""Minimal OpenAI-compatible chat completions server that streams a fixed reply (no credentials needed).
If the last user message contains 'use a tool', the first reply is a bash/shell tool call; the next reply is text."""
import json, sys, time
from http.server import BaseHTTPRequestHandler, ThreadingHTTPServer

LOG = open(sys.argv[2] if len(sys.argv) > 2 else "fakellm.log", "a")


class H(BaseHTTPRequestHandler):
    def log_message(self, *a):
        pass

    def do_GET(self):
        body = json.dumps({"data": [{"id": "fake-model", "object": "model"}]}).encode()
        self.send_response(200); self.send_header("content-type", "application/json"); self.end_headers(); self.wfile.write(body)

    def do_POST(self):
        req = json.loads(self.rfile.read(int(self.headers["content-length"])))
        msgs = req.get("messages", [])
        LOG.write(json.dumps({"path": self.path, "system": [m.get("content") for m in msgs if m.get("role") == "system"], "last": msgs[-1] if msgs else None, "messages": msgs, "tools": req.get("tools", [])}) + "\n"); LOG.flush()
        last_user = next((m for m in reversed(msgs) if m["role"] == "user"), {})
        text = json.dumps(last_user.get("content"))
        tool_names = [t["function"]["name"] for t in req.get("tools", [])]
        shell = next((n for n in ("bash", "shell") if n in tool_names), None)
        # keyword in the prompt -> (tool, args); only on the turn's first model call
        plans = {
            "use a tool": (shell, {"command": "echo from-tool", "description": "Echo"}),
            "fleet tool": ("fleet_echo", {"text": "ping"}),
            "ask me": ("question", {"questions": [{"question": "Pick one", "header": "Pick", "options": [{"label": "A", "description": "a"}, {"label": "B", "description": "b"}]}]}),
            "delegate": (next((n for n in ("task", "subagent") if n in tool_names), None),
                         {"description": "Child work", "prompt": "say hello", "subagent_type": "general", "agent": "general"}),
        }
        raw = last_user.get("content")
        said = raw if isinstance(raw, str) else " ".join(part.get("text", "") for part in (raw or []) if isinstance(part, dict))
        import re as _re
        m = _re.search(r"remember that (.+?)(?: on this machine)?[\.\"]*$", said.strip(), _re.I)
        if m:
            plans["remember that"] = ("fleet_memory_save", {"text": m.group(1).strip().rstrip(".") + ".", "list": "machine" if "on this machine" in said else "repository", "kind": "from-you", "replaces": ""})
        c = _re.search(r"run code ([a-z0-9_-]+)", said)
        if c:
            import os as _os
            code = open(_os.path.join(_os.path.dirname(_os.path.abspath(__file__)), "scripts", c.group(1) + ".js")).read()
            plans["run code"] = ("execute", {"code": code})
        t = _re.search(r"call tool ([a-z0-9_]+) (\{.*\})", said)
        if t:
            plans["call tool"] = (t.group(1), json.loads(t.group(2)))
        f = _re.search(r"forget note ([0-9a-f]+)", said)
        if f:
            plans["forget note"] = ("fleet_memory_forget", {"id": f.group(1)})
        plan = next((p for k, p in plans.items() if k in text), None)
        first_call = msgs[-1]["role"] == "user"
        want_tool = plan is not None and first_call and plan[0] in tool_names
        self.send_response(200); self.send_header("content-type", "text/event-stream"); self.end_headers()

        def chunk(delta, finish=None):
            c = {"id": "c1", "object": "chat.completion.chunk", "created": int(time.time()), "model": "fake-model",
                 "choices": [{"index": 0, "delta": delta, "finish_reason": finish}]}
            self.wfile.write(b"data: " + json.dumps(c).encode() + b"\n\n"); self.wfile.flush()

        if want_tool:
            args = json.dumps(plan[1])
            chunk({"role": "assistant", "tool_calls": [{"index": 0, "id": "call_%d" % int(time.time() * 1000), "type": "function", "function": {"name": plan[0], "arguments": ""}}]})
            chunk({"tool_calls": [{"index": 0, "function": {"arguments": args}}]})
            chunk({}, "tool_calls")
        else:
            for w in ["Hello ", "from ", "the ", "fake ", "model."]:
                chunk({"role": "assistant", "content": w}); time.sleep(0.05)
            chunk({}, "stop")
        u = {"id": "c1", "object": "chat.completion.chunk", "choices": [], "usage": {"prompt_tokens": 10, "completion_tokens": 5, "total_tokens": 15}}
        self.wfile.write(b"data: " + json.dumps(u).encode() + b"\n\ndata: [DONE]\n\n"); self.wfile.flush()


ThreadingHTTPServer(("127.0.0.1", int(sys.argv[1])), H).serve_forever()
