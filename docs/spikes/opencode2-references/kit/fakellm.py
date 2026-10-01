"""OpenAI-compatible scripted model. Logs every request (system, messages, tool names) as JSON lines.
A user message containing `CALL <tool> <json-args>` makes the model's first reply in that turn a call to <tool>
(repeat CALL lines → one call per model step, in order). Otherwise it replies with plain text."""
import json, re, sys, time
from http.server import BaseHTTPRequestHandler, ThreadingHTTPServer

LOG = open(sys.argv[2], "a")


def text_of(c):
    return c if isinstance(c, str) else " ".join(p.get("text", "") for p in (c or []) if isinstance(p, dict))


class H(BaseHTTPRequestHandler):
    def log_message(self, *a):
        pass

    def do_GET(self):
        body = json.dumps({"data": [{"id": "fake-model", "object": "model"}]}).encode()
        self.send_response(200); self.send_header("content-type", "application/json"); self.end_headers(); self.wfile.write(body)

    def do_POST(self):
        req = json.loads(self.rfile.read(int(self.headers["content-length"])))
        msgs = req.get("messages", [])
        tools = [t["function"]["name"] for t in req.get("tools", [])]
        LOG.write(json.dumps({"t": time.time(), "tools": tools, "messages": msgs}) + "\n"); LOG.flush()
        # index of the last user message; count tool calls made since it
        last_user_i = max((i for i, m in enumerate(msgs) if m["role"] == "user"), default=-1)
        said = text_of(msgs[last_user_i]["content"]) if last_user_i >= 0 else ""
        calls = re.findall(r"CALL (\S+) (\{.*?\})\s*(?:$|\n)", said, re.M)
        made = sum(len(m.get("tool_calls") or []) for m in msgs[last_user_i + 1:] if m["role"] == "assistant")
        self.send_response(200); self.send_header("content-type", "text/event-stream"); self.end_headers()

        def chunk(delta, finish=None):
            c = {"id": "c1", "object": "chat.completion.chunk", "created": int(time.time()), "model": "fake-model",
                 "choices": [{"index": 0, "delta": delta, "finish_reason": finish}]}
            self.wfile.write(b"data: " + json.dumps(c).encode() + b"\n\n"); self.wfile.flush()

        if made < len(calls):
            name, args = calls[made]
            chunk({"role": "assistant", "tool_calls": [{"index": 0, "id": "call_%d" % int(time.time() * 1000), "type": "function", "function": {"name": name, "arguments": ""}}]})
            chunk({"tool_calls": [{"index": 0, "function": {"arguments": args}}]})
            chunk({}, "tool_calls")
        else:
            for w in ["Done ", "from ", "the ", "fake ", "model."]:
                chunk({"role": "assistant", "content": w}); time.sleep(0.02)
            chunk({}, "stop")
        u = {"id": "c1", "object": "chat.completion.chunk", "choices": [], "usage": {"prompt_tokens": 10, "completion_tokens": 5, "total_tokens": 15}}
        self.wfile.write(b"data: " + json.dumps(u).encode() + b"\n\ndata: [DONE]\n\n"); self.wfile.flush()


ThreadingHTTPServer(("127.0.0.1", int(sys.argv[1])), H).serve_forever()
