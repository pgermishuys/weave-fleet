# Stage 1: Fleet mods, UI mods and the writing loop

Written 10 October 2026 against `main` at `1c93a44d` (Stage 0 merged except E3 `useActivityMessages` and E5
`useStreamScroll`). Line numbers are from that commit; re-check before editing.

Background: `~/.cache/fleet-mods-proposal/index.html` (the proposal), `stage0-frontend-cleanup.md` (the clean-up this
builds on). Claude Code mods docs: https://code.claude.com/docs/en/plugins/mods/overview and `/reference`.

## Decisions already made by the user (don't reopen)

1. **Mirror Claude Code mods.** A mod is a module exporting `register(on, options)`; hooks are middleware
   `($, e, next)` that observe, rewrite or answer; `$` is the only way out. Prompt changes (Stage 2) behave as in
   Claude Code: a rewrite shows in the bubble, added context goes to the model unseen (`PromptOptions.ModelNotes`).
2. **Draft scope A.** A draft mod runs only for the session that wrote it, wherever that session is open (desktop,
   phone, other tabs). Its UI outside a session (a status-bar chip) shows only while that session is on screen, marked
   Draft. Keep turns it on for all the user's sessions on this machine.
3. **One Bun mod host beside Fleet.** One process runs every mod, over stdio JSON-RPC. Bun is downloaded the first
   time the user turns on a mod (pinned version, sha256-checked), not shipped in releases. Measured on this machine:
   17 MB idle, +0.35 MB per mod, 0.7 ms median round trip, 76 ms start. Only events a mod registered for cross the
   pipe; never streaming deltas.
4. **First mod: test-chips.** When an agent runs a test command (`dotnet test`, `bun run test`, `vitest`, `pytest`,
   `go test`), the tool row shows passed / failed / skipped counts, and the failing names when opened. UI only, so it
   works on every harness with no harness bridge.

Also from the proposal: mods return **element trees that Fleet draws with its own Vue components** (themed, phone-safe);
a mod never runs script in Fleet's page. Richer UI goes in a sandboxed conversation page (`ConversationPage.vue`,
already on main). Stage 1 is for the user first; no organisation policy tiers yet, but there must be a way to start
without mods and a list of what a mod touches before Keep.

## Scope of Stage 1

In: the mod host, the mod store with versions, the Bun installer, the `Mods` experimental switch (off by default),
UI render sites (tool row, composer band, status-bar chip, content pane), the `fleet-mod` skill and `fleet_mod_*`
tools, draft → review → Keep → Undo, Settings → Mods, Start without mods, test-chips proven end to end.

Out (later stages): prompt/tool/permission hooks (Stage 2), `$.fs`/`$.process`/`$.http` (Stage 2), built-ins as mods,
`.weave/mods` sharing, Suggest to Fleet (Stage 3), harness bridges (later).

## How to run this (orchestration)

You are the orchestrator, on Opus. Same model as Stage 0, which worked:

1. Copy this file into the repo as `.weave/plans/fleet-mods-stage1.md` in your first PR.
2. **Wave 0 is yours, not a subagent's, and it ends at the user.** Write the contract (below), open it as a docs PR,
   and stop until the user approves it. Everything after builds against it.
3. Then run each wave's PRs as **parallel subagents on Sonnet** (`Agent` tool, `model: "sonnet"`,
   `isolation: "worktree"`), one per PR, all of a wave in one message, each with a self-contained brief: scope, files,
   the contract sections it implements, tests first, acceptance, rules.
4. Never run two PRs that edit the same file at once. At most 3 subagents running test suites (or `dotnet` builds)
   at once; this machine has 7 GB and slows down.
5. Review every PR before it's opened: read the diff, re-run its tests, spot-check claims, look at the screenshots.
   Merge when green unless the user says otherwise; tell the user as each PR goes up.
6. The client lane (M5) waits until Stage 0's E3 and E5 are merged (check `git log origin/main`); the other Wave 1
   lanes don't touch the client and can start as soon as the contract is approved.
7. Don't use the Workflow tool unless the user asks.

## Wave 0: the contract (one docs PR, user approval required)

`docs/mods/api.md` plus `mods/types/fleet-mods.d.ts` (the types the host and mods are checked against). Keep it the
Stage 1 subset of Claude Code's API, named the same where the concept is the same:

