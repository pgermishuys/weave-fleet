#!/bin/bash
# Scratch Fleet for the browser-plugin spike (spike/opencode2-browser-plugin), port 5451, scripted model on 4991. Scratch HOME: OpenCode 2 (separate install, the real
# binary linked in) with its own config pointing at the scripted model, and OpenCode 1 linked in.
#   start | restart | stop
W=/home/pgermishuys/source/weave-fleet/.claude/worktrees/spike-oc2-browser
R=/home/pgermishuys/source/weave-fleet/.claude/worktrees/spike-oc2-browser/.poc-runtime
RUN=$R/run
PORT=5451
LLM_PORT=4991

stop() {
  for p in $(pgrep -f "WeaveFleet.Api|opencode|fakellm|chrom"); do
    [ "$p" = "$$" ] && continue
    tr '\0' '\n' < /proc/$p/environ 2>/dev/null | grep -qx "HOME=$RUN/home" && kill -9 $p
  done
  sleep 1
}

scratch_env() {
  export HOME=$RUN/home XDG_CONFIG_HOME=$RUN/config XDG_DATA_HOME=$RUN/data XDG_CACHE_HOME=$RUN/cache XDG_STATE_HOME=$RUN/state
  export TMPDIR=/home/pgermishuys/.cache/oc2b/f PATH=/usr/bin:/bin SHELL=/bin/bash
  unset OPENCODE_INSTALL_DIR XDG_BIN_DIR OPENCODE_CONFIG_DIR OPENCODE_DB OPENROUTER_API_KEY ANTHROPIC_API_KEY OPENAI_API_KEY
}

provider_config() {
  cat <<JSON
{
  // The scripted model (Fleet live check)
  "\$schema": "https://opencode.ai/config.json",
  "model": "fakellm/fake-model",
  "provider": {
    "fakellm": {
      "npm": "@ai-sdk/openai-compatible",
      "options": { "baseURL": "http://127.0.0.1:$LLM_PORT/v1", "apiKey": "x" },
      "models": { "fake-model": { "name": "Fake" } }
    }
  }
}
JSON
}

launch() {
  scratch_env
  export DOTNET_CLI_HOME=$RUN/home NUGET_PACKAGES=/home/pgermishuys/.nuget/packages
  python3 $R/fakellm.py $LLM_PORT $RUN/fakellm.log $RUN/work/alpha/note.txt > /dev/null 2>&1 &
  export Fleet__Browser__ChromePath=/home/pgermishuys/.cache/ms-playwright/chromium-1243/chrome-linux64/chrome
  export ASPNETCORE_ENVIRONMENT=Development Fleet__Host=127.0.0.1 Fleet__Port=$PORT
  export Fleet__DatabasePath=$RUN/fleet/fleet.db Fleet__AnalyticsEnabled=false ASPNETCORE_WEBROOT=$W/client/dist
  cd $W/src/WeaveFleet.Api/bin/Debug/net10.0/linux-x64 && dotnet WeaveFleet.Api.dll >> $RUN/host.log 2>&1 &
  listening() { local n; n=$(grep -c 'Now listening' $RUN/host.log 2>/dev/null); echo "${n:-0}"; }
  i=0; while [ "$(listening)" -lt "$1" ] && [ $i -lt 90 ]; do sleep 1; i=$((i+1)); done
  echo "fleet on $PORT: $(grep -c 'Now listening' $RUN/host.log) listening line(s)"
}

case "$1" in
  start)
    stop; rm -rf $RUN /home/pgermishuys/.cache/oc2b/f; mkdir -p /home/pgermishuys/.cache/oc2b/f
    mkdir -p $RUN/home/.weave/harnesses/opencode2/.opencode $RUN/home/.weave/harnesses/opencode2/config $RUN/home/.opencode/bin \
      $RUN/data $RUN/cache $RUN/state $RUN/config/opencode $RUN/fleet $RUN/tmp $RUN/work/alpha
    ln -s /home/pgermishuys/.weave/harnesses/opencode2/.opencode/bin $RUN/home/.weave/harnesses/opencode2/.opencode/bin
    echo separate > $RUN/home/.weave/harnesses/opencode2/install-mode
    ln -s /home/pgermishuys/.opencode/bin/opencode $RUN/home/.opencode/bin/opencode
    provider_config > $RUN/home/.weave/harnesses/opencode2/config/opencode.json
    provider_config > $RUN/config/opencode/opencode.json
    echo "hello from alpha" > $RUN/work/alpha/note.txt
    git init -q $RUN/work/alpha && git -C $RUN/work/alpha add -A && git -C $RUN/work/alpha -c user.name=fleet -c user.email=fleet@example.com commit -qm init
    git -C $RUN/work/alpha worktree add -q $RUN/work/alpha-worktrees/feature -b feature
    mkdir -p $RUN/work/beta && echo "hello from beta" > $RUN/work/beta/note.txt
    git init -q $RUN/work/beta && git -C $RUN/work/beta add -A && git -C $RUN/work/beta -c user.name=fleet -c user.email=fleet@example.com commit -qm init
    launch 1
    curl -s -X POST http://127.0.0.1:$PORT/api/workspace-roots -H 'content-type: application/json' -d "{\"path\":\"$RUN/work\"}" > /dev/null ;;
  restart) stop; launch $(( $(grep -c 'Now listening' $RUN/host.log) + 1 )) ;;
  stop) stop; echo "stopped" ;;
esac
