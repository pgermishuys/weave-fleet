# Stage 0: front-end clean-up (client/)

Written 9 October 2026 against `main` at `8502f510`. Line numbers are from that commit; re-check before editing.

## Why

Fleet will get Claude-Code-style mods (proposal: `~/.cache/fleet-mods-proposal/index.html`, decisions made). Mods draw in
the client, and the client isn't ready: tool knowledge is in 9 places and has drifted, 17 server event types are missing
from the client's `DomainEvent` union, six registries have six shapes, the plugin system is half-built, and the big
components mix many jobs. The server just went through the same kind of clean-up (#435–#463: characterise, move, one PR
each). Do the same for the client. **This stage is clean-up only: no mod features, no new UI, no behaviour changes**
except the bugs listed below.

## How to run this (orchestration)

You are the orchestrator, on Opus. Do not do the lanes' work yourself.

1. Read this file and copy it into the repo as `.weave/plans/frontend-cleanup.md` in the first PR you open.
2. Run the lanes below as **parallel subagents on Sonnet** (`Agent` tool, `model: "sonnet"`, `isolation: "worktree"`),
   one subagent per PR, several in one message. Give each a self-contained brief: the PR's scope from this file, files
   and line numbers, the characterisation tests to write first, acceptance, and the rules below.
3. Waves: start every PR of a wave together; start the next wave when the PRs it depends on are merged into `main`.
   Never run two PRs that edit the same file at once.
4. You review each PR before it's opened: read the diff, re-run its tests, spot-check claims, check the screenshots.
5. Keep at most 3 subagents running test suites at the same time; this machine gets slow (full vitest runs hit the 5 s
   timeout under load). Subagents run targeted test files while iterating and the full suite once before the PR.
6. Don't use the Workflow tool unless the user asks for it.
7. Tell the user when each wave's PRs are up. Ask before merging unless they say otherwise.

## Rules for every PR

- Follow the shape of the server refactor PRs (#435–#463): a `test: characterize …` commit first (tests pass on the old
  code), then the `refactor: …` commit (same tests still pass), then `docs(mockups): …` with before/after screenshots.
- Every PR embeds before/after screenshots proving the app still works, refactors included. Use Vite mock mode from the
  worktree's own `client/` (see below) and Playwright with invented data only. Never point anything at port 5173 (it
  serves one of the user's real apps); check `ss -ltn` and pick a free port.
- Toolchain: `bun` (`bun run test`, `bunx vue-tsc --noEmit`, `bun run lint`, `bun run build`). CI uses Node 22 and
  `npm ci`; before opening a PR, run the client checks the way CI does if anything looks environment-dependent.
- Each worktree needs its own `client/node_modules` (`npm ci` on Node 22 in the worktree). A symlinked one gives 160+
  false failures and a broken build.
- Never run the Fleet API with the real HOME, and never touch a Fleet database for this work. It's client-only.
- Branch from `origin/main`; rebase (not merge) onto `origin/main` before pushing; check a PR isn't merged before
  pushing to it.
- Invented data in every test, fixture and screenshot. Never real session titles, repos or people.
- Keep the phone's separate view tree. Don't merge phone and desktop views; make both read the same registries.
- No new features, no mod APIs, no slots without a contributor. Stage 0 ends with registries and clean seams.

## The inventory (verified)

### Dead code
- Plugins never registered (`main.ts:26-28` registers only github and marketplace): `plugins/builtin/linear` (375
  lines), `sentry` (262), `docker` (258), `slack` (176). No imports from outside their folders, no tests.
- Contribution kinds nothing reads (`plugins/types.ts:117-126`, `slots.ts`): `routes` (`getRoutes`, slots.ts:55),
  `startupHooks` (:91), `contextResolvers` (:103), `sessionSources` (:112, only read by `composable.ts:48` whose computed
  has no reader). Also unused: `defineAsyncPluginComponent` and helpers (`composable.ts:23-56,117`),
  `FleetPluginRenderable` (`types.ts:142`), `FleetBuiltInPluginModule` (:133), `getPlugin` (`registry.ts:19`).
