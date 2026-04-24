# Weave Fleet

Installable local Fleet server builds are published through GitHub Releases.

## Install

### macOS / Linux (`linux-x64`, `osx-arm64`)

```bash
curl -fsSL https://github.com/pgermishuys/weave-fleet/releases/latest/download/install.sh | sh
export PATH="$HOME/.weave/weave-fleet/bin:$PATH"
weave-fleet version
```

### Windows PowerShell (`win-x64`)

```powershell
irm https://github.com/pgermishuys/weave-fleet/releases/latest/download/install.ps1 | iex
weave-fleet version
```

## Run

```bash
weave-fleet
```

Default URL:

- `http://127.0.0.1:5000`

Health check:

```bash
curl -fsS http://127.0.0.1:5000/healthz
```

## Common commands

- `weave-fleet version`
- `weave-fleet update`
- `weave-fleet uninstall`

## Supported release artifacts

- `linux-x64`
- `osx-arm64`
- `win-x64`

## Releasing

Release workflow details live in [`RELEASE.md`](./RELEASE.md).
