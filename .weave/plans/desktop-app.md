# Fleet desktop app

Fleet as an installed app on macOS, Windows and Linux, like t3code's. The app is an Electron
shell around the Fleet binary we already ship. If no Fleet is running, it starts the bundled
binary. If one is (your `fleet.service`, say), it connects to that one. The window loads the UI
from Fleet itself, the same page a browser gets.

Scope: https://claude.ai/code/artifact/7d38c9bc-078f-47f0-8bc5-1ae0c1ad6e14

## Decisions already made (2026-09-14)

- **Electron, modelled on t3code's `apps/desktop`.** Not Tauri. The dead Tauri code in the
  client goes.
- **The UI comes from the server**, not bundled into the app. No `fleet://` scheme, no CORS or
  absolute hub URL changes.
- **One data directory.** The app uses `~/.weave`, the same as CLI installs, so you can move
  between the app and the service. The lock below makes that safe.
- **Closing the window.** If the app started Fleet, closing hides to the tray while any session
  is working, and "Quit Fleet" stops everything. If it connected to a running Fleet, closing
  just closes.
- **Platforms:** linux-x64, osx-arm64, win-x64, win-arm64. Intel Macs only if asked.
- **Unsigned first.** Signing is optional per platform, as in t3code's pipeline: without the
  Apple or Azure secrets, builds still publish unsigned. Adding the secrets turns signing on.
  Unsigned macOS can't auto-update, so the app says when a new version is out instead.

## Phase 0: server groundwork

All in the server. Worth having even if the app never ships.

1. **One Fleet per database.** Fleet takes an exclusive lock on `<db name>.lock` next to its
   database (`~/.weave/fleet.lock` for installs) before the migrator, the orphan kill or anything
   else touches the data. A second Fleet on the same database prints who holds it and exits with
   code 75. The lock is released when the process ends, however it ends.
   - Test hosts (the `Testing` environment) skip it: `WebApplicationFactory` runs `Program`
     past `Build`, integration tests run several hosts in one process, and many of them share
     the default database path while their real database comes from DI. The lock is named after
     the database file, not the directory, because test databases share the temp directory.
2. **Instance file.** Once Kestrel is listening, Fleet writes `<db name>.instance.json` next to
   the lock: pid, loopback URL, version, database path, whether it runs for the app, start time.
   Owner-only permissions on Unix. Deleted on shutdown; a leftover one is stale, since whoever
   holds the lock rewrites it. No token: loopback requests already sign in on their own.
3. **The legacy backup never deletes a live database.** `BackupLegacyAgentDb` moves
   `~/.weave/fleet.db` aside when Fleet's own database is elsewhere (a `dotnet run` with the
   default path, say). It now skips when another Fleet holds that database's lock.
4. **Desktop mode** (`Fleet:Desktop:Enabled`, set by the app):
   - Update checks and downloads are off; `/api/update/status` reports `managed` and Settings
     says updates come from the Fleet app. Staging would write inside the app bundle.
   - Fleet exits when its standard input closes. The app holds a pipe to it, so a crashed app
     never leaves a Fleet behind holding the lock.
5. **`ELECTRON_*` never reaches agents or terminals.** Added to `TerminalEnvironment`'s Fleet-owned
   list. `ELECTRON_RUN_AS_NODE` leaking into a shell makes every Electron app it starts act as
   Node.

## Phase 1: the app (`desktop/`)

- Start or attach: read `~/.weave/fleet.instance.json`; if that Fleet answers `/readyz`, load it.
  Otherwise pick a free port, start the bundled binary with the launcher's environment
  (`Fleet__DatabasePath` etc. under `~/.weave`), `Fleet__Desktop__Enabled=true` and a stdin
  pipe, show a splash until `/readyz`, then load the UI. Exit code 75 means someone else took the
  lock in between: attach instead.
- Supervise: server output to a log file, restart after a crash with backoff, an error page
  with the log path when it keeps failing.
- Login-shell PATH (t3code's `DesktopShellEnvironment`), so `opencode` is found when started
  from the Dock or a menu.
- Single instance, remembered window bounds, links open in the system browser, a real Edit
  menu, tray while sessions work.
- `window.fleetDesktop` bridge (version, update state, open logs) replacing the Tauri helpers.
- Dev loop against a scratch `HOME` only.

### Phase 1 as built (2026-09-14)

- **No login token.** Fleet signs in every loopback request in local mode (`BearerTokenHandler`), the window's
  and the main process's alike, so the app passes none and the instance file carries none.
- **A stable port.** The app's own Fleet prefers the port it used last time (5000 the first time), so the UI's
  local storage (theme, drafts, remembered choices), which is per origin, survives restarts.
- **Closing.** macOS: the window hides and the app stays in the Dock. Windows and Linux: with sessions working in
  the app's own Fleet (`GET /api/desktop/status`, from the activity tracker, counting `busy` and `retry`), the
  window hides to the tray with a notification; otherwise the app quits. Quitting with sessions working asks
  first. Opening the app again shows a hidden window. A connected-to Fleet is never stopped.
- **Crashes.** A crashed Fleet restarts on the same port (immediately, then 1 s, 2 s, 4 s; it gives up after five
  crashes in a minute and shows the error page). If the app dies, Fleet sees its stdin close and stops.
- **Right-click menu** with spelling suggestions, cut/copy/paste and link actions: Electron has none by default.
- **Not done yet:** client code using `window.fleetDesktop` (it arrives with updates in Phase 3), an app ID for
  Windows notifications (Phase 2, with electron-builder's `appId`), and a real icon (the one in `static/` is cut
  from the Weave logo). Only Linux has been run; macOS and Windows need a run on those machines.
- **Checked live** under `xvfb` with Playwright driving the real app and a scratch `HOME` (27 checks: splash, own
  Fleet in desktop mode on `~/.weave`, login-shell PATH reaches Fleet and `ELECTRON_*` doesn't, links to the
  browser, quit stops Fleet, crash restart on the same port, app crash stops Fleet, attach without starting a
  second Fleet, error page, tray on close with sessions working, quit confirmation).

## Phase 2: packaging and CI

electron-builder with the AOT publish output as `extraResources`; `.dmg` + `.zip`, NSIS x64 and
arm64, AppImage + `.deb`; a packaging job per platform in `release.yml` uploading next to
today's archives; a headless smoke test (xvfb, scratch `HOME`, `/readyz`, quit, lock released).

## Phase 3: updates and signing

electron-updater against `fleet-releases` (Linux and Windows unsigned; macOS once signed), a
warning before an update interrupts working sessions, signing turned on by secrets.

## Phase 4 (optional)

Homebrew cask, winget, AUR.
