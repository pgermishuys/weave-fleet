# Weave Fleet

Installable local Fleet server builds are published through GitHub Releases.

## Install

### macOS / Linux (`linux-x64`, `osx-arm64`)

```bash
curl -fsSL https://github.com/pgermishuys/fleet-releases/releases/latest/download/install.sh | sh
export PATH="$HOME/.weave/fleet/bin:$PATH"
fleet version
```

### Windows PowerShell (`win-x64`, `win-arm64`)

```powershell
irm https://github.com/pgermishuys/fleet-releases/releases/latest/download/install.ps1 | iex
fleet version
```

## Run

```bash
fleet
```

Default URL:

- `http://127.0.0.1:5000`

Health check:

```bash
curl -fsS http://127.0.0.1:5000/healthz
```

## Common commands

- `fleet version`
- `fleet update`
- `fleet uninstall`

## Supported release artifacts

- `linux-x64`
- `osx-arm64`
- `win-x64`
- `win-arm64`

## Releasing

Release workflow details live in [`RELEASE.md`](./RELEASE.md).
