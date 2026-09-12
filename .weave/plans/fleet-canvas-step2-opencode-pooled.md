# fleet-canvas-step2-opencode-pooled

## TL;DR
Move canvas state to the server and let the agent open, read and change canvases, for **pooled OpenCode sessions only**. Fleet stores canvases per session (versioned, every change marked `agent` or `user`) and pushes `canvas.updated` over SignalR. Fleet ships a local OpenCode plugin, `fleet-canvas.ts` (a plugin rather than a tool file since Task 0), that gives the agent `fleet_canvas_list`, `fleet_canvas_open`, `fleet_canvas_read`, `fleet_canvas_patch` and `fleet_canvas_focus`. Each call carries OpenCode's `sessionID`; Fleet maps it to the Fleet session through the pool binding table, using a per-process token.

You can drag and remove boxes in a Diagram canvas, and "Ask agent" attaches a box to your message. **Fleet never tells the agent about your edits.** Your edits are protected instead: your box positions are always kept, changes name boxes by id, and Fleet refuses an agent change that touches a box you removed. The refusal is how the agent finds out. Edits cost no tokens, and a read returns only what changed, as short text.

## Context
Ground truth from the codebase (2026-09-12). Don't re-check these:

- **Pooled is the default.** `FleetOptions.Harness.PooledOpenCodeHarness = true` (`src/WeaveFleet.Application/Configuration/FleetOptions.cs:118`). One `opencode serve` process hosts many Fleet sessions, demuxed by `PoolDemuxBindingTable` keyed `(PooledOpenCodeInstance, openCodeSessionId)` (`src/WeaveFleet.Infrastructure/Harnesses/OpenCode/Pooling/PoolDemuxBindingTable.cs:302`). A binding carries `FleetSessionId`, `UserId`, `Directory` and `LeaseGeneration` (line 305).
- **Why not MCP here.** An MCP server config is per process, and OpenCode's MCP client doesn't tell the server which session is calling. So a pooled process can't tell Fleet which session a call belongs to. A native OpenCode tool can: `ToolContext` has `sessionID`, `messageID`, `agent`, `directory`, `worktree` and `metadata()` (`~/.config/opencode/node_modules/@opencode-ai/plugin/dist/tool.d.ts`, plugin 1.18.28). MCP comes back when a second harness is added (Claude Code, NuCode).
- **OpenCode config hooks.** The installed OpenCode is 1.18.30. Its binary reads `OPENCODE_CONFIG_DIR` and `OPENCODE_CONFIG_CONTENT`. Fleet already sets `OPENCODE_CONFIG_CONTENT` to auto-allow permissions (`OpenCodeProcessManager.cs:121`). User tools live in `~/.config/opencode/tools/*.ts`; the catalog `visualize.ts` is one. A file with several named exports becomes `<file>_<export>` tools, so `fleet_canvas.ts` exporting `open` gives `fleet_canvas_open`.
- **Where pooled processes start.** `OpenCodeHarnessRuntime.CreatePooledInstanceAsync` (`OpenCodeHarnessRuntime.cs:808`) spawns them with `Port = 0` on `127.0.0.1`. Pool membership is keyed by a credential hash of the launch env, computed *before* this method runs (`SpawnPooledAsync`, line 594). Env added inside `CreatePooledInstanceAsync` therefore doesn't change pool keying.
- **Auth.** `BearerTokenHandler` authenticates any loopback request that has no header (`src/WeaveFleet.Api/Auth/BearerTokenHandler.cs:30`). The bridge can't rely on that: loopback proves nothing about which instance or session is calling.
- **Fleet URL.** `FleetOptions.ListenUrl` is built from `Host` (default `127.0.0.1`) and `Port`.
- **Persistence.** SQLite with Dapper(.AOT) and DbUp migrations in `src/WeaveFleet.Infrastructure/Migrations/NNN_*.sql`; the latest is `027`. `PRAGMA foreign_keys=ON`. Session children use `REFERENCES sessions(id) ON DELETE CASCADE`. The API is published AOT and trimmed in Release, so no reflection-based patch or schema libraries.
- **Events.** Typed `DomainEvent` records with `[JsonDerivedType]` (`src/WeaveFleet.Domain/Events/DomainEvent.cs`). `FilesChanged` (`files.changed`) is the closest template. Session events go out on topic `session:{id}` through `IEventBroadcaster`. The client reduces them in `client/src/lib/domain-event-reducer.ts`, typed in `client/src/lib/domain-events.ts`.
- **Client after step 1.** `client/src/stores/canvases.ts` holds per-session `canvases[]` and `activeId`, and visual canvases carry a client-only `VisualPayload`. `client/src/lib/canvas-registry.ts` maps kind to component. `ActivityStream.vue` parses visualize output and calls `openVisual`. Flow diagrams render with `@vue-flow/core` and dagre (`VueFlowRenderer.vue`). Annotations turn into prompt text through `formatAnnotationPrompt` (`client/src/lib/format-annotation-prompt.ts`).
- **Test rigs.** `tests/NuCode.ConformanceTests/OpenCode/OpenCodeFixture.cs` runs a real `opencode` against `tests/FakeLlmServer` (scripted OpenAI responses, including tool calls). SignalR contract tests: `tests/WeaveFleet.IntegrationTests/Sessions/SignalREventContractTests.cs`.
- **Safety** (from memory): never run the Fleet API or `opencode` spikes with the real `HOME`. `LegacyDataMigrator` deletes the installed Fleet's live `~/.weave/fleet.db`. Use a scratch `HOME`. UI work runs in Vite mock mode (port 3099).