- **A mod on disk:** `{name}/mod.json` (`name`, `version`, `description`, `hooks`: path to the module) and the module
  (`.js`/`.ts`, ES module) exporting `register(on, options)`. `on(event, matcher?, hook)` returns a registration with
  `.catch(handler)`.
- **Events in Stage 1:** `session.start` (once per mod per session, again after a reload), `turn.complete` (watch only),
  `ui.render` at sites `ToolRow` (matcher `{ tool }` on the registry's resolved name; props: tool name, category,
  status, input, output text, title), `ComposerBand`, `StatusChip`, `Pane`; `ui.press`, `ui.input`, `ui.select` for
  controls a mod drew. Each with its `e` shape and what a hook can return.
- **`$` in Stage 1:** `$.ui` (`invalidate`, `open`/`close` a pane, `toast` → Fleet notices, `log`), `$.state`
  (reactive per session, `get`/`set`; reading inside a render subscribes), `$.store` (per user, persisted, size
  limit), `$.session` (`id`, `title`, `harness`, `cwd`, `surfaces`), `$.clock` (`now`, `after`, `every`). Nothing that
  reaches files, processes, the network or models: those come in Stage 2 with the `calls:` review.
- **Elements:** `Box`, `Text`, `Pill` (good/warn/bad/neutral), `Button`, `Input`, `Select`, `Markdown`, `Code`,
  `Icon` (a fixed set of names Fleet maps to its icons), `Page` (a sandboxed page the mod ships, drawn with
  `ConversationPage`). Colours only by role (text, muted, accent, good, warn, bad), never hex, so trees follow the
  theme. Limits (text length, tree size) like Claude Code's.
