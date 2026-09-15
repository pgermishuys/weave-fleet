---
name: fleet-simplify
description: Review the code changed in this session or on this branch for reuse, simplification and efficiency, then apply the fixes. Quality only, it doesn't hunt for bugs (fleet-code-review does). Use when the user asks to simplify, tidy or clean up a change.
---

# Simplify a change

Make the change smaller and plainer without changing what it does.

## 1. Find the change

Use what the user named: files, a commit, a range. Otherwise take everything this branch changes:

```bash
base=$(git merge-base HEAD "$(git symbolic-ref --short refs/remotes/origin/HEAD 2>/dev/null || echo main)")
git diff "$base" --stat
git diff "$base"
```

That includes uncommitted work. If the branch *is* the default branch, use `git diff HEAD` for uncommitted work only.

Read each changed file around the hunks, not only the hunks: most simplifications come from code that was already there.

## 2. Look for

**Reuse.** New code that does what existing code already does. Search the repository for the behaviour, not only the
name: a date formatter, a retry loop, a path helper, a component. Use the existing one.

**Simpler code.**
- Abstractions with one caller, interfaces with one implementation, parameters and options nobody passes
- Defensive checks for states that can't happen, and fallbacks that are never used
- Copies of the same block that one loop or helper would replace
- Nesting that an early return flattens
- Dead code, commented-out code and debugging output left behind
- Comments that narrate the change ("added this to fix…") instead of explaining the code

**Efficiency**, only where it matters: work repeated inside loops (a query or file read per item), data loaded and
thrown away, growth with no bound, blocking calls on a hot path.

**The right place.** A fix patched in at one call site when the value is produced wrong somewhere else, or logic in a
UI component that belongs with the data. Move it to where it belongs if that is small; otherwise mention it.

**The surrounding style.** New code should read like its neighbours: naming, error handling, comment density, idiom.

## 3. Don't

- Change behaviour. If a simplification would, it's not a simplification: mention it instead.
- Reformat or rename code the change didn't touch, or rename public APIs.
- Mix in unrelated refactors because you saw them.
- Replace clear code with clever code. Fewer lines is not the goal; less to understand is.

## 4. Apply, check, report

Make the edits. Then run the tests, type checks and linters that cover the changed files, and fix what you broke.

Report briefly:
- each change, one line with why ("used `formatBytes` from `lib/format.ts` instead of a second copy")
- what you considered and left, and why, if the user would otherwise wonder
- what you ran and the result. If something couldn't be run, say so.

If the change is already simple, say that. Don't invent changes.
