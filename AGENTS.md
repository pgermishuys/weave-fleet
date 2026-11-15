# Weave Fleet — Agent Instructions

## Toolchain

- **Package manager / script runner:** Always use `bun`. Never use `npm` or `npx` directly.
  - Run scripts: `bun run <script>`
  - Execute binaries: `bunx <package>` (e.g., `bunx vue-tsc --noEmit`)
  - Install packages: `bun install`
  - If you truly need npm CLI behaviour, use `bunx npm <args>`

## Layout Vocabulary

The application shell is a composable layout built from named panels:

```
[rail][context]
```

- **Rail** — Always-visible icon navigation fixed to the left edge. Controls which view fills the context area.
- **Context** — Everything to the right of the rail. Its composition changes per route.

**The rule:** whatever the user picks in the rail owns the whole Context area. No view shows another view's panel (the
session list belongs to Sessions only). A view that is switched off fills the whole Context area with its notice. A view
that needs a left panel supplies its own menu or list, like Settings does with `settings-menu` and `settings-detail`.
A route no rail owns (`/welcome`, `/repositories`) renders full width.

### Route Compositions

| Route        | Context layout                              |
|--------------|---------------------------------------------|
| Fleet        | `[fleet-menu][fleet-detail]`                |
| Sessions     | `[session-list][conversation][content]`     |
| Settings     | `[settings-menu][settings-detail]`          |
| Automations  | `[automations-list][automations-detail]`    |
| Analytics    | `[analytics-menu][analytics-detail]`        |
| Jobs         | `[jobs-list][jobs-detail]`                  |
| Workflows    | `[workflows-list][workflows-detail]`        |

### Panel Definitions

| Panel             | Description                                                                 |
|-------------------|-----------------------------------------------------------------------------|
| `rail`            | Vertical nav bar (left edge, always visible): an icon with its name in a tooltip (hover and keyboard focus) |
| `session-list`    | List of sessions for the current workspace (collapsible)                    |
| `conversation`    | The message/activity stream for the active session                          |
| `content`         | Right-side artifact viewer (rendered markdown, HTML preview, raw source)     |
| `settings-menu`   | Left nav for settings categories                                            |
| `settings-detail` | Detail pane for the selected settings category                              |
| `automations-list`| List of automations in the left nav panel                                   |
| `analytics-menu`  | Spend (Overview, Daily) and Breakdown (Projects, Models, Sessions) with counts, then the From/To/Project filters and Export. On a phone, a chip strip above the page |
| `analytics-detail`| The selected section; each has its own route (`/analytics`, `/analytics/daily`, …) |
| `automations-detail`| Detail pane for the selected automation                                   |
| `fleet-menu`      | Fleet (mission control, in Labs): Needs you first, then one entry per machine with its count; on a phone, a chip strip above the page |
| `fleet-detail`    | The selected view: every ask waiting on you, or one machine's sessions; each has its own route (`/fleet`, `/fleet/machines/{id}`) |
| `jobs-list`       | Jobs grouped Running, Queued, Done (Labs; a job that needs you stays Running). On a phone, a chip strip above the page |
| `jobs-detail`     | The selected job, at its own route (`/jobs/$id`)                            |
| `workflows-list`  | The workflow library: built into Fleet, then the repo's `.weave/workflows`   |
| `workflows-detail`| The selected workflow's steps, recent runs and Run box                     |

### Resize Gutters

Two gutters, both on the desktop only (a phone has none). Drag either to resize:

- **The rail's panel ↔ the page:** between whichever left panel the rail brings (`session-list`, `settings-menu`,
  `fleet-menu`, …) and the page beside it. It's there whenever the panel is: not while the panel is collapsed, and not
  for a view with no panel (a switched-off Labs feature, a route no rail owns).
- **`[conversation]` ↔ `[content]`:** while the right panel sits beside the conversation (an open session, or Board).
  Without room for a readable conversation beside it, the right panel opens as a sheet over the conversation instead,
  with no gutter.

No gutter between the rail and the context area.

## Design Rules

- **Use the shared pieces** in `client/src/components/ui/` (Button, Dialog, AlertDialog, DropdownMenu, Popover, Select,
  Switch, Tabs, Tooltip, …) instead of hand-rolling them.
- **Buttons:** `<Button>` with a variant (`default` primary, `destructive`, `outline`, `secondary`, `ghost`, `link`,
  `toolbar-icon` / `toolbar-icon-danger` for icon-only toolbar buttons, `filter` for toggles) and a size (`default`,
  `sm`, `lg`, `icon`, `icon-sm`, `icon-lg`, `toolbar`, `toolbar-lg`). They're defined in `ui/button/index.ts`.
- **Empty states:** `<EmptyState>` (`ui/empty-state`) for a list, panel or page with nothing in it: an optional icon, a
  title, one line of text and an optional `#action`. `bordered` draws the dashed card; `compact` is for side panels and
  narrow lists. A picker's "no matches" row (`CommandEmpty`) and a one-line note inside a card are not empty states.
