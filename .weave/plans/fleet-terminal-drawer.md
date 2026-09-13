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
- **Safety.** Never run the Fleet API with the real `HOME`: `LegacyDataMigrator` deletes the installed Fleet's `~/.weave/fleet.db`. Use a scratch `HOME` for anything that starts the API.

## Decisions (2026-09-13)
1. **A drawer under the chat, not a canvas tab** (the user's choice). It spans the conversation column only, so the right panel stays visible beside it.
2. **Terminals belong to a session.** Each shell starts in the session's folder, and switching sessions switches the drawer. This was the mockup's recommendation, and the user approved the mockup.
3. **The agent never sees the terminal.** Terminal output reaches the agent only when the user selects lines and sends them. There's no agent tool in this plan.
4. **All three platforms.** Linux is built and tested here. The user runs the macOS and Windows checklist at the end of this plan before merge.
5. **Output doesn't go through SignalR or domain events.** Each attached terminal gets a raw WebSocket: binary frames carry bytes, text frames carry small JSON control messages. Only lifecycle changes (opened, exited, closed) go out as session events, so a second window sees tabs appear and disappear.
6. **Scrollback is saved to disk** under app data: at most 5,000 lines or 2 MB per terminal. Query sequences are stripped before saving. It's deleted when the tab closes or the session is archived or deleted.
7. **After a Fleet restart, reopening a saved terminal starts a new shell under its old scrollback**, with a dim `— Fleet restarted —` line between them. Shells themselves don't survive a restart.
8. **Off when Fleet is hosted** (`Auth.Enabled`): a shell there runs as the service user on a shared machine. Local mode keeps it on, including remote access with a token, because the agents there already run shell commands with the same rights. `FleetOptions.Terminal.Enabled` overrides the default either way.
9. **Keys.** `Ctrl J` (`⌘ J` on a Mac) shows and hides the drawer. While the terminal has focus, Esc, Ctrl K, Ctrl B and Ctrl [ ] go to the shell. Fleet keeps Ctrl J and Ctrl Shift B. Copy is Ctrl Shift C on Linux and Windows, ⌘ C on a Mac. The status bar says when the terminal has the keyboard.
10. **Tab names are the shell name** (`zsh`, `zsh 2`). Naming tabs after the running command, and asking before closing a busy tab, come later (the mockup marked them Later).

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

**Events** on `session:{id}`: `terminal.opened`, `terminal.exited` (with `exitCode`) and `terminal.closed`, each carrying `{ sessionId, terminalId, title }`. They aren't persisted.

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

- [ ] 0. Spike: a PTY in .NET that survives AOT
  - **What**: In a scratch project outside the solution, try `Porta.Pty` 2.2.2 and a small interop of our own (Unix `posix_openpt`, `grantpt`, `unlockpt`, `ptsname`, then spawn the shell as a session leader on the slave; Windows `CreatePseudoConsole`). On Linux check: (a) `$SHELL` starts in a given folder with a given size; (b) write `echo hi\r` and read `hi` back; (c) resize, then `stty size` prints the new size; (d) `exit 3` reports exit code 3; (e) killing the shell also kills a child `sleep 1000`; (f) `dotnet publish -c Release -r linux-x64 -p:PublishAot=true` builds with no new warnings, and the published binary does (a)–(e). Also build the macOS and Windows paths for `osx-arm64` and `win-x64` to catch compile and trim warnings there. Check licences.
  - **Output**: "Task 0 findings" at the end of this plan, with the choice for Task 1. Spawning a managed process with `fork` in a multi-threaded runtime is the main risk to look at.
  - **Depends on**: None.

- [ ] 1. PTY layer
  - **Files**: `src/WeaveFleet.Application/Terminals/IPtyFactory.cs`, `IPtyProcess.cs` (new); `src/WeaveFleet.Infrastructure/Terminals/` (Unix and Windows implementations per Task 0); registration in `DependencyInjection.cs`.
  - **Acceptance**: Task 0's (a)–(e) as tests. Output arrives as bytes, split wherever the OS splits it. Exit fires once. `Dispose` kills the tree and never throws.
  - **Tests**: `tests/WeaveFleet.Infrastructure.Tests/Terminals/` on Linux, marked so they're skipped on other OSes until the checklist runs.

- [ ] 2. Scrollback: history, cleaning and files
  - **Files**: `src/WeaveFleet.Application/Terminals/TerminalHistory.cs`, `TerminalReplaySanitizer.cs`; `src/WeaveFleet.Infrastructure/Terminals/TerminalHistoryStore.cs` (new).
  - **Acceptance**: History keeps the last 5,000 lines or 2 MB, whichever is smaller, without splitting a UTF-8 sequence or an escape sequence. The sanitizer strips query sequences even when one arrives split across two chunks, and keeps colours, cursor moves and titles. Writes to disk are debounced (about 250 ms) and flushed on close and on shutdown.
  - **Tests**: unit tests with byte-level fixtures, including sequences split across chunks, following t3code's `Manager.test.ts` cases.

- [ ] 3. Terminal service
  - **Files**: `src/WeaveFleet.Application/Terminals/ITerminalService.cs`, `TerminalService.cs`, `TerminalEnvironment.cs`, `TerminalShell.cs`; `src/WeaveFleet.Domain/Events/TerminalEvents.cs` plus `[JsonDerivedType]` entries; `FleetOptions.Terminal`; calls from `SessionOrchestrator.ArchiveSessionAsync` and `DeleteSessionAsync`; a hosted service that ends every shell and flushes history on shutdown.
  - **Acceptance**: Create → attach → write → output → resize → close works for the session owner only. A missing session folder fails with a clear message instead of starting in Fleet's own folder. Archive and delete end the session's shells and delete their scrollback. Limits return errors, not exceptions. The environment has no `Fleet__`, `FLEET_` or `ASPNETCORE_` keys. After a simulated restart, the saved terminal is listed as `exited`, and attaching starts a new shell under its scrollback.
  - **Tests**: application tests with a fake `IPtyFactory`, plus one test against the real PTY on Linux.

- [ ] 4. Endpoints and socket
  - **Files**: `src/WeaveFleet.Api/Endpoints/TerminalEndpoints.cs` (new); `EndpointExtensions.cs`; `Program.cs` (`UseWebSockets`); `SessionEventsHub.ResolveDomainEventType`; JSON context entries; `terminalEnabled` in the client config endpoint.
  - **Acceptance**: The REST calls and the socket protocol in Design. A foreign `Origin` gets 403 before the upgrade. Another user's session gets 404. The feature flag off gives 404 everywhere and `terminalEnabled: false`. A slow socket is dropped without slowing the others.
  - **Tests**: `tests/WeaveFleet.IntegrationTests/Terminals/` with a real Kestrel server and `ClientWebSocket`; a SignalR contract test for the three events.

- [ ] 5. Client API, socket and store
  - **Files**: `client/src/lib/terminal-api.ts`, `terminal-socket.ts`, `client/src/stores/terminals.ts`; mock REST in `vite-plugin-mock-api.ts`; the mock transport (the mockup's pretend shell); reducer cases for the three events; `bun add @xterm/xterm @xterm/addon-fit @xterm/addon-web-links`.
  - **Acceptance**: The socket reconnects with backoff and replays. Events keep the tab list in step across windows. Mock mode opens a working pretend shell with no server.
  - **Tests**: vitest for the store, the reducer cases and reconnect.

- [ ] 6. The drawer
  - **Files**: `client/src/components/terminal/TerminalDrawer.vue`, `TerminalView.vue`, `client/src/lib/terminal-theme.ts`; mount in `routes/sessions.$id.tsx`; the header button with its running dot; the `toggle-terminal` command (Ctrl/Cmd J) in `keybinding-types.ts`.
  - **Acceptance**: It matches the mockup in light and dark mode: 272 px by default, resizable from 140 px to 60% of the sheet, with the height remembered. Tabs, +, clear and hide work, and the size readout follows the fit addon. The button is hidden when `terminalEnabled` is false. Switching sessions switches terminals without killing them.
  - **Tests**: component tests; screenshots in mock mode against the mockup.

- [ ] 7. Keyboard hand-off
  - **Files**: `TerminalView.vue` (`attachCustomKeyEventHandler`), `use-commands.ts`, `use-keyboard-shortcut.ts`, `StatusBar.vue`.
  - **Acceptance**: With the terminal focused, Esc, Ctrl K, Ctrl B and Ctrl [ ] reach the shell and don't trigger Fleet. Ctrl J and Ctrl Shift B still reach Fleet. Copy and paste follow Decision 9. The status bar shows "Terminal has the keyboard" while it has focus.
  - **Tests**: vitest for the global handlers ignoring events from inside the terminal.

- [ ] 8. Select lines → message
  - **Files**: a selection popover in `TerminalView.vue`; terminal lines in the draft (next to `useDraftAttachments`); chips in `Composer.vue`; `client/src/lib/format-terminal-context.ts`.
  - **Acceptance**: Selecting output shows "Add lines 9–11 to message". The chip shows the tab name and line range, can be removed, and survives a session switch like the rest of the draft. On send, each attachment becomes a fenced block with its label before the message text.
  - **Tests**: vitest for the formatter and the draft round trip.

- [ ] 9. End to end, screenshots and checklist
  - **What**: A Playwright test on Linux (scratch `HOME`): open the drawer, run `echo fleet-e2e`, see it; reload and see it again; close the tab and confirm the process is gone. Screenshots in `mockups/terminal/` next to the mockup. Hand the user the checklist below.

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
