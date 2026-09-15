# Fleet desktop app

An Electron shell around the Fleet server. If a Fleet is already running on your data (it finds it through
`~/.weave/fleet.instance.json`), the app connects to it. Otherwise it starts the bundled Fleet in desktop mode and
stops it when you quit. The window loads the UI from Fleet itself. Plan: `.weave/plans/desktop-app.md`.

## Develop

```sh
cd client && bun install && bun run build          # the UI, copied into src/WeaveFleet.Api/wwwroot
dotnet build src/WeaveFleet.Api -c Release          # the server the app starts in development
cd desktop && bun install && bun run dev
```

In development the app starts `src/WeaveFleet.Api/bin/Release/net10.0/<rid>/WeaveFleet.Api` with
`src/WeaveFleet.Api` as its content root. Override with `FLEET_DESKTOP_SERVER_BIN` (and
`FLEET_DESKTOP_SERVER_CONTENT_ROOT`).

**Use a scratch data directory.** By default the app uses `~/.weave`, the same data as an installed Fleet. Point it
somewhere else while developing:

```sh
FLEET_DESKTOP_DATA_DIR=/tmp/fleet-dev bun run dev
```

## Package

```sh
dotnet publish src/WeaveFleet.Api -c Release -r linux-x64 -o /tmp/fleet-server   # AOT in CI; add -p:PublishAot=false locally
cd desktop && FLEET_SERVER_DIR=/tmp/fleet-server bun run dist -- --linux --x64
xvfb-run -a node scripts/smoke.mjs release/linux-unpacked/fleet-desktop --no-sandbox
```

Installers land in `release/`. CI builds all four platforms in `.github/workflows/desktop-packages.yml`, and the
Release workflow publishes them.

## Test

```sh
bun run typecheck
bunx vitest run
```

## Files

| File | What it does |
|---|---|
| `src/main.ts` | Starts or connects, owns the window, tray and quit rules |
| `src/server.ts` | Starts the bundled Fleet, logs it, restarts it after a crash, stops it by closing its stdin |
| `src/instance.ts` | Reads the instance file a running Fleet writes |
| `src/shell-env.ts` | Takes PATH from your login shell, so agents find `opencode`, `node` and `git` |
| `src/links.ts` | Fleet's pages stay in the window; web links open in your browser |
| `src/updates.ts` | Self-update: installs on Windows and from the AppImage, links to the download on macOS and the `.deb` |
| `src/preload.ts` | `window.fleetDesktop`, the bridge the UI uses (Settings → System shows the app's updates) |
| `static/` | The splash and error pages, and the icons |
| `electron-builder.config.js` | Installers, with the server under `resources/fleet` |
| `scripts/smoke.mjs` | Starts a packaged app, checks its Fleet, kills the app, checks Fleet stopped |
| `scripts/merge-update-manifests.mjs` | Merges the two Windows update feeds into one `latest.yml` at release |

To try an update against a local feed, serve a newer build's `release/` folder and start the app with
`FLEET_DESKTOP_UPDATE_URL=http://127.0.0.1:<port>`. `FLEET_DESKTOP_UPDATES=off` turns updates off.
