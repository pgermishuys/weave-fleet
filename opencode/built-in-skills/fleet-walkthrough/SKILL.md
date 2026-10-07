---
name: fleet-walkthrough
description: Walk the user through a change as a guided tour, the uncommitted work, a branch or a pull request. Groups related edits into a few chapters, puts them in the order that makes the change make sense, and explains each one beside its code in a page beside the chat. Explains, doesn't review. Use when the user asks to walk through, explain or understand a diff, a branch or a PR, or wants help reading or reviewing a large change.
---

# Walk through a change

A diff lists files in alphabetical order, which is rarely the order that explains anything. Turn it into a short tour
the user can follow from top to bottom, and understand the change by the end.

This explains the change. It doesn't judge it: for bugs, use fleet-code-review.

## 1. Find the change

| The user said | Walk through |
|---|---|
| nothing specific | this session's changes: the branch against where it left main, uncommitted work included, as the Changes tab shows them |
| a branch | `from`: the branch it goes into (usually `main`), with that branch checked out here |
| a pull request (`#123`, a URL) | `gh pr view 123 --json baseRefName,headRefOid`, then `git fetch origin <headRefOid>`: `from` is `origin/<baseRefName>`, `to` is the commit |
| a commit or a range | `from` and `to` as commits |

```bash
base=$(git merge-base HEAD "$(git symbolic-ref --short refs/remotes/origin/HEAD 2>/dev/null || echo main)")
git diff "$base" --stat
git diff "$base"
git status --short   # files not tracked yet are part of the change too
```

Read what it's for (the PR description, commit messages, a plan in `.weave/plans/`), and read around the hunks, not
only the hunks: a chapter is only clear if you know what the code did before.

## 2. Group into chapters

A chapter is one idea, which can span several files: "the new column and its migration", "the endpoint that reads it",
"the UI that shows it". Aim for three to eight. Leave the noise out of the chapters (lock files, generated code,
screenshots, renames with no other change, formatting): list it once under `alsoChanged`.

## 3. Order them

Put each chapter after the ones it depends on, so nothing is used before it's explained. Lead with the change's core:
usually data and types, then the logic that uses them, then the edges (API, UI, CLI), then tests. When the change fixes
a bug, start with the chapter that holds the fix.

## 4. Write each chapter, and check it

For each chapter: a title that says what it does ("The endpoint reads the new column"), then a few short paragraphs in
plain words. What changed, and why it's here: how it uses the chapter before, and what the next one needs from it.
Name functions and values as the code spells them, in `backticks`. Give the one place to start reading as `path:line`.

Every sentence must be true of the code. Before you show anything, open each place a chapter talks about and check it
says what you wrote: who calls whom, what happens first, which branch is taken and when, the names and values. Cut what
you can't confirm.

When something deserves a second look, say it as a question, not a verdict ("Every refresh now runs three more git
commands: is that fine on Windows?"). Leave it out if there's nothing.

## 5. Show it

With `fleet_walkthrough_show`, send the outline. Fleet puts each file's real hunks beside its chapter, so never copy
code into it:

- `title`: what the change does.
- `summary`: two or three sentences, what and why. `steps`: the three to five steps the change takes, each pointing at
  the chapter that explains it.
- `chapters`, in order, each with its `files` named as git does, from the repository's root. Mark a file
  `collapsed` when the chapter is only a pointer to it (tests, a long fixture).
- `alsoChanged`: the noise, with a word on what it is. Patterns like `docs/screenshots/*.png` work.
- `from` and `to` only for a branch, pull request or range (step 1); left out, it's the session's changes.
- `diagram`, only when the change alters how things flow or call each other: rows of nodes joined by arrows, before
  and after, new parts marked `"kind": "new"`, each node naming the chapter that explains it. A node or arrow is a
  claim too: draw only what the code does.

Fleet answers with any file it can't find in the change, and the changed files no chapter covers. Fix the first; for the
second, add a chapter or put them in `alsoChanged`, unless leaving them out was the point. Calling it again with the
same title updates the user's tab.

Then say in the chat, in two or three sentences, what the walkthrough covers, and mention any question you raised. The
user can mark chapters reviewed, and "Ask about this" on a chapter puts a question about it in their composer.

Without `fleet_walkthrough_show` (a harness that has no Fleet tools), write the tour in the chat instead: the summary,
then each chapter as a heading with its `path:line` and paragraphs, and finally what else changed.

## Not this skill

- Finding bugs in the change: fleet-code-review. A walkthrough can come first, to understand it.
- Tidying the change: fleet-simplify.
- How one flow works over time, as an animation: fleet-explain.
