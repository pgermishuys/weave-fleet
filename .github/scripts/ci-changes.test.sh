#!/usr/bin/env bash
# Checks ci-changes.sh against fixed cases. The Changed paths job runs it first, so a broken rule fails CI instead of
# quietly skipping jobs. Each case: changed paths (space-separated) | the groups that must run (space-separated).
set -euo pipefail
here="$(cd "$(dirname "$0")" && pwd)"
all="dotnet aot e2e client desktop launchers phone"
cases=(
  "client/src/main.ts|client e2e phone"
  "client/src/lib/api/sessions.ts|client e2e phone"
  "client/package.json|client e2e phone"
  "client/vite.config.ts|client e2e"
  "client/scripts/lint-design-system.ts|client e2e"
  "client/src/assets/weave_logo.png|dotnet aot e2e client phone"
  "src/WeaveFleet.Application/Foo.cs|dotnet aot e2e"
  "src/WeaveFleet.Api/Program.cs|dotnet aot e2e launchers"
  "src/WeaveFleet.Api/Browser/preview-bridge.js|dotnet aot e2e client"
  "tests/WeaveFleet.Api.Tests/X.cs|dotnet aot e2e"
  "tests/contracts/worktree-naming-cases.json|dotnet aot e2e client"
  "opencode/skills/fleet/SKILL.md|dotnet aot e2e client"
  "opencode2/fleet/index.js|dotnet aot e2e"
  "bun/bun.json|dotnet aot e2e"
  "aspire/AppHost/Program.cs|dotnet aot e2e"
  "mods/host/src/main.ts|dotnet aot e2e"
  "mods/types/fleet-mods.d.ts|dotnet aot e2e client"
  "desktop/src/main.ts|desktop"
  "scripts/launcher.sh|aot launchers"
  "scripts/pre-push-check.sh scripts/test-pre-push-check.sh scripts/install-pre-push.sh|launchers"
  "docs/workflows.md|dotnet aot e2e"
  "docs/machines.md|"
  "mockups/x/a.png .weave/plans/p.md README.md AGENTS.md RELEASE.md|"
  "mobile/README.md|phone"
  "mobile/src/app/new.tsx|phone"
  "mobile/bun.lock|$all"
  ".github/workflows/mobile.yml|$all"
  "client/src/main.ts desktop/src/main.ts|client e2e desktop phone"
  ".github/workflows/ci.yml|$all"
  ".github/scripts/ci-changes.sh|$all"
  "Directory.Packages.props|$all"
  "global.json|$all"
  "WeaveFleet.slnx|$all"
  "client/package-lock.json|$all"
  "mods/host/bun.lock|$all"
  ".bun-version|$all"
  "catalog/catalog.json|$all"
  "some-new-folder/file.txt|$all"
  "client/src/main.ts something-unknown|$all"
  "|$all"
)
fail=0
for c in "${cases[@]}"; do
  paths="${c%%|*}"; want="${c#*|}"
  got=$(tr ' ' '\n' <<<"$paths" | "$here/ci-changes.sh" | sed -n 's/=true$//p' | tr '\n' ' ' | sed 's/ $//')
  want_sorted=$(tr ' ' '\n' <<<"$want" | awk NF | sort | tr '\n' ' ' | sed 's/ $//')
  got_sorted=$(tr ' ' '\n' <<<"$got" | awk NF | sort | tr '\n' ' ' | sed 's/ $//')
  if [ "$want_sorted" != "$got_sorted" ]; then
    echo "::error::ci-changes.sh: '$paths' runs [$got_sorted], expected [$want_sorted]"; fail=1
  fi
done
[ "$("$here/ci-changes.sh" --all | grep -c '=true$')" -eq 7 ] || { echo "::error::ci-changes.sh --all doesn't run every group"; fail=1; }
[ "$fail" -eq 0 ] && echo "ci-changes.sh: ${#cases[@]} cases pass"
exit "$fail"
