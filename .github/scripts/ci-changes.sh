#!/usr/bin/env bash
# Which of ci.yml's job groups a change can affect. Reads changed paths, one per line, on stdin (old and new path of a
# rename both count), and writes `<group>=true|false` lines for $GITHUB_OUTPUT.
#
# The default is "run": a path runs every group unless a rule below names what reads it, and each rule says why. Build
# files, lockfiles, the workflows and any path no rule knows run everything. Pushes to main and dispatches don't call
# this; they always run everything.
#
#   dotnet    .NET Unit, .NET Live, Mod host, Windows processes, Mod file safety
#   aot       AOT Publish
#   e2e       E2E (builds the client and the API, drives the SPA)
#   client    Client checks, Client tests
#   desktop   Desktop
#   launchers Launchers
set -euo pipefail

groups=(dotnet aot e2e client desktop launchers)
declare -A run=()
all() { for g in "${groups[@]}"; do run[$g]=1; done; }
on() { for g in "$@"; do run[$g]=1; done; }

# --all: every group (pushes to main, dispatches).
if [ "${1:-}" = "--all" ]; then
  for g in "${groups[@]}"; do echo "$g=true"; done
  exit 0
fi

# The .NET build embeds or copies files from outside src/ (WeaveFleet.Infrastructure.csproj: opencode/fleet,
# opencode/skills, opencode/built-in-skills, opencode2/fleet; WeaveFleet.Application.csproj: bun/bun.json;
# WeaveFleet.TestHarness.csproj: tests/contracts; WeaveFleet.Api.csproj: mods/host (built and shipped), mods/samples,
# client/src/assets/weave_logo.png). Tests read docs/workflows.md (WorkflowYamlWriterTests), bun/bun.json,
# opencode/ and opencode2/ through the repository root. So each of those runs every .NET job.
net=(dotnet aot e2e)

count=0
while IFS= read -r path; do
  [ -n "$path" ] || continue
  count=$((count + 1))
  case "$path" in
    # Everything a build or a job's setup reads: run everything.
    .github/*|Directory.*|global.json|*.slnx|build.cs|_build/*|NuGet.config|.bun-version|.editorconfig|.gitignore|.gitattributes) all ;;
    *package-lock.json|*bun.lock|*bun.lockb|*packages.lock.json) all ;;

    # Read by nothing in ci.yml: screenshots, the repository's own agent notes and plans, root docs, and the phone app
    # (mobile.yml tests it). No project, test, script or client file reads these (searched every csproj, props,
    # .cs file, client/ and mods/ source, scripts/ and ci.yml).
    mockups/*|.weave/*|mobile/*) ;;
    README.md|AGENTS.md|RELEASE.md) ;;

    # docs/: only docs/workflows.md is read (WorkflowYamlWriterTests reads it through the repository root).
    docs/workflows.md) on "${net[@]}" ;;
    docs/*) ;;

    # The API embeds the logo in every build (WeaveFleet.Api.csproj: EmbeddedResource weave_logo.png).
    client/src/assets/weave_logo.png) on "${net[@]}" client ;;
    # Otherwise no .NET project or test reads client/ (the API copies client/dist only when it exists, and no job but
    # E2E builds it); E2E builds the SPA and drives it.
    client/*) on client e2e ;;

    desktop/*) on desktop ;;

    # The client's mod-types drift test reads mods/types/fleet-mods.d.ts; the mod host's tsconfig includes it.
    mods/types/*) on "${net[@]}" client ;;
    # Mod host job (mods/host), the API builds and ships mods/host and copies mods/samples.
    mods/*) on "${net[@]}" ;;

    # The client's worktree-naming conformance test reads tests/contracts/; the TestHarness embeds them.
    tests/contracts/*) on "${net[@]}" client ;;
    # Launchers' test-launchers.sh reads the options Program.cs parses.
    src/WeaveFleet.Api/Program.cs) on "${net[@]}" launchers ;;
    # The client's mock API and preview read src/WeaveFleet.Api/Browser/preview-bridge.js.
    src/WeaveFleet.Api/Browser/*) on "${net[@]}" client ;;
    src/*|tests/*|aspire/*|opencode2/*|bun/*) on "${net[@]}" ;;
    # The client's mock API plugin reads opencode/built-in-skills (vite.config.ts loads it for every build).
    opencode/*) on "${net[@]}" client ;;

    # package.sh (AOT) packs launcher.sh; the Launchers job runs the launcher tests.
    scripts/*) on aot launchers ;;

    *) all ;;
  esac
done

# An empty list (or one the caller couldn't read) runs everything.
[ "$count" -gt 0 ] || all

for g in "${groups[@]}"; do
  if [ -n "${run[$g]:-}" ]; then echo "$g=true"; else echo "$g=false"; fi
done
