"""Scripted OpenAI-compatible model for the native-client experiment.

Replies word by word (so streaming shows), and on keywords in the last user text:
  run-tests -> calls the bash tool once (`ls -la && echo tests passed`), then summarises
  ask-me    -> calls the question tool with two choices
  slow-task -> calls the bash tool with a 45 s command, so the session stays working
Anything else gets a short Markdown reply.
"""
import json, sys, time
from http.server import BaseHTTPRequestHandler, ThreadingHTTPServer

PORT = int(sys.argv[1])
LOG = open(sys.argv[2], "a")

REPLY = (
    "Here's the plan.\n\n"
    "1. **Read** the failing test and the code it covers.\n"
    "2. **Fix** the off-by-one in `paginate()`.\n"
    "3. **Run** the suite again.\n\n"
    "Say `run-tests` and I'll run them now."
)


def last_user_text(msgs):
    for m in reversed(msgs):
        if m["role"] == "user":
            c = m.get("content")
            if isinstance(c, str):
                return c
            if isinstance(c, list):
                return " ".join(p.get("text", "") for p in c if isinstance(p, dict))
    return ""


class H(BaseHTTPRequestHandler):

    def log_message(self, *a):
        pass

    def do_GET(self):
        body = json.dumps({"data": [{"id": "fake-model", "object": "model"}]}).encode()
        self.send_response(200); self.send_header("content-type", "application/json")
        self.send_header("content-length", str(len(body))); self.end_headers(); self.wfile.write(body)

    def do_POST(self):
        req = json.loads(self.rfile.read(int(self.headers["content-length"])))
        msgs = req.get("messages", [])
        last_role = msgs[-1]["role"] if msgs else "?"
        text = last_user_text(msgs)
        tools = [t["function"]["name"] for t in req.get("tools", [])]
        LOG.write(f"{time.strftime('%H:%M:%S')} last={last_role} text={text[:60]!r} tools={len(tools)}\n"); LOG.flush()

        call, reply = None, None
        slow = "slow-task" in text and "bash" in tools
        if last_role == "tool":
            reply = "Ran it. **All 42 tests pass** and the build is clean."
        elif "title" in text.lower() and "generate" in text.lower() or (msgs and "title" in str(msgs[0].get("content", ""))[:400].lower() and not tools):
            reply = "Fix pagination off-by-one"
        elif slow:
            call = ("bash", {"command": "sleep 45 && echo migrated", "description": "Migrate the cache"})
        elif "run-tests" in text and "bash" in tools:
            call = ("bash", {"command": "ls -la && echo tests passed", "description": "Run the tests"})
        elif "ask-me" in text and "question" in tools:
            call = ("question", {"questions": [{"header": "Database", "question": "Which database should the cache use?",
                                                "options": [{"label": "SQLite", "description": "Local file"},
                                                            {"label": "Redis", "description": "Shared server"}]}]})
        else:
            reply = REPLY

        self.send_response(200); self.send_header("content-type", "text/event-stream"); self.end_headers()

        def chunk(delta, finish=None):
            d = {"id": "x", "object": "chat.completion.chunk", "created": int(time.time()), "model": "fake-model",
                 "choices": [{"index": 0, "delta": delta, "finish_reason": finish}]}
            self.wfile.write(f"data: {json.dumps(d)}\n\n".encode()); self.wfile.flush()

        if call:
            chunk({"role": "assistant", "tool_calls": [{"index": 0, "id": "call_%d" % int(time.time() * 1000), "type": "function", "function": {"name": call[0], "arguments": ""}}]})
            chunk({"tool_calls": [{"index": 0, "function": {"arguments": json.dumps(call[1])}}]})
            chunk({}, "tool_calls")
        else:
            chunk({"role": "assistant", "content": ""})
            for word in reply.split(" "):
                chunk({"content": word + " "})
                time.sleep(0.06)
            chunk({}, "stop")
        self.wfile.write(b"data: [DONE]\n\n"); self.wfile.flush()


ThreadingHTTPServer(("127.0.0.1", PORT), H).serve_forever()
