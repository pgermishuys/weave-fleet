# Live Progress

## TL;DR
Show how far along each session is, live: its todo list and the plan it's working through. Fleet works this out from events it already receives, so the agent is never asked to report progress. Pooled OpenCode is the only harness wired up; nothing after the OpenCode adapter knows it's OpenCode.

## Context

When the agent calls `todowrite`, pooled OpenCode also sends a `todo.updated` event (`{sessionID, todos: [{content, status, priority}]}`, checked against OpenCode 1.18.30). It already reaches Fleet on the pooled stream, tagged with its session, but `DomainEventTranslator` drops it as an unknown type. Today the client rebuilds the todo list for the open session only, by searching messages for a tool named `todowrite` (`client/src/lib/todo-utils.ts`). That puts an OpenCode tool name in the client and leaves every other session with no progress.

Plans are markdown checklists. Weave's executor prompt (`@weaveio/weave-adapter-opencode` 0.1.2) says: "use the Edit tool to change `- [ ]` to `- [x]` in the plan file". It hands each task to a Shuttle subagent. Other planners (superpowers, spec-kit, a hand-written `TODO.md`) also write checklists. So every tick arrives as a file-writing tool call, and Fleet can follow any plan without knowing who wrote it.

Precedents in the code:
- `SmartLinkDetector.Observe` in `HarnessEventRelay` watches events and queues work for a background service that stores results and pushes them on the `"sessions"` topic. Progress uses the same shape, but watches Fleet's own events rather than OpenCode payloads.
- `OpenCodeHarnessSession.TryBuildToolResultEvent` already creates an event of its own from a raw OpenCode event. The new neutral events are created the same way.
- Rows already update live: `activity_status` is pushed on the `"sessions"` topic, and `use-session-activity-updates.ts` calls `sessionsStore.patchSession`. Progress goes the same way.
- `FleetCanvasPluginLiveTests` runs a real pooled OpenCode process against `FakeLlmServer`, with a scratch HOME and a scripted model. Live checks for this plan copy it.

Mockup (Today vs Proposed, driven by the real event sequence): https://claude.ai/code/artifact/33796920-18de-4ef5-bfee-7d6c67dee2d6

### The harness-neutral line
| Layer | Knows about |
|---|---|
| OpenCode adapter (`Infrastructure/Harnesses/OpenCode`) | `todo.updated`, the `edit`/`write`/`apply_patch` tool names, OpenCode payload shapes |
| Fleet events | `todos.reported`, `files.written`, and the existing delegation and idle events |
| Progress tracker (Application) | Todo lists, checklist files, ticks, steps. No harness names. |
| Client | `session_progress` / `progress.updated` payloads only |

## Scope
- In scope:
  - `todos.reported` and `files.written` Fleet events, and the OpenCode adapter mapping for both
  - A progress tracker that keeps, stores and pushes each session's progress
  - Finding plans in checklist markdown files, parsing them, and recording ticks
  - Progress on session rows, a strip under the right-panel tabs, a built-in Progress tab, and the collapsed rail
  - Subagent sessions shown under the plan step they work on
- Out of scope:
  - Claude Code and Pi. Their capability flags stay false, and the UI shows only what a harness reports.
  - Guessing the current step from edited files (Phase 4). Decide after using Phases 1 to 3.
  - Reading Weave's `.weave/state.json` or anything else specific to one planner
  - Editing plans from Fleet. The Progress tab is read-only.
  - Sessions on other machines (plans are read from the local disk)
