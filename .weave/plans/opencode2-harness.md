# OpenCode 2 harness

Add OpenCode 2 as a new Fleet harness, next to OpenCode (v1). Research, spike results and the V2 API/event notes:
`.weave/research/opencode2-harness.md`. Spike kit (real 2.0.x binary, scratch HOME, scripted model):
`~/.cache/opencode2-spike`.

## Decisions (user, 2026-09-18)

- **Alongside, never instead.** The OpenCode harness stays as it is; a large community still uses V1.
- **A new harness, built from scratch.** OpenCode 2 is not "OpenCode, newer". No shared base classes, no
  shared helpers, no "OpenCode family" abstraction, and nothing in `Harnesses/OpenCode2/` references
  `Harnesses/OpenCode/`. Copy code only when there's no reasonable alternative, and then it becomes V2 code
  that changes on its own schedule.
- **Install:** on a machine with V1 installed, keep V2 separate (own binary, config and database). On a
  machine without V1, install V2 where OpenCode's installer puts it by default.

## Rules for the build

- Type id `opencode2`, display name "OpenCode 2". Code in `src/WeaveFleet.Infrastructure/Harnesses/OpenCode2/`,
  `OpenCode2`-prefixed types, its own JSON context, its own named `HttpClient`.
- Plug in only through the existing harness seams: `IHarness`, `IHarnessRuntime`, `IHarnessSession`, and the
  domain events the other harnesses emit. If a seam doesn't fit, change the seam for everyone, don't add a
  V2-only side channel.
- Harness-neutral helpers that Claude Code and Pi already use (`HarnessProbe`, `ExecutableResolver`,
  `ProcessGroupHelper`) are fair game; they aren't OpenCode code.
- Shared code that says `"opencode"` (message proxy, warmup, pool recycler, built-in skill catalog) stays
  OpenCode's. V2 gets its own implementation, or does without, not a branch in OpenCode's code.
- Every stage is checked live against the real binary with the scripted model before its PR. Never against
  the real HOME.

## Stage 0 — protect the OpenCode harness (independent, ship first)

Anyone who runs the V2 installer today replaces V1's `opencode`, and Fleet's OpenCode harness would then
report it "ready" and fail every call.

- [x] OpenCode harness rejects a 2.x `opencode`: state `not-working` with the reason, and
      `OpenCodeProcessManager` refuses to launch it (warmup + sessions). All in `OpenCodeExecutable`, no shared
      interface change. PR #250 (branch `fix/opencode-rejects-v2`), checked live on a scratch Fleet with 2.0.8.
      When the OpenCode 2 harness ships, change the message to point at it.

Size: ½ day.

## Stage 1 — a session that talks (text only)

- [x] `OpenCode2Harness` descriptor. Capabilities start minimal (streaming, resume) and grow per stage.
- [x] `OpenCode2HarnessRuntime.CheckAvailabilityAsync`: find the executable (see Stage 5 for where),
      `--version` must be 2.x.
- [x] Server process: Fleet starts `opencode serve --port <free> --hostname 127.0.0.1` with
      `OPENCODE_SERVER_PASSWORD` (random per process), `FLEET_URL`, bridge token. **One server per owner (and
      later per profile), for every directory**: V2 handles directories itself through `location`. No pool
      keyed by directory.
- [x] One SSE subscription per server (`/api/event`), routed to sessions by `data.sessionID`.
- [x] Session: create with `location.directory`; prompt; interrupt; events
      `session.execution.started/succeeded/interrupted/failed` → busy/idle/turn failed,
      `session.text.started/delta/ended` and `session.reasoning.*` → message parts,
      `session.step.ended` / `session.usage.updated` → tokens and cost, model from `session.step.started`.
- [x] Resume token = V2 session id; resume re-attaches to the owner's server.
- [x] Registered in DI, off by default (`opencode2.enabled`), client entry in `harness-display.ts`.

Live check: new session → prompt → streamed reply → idle; stop Fleet, restart, same session answers again.
Size: 4–5 days.

Built (branch `feat/opencode2-harness`), checked live on a scratch Fleet with 2.0.8 + the scripted model: streamed
reply → idle, interrupt mid-reply, Fleet restart → the same session answers, an OpenCode 1 session beside it.

What Stage 1 learned:

- `opencode2 serve --port 0 --hostname 127.0.0.1` picks its own port and prints
  `server listening on http://127.0.0.1:PORT` on stdout; Fleet reads that line instead of choosing a port.
- Fleet runs `opencode2` from PATH or `~/.opencode/bin` and checks `--version` is 2.x both in the availability
  check and again before starting a server (it runs on the user's OpenCode data).
- `POST /api/session/{id}/prompt` takes `id`: Fleet passes its own `msg_…` id for the user message, same form as V2's.
- Mapping: `session.step.started` → assistant `message.updated` (agent, model, provider) + `step-start` part;
  text/reasoning part ids are `{assistantMessageID}-text-{ordinal}` / `-reasoning-{ordinal}` (Stage 2's history must
  derive the same ids); `session.step.ended`/`step.failed` → completed `message.updated` + `step-finish` part and one
  token analytics event per step. `session.usage.updated` is the session's running total, so it's not counted.
  `session.execution.failed` → `session.error` (V2's `{type, message}` → Fleet's `{name, message}`) + `session.idle`;
  `session.retry.scheduled` → retry status (`at` is epoch ms, turned into a delay). Everything else maps to nothing.
- Fleet's part payloads are read polymorphically: `"type"` must be the first property, or the part silently drops
  out of the domain events (caught by the translator test, the same trap the Claude Code harness hit).
- A session whose server stopped re-attaches on its next request to the owner's new server (after
  `GET /api/session/{id}`), so a dead server needs no orchestrator change. A turn running when the server stops ends
  with a "server stopped" failure + idle. Stopping a Fleet session interrupts its V2 turn if one is running.
- Shared change: `FLEET_URL` is `{fleet}/agent/{bridge token}` for V2, and `AgentRequests` now asks every registered
  `IHarnessBridgeTokens` (one per harness), not just OpenCode's.
- The Stage 0 message now points 2.x users at the OpenCode 2 harness.
- **Gap until Stage 2:** reopening an OpenCode 2 session (page reload, Fleet restart) shows only prompts Fleet saved
  itself, not the replies, because the message proxy reads live history only for `opencode`. The next prompt works.
- **Gap until Stage 2:** the SSE stream reconnects after a drop, but nothing re-reads `/api/session/active` then, so a
  turn that ended during the gap stays busy until the next turn.

## Stage 2 — tools, questions, permissions, history

- [x] Tool events `session.tool.input.started`, `session.tool.called`, `session.tool.success`/`failed` → tool parts
      (text content blocks → output, file content → file parts). `session.tool.input.ended`/`progress` map to nothing.
- [x] Client: labels, icons and cards for V2's tool names in the client's own switches (`shell`, `subagent`, `edit`,
      `read`, `glob`, `grep`, `webfetch`, `websearch`, `question`, `execute`; also `write`, `skill`), next to the V1
      cases. No translating V2 names back into V1 names on the server.
- [x] Questions: `form.created` → waiting for you; answer → `POST /form/{id}/reply {"answer":{key:value}}`;
      dismiss → `DELETE /form/{id}`; `form.replied`/`form.cancelled` → busy again.
