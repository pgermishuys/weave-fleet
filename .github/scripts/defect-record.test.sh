#!/usr/bin/env bash
# Checks defect-record.sh against fixed cases. The Defect record workflow runs it first, so a broken rule fails the
# check instead of quietly passing every PR. Each case: want (pass/fail) | title | body ("\n" is a newline) |
# text the output must contain (optional).
set -euo pipefail
here="$(cd "$(dirname "$0")" && pwd)"

ok='- **Found by:** bug-check\n- **Cause:** #812\n- **Shipped in:** v0.71.0\n- **Guard:** `AutomationsPage.test.ts` says why a load failed'
cases=(
  # Which PRs are fix PRs: the title prefix alone.
  "pass|feat(jobs): a queue of work across machines|No record at all.|Not a fix PR"
  "pass|test(relay): fix flaky A_queued_message test|Flake fix titled test:, not checked.|Not a fix PR"
  "pass|ci: pin OpenCode 2 to 2.2.6||Not a fix PR"
  "pass|Revert \"fix(ui): focus ring\"||Not a fix PR"
  "pass|fixture: not a fix prefix||Not a fix PR"
  "pass|fixes(ui): not the conventional prefix||Not a fix PR"
  "fail|fix(automations): a failed load says so|Body without a record.|Found by is missing"
  "fail|fix: no scope|Body.|Guard is missing"
  "fail|Fix(UI): capitalised|Body.|Cause is missing"
  "fail|fix!: breaking fix|Body.|Shipped in is missing"
  "fail|  fix(ui): leading space|Body.|Found by is missing"
  # A complete record, in the forms people write it.
  "pass|fix(automations): a failed load says so|Intro.\n\n## Defect record\n$ok\n\n## Test\nMore.|Defect record complete"
  "pass|fix: plain lines|Found by: CI\nCause: a1b2c3d\nShipped in: not released\nGuard: CI's typecheck job"
  "pass|fix: bold outside colon|**Found by**: you (the user, in #fleet)\n**Cause**: old\n**Shipped in**: 0.64.0\n**Guard**: none: the screen it broke was removed in this PR"
  "pass|fix: star bullets, CRLF|* Found by: in passing\r\n* Cause: external (OpenCode 2.2.6)\r\n* Shipped in: v0.70.1\r\n* Guard: lint rule no-raw-button\r\n"
  "pass|fix: url cause|- Found by: live check\n- Cause: https://github.com/pgermishuys/weave-fleet/pull/811\n- Shipped in: v0.68.0\n- Guard: SettingsPermissions.test.ts"
  "pass|fix: commit url, backticks|- Found by: \`review\`\n- Cause: https://github.com/o/r/commit/0123abcd\n- Shipped in: \`v0.68.0\`\n- Guard: \`dotnet test --filter Foo\`"
  "pass|fix: other spellings|- Found by: bug check\n- Cause: #1, #2\n- Shipped in: v1.2.3.\n- Guard: none — UI copy only, nothing to test"
  "pass|fix: quoted|> - **Found by:** flake\n> - **Cause:** #900\n> - **Shipped in:** not released\n> - **Guard:** QueuedFollowUpTests"
  "pass|fix: report|- **Found by:** report (fleet-reports #40)\n- **Cause:** #700\n- **Shipped in:** v0.65.0\n- **Guard:** MachineCardTests.Shows_reason"
  "pass|fix: prose cause above the record|## Cause and fix\n- **Cause:** the menu button floats over the heading.\n\n## Defect record\n$ok"
  # Incomplete or malformed records.
  "fail|fix: template left as is|## Defect record\n<!-- Found by: you, report, ...\nCause: #123\nShipped in: v0.71.0\nGuard: a test -->\n- **Found by:**\n- **Cause:**\n- **Shipped in:**\n- **Guard:**|Found by is missing"
  "fail|fix: only in a code block|\`\`\`\nFound by: CI\nCause: old\nShipped in: not released\nGuard: a test\n\`\`\`|Guard is missing"
  "fail|fix: unknown found by|- Found by: a customer\n- Cause: #1\n- Shipped in: v0.1.0\n- Guard: X test|isn't one of"
  "fail|fix: found by prefix only|- Found by: CIA\n- Cause: #1\n- Shipped in: v0.1.0\n- Guard: X test|isn't one of"
  "fail|fix: vague cause|- Found by: CI\n- Cause: the store never read the error\n- Shipped in: v0.1.0\n- Guard: X test|Cause \"the store"
  "fail|fix: short sha|- Found by: CI\n- Cause: a1b2c3\n- Shipped in: v0.1.0\n- Guard: X test|Cause \"a1b2c3\""
  "fail|fix: bad release|- Found by: CI\n- Cause: old\n- Shipped in: last week\n- Guard: X test|Shipped in \"last week\""
  "fail|fix: release without patch|- Found by: CI\n- Cause: old\n- Shipped in: v0.71\n- Guard: X test|Shipped in \"v0.71\""
  "fail|fix: guard none bare|- Found by: CI\n- Cause: old\n- Shipped in: v0.1.0\n- Guard: none|needs a reason"
  "fail|fix: guard none short|- Found by: CI\n- Cause: old\n- Shipped in: v0.1.0\n- Guard: none: copy|needs a reason"
  "fail|fix: guard tbd|- Found by: CI\n- Cause: old\n- Shipped in: v0.1.0\n- Guard: TBD|placeholder"
  "fail|fix: guard dash|- Found by: CI\n- Cause: old\n- Shipped in: v0.1.0\n- Guard: -|placeholder"
  "fail|fix: long prose cause is cut|- **Cause:** \`readPageMessage\` refused any page-choice whose summary was over 200 characters, so the pick was dropped without a word.|summary was over 200 characte...\" should start"
  "fail|fix: everything missing|Nothing here.|- **Guard:** the test, lint rule or CI check"
)
fail=0
for c in "${cases[@]}"; do
  want="${c%%|*}"; rest="${c#*|}"
  title="${rest%%|*}"; rest="${rest#*|}"
  body="${rest%%|*}"; expect=""
  [ "$body" != "$rest" ] && expect="${rest#*|}"
  set +e
  out="$(printf '%b' "$body" | "$here/defect-record.sh" "$title" 2>&1)"; code=$?
  set -e
  got=pass; [ "$code" -ne 0 ] && got=fail
  if [ "$got" != "$want" ]; then
    echo "::error::defect-record.sh: '$title' should $want, got $got:"; printf '%s\n' "$out" | sed 's/^/    /'; fail=1
  elif [ -n "$expect" ] && ! grep -qF -- "$expect" <<<"$out"; then
    echo "::error::defect-record.sh: '$title' output lacks '$expect':"; printf '%s\n' "$out" | sed 's/^/    /'; fail=1
  fi
done

# The PR template: left as it is, a fix PR fails on all four lines; filled in, it passes.
tpl="$here/../pull_request_template.md"
if out="$("$here/defect-record.sh" "fix: x" < "$tpl")" || [ "$(grep -c 'is missing or empty' <<<"$out")" -ne 4 ]; then
  echo "::error::defect-record.sh: the unfilled PR template should fail a fix PR on all four lines:"; printf '%s\n' "$out"; fail=1
fi
filled="$(sed -E 's/^(- \*\*Found by:\*\*).*/\1 CI/; s/^(- \*\*Cause:\*\*).*/\1 old/; s/^(- \*\*Shipped in:\*\*).*/\1 not released/; s/^(- \*\*Guard:\*\*).*/\1 a test/' "$tpl")"
if ! out="$(printf '%s\n' "$filled" | "$here/defect-record.sh" "fix: x")"; then
  echo "::error::defect-record.sh: the PR template filled in should pass:"; printf '%s\n' "$out"; fail=1
fi
[ "$fail" -eq 0 ] && echo "defect-record.sh: ${#cases[@]} cases pass"
exit "$fail"
