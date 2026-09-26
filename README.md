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

- `http://127.0.0.1:6262`

Health check:

```bash
curl -fsS http://127.0.0.1:6262/healthz
```

### From another device

`fleet --host 0.0.0.0` listens on every address, so you can open Fleet from another machine on your network. Fleet then asks every request for its access token, its own machine's included; open the `/login?token=…` link it prints once.

Another Fleet can add this one as a machine (Settings → Machines) and work in its sessions. Behind `tailscale serve` or another reverse proxy, keep Fleet on loopback and start it with `--require-token`. See [docs/machines.md](docs/machines.md).

App previews in the Browser tab follow Fleet: each preview gets its own port on the address Fleet listens on. With `--host 0.0.0.0`, anyone who can reach this machine can open a running preview without signing in to Fleet, so only do this on a network you trust. The app itself stays on `localhost`. To let previews through a firewall, give them a fixed range with `Fleet__Browser__PortRange=41000-41099`.

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