- `stores/slash-commands.ts`: no importers. `lib/command-registry.ts` `CommandRegistryValue`: no users.
- Modules with no importers: `lib/draft-utils.ts`, `lib/chart-theme.ts`, `lib/tool-card-utils.ts`,
  `lib/board-mock-data.ts`, `lib/highlight-utils.ts`, `lib/update-preferences.ts`; composables `use-credentials`,
  `use-activity-filter`, `use-command-history`, `use-scroll-anchor`, `use-active-questions`, `use-rename-workspace`,
  `use-workspaces`, `use-sidebar-resize`, `use-integrations`, `use-message-pagination` (only a test and
  `eslint.config.mjs:80`); components `session/TokenGrid.vue`, `layout/TopBar.vue` (only `eslint.config.mjs:142`),
  `pages/queue/QueueItemRow.vue`. Re-verify each with a grep over `client/` (including `.tsx` routes and config) before
  deleting.
- Core code importing GitHub plugin internals (`@/plugins/builtin/github/...`): `GitHubRepoPage.vue:18,25,26`,
  `GitHubBrowserPage.vue:21,22`, `FolderPicker.vue:54`, `AddRepositoryDialog.vue:13,14`, `BoardSourceConfig.vue:5`,
  `lib/github-items.ts:7`. Move the shared composables/types into core (e.g. `composables/github/`, `lib/github/`) so
  the plugin depends on core, not the other way round.

### Bugs to fix along the way
- `Composer.vue:341-358` adds a `window` listener for `weave:session-state-changed`, which nothing dispatches (client or
  server), and never removes it: one leaked listener per Composer mount. The watch at :328-339 and the MutationObserver
  at :360-397 already do its job. Delete it.
- `ContextPanel.vue:95` calls `getSidebarPanels()` with no argument, reading the plugin `Map` directly, so that computed
  never updates. Fixed by the registry work (lane D).
