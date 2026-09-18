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

- [ ] `OpenCode2Harness` descriptor. Capabilities start minimal (streaming, resume) and grow per stage.
- [ ] `OpenCode2HarnessRuntime.CheckAvailabilityAsync`: find the executable (see Stage 5 for where),
      `--version` must be 2.x.
- [ ] Server process: Fleet starts `opencode serve --port <free> --hostname 127.0.0.1` with
      `OPENCODE_SERVER_PASSWORD` (random per process), `FLEET_URL`, bridge token. **One server per owner (and
      later per profile), for every directory**: V2 handles directories itself through `location`. No pool
      keyed by directory.
- [ ] One SSE subscription per server (`/api/event`), routed to sessions by `data.sessionID`.
- [ ] Session: create with `location.directory`; prompt; interrupt; events
      `session.execution.started/succeeded/interrupted/failed` → busy/idle/turn failed,
      `session.text.started/delta/ended` and `session.reasoning.*` → message parts,
      `session.step.ended` / `session.usage.updated` → tokens and cost, model from `session.step.started`.
- [ ] Resume token = V2 session id; resume re-attaches to the owner's server.
- [ ] Registered in DI, off by default (`opencode2.enabled`), client entry in `harness-display.ts`.

Live check: new session → prompt → streamed reply → idle; stop Fleet, restart, same session answers again.
Size: 4–5 days.

## Stage 2 — tools, questions, permissions, history

- [ ] Tool events `session.tool.input.started/ended`, `session.tool.called`, `session.tool.progress`,
      `session.tool.success`/`error` → tool parts (content blocks → output, file content → attachments).
- [ ] Client: labels and cards for V2's tool names in the client's own switch (`shell`, `subagent`, `edit`,
      `read`, `glob`, `grep`, `webfetch`, `websearch`, `question`, `execute`), added next to the V1 cases.
      No translating V2 names back into V1 names on the server.
- [ ] Questions: `form.created` → Fleet question (fields → questions, options, `custom` → free text);
      answer → `POST /form/{id}/reply {"answer":{key:value}}`; dismiss → cancel form; `form.replied`.
      Waiting-for-you status from pending forms.
- [ ] Permissions: sessions are created with an allow-all `permissions` ruleset, the way Fleet runs V1
      headless. Any `permission.asked` that still arrives is answered `once` and logged.
- [ ] History for reopening a session, the way OpenCode (v1) does it: `OpenCode2HarnessSession.GetMessagesAsync`
      reads `GET /api/session/{id}/message` (newest first, cursor paging) and returns domain `HarnessMessage`s.
      `OpenCodeSessionMessageProxy` already builds snapshots from any `IHarnessSession.GetMessagesAsync`; only
      its three `HarnessType == "opencode"` gates (live read, live messages, partial fallback) need to let V2
      through, as a capability flag on `HarnessCapabilities` (e.g. `HistoryLivesInHarness`), not a name list.
      No V2 code goes into the proxy. Fallback to Fleet's stored messages when the server is down, marked partial.
- [ ] Reconnect: V2 streams are live-only. On reconnect, re-read `/api/session/active` and each open
      session's messages; durable events carry `aggregateID` + `seq`, so gaps can be detected.

Live check: shell tool with approval, a question answered from the UI, interrupt mid-tool, reopen.
Size: 4–5 days.

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
