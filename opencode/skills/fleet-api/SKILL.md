---
name: fleet-api
description: Drive Weave Fleet itself through its REST API. List, start, prompt and fork Fleet sessions, start them from GitHub issues and pull requests, and manage automations, projects and boards. Use when the user asks for something done in Fleet, not in the code.
---

# Fleet API

Fleet started this session, and its API is at `$FLEET_URL` (for example `http://127.0.0.1:6262`). Requests from this
machine need no token. If `$FLEET_URL` is empty, Fleet didn't start this process: ask the user for Fleet's address.

Fleet sessions are Fleet's, not the harness's: use the ids the API returns, not OpenCode session ids.

## Look it up, don't guess

The OpenAPI document lists every route and request body, and it's always current:

```bash
curl -s "$FLEET_URL/openapi/v1.json" | jq '.paths | keys'
curl -s "$FLEET_URL/openapi/v1.json" | jq '.components.schemas.CreateSessionApiRequest'
```

Without `jq`, save the document to a file and search it. Several request bodies reject unknown fields with a 400, so read the schema before you send one. Errors come back as
JSON with an `error` or `detail` field that says what was wrong.

## Sessions

| Method | Path | What it does |
|--------|------|--------------|
| GET | `/api/sessions` | List sessions. Query: `limit`, `offset`, `status`, `retentionStatus`, `projectId`, `tags` (comma-separated) |
| POST | `/api/sessions` | Start a session |
| GET | `/api/sessions/{id}` | One session, with its folder, branch and `activityStatus` |
| GET | `/api/sessions/{id}/status` | Whether it's working or idle |
| GET | `/api/sessions/{id}/messages` | Its conversation. Query: `limit`, `before` |
| GET | `/api/sessions/{id}/diffs` | The changes in its folder |
| POST | `/api/sessions/{id}/prompt` | Send it a message: `{"text": "…"}`. An idle session wakes up. If you have the `fleet_message` tool, use it instead: Fleet refuses this from agents then |
| POST | `/api/sessions/{id}/abort` | Stop the turn it's working on |
| POST | `/api/sessions/{id}/fork` | Copy it into a new session: `{"title": "…"}` |
| POST | `/api/sessions/{id}/sources` | Add a GitHub issue or pull request to it as context |
| PATCH | `/api/sessions/{id}` | Rename it: `{"title": "…"}` |
| PATCH | `/api/sessions/{id}/tags` | Replace its tags: `{"tags": ["…"]}` |
| PATCH | `/api/sessions/{id}/project` | Move it to a project |
| DELETE | `/api/sessions/{id}` | Delete it |

### Start a session in a folder

```bash
curl -s -X POST "$FLEET_URL/api/sessions" -H 'content-type: application/json' -d '{
  "directory": "/path/to/repo",
  "title": "Fix the flaky login test",
  "isolationStrategy": "worktree",
  "initialPrompt": "The login test fails about one run in ten. Find out why and fix it."
}'
```

`isolationStrategy` is `existing` (work in the folder as it is), `worktree` (a new git worktree) or `clone`.
`branch`, `projectId` and `tags` are optional. If you have the `fleet_message` tool, leave out `initialPrompt`
(Fleet refuses it from agents then) and give the new session its task with the tool.

### Start a session from a GitHub issue or pull request

The source brings the issue or pull request in as context. Use `github-issue` or `github-pull-request`:

```bash
curl -s -X POST "$FLEET_URL/api/sessions" -H 'content-type: application/json' -d '{
  "title": "Review PR #42",
  "initialPrompt": "Review this pull request.",
  "source": {
    "key": { "providerId": "builtin.github", "sourceType": "github-pull-request", "actionId": "start-session", "contractVersion": 1 },
    "input": { "owner": "myorg", "repo": "myrepo", "number": 42, "repositoryPath": "/path/to/myrepo", "isolationStrategy": "worktree" }
  }
}'
```

`repositoryPath` is the local clone. `GET /api/session-sources/catalog` lists every source and the input it takes.

### Add an issue or pull request to a running session

```bash
curl -s -X POST "$FLEET_URL/api/sessions/$ID/sources" -H 'content-type: application/json' -d '{
  "source": {
    "key": { "providerId": "builtin.github", "sourceType": "github-issue", "actionId": "add-to-session", "contractVersion": 1 },
    "input": { "owner": "myorg", "repo": "myrepo", "number": 17 }
  },
  "confirm": true
}'
```

## Automations

An automation sends a prompt on a schedule, once, or when something happens in Fleet.

| Method | Path | What it does |
|--------|------|--------------|
| GET | `/api/automations` | List them, with when each runs next and its last run |
| POST | `/api/automations` | Create one |
| GET / PUT / DELETE | `/api/automations/{id}` | Read, replace or delete one |
| POST | `/api/automations/{id}/enable`, `/disable` | Turn it on or off |
| POST | `/api/automations/{id}/run` | Run it now |
| GET | `/api/automations/{id}/runs` | Its runs, newest first |
| GET | `/api/automations/event-catalog` | The events it can wait for |

```bash
curl -s -X POST "$FLEET_URL/api/automations" -H 'content-type: application/json' -d '{
  "name": "Morning dependency check",
  "prompt": "Check for outdated dependencies and open a PR for safe updates.",
  "triggerType": "schedule",
  "triggerConfig": "0 9 * * 1-5",
  "timeZone": "Europe/London",
  "workspaceId": "/path/to/repo",
  "isolation": "worktree"
}'
```

- `triggerType` `schedule` takes a cron expression, `once` takes a local date and time like `2026-09-21T09:00`, and
  `event` takes JSON as a string: `"{\"eventType\":\"session_created\"}"`.
- `timeZone` is an IANA name. Without one, times are UTC.
- `workspaceId` is the folder a run works in. `isolation` is `worktree` (a new worktree per run, `baseBranch` optional)
  or `existing`.
- `targetType` is `new_session` (the default), `same_session`, `most_recent_session` or `tagged_session` with
  `targetTags`.

## Everything else

These are in the OpenAPI document too:

- Projects: `/api/projects`
- Boards, with their lanes, cards and GitHub sources: `/api/boards`
- GitHub: `/api/integrations/github/repos/{owner}/{repo}/issues` and `/pulls`, with `/{number}` and `/comments`
- Folders Fleet can use: `/api/workspace-roots` and `/api/repositories`
- Tokens and cost: `/api/fleet/summary` and `/api/analytics/*`

## Before you change things

Starting a session can create a worktree or a clone, and a prompt makes an agent act. Automations keep running after
this conversation ends. Say what you're about to create, change or delete, unless the user already asked for exactly
that.
