---
name: fleet-catch-up
description: Catch the user up on where the work in this folder stands after time away. Reads the branch, its pull request, recent commits and uncommitted changes, then says in a few sentences what they were doing and what to do next. Use when the user asks where they were, what's going on here, or to catch them up.
---

# Catch me up

The user is back after time away and wants their bearings: where they were, and what to do next. A teammate
catching them up, not a status report.

## 1. Look, quietly

Check the state yourself, and don't narrate the commands:

```bash
git status --short --branch
git log --oneline -15
base=$(git merge-base HEAD "$(git symbolic-ref --short refs/remotes/origin/HEAD 2>/dev/null || echo main)")
git diff --stat
```

Then build the picture in layers:

- **On a feature branch:** what the branch is for. Read its commits since the default branch
  (`git log -p "$base"..HEAD`), going back only until the intent is clear. Look for its own pull request
  (`gh pr view --json title,body,state,reviewDecision,statusCheckRollup,comments` if `gh` is there): the description,
  review comments and failing checks say what the current work is for. Note if the remote has commits you don't.
- **On the default branch:** skim the last few commits, to see whether the uncommitted work continues one of them.
- **Uncommitted changes:** read the diff, through the layers above. Is it finishing the feature, answering review, or
  something new? What looks done, and where did it stop?
- `.weave/plans/` or a plan in the repository that names this work: which steps are done.

## 2. Tell them

Lead with the work, not the git state: "You were in the middle of X..." when there are uncommitted changes, otherwise
what the branch or recent commits were doing.

Then one clear next step, about the work itself: "Next is wiring X into Y and handling Z." They came back because it
isn't finished, so don't suggest pushing, opening a PR or running checks unless there's truly nothing left to build,
or that really is the most useful thing.

## Rules

- Only this branch and its own work. Leave out other branches, other people's pull requests and review requests.
- Use commit counts and ahead/behind to understand, not to report. Mention them only if they matter ("someone pushed
  to this branch since").
- A few sentences and a next step. The depth goes into what you read, not into the length of the answer.
- Don't change anything. This is reading only.