## Decisions (2026-09-12)
1. **The agent isn't told about your edits.** No note on your next message and no "unread edits" anywhere in the prompt. The user asked "Why do we even need to tell the agent anything?" Fleet protects your edits on its own (below), and "Ask agent" is how you point the agent at something on purpose.
2. **Tool names are `fleet_canvas_*`**, so they don't clash with a user tool named `canvas.ts`. Since Task 0 they're the plugin's `tool` keys, not derived from a file name.
3. **Fleet refuses only an agent change that touches something you removed** since the agent last read or wrote the canvas. The refusal names what you removed, and that's how the agent learns about it. Moves never conflict. There's no version check on every write.
4. **`fleet_canvas_open` with an existing title reopens that canvas** (open or closed) instead of creating a new one, the same rule as step 1's `openVisual`.
5. **The mockup's "N edits not read yet" pill is dropped.** The agent is never told about edits, so a counter of unread edits would suggest something is pending when nothing is. The version badge stays. This is a deliberate change from the mockup.
6. **Keep agent tokens low.** Edits cost no tokens until the agent works on the canvas. Moves are layout and never reach the agent. Reads return a short text diff by default. Writes return one line and don't echo the diagram back.

## Scope
- In scope:
  - Server canvas store: tables, repository, `ICanvasService`, per-kind change ops and validation, and `canvas.updated`, `canvas.closed` and `canvas.focused` events.
  - Client REST API for canvases (list, user changes, close).
  - An agent bridge for pooled OpenCode: per-process token, session resolution, bridge endpoints.
  - `fleet-canvas.ts` local OpenCode plugin, written to Fleet's app data and loaded by pooled processes through `OPENCODE_CONFIG_CONTENT`.
  - Canvas kinds `diagram` (boxes and edges with positions, the mockup's canvas) and `sequence` (Mermaid source, which only the agent edits). Together they cover what `visualize.ts` does today.
  - Client: server-backed canvases in the store, the Diagram canvas with move and remove, canvas tool cards, the Ask agent chip, mock-mode fixtures.
- Out of scope:
  - Dedicated (non-pooled) OpenCode, Claude Code, NuCode and Pi. The store and tool file are harness-neutral, so adding them later only touches launch config.
  - An MCP server.
  - Terminal, repo and personal canvases, and a JSON Schema engine (steps 3–5).
  - User edits beyond move and remove: renaming, adding boxes or edges, editing sequence source.
  - Removing `visualize.ts` from the catalog. The client keeps rendering its output. Retiring it is a follow-up once `fleet_canvas_open` has shipped.
  - Markdown and HTML canvas kinds.
- Constraints:
  - AOT-safe: `System.Text.Json.Nodes` plus source-generated contexts, with hand-written ops and validators.
  - A bridge call succeeds only with a valid instance token from a loopback address, and only for a session bound to that instance. Any miss returns 404, with no difference between "unknown" and "not yours".
  - Canvas state is small: at most 256 KB serialized and at most 200 nodes per diagram. Changes are rejected over those limits.

## Design

### Data
```sql
-- 028_add_canvases.sql
CREATE TABLE canvases (
  id TEXT PRIMARY KEY,                 -- cv_<ulid>
  session_id TEXT NOT NULL REFERENCES sessions(id) ON DELETE CASCADE,
  user_id TEXT NOT NULL,
  kind TEXT NOT NULL,                  -- diagram | sequence
  title TEXT NOT NULL,
  state_json TEXT NOT NULL,
  version INTEGER NOT NULL,            -- bumps on every accepted change
  agent_seen_version INTEGER NOT NULL, -- last version the agent read or wrote
  created_at TEXT NOT NULL,
  updated_at TEXT NOT NULL,
  closed_at TEXT
);
CREATE INDEX ix_canvases_session ON canvases(session_id);

CREATE TABLE canvas_revisions (
  canvas_id TEXT NOT NULL REFERENCES canvases(id) ON DELETE CASCADE,
  version INTEGER NOT NULL,
  actor TEXT NOT NULL,                 -- agent | user
  ops_json TEXT NOT NULL,              -- the change ops that produced this version
  created_at TEXT NOT NULL,
  PRIMARY KEY (canvas_id, version)
);
```
`agent_seen_version` exists only to find "what the user removed since the agent last looked". The conflict check (Decision 3) and the default read (below) both use it. It never produces a prompt, a count or a UI element.

### Canvas states
```jsonc
// diagram
{ "direction": "TB",
  "nodes": [{ "id": "n1", "label": "NuCode session", "detail": "NuCode/Sessions", "x": 20, "y": 44, "placedByUser": true }],
  "edges": [{ "id": "e1", "from": "n1", "to": "n2", "label": "publishes", "style": "solid" }] } // style: solid | dashed | planned
// sequence
{ "source": "sequenceDiagram\n  Agent->>Fleet: fleet_canvas_open" }
```
`x` and `y` are optional. The client lays out nodes without positions using dagre and never writes those computed positions back. A drag writes the position and sets `placedByUser`. The agent never sees `x`, `y` or `placedByUser`.

### Change ops (instead of JSON Patch)
Ops name things by id, so your edits and the agent's don't shift each other's references. This is why a stale write doesn't need a version check.

| Actor | Diagram ops | Sequence ops |
|---|---|---|
| agent | `addNode {id, label, detail?}`, `updateNode {id, label?, detail?}`, `removeNode {id}` (removes its edges too), `addEdge {id, from, to, label?, style?}`, `updateEdge {id, label?, style?}`, `removeEdge {id}`, `setDirection {direction}` | `setSource {source}` |
| user | `moveNode {id, x, y}`, `removeNode {id}` | none |

- A batch applies all-or-nothing, and the state is validated afterwards: unique ids, edges pointing at existing nodes, known `style` values, size limits.
- **Conflict rule (agent only).** The batch is refused if any op references an id that the user removed after `agent_seen_version`. That covers updating it, removing it, re-adding the same id, or an edge to or from it. The message is one line per removed item, e.g. `Refused: the user removed box "use-sessions.ts" (n7) in v4. Call fleet_canvas_read to see the current diagram.` A reference to an id that never existed is a plain validation error.
- Agent ops can't set positions, so the user's layout is always kept. Boxes the agent adds get placed by dagre on the client.

### Agent tools (`fleet_canvas.ts`)
| Tool | Args | Returns |
|---|---|---|
| `fleet_canvas_list` | none | one line per open canvas: id, kind, title, version, and `(changed by user)` if the user removed something since the agent last looked |
| `fleet_canvas_open` | `kind`, `title`, `state` (no positions) | `Opened "Session event flow" (cv_…) at v1`, and focuses the tab |
| `fleet_canvas_read` | `canvasId`, `full` (boolean, required: plain JSON Schema args are all required, see Task 0 findings) | `full: false`: what the user removed since the agent last looked (moves omitted), or `No changes since v5`. `full: true`: the whole diagram as compact text |
| `fleet_canvas_patch` | `canvasId`, `ops[]` | `Updated to v3 (+2 boxes, −1 edge)`, or the refusal above |
| `fleet_canvas_focus` | `canvasId` | brings the tab forward |

A full read is compact text, not JSON, and leaves out positions:
```
diagram cv_01J… "Session event flow" v5 TB
n1 NuCode session · NuCode/Sessions
n3 SessionEventsHub · Api/Hubs
e1 n1 -> n2 publishes
e9 n3 -> n5 SessionListChanged [planned]
```
The tool description includes: "The user can move and remove boxes at any time. Read a canvas before you describe it or change it. Use `full: true` if you don't have it in context. Use `fleet_canvas_open` instead of drawing diagrams in chat."

Each tool POSTs to `{FLEET_URL}/api/bridge/opencode/canvas/{op}` with `Authorization: Bearer {FLEET_BRIDGE_TOKEN}` and `openCodeSessionId: context.sessionID` in the body. It returns `{ title, output, metadata }`, so the tool card reads like the mockup, e.g. "patch Diagram · +8 boxes, +7 edges · v2".

### Session resolution
1. Bearer token → `PooledOpenCodeInstance`. `CreatePooledInstanceAsync` mints a token per process, stores it on the instance, and injects `FLEET_URL` and `FLEET_BRIDGE_TOKEN`. The plugin itself is loaded through `OPENCODE_CONFIG_CONTENT` (Task 0 findings). The token dies with the instance.
2. `(instance, openCodeSessionId)` → binding, via a new directory-less `TryGetBinding` overload.
3. No binding: the caller may be a subagent's child session. Ask that instance's `OpenCodeHttpClient` for the session's `parentID` and retry, up to 3 hops.
4. Run the call as `binding.UserId` (`BackgroundUserContext.BeginScope`) with `actor = agent`.

## Tasks

- [x] 0. Spike: does a Fleet-owned tool dir work in `opencode serve`? (done 2026-09-12, see "Task 0 findings" at the end: Task 6 switches to a local plugin)
  - **What**: Use a scratch `HOME` and a standalone `opencode serve`, without Fleet. Adapt `OpenCodeFixture` and FakeLlmServer so the scripted model calls `fleet_canvas_open`. Check four things: (a) `OPENCODE_CONFIG_DIR=<dir>` with `<dir>/tools/fleet_canvas.ts` loads the tools *and* still loads `~/.config/opencode` (the user's `visualize.ts` and skills), so the dir adds to the user's config instead of replacing it; (b) `import { tool } from "@opencode-ai/plugin"` resolves in that dir, whether OpenCode installs it or Fleet has to write a `package.json`; (c) `context.sessionID` equals the id returned by `POST /session`; (d) for a `task` subagent call, `context.sessionID` is the child session and `GET /session/{id}` returns `parentID`.
  - **Output**: A short note appended to this plan. If (a) fails, fall back to adding the tool dir through `OPENCODE_CONFIG_CONTENT` or writing into the workspace's `.opencode`, and choose before Task 6.
  - **Depends on**: None.

- [x] 1. Domain types and migration (done 2026-09-12 on branch `feat/canvas-store`)
  - **Files**: `src/WeaveFleet.Domain/Entities/Canvas.cs`, `CanvasRevision.cs` (new); `src/WeaveFleet.Domain/Repositories/ICanvasRepository.cs` (new); `src/WeaveFleet.Infrastructure/Migrations/028_add_canvases.sql` (new); `src/WeaveFleet.Infrastructure/Data/Repositories/CanvasRepository.cs` (new), following `SessionRepository.cs`; register in `DependencyInjection.cs`.
  - **Acceptance**: Migration applies on a fresh DB and on a copy of a v027 DB. Deleting a session deletes its canvases and revisions. The repository writes a canvas row and its revision in one transaction, with `UPDATE … WHERE version = @expected`, so two concurrent writers are serialized: the loser retries against the new state.
  - **Tests**: `tests/WeaveFleet.Infrastructure.Tests` covers the repository round trip, cascade and version guard.
  - **Notes for Task 3**: Every query is scoped to the current user, and every canvas lookup to its session (`GetByIdAsync(sessionId, canvasId)`), so the service must run inside the binding's user scope. `TryUpdateAsync(canvas, expectedVersion, revision)` returns `false` on a lost race; the service reloads and retries. It writes only `state_json`, `version`, `agent_seen_version` and `updated_at`: kind and title are fixed, and reopening goes through `SetClosedAtAsync(…, null)` so a stale write can't undo a close. `agent_seen_version` never moves backwards, and `MarkAgentSeenAsync` caps it at the current version. `GetByTitleAsync` matches the exact title (the service trims), open or closed, most recently updated first. `ListRevisionsAsync(canvasId, afterVersion)` feeds the conflict rule.

- [x] 2. Change ops, validation and text rendering (done 2026-09-12 on branch `feat/canvas-store`)
  - **What**: Apply diagram and sequence ops to a `JsonNode` state. Add `DiagramStateValidator`, the conflict rule (removed-since-seen ids from revisions), a change summary for tool cards ("+2 boxes, −1 edge"), the compact text renderer for `full` reads, and the removed-items diff for default reads.
  - **Files**: `src/WeaveFleet.Application/Canvases/CanvasOps.cs`, `CanvasValidators.cs`, `CanvasConflicts.cs`, `CanvasText.cs` (new).
  - **Acceptance**: An agent `updateNode` on a box the user removed after `agent_seen_version` is refused with the one-line message. The same op after a read succeeds, including re-adding the id. A user `moveNode` never conflicts and never shows up in the agent's diff. Agent ops can't carry `x` or `y`.
  - **Tests**: `tests/WeaveFleet.Application.Tests/Canvases/`.
  - **As built**:
    - The state is a typed model (`CanvasStates.cs`) read from `JsonNode` and written with `Utf8JsonWriter`, not patched as a `JsonNode`. It's AOT-safe with no JSON context. Result and error types live in `CanvasTypes.cs`. `CanvasErrorKind` maps to HTTP later: `Invalid` 422, `UnknownId` 409 for user ops, `Refused` (agent only), `TooLarge`.
    - "The same op after a read succeeds" means the refusal no longer applies. An `updateNode` on the removed box then gets a plain unknown-id error, because the box is gone. Re-adding the id succeeds.
    - `CanvasOps.Open(kind, state)` turns the agent's state into agent ops applied to an empty canvas. Opening gets the same validation as a patch, and revision 1 records how the canvas was built. `setDirection` is included only when the direction isn't TB. A diagram with no boxes is rejected.
    - An applied user `removeNode` records the box's label and the edges it took (`removedLabel`, `removedEdges` in `ops_json`), so the refusal and the default read can name a box that's gone.
    - Parsing is strict. Unknown fields are rejected with the list of allowed ones, and positions get their own message. Ids are 1–64 of `[A-Za-z0-9_.:-]` and are unique across boxes and edges. Ops and state sent as a JSON string, and a single op object instead of an array, are accepted (Task 0 saw a model send `state` as a string). Direction and style match case-insensitively.
    - An unknown-id error on an agent op ends with "Call fleet_canvas_read to see the current diagram." A user op's doesn't.
    - The sequence summary is the line count, e.g. "14 lines".
  - **For Task 3: an agent write must not move `agent_seen_version` past user removals it hasn't read.** If it did, a patch that happens not to touch a removed box would silently use up the refusal. The agent would get "no box with id" later, and `fleet_canvas_list` would drop "(changed by user)". Rule: after an accepted agent write, set `agent_seen_version` to the new version only if `CanvasConflicts.UserRemovals` since the old seen version is empty. Otherwise keep the old value. Open and read always set it.

- [x] 3. `ICanvasService` and events (done 2026-09-12 on branch `feat/canvas-store`)
  - **What**: `ListAsync`, `GetAsync`, `OpenAsync`, `ReadAsync(full)`, `ApplyAsync(actor, ops)`, `FocusAsync`, `CloseAsync`. An agent open, apply or read sets `agent_seen_version`. Each accepted change broadcasts `CanvasUpdated` (`canvas.updated`: canvas id, kind, title, version, actor, full state including positions, summary). Close broadcasts `canvas.closed`, and focus broadcasts `canvas.focused`. Opening with an existing title reopens that canvas (Decision 4).
  - **Files**: `src/WeaveFleet.Application/Canvases/ICanvasService.cs`, `CanvasService.cs` (new); `src/WeaveFleet.Domain/Events/CanvasEvents.cs` (new) plus `[JsonDerivedType]` entries in `DomainEvent.cs`; source-gen registrations in the `JsonContext.cs` files.
  - **Acceptance**: The sequence agent open → user move → user remove → agent patch touching the removed box is refused → agent read → agent patch succeeds leaves the user's positions intact.
  - **Tests**: application tests for that sequence, and a SignalR contract test that asserts the exact `canvas.updated` JSON.
  - **As built**:
    - Wire shape: `{"type":"canvas.updated","eventId":null,"properties":{sessionId, canvasId, kind, title, version, actor, state, summary}}`. `canvas.closed` and `canvas.focused` carry `{sessionId, canvasId}`. The events aren't persisted and have no event id; the client loads canvases with `GET …/canvases` on session open (Task 8). `SessionEventsHub.ResolveDomainEventType` maps the three new types.
    - `canvas.updated` means "this canvas is open, and here it is". The client upserts the tab from it. Reopening sends it with summary `reopened`.
    - Opening an existing title computes the change as ops (`CanvasOps.Replace`), so boxes that stay keep the user's positions, and the conflict rule applies: an open that brings back a box the user removed is refused. The same state only focuses. A title that's already another kind is rejected. Titles are trimmed and 1–120 characters.
    - Agent writes follow the Task 2 rule. Open and read always mark the canvas seen, and a read marks only the version it read.
    - An agent patch on a closed canvas is refused ("the user closed … Call fleet_canvas_open with its title to reopen it"). Focus reopens a closed canvas. Close is idempotent.
    - Errors carry `CanvasErrorKind`; `NotFound` was added for a missing session or canvas. Lost write races retry up to 5 times.
    - Registered as `ICanvasService` (scoped) in `DependencyInjection.cs`. `InMemoryCanvasRepository` in `tests/WeaveFleet.Testing` mirrors the SQLite rules for application tests.

- [ ] 4. Client canvas endpoints
  - **What**: `GET /api/sessions/{id}/canvases`, `POST /api/sessions/{id}/canvases/{canvasId}/changes` (body `{ ops }`, user ops only, actor `user`), `DELETE /api/sessions/{id}/canvases/{canvasId}`. A user op on a box the agent has since removed returns 409, and the client refetches. The session-owner check matches the neighbouring session endpoints.
  - **Files**: `src/WeaveFleet.Api/Endpoints/CanvasEndpoints.cs` (new), mapped in `Program.cs`; contracts and `JsonContext.cs`.
  - **Tests**: integration tests for owner isolation (another user gets 404), 409 on a missing box, and 422 when an agent-only op is sent.

- [ ] 5. Agent bridge for pooled OpenCode
  - **What**: A token on `PooledOpenCodeInstance`, a token → instance lookup in `PooledOpenCodeInstanceRegistry` (constant-time compare), and a directory-less `TryGetBinding(instance, openCodeSessionId)`. An application-facing `IHarnessCanvasCallerResolver` returns `(fleetSessionId, userId)` or nothing, and does the child → parent walk. Bridge endpoints `POST /api/bridge/opencode/canvas/{list|open|read|patch|focus}` are `AllowAnonymous` but require a loopback address plus the bearer token, then call `ICanvasService` as `agent`. They return the plain-text outputs from the design.
  - **Files**: `Pooling/PooledOpenCodeInstance.cs`, `Pooling/PooledOpenCodeInstanceRegistry.cs`, `Pooling/PoolDemuxBindingTable.cs`, `OpenCode/OpenCodeCanvasCallerResolver.cs` (new); `src/WeaveFleet.Application/Canvases/IHarnessCanvasCallerResolver.cs` (new); `src/WeaveFleet.Api/Endpoints/CanvasBridgeEndpoints.cs` (new).
  - **Acceptance**: A token from instance A with a session bound to instance B returns 404. A session that stays bound after a lease moves (`MoveBindings`) resolves through the new instance's token. A request without a token from loopback returns 404.
  - **Tests**: infrastructure tests for the resolver, including the child-session walk against a fake `OpenCodeHttpClient`; integration tests for the bridge endpoints.

- [ ] 6. Ship `fleet_canvas.ts` and wire pooled processes
  - **What** (changed by Task 0): The source lives in the repo at `opencode/fleet/fleet-canvas.ts`, embedded in Infrastructure. It's a local OpenCode **plugin** with **no imports**: it exports one plugin function that returns `{ tool: { fleet_canvas_list, fleet_canvas_open, … } }`, and every `args` entry is a plain JSON Schema object. At startup Fleet writes it to `{AppData}/opencode/fleet-canvas.ts`, only when the content hash changes. `OpenCodeProcessManager` adds `"plugin": ["<file URI of that path>"]` to the `OPENCODE_CONFIG_CONTENT` it already sets. `CreatePooledInstanceAsync` adds `FLEET_URL` and `FLEET_BRIDGE_TOKEN`. No `OPENCODE_CONFIG_DIR`, no `package.json`. The tool descriptions carry the guidance sentence from the design.
  - **Files**: `opencode/fleet/fleet-canvas.ts` (new); `src/WeaveFleet.Infrastructure/Harnesses/OpenCode/OpenCodeFleetPlugin.cs` (new, writes the file); `OpenCodeProcessManager.cs:121`; `OpenCodeHarnessRuntime.cs:808`; `OpenCodeProcessOptions` if the env needs to be split out.
  - **Acceptance**: A live test built on `OpenCodeFixture` and FakeLlmServer: a pooled session's scripted `fleet_canvas_open` then `fleet_canvas_patch` produces a stored canvas at v2 and two `canvas.updated` events on `session:{id}`. It must use a scratch `HOME`. The user's own `plugin` entries still load.

- [ ] 7. Ask-agent context
  - **What**: On the client, an Ask-agent chip turns into `[Canvas "Session event flow" (cv_…) › box "use-sessions.ts" (n7)]` ahead of the message, next to `formatAnnotationPrompt`. That's the only way canvas context gets into a prompt, and only when you attach it.
  - **Files**: `client/src/lib/format-canvas-context.ts` (new).
  - **Tests**: a client unit test for the formatter.

- [ ] 8. Client: server-backed canvases and the Diagram canvas
  - **What**:
    - Store: load `GET …/canvases` when a session opens. Apply `canvas.updated`, `canvas.closed` and `canvas.focused` from the session topic. Server canvases sit next to Changes and Files. A new kind `diagram` maps to `DiagramCanvas.vue`, and `sequence` reuses the Mermaid renderer.
    - `DiagramCanvas.vue` builds on `VueFlowRenderer`. A drag end sends `moveNode`, and Remove sends `removeNode`. On 409, refetch and show a toast ("The agent removed that box"). It shows the version badge and a node popover with Ask agent and Remove. There's no unread pill (Decision 5).
    - A live dot on the tab after an agent `canvas.updated`, cleared on `turn.ended` or `session.idled`.
    - A canvas tool-card variant in `ToolCard.vue` for `fleet_canvas_*` tools, using the metadata title.
    - An Ask-agent chip on the composer.
    - Mock mode: seed one diagram canvas and apply user ops locally in `client/vite-plugin-mock-api.ts`.
  - **Files**: `client/src/stores/canvases.ts`, `client/src/lib/canvas-registry.ts`, `client/src/lib/domain-events.ts`, `client/src/lib/domain-event-reducer.ts` (or a canvas-specific handler), `client/src/components/canvas/DiagramCanvas.vue` (new), `client/src/components/session/ToolCard.vue`, the composer component, `client/vite-plugin-mock-api.ts`.
  - **Acceptance**: In mock mode (3099) you can drag, remove, see the version badge, and attach an Ask-agent chip. It matches the mockup in light and dark, apart from the dropped pill. `bunx vue-tsc --noEmit` and `bun run test` pass on Node 22 with `npm ci`, as in CI.
  - **Tests**: store reducer tests with real event payloads; a DiagramCanvas test that checks a drag sends one `moveNode` op.

- [ ] 9. End-to-end check
  - **What**: Run Fleet with a scratch `HOME` and pooled OpenCode on a real model. Ask for a diagram. Drag two boxes and remove one. Then ask it to add a push path. Check that your removed box stays removed and your positions stay. If the agent touched the removed box, check it got the refusal, read, and carried on. Check the turn's token usage against the same turn with no edits.
  - **Output**: screenshots in `mockups/canvas/`, next to the step 1 ones.

## Dependencies and order
Task 0 comes first because it decides how Task 6 installs the tool. Tasks 1 → 2 → 3 → 4 run in sequence. Task 5 needs 3. Task 6 needs 0 and 5. Task 7 is independent. Task 8 needs 3 for the event shapes, and its UI can start in mock mode in parallel with 4–6. Task 9 is last.

## Risks
- ~~**`OPENCODE_CONFIG_DIR` replaces the user's config instead of adding to it.**~~ Task 0 found it adds to the user's config, but Task 6 uses a plugin instead (Task 0 findings).
- **OpenCode changes how it loads plugin tools.** The import-free plugin relies on two 1.18.30 behaviors: plain JSON Schema `args` (the non-zod branch) and `file://` plugin specs. The Task 6 live test runs against the installed `opencode`, so an upgrade that breaks either fails that test.
- **The agent describes a diagram without reading it** and mentions a box you removed. The tool description tells it to read first; Task 9 checks whether real models follow that. If they don't, the fallback is a one-line note, and only when your message mentions or attaches a canvas.
- **Warm pool instances** started before Fleet upgrades don't have the env vars. The pool recycles on restart, and the tool returns a clear "restart Fleet" error if `FLEET_BRIDGE_TOKEN` is missing.
- **Pooled lease moves** (`LeasedInstanceHandle.ReconnectAsync`) move bindings to a new process, which has its own token and env, so resolution holds. A resolver test covers it.

## Task 0 findings (2026-09-12)
Standalone `opencode serve` 1.18.30 with a scratch `HOME` (and scratch `XDG_*`), no Fleet. A small Bun script stood in for the model and the bridge: it answered OpenAI chat completions and logged each request's tool list and every bridge call. The model came from a custom provider in `OPENCODE_CONFIG_CONTENT`, which needs no install and works offline: `"provider":{"fake":{"npm":"@ai-sdk/openai-compatible","options":{"baseURL":"http://127.0.0.1:<port>/v1","apiKey":"x"},"models":{"fake-model":{"tool_call":true}}}}` plus `"model":"fake/fake-model"`.

**The four questions all pass for `OPENCODE_CONFIG_DIR`:**
- (a) It adds to the user's config. The model's tool list had the user's `visualize` and `fleet_canvas_list`/`fleet_canvas_open`, and the user's skill was in the system prompt.
- (b) `@opencode-ai/plugin` resolves without Fleet doing anything. OpenCode runs a background `npm install` of `@opencode-ai/plugin@<its own version>` in every config dir, writing `package.json`, `package-lock.json`, `.gitignore` and `node_modules`. Three processes starting at once on an empty dir all loaded the tools. A warm restart doesn't reinstall.
- (c) `context.sessionID` equals the id from `POST /session`. `FLEET_URL` and `FLEET_BRIDGE_TOKEN` reach the tool through `process.env`. A returned `{ title, output, metadata }` lands on the tool part as-is (OpenCode adds `truncated` to the metadata).
- (d) In a `task` subagent, `context.sessionID` is the child session (agent `general`), and `GET /session/{child}` returns `parentID`. The parent's `task` tool part metadata also carries `sessionId` (the child) and `parentSessionId`.

**But the config dir is the wrong route.** The tool registry scans `{tool,tools}/*.{js,ts}` in every config dir, waits for that dir's dependency install if it found any files, then `import()`s each file with no error handling. With the npm registry unreachable:
- A tool file that imports `@opencode-ai/plugin` from a dir whose install never succeeded makes **every prompt in the process return 500**, after a 70 s wait.
- An import-free tool file loads, but a never-installed Fleet dir still costs **70 s on the first request of every process start**, until an install succeeds. The user's own dir is already installed on any machine where OpenCode has run online, so today there's no stall (0.15 s). The Fleet dir would add one.

**Decision for Task 6: a local plugin through `OPENCODE_CONFIG_CONTENT`.** `"plugin": ["file:///…/fleet-canvas.ts"]`, with the file exporting a plugin function that returns `{ tool: { fleet_canvas_open: {...}, ... } }`. Checked on this route: offline with the user's config installed, the first request took 0.15 s and there was no new dir or install. The user's `visualize`, skills and own `plugin` entries still load (OpenCode concatenates plugin lists). (c) and (d) behave the same, because plugin tools go through the same wrapper as tool files. A fresh `HOME` that has never been online still stalls 70 s on either route. That's existing OpenCode behavior for the user's own dir, not something Fleet adds.

**No imports in the plugin file.** If any `args` value isn't a zod schema, OpenCode builds the JSON Schema from the plain objects instead: `{ type: "object", properties: args, required: Object.keys(args) }`. The model receives it exactly as written. Two consequences:
- Every arg is required, so `fleet_canvas_read`'s `full` is a required boolean. The design table now says so.
- OpenCode doesn't validate the args. The spike's model sent `state` as a string where the schema said object, and it went through. Fleet validates everything on the bridge side anyway (Task 2).

**Notes for later tasks:**
- Task 6: build the plugin path with `new Uri(path).AbsoluteUri`. Windows `file:///C:/…` wasn't tested (the spike ran on Linux).
- Task 6: OpenCode's title generation also calls the model: the first request of a session has no tools. FakeLlmServer dequeues a scripted response for every request, so title generation would take one. The live test either answers tool-less requests without dequeuing or queues an extra response.
- `context.metadata({ title })` is available for a live tool-card title while a call runs.
- Using OpenCode's own tool file APIs was checked against the compiled 1.18.30 binary. If those change, the Task 6 live test catches it (see Risks).
