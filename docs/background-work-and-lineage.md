# Background work and lineage

Work an agent leaves running, and the sessions a session starts, are hard to find today. Background shells and
subagents only show as tool cards somewhere up the conversation. Child sessions are hidden from the session list.
This plan makes both visible for every harness.

The idea came from T3 Code (`/home/pgermishuys/source/t3code`, MIT). Its `pendingBackgroundTasks` roster, child
threads and lineage panel are the reference: `apps/server/src/orchestration-v2/Adapters/*AdapterV2.ts`,
`SubagentProjection.ts`, `packages/shared/src/orchestrationV2PendingBackgroundWork.ts`,
`apps/web/src/components/chat/ThreadRelationshipsControl.tsx`.

## What the user sees

The mockup is `mockups/background-work-lineage/index.html` (open it in a browser; the control box switches option,
theme, harness and the `@` picker). The user chose **option A plus C's status-bar counter**:

1. **Background strip** above the queued messages, styled like `QueuedMessages.vue`. One row per running item:
   kind (Shell, Agent, Monitor, Task), what it is, model for agents, elapsed time, then *Output* / *Open* /
   *Events* and *Stop*. A finished item stays for a short while with its result (`exit 0`), then drops off.
   The header (`Background · 3 running · 1 finished`) collapses the strip to one line.
2. **Agents tab** in the content panel (a canvas tab): the lineage. Parent, *Running now*, *Started by this
   session*, and a collapsed *Earlier agents*. Each row: status dot, name and task, kind, harness and model,
   elapsed time or state. Selecting a row shows what it was asked, tool calls, tokens, its latest output, and
   *Open session* / *Stop*.
3. **Session list nesting**: child sessions (subagents, forks, sessions it started) nest under their parent with a
   kind label, and a green chip on the parent counts work still running.
4. **"Started by …"** link next to the title of a session another session started.
5. **Status-bar counter**: `5 running in 2 sessions`, opening a popover of running work grouped by session. Hidden
   when nothing runs.
6. **`@` a session** in the composer: sessions are offered before files. Attaching one sends a link, and the agent
   reads what it needs with a tool (see *`@` sessions* below).

Capabilities differ by harness (screenshots `harness-*.png`). The UI only shows what the harness reports: no
Output button when output can't be read, no Stop when an item can't be stopped on its own, no Open when there's no
child session.

## What each harness can provide

| | Claude Code | OpenCode | OpenCode 2 | Pi |
|---|---|---|---|---|
| Native subagents | Yes (`Agent`/`Task`; `task_started`/`task_progress`/`task_notification`; child lines carry `parent_tool_use_id`); background and nested | Yes (`task` → child session with `parentID`); background only behind `OPENCODE_EXPERIMENTAL_BACKGROUND_SUBAGENTS` | Yes (`subagent`, `background`, `session.created{parentID}`) | No. Only the example `subagent` extension, whose children have no session |
| Background shells | Yes (`Bash run_in_background`, `local_bash` tasks, `background_tasks_changed` roster) | No | Yes (`shell background:true` → `shellID`; `GET /api/shell/:id/output`, `DELETE /api/shell/:id`) | No |
| Monitors | Yes (`Monitor` → a `local_bash` task) | No | No | No |
| Survives a harness restart | No | No | Partly (shells reported cancelled, subagents resumed) | n/a |
| Fleet today | Drops child lines (`ClaudeCodeHarnessSession.cs` ~492), ignores `system` events other than `init`, and **kills the process group at the end of every turn**, so all background work dies | Delegation + hidden child session | Delegation + child, `Background` flag, completion notices, lost-work settling; no stop or output | Generic tool cards |

## The shared model

One record of running work per session, written by one service, read by the UI. Harnesses feed it; the UI never
looks at harness specifics.

