# browser-canvas-v1

## TL;DR
Ship the browser canvas as a real capability for a single-user Fleet. You say "host it and show me"; the agent calls `fleet_app_start`, Fleet runs the app in the session's folder, and a Browser tab shows it beside the chat. **Every code change shows up in that tab within about 2 seconds, on every stack in the test matrix.** The app's own hot reload does the updating where it exists (Vite, Bun, Next, `dotnet watch`), and Fleet reloads or restarts where it doesn't. It works whether you open Fleet on the machine it runs on or from another device.

V1 builds on the spike on branch `spike/browser-canvas` (2026-09-13), which already works end to end on a real model. It hardens the four layers the spike introduced and keeps the seams between them fixed, so later work (cloud, Claude Code, letting the agent see the page) adds to one layer instead of rebuilding the feature.

## Context
Ground truth from the spike and the codebase (2026-09-13). Don't re-check these:

- **The spike works.** Claude Sonnet 5, given "Can you just host it and show me?" in a Bun app, read `package.json` and called `fleet_app_start` with `bun --hot server.ts` on its own. Given "Can you host Fleet and show me?" in a Fleet worktree, it found `serve-scratch.sh` through `AGENTS.md` and ran it. Screenshots: `mockups/canvas/browser-agent-*.png`.
- **Hot reload survives the proxy.** A CSS edit to the Bun app hot-swapped in the canvas without a page reload (checked by marking `window` before the edit and finding the mark after). An HTML edit made Bun do a full reload, also automatic. Fleet did nothing in either case; the dev server's websocket went through the proxy.
- **Not yet checked: `dotnet watch`.** The Fleet demo used `dotnet run`, which doesn't reload at all. `dotnet watch` injects a browser-refresh script that connects to a **separate** websocket port, not the page's origin. That works by accident on the same machine and breaks from another device.
- **Spike code, by layer:**
  - Runner: `src/WeaveFleet.Application/Browser/IAppRunner.cs`, `src/WeaveFleet.Infrastructure/Browser/AppRunner.cs`, `LinuxListeningPorts.cs`. Runs are in memory. Page detection: printed URLs, "listening" lines first, only on ports the process tree listens on, page must answer 2xx/3xx/HTML (any answer after 15 s). `PORT` is set to a free port that stays the same across restarts. `BROWSER=none`, `NO_COLOR=1`, `DOTNET_WATCH_SUPPRESS_LAUNCH_BROWSER=1`, `DOTNET_WATCH_RESTART_ON_RUDE_EDIT=1`.
  - Proxy: `src/WeaveFleet.Api/Browser/PreviewProxies.cs`. One loopback Kestrel (`CreateEmptyBuilder` + `UseKestrelCore`) per app origin, reached as `{slug}.localhost:{port}`. Strips `X-Frame-Options`, `frame-ancestors` and HSTS, rewrites redirects to the app, drops cookie `Domain`, passes websockets through (subprotocols kept), injects `<script src="/__fleet_browser/nav.js">` into HTML.
  - Canvas kind `browser`: state `{url, appId?}`, agent op `setPage` (`CanvasOps.cs`, `CanvasStates.cs`, `CanvasValidators.cs` with `LoopbackUrl`). `fleet_canvas_read` appends app status and the last 40 lines of output (`CanvasBridge.cs`).
  - Agent tools `fleet_app_start` and `fleet_browser_open` in `opencode/fleet/fleet-canvas.ts`, served by `BrowserBridge.cs` through `/api/bridge/opencode/canvas/app-start` and `/browser-open`. Calling `fleet_app_start` again with the same command restarts that run.
  - User endpoints: `POST /api/sessions/{id}/browser/proxy`, `GET /api/sessions/{id}/apps/{appId}`, `POST …/restart`, `POST …/stop` (`BrowserEndpoints.cs`).
  - Client: `client/src/components/canvas/BrowserCanvas.vue` (iframe, address bar, back/forward/reload, port picker, strip with Restart/Stop/Output). It polls app status every 2 s.