- **Errors:** `<ErrorNote>` (`ui/error-note`) for an inline error: `message` (what went wrong), an optional `reason` (the
  server's words) and, when you listen for `@retry`, a "Try again" button (`retrying` holds it). `compact` drops the
  tint for a line under a field. Its words are `--error-text`, which every theme keeps at 4.5:1 on the error tint;
  `--error` itself is for icons and borders. Never Tailwind's red palette.
- **Confirming:** `await confirmAction({ title, body, confirmLabel, destructive })` (`ui/confirm`) before an action that
  can't be taken back; it resolves true or false. `<ConfirmHost>` in `App.vue` draws it on AlertDialog: Esc and Cancel
  answer false, focus starts on Cancel when `destructive` and goes back where it was. The button says the action
  ("Delete card"), never "OK". `subject` names the thing (`monoSubject` for a path). With `run`, the dialog stays open
  and busy while the action runs and shows its error if it fails. Where an action offers undo, don't ask first. A dialog
  with inputs or more than two choices is its own AlertDialog. Never `window.confirm` / `alert` / `prompt`.
- **Loading:** `<Spinner>` (`ui/spinner`), sizes `xs` 12px (a chip or dense row), `sm` 14px (a small button), `md`
  16px (the default) and `lg` 20px (a panel or page). It turns only for `motion-safe` and fades slowly under
  prefers-reduced-motion. Pass `label` when nothing beside it says what's loading. Busy refresh buttons show a
  Spinner in place of their icon; never `animate-spin` or a home-made rotating `@keyframes`.
- **Times and durations:** `@/lib/format`, never Intl or `toLocale*` in a component. `formatRelative(at, { compact })`
  is the one relative style: `compact` in dense lists and tables ("now", "4m", "3h", "5d", "2w", then a date such as
  "12 Oct" from eight weeks, never "119w"), words elsewhere ("4 minutes ago"). Every relative time has the absolute one
  (`formatDateTime`) in its tooltip; `<RelativeTime :at compact>` (`ui/relative-time`) does both. Absolute times take
  the user's locale (`formatTime`, `formatDate`, `formatDateTime`, `formatTimestamp`, `formatWhen`) and leave the year
  out this year. Durations are `formatDuration(ms)`: "45 s", "3 min 20 s", "1 h 05 min"; `formatCountdown` for
  "in 3 min".
- **Counts and money:** `@/lib/format` too. `formatCount` ("12,500", the locale's grouping), `formatTokens` ("950",
  "17.1k", "3.4M"), `formatBytes` ("3.4 MB"), `formatMoney` (two decimals in the data's currency, "<$0.01" for a
  sliver). Money is a list-price estimate and says so; it appears only where Fleet already shows cost, never anywhere new.
- **Colours:** theme tokens only (`--text`, `--muted`, `--accent`, `--border`, `--main-bg`, `--panel-bg`, `--card-bg`, …,
  and the Tailwind classes built on them such as `text-muted`, `bg-card-bg`).
  - Every theme in `client/src/assets/main.css` defines them.
  - `theme-contrast.test.ts` holds text to 4.5:1 and the focus ring to 3:1 in each theme.
- **Radius:** the tokens `rounded-btn` (8px), `rounded-card` (10px) and `rounded-panel` (12px); `rounded-full` for circles.
- **Motion:** `var(--transition)` (150ms ease-out) for hover, focus and press.
- **Keyboard focus:** the global 2px ring in `--focus-ring`. Never remove an outline without drawing another.
- **Ratchet:** `bun run lint` includes the design lint (`client/scripts/lint-design-system.ts`).
  - In a `.vue` file outside `components/ui/`, it fails a new raw `<button>`, a border-radius that isn't 0, 50% or a
    token, a duration that isn't `var(--transition…)`, and an element with its own empty-state class (`x__empty`,
    `empty-message`), any red-palette class or `text-error` / `text-destructive` as a text colour, and `animate-spin`
    or a rotating `@keyframes`.
  - In any `.vue` or `.ts` file under `src/` (tests aside), it fails `window.confirm`, `alert` or `prompt`, and
    (outside `src/lib/format/`) a new `Intl.DateTimeFormat` / `NumberFormat` / `RelativeTimeFormat` or `toLocale*`
    call.
  - The counts are compared with `scripts/design-lint-baseline.json`; `--list <RULE>` prints every one.

## Diagnostics and Testing

This section describes how to quickly diagnose and verify issues at each layer without guessing.

### Principle: Interrogate, Don't Guess

Never speculate about what's happening on the wire or in state. Build a test that proves the actual behaviour, then fix from evidence. The codebase has layered test infrastructure for exactly this purpose.

### Layer 1: API (Server-side)

**SignalR hub contract tests** (`tests/WeaveFleet.IntegrationTests/Sessions/SignalREventContractTests.cs`):
- Boots a real Kestrel server with SignalR hub (no browser, no frontend build)
- Connects a .NET `HubConnection`, subscribes to a session, broadcasts events via `IEventBroadcaster`
- Asserts the exact JSON shape received by the client
- Run: `dotnet test tests/WeaveFleet.IntegrationTests -c Debug --filter "FullyQualifiedName~SignalREventContractTests"`
- Use when: events aren't arriving, wrong shape, serialization issues, hub pump problems