- Constraints / assumptions:
  - The agent is never prompted to report progress, and no context is added to its prompts.
  - Plan files are read only from inside the session's directory. They must be `.md` files of 256 KB or less, and symlinks are resolved before the directory check.
  - Phases 1 to 3 ship as one pull request (the user's choice, 2026-09-13). Each phase still leaves Fleet working.

## Objectives
1. Todo progress on every session row, live, for sessions that aren't open too
2. Any checklist plan the agent works through, shown with ticks, times and the current step
3. No OpenCode names past the adapter. A new harness only has to send the two Fleet events.
4. No extra tokens

## Dependencies and Order
1. **Phase 1: Todos, end to end.** Fleet events, adapter mapping, tracker, storage, push, rows, and replacing the client's tool-name matching.
2. **Phase 2: Plans from checklist files.** `files.written`, the parser, tick detection and the Progress tab. Needs Phase 1's tracker.
3. **Phase 3: Subagents under their step.** Needs Phase 2's steps.
4. **Phase 4 (optional): Guess the current step.** Decide after Phases 1 to 3.

## Tasks

### Phase 1: Todos, end to end

- [x] 1. Add the `todos.reported` Fleet event and the capability flag
  - **What**: Add `EventTypes.TodosReported = "todos.reported"` and classify it in `EventTypeMetadata`: known, not durable, and handled like the other session-state events there. Add a `TodosReported` domain event with a `TodosReportedPayload { SessionId, Items }`. Each item is `TodoEntry { Content, Status, Priority? }`, with status one of `pending`, `in_progress`, `completed` or `cancelled`. Map it in `DomainEventTranslator` and give it the wire name `todos.reported` in `SessionEventsHub`. Add `ReportsTodos` to `HarnessCapabilities` (default false). Register the new types in the source-generated JSON contexts; the AOT build fails without them.
  - **Files**:
    - `src/WeaveFleet.Domain/Harnesses/EventTypes.cs`
    - `src/WeaveFleet.Domain/Harnesses/EventTypeMetadata.cs`
    - `src/WeaveFleet.Domain/Events/ProgressEvents.cs` (new)
    - `src/WeaveFleet.Domain/Harnesses/HarnessTypes.cs`
    - `src/WeaveFleet.Infrastructure/Events/DomainEventTranslator.cs`
    - `src/WeaveFleet.Api/Hubs/SessionEventsHub.cs`
    - `src/WeaveFleet.Infrastructure/JsonContext.cs`, `src/WeaveFleet.Api/JsonContext.cs`
  - **Depends on**: None
  - **Acceptance**:
    - A `todos.reported` harness event becomes a `TodosReported` domain event with its items
    - An unknown status becomes `pending` (as the client does today); an item without content is dropped
    - Translator unit tests cover a normal list, an empty list and bad items

- [x] 2. Map OpenCode's `todo.updated` in the adapter
  - **What**: In `OpenCodeHarnessSession.SubscribeAsync`, turn `todo.updated` into a `todos.reported` event. The mapping itself lives in `OpenCodeMapper`. Subagent events keep their routed `FleetSessionId`, so a child's todos land on the child session. Send the Fleet event instead of the raw one, so nothing downstream sees `todo.updated`. Set `ReportsTodos = true` on `OpenCodeHarness`. Add optional `IHarnessSession.GetTodosAsync` (default: returns null). Implement it for OpenCode with `GET /session/{id}/todo?directory=` in `OpenCodeHttpClient`; it's used to rebuild progress after a restart.
  - **Files**:
    - `src/WeaveFleet.Infrastructure/Harnesses/OpenCode/OpenCodeMapper.cs`
    - `src/WeaveFleet.Infrastructure/Harnesses/OpenCode/OpenCodeHarnessSession.cs`
    - `src/WeaveFleet.Infrastructure/Harnesses/OpenCode/OpenCodeHarness.cs`
    - `src/WeaveFleet.Infrastructure/Harnesses/OpenCode/OpenCodeHttpClient.cs`
    - `src/WeaveFleet.Domain/Harnesses/IHarnessSession.cs`
    - `tests/WeaveFleet.Infrastructure.Tests/Harnesses/` (mapper tests)
    - `tests/WeaveFleet.ConformanceTests/OpenCode/OpenCodeConformanceTests.cs`
  - **Depends on**: Task 1
  - **Acceptance**:
    - Mapper tests use the real 1.18.30 payload shape, including a child session's event
    - The `todo.updated` shape is pinned against real OpenCode by Task 6's live test, not the conformance suite: `OpenCodeFixture` starts OpenCode with the real HOME, so it would load the user's own OpenCode config and plugins. An OpenCode upgrade that changes the shape then fails in CI, not in production.
    - Harnesses without `GetTodosAsync` return null

- [x] 3. Add the progress tracker, storage and push
  - **What**: Application layer:
    - A `SessionProgress` model: kind (`todos` or `plan`), done, total, a current-item label, full detail, and the update time.
    - A pure `SessionProgressTracker` that applies Fleet events to it.
    - `ISessionProgressRepository`.

    Infrastructure layer:
    - A `SessionProgressObserver`. `HarnessEventRelay` calls `Observe(sessionId, userId, domainEvent)` right after translating, next to `SmartLinkDetector.Observe`. It only writes to a bounded channel, so the relay never waits.
    - A `SessionProgressService` background service that reads the channel, applies each event, and stores the result in a new `session_progress` table with the detail as JSON. It skips unchanged results. It pushes the summary as `session_progress` on the `"sessions"` topic and the full detail as `progress.updated` on `session:{id}`.
    - When an event arrives for a session Fleet has no stored progress for, and the harness has `GetTodosAsync`, the service seeds from it.
  - **Files**:
    - `src/WeaveFleet.Application/Progress/SessionProgress.cs` (new)
    - `src/WeaveFleet.Application/Progress/SessionProgressTracker.cs` (new)
    - `src/WeaveFleet.Application/Progress/ISessionProgressRepository.cs` (new)
    - `src/WeaveFleet.Infrastructure/Progress/SessionProgressObserver.cs` (new)
    - `src/WeaveFleet.Infrastructure/Progress/SessionProgressService.cs` (new)
    - `src/WeaveFleet.Infrastructure/Data/Repositories/SessionProgressRepository.cs` (new)
    - `src/WeaveFleet.Infrastructure/Migrations/032_add_session_progress.sql` (new; renumber if main has moved on)
    - `src/WeaveFleet.Infrastructure/Services/HarnessEventRelay.cs`
    - `src/WeaveFleet.Infrastructure/DependencyInjection.cs`
  - **Depends on**: Task 1
  - **Acceptance**:
    - Tracker unit tests: first list, updated list, emptied list, identical list (no push)
    - `session_progress` rows are deleted with their session (cascade)
    - A SignalR contract test in `SignalREventContractTests` asserts the exact JSON of `session_progress` on `"sessions"` and `progress.updated` on the session topic
    - A slow database or broadcaster never blocks the relay (the channel drops the oldest item, like `SmartLinkDetector`)

- [x] 4. Serve progress through the API
  - **What**: Add an optional `Progress` summary to `SessionListResponse` as an init property, like `Origin`, so the positional record doesn't change. Load it with one query for the whole page, not one per session. Add `GET /api/sessions/{id}/progress` for the open session's full detail, owner-scoped like the other session endpoints. Regenerate or extend the client API types.
  - **Files**:
    - `src/WeaveFleet.Application/DTOs/SessionDtos.cs`
    - `src/WeaveFleet.Api/Endpoints/SessionEndpoints.cs`
    - `src/WeaveFleet.Api/JsonContext.cs`
    - `client/src/api/client.ts`
  - **Depends on**: Task 3
  - **Acceptance**:
    - The list includes progress for sessions that have it and `null` for the rest, like `origin`. The API writes null fields and the SignalR push omits them, as each already does.
    - Another user's session returns 404 from the progress endpoint
    - Api tests cover both endpoints

- [x] 5. Client: rows, strip and rail from server progress; drop the tool-name matching
  - **What**:
    - Add the `session_progress` and `progress.updated` event types to `domain-events.ts`.
    - Patch rows from `session_progress` in the same `"sessions"` listener as `activity_status`.
    - Add `useSessionProgress(sessionId)`: it fetches the detail and then follows `progress.updated`. It refetches on reconnect, like the snapshot.
    - Point `useSessionTodos` and its callers (`SessionMetadataHeader`, `CollapsedRightRail`, `SessionsV2RightPanel`) at it.
    - Add a `ProgressRing` and a count to `SessionItem`. A "Needs you" status still wins the space.
    - Delete `extractLatestTodos`, `isTodoWriteTool` and `parseTodoOutput` from `todo-utils.ts`; keep the `TodoItem` type.
    - Delete `PlanPanel.vue`, which nothing uses.
  - **Files**:
    - `client/src/lib/domain-events.ts`
    - `client/src/composables/use-session-activity-updates.ts`
    - `client/src/composables/use-session-progress.ts` (new)
    - `client/src/composables/use-session-todos.ts`
    - `client/src/lib/todo-utils.ts`
    - `client/src/components/sessions/SessionItem.vue`
    - `client/src/components/sessions/ProgressRing.vue` (new)
    - `client/src/components/session/SessionMetadataHeader.vue`
    - `client/src/components/layout/CollapsedRightRail.vue`
    - `client/src/components/sessions/SessionsV2RightPanel.vue`
    - `client/src/components/session/PlanPanel.vue` (delete)
  - **Depends on**: Task 4
  - **Acceptance**:
    - No client code mentions `todowrite`
    - A `session_progress` event updates a row that isn't open
    - Vitest covers the composable and the row. The ring's colours and timing come from tokens, so it adds no `lint:design` violations; the 9 raw-button violations were already on main.

- [x] 6. End-to-end and live checks for Phase 1
  - **What**:
    - An E2E test where the TestHarness sends `todos.reported` directly. That proves a harness other than OpenCode needs only the Fleet event. It checks that the row shows the ring and count, and that the strip lists the todos.
    - A live integration test copying `FleetCanvasPluginLiveTests`: a real pooled OpenCode process and a scripted model calling `todowrite` twice. It asserts `session_progress` arrives with the right counts.
    - A manual check against a scratch Fleet in the browser.
  - **Files**:
    - `tests/WeaveFleet.E2E/Tests/SessionProgressTests.cs` (new)
    - `tests/WeaveFleet.IntegrationTests/Harnesses/OpenCode/SessionProgressLiveTests.cs` (new)
  - **Depends on**: Tasks 2, 5
  - **Acceptance**:
    - Both tests pass locally and in CI (the live test is skipped where `opencode` isn't installed, like `[OpenCodeFact]`)
    - Checked by hand in a scratch Fleet with a scratch HOME. The real `~/.weave` is never touched.
    - Done 2026-09-13: the live test passes against OpenCode 1.18.30. It uses the shared `PooledOpenCodeLiveHost` from PR #195. The E2E test passes and was screenshotted in a real browser: the row shows the ring and "1/3", and the right panel shows "1 of 3 todos". Locally the .NET E2E tests need `PLAYWRIGHT_HOST_PLATFORM_OVERRIDE=ubuntu24.04-x64` on Ubuntu 26.04. Not yet checked: a real model in a real pooled session in the browser.

### Phase 2: Plans from checklist files

- [x] 7. Add the `files.written` Fleet event and map OpenCode's file tools
  - **What**: Add `EventTypes.FilesWritten = "files.written"` and a `FilesWritten` domain event with `{ SessionId, MessageId, Paths }` (absolute paths). The OpenCode adapter sends it for completed `edit` and `write` tool parts (`state.input.filePath`) and `apply_patch` parts (paths from `*** Add File:`, `*** Update File:` and `*** Move to:` lines in `state.input.patchText`). Each tool call is reported once, when it completes. Add a `ReportsFileWrites` capability. This isn't `FilesChanged`, which comes from the file watcher and has no message.
  - **Files**:
    - `src/WeaveFleet.Domain/Harnesses/EventTypes.cs`, `src/WeaveFleet.Domain/Events/ProgressEvents.cs`, `src/WeaveFleet.Domain/Harnesses/HarnessTypes.cs`
    - `src/WeaveFleet.Infrastructure/Harnesses/OpenCode/OpenCodeMapper.cs`, `OpenCodeHarnessSession.cs`, `OpenCodeHarness.cs`
    - `src/WeaveFleet.Infrastructure/Events/DomainEventTranslator.cs`, `src/WeaveFleet.Api/Hubs/SessionEventsHub.cs`, JSON contexts
  - **Depends on**: Task 1
  - **Acceptance**:
    - Mapper tests cover all three tools, relative and absolute paths, a multi-file patch, and running parts (no event until the part completes)
    - The `write` and `edit` part shapes are pinned against real OpenCode by `SessionProgressLiveTests`, for the same reason as Task 2

- [x] 8. Add a checklist plan parser
  - **What**: A pure `ChecklistPlanParser` in the Application layer that turns markdown into `PlanDocument { Title, Groups[{ Title?, Steps[{ Number?, Title, Checked, SubDone, SubTotal, Mentions }] }] }`.
    - The title is the first `#` heading.
    - A group is the nearest heading above its steps; a flat list has one group with no title.
    - Steps are unindented `- [ ]`, `* [ ]` or `1. [ ]` items (`[x]` or `[X]` means checked), with any leading `N.` taken as the number.
    - Indented checkboxes are counted as sub-steps.
    - Fenced code blocks are skipped. Weave plans have `# Backend compiles` lines inside them.
    - Mentions are backticked paths in the step's text; Phase 4 uses them.
  - **Files**:
    - `src/WeaveFleet.Application/Progress/ChecklistPlanParser.cs` (new)
    - `src/WeaveFleet.Application/Progress/PlanDocument.cs` (new)
    - `tests/WeaveFleet.Application.Tests/Progress/ChecklistPlanParserTests.cs` (new)
    - `tests/WeaveFleet.Application.Tests/Progress/Fixtures/` (copies of real `.weave/plans` files plus a superpowers-style plan)
  - **Depends on**: None
  - **Acceptance**:
    - `thin-proxy-simplification.md` parses to 6 groups and 17 steps; `session-tags.md` parses to 1 untitled group and 13 steps
    - Code fences, nested boxes, `[X]`, CRLF line endings and a file with no checkboxes all pass tests

- [x] 9. Track plans and ticks
  - **What**: In the tracker and service:
    - When `FilesWritten` names a `.md` file inside the session's directory (256 KB or less, symlinks resolved), read and parse it.
    - Compare with the last version seen for that session and file. Each box that has flipped to checked becomes a tick with its time and message id. Boxes already checked when Fleet first sees a file get no time; the UI says "tracked since".
    - A `.md` file the session writes becomes a plan when it has 3 or more unindented checkboxes, whether the session created it (the planning session) or ticked it (the executor).
      - Changed from the first draft ("when the session ticks a box in it"): the first time Fleet reads a file it has no earlier version, so it can't tell a fresh tick from an old one. Boxes already ticked at that first read count as done without a tick time. The UI says "tracked since".
      - PR templates and READMEs rarely have 3 or more unindented checkboxes, and agents rarely write them as files.
    - When a session touches more than one plan, the one ticked most recently wins.
    - On `SessionIdle`, re-read the session's plan files. That catches ticks made by shell commands or by the user in an editor.
    - The current step is the first unticked one. The live todo list belongs to it.
    - Ticks are stored in the progress detail JSON.
  - **Files**:
    - `src/WeaveFleet.Application/Progress/SessionProgressTracker.cs`
    - `src/WeaveFleet.Infrastructure/Progress/SessionProgressService.cs`
    - `src/WeaveFleet.Infrastructure/Progress/PlanFileReader.cs` (new; path guard and size cap)
  - **Depends on**: Tasks 3, 7, 8
  - **Acceptance**:
    - Tracker tests: first sighting, a tick, two ticks in one edit, an unticked box, a file that isn't a plan, two plans, and a plan deleted from disk
    - Path-guard tests: `..`, a symlink out of the directory, a non-`.md` file and an oversized file are all refused
    - The row switches from the todo count to the plan count once there's a plan

- [x] 10. Add the Progress tab and the strip
  - **What**: Add a built-in, client-only canvas kind, `progress` (label "Progress", `ListChecks` icon), next to `changes`, `files` and `context` in `canvas-registry.ts`, and pickable. `ProgressCanvas.vue` shows:
    - the plan title and file, and the count
    - the flow bar: one segment per group, or one per step for a flat plan
    - groups with finished ones folded, showing how long they took
    - steps with tick times, the current step marked, and its live todos
    - a small legend

    Its tab badge shows the count. With only todos it shows the list; with nothing it shows an empty state that names both sources. The strip under the tabs (replacing today's todo chip in `SessionMetadataHeader`) shows the current group and step when another tab is open, and opens the Progress tab on click. Match the mockup.
  - **Files**:
    - `client/src/lib/canvas-registry.ts`
    - `client/src/components/canvas/ProgressCanvas.vue` (new)
    - `client/src/components/session/SessionMetadataHeader.vue`
    - `client/src/components/canvas/CanvasHost.vue` (badge)
  - **Depends on**: Tasks 5, 9
  - **Acceptance**:
    - Vitest covers a phased plan, a flat plan, todos only and empty
    - Works in the dark and light themes and at the narrowest right-panel width (280px)
    - An E2E test: the TestHarness sends `files.written` for a fixture plan in the workspace, then ticks it; the tab and the row update
    - Done 2026-09-13, checked in a real browser from the E2E run. Open point: at the default 360px panel width the tab strip already scrolls sideways once Context is added, and with Progress added the Files tab is scrolled out of view.

### Phase 3: Subagents under their step

- [x] 11. Nest subagent sessions under the step they're working on
  - **What**: When `DelegationCreated` arrives for a session with a plan, attach the child session to the parent's current step (the first unticked one). For Weave this is exact: the executor delegates the next unchecked task. The child's own progress summary (its todos) goes into the parent's detail and is pushed when the child changes. `DelegationCompleted` marks it finished. The Progress tab shows a subagent card under the step, with its count and current todo, linking to the child session.
  - **Files**:
    - `src/WeaveFleet.Application/Progress/SessionProgressTracker.cs`
    - `src/WeaveFleet.Infrastructure/Progress/SessionProgressService.cs`
    - `client/src/components/canvas/ProgressCanvas.vue`
  - **Depends on**: Task 9
  - **Acceptance**:
    - Tracker tests: delegation with a plan, without a plan, two children on one step, and a child finishing after its step is ticked
    - A live integration test: a scripted model delegates to a subagent that calls `todowrite`, and the parent's detail shows the child's counts
    - Done 2026-09-13, with two findings from the live test:
      - OpenCode 1.18 doesn't offer `todowrite` (or `task`) to any subagent; its built-in `general` agent even has `todowrite: "deny"`. So with OpenCode a subagent card shows its task, status and a link, never a count. The roll-up of counts is harness-neutral and tested, and lights up for any harness whose subagents keep todo lists.
      - Fleet named child sessions after the agent ("general"), so the task comes from the `task` call's `description`. The adapter reads it and `DelegationService` passes it to progress (no schema change).
      - Separately, one delegation left three child session rows in the live test. That looks like an existing race in Fleet's child-session creation, not caused by this plan.

### Phase 4 (optional): Guess the current step

- [ ] 12. Decide whether to guess the current step from edited files
  - **What**: After using Phases 1 to 3 on real Weave runs, decide whether a "probably in progress" state is worth it. It would come from `FilesWritten` paths, including child sessions', matching a step's mentions; it's the dashed ring in the mockup. It would never tick a box. If yes, write it as its own plan.
  - **Files**: None yet
  - **Depends on**: Task 11
  - **Acceptance**:
    - A yes or no from the user, with examples from real sessions

## Open questions
1. Should the planning session (the one that writes the plan) show the plan at 0/N? Proposed: yes (Task 9, condition b).
2. When a session has both a plan and todos, the row shows the plan count. Is that right, or should rows always show todos?
3. Phase 4: keep it, or drop it until someone asks?

## Verification
```bash
# Backend builds, including AOT-sensitive JSON contexts
dotnet build src/WeaveFleet.Api -c Release

# Server tests
dotnet test tests/WeaveFleet.Application.Tests -c Debug
dotnet test tests/WeaveFleet.Infrastructure.Tests -c Debug
dotnet test tests/WeaveFleet.Api.Tests -c Debug
dotnet test tests/WeaveFleet.IntegrationTests -c Debug --filter "FullyQualifiedName~SignalREventContractTests|FullyQualifiedName~SessionProgress"

# Live and conformance tests (real opencode + FakeLlmServer, scratch HOME)
dotnet test tests/WeaveFleet.IntegrationTests -c Debug --filter "FullyQualifiedName~SessionProgressLiveTests"
dotnet test tests/WeaveFleet.ConformanceTests -c Debug

# Client, matching CI (Node 22 + npm ci, not bun, not local Node 26)
cd client && corepack npm ci && npm run lint && npm run typecheck && npx -y -p node@22 node node_modules/vitest/vitest.mjs run && bun run lint:design

# E2E (needs a frontend build)
cd client && bun run build && cd .. && dotnet test tests/WeaveFleet.E2E --filter "FullyQualifiedName~SessionProgressTests"

# Manual: a scratch Fleet with a scratch HOME (never the real ~/.weave), pooled OpenCode, a Weave plan in the workspace
```