- **Storage**: generalise `delegations` (migration `048`) with `kind` (`subagent | shell | monitor | task`),
  `work_id` (the harness's handle: shell id, task id, child session id), `label`, `background`,
  `child_session_id` (nullable: Pi extension subagents and some Claude tasks have none), `tool_call_id`,
  `can_stop`, `can_read_output`, `started_at`, `ended_at`, `ended_reason` (`completed | error | cancelled | lost`),
  `detail` (exit code, summary). Keep `DelegationService` as the single writer (rename it if that reads better).
- **Events**: harness sessions emit `work.started`, `work.updated` and `work.ended` harness events
  (`EventTypes.cs`). `HarnessEventRelay` hands them to the service, which persists them and publishes domain events
  over SignalR next to the existing `delegation.*` ones. The OpenCode and OpenCode 2 paths that call
  `DelegationService` directly move onto these events.
- **Snapshot and list**: the session snapshot gets a `runningWork` list (`SessionSnapshotBuilder.cs` already works
  out `Background`), and the session list DTO gets the running count and the lineage fields below, so the list can
  nest children and show the chip without loading each session.
- **Actions** on `IHarnessSession`, with defaults that say "not supported":
  - `StopWorkAsync(workId)`: OpenCode 2 `DELETE /api/shell/:id` or interrupt the child session; OpenCode aborts
    the child; Claude Code has no per-task stop yet (unverified), so it isn't offered.
  - `ReadWorkOutputAsync(workId, offset)`: OpenCode 2 `GET /api/shell/:id/output`; Claude Code tails the task's
    output file.
  - `GetRunningWorkAsync()`: the harness's own roster, to catch up after Fleet or the harness restarts.
- **Capabilities**: `ReportsBackgroundWork`, `SupportsChildSessions`, `ChildSessionsResumable`.
- **Restart**: Fleet settles work it lost (generalising `OpenCode2History.SettleLostBackgroundWork`) as
  `ended_reason = lost`, and tells the agent on its next turn which work was cancelled and won't report back.

## Lineage

Add to `sessions` (migration `048`): `forked_from_session_id`, `spawned_by_session_id`, `spawn_kind`
(`fork | api | message | automation | workflow`). Don't overload `parent_session_id`, which means "hidden delegated
child" today. Set them on fork (`SessionOrchestrator.Fork.cs` saves nothing today), on `POST /api/sessions` when the
caller is a known session, and on workflow and automation runs. Side conversations already have
`side_of_session_id`.

Knowing the calling session needs the caller to identify itself. OpenCode and OpenCode 2 can (bridge token plus
harness session id, `IHarnessCanvasCallerResolver`); Claude Code and Pi have no Fleet tools yet, so their
agent-started sessions get no `spawned_by` until a Fleet MCP server or Pi extension exists. That's a later phase.

## API (built in PR 2)

What the UI PRs (4, 5, 8) and the harness PRs (6, 7) build on.

**A work item** (`RunningWorkItem`), the same shape everywhere below:

```jsonc
{
  "id": "5f0c…",              // Fleet's id; stop and output take this
  "sessionId": "…",           // the session whose agent started it
  "workId": "sh_1076…",       // the harness's handle (shell id, task id, a subagent's call id)
  "kind": "shell",            // subagent | shell | monitor | task
  "title": "shell",           // short name: the agent, or the tool
  "label": "bun run test:e2e",// what it is: the task, the command
  "status": "running",        // pending | running | completed | error | cancelled
  "background": true,         // its call returned while it runs on
  "childSessionId": "…",      // the Fleet session it runs in (subagents); absent when none
  "toolCallId": "call_…",     // its card in the conversation
  "canStop": true, "canReadOutput": true,
  "startedAt": "…", "endedAt": "…",
  "endedReason": "completed", // completed | error | cancelled | lost
  "detail": "exit 0"
}
```

**REST**
- `GET /api/sessions/{id}/work` — running, plus what ended in the last 10 minutes (with its result). `?all=true`
  for everything the session ever ran (the Agents tab's *Earlier agents*).
- `POST /api/sessions/{id}/work/{workId}/stop` — `workId` is the item's `id`. 200 with the item, ended `cancelled`
  (or `lost` when the harness no longer had it); 400 when `canStop` is false; 409 when it already ended or the
  session isn't running; 404 for an unknown item.
- `GET /api/sessions/{id}/work/{workId}/output?offset=0` — `{ output, nextOffset, size, truncated }`, byte offsets.
  Ask again from `nextOffset`; more may come while `nextOffset < size` or the work runs. 400 when `canReadOutput`
  is false.
- `GET /api/work/running` — the user's running work in every session, oldest first (the status-bar counter).
- The session list gets `runningWorkCount`, `forkedFromSessionId`, `spawnedBySessionId`, `spawnKind`;
  `GET /api/sessions/{id}` gets the lineage fields; the snapshot gets `runningWork` (the same list as
  `GET …/work`); `session_created` carries the lineage fields. `delegations` in the snapshot and
  `GET …/delegations` stay subagents only.

**SignalR**: `work.started`, `work.updated`, `work.ended`, each with a work item as `properties`, on
`session:{id}` and on `sessions` (subscribe with `SubscribeToSessionsTopicAsync`). Subagents also keep their
`delegation.*` events.

**Harnesses** report work with `WorkEvents.Started/Updated/Ended(WorkReport, …)` (Infrastructure) as harness
events; the relay hands them to `RunningWorkRecorder`, which makes a Fleet child session from
`ChildHarnessSessionId` and calls `DelegationService`, the one writer. A report changes only the fields it sets;
work that ended stays ended. Actions on `IHarnessSession`: `StopWorkAsync(workId)` (false when the harness no longer
has it), `ReadWorkOutputAsync(workId, offset)`, `GetRunningWorkAsync()` (null when the harness can't say; on attach,
Fleet ends what isn't in it as `lost`, matching by `workId` or by child session). Capabilities:
`ReportsBackgroundWork`, `SupportsChildSessions`, `ChildSessionsResumable`.

| | Work items | Stop | Output | Running list |
|---|---|---|---|---|
| OpenCode 2 | background shells, subagents | shell: `GET` then `DELETE /api/shell/:id`; subagent: interrupt the child | shells: `GET /api/shell/:id/output` | the server's shells for the session, and its children at work |
| OpenCode | subagents (`task`) | abort the child | — | — |
| Claude Code, Pi | none yet (PRs 6, 7) | — | — | — |

**Lineage**: `forked_from_session_id` + `spawn_kind = fork` on Fork and on a kept side conversation;
`spawned_by_session_id` + `spawn_kind = api` on `POST /api/sessions` from an agent (its `/agent/{token}` prefix)
that names its own harness session in `X-Fleet-Harness-Session`. Both plugins put that id in the agent's shell as
`FLEET_HARNESS_SESSION_ID` (OpenCode's `shell.env` hook; OpenCode 2's `tool execute.before` + `shell create.before`),
and the Fleet API skill sends it. Workflow steps get `spawn_kind = workflow`, automation runs `automation`; neither
has a starting session. `parent_session_id` still means a hidden delegated child.

## `@` sessions

The composer's `@` picker (`use-autocomplete.ts`) gets a Sessions group. Attaching a session adds a reference, not a
transcript. On harnesses with Fleet tools the agent gets the title, id and a `fleet_session_read` tool to page
through the history. On harnesses without Fleet tools (Claude Code, Pi today) Fleet adds the session's recap
(`SessionRecapService`) instead, so `@` works everywhere.

## Pull requests

Each PR is green on CI, follows `AGENTS.md`, and shows before/after screenshots under `mockups/<topic>/` when it
changes the UI. Migration numbers: `048` belongs to PR 2; later PRs take the next free number after rebasing.

| # | PR | Depends on | Size |
|---|---|---|---|
| 0 | This design doc and the mockup | — | S |
| 1 | Claude Code: one long-lived process per session (stream-json input stays open), so background work survives the end of a turn; settle it when the process does end | — | L |
| 2 | Shared work model, lineage columns, harness events, actions; OpenCode and OpenCode 2 wired up (OpenCode 2 gets Stop and Output) | — | L |
| 3 | Pi: the model picker really switches the model (`PiSetModelCommand` is never sent) | — | S |
| 4 | UI: background strip and status-bar counter | 2 | M |
| 5 | UI: Agents tab, session-list nesting, "Started by" | 2 | M |
| 6 | Claude Code: `task_*` and `background_tasks_changed` events and `parent_tool_use_id` children into the model | 1, 2 | M |
| 7 | Pi: subagent extension results into the model (no child session) | 2, 3 | M |
| 8 | `@` sessions in the composer, `fleet_session_read`, recap fallback | 2 | M |

## Not verified yet

- A host-side way to stop one Claude Code task without ending the turn.
- Whether `claude --resume` reports tasks lost in a restart.
- Whether Claude Code's `background_tasks_changed` event is stable; it isn't in the documented schema.
