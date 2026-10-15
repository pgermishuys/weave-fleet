# Workflows

Offer one when the user describes a process they'll run again, with steps in order (bump, changelog, test, tag,
release notes). A workflow is a short list of steps; each agent step is an ordinary Fleet session, and Fleet moves
the run from step to step and stops wherever a step asks the user.

**Check that Workflows is on first**: `GET /api/workflows` answers 404 "turned off in Fleet's Settings" when it's not.
Then say so and point at Settings → Workflows. Don't turn it on.

Look at what exists before you offer a new one:

```bash
curl -s "$FLEET_URL/api/workflows?directory=/path/to/repo"
```

It lists Fleet's built-in workflows (`builtin:…`, such as Build a feature and Fix a bug), then the repository's own
(`repo:…`, files in `.weave/workflows/*.yaml`). If a built-in already does what the user wants, offer to run it and
don't make a copy.

| Method | Path | What it does |
|--------|------|--------------|
| POST | `/api/workflows/runs` | Start a run: `{"workflowId": "builtin:fix-a-bug", "directory": "/path/to/repo", "request": "…"}`. Optional: `baseBranch`, `harnessType`, `checkWithMe` |
| GET | `/api/workflows/runs?workflowId=…` | Recent runs; `/runs/{id}` is one |
| POST | `/api/workflows/drafts/from-description` | Draft a workflow from a sentence: `{"directory": "…", "description": "…"}`. Nothing is saved |
| POST | `/api/workflows/drafts/from-session` | Draft one from a finished session: `{"sessionId": "…"}` |
| POST | `/api/workflows/files` | Save a workflow into the repository's `.weave/workflows`: `{"directory": "…", "name": "…"}`, or with the drafted `draft` |

To make a new workflow, offer to draft it from the user's description, tell them where it would be saved (in the
repository, so their team gets it with the code), and wait for a yes before saving. They finish it in Fleet's
Workflows page. Starting a run makes sessions that change code: ask first.