- `todowrite` (Claude Code's TodoWrite, lowercased by the server) has no label or icon anywhere: Wrench and "Todowrite".
  Fixed by the tool registry (lane B).

### Tool classification: 9+ places, drifted
Sites: `lib/tool-labels.ts:30-173` (switch; `task` missing), `lib/tool-icons.ts:26-87` (iconMap, labelMap),
`components/session/activity-stream-tool-card.ts:91,102,108,267` (SUBAGENT_TOOLS, BROWSER_TOOLS, TITLED_TOOLS,
isPatternTool), `lib/turns.ts:88-93` (WRITE/CREATE/SHELL_TOOLS, normalises names), `lib/phone/fold-steps.ts:36-46,210`
(SUBAGENT, READ, EDIT, RUN, SEARCH, PATTERN), `lib/pr-utils.ts:20`, `ActivityStream.vue:394,1099` (skill),
`MessageBubble.vue:105` (usesBrowser), question checks (`lib/question-types.ts:43`, `lib/phone/dock-state.ts:31,37`),
`lib/session-lineage.ts:340` (strips `mcp__`). Permission-kind wording is copied three times: `PermissionCard.vue:19-26,
56-60` (no `read` case), `lib/phone/asks.ts:6-26`, `lib/phone/inbox.ts:120-129`.
Drift: edit (turns has notebookedit/strreplaceeditor, fold-steps doesn't; icons know only edit/write), shell (turns
adds `terminal`; server counts `execute` as shell, client gives it the Code icon), read/search/web disagree between
fold-steps, icons and the server (`src/WeaveFleet.Domain/Harnesses/Permissions.cs:58-90`,
`.../ClaudeCode/ClaudeCodeTools.cs:38-52`), subagent set defined twice, case handling differs (some exact, some
lowercased). Tool `input` is untyped everywhere; there are 7 copies of `asRecord`.

### Events: 17 types missing from the client union
`DomainEvent` (`lib/domain-events.ts:646-680`, 34 members) lacks: `session.messaged`, `session.reported`,
`todos.reported`, `files.written` (all in `src/WeaveFleet.Domain/Events/DomainEvent.cs`), `session.status`
(`Domain/Harnesses/EventTypes.cs:19`), `session_created`, `session_archived`, `session_unarchived`, `session_deleted`,
`session_progress`, `progress.updated`, `session_tokens`, `smart_link.updated`, `memory.saved`, `workflow_run`,
`harness.usage`, `harness.catalog_changed`. Eight subscribers cast `(event.type as string) !== …`:
`stores/workflows.ts:107`, `lib/harness-catalog-changes.ts:63`, `composables/use-session-token-updates.ts:34`,
`use-session-progress-updates.ts:20`, `use-session-progress.ts:31`, `use-memory-notices.ts:74`, `use-harness-usage.ts:34`,
`use-smart-link-updates.ts:24`. `toDomainEvent` (`use-signalr-socket.ts:523-530`) casts without validating.
Session status: `deriveSessionStatus` is copied word for word in `use-session-activity-updates.ts:20` and
`use-session-stream.ts:101`, with a third, different derivation in `lib/domain-event-reducer.ts:218-257`.
Subscribers without tests: use-session-activity-updates, use-session-progress-updates, use-smart-link-updates,
use-harness-usage, use-session-queue, use-session-retry, use-agent-browser.

### Registries: six shapes
Commands (`stores/commands.ts`, Pinia + copy-on-write Map, registered only from `use-commands.ts:728-741`), keybindings
(`lib/keybinding-types.ts` + `stores/keybindings.ts`), canvas types (`lib/canvas-registry.ts:49`, closed `CanvasKind`
union at `stores/canvases.ts:26`), visual renderers (`lib/visual-renderer-registry.ts:9`), plugins (`plugins/registry.ts`
Map + `plugins/composable.ts` shallowRef mirror needing `refresh()` + `slots.ts` getters), sidebar rails
(`stores/sidebar.ts:5` union, `ContextPanel.vue:18` PluginRailId, `IconRail.vue:180` hard-coded list). Window buses:
`weave:command-*` (`lib/command-events.ts`, dispatched from use-commands and 5 canvases, all listened to in
`ActivityStream.vue:993-1026`) and `weave:session-sync`.

### Big components
`ActivityStream.vue` 1,990 lines (message-view building 284–587 + 1097–1281, background work with a direct API call at
:353, scroll/mount 706–1095, six window command listeners 881–1027), `Composer.vue` 1,585, `SessionItem.vue` 1,394
(already well tested, 39 tests), `routes/sessions.$id.tsx` 1,050 (detail fetch + 5 normalisers 41–371, actions
669–756). A tool row is drawn by `MessageBubble.vue` 302–351 (dispatch between AgentTaskRow, ToolCard, screenshot,
BrowserSteps, page), not ActivityStream. `ToolCardItem` drops the raw tool input (`activity-stream-tool-card.ts:243`).
Phone draws tool rows with `components/phone/session/PhoneToolRun.vue` (no tests), categorised by `fold-steps.ts`.

## The PRs, in waves

Sizes: S small, M normal, L large. Dependencies in brackets.

### Wave 1 (all in parallel)
- **A1 Delete dead modules (S).** The unused lib/composables/components listed above, their eslint config entries, and
  `stores/slash-commands.ts`, `CommandRegistryValue`. Plus the Composer listener leak (its own commit).
- **A2 Delete dead plugins (S).** linear, sentry, docker, slack folders. Nothing else; lane D owns `types.ts`/`slots.ts`.
- **B1 Tool registry, characterised (M).** First commit: tests that pin today's behaviour of every site above (labels,
  icons, turns categories, fold-steps categories, pr-utils, permission wording), including the current drift, so changes
  are visible. Second commit: `lib/tools/` with one descriptor per tool name (aliases and case handled once: category,
  label builder, icon, typed input parser, permission wording) and one `asRecord`. Don't migrate sites yet, except
  adding a `todowrite` descriptor. Decide the canonical category for each drifting name from the server's
  `Permissions.cs`, and write the decisions in the PR description.
