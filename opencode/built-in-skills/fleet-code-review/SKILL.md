---
name: fleet-code-review
description: Review a change for bugs, the current diff, a branch or a pull request. Checks each finding before reporting it and ranks them by severity. Fixes the findings or posts them on the pull request only when asked. Use when the user asks for a code review, or to review changes, a branch or a PR.
---

# Review a change for bugs

Find the problems that would hurt someone: wrong results, crashes, data loss, security holes. Report only what you
can back up.

## 1. Find the change and what it's for

| The user said | Review |
|---|---|
| nothing specific | this branch against its base, plus uncommitted work (below) |
| a pull request (`#123`, a URL) | `gh pr diff 123` and `gh pr view 123 --comments` |
| a branch or a range | `git diff <base>...<branch>` |

```bash
base=$(git merge-base HEAD "$(git symbolic-ref --short refs/remotes/origin/HEAD 2>/dev/null || echo main)")
git diff "$base"
git log --oneline "$base"..HEAD
```

Work out what the change is meant to do, from the PR description, commit messages, a linked issue, or this
session's task. A bug is the code not doing what it's meant to.

**How deep.** If the user said "quick", report only findings you're sure of and don't dig further. By default, be
thorough. For "deep" or a large change, split the work as described in step 4.

## 2. Read around the diff

For each changed function, find its callers and the tests that cover it. Many bugs are in what the diff *doesn't*
touch: a caller that still passes the old argument, a second code path that needed the same fix.

## 3. Hunt

- **Logic:** inverted conditions, off-by-one, wrong operator, wrong variable, a case the switch doesn't handle
- **Missing and empty:** null, empty lists, missing keys, zero, the first run, a file that isn't there
- **Errors:** errors swallowed or logged and ignored, a failure halfway through that leaves state inconsistent, retries
  that repeat side effects
- **Concurrency:** races, missing `await`, shared state changed without a lock, work that outlives its owner
- **Resources:** handles, processes, timers and subscriptions that are never released
- **Security:** input reaching a shell, SQL or file path without checks, missing authorization, secrets in logs or
  responses
- **Contracts:** changed signatures, response shapes, config keys, database columns, anything another caller, client
  or older version still depends on
- **Data:** migrations that lose data or can't run twice, defaults that change existing behaviour
- **Platforms:** paths, line endings, shells and case sensitivity on the other operating systems the project supports
- **Tests:** tests that pass without testing the change, or that assert the bug

## 4. Verify every finding

For each candidate, find the concrete input or state that triggers it, and trace the code path from there to the
wrong result. When it's cheap, prove it: a small failing test, a one-off script, a request against the running app.

- **Confirmed:** you reproduced it, or traced it end to end.
- **Plausible:** you couldn't rule it out, and it matters enough to mention.
- Anything else: drop it. A vague "might be an issue" costs the reader more than it saves.

**Large changes.** If you can start sub-agents, give each one part of the diff, or one concern from the list above,
and ask for candidates with their failure scenario. Verify what they send back yourself: sub-agents over-report.

## 5. Report

Most severe first. For each finding:

```
path/to/file.ts:42, confirmed
What's wrong, in one sentence.
When: the input or state that triggers it → the wrong result.
Fix: the change that would fix it.
```

If nothing survives verification, say so, and say what you checked. Leave style out: at most a short list of cleanups
at the end, or point to fleet-simplify.

## Only when asked

**Fix the findings.** Fix them one at a time, run the tests, and report each one as fixed, skipped (and why) or not
needing a change.

**Post to the pull request.** Posting is visible to everyone with access to the repository: post what the user asked
for, nothing more. A summary comment:

```bash
gh pr review 123 --comment --body-file review.md
```

Inline comments need the head commit and a line in the diff:

```bash
gh api repos/{owner}/{repo}/pulls/123/comments \
  -f commit_id="$(gh pr view 123 --json headRefOid -q .headRefOid)" \
  -f path=src/file.ts -F line=42 -f side=RIGHT -f body="…"
```
