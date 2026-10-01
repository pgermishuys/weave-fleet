#!/bin/bash
# OpenCode 2 alone (no Fleet) on a scratch HOME, pointed at the scripted model. No lock needed.
#   start <version|installed> | stop
R=/home/pgermishuys/source/weave-fleet/.claude/worktrees/spike-oc2-browser/.poc-runtime
RUN=$R/v2run
PORT=5452
LLM_PORT=4991
stop() {
  for p in $(pgrep -f "opencode|fakellm"); do
    tr '\0' '\n' < /proc/$p/environ 2>/dev/null | grep -qx "HOME=$RUN/home" && kill -9 $p
  done; sleep 0.5
}
case "$1" in
  start)
    stop; rm -rf $RUN; mkdir -p $RUN/home $RUN/config/opencode $RUN/data $RUN/cache $RUN/state $RUN/tmp $RUN/work/alpha
    if [ "$2" = installed ]; then BIN=/home/pgermishuys/.weave/harnesses/opencode2/.opencode/bin/opencode
    else BIN=/home/pgermishuys/.cache/opencode2-spike/versions/$2/.opencode/bin/opencode; fi
    PL=""; [ -n "$3" ] && PL="\"plugins\": [\"$3\"],"
    cat > $RUN/config/opencode/opencode.json <<JSON
{ "model": "fakellm/fake-model", $PL
  "provider": { "fakellm": { "npm": "@ai-sdk/openai-compatible",
     "options": { "baseURL": "http://127.0.0.1:$LLM_PORT/v1", "apiKey": "x" },
     "models": { "fake-model": { "name": "Fake" } } } } }
JSON
    echo "hello" > $RUN/work/alpha/note.txt; git init -q $RUN/work/alpha
    export HOME=$RUN/home XDG_CONFIG_HOME=$RUN/config XDG_DATA_HOME=$RUN/data XDG_CACHE_HOME=$RUN/cache XDG_STATE_HOME=$RUN/state
    export TMPDIR=$RUN/tmp PATH=/usr/bin:/bin SHELL=/bin/bash OPENCODE_SERVER_PASSWORD=spike
    unset OPENROUTER_API_KEY ANTHROPIC_API_KEY OPENAI_API_KEY
    python3 $R/fakellm.py $LLM_PORT $RUN/fakellm.log > /dev/null 2>&1 &
    $BIN serve --port $PORT --hostname 127.0.0.1 --print-logs > $RUN/v2.log 2>&1 &
    for i in $(seq 60); do curl -s -u opencode:spike http://127.0.0.1:$PORT/api/info >/dev/null && break; sleep 0.5; done
    echo "v2 $($BIN --version 2>/dev/null) on $PORT: $(curl -s -u opencode:spike http://127.0.0.1:$PORT/api/info | head -c 200)";;
  stop) stop; echo stopped;;
esac
