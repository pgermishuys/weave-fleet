"""Anthropic Messages API stand-in. Logs each request (system, messages, tool names). `CALL <Tool> <json>` lines in the
last user text make the model call those tools in order; then it replies with text."""
import json, re, sys, time
from http.server import BaseHTTPRequestHandler, ThreadingHTTPServer
LOG = open(sys.argv[2], "a")

def text_of(c):
    return c if isinstance(c, str) else " ".join(p.get("text", "") for p in (c or []) if isinstance(p, dict) and p.get("type") == "text")

class H(BaseHTTPRequestHandler):
    def log_message(self, *a): pass
    def do_GET(self):
        self.send_response(200); self.send_header("content-type", "application/json"); self.end_headers(); self.wfile.write(b"{}")
    def do_HEAD(self):
        self.send_response(200); self.end_headers()
    def do_POST(self):
        req = json.loads(self.rfile.read(int(self.headers["content-length"])))
        if "count_tokens" in self.path:
            self.send_response(200); self.send_header("content-type", "application/json"); self.end_headers(); self.wfile.write(b'{"input_tokens": 10}'); return
        msgs = req.get("messages", [])
        LOG.write(json.dumps({"t": time.time(), "path": self.path, "model": req.get("model"), "tools": [t.get("name") for t in req.get("tools", [])], "system": req.get("system"), "messages": msgs}) + "\n"); LOG.flush()
        # the last user message that carries text (tool results are user messages too)
        idx = max((i for i, m in enumerate(msgs) if m["role"] == "user" and text_of(m["content"]).strip()), default=-1)
        said = "\n".join(text_of(m["content"]) for m in msgs[idx:idx + 1])
        calls = re.findall(r"CALL (\S+) (\{.*?\})\s*(?:$|\n)", said, re.M)
        made = sum(1 for m in msgs[idx + 1:] if m["role"] == "assistant" for p in (m["content"] if isinstance(m["content"], list) else []) if p.get("type") == "tool_use")
        self.send_response(200); self.send_header("content-type", "text/event-stream"); self.end_headers()
        def ev(name, data):
            self.wfile.write(f"event: {name}\ndata: {json.dumps(data)}\n\n".encode()); self.wfile.flush()
        ev("message_start", {"type": "message_start", "message": {"id": "msg_1", "type": "message", "role": "assistant", "model": req.get("model"), "content": [], "stop_reason": None, "usage": {"input_tokens": 10, "output_tokens": 1}}})
        if made < len(calls) and req.get("tools"):
            name, args = calls[made]
            ev("content_block_start", {"type": "content_block_start", "index": 0, "content_block": {"type": "tool_use", "id": "toolu_%d" % int(time.time() * 1000), "name": name, "input": {}}})
            ev("content_block_delta", {"type": "content_block_delta", "index": 0, "delta": {"type": "input_json_delta", "partial_json": args}})
            ev("content_block_stop", {"type": "content_block_stop", "index": 0})
            stop = "tool_use"
        else:
            ev("content_block_start", {"type": "content_block_start", "index": 0, "content_block": {"type": "text", "text": ""}})
            ev("content_block_delta", {"type": "content_block_delta", "index": 0, "delta": {"type": "text_delta", "text": "Done from the fake model."}})
            ev("content_block_stop", {"type": "content_block_stop", "index": 0})
            stop = "end_turn"
        ev("message_delta", {"type": "message_delta", "delta": {"stop_reason": stop}, "usage": {"output_tokens": 5}})
        ev("message_stop", {"type": "message_stop"})

ThreadingHTTPServer(("127.0.0.1", int(sys.argv[1])), H).serve_forever()