- **C1 Typed events (M).** Add the 17 missing types (with payload types) to `DomainEvent`; add
  `onDomainEvent(type, handler)` that narrows the payload, over `onGlobalEvent`/`subscribeV2`; move the 8 casting
  subscribers onto it; tests for the 7 untested subscribers first. One `deriveSessionStatus` (delete the copy).
- **D1 One contribution primitive + plugins (M).** `defineContributionPoint<Id, T>()`: reactive (shallowRef Map,
  copy-on-write), ordered (`order`, then group, then label), owned (`contribute(owner, items)` returns a disposer;
  `removeByOwner`), duplicate-id policy. Move the plugin registry + composable mirror + slots onto it; delete the four
  unread contribution kinds and the unused helpers; fixes the ContextPanel bug. Tests for the primitive and for the
  plugin runtime (there are none today).
- **E1 Characterise the tool row (S, tests only).** MessageBubble's choice of tool component (AgentTaskRow, ToolCard,
  screenshot, BrowserSteps, page), ToolCard's glyph per status and collapse rules, a first `PhoneToolRun` test.

### Wave 2
- **B2 Migrate the tool sites (M) [B1, E1].** Every site reads `lib/tools/`; delete the local sets and switches.
  Desktop and phone both. Permission wording from one place. Can be two PRs (desktop, phone) if the diff is large.
- **A3 GitHub internals into core (S) [A2].** Move the shared GitHub composables/types out of the plugin folder.
- **D2 Commands and keybindings on the primitive (M) [D1].** Typed command ids; replace the `weave:command-*` window
  bus with commands whose handler ActivityStream registers while mounted.
- **E2 One tool-row component (M) [E1, B1].** Move `MessageBubble.vue` 302–351 (+ `pagedTools`) into a
  `MessageToolList.vue`/`ToolRow.vue` dispatcher; add `input` to `ToolCardItem` with a test. No behaviour change.

### Wave 3
- **D3 Rails, canvases, renderers on the primitive (M) [D1, D2].** `SidebarRail`/`PluginRailId`/`isSidebarRail` from
  plugin sidebar items; `CANVAS_TYPES` and visual renderers become contribution points (open `CanvasKind`).
- **E3 useActivityMessages (M) [E2, B2].** Characterise how ActivityStream builds message views, then move 284–587 and
  1097–1281 into `useActivityMessages`, making the `withDelegation`/`withImprove` chain an ordered list of decorators.
- **E4 Split the session route (M).** Characterise the detail fetch and normalisers (extend
  `SessionRoute.view-mode.test.tsx`), then `useSessionDetail` + `useSessionDetailActions`.
- **E5 ActivityStream scroll and window commands (M) [D2, E3].** `useStreamScroll`, `useIncrementalMount`; the window
  listeners are gone after D2.

Optional, only if time allows and nothing else is waiting: Composer split (`useComposerDisabledState`,
`useComposerAttachments`, `useComposerSend`), 17 components calling the API directly, the 80 hand-written API types.

## Done when
- One place classifies tools; desktop and phone agree; `todowrite` has a label and icon.
- Every server event type is in `DomainEvent`; no `(event.type as string)` casts remain.
- Plugins, commands, keybindings, rails, canvas kinds and visual renderers use one contribution primitive.
- MessageBubble's tool rows come from one component; ActivityStream's message building is a composable.
- No dead modules from the list remain; the Composer leak and ContextPanel bug are fixed.
- Every PR merged green with before/after screenshots.