- **Fleet already auto-allows every OpenCode permission** (`OpenCodeHarnessSession.TryAutoApprovePermissionAsync`), so `fleet_app_start` doesn't bypass any prompt that bash doesn't.
- **Releases ship linux-x64, osx-arm64 and win-x64**, Native AOT and trimmed (`.github/workflows/release.yml`; CI has an AOT publish step). The spike only ran Debug on Linux.
- **Session lifecycle hooks:** `SessionOrchestrator.StopSessionAsync` (`SessionOrchestrator.cs:1245`), `ArchiveSessionAsync` (1295), `DeleteSessionAsync` (1342).
- **Latest migration is `029`**; the next is `030_…`.
- **`files.changed`** comes from OpenCode's `file.watcher.updated` (`DomainEventTranslator.TranslateFileWatcherUpdated`). It's harness-specific, so V1 doesn't depend on it.
- **The prompt API takes `model: {providerID, modelID}`**, not camelCase `providerId`/`modelId`. A wrong name is ignored silently and OpenCode falls back to its default model.
- **Safety** (from memory): never run the Fleet API with the real `HOME` (`LegacyDataMigrator`). An app started from a scratch Fleet inherits its `Fleet__*` variables, so Fleet-as-the-app needs its own environment (`serve-scratch.sh` in the spike). Point Playwright's `TMPDIR` off `/tmp`; the tmpfs quota filled up during the spike.

## Decisions (2026-09-13)
1. **The page is rendered by the user's own browser in an iframe.** Native scrolling, typing and hot reload matter most for the edit-and-see loop, which the user named as the feature that matters. A streamed headless browser can come later for the agent to see the page, as an addition to the viewer layer, not a replacement.
2. **Four layers with fixed seams:** runner (runs apps, knows nothing about viewing), gateway (gives each preview a reachable origin), bridge (versioned message protocol with the page), viewer (the canvas). The canvas state stays `{url, appId}`, and agent tools address previews by canvas and app id, never by port.
3. **Fleet never reloads a page that has hot reload.** It reloads or restarts only when the page has no hot-reload client or the command doesn't watch files. Otherwise it would throw away the page's state.
4. **Acceptance bar:** after a file edit, the canvas shows the change within ~2 s on every stack in the test matrix. Stacks with hot reload keep the page (no full reload) for style and component edits.
5. **Runs are stored, but not restarted on their own when Fleet starts.** After a Fleet restart the canvas shows "stopped" with a Start button. Starting processes nobody asked for would be surprising.
6. **A session's apps stop when the session stops, is archived or is deleted.** Caps: 3 running apps per session and 10 per Fleet (configurable). Starting past a cap fails with a message naming the running apps.
7. **Pooled OpenCode only**, like canvases. The tools stay harness-neutral; MCP for Claude Code comes later.
8. **Nothing is pushed into the agent's context.** It reads status and output with `fleet_canvas_read` when it wants ([[dont-inject-agent-context-unasked]]).
9. **The address a preview gets (decided with the user 2026-09-13, "for now"):** `*.localhost` when the browser is on Fleet's machine; a wildcard host name when the user configures one (`Fleet:Browser:PreviewHost = "*.fleet.home.example"`), which keeps cookies apart; otherwise a port per preview on Fleet's host (no DNS needed, but ports share cookies, see Risks). All three sit behind one address-strategy interface, which the cloud later reuses. Revisit if the cookie mitigations in Task 4 fall short.

## Scope
- In scope:
  - Runner lifecycle: stored runs, events, cleanup on session end and at startup, caps, restart keeps the port.
  - Port detection and tree kill on Linux, macOS and Windows.
  - The gateway: one component addressed by preview id, three address strategies, auth for previews reachable off the machine, streaming-safe HTML injection, websocket pass-through, a styled page for "app stopped" and "can't reach the app".
  - The live-update loop: `dotnet watch` refresh socket through the gateway, hot-reload detection, the reload/restart fallback, a "build failed" state.
  - Bridge protocol v1.
  - Client: canvas opens as soon as the app starts, event-driven status, stopped/crashed/build-failed states, a Browser entry in the + menu (URL or command), a remembered per-project preview command, tool cards, narrow layout, mock mode.
  - AOT publish checked, test matrix automated, end-to-end check on a real model from the same machine and another device.
