# fleet-terminal-drawer

## TL;DR
Give every session a real shell in a drawer under the chat, as in the mockup (`mockups/terminal/fleet-terminal.html`, published at https://claude.ai/code/artifact/afc0dbe6-cb88-4552-b827-35ee883fceca). Fleet runs the shell on the server in the session's folder and streams it to xterm.js over a WebSocket of its own. Scrollback is saved to disk, so a reload or a Fleet restart brings it back. The agent never sees the terminal. You select lines and add them to your message. Linux, macOS and Windows all ship; I test Linux, and the user runs a short checklist on macOS and Windows before merge.

## Context
Ground truth from the codebase (2026-09-13). Don't re-check these:

- **Where the drawer mounts.** `client/src/routes/sessions.$id.tsx` renders `<ActivityStream>` then `<Composer>` in the chat view (around line 905), inside a flex column that already holds `SessionDetailHeader`. The drawer goes after `<Composer>`. `SessionActionToolbar` fills the header's `actions` slot, which is where the terminal button goes.
- **Composer.** `client/src/components/session/Composer.vue` has image attachments through `useDraftAttachments(sessionId)` and draft text through `useDraftState`. Canvas annotations reach the composer through `use-canvas-annotation.ts` and `lib/format-annotation-prompt.ts`. That's the pattern for terminal lines.
- **Keys.** Global shortcuts are handled in `client/src/composables/use-commands.ts` (`handleGlobalKeyDown`, line 678) and `use-keyboard-shortcut.ts` (line 92). Defaults are in `client/src/lib/keybinding-types.ts`. `Ctrl/Cmd J` is unbound. `Escape` interrupts the session, `Ctrl K` opens the palette, `Ctrl B` toggles the sidebar and `Ctrl [ ]` switch sessions. All four are everyday shell keys. The status bar is `client/src/components/layout/StatusBar.vue`.
- **Mock mode.** `client/vite-plugin-mock-api.ts` serves the REST API, and `use-signalr-socket.ts` takes live events from `import.meta.hot` (`fleet:mock-hub-event`). UI work happens there, on port 3099 from Playwright. Never touch a Fleet database for UI work.
- **Endpoints and auth.** `src/WeaveFleet.Api/Endpoints/EndpointExtensions.cs` maps everything onto `apiScope`, which is an authorized group when `RequiresFleetAuthorization`. `Program.cs` returns 401 instead of a login redirect for `/api` and `/hubs` (`IsApiOrWebSocketRequest`). Cloud (hosted) mode is `FleetOptions.Auth.Enabled`. Local mode signs in loopback requests automatically and accepts bearer tokens for remote access (`TokenAuthEnabled`, default true). CORS dev origins are listed in `Program.cs` around line 170. A raw WebSocket endpoint needs `app.UseWebSockets()`: SignalR adds it only inside its own hub pipeline.
- **Sessions.** `Session.Directory` is the working folder (a worktree or clone when isolated). `SessionOrchestrator.ArchiveSessionAsync` (line 1295) and `DeleteSessionAsync` (line 1342) are the two lifecycle points where terminals must end. Archived sessions can't be unarchived.
- **App data.** `FleetPaths.DefaultAppDataDirectory` is `LocalApplicationData/WeaveFleet` (`~/.local/share/WeaveFleet` on Linux). Scrollback goes under `terminals/` there, not `~/.weave` as the mockup's note said.
- **Environment Fleet carries.** Settings come in as `Fleet__*` environment variables (secrets included, e.g. `Fleet__Auth__ClientSecret`), the service sets `ASPNETCORE_ENVIRONMENT` and `ASPNETCORE_URLS` (`deploy/fleet.service`), and Fleet reads `FLEET_HARNESS`. A shell that inherits `ASPNETCORE_URLS` breaks the user's own `dotnet run`.
- **Processes.** `src/WeaveFleet.Infrastructure/Harnesses/ProcessGroupHelper.cs` has the Unix and Windows interop for killing a process tree (Unix `setpgid` always fails, see commit 6e9fd7a).
- **Events.** Typed `DomainEvent` records with `[JsonDerivedType]`, broadcast on `session:{id}` through `IEventBroadcaster`, mapped to wire names in `SessionEventsHub.ResolveDomainEventType`, and reduced in `client/src/lib/domain-event-reducer.ts`. The canvas events (`CanvasEvents.cs`) are the closest template.
- **Build.** The API is published AOT and trimmed in Release for `linux-x64`, `osx-arm64` and `win-x64`. CI (`.github/workflows/ci.yml`) runs on `ubuntu-latest` only. Client CI uses Node 22 and `npm ci`; locally, use `bun` (AGENTS.md).
- **Packages.** NuGet has `Porta.Pty` 2.2.2 (the maintained Pty.Net fork) and Microsoft's `Microsoft.Windows.Console.ConPTY`. npm has `@xterm/xterm` 6.0.0, `@xterm/addon-fit` 0.11.0, `@xterm/addon-web-links` 0.12.0.
- **t3code reference** (`~/source/t3code`): `apps/server/src/terminal/PtyAdapter.ts` (a six-member PTY interface), `Manager.ts` (lifecycle, history in `.log` files, query sequences stripped before replay, environment blocklist), `apps/web/src/components/ThreadTerminalDrawer.tsx` (the drawer, `DEFAULT_THREAD_TERMINAL_HEIGHT = 280`, `Mod+J`).
- **Safety.** Never run the Fleet API with the real `HOME`: `LegacyDataMigrator` deletes the installed Fleet's `~/.weave/fleet.db`. Use a scratch `HOME` for anything that starts the API. That includes the test projects that boot `Program` (`WeaveFleet.Api.Tests`, `WeaveFleet.IntegrationTests`, `WeaveFleet.E2E`): in this worktree the sandbox refuses a scratch `HOME`, so they run in PR CI only. (Tasks 3 and 4 ran them locally by mistake on 2026-09-13; the existing `fleet.db.legacy-backup` guard held and nothing was lost.)

## Decisions (2026-09-13)
1. **A drawer under the chat, not a canvas tab** (the user's choice). It spans the conversation column only, so the right panel stays visible beside it.
2. **Terminals belong to a session.** Each shell starts in the session's folder, and switching sessions switches the drawer. This was the mockup's recommendation, and the user approved the mockup.
3. **The agent never sees the terminal.** Terminal output reaches the agent only when the user selects lines and sends them. There's no agent tool in this plan.
4. **All three platforms.** Linux is built and tested here. The user runs the macOS and Windows checklist at the end of this plan before merge.
5. **Output doesn't go through SignalR or domain events.** Each attached terminal gets a raw WebSocket: binary frames carry bytes, text frames carry small JSON control messages. Only lifecycle changes (opened, closed) go out as session events, so a second window sees tabs appear and disappear.
6. **Scrollback is saved to disk** under app data: at most 5,000 lines or 2 MB per terminal. Query sequences are stripped before saving. It's deleted when the tab closes or the session is archived or deleted.
7. **After a Fleet restart, reopening a saved terminal starts a new shell under its old scrollback**, with a dim `— Fleet restarted —` line between them. Shells themselves don't survive a restart.
8. **Off when Fleet is hosted** (`Auth.Enabled`): a shell there runs as the service user on a shared machine. Local mode keeps it on, including remote access with a token, because the agents there already run shell commands with the same rights. `FleetOptions.Terminal.Enabled` overrides the default either way.
9. **Keys.** `Ctrl J` (`⌘ J` on a Mac) shows and hides the drawer. While the terminal has focus, Esc, Ctrl K, Ctrl B and Ctrl [ ] go to the shell. Fleet keeps Ctrl J and Ctrl Shift B. Copy is Ctrl Shift C on Linux and Windows, ⌘ C on a Mac. The status bar says when the terminal has the keyboard.
10. **Tab names are the shell name** (`zsh`, `zsh 2`). Naming tabs after the running command, and asking before closing a busy tab, come later (the mockup marked them Later).
11. **A shell that ends on its own closes its tab** (decided while building Task 3), the way closing a terminal window does: typing `exit` removes the tab and its scrollback. So there's no "exited" state and no `terminal.exited` event; `terminal.closed` carries the exit code when the shell ended itself. A shell Fleet ends (tab closed, session archived or deleted, Fleet stopping) reports no exit code.

## Scope
- In scope:
  - A PTY layer with one interface and implementations for Unix (Linux, macOS) and Windows (ConPTY).
  - A terminal service: create, list, attach, write, resize, clear, close; limits; environment cleanup; shell choice; scrollback on disk; lifecycle with sessions and with Fleet shutdown.
  - REST endpoints, the WebSocket attach endpoint, Origin and ownership checks, the feature flag, and lifecycle events.
  - Client: API and socket with reconnect and replay, a store, the drawer (tabs, resize, header button, Ctrl J), xterm.js themed from Fleet's tokens, keyboard hand-off, select-to-message, and mock mode.
  - Tests at each layer, a Linux E2E test, screenshots, and the macOS/Windows checklist.
- Out of scope:
  - Agent access of any kind (no `fleet_terminal_*` tools).
  - Tab names from the running command, close confirmation, splits, port detection and a browser canvas, a restart button.
  - Hosted mode (off, Decision 8).
- Constraints:
  - AOT-safe: no reflection, `LibraryImport` or AOT-compatible `DllImport`, source-generated JSON.
  - The Release publish must stay free of new trim or AOT warnings.
  - A socket or endpoint call for a session the caller doesn't own returns 404, with no difference between "unknown" and "not yours".

## Design

### Server
```
Application/Terminals/
  IPtyFactory, IPtyProcess        spawn(shell, args, cwd, cols, rows, env) → pid, write, resize, kill, output, exit
  ITerminalService, TerminalService per-session terminals, limits, lifecycle, fan-out
  TerminalHistory                 bounded lines/bytes, sanitize, snapshot for replay
  TerminalReplaySanitizer         strips DSR/CPR/DA/DECRQM replies and OSC 10/11/12 colour queries
  TerminalEnvironment             base env minus Fleet__*, FLEET_*, ASPNETCORE_*; adds TERM, COLORTERM
  TerminalShell                   $SHELL → /bin/zsh → /bin/bash → /bin/sh; pwsh → powershell → cmd
Infrastructure/Terminals/
  UnixPty, WindowsConPty          chosen in Task 0
  TerminalHistoryStore            {appData}/terminals/{sessionId}/{terminalId}.log (+ index.json)
Api/Endpoints/TerminalEndpoints   REST + WebSocket
```

**REST** (in `apiScope`, 404 when the feature is off):
- `GET /api/sessions/{id}/terminals` → `[{ id, title, status: "running" | "exited", exitCode, createdAt }]`, including terminals saved before a restart (status `exited`).
- `POST /api/sessions/{id}/terminals` with `{ cols, rows }` → 201 and the terminal. At most 8 per session and 32 live shells per Fleet; past either limit the call returns 409 with a message.
- `DELETE /api/sessions/{id}/terminals/{terminalId}` → 204. Kills the process tree and deletes the scrollback.

**WebSocket** `GET /api/sessions/{id}/terminals/{terminalId}/socket?cols=&rows=`:
- The server checks the `Origin` header first (same host as the request, or a configured CORS origin), then the session's owner. A failure is a plain 403 or 404 before the upgrade.
- On connect, the server starts a shell if the terminal has none (Decision 7), sends the saved scrollback as binary frames, then `{"type":"ready"}`, then live output.
- Server → client: binary = output bytes. Text = `{"type":"ready"}`, `{"type":"exit","exitCode":0}`, `{"type":"cleared"}`.
- Client → server: binary = input bytes (at most 64 KB a frame). Text = `{"type":"resize","cols":120,"rows":30}`, `{"type":"clear"}`.
- Several sockets can attach to one terminal, and output fans out to all of them. The last resize wins. Each socket has a bounded send queue (1 MB). A socket that falls behind is closed, and the client reconnects and gets the scrollback again.

**Events** on `session:{id}`: `terminal.opened` and `terminal.closed`, each carrying `{ sessionId, terminalId, title, exitCode }` (`exitCode` only when the shell ended itself, Decision 11). They aren't persisted.

### Client
```
lib/terminal-api.ts          REST calls and socket URLs
lib/terminal-socket.ts       WebSocket with reconnect and replay; a mock transport in mock mode
lib/terminal-theme.ts        Fleet tokens → xterm ITheme, updated when the theme changes
lib/format-terminal-context.ts  selected lines → fenced block in the prompt
stores/terminals.ts          per session: terminals, activeId, open; drawer height (per window)
components/terminal/TerminalDrawer.vue   grip, tabs, cwd and size, clear, hide
components/terminal/TerminalView.vue     one xterm per terminal, kept alive while its session is open
```

## Tasks

- [x] 0. Spike: a PTY in .NET that survives AOT (done 2026-09-13: use Porta.Pty 2.2.2, see "Task 0 findings")
  - **What**: In a scratch project outside the solution, try `Porta.Pty` 2.2.2 and a small interop of our own (Unix `posix_openpt`, `grantpt`, `unlockpt`, `ptsname`, then spawn the shell as a session leader on the slave; Windows `CreatePseudoConsole`). On Linux check: (a) `$SHELL` starts in a given folder with a given size; (b) write `echo hi\r` and read `hi` back; (c) resize, then `stty size` prints the new size; (d) `exit 3` reports exit code 3; (e) killing the shell also kills a child `sleep 1000`; (f) `dotnet publish -c Release -r linux-x64 -p:PublishAot=true` builds with no new warnings, and the published binary does (a)–(e). Also build the macOS and Windows paths for `osx-arm64` and `win-x64` to catch compile and trim warnings there. Check licences.
  - **Output**: "Task 0 findings" at the end of this plan, with the choice for Task 1. Spawning a managed process with `fork` in a multi-threaded runtime is the main risk to look at.
  - **Depends on**: None.

- [x] 1. PTY layer (done 2026-09-13)
  - **Files**: `src/WeaveFleet.Application/Terminals/IPtyFactory.cs`, `IPtyProcess.cs` (new); `src/WeaveFleet.Infrastructure/Terminals/` (Unix and Windows implementations per Task 0); registration in `DependencyInjection.cs`.
  - **Acceptance**: Task 0's (a)–(e) as tests. Output arrives as bytes, split wherever the OS splits it. Exit fires once. `Dispose` kills the tree and never throws.
  - **Tests**: `tests/WeaveFleet.Infrastructure.Tests/Terminals/` on Linux, marked so they're skipped on other OSes until the checklist runs.
  - **As built**:
    - `IPtyFactory` and `IPtyProcess` (with `PtySpawnOptions` and `PtyExit`) all live in `IPtyFactory.cs`. `PortaPtyFactory` and `PortaPtyProcess` are the one implementation for every OS, registered as a singleton.
    - `PtySpawnOptions.Environment` is the child's whole environment. The factory turns it into Porta.Pty's changes, setting every other variable in Fleet's environment to empty so it's removed. Task 3 builds that environment; nothing leaks by default.
    - `Exited` completes once with `PtyExit(code, Killed: false)` for a normal exit, or `PtyExit(null, Killed: true)` after `Kill` or `DisposeAsync`. `ReadAsync` returns 0 instead of throwing once the terminal closes (Linux reports EIO). Write, resize and kill after exit do nothing.
    - 11 tests, all Linux-only by an early return, like `ProcessGroupHelperTests`. They use `bash --norc --noprofile -i` so the machine's rc files don't matter.

- [x] 2. Scrollback: history, cleaning and files (done 2026-09-13)
  - **Files**: `src/WeaveFleet.Application/Terminals/TerminalHistory.cs`, `TerminalReplaySanitizer.cs`; `src/WeaveFleet.Infrastructure/Terminals/TerminalHistoryStore.cs` (new).
  - **Acceptance**: History keeps the last 5,000 lines or 2 MB, whichever is smaller, without splitting a UTF-8 sequence or an escape sequence. The sanitizer strips query sequences even when one arrives split across two chunks, and keeps colours, cursor moves and titles. Writes to disk are debounced (about 250 ms) and flushed on close and on shutdown.
  - **Tests**: unit tests with byte-level fixtures, including sequences split across chunks, following t3code's `Manager.test.ts` cases.
  - **As built**:
    - `TerminalReplaySanitizer.Process(bytes)` returns what to keep and holds back an unfinished sequence; `Flush()` returns what's held. It recognises only 7-bit forms (`ESC [`, `ESC ]`, `ESC P`, `ESC ^`, `ESC _`), because a lone 0x9B byte in UTF-8 belongs to an ordinary character. Terminators are BEL, `ESC \` and the UTF-8 ST (C2 9C). An unfinished sequence over 4 KB is let through as text. Strip rules are t3code's: CSI `n`; `R` and `c` replies; `$p`/`$y`; `>q`; `?u`; DCS `$q`, `+q`, `[01]$r`, `[01]+r`; OSC 10/11/12 with `?` or `rgb:`.
    - `TerminalHistory(maxLines, maxBytes)` holds 16 KB chunks and trims from the front just after a line break; with no line break to cut at, it cuts on a UTF-8 character boundary. It isn't thread-safe; the terminal that owns it locks.
    - `ITerminalHistoryStore` (Application) and `TerminalHistoryStore` (Infrastructure): `{root}/{sessionId}/index.json` plus `{terminalId}.log`, atomic writes through a temp file and `File.Move`, a per-session lock for the index, and ids checked against `^[A-Za-z0-9_.-]{1,128}$` (not `.` or `..`). A damaged index reads as empty.
    - `FleetOptions.Terminal` (`TerminalOptions`: `Enabled`, `HistoryDirectory`, `MaxHistoryLines`, `MaxHistoryBytes`, `MaxTerminalsPerSession`, `MaxLiveTerminals`), `ResolvedTerminalHistoryDirectory` ("terminals" next to the database, like the analytics DB) and `TerminalEnabled` (Decision 8).
    - **Moved to Task 3:** the debounced writes and the flush on close and shutdown. The terminal that owns the history owns the timer.

- [x] 3. Terminal service (done 2026-09-13)
  - **Files**: `src/WeaveFleet.Application/Terminals/ITerminalService.cs`, `TerminalService.cs`, `TerminalEnvironment.cs`, `TerminalShell.cs`; `src/WeaveFleet.Domain/Events/TerminalEvents.cs` plus `[JsonDerivedType]` entries; `FleetOptions.Terminal`; calls from `SessionOrchestrator.ArchiveSessionAsync` and `DeleteSessionAsync`; a hosted service that ends every shell and flushes history on shutdown.
  - **Acceptance**: Create → attach → write → output → resize → close works for the session owner only. A missing session folder fails with a clear message instead of starting in Fleet's own folder. Archive and delete end the session's shells and delete their scrollback. Limits return errors, not exceptions. The environment has no `Fleet__`, `FLEET_` or `ASPNETCORE_` keys. After a simulated restart, the saved terminal is listed as `exited`, and attaching starts a new shell under its scrollback.
  - **Tests**: application tests with a fake `IPtyFactory`, plus one test against the real PTY on Linux.
  - **As built**:
    - Two layers. `TerminalManager` (singleton, also `ISessionTerminalCleanup`) holds every live shell keyed by `(sessionId, terminalId)` and trusts its `TerminalContext(SessionId, UserId, Directory)`. `TerminalService` (scoped) is what endpoints call: it returns `NotFound` when terminals are off or the session isn't the caller's (the session repository is user-scoped), refuses a new shell for an archived session (`Unavailable`), and builds the context from `Session.Directory` (the worktree or clone folder).
    - `LiveTerminal` (internal) owns one shell: a read loop that appends cleaned output to the history and hands raw output to every client under one lock, so `Attach()` gets the scrollback and then live frames with no gap or repeat. Each client has a bounded queue of 64 frames; a client that falls behind gets the queued frames and then `TerminalClientTooSlowException`, which the socket (Task 4) turns into a close so the browser reconnects and replays.
    - `TerminalAttachment`: `Replay` (bytes), `Frames` (`ChannelReader<TerminalFrame>`: `Output`, `Cleared`, `Exited`), `WriteAsync`, `Resize`, `Clear`, `Dispose` to detach.
    - Scrollback saves 250 ms after output stops arriving. Killing a terminal stops the timer and waits for a save already running, so a closed terminal's `.log` can't reappear. `ShutdownAsync` (from `TerminalShutdownService`, an `IHostedService` in Infrastructure) saves, then ends each shell; after a restart the terminal lists as `Stopped`, and attaching starts a new shell under the old scrollback plus `RestartDivider`.
    - Shells: `TerminalShell.Candidates` tries `$SHELL`, zsh, bash, sh on Unix (zsh and bash with `-l`, because Fleet often runs as a service with a bare `PATH`), and pwsh, Windows PowerShell (`-NoLogo`), cmd on Windows; a shell that fails to start moves on to the next. Tab titles are the shell name, then `zsh 2`, `zsh 3`.
    - Environment: `TerminalEnvironment.Build` drops `Fleet__*`, `FLEET_*`, `WEAVE_FLEET_*`, `ASPNETCORE_*`, `DOTNET_ENVIRONMENT`, `DOTNET_URLS` and the OpenCode server variables, sets `TERM=xterm-256color`, `COLORTERM=truecolor`, `TERM_PROGRAM=WeaveFleet`, and `LANG=C.UTF-8` on Unix only when no locale is set.
    - Limits come from `FleetOptions.Terminal` and fail with `LimitReached` and a message saying what to close. A missing folder fails with `Unavailable` rather than starting in Fleet's own folder. Ids are `t_` plus a version-7 GUID.
    - `SessionOrchestrator` takes an optional `ISessionTerminalCleanup` (last constructor parameter, so the test builders didn't change). Delete ends the terminals before the workspace is cleaned up (a shell inside a worktree holds it open on Windows); archive ends them after the archive is written. Failures are logged, never thrown.
    - Events `TerminalOpened` and `TerminalClosed` (`TerminalPayload`) are registered in `DomainEvent`, `ApplicationJsonContext` and `SessionEventsHub.ResolveDomainEventType` already, ahead of Task 4.
    - Application exposes internals to `WeaveFleet.Infrastructure.Tests` too, so the real-shell test can swap the process environment.
    - Tests: 22 in `TerminalManagerTests`/`TerminalServiceTests` with a fake PTY and an in-memory store; one real-shell test in `TerminalManagerRealShellTests` (folder, no `Fleet__` leak, output survives a restart). Full Application (521), Infrastructure (796), Api (180) and Integration (76) suites pass.

- [x] 4. Endpoints and socket (done 2026-09-13)
  - **Files**: `src/WeaveFleet.Api/Endpoints/TerminalEndpoints.cs` (new); `EndpointExtensions.cs`; `Program.cs` (`UseWebSockets`); `SessionEventsHub.ResolveDomainEventType`; JSON context entries; `terminalEnabled` in the client config endpoint.
  - **Acceptance**: The REST calls and the socket protocol in Design. A foreign `Origin` gets 403 before the upgrade. Another user's session gets 404. The feature flag off gives 404 everywhere and `terminalEnabled: false`. A slow socket is dropped without slowing the others.
  - **Tests**: `tests/WeaveFleet.IntegrationTests/Terminals/` with a real Kestrel server and `ClientWebSocket`; a SignalR contract test for the three events.
  - **As built**:
    - `TerminalEndpoints` (in `apiScope`): `GET` and `POST /api/sessions/{id}/terminals`, `DELETE …/{terminalId}`, and `GET …/{terminalId}/socket?cols=&rows=` (left out of OpenAPI). Errors are `{ "error": "…" }`: `NotFound` 404, `Unavailable` and `LimitReached` 409, `SpawnFailed` 500. `POST` returns 201 with `TerminalResponse(id, title, status: "running" | "stopped", createdAt)`; its body `{ cols, rows }` is optional (120×30).
    - `TerminalSocket.RunAsync` sends the scrollback in 16 KB binary frames, then `{"type":"ready"}`, then pumps frames; `exit` carries `exitCode` (null when Fleet ended the shell). Input binary goes to the shell a fragment at a time; text control messages are capped at 4 KB. **Never cancel a pending WebSocket receive**: .NET aborts the socket, and the browser sees a reset instead of a close. So when the shell ends, the server sends its close frame, waits up to 2 s for the client's reply, then aborts. Close codes: 1000 (shell ended, or detached), 4001 (fell behind: reconnect), 1001 (Fleet stopping).
    - Origin check (`TerminalSocket.IsOriginAllowed`), stricter than the hub's, which allows any origin in local mode: no `Origin` (not a browser; auth still applies), the request's own host, `Auth.AllowedOrigins`, and in Development any `localhost`, `127.0.0.1` or `*.localhost` origin (the Vite dev server). A foreign origin gets 403 before the upgrade.
    - `app.UseWebSockets` (30 s keep-alive) after authorization. `ClientConfigResponse.TerminalEnabled` reports `FleetOptions.TerminalEnabled`.
    - Session events leave out null fields (the application JSON context's convention), so `terminal.closed` has no `exitCode` when Fleet ended the shell. The client treats a missing `exitCode` as null.
    - The history store now takes its folder from the `FleetOptions` in DI, so a host that swaps the options (the integration test server) writes where it expects.
    - Tests: 6 in `TerminalEndpointTests` on the existing `SignalRTestServer` (open, echo, resize, close with a clean 1000; a second connection gets the scrollback; foreign origin 403; unknown session 404 for REST and socket; `terminalEnabled`; `terminal.opened` and `terminal.closed` over the hub).

- [x] 5. Client API, socket and store (done 2026-09-13)
  - **Files**: `client/src/lib/terminal-api.ts`, `terminal-socket.ts`, `client/src/stores/terminals.ts`; mock REST in `vite-plugin-mock-api.ts`; the mock transport (the mockup's pretend shell); reducer cases for the three events; `bun add @xterm/xterm @xterm/addon-fit @xterm/addon-web-links`.
  - **Acceptance**: The socket reconnects with backoff and replays. Events keep the tab list in step across windows. Mock mode opens a working pretend shell with no server.
  - **Tests**: vitest for the store, the reducer cases and reconnect.
  - **As built**:
    - `lib/terminal-api.ts`: `listTerminals`, `createTerminal`, `closeTerminal` (404 counts as closed), `terminalSocketUrl`; failures throw `TerminalApiError` with the server's `error` text.
    - `lib/terminal-socket.ts`: `connectTerminal({ sessionId, terminalId, cols, rows, handlers })`. Every (re)connect calls `onReset`, then the scrollback arrives through `onOutput`, then `onReady`. It reconnects after any close but 1000 (250 ms, doubling to 8 s), keeps up to 4 KB typed while reconnecting and sends it after `ready`, and gives up with status `failed` after 5 attempts in a row that never got the scrollback (the terminal is gone, or the server refuses). `onExit(null)` means Fleet ended the shell.
    - `lib/terminal-mock-shell.ts` and `lib/terminal-connection.ts`: in mock mode, `openTerminalConnection` returns the mockup's pretend shell instead of a socket. It keeps each terminal's output in the page so reopening replays it, like the server does; control characters are built with `String.fromCharCode`, never written as escapes (see the control-character note in memory).
    - `stores/terminals.ts`: tabs and the active tab per session, drawer open per session (localStorage `weave:terminal-open`), one drawer height for the window (`weave:terminal-height`, default 272, minimum 140; the drawer clamps the maximum). A tab another window opens is added without taking focus.
    - `composables/use-session-terminals.ts`: `useSessionTerminals(sessionId)` loads the list on session open and reconnect and applies `terminal.opened`/`terminal.closed`, only when `terminalEnabled`; `openNewTerminal` (returns the error text to show) and `closeTerminalTab` (removes the tab at once, reloads if the server refuses). The drawer closes when its last tab goes.
    - `terminalEnabled` was added by hand to `ClientConfigResponse` in `api/generated/schema.d.ts` (earlier commits edit that file by hand too), and to the mock config. The mock plugin keeps each session's tabs and pushes the two events. Vite's `/api` proxy now forwards WebSockets.
    - `@xterm/xterm` 6.0.0, `@xterm/addon-fit` 0.11.0, `@xterm/addon-web-links` 0.12.0 in `package.json`, `bun.lock` and `package-lock.json` (`bunx npm install --package-lock-only`; the lockfile diff is xterm only).
    - Tests: 28 (store 7, socket 9, pretend shell 5, composable 7). Typecheck and lint clean.

- [x] 6. The drawer (done 2026-09-13)
  - **Files**: `client/src/components/terminal/TerminalDrawer.vue`, `TerminalView.vue`, `client/src/lib/terminal-theme.ts`; mount in `routes/sessions.$id.tsx`; the header button with its running dot; the `toggle-terminal` command (Ctrl/Cmd J) in `keybinding-types.ts`.
  - **Acceptance**: It matches the mockup in light and dark mode: 272 px by default, resizable from 140 px to 60% of the sheet, with the height remembered. Tabs, +, clear and hide work, and the size readout follows the fit addon. The button is hidden when `terminalEnabled` is false. Switching sessions switches terminals without killing them.
  - **Tests**: component tests; screenshots in mock mode against the mockup.
  - **As built**:
    - `TerminalDrawer.vue` sits after the chat or files view in `routes/sessions.$id.tsx`, keyed by session. It renders nothing until the session's drawer is first opened, and each tab's `TerminalView` mounts the first time that tab shows, then stays (hidden with `v-show`): visiting a session never starts a shell, and hiding the drawer or switching tabs doesn't reconnect. Opening an empty drawer starts a shell once the session's list has loaded (`store.isLoaded`), at 120×14 until the view measures itself. A refused start shows the server's message with **Try again**.
    - Header: tabs (`SquareTerminal`, title, close; arrows, Home/End, Delete, middle-click), **+**, the session folder (ellipsis on the left, full path in the title), `cols×rows`, **Clear**, **Hide**. The grip resizes by pointer (60% of the column at most) and by arrow keys (24 px); double-click resets to 272. No measured column means no cap (jsdom, or before layout).
    - `TerminalView.vue`: xterm 6 with fit and web-links (links open in a new tab), JetBrains Mono from `--font-mono-stack` loaded before measuring, 12 px at 1.2 line height, 5,000 lines of local scrollback. The background is transparent and the drawer paints `color-mix(--panel-bg 96%, --text)`. Shows **Reconnecting…** while reconnecting and a **Close tab** notice when the socket gives up.
    - `lib/terminal-theme.ts` builds the xterm theme from the live CSS variables (so every Fleet theme works): text, cursor, selection and the xterm 6 scrollbar from `--text`/`--accent-dim`, red/green/yellow/blue/magenta from `--error`/`--running`/`--idle`/`--complete`/`--queued`, the rest from a light or dark ANSI palette chosen by the panel's brightness. It re-applies when `resolvedThemeId` changes.
    - `TerminalToggleButton.vue` in the header's actions slot (hidden when `terminalEnabled` is false), pressed while open, with a green dot while shells run and the drawer is hidden.
    - `toggle-terminal` command (Ctrl/Cmd J, "Show/Hide Terminal" in the palette). Commands got `allowInEditable`, so Ctrl J works from the composer and from inside the terminal; other command shortcuts still ignore keys typed into fields. The command is off on `/sessions/new` and when terminals are off.
    - Test setup gives every test a fresh Pinia and installs it into mounted components, so component tests must not call `setActivePinia(createPinia())` themselves. jsdom's localStorage (Node 22) outlives a test, so the terminal tests clear it; Node 26 has none (`globalThis.localStorage?.clear()`).
    - Tests: drawer 9, toggle 3, theme 12 (and the store tests). The whole client suite passes on Node 22 (467); on Node 26, 46 tests in untouched files fail on `localStorage.clear()`, the known local-only issue. Typecheck clean; no lint warnings in the changed files; the design linter's 9 findings are all in untouched files.

- [x] 7. Keyboard hand-off (done 2026-09-13)
  - **Files**: `TerminalView.vue` (`attachCustomKeyEventHandler`), `use-commands.ts`, `use-keyboard-shortcut.ts`, `StatusBar.vue`.
  - **Acceptance**: With the terminal focused, Esc, Ctrl K, Ctrl B and Ctrl [ ] reach the shell and don't trigger Fleet. Ctrl J and Ctrl Shift B still reach Fleet. Copy and paste follow Decision 9. The status bar shows "Terminal has the keyboard" while it has focus.
  - **Tests**: vitest for the global handlers ignoring events from inside the terminal.
  - **As built**:
    - `lib/terminal-keys.ts` `terminalKeyOwner(event, isMac, passThrough)` decides: `fleet` for the user's `toggle-terminal` and `toggle-right-panel` bindings (rebinding them is followed), `copy`/`paste` for Ctrl Shift C/V off a Mac, `shell` for everything else, Ctrl C included.
    - `TerminalView` uses it twice: xterm's `attachCustomKeyEventHandler` ignores `fleet` and `paste` keys (the browser pastes and xterm's paste handler sends it) and copies the selection for `copy`; and a keydown listener on the terminal's box stops propagation for everything but `fleet`, so Fleet's document-level shortcuts (Esc to interrupt, Ctrl K for the palette, …) never see a key typed into the terminal. That doesn't depend on what xterm does with the event.
    - The status bar shows "Terminal has the keyboard · Esc Ctrl K Ctrl B go to the shell · Ctrl J Hide terminal · Ctrl Shift C Copy" (⌘ on a Mac) while a terminal has focus, from `terminals.focused`; the drawer clears it when it's hidden or unmounted. The usual hints gain "Ctrl J Terminal" when terminals are on.
    - Checked in the browser in mock mode: Ctrl J from the composer opens the drawer with focus in the terminal; Esc and Ctrl K in the terminal don't open the palette; Ctrl J in the terminal hides it and the status bar returns; Ctrl K elsewhere still opens the palette. There's no `useCommands` test harness to extend, so the global handler's `allowInEditable` is covered by that browser check.

- [x] 8. Select lines → message (done 2026-09-13)
  - **Files**: a selection popover in `TerminalView.vue`; terminal lines in the draft (next to `useDraftAttachments`); chips in `Composer.vue`; `client/src/lib/format-terminal-context.ts`.
  - **Acceptance**: Selecting output shows "Add lines 9–11 to message". The chip shows the tab name and line range, can be removed, and survives a session switch like the rest of the draft. On send, each attachment becomes a fenced block with its label before the message text.
  - **Tests**: vitest for the formatter and the draft round trip.
  - **As built**:
    - Selecting output in `TerminalView` shows a bubble above the selection (below it when the selection is at the top): **Add lines 13–15 to message** and **Copy**. The lines are whole rows, numbered from the top of the scrollback; a row that continues a wrapped line is joined back onto it, and a selection ending at the very start of a row leaves that row out.
    - The drawer adds them to the session's draft (`composables/use-draft-terminal-context.ts`, in memory per session like image attachments; the same lines of the same terminal are only added once) and focuses the composer through `weave:command-focus-prompt`.
    - `Composer.vue` shows a chip per attachment (terminal, line range, remove; the tooltip holds the text) and counts them as content, so they can be sent with nothing typed. On send they go in front of the typed text as fenced blocks (`lib/format-terminal-context.ts`: "Terminal zsh, lines 9–11:" then a `text` fence longer than any run of backticks inside). A refused send puts the draft back without them in the text; a message queued while the agent is busy carries them too. Slash commands ignore them.
    - Found while checking this in the browser: xterm blends a translucent selection colour with its own background, which is transparent here, so the light theme's selection came out nearly black. The theme now uses an opaque mix of `--accent` into the panel colour (`mix`), and a text-tinted one for an unfocused selection.
    - Tests: formatter 5, draft 3, composer 3 (sent before the typed text, sent alone, removed), theme 15. The whole client suite passes on Node 22 (491). Checked in the browser in mock mode: selecting the vitest failure shows "Add lines 13–15 to message"; clicking it adds a "zsh lines 13–15" chip holding the three lines and focuses the composer.

- [x] 9. End to end, screenshots and checklist (written 2026-09-13; the E2E test runs in CI only)
  - **What**: A Playwright test on Linux (scratch `HOME`): open the drawer, run `echo fleet-e2e`, see it; reload and see it again; close the tab and confirm the process is gone. Screenshots in `mockups/terminal/` next to the mockup. Hand the user the checklist below.
  - **As built**:
    - `tests/WeaveFleet.E2E/Tests/TerminalDrawerTests.cs`, Workflow lane, Linux only: Ctrl J opens the drawer with one tab; `echo fleet-e2e-$((6*7))` prints 42; after a reload the drawer is still open and the output is back; closing the tab leaves `GET …/terminals` empty. A second test checks Ctrl K and Esc typed in the terminal don't open the palette and Ctrl J hides the drawer. It builds, but it hasn't run: the E2E project boots `Program`, so it only runs in PR CI (see Safety). "The process is gone" is covered by the manager and endpoint tests rather than from the browser.
    - Screenshots from Vite mock mode (the pretend shell), next to the mockup: `drawer-closed-light.png`, `drawer-open-light.png`, `drawer-open-dark.png`, `drawer-keyboard-light.png` (status bar while the terminal has the keyboard), `drawer-selected-light.png` (the "Add lines 13–15 to message" bubble), `drawer-attached-dark.png` (the chip in the composer).
    - Left before merge: push the branch and open a PR so CI runs the Api, Integration and E2E tests; the macOS and Windows checklist below.

## Dependencies and order
0 → 1 → 3 → 4. 2 can run alongside 1. Client work (5–8) starts once 4's protocol is fixed, and runs against mock mode until then. 9 comes last.

## Risks
- **Spawning a shell from .NET on Unix.** `fork` in a multi-threaded runtime is unsafe unless the child calls `exec` straight away. Task 0 decides between a library and our own interop before any product code depends on it.
- **macOS and Windows aren't run here or in CI.** The checklist below is the gate. ConPTY needs Windows 10 1809 or later.
- **AOT.** A PTY library that uses reflection or marshalling that can't be generated at compile time fails only in Release. Task 0 publishes AOT on purpose.
- **Remote access.** With token auth over a LAN, the terminal is a remote shell. That's the same power the agents already have, but the Origin check and ownership check must hold.
- **Keyboard conflicts** beyond the ones listed will show up in use. Decision 9's rule (the shell wins while focused, except Ctrl J and Ctrl Shift B) is the fallback.

## macOS and Windows checklist (for the user, before merge)
1. Open a session, press Ctrl J (⌘ J), and check the prompt starts in the session's folder.
2. Run `vim` or `less` in the terminal. Esc should work inside it and must not interrupt the session.
3. Resize the drawer, then run `tput cols; tput lines` (`$Host.UI.RawUI.WindowSize` in PowerShell). The size should match the readout.
4. Start `bun run dev` (or any long command). Hide the drawer; the header dot stays on. Close the tab; the process is gone from Activity Monitor or Task Manager.
5. Reload the page; the scrollback is still there. Quit and restart Fleet; reopening the terminal shows the old output, then `— Fleet restarted —`, then a new prompt.
6. Select two lines, add them to your message, and send. The message shows them in a fenced block.

## Task 0 findings (2026-09-13)
**Use `Porta.Pty` 2.2.2** (MIT, github.com/tomlm/Porta.Pty, targets `net10.0`). No interop of our own.

- **Why it's safe on Unix.** It forks and execs inside a small native shim (`libporta_pty.so`, `libporta_pty.dylib`), not in managed code, so the multi-threaded `fork` risk is gone. The shell is a session leader with the PTY as its controlling terminal (`tty` prints `/dev/pts/N`, and a child's session id equals the shell's pid).
- **Windows.** It depends on `Microsoft.Windows.Console.ConPTY` 1.24.260710001 and copies the out-of-band `conpty.dll` plus `x64/OpenConsole.exe` and `arm64/OpenConsole.exe` into a RID-specific publish, falling back to the in-box console host when they're missing. Fleet publishes per RID, so it gets the out-of-band host.
- **Linux results**, under the JIT and again as a Release `PublishAot` binary for `linux-x64` (no warnings): starts in the given folder; `echo` round trip; `stty size` shows the initial 30×100 and then 40×132 after `Resize(132, 40)`; `exit 3` reports 3; UTF-8 (`café ✓`) comes through; `Kill()` fires `ProcessExited` and ends the shell, a foreground `sleep` and a background `sleep &`.
- **macOS and Windows** were published trimmed from Linux (a real AOT build needs each OS, which the release workflow already uses). No trim warnings, and the native pieces land in the output: `libporta_pty.dylib` for `osx-arm64`; `conpty.dll` and both `OpenConsole.exe` for `win-x64`. They still need the checklist on real machines.
- **Packaging.** `scripts/package.sh` and `package.ps1` copy the whole publish folder into `app/`, so the native files ship without changes.
- **API** (for Task 1): `PtyProvider.SpawnAsync(PtyOptions { Name, Cols, Rows, Cwd, App, CommandLine, Environment })` returns `IPtyConnection` with `ReaderStream`, `WriterStream`, `Pid`, `ExitCode`, `ProcessExited`, `Resize(cols, rows)`, `Kill()`. `PtyOptions.Environment` adds to the parent's environment, and an empty value removes a key; that's how Task 3 strips `Fleet__*` and `ASPNETCORE_*`.
- **Things to handle in Task 1.** After `Kill()`, `ProcessExited` reports exit code 0, so the wrapper must remember that Fleet killed it rather than trust the code. A child started with `nohup` or `setsid` survives, the same as closing a terminal window; that's expected.
- **Scratch spikes fill `/tmp`.** A self-contained publish per RID is about 150 MB and hit the tmpfs quota. Delete `bin/`, `obj/` and outputs between runs.