**Diagnostic pattern for unknown issues:**
1. Add a `_hub.On<string, long, JsonElement>("Event", ...)` handler and log `data.GetRawText()`
2. Add a `_hub.Closed += ex => ...` handler to catch connection kills (e.g., "Error reading JSON")
3. Check `InMemoryEventBroadcaster.SubscriberCount` (internal property) to verify the pump is subscribed
4. Broadcast with `userId: "local-user"` in test mode (matches `LocalUserContext`)

**Other API tests:**
- Unit tests: `tests/WeaveFleet.Api.Tests/` (hub logic, snapshot merge)
- Application tests: `tests/WeaveFleet.Application.Tests/` (orchestrator, streaming state)
- Infrastructure tests: `tests/WeaveFleet.Infrastructure.Tests/` (event bus, persistence)

### Layer 2: Client (Frontend)

**Unit tests** (`client/src/composables/__tests__/`, `client/src/lib/__tests__/`):
- Test composables in isolation with mocked socket/API, and the event reducer on its own
- Run: `bun run test` (from `client/`)
- Use when: state management, event handling logic, message accumulation bugs

**Key composables:**
- `use-signalr-socket.ts` — SignalR connections (one per machine; every call takes a `MachineTarget`), topic dispatch
- `use-session-stream.ts` — Subscribes a session's topic, loads history, batches live events per frame
- `lib/domain-event-reducer.ts` — `applyDomainEvent`: event-to-state reducer, message accumulation
- `use-weave-socket.ts` — Re-exports from signalr-socket (the active transport)

**Diagnostic pattern for client issues:**
1. Check browser console for SignalR errors ("Error reading JSON", connection closed)
2. Use `window.__WEAVE_SOCKET_TEST_API` in devtools:
   - `.hasOpenSocket()` — is SignalR connected?
   - `.hasV2Subscriptions()` — are topics registered?
   - `.hasV2Snapshot(topic)` — did the snapshot arrive?
   - `.v2SnapshotHasText(topic, text)` — does snapshot contain expected text?
3. Add `diagLog()` calls (from `@/lib/message-diagnostics`) to trace event flow

### Layer 3: End-to-End (Full Stack)

**Playwright E2E tests** (`tests/WeaveFleet.E2E/`):
- Full browser against real Kestrel server with TestHarness
- Run: `dotnet test tests/WeaveFleet.E2E --filter "Category=E2E"`
- SignalR-specific: `dotnet test tests/WeaveFleet.E2E --filter "FullyQualifiedName~SignalRTransportTests"`
- Use when: verifying the user-visible behaviour end-to-end

**E2E requires frontend build:**
```
cd client && bun install && bun run build
```

Skip rebuild on iteration: `dotnet test tests/WeaveFleet.E2E -p:SkipFrontendBuild=true`

### The UI crawl (every screen, every night)

`scripts/ui-crawl/run.sh` builds the client in mock mode and visits every screen in `scripts/ui-crawl/screens.mjs` in
5 variants (dark/light, 1440/390 px, Labs on/off): page and console errors, a screen showing an error page (Fleet's
route error page carries `data-fleet-error-fallback` and logs what it caught), failed requests and sideways overflow at
390 px fail it; screenshots are a report (`out/report.html`). Axe (WCAG 2 A/AA) is a ratchet: each rule's count is
compared with `scripts/ui-crawl/axe-baseline.json`, and a rise fails the crawl, naming the screens that grew. When a
count falls, lower the baseline in the same PR: `UI_CRAWL_UPDATE_BASELINE=1 scripts/ui-crawl/run.sh` (a full crawl),
or `node scripts/ui-crawl/report.mjs --out=<out> --update-baseline` on a CI run's `ui-crawl-report` artifact. The
baseline holds CI's counts. A second pass, `run.sh --set=themes`, visits every screen in the other eight themes on the
desktop with axe's colour rules only, against `axe-baseline-themes.json` (lower it the same way, with `--set=themes`).
`run.sh --self-test` crawls a mock-only route that throws while it renders and fails unless the gates catch it.
`ui-crawl.yml` runs all three every night on main and on a PR that changes the crawl, and keeps one issue open while it's
red. Add a screen there when you add a route.

### Decision Tree: Where to Start

| Symptom | Start here |
|---------|-----------|
| Events not arriving at client | Layer 1: SignalR contract tests |
| Events arrive but wrong shape | Layer 1: assert `data.GetRawText()` |
| Events arrive but UI doesn't update | Layer 2: unit test `applyDomainEvent` with real payload |
| Connection drops/reconnect issues | Layer 2: `use-signalr-socket` tests + devtools API |
| Everything works in tests but not in browser | Layer 3: E2E with headed mode (`$env:HEADED=1`) |
| Flaky behaviour | Add `diagLog()` + check the per-frame batching in `use-session-stream.ts` |
