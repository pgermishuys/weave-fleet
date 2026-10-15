---
name: fleet
description: Weave Fleet runs this session. Read before acting when the user lists two or more separate tasks, issues or PRs (Fleet runs them in parallel worktree sessions), wants something on a schedule or later, a repeatable process, says "remember" or "from now on" (Fleet has its own memory, check it first), or asks what you or Fleet can do or about Fleet's settings.
---

# Fleet

Fleet started this session. Its API is at `$FLEET_URL` (for example `http://127.0.0.1:6262`); requests from this
machine need no token. If `$FLEET_URL` is empty, Fleet didn't start this process: say so and don't guess an address.
Fleet's sessions have Fleet's ids, not the harness's.

## When to bring Fleet up

Only when the request fits one of these. For anything else, do the work and don't mention Fleet.

| The user wants | Fleet can | Read |
|---|---|---|
| Several independent tasks done at once, or a risky change kept away from their checkout | Start one session per task, each in its own git worktree | `sessions.md` |
| Something every day or week, at a time, or when an event happens | Create an automation | `automations.md` |
| A process they'll run again: bump, changelog, test, tag | Run or draft a workflow | `workflows.md` |
| You to remember something across sessions | Save a note, if Memory is on | `memory.md` |
| To know what Fleet or you can do, or how a setting is | Read its settings and say what's on | `settings.md` |

Read only the file you need, after you know the request fits. Read it before you start the work. Two or more
separate tasks in one request: offer a session each in its own worktree and wait for the answer, however small the
tasks are, and don't start any of them first. Offering costs one sentence; the user can answer "just do them here".

## How to behave

1. **Suggest, then ask.** Say what you'd create (how many sessions, the schedule, the workflow) and wait for a yes.
   Starting a session can create a worktree or a clone, an automation keeps running after this conversation, and
   a prompt makes an agent act. Don't do any of it unasked, unless the user asked for exactly that.
2. **Check what's switched on first.** Some features are off until the user turns them on (Workflows, Memory, Mods).
   Check through the API before you offer one. If it's off, say so and where to turn it on; don't offer what the user
   can't use.
3. **Read settings, never change them.** Switching a feature on is the user's decision. Tell them the place in
   Settings and stop. This includes the preferences API.
4. **Don't do it twice.** If the user says no to Fleet's way, do it the plain way and don't ask again.
5. **Stay short.** One or two sentences to offer, not a tour. Don't list Fleet's features when asked for something else.

## Look it up, don't guess

The OpenAPI document lists every route:

```bash
curl -s "$FLEET_URL/openapi/v1.json" | jq '.paths | keys'
```

If it fails (some installed builds answer 500), use the request bodies in the reference files. Several bodies reject
unknown fields with a 400. Errors come back as JSON with an `error` or `detail` field that names what was wrong. Send
JSON as UTF-8 with `Content-Type: application/json`.

Other places in the API, when the user asks for them:

- Projects: `/api/projects`. Boards, with lanes and cards: `/api/boards`.
- GitHub: `/api/integrations/github/repos/{owner}/{repo}/issues` and `/pulls`.
- Folders Fleet can use: `/api/workspace-roots` and `/api/repositories`. Other machines: `/api/machines`.
- Tokens and cost: `/api/fleet/summary` and `/api/analytics/*`.
