#!/bin/bash
# Standalone OpenCode 2 (2.0.18) on a scratch HOME, scripted model on 4992, server on 5469.  start | stop
K=/home/pgermishuys/source/weave-fleet/.claude/worktrees/spike-oc2-references/.poc-runtime
RUN=/home/pgermishuys/.cache/fleet-oc2-references/run; LLM_PORT=4992; PORT=5469
BIN=${V2BIN:-/home/pgermishuys/.weave/harnesses/opencode2/.opencode/bin/opencode}
stop() {
  for p in $(pgrep -f "opencode|fakellm"); do
    tr '\0' '\n' < /proc/$p/environ 2>/dev/null | grep -qx "HOME=$RUN/home" && kill -9 $p
  done; sleep 1
}
case "$1" in
  start)
    stop
    export HOME=$RUN/home XDG_CONFIG_HOME=$RUN/config XDG_DATA_HOME=$RUN/data XDG_CACHE_HOME=$RUN/cache XDG_STATE_HOME=$RUN/state
    export TMPDIR=$RUN/tmp PATH=/usr/bin:/bin SHELL=/bin/bash
    export OPENCODE_SERVER_PASSWORD=pw
    unset OPENCODE_CONFIG_DIR OPENCODE_DB ANTHROPIC_API_KEY OPENAI_API_KEY OPENROUTER_API_KEY GH_TOKEN GITHUB_TOKEN
    mkdir -p $HOME $XDG_CONFIG_HOME/opencode $XDG_DATA_HOME $XDG_CACHE_HOME $XDG_STATE_HOME $TMPDIR
    setsid python3 $K/fakellm.py $LLM_PORT $RUN/fakellm.log > /dev/null 2>&1 < /dev/null &
    cd $RUN/work/proj && setsid $BIN serve --port $PORT --hostname 127.0.0.1 --print-logs >> $RUN/v2.log 2>&1 < /dev/null &
    for i in $(seq 1 60); do curl -s -o /dev/null -u opencode:pw http://127.0.0.1:$PORT/api/info && break; sleep 1; done
    curl -s -u opencode:pw http://127.0.0.1:$PORT/api/info; echo ;;
  stop) stop; echo stopped ;;
esac