- Out of scope:
  - Cloud / multi-user Fleet (per-user subdomains, TLS, resource limits). The gateway's address strategy must allow it; nothing else.
  - Claude Code, non-pooled OpenCode and other harnesses (MCP).
  - A headless browser for the agent (screenshots, console, clicking).
  - Pointing at an element to change it, device-size presets, dev tools.
  - Containers: `docker compose` ports aren't visible to the runner. `fleet_browser_open` with the URL still works.
- Constraints:
  - AOT-safe throughout: source-generated JSON, no reflection-based proxy libraries.
  - A preview reachable from the network requires auth. A preview on loopback is no more exposed than the dev server itself.
  - The gateway only proxies ports that belong to one of the session's runs, or a loopback URL the agent or user opened explicitly for that session.

## Design

### Runner
- `app_runs` table (migration `030`): id, session_id (`ON DELETE CASCADE`), user_id, command, directory, port (the `PORT` value), status, exit_code, url, pid, pid_started_at, created_at, updated_at. Output stays in memory (ring buffer, 2000 lines) and is gone after a restart.
- `project_preview_commands` or a column on projects: the last command that served a page in this project, offered by "Run preview" in the + menu.
- Events on `session:{id}`: `app.updated` `{sessionId, appId, status, url, ports, exitCode, reason}` where reason is `started | ready | restarted | stopped | exited | build-failed | reloaded`. Output is fetched with `GET …/apps/{appId}/output?after={line}`.
- Startup: for each stored run with a pid, if a process with that pid and start time still exists, kill its tree (it outlived a crashed Fleet), then mark the run stopped.
- `IListeningPorts` per platform: `/proc` (Linux, done), `proc_pidinfo`/`lsof -iTCP -sTCP:LISTEN -a -p` (macOS), `GetExtendedTcpTable` (Windows).