- [x] Permissions: sessions are created with an allow-all `permissions` ruleset. Any `permission.asked` that still
      arrives is answered `once` and logged, for Fleet's sessions and for sessions no Fleet session listens to.
- [x] History for reopening a session: `OpenCode2HarnessSession.GetMessagesAsync` reads `GET /api/session/{id}/message`
      and returns domain `HarnessMessage`s. `OpenCodeSessionMessageProxy`'s three `"opencode"` gates are now
      `HarnessCapabilities.HistoryLivesInHarness` (OpenCode, OpenCode 2, and the E2E TestHarness that stands in for
      OpenCode). No V2 code in the proxy.
- [x] Reconnect: when the event stream comes back, the server reads `/api/session/active` once and each attached
      session catches up (messages of a turn that ran, open questions, busy/idle) before newer events are delivered.

Built (branch `feat/opencode2-stage2`, stacked on Stage 1), checked live on a scratch Fleet with 2.0.8 + the scripted
model: shell/read/failed-read cards, a question answered from the card (the session shows "Needs input" meanwhile),
one dismissed, interrupt mid-tool, a subagent, forced permission asks (a Fleet session and an unattached one), page
reload and Fleet restart (the conversation text is identical), the event stream dropped mid-reply for 20 s, the V2
server killed mid-reply, and an OpenCode 1 session running a tool beside it.

What Stage 2 learned:

- Part ids: V2 numbers text and reasoning separately within a step (`ordinal` is a per-kind counter in V2's
  source), and history lists an assistant message's content in stream order, so history derives
  `{msg}-text-{n}` / `-reasoning-{n}` by counting per kind. Tool parts are `{msg}-tool-{callID}` (history has the call
  id); a prompt's text is `{msg}-text-0`, the id Fleet shows its own prompt with. Step-finish parts can't match:
  live they're indexed by the session-wide step counter the translator uses as the turn index, and history can't know
  it. The client only sums their cost, so this doesn't show.
