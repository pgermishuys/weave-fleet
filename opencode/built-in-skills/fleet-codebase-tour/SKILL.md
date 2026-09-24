---
name: fleet-codebase-tour
description: Give a high-level tour of a codebase, or one area of it, as if onboarding a new contributor. Reads the code, then explains the big picture, the main parts, how a request flows through them, the conventions, and where to start. Use when the user is new to a repository, asks how it's organised, or asks for a tour or overview.
---

# A tour of the codebase

Explain the codebase the way a good colleague would on someone's first day: the shape of it first, then enough detail
to find their way. Read the code; don't guess from file names.

## 1. Read

If the user named an area, tour that, with just enough of the rest to place it.

- `README`, `AGENTS.md`, `CLAUDE.md`, `CONTRIBUTING.md` and a `docs/` folder, if there is one
- the build and run setup: `package.json`, solution and project files, `Makefile`, `Dockerfile`, CI workflows
- the top-level folders, and what each one's entry point does
- one real path end to end: a request, a command or a user action, from where it enters to where it's stored or
  shown

Read enough actual code in each main part to describe it accurately. The docs can be out of date; the code isn't.

## 2. Explain

In this order, and short:

1. **What it is:** the project in two or three sentences, and the main technologies.
2. **The main parts:** each folder or package that matters, what it's responsible for, one line each. A small tree
   helps:
   ```
   src/Api/          HTTP endpoints and the SignalR hub
   src/Application/  the use cases; no I/O of its own
   client/           the Vue app
   ```
3. **How it fits together:** the path you traced, step by step, naming the file at each step. A small diagram helps
   when there are more than three hops.
4. **Worth knowing:** conventions and traps: where shared types and config live, naming, how errors are handled, what
   is generated, the command that runs the tests, anything that surprised you.
5. **Where to start:** three or four concrete pointers: the file to read first, a small first change, the tests to
   run.

Link files as `path/to/file.ts:42` so they can be opened. Favour the mental model over completeness: a newcomer can't
use a list of every file.
