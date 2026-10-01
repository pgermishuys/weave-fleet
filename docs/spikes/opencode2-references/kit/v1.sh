#!/bin/bash
# Standalone OpenCode 1 (1.18.x) on a scratch HOME, scripted model on 4992, server on 5470.  start | stop
K=/home/pgermishuys/source/weave-fleet/.claude/worktrees/spike-oc2-references/.poc-runtime
R=/home/pgermishuys/.cache/fleet-oc2-references/v1
stop() { for p in $(ps -eo pid=,args= | awk '/opencode serve --port 5470|fakellm.py 4992|fakeanthropic.py 4992/ && !/awk/ {print $1}'); do kill -9 $p; done; sleep 1; }
case "$1" in
  start) stop
    mkdir -p $R/{home,config/opencode,data,cache,state,tmp}
    cp /home/pgermishuys/.cache/fleet-oc2-references/run/config/opencode/opencode.json $R/config/opencode/
    setsid python3 $K/fakellm.py 4992 $R/fakellm.log >/dev/null 2>&1 </dev/null &
    cd /home/pgermishuys/.cache/fleet-oc2-references/run/work/proj
    env -i HOME=$R/home XDG_CONFIG_HOME=$R/config XDG_DATA_HOME=$R/data XDG_CACHE_HOME=$R/cache XDG_STATE_HOME=$R/state TMPDIR=$R/tmp PATH=/usr/bin:/bin SHELL=/bin/bash \
      setsid /home/pgermishuys/.opencode/bin/opencode serve --port 5470 --hostname 127.0.0.1 > $R/v1.log 2>&1 </dev/null &
    for i in $(seq 1 40); do curl -s -o /dev/null http://127.0.0.1:5470/config && break; sleep 1; done
    curl -s http://127.0.0.1:5470/config | head -c 200; echo ;;
  stop) stop; echo stopped ;;
esac