- A V2 assistant message is one step; history gives each finished one a step-finish (tokens, cost, reason). An
  interrupted step carries `error: {type: "aborted"}`, which history does not show as a failure (live doesn't either).
- `GET /message` sends a `cursor.next` on the last page too; a page shorter than the limit is the last. It takes
  `order` or `cursor`, not both.
- `form.created` carries the session id in `data.form.sessionID`, not `data.sessionID`; the server's routing reads both.
- The question tool's input (`questions[{question, header, options[{label, description}]}]`) and its completed
  metadata (`answers: [["B"]]`) have OpenCode's (1.x) shapes, so the existing question card works unchanged. The form
  reply sends an option's `value` (same as its label for the question tool) or the typed text.
- Dismissing a question makes V2 fail the question tool ("The user dismissed this question") and interrupt the turn
  (`reason: "shutdown"`). It doesn't continue like OpenCode (1.x) does after a rejected question.
- The allow-all session ruleset beats the user's config, even a specific `{"bash": {"echo *": "ask"}}`. A subagent's
  child session inherits it. So in practice no ask arrives; the answer-once path was checked live by removing a
  session's rules (`PATCH /api/session/{id} {"permissions":[]}`) and with a V2 session Fleet doesn't know.
- Waiting-for-you: the session's activity check reads open question forms too, so a session reopened while V2 waits on
  a question shows "Needs input" (V2 keeps the form; Fleet hadn't seen it). Statuses go through the relay's raw
  `session.status` handling, not the translator.
- Fleet's scratch kit puts a TCP relay in front of V2 (`relay-shim.py`, `fleet.sh dropstream`) to drop the event
  stream without stopping V2; `fleet.sh killv2` kills the server.
- **Left for later stages:** a subagent's child session isn't shown (its tool card only; Stage 4), and a question or
  form a child session asks isn't routed to Fleet, so it would wait until the turn is interrupted (Stage 4). Forms that
  aren't questions (`metadata.kind` other than `question`) are ignored. Gaps aren't detected from `durable.seq`; Fleet
  catches up on every reconnect instead, which is simpler and covers the same case. Tool `progress` metadata (a
  subagent's child session id, a shell's id) isn't used yet.

## Stage 3 — Fleet's own tools in V2

- [x] New plugin `opencode2/fleet/index.js` (a directory; V2 won't load a plugin file path), written against
      the V2 plugin API: `export default { id: "fleet", setup(ctx) { ctx.tool.transform(...) } }`, no imports.
      Tools: canvas list/open/read/patch/focus, app start, browser open, browser screenshot. Each tool sets
      `options: { codemode: false }` or V2 only exposes it inside Code Mode. Session id comes from the
      executor's second argument (`tool.sessionID`).
      Copying the tool descriptions and the bridge `fetch` from `opencode/fleet/fleet-canvas.ts` is fine;
      the file itself stays separate.
- [x] Loaded via `OPENCODE_CONFIG_CONTENT` `plugins`, the same variable mechanism, V2's own content.
- [x] `OpenCode2CanvasCallerResolver` (token → server, V2 session id → Fleet session).
      `CanvasBridge` and `BrowserBridge` currently take a single `IHarnessCanvasCallerResolver`; change them
      to ask every registered resolver (tokens are unique per process). This is the one shared change.
- [x] Skills: Fleet's skill folders via `skills` in the injected config.
- Todos: not in the first version (`ReportsTodos = false`); see Later.

Live check: agent opens a canvas, takes a browser screenshot (image comes back), app start.
Size: 3–4 days.

Built (branch `feat/opencode2-tools`), checked live on a scratch Fleet with 2.0.8 + the scripted model: a canvas opened
and patched (shown in the right panel), an app started in a browser canvas, a browser screenshot whose image reached the
model, `fleet-code-review` offered when switched on and gone when switched off, the same session's tools after a Fleet
restart and after the V2 server was killed, a user's own V2 plugin and skill loading beside Fleet's, `fleet_message`
offered with messages between sessions on, and an OpenCode 1 session drawing its canvas beside it.

What Stage 3 learned:

- V2 adds `plugins` and `skills` from `OPENCODE_CONFIG_CONTENT` to the user's own (config file and `.opencode/plugins/`,
  `.opencode/skills/`): unlike OpenCode (1.x), arrays aren't replaced, so Fleet can put its skill folders straight into
  the config instead of adding them from the plugin.
- A plugin tool returns `{ title, content: [{type: "text", text}, {type: "file", uri: "data:…", mime, name}], metadata }`.
  V2 sends the file to an OpenAI-compatible model as a user message with `image_url` after the tool result, as
  OpenCode (1.x) does, and to Fleet as a file part (Stage 2's mapping), so a V2 screenshot shows as an image in the
  conversation; OpenCode (1.x) sessions don't show it. A thrown `Error` fails the tool with its message.
- The executor's second argument has `sessionID, agent, messageID, id, progress`, and no permission ask. Built-in tools
  declare `options.permission` (V2's shell tool is `shell`), and V2 leaves a tool out when the agent's rules deny that
  permission; `fleet_app_start` declares `permission: "shell"` in place of OpenCode (1.x)'s `ask`. Fleet's allow-all
  session ruleset still allows it everywhere else, as before.
- The tools reach Fleet under `FLEET_URL` = `/agent/{token}` (Stage 1), so they work with messages between sessions
  off too.
- One server per owner means per-owner settings (built-in skills, messages between sessions) are server settings. The
  runtime works out what the owner's server should start with on every spawn/resume and replaces a server started with
  other settings once `/api/session/active` is empty; until then the owner keeps the old one. A prompt sent to an idle
  session in the moment of the replacement would fail as "server stopped"; not seen live.
- Fleet writes its own copy of the skill files under `{data}/opencode2/` from the same embedded `opencode/skills` and
  `opencode/built-in-skills` resources (the ~30 lines that write embedded files are copied from the OpenCode adapter,
  per the no-shared-code rule).
- The skill manifest's targets (`BundledSkillsHostedService`, `SkillManifestMigrator`: `["opencode", "claude-code"]`)
  aren't how built-in skills reach sessions: those go through the per-owner switch and the injected `skills`. The
  manifest syncs the user's skills into `~/.config/opencode/skills`, which V2 reads too while it shares OpenCode's
  config folder, so no `opencode2` target is needed yet. Stage 5's separate mode (own config folder) needs one.
- V2 looks for `.claude/skills` in the session's folder and every parent, including the real HOME's when the folder is
  under it (read only).
- Scratch kit: Chrome refuses a TMPDIR whose socket path is over ~108 characters, so the scratch Fleet's TMPDIR is short.
- **Left for later:** sending a message with `fleet_message` from V2 wasn't exercised live (the tool is offered; the
  bridge is the shared one). A subagent's call follows `parentID` to the Fleet session (unit-tested; live with Stage 4's
  subagents). No capability flag was needed: nothing gates Fleet's tools by harness name.

## Stage 4 — catalog, agents/models, profiles, subagents, recap

- [x] Catalog (agents, models, commands, providers) from `/api/agent`, `/api/model`, `/api/command`,
      `/api/provider`. V2 loads a directory lazily and returns `[]` until then: create a throwaway
      location load (e.g. `GET /api/location?location[directory]=…`) before listing. Verify this in the spike first.
- [x] Agent/model choice per session (`agent`, `model` on create; `/agent`, `/model` switch).
- [x] Profiles: one server per (owner, profile version). The profile reaches V2 as `OPENCODE_CONFIG` (a file), not
      through `OPENCODE_CONFIG_CONTENT`; see "Profiles" below. The check starts a throwaway server with the content.
- [x] Subagents: `subagent` tool + child session (`parentID`) → Fleet delegation events; child events
      routed to the parent's view.
- [x] Off the record (recap): `POST /api/session/{id}/generate`.
- [x] Commands: `POST /api/session/{id}/command`. Fork: not built (see below).

Size: 4–5 days.

Built as Track B (branch `feat/opencode2-agents`, everything but profiles), checked live on a scratch Fleet with 2.0.8 +
the scripted model: the new-session composer lists V2's agents (the user's and the folder's own) and models, a session
started on a non-default agent and model answers with them, switching both mid-session, a slash command, the
welcome-back recap, a subagent (delegation, child activity, parent finishes), a subagent that asks a question
(answered in the child, child and parent finish), reload and Fleet restart with subagents in the history, and an
OpenCode 1 session beside it.

What Stage 4 learned:

- **Loading a folder is asynchronous.** `GET /api/location` returns before V2 has read the folder's config; lists read
  in the next ~2 s leave out the user's and the folder's agents, models and commands (the spike's first "before/after"
  looked fine only because the "before" reads had started the load). Neither `/api/debug/location` nor
  `POST /api/location/reload` says when it's done. V2 does: it sends `provider.updated`, `model.updated`,
  `agent.updated` and `command.updated` with `location.directory` once the folder is loaded. The server remembers
  folders it has seen loaded (also by a session there); a catalog read of a new folder waits for those events, 15 s at
  most, then lists what V2 has.
- Catalog shapes: an agent's `id` is what a session switches to (`name` is a label; there's no description). Models
  come from `/api/model` (`id` is the selectable one, `variants[].id` are Fleet's effort levels), grouped under
  `/api/provider` entries that aren't `activation: disabled`. The default model is `/api/model/default`; the default
  agent is the last `default_agent` in `/api/config`'s documents (global first, the folder's own last), else `build`,
  else the first visible primary agent — V2's own rule. `/api/command` has names and descriptions.
- **Agent and model live on the session, not the prompt**, and `prompt` takes neither. Fleet switches the session with
  `POST /agent` / `/model` before a prompt or command when the choice differs from what the session has (read from the
  session on resume, and from `session.agent.selected` / `session.model.selected`). V2 accepts an unknown agent or
  variant with 204 and fails the next turn ("Agent not found"), so the choice has to come from the catalog. A model
  selected without a variant reads back as variant `default`.
- A folder's agents only exist where its config is: a session the composer puts in a **new worktree** gets the
  worktree's config, so an agent defined in an uncommitted `opencode.json` is missing there and the turn fails (the
  catalog reads the repository's folder, as for OpenCode). Same behaviour as OpenCode; noted, not changed.
- Subagents: the parent's `subagent` tool call (`tool.called` input: `agent`, `description`; `tool.progress` and the
  result's metadata: the child's `sessionID`) is Fleet's delegation, through the same `DelegationService` and
  `SessionOrchestrator.EnsureDelegatedChildSessionAsync` OpenCode uses. The child is resumed on the owner's server like
  any session. Updates are handled one at a time in arrival order (the #242 race). The child's `session.created`
  (`parentID` = an attached or held session) makes the server **hold the child's events until Fleet attaches it**, then
  deliver them in order (the #246 race); a held child's permission asks are still answered at once. So a child's
  question reaches its Fleet session, which shows it and answers the form on the child.
- `generate` answers from the session's conversation with its agent and model, adds nothing to the history and emits no
  execution events. It answers an empty session too.
- `command` expands the template into a user message (Fleet shows `/hello the world` live; the reloaded history shows
  V2's expanded text, as OpenCode's does). An unknown command is 404 `CommandNotFoundError`.
- **Fork:** Fleet's Fork starts a new session in the same folder for every harness and never calls the harness's fork,
  so there's nothing to build; `SupportsForking` stays off.
- **Left for later:** a parent whose child waits on a question still reads "Working", not "Needs you": the shared
  `SessionActivityTracker.GetEffectiveActivityStatus` lets the parent's own busy win over a waiting child (the child
  itself shows "Needs you"). That's shared code, not the harness. A folder V2 evicts after it was loaded isn't noticed,
  so the next catalog read of it doesn't wait. A child created while the event stream was down isn't linked until its
  parent's next subagent update. Profiles.

## Stage 5 — setup, install modes, updates

- [x] **Default mode** (no V1 `opencode` found): setup types OpenCode's installer into the terminal,
      `curl -fsSL https://opencode.ai/v2/install | bash`, per the harness-setup rule that Fleet never
      downloads harnesses itself. V2 then lives at `~/.opencode/bin/opencode` (+ `opencode2`), with default
      config and database. A V2 the user installed themselves is used as-is.
- [x] **Separate mode** (a V1 `opencode` is installed): the same installer, with a different HOME and without
      shell-file edits:
      `curl -fsSL https://opencode.ai/v2/install | HOME=~/.weave/harnesses/opencode2 bash -s -- --no-modify-path`.
      Verified: binary lands in `~/.weave/harnesses/opencode2/.opencode/bin/`, V1 untouched, no rc edits.
      Fleet starts it with `OPENCODE_CONFIG_DIR=~/.weave/harnesses/opencode2/config` and
      `OPENCODE_DB=~/.weave/harnesses/opencode2/data/opencode.db` (verified: V1's DB unchanged, V1's global
      config not read). Separate mode needs its own provider sign-in. The setup screen says so, and that
      repo-level `opencode.json` / `.opencode/` are still read by both.
- [x] Which mode: decided at setup and remembered (a V1 installed later must not flip an existing V2 into
      default mode, or its sessions move databases).
- [x] Skills: a skill for OpenCode also goes to a separate install's config folder (`opencode2` skill target).
- [x] Windows: V2's installer is bash-only and package managers aren't supported. Setup shows the manual
      download for now.
- [x] Updates: `LatestVersionPackage = "@opencode/cli"`, `MinimumVersion` from the oldest version the live
      tests pass on; update = re-run the installer (same mode, same HOME) when no session is working.
- [x] Settings → Harnesses: OpenCode 2's card shows the install (mode, version, folders, what to know, sign-in)
      instead of "No settings yet".

Built as Track C (branch `feat/opencode2-setup`), checked live on a scratch Fleet with the real installer (network) and
the scripted model, every install inside the scratch HOME: with no OpenCode 1, setup typed the default command, V2
2.0.8 installed from the setup terminal, the harness turned ready without a restart and a session answered; the update
strip took it to 2.0.9 in default mode (servers restarted on 2.0.9, a new session answered); OpenCode 1 installed
afterwards (npm-style in `~/.local/bin`, then OpenCode 1's own installer) left the remembered mode at default, and after
the installer replaced V2 Fleet said so. With OpenCode 1 installed, setup typed the separate command, V2 landed under
the scratch `~/.weave/harnesses/opencode2`, a V2 session answered from the separate database (with the scripted model
set up in its own config folder), a user skill installed for OpenCode reached its config folder and V2 listed it, and
OpenCode 1's database, config and tables were byte-identical afterwards. 2.0.5 read as too old; 2.0.6 answered after a
Fleet restart; the update strip took it to 2.0.9 in separate mode with the same HOME; a session from before the restart
answered again; an OpenCode 1 session answered beside it. Not checked live: Windows (the manual download), a real
provider sign-in (the command's environment was checked with `debug paths` and `auth list --standalone`).

What Stage 5 learned:

- **The mode is remembered in `~/.weave/harnesses/opencode2/install-mode`** (`default` or `separate`), written the first
  time Fleet finds a working 2.x (availability check or server start). Until then a machine with an `opencode` that
  isn't OpenCode 2 gets separate mode; one that won't say its version counts as OpenCode 1 too, so V2's installer never
  overwrites it. With a remembered mode only that mode's executable counts: separate looks only in its own
  `.opencode/bin`, default on PATH, `~/.opencode/bin` and the usual user folders. Without one, a separate install wins
  over a default one, and an `opencode2` that runs OpenCode 1 decides nothing (`OpenCode2Install`).
- **OpenCode 1's installer replaces a default-mode V2.** Both install as `~/.opencode/bin/opencode`, and V2's `opencode2`
  is only a shim (`exec "$(dirname "$0")/opencode"`), so after `curl -fsSL https://opencode.ai/install | bash` the
  shim runs 1.x. The remembered mode stays default, Fleet says why ("OpenCode 1's installer replaced OpenCode 2 in
  ~/.opencode/bin, where both install"), and setup notes that installing V2 again replaces OpenCode 1 there. An
  OpenCode 1 installed elsewhere (npm) leaves V2 alone and the mode doesn't change. Moving a default install to
  separate mode is not built (its sessions would move databases).
- **Minimum version 2.0.6.** `.poc-runtime/v2smoke.py` installs a release with V2's installer into a scratch HOME and
  checks every endpoint and event the adapter uses (43 checks: info, event stream, folder load events, catalog, session
  create with location + permissions, prompt with id, text/step/tool events, history order, question form, subagent
  child `parentID`, generate, agent/model switch + selected events, command, active sessions, interrupt). 2.0.6–2.0.9
  pass all 43. 2.0.0–2.0.5 have no `/api/info` (a server start calls it, so Fleet couldn't use them at all);
  2.0.0–2.0.3 also want `command` where Fleet sends `name`.
- `@opencode/cli` on npm (2.0.9 on 2026-09-19) runs ahead of the installer's own latest
  (`opencode.ai/update/api/latest/cli/npm`, 2.0.8 then), so an update passes `--version` from npm. The installer takes
  `--version`, and its `--no-modify-path` keeps an update from editing shell files. Fleet runs it with `bash -c`
  (stdin closed, like #248's updaters), only for an executable in the mode's own `.opencode/bin`; a V2 from npm or
  Homebrew isn't Fleet's to update. After an update idle servers stop and the next request starts one on the new
  binary; a busy one is replaced once it's idle.
- **Sign-ins don't carry over.** A new separate database doesn't pick up OpenCode 1's `auth.json` (2.0.9, checked with
  `opencode2 auth list`), so the setup and settings notes say to sign in again; the settings panel shows
  `OPENCODE_CONFIG_DIR=… OPENCODE_DB=… opencode2 auth login --standalone`. Keys in environment variables work in both.
- **V2's CLI runs through one background service per machine.** `auth`, like other CLI commands, starts or reuses a
  `serve --service` process on a fixed port (49374) and leaves it running. One started with another database (a
  default install, or another HOME) would take a separate install's sign-in, or the command times out when the port is
  held. `--standalone` (in 2.0.6+) runs a private server for the command instead, so the separate sign-in uses it.
- The separate server's environment (`OPENCODE_CONFIG_DIR`, `OPENCODE_DB`) reaches the agent's shell tool, so an agent
  that runs `opencode` inside a separate-mode V2 session would point it at V2's database. Not handled.
- Skills: `HarnessInstallPaths` has an `opencode2` target (`~/.weave/harnesses/opencode2/config` globally, `.opencode`
  in a repository). A skill for OpenCode goes there too once that folder exists (`SkillTargets`), so older manifest
  entries and user-installed skills (which name only `opencode`) reach it, and a default install, which reads
  `~/.config/opencode`, doesn't get a useless copy. A repository's `.opencode/skills` is written once for both. The
  first separate-mode server Fleet starts copies the existing skills there (`ISkillSyncEngine.SyncHarnessAsync`), since
  the install can be newer than the skills. The bundled/migrator target lists stay as they were: the rule applies at
  sync time instead.
- Seams: `HarnessSetup` gained `DownloadUrl`, `Mode`, `Folders` and `Notes` for every harness; setup lists a harness
  with only a download and shows its notes until it's ready, and Settings shows the install panel for any harness that
  describes its mode. OpenCode 2's card doesn't copy OpenCode's pooled mode or warmup.
- Logs, shell output and **snapshots** of a separate install still go to `~/.local/share/opencode`: V2 runs its
  `write-tree` in the same `snapshot/<project>/<hash>` git folder OpenCode 1 uses for the folder (content-addressed, so
  harmless so far). Settings lists that folder as shared with OpenCode 1.
- In default mode, OpenCode 1's setup row still offers OpenCode 1's installer (Stage 0's message says it puts OpenCode 1
  back in place of OpenCode 2); unchanged here.
- **Left for later:** a way to move a default install to separate mode; a Windows install path beyond the manual
  download (and Windows updates); new skills reaching a running separate server needs a server restart (as in default
  mode).

## Stage 6 — tests

- [x] Unit: event mapping from recorded V2 SSE (`events*.sse` from the spike), forms, message snapshots. They landed
      with Stages 1–4. The one gap was a session whose server stops: now tested with a turn running and with none.
- [x] Conformance fixture `OpenCode2Fixture` in `WeaveFleet.ConformanceTests`.
- [x] Live tests with the real binary + `FakeLlmServer`, scratch HOME, pinned 2.0.x in CI (like PR #242).

Size: 3 days (spread across the stages; tests land with each stage's PR).

Built as Track D (branch `test/opencode2-live-tests`, rebased on Stage 5). CI installs OpenCode 2.0.9 next to OpenCode
1.18.31 in Stage 5's separate mode, checks each reports its pinned version, and runs the conformance tests and the
`[OpenCode2Fact]` live tests with `FLEET_REQUIRE_OPENCODE2=1`. Checked locally on 2.0.0, 2.0.4, 2.0.5, 2.0.6, 2.0.8 and 2.0.9 through a scratch-HOME script,
plus OpenCode 1's live tests beside them.

What Stage 6 learned:

- **`MinimumVersion` 2.0.6** (Stage 5 set it; the live tests agree). They pass on 2.0.6, 2.0.8 and 2.0.9. On 2.0.0–2.0.5
  Fleet can't start a server: `GET /api/info`, the check that the password works, is 404 there. The conformance tests
  don't call it and pass on all of them.
- **Installing V2 next to V1 in CI:** V2's installer writes `~/.opencode/bin/opencode` (V1's binary) and, when
  `GITHUB_ACTIONS` is set, adds its folder to `$GITHUB_PATH` even with `--no-modify-path`, which would put V2's `opencode`
  ahead of V1's in later steps. CI runs it as separate mode's setup does (`HOME=~/.weave/harnesses/opencode2`) and without
  `GITHUB_ACTIONS`, so the harness finds it through its own discovery; the test fixtures use the same
  `OpenCode2Install.Locate()`. The live tests put the separate install's config folder and database
  (`OPENCODE_CONFIG_DIR`, `OPENCODE_DB`) on scratch folders too.
- The live tests (`tests/WeaveFleet.IntegrationTests/Harnesses/OpenCode2/`) share one Fleet on Kestrel and one V2 server
  per class, as Fleet runs one server per owner, and each test has sessions and folders of its own. They drive Fleet
  through the orchestrator, as the client does: a text turn, a tool card (running, then its output), a question answered
  from its card, a reopened session equal to the live stream (text and tool parts, compared as JSON), `fleet_canvas_open`,
  a subagent whose child asks a question, the agent/model switch kept by the session, a turn that ends with a failure
  when the server is killed (and the next prompt answered by a new server), and Fleet stopping its server when it stops.
  About 30 s locally.
- The model answers by what it's asked (`ScriptedResponseStore.Fallback`), since sessions share it. V2 gives a subagent
  its prompt after instructions of its own ("You are a subagent spawned by another session…"), in the child's last user
  message. Title requests offer no tools and get `ToolLessResponse`.
- Test seam: `OpenCode2HarnessRuntime.ServerEnvironment` (internal) sets variables every server starts with, over the
  install's and under Fleet's own. The live tests give V2 a scratch HOME this way. Empty in Fleet.
- Fleet broadcasts V2 sessions' raw harness types (`session.status` busy, `session.idle`, `session.error`), not
  `turn.*`. The shared conformance suite listened for `session.busy`, which no harness sends, and its
  `SendPromptAndWaitAsync` didn't wait. It never ran anywhere: the project isn't a `dotnet test` project and CI didn't run
  it. Fixed for every harness: busy is `session.status` `busy`, and the helper waits for `session.idle`. A harness can
  skip a shared test with its reason (`NotApplicable`). V2 skips four: its session and resume token exist before the
  first prompt, so there's no `session.created` on a prompt, and it streams assistant messages only, so there's no
  `message.created` for the user's. CI runs only V2's conformance tests. OpenCode 1's fixture starts `opencode` with the
  real HOME, so it wasn't run here (locally that's the user's data) and stays out of CI; wiring it in is for later.
- **Harness fix:** stopping a V2 server always waited 5 s. `setpgid` fails once the server has exec'd (errno 13), so the
  group kill misses (errno 3), and the tree was killed only after the wait. A Fleet that exited in those 5 s left
  `opencode2 serve` running on the user's database. The tree is now killed straight away. The shared
  `ProcessGroupHelper` has the same `setpgid` race for every harness. Not changed here.
- **Left for later:** an OpenCode 1 session and a V2 session in one Fleet aren't in one test. CI runs both harnesses'
  live tests in the same run instead. Todos, profiles and images stay out of the live tests until they exist.

## Follow-up decisions (2026-09-18)

- **Todos:** left out of the first version; V2 sessions have no Progress list.
- **History on reopen:** OpenCode's approach, through the capability flag (Stage 2).

## Gaps closed (Track E)

- [x] **Image attachments.** A prompt's pasted images go to V2 as prompt `files` with inline `data:` URIs
      (`PromptInput.FileAttachment`); `SupportsImageAttachments` is on. An `@` reference stays plain text in the
      prompt, as for every harness (the agent reads the path).
- [x] **File writes.** A finished `edit` or `write` call reports its file as Fleet's `files.written` and
      `file.watcher.updated` (which Fleet turns into `files.changed`), so open editor tabs and the Files canvas reload
      during the turn. `ReportsFileWrites` is on.
- [x] **A parent shows "Needs you" while its subagent waits on a question**, for every harness (shared code).

Built (branch `feat/opencode2-gaps`), checked live on a scratch Fleet with 2.0.8 + the scripted model: a pasted image
and an `@note.txt` reference in one V2 prompt (the model got the text and an `image_url`; the image is on the prompt
after a reload), an edit to a file open in an editor tab and a new file shown in the Files canvas (both while the turn
was still running, `files.changed` on the wire), and a subagent's question on a V2 parent and on an OpenCode 1 parent
(`task`): the list and header read "Needs input", the list went `busy → waiting_input → busy → idle`, and one
"needs you" notification was sent, for the parent.

What Track E learned:

- **Attachments:** `prompt` takes `files: [{uri, name}]`; a `data:` URI works (HTTP URLs don't). V2 reads the bytes
  before admitting the prompt and sends a PNG/JPEG/GIF/WebP to an OpenAI-compatible model as `image_url`. The user
  message keeps every attachment inline (`files[].data` + `mime`, `source: inline`), so history shows it with no extra
  request. V2 detects the type from the bytes, not the given mime. Fleet's `@` references were never attachments (the
  composer sends them as text for every harness), so nothing changes for them.
- **File events:** V2 has a file watcher (`filesystem.changed {file, event}`) and a `file.edited` event, but neither is
  sent on `/api/event` (not seen with edit, write, or a file changed on disk). The `edit` and `write` tools both name
  their file in `input.path` (absolute from the scripted model; a relative one resolves against the event's
  `location.directory`); V2 has no `apply_patch`. `ReportsFileWrites` gates nothing today (only OpenCode sets it); it
  documents that the harness sends `files.written`.
- **Parent status (shared):** `GetEffectiveActivityStatus` now lets a waiting child win over the parent's own busy.
  The relay broadcasts and notifies what a session *shows* (`ShownActivityStatus`: its own report, unless a child
  waits), so the parent's own busy events don't hide the question, and the notifier hears the parent's shown status
  when a child changes. A Fleet delegation parent that idles before its children is unchanged (no second "finished").
- **Found live:** both snapshot builders (`SessionSnapshotBuilder`, `OpenCodeSessionMessageProxy`) turned
  `waiting_input` into `idle`, so opening any session stopped on a question (its own or a subagent's) made its row and
  header read idle until the next event. Fixed in both. The header now says "Needs input" with the row's diamond.
- **Left for later:** inside the parent's conversation, the subagent's task row and the Working line still say
  "Working" while the child waits (the list and header say "Needs input"). A V2 subagent's own edits refresh the child
  session's views, not the parent's open editor tabs, until the parent's turn ends. Edits by shell (`sed`) aren't
  seen mid-turn (as for OpenCode, the client reloads open files at turn end).

## Profiles

- [x] `SupportsProfiles` on. A session's profile picks its server: one per owner and profile version (content hash),
      and the owner's server without a profile as before (`OpenCode2Servers`). Every server shares the one database,
      so a session can move between them.
- [x] **Layering, checked live on 2.0.6 and 2.0.9** (`~/.cache/opencode2-profiles/p1-layering.sh`): V2 honours
      `OPENCODE_CONFIG` (a file path) as V1 does, with JSONC and V1 syntax. `GET /api/config` lists, low to high: the
      config folder (`OPENCODE_CONFIG_DIR` in separate mode), the profile, the folder's own `opencode.json`, then
      `OPENCODE_CONFIG_CONTENT`. Arrays (`skills`, `plugins`) add up across layers. So the profile is a hash-named
      file under `{data}/opencode2/profiles` passed as `OPENCODE_CONFIG`, and Fleet's plugin and skills stay in
      `OPENCODE_CONFIG_CONTENT` untouched: no merging, a folder's own config still wins over the profile (as on V1),
      and the separate install keeps its own config folder and database.
- [x] The catalog asks the profile's server, so the composer's agents and models follow the profile chip.
- [x] Children: a delegated child resumes on the server its parent listens on (`FindServing`), even when the profile
      was edited since that server started; V2's own subagent runs inside the parent's server anyway.
- [x] **The check.** V2 never refuses a config and no request errors (`p2-broken.sh`, `p3-diagnostics.sh`): a broken
      profile starts, answers every request and creates sessions. What it drops it writes to its log as
      `configuration normalization diagnostic` (`path=$.model kind=invalid|unsupported`), only when a folder loads.
      Unknown settings leave no trace at all, a plugin that doesn't load logs `failed to load plugin`, and a `model`
      with no provider silently falls back to another model. So the check parses the profile (JSONC), starts a
      throwaway server with `--print-logs`, loads an empty folder, and reads the log, the profile as V2 stored it
      (`GET /api/config`) and `GET /api/model/default`. The server is killed on every path.
- [x] Idle: a profile's server stops after `Harness:OpenCode2ProfileServerIdleSeconds` (300) without use and with no
      turn running; the session's next request starts it again (~0.35 s to listen). Measured on 2.0.9 in separate
      mode: an idle profile server holds ~260 MB RSS and 122 inotify watches (the owner's server ~265 MB, 126).
- **Left out:** the owner's server without a profile still never stops. A session keeps the profile version it
  started on until Fleet restarts (the server it goes back to after an idle stop is the same version).

## Later

- **Todos / Progress:** V2 has no todo tool. Add `fleet_todo` to the V2 plugin, reported as todo events, and
  turn `ReportsTodos` on.

## Open

- Nothing right now. Stage 0 approved and built (PR #250).

## Estimate

About 4–5 weeks for parity with the OpenCode harness. Stages 0–2 (≈ 2 weeks) give a usable text-and-tools
harness behind the off-by-default switch. V2 is days old and its API spec calls itself experimental: pin a
version and expect changes.

## Live catalog (Track H) (2026-09-22)

- [x] `OpenCode2Server` keeps listening after the load gate. For a folder Fleet asked about (`LoadLocationAsync`) or
      runs a session in, `agent/model/provider/command/config.updated` count as a change; `skill`, `plugin`,
      `websearch`, `reference` and `integration.updated` don't (nothing Fleet lists). A change is told once V2 has been
      quiet about the folder for 1 s, and nothing in a folder's first 3 s after its load gate completed counts (the
      rest of its loading burst). V2's own working folder is never told.
- [x] The runtime publishes a harness-neutral `harness.catalog_changed` on the `sessions` topic
      (`HarnessCatalogChanges`, Application): harness type, folder, `quickChat`, the Fleet profile ids whose catalog it
      is (`none` for the server without a profile; a profile's server names every profile id that asked for its
      content hash) and the Fleet sessions in that folder on that server. OpenCode 1 never sends it.
- [x] Client: `useHarnessCatalog` refetches when harness, folder and profile match (the old list stays up meanwhile)
      and forgets cached catalogs a change is about; a session's slash-command and `@` agent lists refetch when the
      change names the session. No polling.
- Checked live (2.0.9, separate mode, scratch Fleet): two composers on one folder (No profile, a profile). An agent
  file in the repo's `.opencode/agents/`, then one in V2's config folder, each reached both composers without
  reopening, with one catalog request per composer per change (both servers had loaded the folder, so two broadcasts,
  each matched by its own profile). Starting sessions in a new folder and in the open one made no request. A session's
  open slash list showed a new `.opencode/commands/*.md` with one request. An OpenCode 1 session answered beside it.
- Learned: creating `.opencode/` and writing a file into it straight away can reach V2 as two bursts (the new config
  folder, then the file) more than 500 ms apart; 1 s of quiet merged them in every run. V2 lists
  `.opencode/commands/*.md` as commands and hot-reloads them.
- **Left for later:** a session's own agent and model pickers in the conversation (`useAgents`, `useModels`) don't
  listen yet. A catalog that changed while its server was down (replaced, idle-stopped) isn't told.

## "Needs input" inside the parent's conversation (2026-09-22)

Track I. Shared code, every harness (OpenCode's `task`, OpenCode 2's `subagent`, Fleet's own delegations).

- **Gap:** #262 made the list and header read "Needs input" when a subagent stops on a question, but the parent's
  conversation still said "Working" on the subagent's row and on the Working line. The stream reducer only knew each
  delegation's own status (`running`), never what its child session showed.
- **Seam, no new event:** the snapshot's delegations carry `childActivityStatus` (the tracker's status for the child),
  for a parent opened while the child waits. Live, the parent's stream listens to `activity_status` on the `sessions`
  topic, which every harness's child sessions already broadcast, and records it on the matching delegation. A waiting
  child makes the stream `waiting_input`, outranking the parent's own busy, as `GetEffectiveActivityStatus` does.
- **UI:** the row says "Needs input" with the diamond and opens the child, where the question card is. The Working
  line says "Needs input" (no clock) while a question holds the turn: the child's, or the session's own (read from
  the session's shown status, which the header uses too).
- **Checked live** on a scratch Fleet (2.0.9 and OpenCode 1, fake model `delegate a question`): row and line say
  Needs input while the child waits and after a reload; the row opens the question; after answering, Working, then
  Done with no line. 13/13 per harness.
- **Left alone:** the background subagent's Working line (#270). It comes from the same `delegating` status, but a
  background call is only known from the tool part's metadata (the card layer), not the delegation, and the server
  side disagrees too (the list is idle live, but the list endpoint counts a working child as busy on refetch). Needs a
  decision on what an idle parent with background work should show; not changed here.

## Built-in skills under one folder (Track H) (2026-09-22)

- [x] Each owner has one folder, `{data}/opencode2/built-in-skills/<hash of owner id>`, named in the `skills` array
      of `OPENCODE_CONFIG_CONTENT` once, whatever is in it (`OpenCode2FleetFiles.SyncBuiltInSkills`). It holds exactly
      the built-in skills the owner turned on. Fleet rewrites it when the setting changes (a new
      `IHarnessRuntime.BuiltInSkillsChangedAsync`, called by `BuiltInSkillService` for every harness; a no-op for the
      others) and before every server request. The old layout (one array entry per skill, all skills in
      `built-in-skills/`) is cleaned up on the first sync.
- [x] Built-in skills are out of the "settings changed → replace the server" comparison: the config content no longer
      changes with them. Messages between sessions still replaces the server when idle.
- Checked live (2.0.9, separate mode, scratch Fleet; the fake model answers with the `fleet-*` skills in the request's
  system prompt): switched `fleet-simplify` on in Settings with a server running and a profile's server running. A
  new session on each listed it; a session that was already running didn't; both server pids stayed the same and no
  server was replaced. Switched off: new sessions on both servers didn't list it, while the session started while it
  was on kept it. An OpenCode 1 session answered beside it.
- **Correction to Stage 5 and #265's "Setup and install" list:** "new skills reach a running separate-mode server
  only after the server restarts" is wrong. A *new session* picks up a synced skill live, with no restart (watched
  folders: the config folder's `skills/`, a repo's `.opencode/skills/`, and any folder already in the array). An
  existing session's skill list is fixed when it's created, and nothing changes it (not a reload, not a restart). The
  one thing that needed a new server was a new array entry, which is what this change removes for built-in skills.
- **Left alone:** V2 also reads `~/.claude/skills` from the real home whatever `HOME` is set to (out of scope).

## Sign in to providers from Fleet (Track G) (2026-09-22)

- [x] Settings → Harnesses → OpenCode 2 lists V2's providers (`GET /api/integration`, 227 on 2.0.9), which are signed
      in and which sign-in each uses, with Sign in (key, browser) and Sign out; switching between several sign-ins.
- [x] A harness-neutral seam: `IHarnessRuntime.ProviderSignIn` (`IHarnessProviderSignIn`) and the
      `SupportsProviderSignIn` capability, served by `/api/harnesses/{type}/sign-in`. OpenCode 2 implements it in
      `OpenCode2SignIn` over `/api/integration` and `/api/credential`, on the owner's server without a profile.
- [x] The terminal command stays in the install panel as the fallback ("Or sign in to a provider in a terminal").

What Track G learned (2.0.9, scratch HOME, dummy keys only):

- **Integrations are per location, and only once it has loaded.** On a server that has just started, `POST
  …/connect/oauth` answers 500 until a location's integrations are registered, and browser sign-ins are kept per
  location. Every request names one folder of Fleet's own (`{data}/opencode2/sign-in`) and waits for it to load.
- **Stored sign-ins reach every server of the install at once.** A key added or removed through one server shows in
  another's `/api/model` straight away (two servers on one database), so profile servers need nothing.
- **`connections` lists stored sign-ins first, the one in use first**, then environment variables V2 found set. On
  2.0.9 a connection has no `method` field, although the spec requires one. A new key becomes the one in use;
  removing the one in use hands over to the newest other one.
- **V2 accepts an empty key** (204, and a sign-in named "Anthropic 2"), so Fleet refuses one first. Unknown
  credential ids answer 204; an unknown OAuth method or `complete` on an `auto` attempt answer 500.
- **Sign-in doesn't emit `integration.updated`** on 2.0.9. A key emits `credential.updated`, `credential.switched`,
  `provider.updated` and `model.updated` (the last two reach the live catalog). No event carries the key.
- **`auto` mode is two different flows.** A device flow (GitHub Copilot, ChatGPT headless, xAI, OpenCode Console)
  shows a URL and a code, and V2 polls the provider: it works from any device. A browser flow (ChatGPT browser,
  DigitalOcean, Poe, Snowflake) sends the browser back to a listener V2 opened on `localhost` (ChatGPT on 1455, then
  1457), on the machine Fleet runs on. From another device that page can't load; the attempt's `redirect_uri` tells
  the two apart, Fleet says so, and it passes the query of the address that browser landed on to the listener
  (only the query, only to the address V2 gave the provider, only with its path and port). Checked by passing
  `error=access_denied`: the attempt went `failed: access_denied`. No built-in provider uses `code` mode or the
  `command` method on 2.0.9.
- Attempts last 10 minutes; a finished one is kept for a minute, a cancelled one is gone (404), and cancelling closes
  the listener.
- **Keys** go browser → Fleet → V2 in request bodies only. Fleet never logs, stores or repeats them; the callback
  forwarder is a named `HttpClient` with no loggers and no proxy, since its address carries the provider's code.
- **Off with Fleet's sign-in on** (and in cloud mode): V2's sign-ins are the machine's, so one user's key would pay
  for every user's sessions. With token auth (one user, e.g. Fleet opened from a phone) it's on.
- **Left for later:** renaming a sign-in (`PATCH /api/credential`), the `command` method (V2 would run a command on
  Fleet's machine; Fleet shows it and doesn't run it), `code`-mode attempts seen live (none in 2.0.9's built-ins).

## Track F — busy while background work runs, the agent's shell environment (2026-09-22)

- [x] **A background shell keeps its server busy.** On 2.0.9 a shell call moved to the background (`background: true`
      or `POST /api/session/{id}/background`) keeps running after its turn ended, and `GET /api/session/active` is
      `{}` the whole time. `OpenCode2Server.IsIdleAsync` now also asks `GET /api/shell` for each folder the server
      has loaded (`GET /api/debug/location`, present since 2.0.6); a shell with `status: running` makes it busy.
      Findings (`.poc-runtime/probe-shells.sh` in the Track F worktree):
      - `GET /api/shell` without a location lists only the server's own folder (its working directory), so each
        folder is asked. Asking about a folder the server hasn't loaded loads it, so only loaded ones are asked. A
        session's folder loads on its first prompt, not when it's created.
      - A running item: `{"id":"sh_…","status":"running","command":…,"pid":…,"metadata":{"sessionID":"ses_…"}}`.
        Once it exits it's gone from the list; `GET /api/shell/{id}?location…` still has it with
        `status: exited` and its exit code.
      - A background subagent was already covered: its child session is in `/api/session/active` while it works.
      - When V2 can't be asked, the server still counts as busy.
      This covers the profile-server idle stop, the replacement after a settings change, and the stop after an
      update.

## Track F — the agent's shell environment (2026-09-22)

- [x] **The agent's shell no longer gets the server's private environment.** Measured on 2.0.9 by having the agent run
      `env` through its shell tool on a scratch Fleet. Every shell inherited `OPENCODE_SERVER_PASSWORD`,
      `OPENCODE_CONFIG_CONTENT` and `FLEET_BRIDGE_TOKEN`. In separate mode it also got `OPENCODE_CONFIG_DIR` and
      `OPENCODE_DB`, and with a profile `OPENCODE_CONFIG`. `opencode db path` run by the agent in separate mode answered
      "Database is not empty and has no session table": OpenCode 1 opened OpenCode 2's database.
- **`PUT /api/session/{id}/environment` doesn't fit** (`.poc-runtime/probe-env.sh` in the Track F worktree):
  - It replaces the whole shell environment, applies to that session's background shells, and survives
    `location/reload`.
  - It is per session and in memory: a subagent's child session starts with the server's again, and a restart
    forgets it.
  - Sending it when the child's `session.created` arrived lost the race to the child's first command in 2 of 4 runs
    of the live suite.
- **What Fleet does instead:** `ctx.shell.hook("create.before", event => …)` in Fleet's plugin. V2's replacement for
  V1's `shell.env` gets the full `event.env` of every shell V2 starts: the session's, a subagent's (5/5), a background
  one, the user's (`POST /session/{id}/shell`), after `location/reload` and after a restart. It works on 2.0.6 and
  2.0.9 (`probe-hook.sh`).
  - Fleet passes the edits in `FLEET_SHELL_ENVIRONMENT` (`{"NAME": null}` removes, `{"NAME": "value"}` restores),
    decided by `TerminalEnvironment.AgentShellChanges` from the terminal's `IsFleetOwned` list.
  - `FLEET_URL` stays for the fleet-api skill.
  - `OPENCODE_CONFIG`/`_DIR`/`_DB` go back to the user's own values or away. They aren't added to `IsFleetOwned`: that
    list also filters what OpenCode 1 and the V2 server inherit.
- **Left out:**
  - The hook is in Fleet's plugin, which loads only once Fleet knows its own address (as before).
  - OpenCode 1's shell isn't changed.
  - MCP servers, LSPs and formatters V2 starts still inherit the server's environment.

## Harness processes that outlive Fleet (2026-09-23)

Issue #265, "Replacement V2 servers outlive Fleet". Shared by every harness (`ProcessGroupHelper`), so the fix is too.

- **What was measured on `main`** (scratch Fleet on 5301, V2 2.0.9, `.poc-runtime/scenario.sh` in the worktree):
  - `setpgid` fails with `EACCES` for every harness process, first or replacement. `Process.Start` returns after the
    child has exec'd, so the child always stays in Fleet's process group, and `killpg` then fails with `ESRCH`. The
    tree kill added in #264 is what actually stops them.
  - `SIGTERM` to Fleet (graceful stop): nothing left behind, whether it was the first server, a server replaced after
    a settings change, a profile server stopped as idle and started again, or an OpenCode 1 session.
  - The OpenCode 2 live suite (26 tests, then Track G's two tests on their own): nothing left behind. The leftovers
    Track G saw weren't reproduced.
  - `SIGKILL` to Fleet: every harness is left running under PID 1 (OpenCode 1 and 2 alike). The next start's
    cleanup killed the pids in the `instances` table if the process name held `opencode`, `claude` or `node`. That
    missed every server started after its session was activated (a replacement, or a profile server started again),
    and it would kill any `node` process that got a recorded pid.
  - Under systemd (`systemd-run --user`, `KillMode=control-group` as in the installed unit), both `systemctl stop`
    and killing Fleet's main process take the servers with them: the installed service isn't affected.
- **`PR_SET_PDEATHSIG` doesn't fit.** Linux sends it when the *thread* that forked the child exits, and .NET starts
  processes on whatever thread calls `Process.Start`. Measured with `setpriv --pdeathsig KILL`: a child started from a
  dedicated thread died when that thread ended, and one started from a thread-pool thread died when the pool retired
  the idle thread (~20–45 s later), with the parent still running. It's also Linux-only (no `setpriv` or pdeathsig on
  macOS).
- **What Fleet does now** (`ProcessGroupHelper`, `ProcessIdentity`, `HarnessProcessRecords`):
  - A harness is killed with its children (tree kill) when Fleet stops it. The `setpgid`/`killpg` calls are gone.
  - Every harness Fleet starts on Linux and macOS is tracked until it exits. If Fleet's process exits (normally, or on
    an unhandled exception) with any still running, they're killed then.
  - Each is also recorded in a file under `Fleet:Harness:ProcessRecordsDirectory` (default:
    `harness-processes` in the user's app-data folder, so every Fleet the user runs shares it). The file names the
    Fleet and the harness by pid, start time (`/proc/<pid>/stat` on Linux, the kernel's start time on macOS) and boot
    id, and it's removed when the harness exits.
  - When Fleet starts, it stops each recorded process whose Fleet is gone and that is still the same process (same pid,
    start time and boot). A record whose pid now belongs to another process is dropped without killing anything. So
    is a record from before a reboot. A record whose Fleet is still running is left for that Fleet.
  - This replaces the old cleanup by `instances.pid` and process name.
  - `OpenCode2Servers.GetAsync` checks again, once it holds the lock, that it wasn't disposed while it waited. A server
    started after that would never be stopped.
- **Not covered:** after an abrupt exit, the harnesses keep running until a Fleet starts again with the same records
  folder. Windows is unchanged (the Job Object already kills harnesses when Fleet dies). macOS wasn't run.

## A parent reads idle while only a background subagent works (2026-09-23)

The user's decision: while a subagent moved to the background works and nothing else runs in the parent, the parent
reads idle everywhere (list, header, and its conversation, with no Working line), because it is free for the next
prompt and wakes by itself on the child's notice (#270). The subagent's row says "Running in the background". A
foreground subagent still shows Working, and a background child's question still shows Needs input (#272).

- **Where the server disagreed with itself.** `SessionActivityTracker.GetEffectiveActivityStatus` made any parent with
  a working registered child busy, and the list endpoint did the same from `GetActiveChildToParentMappingAsync`. The
  relay's own events (`ShownActivityStatus`) never did, so live the list said idle and its 15 s refetch said active.
  The snapshot builders and `SessionPropagation` (a child's status change broadcasts the parent's effective status)
  used the effective status too.
- **The seam, for every harness:** `DelegationService.HandleDelegationMovedToBackgroundAsync(parent, toolCall)`. It
  marks the child in the tracker (`MoveChildToBackground`), sends `delegation.updated` with `background: true`, and
  sends the parent's activity status. A background child's work doesn't count as its parent's anywhere after that.
  Its `waiting_input` still does. OpenCode 2's delegations call it when a `subagent` call's `session.tool.success`
  has `metadata.status: "running"` (`OpenCode2Delegation.Background`). No other harness backgrounds a subagent yet.
- **The client:** delegation events and snapshot delegations carry `background` (the tracker's, so a reload agrees
  with live). The stream reducer leaves background delegations out of `delegating`, and once a delegation is in the
  background it stays there.
- **Known limit:** the flag lives only in the tracker's memory, like the parent/child index itself. After a Fleet
  restart a delegation still `running` in the database reads as foreground again.
- **Checked live** (scratch Fleet, V2 2.0.9, fake model): background, background question, foreground on V2, and
  foreground on OpenCode 1. The extended `A_background_subagents_delegation_stays_open_while_its_child_works` live
  test passes, and so does the rest of the OpenCode 2 live suite.
