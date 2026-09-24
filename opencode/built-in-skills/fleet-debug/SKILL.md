---
name: fleet-debug
description: Find the root cause of a bug before changing any code. Pins down the symptom, lists the likely causes, checks each against the code and a reproduction, and proposes the smallest fix with a test that fails first. Use when the user reports a bug, an error, a failing or flaky test, or asks why something happens.
---

# Find the cause, then fix it

A fix for the wrong cause hides the bug and adds a second one. Find the cause first, show the evidence, and only
then change code.

## 1. Pin down the symptom

Write down, in a sentence or two:

- what happens, and what should happen instead
- the exact error, stack trace or wrong output, if there is one
- when it happens: always, sometimes, since a change, on one platform

If the user hasn't given enough to start, ask for the one or two things you need (steps, a log, a version), not a
questionnaire. If you have a tool for asking the user questions with options, use it only for concrete choices
("Does it happen on (a) a new session or (b) one you reopened?"); ask for logs and steps in plain text.

## 2. Reproduce it

A bug you can trigger is a bug you can prove fixed. In order of preference:

1. a failing test, next to the tests that already cover that code
2. a command, request or short script that shows it
3. the steps in the app

Run it and see it fail. If you can't reproduce it, say so and say what you tried, then carry on from the code, and
treat everything after this as less certain.

## 3. List the likely causes

Before reading deeply, write two to four candidates, most likely first, each with why the symptom points at it.
Include the dull ones: stale data, a missing `await`, an unhandled empty case, a config value, a changed dependency.
`git log -p` on the files involved often shows the change that started it.

## 4. Check each one

Read the code the failing path runs, from the entry point to the wrong result. For each candidate, find the line
that makes it true or rules it out. Where reading isn't enough, measure: a log line, a debugger, a smaller input.
Remove temporary logging when you're done.

Stop when one candidate explains everything you saw, including the parts the others can't ("it only fails after a
reconnect because...").

## 5. Report the cause

Before editing, tell the user:

```
Cause: <one sentence>, at path/to/file.ts:42.
Evidence: <what you ran or traced that shows it>.
Why the symptom looks like this: <the chain from the cause to what they saw>.
Fix: <the smallest change that removes the cause>.
Test: <the test that fails now and will pass after>.
```

Separate the cause from its symptoms: a null check where the error surfaces is not a fix if the value should never
have been null.

## 6. Fix it, when they agree

Unless the user already asked you to fix it, wait for a go-ahead. Then:

1. write the test first and watch it fail for the reason you found
2. make the smallest change that makes it pass
3. run the tests around it, not only the new one
4. check for the same mistake elsewhere: the same pattern at other call sites is usually the same bug

Say what you changed, what you ran, and anything you saw but left alone.
