# browser-canvas-v1

## TL;DR
Ship the browser canvas as a real capability for a single-user Fleet. You say "host it and show me"; the agent calls `fleet_app_start`, Fleet runs the app in the session's folder, and a Browser tab shows it beside the chat. **Every code change shows up in that tab within about 2 seconds, on every stack in the test matrix.** The app's own hot reload does the updating where it exists (Vite, Bun, Next, `dotnet watch`), and Fleet reloads or restarts where it doesn't. It works whether you open Fleet on the machine it runs on or from another device.

V1 builds on the spike on branch `spike/browser-canvas` (2026-09-13), which already works end to end on a real model. It hardens the four layers the spike introduced and keeps the seams between them fixed, so later work (cloud, Claude Code, letting the agent see the page) adds to one layer instead of rebuilding the feature.

## Context
Ground truth from the spike and the codebase (2026-09-13). Don't re-check these:

- **The spike works.** Claude Sonnet 5, given "Can you just host it and show me?" in a Bun app, read `package.json` and called `fleet_app_start` with `bun --hot server.ts` on its own. Given "Can you host Fleet and show me?" in a Fleet worktree, it found `serve-scratch.sh` through `AGENTS.md` and ran it. Screenshots: `mockups/canvas/browser-agent-*.png`.
- **Hot reload survives the proxy.** A CSS edit to the Bun app hot-swapped in the canvas without a page reload (checked by marking `window` before the edit and finding the mark after). An HTML edit made Bun do a full reload, also automatic. Fleet did nothing in either case; the dev server's websocket went through the proxy.
- **`dotnet watch` (checked in Task 1, see Findings).** It injects a browser-refresh script that connects to a **separate** websocket port. Through the spike proxy it doesn't work even on the same machine: the script isn't injected into framed pages, and the refresh socket refuses the proxy's `Origin`. Routing the socket through the gateway (approach A) fixes both, on the same machine and from another device.
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
10. **Viewing from another device is core, not an extra (decided with the user 2026-09-13).** The user's main setup is Fleet on a Linux box, used from a browser on a Windows machine on the same network. Fleet's interface, and so the Browser canvas inside it, runs in that Windows browser. There, `localhost` means the Windows machine, so the canvas can only show an app that the Windows browser can reach over the network.
11. **The app stays on `localhost`; Fleet's proxy is what listens on the network (decided with the user 2026-09-13).** This is the same idea as VS Code's and Codespaces' port forwarding. The canvas loads the preview at the host name the browser used to reach Fleet (`location.hostname`), not an address Fleet looks up (the box has several: LAN, Tailscale, container bridges). Considered and rejected:
    - **Make the app listen on 0.0.0.0 and load it directly.** Every framework needs its own switch (Vite `--host`, Next `-H`, ASP.NET `--urls`, Django's `ALLOWED_HOSTS`), and it puts unauthenticated dev servers on the network (Vite and webpack-dev-server have had file-disclosure bugs). Pages that forbid framing show blank. `dotnet watch` still wouldn't refresh, and the address bar and back/forward lose the injected script. A possible later option: load an app directly when it already listens on 0.0.0.0.
    - **Stream a headless browser on the Linux box into the canvas.** Worse feel (scrolling, typing, selection, no dev tools), a few hundred MB per preview, and a large project of its own. A browser on the Linux box is still wanted later so the agent can take screenshots; that's separate from how the user views the page.

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
- `dotnet watch` (decided in Task 1): the gateway sends `Sec-Fetch-Dest: document` upstream for framed pages, rewrites the refresh endpoint in the served `/_framework/aspnetcore-browser-refresh.js` to `/__fleet_browser/ws/{port}` on the preview's own origin, and forwards that socket to the refresh server with the app's `Origin`. Details under Findings.
- Fallback when `hmr: none`: the runner watches the run's folder (ignoring `.git`, `node_modules`, `bin`, `obj`, `dist`, `.next`, `target`), debounces 300 ms, then restarts the app if its command doesn't watch files (the page didn't come back after the previous edit, or the run is marked `restartOnChange`), and tells the canvas to reload once the page answers again.
- "Build failed": known output lines per tool set status `build-failed` (`dotnet watch ❌`/`error CS…`, Vite's `[vite] Internal server error`, `error TS…` from `tsc -w`). The table lives in one place and is covered by tests. Unknown tools just show running/exited.

### Bridge protocol v1
`window.parent.postMessage({ fleet: 1, type, … }, "*")` from the page; `{ fleet: 1, type: "nav", action }` into it. Types: `hello {hmr}`, `location {href, title}`, `error {message}` (for the later console feature, not surfaced in V1), `nav back|forward|reload`. The parent accepts only messages from the preview's origin. Unknown types and versions are ignored, so later features add types without breaking older pages.

## Tasks

- [x] 0. Land the spike as the base
  - **What**: Move the spike from `spike/browser-canvas` onto `feat/browser-canvas` as reviewed commits (runner, proxy, canvas kind, tools, client, tests, screenshots). No behaviour change.
  - **Acceptance**: Builds; the spike's tests pass (Application 107 canvas + browser, Api 10, Infrastructure 11, client canvas tests on Node 22 with `npm ci`).

- [x] 1. Spike: `dotnet watch` through the gateway
  - **What**: A Razor Pages app and a Blazor Server app under `dotnet watch`, run by the runner and viewed through the proxy. On the same machine, then from another device against a proxy on Fleet's host. Edit a `.cshtml`, a `.razor` and a `.css`; record what updates and how (hot reload vs refresh vs nothing). Find where the refresh endpoint comes from, and try both approaches from the design.
  - **Output**: Findings appended to this plan; decides Task 5's `dotnet watch` approach.
  - **Depends on**: 0.

- [ ] 2. Runner lifecycle (built 2026-09-13, see Findings; the live SIGKILL check is still to do)
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
- ~~**`dotnet watch` can't be routed through the gateway cleanly.**~~ Resolved in Task 1: approach A works from another device. What's left is that the rewrite depends on the literal in the SDK's refresh script; a test pins it, and if it stops matching, ASP.NET gets the Fleet fallback (reload after the app answers again).
- **inotify instances run out with `dotnet watch`.** Each `dotnet watch` took 51 of the 128 inotify instances a user gets by default on Linux; the third one crashed. See Findings.
- **Memory on small machines.** A `dotnet watch` app costs ~450 MB (watch, `dotnet run`, the app) plus MSBuild nodes. On the 7 GB dev machine, two of them plus builds triggered the OOM killer, which picked the installed Fleet's OpenCode process. See Findings.
- **Dev-server settings that bypass the proxy**, e.g. Vite `server.hmr.host/clientPort` or an absolute asset base URL. Same machine: works anyway. Another device: hot reload silently stops. The bridge's `hello` could detect a websocket that never connects and show a hint.
- **File-watcher limits** (inotify) on big repos for the fallback. It only runs for apps without hot reload, with the ignore list.
- **Firewalls block random ports** on Fleet's host. `Fleet:Browser:PortRange` lets the user open a known range.
- **Model behaviour drifts** (starting servers with bash, restarting hot-reloading apps). Task 7's scenarios are the regression check; rerun them when the tool descriptions change.
- **OpenCode plugin loading changes** (same risk as canvases): the live plugin test fails on an upgrade that breaks it.

## Findings

### Task 0 (2026-09-13)
- `feat/browser-canvas` branches from `origin/main` (`5db0fdd`, which was 6 commits ahead of the spike's base; no overlapping files). Six commits: canvas kind, app runner, preview proxy and app endpoints, agent tools, client canvas, plan and screenshots. Each builds on its own.
- Checks: Application 107 (canvas and browser), Infrastructure 11, Api 195 (includes the 10 browser tests, run with a scratch `HOME`), client lint (0 errors, none of the warnings in changed files), `vue-tsc`, vitest 419/419 on Node 22 with `npm ci` in a clean worktree.
- The spike had rewritten the line endings of `EndpointExtensions.cs` (mixed CRLF/LF, a 191-line diff for a one-line change); the commit keeps the original endings.
- Review notes that no task covers yet:
  - `AppRunner.MonitorAsync` is fire-and-forget: an unexpected exception ends page detection for that run silently. Catch and log it (Task 2).
  - `BrowserBridge` finds "other ports" with `Url.Contains($":{port}")`, so port 80 matches `:8080`. Compare parsed ports (Task 2).
  - Runs inherit Fleet's whole environment (`Fleet__*`, `ASPNETCORE_*`, and a scratch `HOME` in dev runs). Strip `Fleet__*` at least (Task 2).
  - The canvas's "Open in a new tab" opens the target's `localhost` URL, which is wrong from another device (Task 6).

### Task 1: `dotnet watch` through the gateway (2026-09-13)
Setup: SDK 10.0.112, `dotnet new webapp` (Razor Pages) and `dotnet new blazor --interactivity Server`, run by the runner in a scratch Fleet and viewed in the canvas with Playwright. "Another device" is Chromium in a `pasta` network namespace (`-T none -U none`), where `localhost` is the browser's own machine and Fleet is reached at a mapped address. Scripts are in `.poc-runtime/pw/` (`watch-edit.mjs`, local only); the prototype is `browser-canvas-v1-dotnet-watch.patch` next to this plan (applies to `PreviewProxies.cs`; a starting point for Task 4, not code to merge as is).

**How the refresh works.** `dotnet watch` starts the app with `ASPNETCORE_AUTO_RELOAD_WS_ENDPOINT=wss://localhost:A,ws://localhost:B` and a hosting-startup middleware that adds `<script src="/_framework/aspnetcore-browser-refresh.js">` before `</body>`. The app serves that script with the endpoints as a literal: `const webSocketUrls = 'wss://localhost:A,ws://localhost:B'.split(',');`. Both refresh servers listen on 127.0.0.1 and belong to the `dotnet-watch` process, so they're in the run's process tree.

**Why the spike proxy gets nothing, even on the same machine:**
1. The middleware skips injection when `Sec-Fetch-Dest: iframe` (injects for `document` or no header, and only when the request accepts HTML). Canvas pages are always framed, so there is no refresh script at all.
2. The refresh server checks `Origin`: the app's URL gets 101; the proxy's origin gets 403, and so does no `Origin` at all.

Unpatched spike, same machine: `.cshtml`, global CSS and scoped CSS never updated. Blazor's interactive markup did update, because it arrives over Blazor's own `/_blazor` socket, which the proxy passes through.

**Approach A (chosen): route the refresh socket through the gateway.** The prototype is 3 changes to `PreviewProxies`:
- Send `Sec-Fetch-Dest: document` upstream when the browser says `iframe`. The preview should get what a top-level page gets.
- In `/_framework/aspnetcore-browser-refresh.js`, replace `'…ws://localhost:B…'.split(',')` with `[(location.protocol === 'https:' ? 'wss://' : 'ws://') + location.host + '/__fleet_browser/ws/B']`.
- Forward websocket `/__fleet_browser/ws/{port}` to `ws://localhost:{port}/`. The existing websocket forwarding already sends the app's `Origin`, which passes the check.

Results with approach A (warm, meaning after the first edit following a start):

| Edit | Razor Pages, same machine | Razor Pages, other device | Blazor Server, same machine | Opened directly (control) |
|---|---|---|---|---|
| Markup (`.cshtml` / interactive `.razor`) | 0.2 s, full reload | 0.4 s, full reload | 0.3 s, page kept | same as through the gateway |
| Global CSS (`wwwroot/…css`) | 0.1–0.2 s, kept | 0.2 s, kept | 0.1 s, kept | 0.1 s |
| Scoped CSS (`*.cshtml.css` / `*.razor.css`) | 0.1 s, kept | 0.2 s, kept | 0.1 s, kept | 0.1 s |
| First edit after start | 3.8 s | 3.4–3.6 s | 3.8 s | about the same |
| Rude edit (field `int` → `long`), auto-restart | — | — | 12.6 s, full reload | 9.7 s, full reload |

The gateway adds nothing measurable. Razor Pages markup always does a full reload (`dotnet watch` sends `AspNetCoreHotReloadApplied` and its script reloads). Blazor keeps the page. Scoped CSS: `dotnet watch` names the bundle `BlazorApp.css` while the page links `BlazorApp.<hash>.styles.css`, so its script falls back to refetching every local stylesheet, which works. Rude edits take 10–13 s either way: rebuild, restart, then Blazor's reconnect backoff reloads the page.

**Approach B (rejected): `dotnet watch`'s own settings.**
- `DOTNET_WATCH_AUTO_RELOAD_WS_PORT` crashes `dotnet watch` ("address already in use"): its ws and wss servers both bind that port.
- `DOTNET_WATCH_AUTO_RELOAD_WS_HOSTNAME` binds that address, but the script then advertises **only** `wss://`, with the development certificate for `localhost`/127.0.0.1. Another device would have to trust that certificate.
- `DOTNET_WATCH_AUTO_RELOAD_WS_ORIGINS` with one origin wasn't honoured (403 on both ports; format not checked further).
- It would also put an unauthenticated socket on the network.

**For Task 4 (gateway):**
- The refresh route must only forward to ports owned by the run (the refresh servers are in its process tree, so the planned ownership check covers them).
- Rewrite `Sec-Fetch-Dest: iframe` to `document` on every proxied request.
- Rewrite only that exact script path, only when the literal matches, and pass anything else through unchanged. Pin the SDK 10.0.112 script in a test so an SDK change fails the test instead of silently breaking.

**For Task 5 (live-update loop):**
- `hmr: dotnet-watch` when the page loads `aspnetcore-browser-refresh.js`.
- Rude edits: when a `dotnet-watch` run's app process is replaced and the page answers again, Fleet can reload the canvas instead of waiting for Blazor's backoff (12.6 s now).
- Build failures print `dotnet watch ❌` lines, as expected in the design.

**For Task 2 (runner) and Task 8 (matrix):**
- **ASP.NET ignores `PORT`.** The port comes from `launchSettings.json` (`applicationUrl`, fixed per project, 5281 here). Two runs of the same project, such as two sessions on worktrees of one repo, collide: the second fails with "address already in use". The documented settings are `ASPNETCORE_URLS`/`DOTNET_URLS`, but `dotnet run` and `dotnet watch` apply the launch profile, and its `applicationUrl` overwrites `ASPNETCORE_URLS` in the app's environment (the docs say so too: "Configuring the `applicationUrl` sets the `ASPNETCORE_URLS` environment variable and overrides values set in the environment"). Measured on SDK 10.0.112 (`.poc-runtime/url-matrix.sh`):

  | Setting | `WebApplicationBuilder` app (.NET 6+ templates) | Generic Host app (`Host.CreateDefaultBuilder`) |
  |---|---|---|
  | `ASPNETCORE_URLS` | ignored (profile's 5281 wins), `dotnet run` and `dotnet watch` | not tested (same overwrite applies) |
  | `ASPNETCORE_HTTP_PORTS` | ignored (the profile's URLS win) | not tested |
  | `DOTNET_URLS` | **works** (`DOTNET_` outranks `ASPNETCORE_` for `WebApplicationBuilder`) | ignored (profile wins) |
  | `-- --urls http://localhost:$PORT` | **works** | **works** |
  | `--no-launch-profile` + `ASPNETCORE_URLS` | works, but loses the profile's `ASPNETCORE_ENVIRONMENT=Development` | not tested |

  For Task 2: set `DOTNET_URLS=http://localhost:$PORT` for single-project `dotnet run`/`dotnet watch` commands. Not for every command: an Aspire AppHost (or anything starting several .NET apps) would pass it to every service, and they'd all try to bind one port (not tested; that follows from the precedence). The tool description tells the agent to append `-- --urls http://localhost:$PORT` for ASP.NET, which also covers Generic Host apps. Apps that configure Kestrel endpoints themselves (Fleet does) ignore all of these; port detection still finds them.
- **Refresh ports are reported as app ports.** The tool result says "It also listens on 36013, 37715" and the port picker offers them. Filter out ports whose listener is the `dotnet-watch` process, or ports named in the refresh script.
- **inotify:** each `dotnet watch` took 51 inotify instances for a template app; the default per-user limit is 128, and the third concurrent run crashed ("The configured user limit (128) on the number of inotify instances has been reached"). Recognise that line and say what to do (raise `fs.inotify.max_user_instances`, or `DOTNET_USE_POLLING_FILE_WATCHER=1`, not yet checked). The 3-per-session cap may not be reachable with ASP.NET apps on a default Linux machine.
- **SIGTERM doesn't stop `dotnet watch`:** it stops the app and waits for a file change. The runner's tree kill (SIGKILL) works; a graceful stop must fall back to it.
- **Memory:** `dotnet watch` ~220 MB, `dotnet run` ~107 MB, the app ~105 MB, plus MSBuild nodes and the compiler server (536 MB, shared). On 2026-09-13 at 01:06 the dev machine (7.1 GB) ran out of memory with two apps, a Fleet build and another session's build running. The kernel killed the installed Fleet's OpenCode process and systemd restarted `fleet.service`. Consider raising `oom_score_adj` for app runs (no privilege needed to raise it), so the kernel picks a preview before Fleet or OpenCode. Run ASP.NET fixtures in the matrix one at a time.
- **Fixtures:** the Razor template's own `a.navbar-brand` scoped rule never applies (a tag-helper anchor gets no scope attribute); edit a rule on an element that carries it (`.border-bottom` on the `<nav>`). Measure warm edits after one warm-up edit, and rude edits separately (10–13 s, which doesn't meet Decision 4's 2 s; the bar should apply to warm, non-rude edits).
- Seen once, not investigated: after a rude-edit restart, reverting the file logged "No C# changes to apply" and the app kept serving the edited markup until a restart.

### Task 2: runner lifecycle (2026-09-13)
Built in the worktree `.claude/worktrees/browser-canvas`, after rebasing `feat/browser-canvas` onto `origin/main` `884dda0` (the terminal drawer, PR #190; two registration conflicts, both kept).

**Shape.** Three pieces, so the runner stays about processes:
- `AppRunner` (Infrastructure, singleton): processes, in memory. A run has a fixed id and `PORT`; `StartAsync` with a known id restarts it, so a stored run starts again under its id and port (a new free port if the old one is taken). Starts, restarts and stops take turns behind one gate, which is what makes the caps hold. Status is `starting | running | exited | stopped`: `stopped` means Fleet stopped it (user, session lifecycle, Fleet restart), `exited` means it ended on its own with a code. It raises `Changed` with a reason; a restart's kill is not reported, and an instant exit waits until the start has been reported, so clients always see started before exited.
- `AppRunService` (Application, scoped): what the endpoints and agent tools use. Merges live runs with stored ones (a stored run Fleet isn't running reads as stopped, or exited if it had), checks the session and the owner on every call, and reuses a session's stored run of the same command.
- `AppRunRecorder` + `AppRunRecorderService`: one channel, one reader, so changes are stored in order; each change stores and sends the run *as it is now* (reasons can arrive out of order across threads). Before listening, it kills leftovers (pid and start time must both match, 2 s tolerance) and marks every unfinished run stopped.

**Other changes:** `app_runs` (migration 030) with `ON DELETE CASCADE`; `app.updated` on `session:{id}`; `GET …/apps/{appId}/output?after=n`; `restart` also starts a stopped run (409 with the reason when a cap refuses); `ISessionAppCleanup` hooked into stop (before the "already stopped" early return), archive and delete (before the worktree is removed); caps in `Fleet:Browser:MaxAppsPerSession`/`MaxApps` (3/10); `TerminalEnvironment.IsFleetOwned` now also strips `Fleet__*`, `ASPNETCORE_*`, `DOTNET_URLS` and OpenCode's credentials from app runs; `DOTNET_URLS=http://localhost:$PORT` for `dotnet run|watch` of one project that isn't an Aspire AppHost and doesn't pass `--urls`; on Linux the command runs behind `{ echo 1000 > /proc/self/oom_score_adj; } 2>/dev/null;`; inotify-instance and ENOSPC-watcher lines get a Fleet hint line once per start; `dotnet-watch` processes' ports are `HelperPorts`, not `Ports` (kept for Task 4's ownership check). `fleet_canvas_read` reads through the service, so stored runs show as stopped. The browser canvas state accepts `url: ""` only with an `appId`. Client: minimal (an empty page shows a waiting line; stopped/exited show Start); Task 6 does the rest.

**Checks:** Application 556/556, Infrastructure 792 (1 skipped, not ours), including real-process tests for caps, stop vs exit, restart keeping id and port, env stripping and `oom_score_adj` = 1000, output numbering, leftover kill by start time, and started-before-exited (20 instant exits). Client: `vue-tsc`, eslint, vitest 496/496 on Node 22 after `npm ci` (the .NET build had installed with bun, which resolves a different `@vue/test-utils`). Not run locally: `WeaveFleet.Api.Tests` and `WeaveFleet.IntegrationTests` (they boot `Program`, and this worktree session can't set a scratch `HOME`), so the new `app.updated` contract test runs first in CI.

**Still open for Task 2:** the live acceptance check (SIGKILL a scratch Fleet with an app running, start it again, check the processes are gone and the canvas says stopped; stop/archive/delete from the UI; a 4th app refused). `DOTNET_URLS` against a real Aspire AppHost is untested (only a fake `.csproj`). Helper-port filtering is untested against a real `dotnet watch`.

## Assessment (2026-09-13)
Asked by the user after Tasks 0–1: is this a good feature with decent functionality? Written by the agent that built the spike, so read with that in mind.

**Verdict:** worth building. The spike is a convincing demo, not yet a product; V1 closes that gap.

- **Value:**
  - It removes the chore of starting a dev server, finding its port and opening a browser.
  - It gives the agent a proper owner for processes that never exit.
  - Most of all, you can see an app running on Fleet's machine from another device, with hot reload (0.1–0.4 s once warm). Nothing else makes that easy, since dev servers listen on `localhost`.
  - Agent tools for building web apps (Replit, Bolt, Lovable) all show the page beside the chat, so users expect it.
- **Weak today:**
  - The spike only works on the same Linux machine: the proxy listens on loopback, and `dotnet watch` didn't refresh before the Task 1 fix.
  - Runs are lost when Fleet restarts, apps outlive their session, and there's no auth for previews reachable from the network.
  - **The agent can't see the page**, so the person does all the checking. That's the biggest gap in value.
  - Running apps can hurt Fleet itself: on 2026-09-13 memory ran out and the kernel killed the installed Fleet's OpenCode.
- **Costs:**
  - The scope is 9 tasks: three address strategies, auth, port detection on three OSes, and a 7-stack matrix.
  - Maintenance: the `dotnet watch` fix depends on the SDK script's literal, and dev-server settings like Vite's HMR host can bypass the proxy.
  - The cheap alternative, a link that opens `localhost:5173` in a new tab, gets most of the value on the same machine but none from another device.
- **Deciding question for the user:** how much viewing from another device matters. **Answered 2026-09-13:** it's the user's main setup (Linux box, Windows browser), so it's core (Decision 10), and the proxy listening on the network is a core part of V1 (Decision 11).

**Proposed changes to V1 (not agreed yet; the user decides):**
1. Trim the scope:
   - Ship the `*.localhost` and port-per-preview addresses and defer the wildcard host name (Decision 9's third strategy).
   - Start the test matrix with Vite, Bun, `dotnet watch` and plain Node; add Next, Vue and Blazor later.
2. Make resource safety part of V1:
   - Caps (Decision 6) and a raised `oom_score_adj` for app runs, so the kernel picks a preview before Fleet or OpenCode.
   - A clear message when inotify instances run out.
3. Right after V1, let the agent see the page, at least with a screenshot tool (the first step of "a headless browser for the agent", now out of scope). It completes the edit-and-check loop.
4. Apply Decision 4's 2 s bar to warm, live-applied edits. Measure the first edit after a start (~3.8 s for `dotnet watch`) and rude edits (10–13 s) separately.

## Follow-ups
Open items, with where they came from. Tick them here as they're done.

- [x] **Decide** the proposed changes above (user). **Agreed 2026-09-13: all four.** The wildcard host name moves after V1 (Task 4 ships `*.localhost` and port-per-preview); the matrix starts with Vite, Bun, `dotnet watch` and plain Node; caps, `oom_score_adj` and the inotify message are V1 (Task 2); a screenshot tool comes right after V1; Decision 4's 2 s bar applies to warm, live-applied edits, with first-edit and rude-edit times measured separately. Estimate given at the time: ~9–10 working days for the rest of V1, ~5.5 for Tasks 2, 4 and 6.
- [ ] **Draft PR #189** (opened 2026-09-13) for `feat/browser-canvas`: keep it a draft, since it ships agent tools that start processes, and don't merge before Tasks 2, 4 and 6 at least. Until Task 4, the proxy endpoint proxies any loopback URL for an authenticated user.
- [x] Task 2: catch and log exceptions in `AppRunner.MonitorAsync`; compare parsed ports in `BrowserBridge` (`:80` vs `:8080`); strip `Fleet__*` from app runs (Task 0 review notes).
- [x] Task 2: give ASP.NET runs their port (`DOTNET_URLS` for single-project `dotnet run`/`watch`, `-- --urls` in the tool description). Aspire AppHosts are skipped by reading the project file.
- [ ] Check `DOTNET_URLS` against a real Aspire AppHost, and the helper-port filter against a real `dotnet watch` (Task 2 left both to a live run).
- [x] Task 2/5: hide `dotnet watch`'s refresh ports from "also listens on" and the port picker (`HelperPorts`).
- [x] Task 2/5: recognise the inotify-limit error (a Fleet hint line).
- [ ] Check whether `DOTNET_USE_POLLING_FILE_WATCHER=1` works with `dotnet watch` 10 and how slow it is.
- [x] Task 2: raise `oom_score_adj` for app runs (Linux).
- [ ] Decide what caps look like on a 7 GB machine (defaults are 3 per session, 10 per Fleet; three `dotnet watch` apps don't fit in 7 GB with builds running).
- [ ] Task 2: the live acceptance check (SIGKILL with an app running, then restart; stop/archive/delete; a 4th app refused).
- [ ] Task 4: build approach A from `browser-canvas-v1-dotnet-watch.patch` properly: ownership check on the refresh port, exact-path rewrite, a test pinning the SDK 10.0.112 script.
- [ ] Task 5: reload the canvas as soon as a `dotnet watch` app answers again after a rude-edit restart (12.6 s now, because of Blazor's backoff).
- [ ] Task 6: "Open in a new tab" must open the preview's address, not the target's `localhost`, when viewed from another device.
- [ ] Task 3: nothing has run on macOS or Windows yet.
- [ ] After V1: an agent screenshot tool (proposal 3).
- [ ] Investigate if it recurs: after a rude-edit restart, `dotnet watch` ignored the revert of the file ("No C# changes to apply").
