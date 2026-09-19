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

- [ ] New plugin `opencode2/fleet/index.js` (a directory; V2 won't load a plugin file path), written against
      the V2 plugin API: `export default { id: "fleet", setup(ctx) { ctx.tool.transform(...) } }`, no imports.
      Tools: canvas list/open/read/patch/focus, app start, browser open, browser screenshot. Each tool sets
      `options: { codemode: false }` or V2 only exposes it inside Code Mode. Session id comes from the
      executor's second argument (`tool.sessionID`).
      Copying the tool descriptions and the bridge `fetch` from `opencode/fleet/fleet-canvas.ts` is fine;
      the file itself stays separate.
- [ ] Loaded via `OPENCODE_CONFIG_CONTENT` `plugins`, the same variable mechanism, V2's own content.
- [ ] `OpenCode2CanvasCallerResolver` (token → server, V2 session id → Fleet session).
      `CanvasBridge` and `BrowserBridge` currently take a single `IHarnessCanvasCallerResolver`; change them
      to ask every registered resolver (tokens are unique per process). This is the one shared change.
- [ ] Skills: Fleet's skill folders via `skills` in the injected config.
- Todos: not in the first version (`ReportsTodos = false`); see Later.

Live check: agent opens a canvas, takes a browser screenshot (image comes back), app start.
Size: 3–4 days.

## Stage 4 — catalog, agents/models, profiles, subagents, recap

- [ ] Catalog (agents, models, commands, providers) from `/api/agent`, `/api/model`, `/api/command`,
      `/api/provider`. V2 loads a directory lazily and returns `[]` until then: create a throwaway
      location load (e.g. `GET /api/location?location[directory]=…`) before listing. Verify this in the spike first.
- [ ] Agent/model choice per session (`agent`, `model` on create; `/agent`, `/model` switch).
- [ ] Profiles: one server per (owner, profile), profile content through `OPENCODE_CONFIG_CONTENT`.
      Profile check starts a throwaway server with the content.
- [ ] Subagents: `subagent` tool + child session (`parentID`) → Fleet delegation events; child events
      routed to the parent's view.
- [ ] Off the record (recap): `POST /api/session/{id}/generate`.
- [ ] Commands: `POST /api/session/{id}/command`. Fork: `POST /fork` if the UI offers it.

Size: 4–5 days.

## Stage 5 — setup, install modes, updates

- [ ] **Default mode** (no V1 `opencode` found): setup types OpenCode's installer into the terminal,
      `curl -fsSL https://opencode.ai/v2/install | bash`, per the harness-setup rule that Fleet never
      downloads harnesses itself. V2 then lives at `~/.opencode/bin/opencode` (+ `opencode2`), with default
      config and database. A V2 the user installed themselves is used as-is.
- [ ] **Separate mode** (a V1 `opencode` is installed): the same installer, with a different HOME and without
      shell-file edits:
      `curl -fsSL https://opencode.ai/v2/install | HOME=~/.weave/harnesses/opencode2 bash -s -- --no-modify-path`.
      Verified: binary lands in `~/.weave/harnesses/opencode2/.opencode/bin/`, V1 untouched, no rc edits.
      Fleet starts it with `OPENCODE_CONFIG_DIR=~/.weave/harnesses/opencode2/config` and
      `OPENCODE_DB=~/.weave/harnesses/opencode2/data/opencode.db` (verified: V1's DB unchanged, V1's global
      config not read). Separate mode needs its own provider sign-in. The setup screen says so, and that
      repo-level `opencode.json` / `.opencode/` are still read by both.
- [ ] Which mode: decided at setup and remembered (a V1 installed later must not flip an existing V2 into
      default mode, or its sessions move databases).
- [ ] Windows: V2's installer is bash-only and package managers aren't supported. Setup shows the manual
      download for now.
- [ ] Updates: `LatestVersionPackage = "@opencode/cli"`, `MinimumVersion` from the oldest version the live
      tests pass on; update = re-run the installer (same mode, same HOME) when no session is working.

Size: 3–4 days.

## Stage 6 — tests

- [ ] Unit: event mapping from recorded V2 SSE (`events*.sse` from the spike), forms, message snapshots.
- [ ] Conformance fixture `OpenCode2Fixture` in `WeaveFleet.ConformanceTests`.
- [ ] Live tests with the real binary + `FakeLlmServer`, scratch HOME, pinned 2.0.x in CI (like PR #242).

Size: 3 days (spread across the stages; tests land with each stage's PR).

## Follow-up decisions (2026-09-18)

- **Todos:** left out of the first version; V2 sessions have no Progress list.
- **History on reopen:** OpenCode's approach, through the capability flag (Stage 2).

## Later

- **Todos / Progress:** V2 has no todo tool. Add `fleet_todo` to the V2 plugin, reported as todo events, and
  turn `ReportsTodos` on.

## Open

- Nothing right now. Stage 0 approved and built (PR #250).

## Estimate

About 4–5 weeks for parity with the OpenCode harness. Stages 0–2 (≈ 2 weeks) give a usable text-and-tools
harness behind the off-by-default switch. V2 is days old and its API spec calls itself experimental: pin a
version and expect changes.