### Gateway
- `PreviewGateway` replaces `PreviewProxies`: `EnsureAsync(sessionId, previewTarget) → PreviewAddress {origin, token?}`. One listener per preview (ports) or one shared listener that routes by `Host` (wildcard host); the proxy handler is the same code in both.
- Address strategies: `LocalhostSubdomain` (`{slug}.localhost:{port}`), `HostPort` (`{fleetHost}:{port}`, listens on Fleet's bind address, ports from an optional configured range), `WildcardHost` (`{slug}.{configuredHost}`). The client gets the full origin from the server instead of composing it.
- Auth for `HostPort` and `WildcardHost`: the canvas asks Fleet for a short-lived signed token and loads `…/__fleet_browser/enter?t=…`; the gateway sets an HttpOnly, SameSite=Strict cookie scoped to the preview origin and redirects to the page. Requests without it get 401.
- HTML injection streams: forward bytes until `<head…>` (or the first 64 KB), insert the script tag, then stream the rest without buffering, so streamed server rendering isn't held up.

### Live-update loop
- The bridge reports `hmr: vite | bun | webpack | next | dotnet-watch | none` from what the page loads (`/@vite/client`, `/_bun/hmr`, `__webpack_hmr`, `/_next/webpack-hmr`, `aspnetcore-browser-refresh.js`).
- `dotnet watch`: Task 1 decides between rewriting the refresh endpoint in the served script so it goes through the gateway, and setting `DOTNET_WATCH_AUTO_RELOAD_WS_HOSTNAME` (to confirm) so the socket is reachable and proxying it as a second target.
- Fallback when `hmr: none`: the runner watches the run's folder (ignoring `.git`, `node_modules`, `bin`, `obj`, `dist`, `.next`, `target`), debounces 300 ms, then restarts the app if its command doesn't watch files (the page didn't come back after the previous edit, or the run is marked `restartOnChange`), and tells the canvas to reload once the page answers again.
- "Build failed": known output lines per tool set status `build-failed` (`dotnet watch ❌`/`error CS…`, Vite's `[vite] Internal server error`, `error TS…` from `tsc -w`). The table lives in one place and is covered by tests. Unknown tools just show running/exited.

### Bridge protocol v1
`window.parent.postMessage({ fleet: 1, type, … }, "*")` from the page; `{ fleet: 1, type: "nav", action }` into it. Types: `hello {hmr}`, `location {href, title}`, `error {message}` (for the later console feature, not surfaced in V1), `nav back|forward|reload`. The parent accepts only messages from the preview's origin. Unknown types and versions are ignored, so later features add types without breaking older pages.

## Tasks

- [ ] 0. Land the spike as the base
  - **What**: Move the spike from `spike/browser-canvas` onto `feat/browser-canvas` as reviewed commits (runner, proxy, canvas kind, tools, client, tests, screenshots). No behaviour change.
  - **Acceptance**: Builds; the spike's tests pass (Application 107 canvas + browser, Api 10, Infrastructure 11, client canvas tests on Node 22 with `npm ci`).

- [ ] 1. Spike: `dotnet watch` through the gateway
  - **What**: A Razor Pages app and a Blazor Server app under `dotnet watch`, run by the runner and viewed through the proxy. On the same machine, then from another device against a proxy on Fleet's host. Edit a `.cshtml`, a `.razor` and a `.css`; record what updates and how (hot reload vs refresh vs nothing). Find where the refresh endpoint comes from, and try both approaches from the design.
  - **Output**: Findings appended to this plan; decides Task 5's `dotnet watch` approach.
  - **Depends on**: 0.

- [ ] 2. Runner lifecycle
  - **What**: Migration `030` and repository, `app.updated` events, the output endpoint, stop on session stop/archive/delete, orphan cleanup at startup, caps, ownership checks on every endpoint. `fleet_app_start` opens the canvas at once (state `{url: "", appId}`, canvas shows "starting"), then sets the page when it answers; the tool still waits for ready so it can return the URL or the failure.
  - **Files**: `src/WeaveFleet.Infrastructure/Migrations/030_add_app_runs.sql`, `AppRunRepository.cs`, `AppRunner.cs`, `BrowserBridge.cs`, `SessionOrchestrator.cs` (hooks), `Domain/Events` (`AppUpdated`), `SessionEventsHub.ResolveDomainEventType`, JSON contexts.
  - **Acceptance**: Kill Fleet with SIGKILL while an app runs; on the next start the app's processes are gone and the canvas says stopped with Start. Stopping, archiving or deleting a session stops its apps. A 4th app in one session is refused with the names of the 3 running.
  - **Tests**: repository round trip and cascade; lifecycle with a fake process host; SignalR contract test for `app.updated`.

- [ ] 3. macOS and Windows
  - **What**: `IListeningPorts` for macOS and Windows; tree kill checked on both; `cmd /c` quoting on Windows.
  - **Acceptance**: The live runner test (python `http.server`) passes on all three CI runners.
  - **Depends on**: 2 (interface).

- [ ] 4. Gateway
  - **What**: `PreviewGateway` with the three address strategies (Decision 9), auth for off-machine strategies, streaming HTML injection, per-session port ownership check, styled stopped/unreachable pages, config (`Fleet:Browser:PreviewHost`, `Fleet:Browser:PortRange`).
  - **Files**: `src/WeaveFleet.Api/Browser/PreviewGateway.cs` (from `PreviewProxies.cs`), strategies, `BrowserEndpoints.cs`, `FleetOptions` (Browser section).
  - **Acceptance**: From another device, a Vite app previews and hot-reloads through `HostPort`; without the token cookie the preview port answers 401. A streamed page's first bytes arrive before the whole response is ready. A URL on a loopback port that doesn't belong to the session is refused.
  - **Tests**: the existing live proxy test extended to each strategy; auth; streaming (upstream writes, pauses, writes).
  - **Depends on**: 0; can run in parallel with 2–3.

- [ ] 5. Live-update loop
  - **What**: Bridge `hello {hmr}`, the `dotnet watch` approach from Task 1, the watcher fallback (reload or restart), `build-failed` detection, `reloaded` events.
  - **Acceptance**: The test matrix (Task 8) meets Decision 4 on every stack.
  - **Depends on**: 1, 2, 4.

- [ ] 6. Client
  - **What**: Canvas driven by `app.updated` (no polling); starting / running / build failed / exited / stopped states with Start/Restart/Stop; output drawer streaming; narrow-width strip; origin from the server (no client composition); bridge v1; + menu "Browser" (URL, or command with the project's remembered preview command pre-filled); tool cards for `fleet_app_start` and `fleet_browser_open` (title, URL, "Show"); a brief "updated" pulse on the tab when the page reloads or hot-updates; mock mode seeding one running app and scripted events.
  - **Files**: `BrowserCanvas.vue`, `stores/canvases.ts`, `lib/domain-events.ts`, `CanvasHost.vue` (+ menu), tool card registry, `vite-plugin-mock-api.ts`.
  - **Acceptance**: In mock mode (3099), light and dark: each state renders; the strip fits a 360 px panel; the + menu starts a command. `vue-tsc`, lint and vitest pass on Node 22 with `npm ci`.
  - **Depends on**: 2 for event shapes; can start in mock mode alongside 3–5.

- [ ] 7. Agent tools and evals
  - **What**: Tune descriptions using five real-model scenarios: a Bun app ("host it and show me"), an ASP.NET app (expects `dotnet watch`), a monorepo with two apps (expects it to ask or pick the right one), a project with no dev script, and "show me again" (expects `fleet_canvas_focus`, not a restart). After an edit in a hot-reloading app, the agent shouldn't restart it.
  - **Acceptance**: All five behave as expected on Claude Sonnet 5, and on one non-Anthropic model the user picks.
  - **Depends on**: 2, 5.

- [ ] 8. Test matrix
  - **What**: Fixture apps: Vite + React, Vite + Vue, Next.js, Bun, Razor Pages (`dotnet watch`), Blazor Server (`dotnet watch`), plain Node server (no hot reload, `node server.js`). For each: start through the runner, open the preview, edit a style and a component/template, and assert the change is visible within 2 s, and that hot-reload stacks kept the page (mark check from the spike).
  - **Files**: `tests/fixtures/browser-apps/…`, `tests/WeaveFleet.E2E/Tests/BrowserCanvasLoopTests.cs`.
  - **Acceptance**: Passes locally with a scratch `HOME`. Decide with the user whether it runs on every PR or nightly (the fixtures need `npm ci` and `dotnet restore`).
  - **Depends on**: 5.

- [ ] 9. AOT and end-to-end check
  - **What**: AOT-publish Fleet and run the matrix against it. Then the real-model run: Bun app and Fleet itself (with `dotnet watch`), from the same machine and from another device; ask for a visible change and watch it land in the canvas without asking to reload.
  - **Output**: Screenshots in `mockups/canvas/`, findings here.
  - **Depends on**: all.

## Dependencies and order
0 first. 1 and 4 can start right after 0 (1 is research, 4 is gateway work). 2 → 3. 5 needs 1, 2 and 4. 6 can start in mock mode once 2 fixes the event shapes. 7 and 8 need 5. 9 is last.

## Risks
- **Ports share cookies.** With the `HostPort` strategy, a preview at `desktop:41234` and Fleet at `desktop:2113` are different origins but the same site, and cookies ignore ports. The previewed app's JavaScript (including its npm dependencies) could read Fleet's non-HttpOnly cookies, such as the CSRF token, and cookies marked SameSite=Lax count as same-site. Mitigations: prefer `WildcardHost` when configured; make every Fleet cookie HttpOnly where possible; consider giving Fleet's own cookies a `__Host-` prefix and a per-port name. Needs a decision in Task 4.
- **`dotnet watch` can't be routed through the gateway cleanly.** If neither approach in Task 1 works from another device, ASP.NET gets the Fleet fallback (reload after rebuild) off the machine: slower, loses page state, still automatic.
- **Dev-server settings that bypass the proxy**, e.g. Vite `server.hmr.host/clientPort` or an absolute asset base URL. Same machine: works anyway. Another device: hot reload silently stops. The bridge's `hello` could detect a websocket that never connects and show a hint.
- **File-watcher limits** (inotify) on big repos for the fallback. It only runs for apps without hot reload, with the ignore list.
- **Firewalls block random ports** on Fleet's host. `Fleet:Browser:PortRange` lets the user open a known range.
- **Model behaviour drifts** (starting servers with bash, restarting hot-reloading apps). Task 7's scenarios are the regression check; rerun them when the tool descriptions change.
- **OpenCode plugin loading changes** (same risk as canvases): the live plugin test fails on an upgrade that breaks it.
