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
| `src/preload.ts` | `window.fleetDesktop`, the bridge the UI can use |
| `static/` | The splash and error pages, and the icons |
