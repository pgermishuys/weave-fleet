#!/usr/bin/env bash
# Runs real `claude` sessions against fake-anthropic.mjs (no model, no secrets) and leaves their transcripts in
# <out>/transcripts/<scenario>.jsonl for Fleet's ClaudeTranscriptReader (see the test ClaudeTranscriptFormatCheckTests).
#   scripts/claude-transcript-check/run.sh <out dir>
set -euo pipefail
here="$(cd "$(dirname "$0")" && pwd)"
out="$(mkdir -p "${1:?usage: run.sh <out dir>}" && cd "$1" && pwd)"
rm -rf "$out/config" "$out/work" "$out/transcripts"
mkdir -p "$out/config" "$out/work" "$out/transcripts"
echo "claude $(claude --version)" | tee "$out/version.txt"

node "$here/fake-anthropic.mjs" 0 >"$out/fake.log" 2>&1 &
fake=$!
trap 'kill $fake 2>/dev/null || true' EXIT
for _ in $(seq 50); do grep -q '^listening ' "$out/fake.log" && break; sleep 0.1; done
port="$(sed -n 's/^listening //p' "$out/fake.log" | head -1)"

export CLAUDE_CONFIG_DIR="$out/config" ANTHROPIC_BASE_URL="http://127.0.0.1:$port" ANTHROPIC_API_KEY=sk-ant-fake
export DISABLE_TELEMETRY=1 DISABLE_AUTOUPDATER=1 CLAUDE_CODE_DISABLE_NONESSENTIAL_TRAFFIC=1
unset CLAUDE_CODE_ENTRYPOINT CLAUDECODE CLAUDE_CODE_SESSION_ID CLAUDE_PID CLAUDE_CODE_CHILD_SESSION || true
cd "$out/work"
echo "hello from the fixture file" > notes.txt

claude_run() { timeout 120 claude -p --dangerously-skip-permissions "$@" </dev/null; }
newid() { python3 -c 'import uuid; print(uuid.uuid4())'; }
keep() { # keep <scenario> <session id>
  local f; f="$(find "$out/config/projects" -name "$2.jsonl" | head -1)"
  [ -n "$f" ] || { echo "no transcript for $1 ($2)" >&2; return 1; }
  cp "$f" "$out/transcripts/$1.jsonl"
  echo "$1: $(wc -l <"$out/transcripts/$1.jsonl") lines"
}

# Plain turns, the second in a resumed run.
id=$(newid)
claude_run "Say hello." --session-id "$id" >/dev/null
claude_run "Say it again." --resume "$id" >/dev/null
keep plain-resumed "$id"

# A tool call.
id=$(newid)
claude_run "Please look at the file. TOOL:$PWD/notes.txt" --session-id "$id" >/dev/null
keep tool "$id"

# /compact, when this Claude Code can run it in a print session.
id=$(newid)
claude_run "Say hello." --session-id "$id" >/dev/null
claude_run "Say hello once more." --resume "$id" >/dev/null
if claude_run "/compact" --resume "$id" >"$out/compact.out" 2>&1; then
  claude_run "And now?" --resume "$id" >/dev/null
  keep compact "$id"
else
  echo "compact: not reachable here, skipped ($(head -c 200 "$out/compact.out"))"
fi
