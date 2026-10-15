# Automations

Offer one when the user wants something to happen on a schedule, once later, or when something happens in Fleet
("every weekday morning…", "tomorrow at 9…"). Offer it before you write a cron job or a GitHub Action. Show the user
what you'd create and ask: its name, the schedule in plain words, the folder, the prompt each run gets, and whether
runs get a worktree.

An automation sends a prompt on a schedule, once, or on an event, and keeps running after this conversation ends.

| Method | Path | What it does |
|--------|------|--------------|
| GET | `/api/automations` | List them, with when each runs next and its last run. Check it first: don't create a copy of one that exists |
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
- `timeZone` is an IANA name. Without one, times are UTC. Ask the user for theirs if you don't know it.
- `workspaceId` is the folder a run works in. `isolation` is `worktree` (a new worktree per run, `baseBranch`
  optional) or `existing`.
- `targetType` is `new_session` (the default), `same_session`, `most_recent_session` or `tagged_session` with
  `targetTags`. `maxRunsPerHour` (10) and `timeoutMinutes` (30) bound a run.
- `targetMachineId` runs it on another machine (`GET /api/machines`). Leave it out for this machine.
- To run a workflow instead of a prompt, `targetType` is `workflow` with `workflowId` (see `workflows.md`).
