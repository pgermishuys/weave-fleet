# Sessions

Offer this when the user has several independent tasks (issues, files, packages) or wants a change kept in its own
worktree. Offer one session per task, say what each will do, and ask. Don't start a session for work you can do in a
few lines yourself.

If you have the `fleet_message` tool, start each session without an `initialPrompt` and give it its task with the
tool: Fleet refuses `initialPrompt` from agents then.

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
| POST | `/api/sessions/{id}/fork` | A new session with a copy of its conversation up to the last finished turn, in the same folder: `{"title": "…"}`. 400 on a harness that can't fork (Claude Code, Pi) |
| POST | `/api/sessions/{id}/new-in-folder` | A new, empty session in its folder, on the same harness and profile |
| POST | `/api/sessions/{id}/sources` | Add a GitHub issue or pull request to it as context |
| GET | `/api/sessions/{id}/work` | The work its agent left running (subagents, background shells) and what just finished. `all=true` for everything |
| POST | `/api/sessions/{id}/work/{workId}/stop` | Stop one piece of it (`workId` is the item's `id`), when its `canStop` is true |
| GET | `/api/sessions/{id}/work/{workId}/output` | A page of its output from byte `offset`, when its `canReadOutput` is true |
| PATCH | `/api/sessions/{id}` | Rename it: `{"title": "…"}` |
| PATCH | `/api/sessions/{id}/tags` | Replace its tags: `{"tags": ["…"]}` |
| PATCH | `/api/sessions/{id}/project` | Move it to a project |
| DELETE | `/api/sessions/{id}` | Delete it |

## Start a session in a folder

```bash
curl -s -X POST "$FLEET_URL/api/sessions" -H 'content-type: application/json' -H "X-Fleet-Harness-Session: $FLEET_HARNESS_SESSION_ID" -d '{
  "directory": "/path/to/repo",
  "title": "Fix the flaky login test",
  "isolationStrategy": "worktree",
  "initialPrompt": "The login test fails about one run in ten. Find out why and fix it."
}'
```

`isolationStrategy` is `existing` (work in the folder as it is), `worktree` (a new git worktree) or `clone`.
The `X-Fleet-Harness-Session` header tells Fleet this session started the new one; keep it.
`branch`, `projectId` and `tags` are optional; without `projectId` the new session goes in your session's project.
If you have the `fleet_message` tool, leave out `initialPrompt` (Fleet refuses it from agents then) and give the new
session its task with the tool.
Sessions started this way can start their own, three deep at most: past that Fleet answers 409 and says why.

## Start a session from a GitHub issue or pull request

The source brings the issue or pull request in as context. Use `github-issue` or `github-pull-request`:

```bash
curl -s -X POST "$FLEET_URL/api/sessions" -H 'content-type: application/json' -H "X-Fleet-Harness-Session: $FLEET_HARNESS_SESSION_ID" -d '{
  "title": "Review PR #42",
  "initialPrompt": "Review this pull request.",
  "source": {
    "key": { "providerId": "builtin.github", "sourceType": "github-pull-request", "actionId": "start-session", "contractVersion": 1 },
    "input": { "owner": "myorg", "repo": "myrepo", "number": 42, "repositoryPath": "/path/to/myrepo", "isolationStrategy": "worktree" }
  }
}'
```

`repositoryPath` is the local clone. `GET /api/session-sources/catalog` lists every source and the input it takes.

## Add an issue or pull request to a running session

```bash
curl -s -X POST "$FLEET_URL/api/sessions/$ID/sources" -H 'content-type: application/json' -d '{
  "source": {
    "key": { "providerId": "builtin.github", "sourceType": "github-issue", "actionId": "add-to-session", "contractVersion": 1 },
    "input": { "owner": "myorg", "repo": "myrepo", "number": 17 }
  },
  "confirm": true
}'
```
