---
name: fleet-walkthrough
description: Walk the user through a change as a guided tour, the uncommitted work, a branch or a pull request. Groups related edits into a few stops, puts them in the order that makes the change make sense, and explains each one and how they connect. Explains, doesn't review. Use when the user asks to walk through, explain or understand a diff, a branch or a PR, or wants help reading a large change.
---

# Walk through a change

A diff lists files in alphabetical order, which is rarely the order that explains anything. Turn it into a short tour
the user can follow from top to bottom, and understand the change by the end.

This explains the change. It doesn't judge it: for bugs, use fleet-code-review.

## 1. Find the change

| The user said | Walk through |
|---|---|
| nothing specific | uncommitted work if there is any, otherwise this branch against its base |
| a pull request (`#123`, a URL) | `gh pr diff 123`, with `gh pr view 123` for what it's for |
| a branch, a commit or a range | `git diff <base>...<branch>`, `git show <commit>` |

```bash
base=$(git merge-base HEAD "$(git symbolic-ref --short refs/remotes/origin/HEAD 2>/dev/null || echo main)")
git diff "$base" --stat
git diff "$base"
```

Read what it's for (the PR description, commit messages, a plan in `.weave/plans/`), and read around the hunks, not
only the hunks: a stop is only clear if you know what the code did before.

## 2. Group into stops

A stop is one idea, which can span several files: "the new column and its migration", "the endpoint that reads it",
"the UI that shows it". Aim for three to eight stops. Leave the noise out of the stops (lock files, generated code,
renames with no other change, formatting) and list it once at the end.

## 3. Order them

Put each stop after the ones it depends on, so nothing is used before it's explained. Usually: data and types, then
the logic that uses them, then the edges (API, UI, CLI), then tests. When the change fixes a bug, start with the stop
that holds the fix.

## 4. Write the tour

Open with two or three sentences: what the change does, and why.

Then each stop:

```markdown
### 2. The endpoint reads the new column
`src/Api/Endpoints/SessionEndpoints.cs:118`, `src/Application/Sessions/SessionQuery.cs:40`

What changed, in plain words. Why it's here: how it uses stop 1, and what stop 3 needs from it.
```

Quote a few lines of code only when they're the point of the stop; don't reproduce the diff. Link lines as
`path:line` so they can be opened.

End with:

- **How it fits:** one short paragraph, or a small diagram when the pieces talk to each other in a way words make
  hard to follow.
- **Also changed:** the noise you left out of the stops, one line.
- **Worth a closer look:** anything you noticed that the user may want to question, stated as a question, not a
  verdict. Leave it out if there's nothing.

Keep it readable in a few minutes. For a very large change, tour the main stops and say which parts you summarised.