- **The static check:** what `check` reports (`hooks:`, `calls:`, declared state) and what makes a module refuse to
  load (imports, globals other than `$`, calls it can't read).
- **Order and failure:** mods run in load order (kept mods by name, then the session's drafts); 10 s per hook
  excluding time inside `$`; a hook that throws is skipped; three failures turn the mod off with the error shown.
- **The protocol** between Fleet and the host (JSON-RPC 2.0 over stdio, one JSON object per line): `load`, `unload`,
  `check`, `dispatch` (event + session id → result), host → Fleet `$` calls as requests, `invalidate` and `log` as
  notifications. Versioned (`protocol: 1`).
- **Where mods live:** kept mods under `{data}/mods/{user16}/{name}/` with immutable `v{n}/` folders and a `mod.json`
  index (same shape as `FileSkillVersionStore`); drafts under `{data}/mods/{user16}/drafts/{sessionId}/{name}/`.

Ask the user in the PR: anything in the subset they want added or removed, and the element list.

## The PRs, in waves

Sizes: S small, M normal, L large. Dependencies in brackets.

### Wave 1 (after the contract is approved; M5 also waits for Stage 0 E3 + E5)

- **M1 Mod host (L).** New `mods/host/` (Bun, TypeScript): JSON-RPC stdio loop; loader for kept mods and per-session
  drafts; the static check (parse the module, list hooks and `$` calls, refuse what it can't read); middleware chains
  per event in load order; 10 s budget; `.catch`; three strikes; hot reload of a draft folder on change; `$` calls
  proxied to Fleet. `bun test` suite covering every rule in the contract. A `bun build --compile` script. Add a CI job
  that runs the host's tests (keep it fast). Template for behaviour: Claude Code's mods docs.
- **M2 Host client and supervisor in Fleet (M).** `ModsFeature` (copy `WorkflowsFeature`, preference key `Mods`, off
  by default, `FleetOptions.Harness.Mods`), `IModHost` in Application with a `FakeModHost` for tests,
  `ModHostProcessManager` + `ModHostClient` in Infrastructure (copy `Pi/PiProcessManager.cs` and `Pi/PiJsonlClient.cs`:
  stdio, `ProcessGroupHelper.AssignToProcessGroup`, request ids with timeouts, its own `JsonSerializerContext`), lazy
  start when Mods is on and at least one mod is enabled or drafted, restart with backoff, stop on shutdown. A
  `mods.changed` domain event (`DomainEvent.cs` + `EventTypes` + serialization test).
- **M3 Mod store and API (M).** `FileModVersionStore` (copy `Skills/FileSkillVersionStore.cs`; `mod.json` index,
  immutable versions, active pointer, atomic writes), `ModService` (list, get, drafts per session, keep a draft as a
  new version, use a version / Undo, enable/disable via the `Mods` preference list, the stored check result per
  version), `/api/mods` endpoints mirroring `BuiltInSkillEndpoints` (gated by the feature filter like
  `WorkflowEndpoints`). AOT: configure route groups statement by statement, not a fluent chain ending in
  `AddEndpointFilter` (that broke AOT on #415). Tests with a scratch HOME.
- **M4 Bun installer (M).** `BunRuntimeInstaller`: copy the download + `.sha256` + manifest pattern from
  `Updates/UpdateDownloadService.cs`; pinned version with a per-platform asset and sha256 table (linux-x64,
  linux-arm64, osx-x64, osx-arm64, win-x64); zip extract (none exists yet); install to `~/.weave/runtimes/bun/{v}/`;
  resolve order: configured path, installed runtime, `bun` on PATH only in Development. Progress reported as a job the
  client can show ("Installing the mod runtime…"). Tests with a local HTTP fixture, never the real network in CI.
- **M5 Client renderer and sites (L) [Stage 0 E3, E5].** A `ModTree` renderer (element JSON → Fleet components,
  role colours, phone-safe), new contribution points (with `defineContributionPoint` from `lib/contributions.ts`):
  `toolRowViews` (keyed by resolved tool name; `MessageToolList.vue` and `PhoneToolRun.vue` draw a contributed view
  inside the row), `composerBands` (`Composer.vue`, between `BackgroundStrip` and `QueuedMessages`; phone in
  `PhoneSessionPage.vue` before `PhoneComposer`), `statusChips` (`StatusBar.vue` `.status-bar__end`), and a `mod` canvas
  kind via `canvasTypes` for panes. Draft marking. All exercised with fake contributions: tests plus before/after
  screenshots in mock mode.

### Wave 2

- **M6 End to end (L) [M1, M2, M3, M5].** Fleet dispatches `ui.render` for hooked sites per session to the host,
  sends trees to the client on the session's topic (`mod.ui` event), the client contributes them to the points;
  `ui.press`/`ui.input` round trip; `$.state` changes invalidate and redraw; `$.ui.toast` → notices. Draft scope A
  enforced on the server (a draft's hooks only get its session's events). Integration test with the real host binary
  in CI (Bun installed by the CI job), not a fake.
- **M7 Agent tools and the skill (M) [M3].** `fleet_mod_write`, `fleet_mod_check`, `fleet_mod_reload`,
  `fleet_mod_test`, `fleet_mod_keep` in `FleetTools/fleet-tools.json` with `"requires": "mods"`; `ModsSwitch` in
  `FleetToolSwitches`; `ModBridge` + arms in `FleetToolCalls`; the OpenCode plugin (`opencode/fleet/fleet-canvas.ts`,
  descriptions must match: `FleetToolCatalogPluginTests`) and the OpenCode 2 plugin; Claude Code gets them through the
  MCP catalog. A `fleet-mod` built-in skill: how to write a mod, the API subset, the elements, the edit/check/reload
  loop, test-chips as the worked example.
- **M8 Review, Keep, Undo, Settings (M) [M3, M5].** The draft card in the conversation (Draft · name · On in this
  session only; Keep…, Show code, Turn off), the review dialog listing what the mod touches from its check, Settings →
  Mods (kept mods with version, history, Undo, on/off, the error after three failures), "Start without mods" (a menu
  item and a URL parameter, server-side flag so the host isn't started).

### Wave 3

- **M9 test-chips end to end (M) [M6, M7, M8].** On a scratch Fleet with a fake model (the pattern of the
  `~/.cache/fleet-tune-skills` kit): a session asks for test-chips, the agent writes it with the tools, the draft
  draws on that session's test rows, Keep, then it draws in a second session. Live-check with real OpenCode, OpenCode 2
  and Claude Code sessions running a real test command (Pi too if installed). Desktop and phone screenshots. Ship the
  mod as a sample in `mods/samples/test-chips/`, not as a built-in.
- **M10 Hardening and docs (M).** Host crash and restart, hook timeouts, three strikes, a mod that floods
  invalidations (throttle), Start without mods, Windows Job Object path, memory with 20 mods. `docs/mods.md` for
  users.

## Rules for every PR

Everything from Stage 0's rules (characterise first where code moves, before/after screenshots in every PR, invented
data only, never port 5173, `bun` for the client, CI parity on Node 22, each worktree its own `node_modules`, rebase
not merge, check a PR isn't merged before pushing), plus for server work:

- **Never run the Fleet API, integration or E2E tests with the real HOME**: it can delete the live `~/.weave/fleet.db`.
  Always a scratch HOME. Never execute `~/.weave/fleet/bin/fleet`.
- Run .NET tests with `TMPDIR` outside any git repository (e.g. `~/.cache/<topic>/tmp`).
- Check the Release AOT build (`dotnet publish -c Release`) for any PR that adds endpoints or JSON types; new types go
  in a source-generated `JsonSerializerContext`.
- A Fleet tool change needs both OpenCode plugins and the catalog test.
- The live check for M6–M9 runs on a scratch Fleet (install layout, `Fleet__Update__CheckOnStartup=false`), never the
  user's installed Fleet.

## Done when

- With Mods on, the user asks any session for test-chips, sees the draft draw on that session's test rows (desktop and
  phone), keeps it, and it draws in every session; Undo and Turn off work; Start without mods works.
- With Mods off (the default), nothing changes and no Bun is downloaded.
- The host's rules (budget, three strikes, static check, draft scope) are covered by tests; CI runs the host.
- Every PR merged green with before/after screenshots.

## After Stage 1 (not in this session's scope; for planning only)

- **Stage 2, Fleet-layer hooks:** `prompt.submit` in `SessionPrompting.PromptSessionCoreAsync` before the bubble is
  broadcast (text, `context` → `ModelNotes`, `drop`), Pi's first prompt moved through it (`SessionCreation`
  `RequiresInitialPrompt` path), `turn.*`/`session.*` from `DomainEventTranslator`, `tool.check` via an async
  `PermissionGate` reaching the host, `/commands` that run without a turn, `$.fs`/`$.process`/`$.http` with the
  `calls:` review before Keep.
- **Stage 3:** one built-in becomes a mod (the recap or a status chip), Share to repo (`.weave/mods/`, copy
  `WorkflowRepoFiles`/`WorkflowCatalog`), Suggest to Fleet through Report a problem.
- **Later:** harness bridges for tool calls and the system prompt (OpenCode and OpenCode 2 plugins first, then a Fleet
  mod for Claude Code, then a Fleet extension for Pi).

## Decisions and changes during Stage 1

- **10 Oct 2026, contract approved** (PR #481) as written: all five render sites stay in Stage 1.
- **How the work runs.** Each PR runs in a new Fleet session whose orchestrator and reviewer is Opus, delegating to
  Sonnet or Haiku subagents, whichever is strongest for the piece. The lane session opens its PR as a draft with
  screenshots and messages the Stage 1 session; the Stage 1 session reviews, then marks it ready and merges when green.
- **Wave 1 order changed.** `ModsFeature` (the `Mods` switch) moves from M2 to M3, since M3's endpoints are gated by it.
  M2 (host client and supervisor) waits for M3 and M4, because it reads the store to decide whether to start the host
  and the installer to find Bun. Stage 0's E3 (#480) and E5 (#482) merged, so M5 runs in Wave 1. Wave 1 is M1, M3, M4,
  M5; M2 follows.
- **Heavy jobs share three slots.** `dotnet build`/`dotnet test`, full vitest runs and `bun run build` go through a
  machine-wide three-slot lock (`~/.cache/fleet-mods-stage1/heavy.sh`) so lanes in different sessions don't overload
  the 7 GB machine.
- **Client default (M5):** when a mod draws `ToolUse`, Fleet drops the row's preview line (`└ … (22 lines)`).
- **Gaps found in the mockups, for a contract revision before they're needed:** `$.session.usage()` (context gauge);
  `turn.complete` returning `{ text }` (a line under the answer, a sixth site); changed files on `turn.complete`
  (tests-after-edits); `ui.message` from a `Page` back to its mod. Not in Stage 1 unless a lane needs one.
- Mockups used to evaluate the feature (not in the repo): `~/.cache/fleet-mods-stage1/` (`report/`, `testchips-mockup/`,
  `stage2-mockup/`, `sites-mockup/`).
- **10 Oct 2026, Bun for mods (user's decisions, shown in `~/.cache/fleet-mods-stage1/mods-switch-mockup/`):**
  1. *Turning Mods on* (Settings → Features) first says that mods need Bun, what Fleet will download and where, then
     installs at once with a progress bar in that row (inline, not a dialog). The first mod never waits on the
     download. A failed install turns the switch back off.
  2. *Our own manifest.* fleet-releases carries a `bun.json`: the recommended Bun version, a sha256 per platform, the
     oldest safe version and a note. Fleet reads it on its own update schedule (5 s after start, then every
     `Fleet:Update:CheckIntervalHours`). The pinned `BunRelease` stays as the offline fallback. Fleet never reads
     Bun's GitHub to decide anything: Bun publishes no security advisories, so Fleet decides "unsafe" when it bumps the
     manifest. A CI job can watch Bun's releases and open the bump PR. Bun 1.4.3 is the first bump.
  3. *A Bun already on the machine is offered first, in every build*: `PATH`, `~/.bun/bin`, Homebrew, at 1.4 or
     later ("Fleet found Bun 1.4.5 at ~/.bun/bin/bun" with Use my Bun / Download Fleet's own / Cancel). A found Bun
     is used only once the user picks it, which saves it as `Fleet:Harness:BunPath`. The user keeps it current with
     `bun upgrade`; Fleet restarts the host when its version changes.
  4. *Security updates.* If one can't install, Fleet warns (the row says why, with Retry, plus a Fleet notice), mods
     keep running, and Fleet retries on its schedule. A user's own Bun that's too old gets "run bun upgrade" and "Use
     Fleet's own Bun instead"; Fleet never replaces it.
- **The new split for Bun.** *M4b* (two PRs): (1) finding Bun on the machine with versions, `BunLocation` saying which
  Bun runs (the user's own or Fleet's), releases as data with the pin as fallback, several installed versions with the
  newest kept running during a download, pruning, safety status, and the contract line; (2) the `bun.json` manifest:
  shape and validation, fetching on Fleet's update schedule, change events, publishing and the bump job (proposed
  first; nothing changes in fleet-releases without the user's go-ahead). *M2* (host client and supervisor) runs the
  host on `IBunRuntime.FindAsync`'s Bun, restarts it when that Bun's version changes, and prunes once the host has
  moved. *M2b* (turning Mods on): the Settings row, the runtime status API and its events, saving the Bun path the
  user picks, the background install at start, and the notices.

## Hardening (M10)

Cut from M2 when it became a walking skeleton (10 Oct evening). The full code for most of these is on the reference
branch `feat/mods-host-client-reference` (7bcb39b0, closed PR #491), with review 1's findings in
`~/.cache/fleet-mods-stage1/review-m2/REVIEW-1.md`.

- **Hang blame.** The host sends `running {mod, event?, sessionId?}` on every entry into mod code: hook starts, `next`
  resolving back into a hook, a `$` answer resuming a mod, timers, callbacks and `register`. Fleet strikes the mod of the
  last one after the timed-out request was sent, and nobody when none arrived. Until then, a 15 s hang restarts the host
  with no strike.
- **Sticky hang strikes.** Fleet counts hang strikes across restarts. Only a hook the host reports as succeeded resets
  them, and Fleet's own reload `session.start` never does.
- **A load that never finishes** is marked refused, with a problem, instead of just restarting the host.
- **Draft-folder watcher.** Saving a draft reloads it, debounced, with a maximum wait, never following links, and with a
  bounded number of inotify instances per user (poll inside drafts). Until then, a draft reloads through an explicit call.
- **Host stderr.** Read it raw, cut each line at about 8 KiB, rate-limit it, and count what's dropped. Cap the
  per-mod log's line length too.
- **Host → Fleet requests** get a concurrency cap, answering a busy error past it.
- **Forget on archive or delete.** Subscribe to session archive and delete, and call `ForgetSessionAsync`, so a
  session's timers and drafts stop at once.
- **Bun changes.** Poll `FindAsync`, plus M2b's `IModsRuntime.BunChanged`, and restart the host on a new path or
  version. Then prune, keeping every user's in-use Bun. Until then, nothing prunes, so nothing can delete a Bun in use.
- **A user's host coming up on first sight** after a Fleet restart (signed-in users other than the local one).
- **Backoff** is reset by a planned stop. A failed restart (runtime not ready) is retried sooner than the next reconcile.
- **A busy host outlives Fleet on Linux** (an idle one exits when its stdin closes): `prctl(PR_SET_PDEATHSIG)` or
  `setpriv --pdeathsig`. Check the Windows Job Object path at the same time.
- **Inherited file descriptors** from the parent shell reach the host.
