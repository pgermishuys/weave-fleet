---
name: fleet-run
description: Run the project's app to see a change working in the real app, not just in its tests. Covers web apps, servers, CLIs, libraries and terminal apps. Use when asked to run, start or try the app, to confirm a fix works, or after a change the user will run, before saying it's done.
---

# Run the app and check the change

Tests show the code does what the tests say. Running the app shows it does what the user asked. Do both.

## 1. Find how this project runs

Look before guessing, and use what you find:

- a project skill or doc about running the app, `AGENTS.md`, `CLAUDE.md`, the README's development section
- `package.json` scripts (`dev`, `start`), `Makefile`, `justfile`, `Taskfile.yml`, `Procfile`
- `docker-compose.yml`, `.NET` `launchSettings.json`, `Cargo.toml` binaries, `pyproject.toml` scripts

If the app needs secrets, a service or data you don't have, say so. Don't fake them, and never point a run at the
user's real data, production services or accounts. Use test data or a scratch database.

## 2. Run it, by kind of project

### Web app or HTTP server

Use **`fleet_app_start`** with the command that serves it, for example `npm run dev` or
`dotnet run --project src/Api -- --urls http://localhost:$PORT`. Fleet sets `PORT`, waits for the page, shows it to the user in a browser canvas and
tells you the address. If the app doesn't come up, it gives you the last lines of output. Don't start servers from the
shell in the background: they never exit, and the user can't see them.

Then check what you changed, not just that the page loads:

- request the endpoint or page you changed: `curl -si http://localhost:5173/api/items?page=2`
- `fleet_canvas_read` on the app's canvas shows its status and recent output, including errors and stack traces
- after more changes, call `fleet_app_start` again with the same command to restart it, unless the dev server
  reloads by itself

Without `fleet_app_start` (outside Fleet): start the server in the background with its output going to a file in a
temp directory, poll until the port answers (give up after a few minutes), check, then stop it.

### Command-line tool

Build it and run it with the inputs your change affects. Include one failure: a bad argument, a missing file. Check
the exit code as well as the output.

### Library

Write a short script in a temp directory, outside the repository, that uses the public API the way a caller would
and exercises the change. Run it. Don't leave it in the repository.

### Terminal UI

Run it in a pseudo-terminal if you can (`tmux new -d` then `tmux send-keys` and `tmux capture-pane -p`, or `script`),
and read the screen. Otherwise run its non-interactive modes, and say what you couldn't check.

### Desktop app

If its window loads a dev server, start that with `fleet_app_start` and check the page. Otherwise start it, read its
logs, and say what you couldn't see.

### Generated output

Docs, static sites, reports, code generators: generate the output and read it.

## 3. Be a good neighbour

- Scratch files go in a temp directory, not the repository.
- Other sessions may be using this machine. Before a heavy build or full test suite, check free memory, and prefer
  the tests that cover your change.
- Stop processes you started from the shell. Apps started with `fleet_app_start` can stay running: the user can see
  and stop them.

## 4. Report what you saw

Say what you ran, what you checked and what happened: "Started `npm run dev`, requested `/api/items?page=2`: 200 with
20 items; page 3 is empty as expected."

If you couldn't run it, say why, and give the user the command to run. Never report that something works when you
didn't run it.
