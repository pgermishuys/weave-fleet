# Machines

Fleet can work with sessions on more than one computer, and on your phone (see [phone.md](phone.md)). Every machine runs its own Fleet and keeps its own
sessions, repositories, worktrees, harness installs and provider credentials. A session stays on the machine it
started on. A client (the web app today, native apps later) keeps a list of machines, shows every machine's
sessions, and works in one of them at a time.

This page has two parts: how to make a machine reachable, and the contract a client uses to talk to one.

## Making a machine reachable

A Fleet only listens on `127.0.0.1` by default, so nothing else can reach it. There are two ways to open it up.
Both assume the devices can already reach each other, for example over [Tailscale](https://tailscale.com).

### Listen on the network

```sh
fleet --host 0.0.0.0 --port 2113
```

Bound to anything other than loopback, Fleet asks every request for its access token, its own machine's
included. Open the `/login?token=…` link Fleet prints at startup once, and the browser remembers the sign-in.

On a tailnet, `--host <the machine's 100.x address>` listens only there.

### Behind `tailscale serve` (HTTPS)

```sh
fleet --port 2113 --require-token
tailscale serve --bg --https=443 http://127.0.0.1:2113
```

`tailscale serve` gives the machine a real certificate on `https://<machine>.<tailnet>.ts.net`. HTTPS
certificates have to be switched on for the tailnet (admin console → DNS → HTTPS Certificates). The proxy
connects to Fleet over loopback, so **`--require-token` is required**. Without it a loopback-bound Fleet signs in
every request that arrives over loopback, and through the proxy that means every device on the tailnet. Fleet
also refuses auto sign-in to any loopback request that carries a proxy's forwarding headers, but don't rely on
that alone.

Use HTTPS when the client page is served over HTTPS. A browser won't let an `https://` page call an `http://`
machine.

### The access token

Settings → Machines → **This machine** shows the token and the addresses other devices can use. Fleet keeps the
token next to its database (`fleet.machine.json`, readable only by you), so it survives restarts. **Replace
token** there locks out every device that has the old one.

`WEAVE_FLEET_AUTH_TOKEN` (16 characters or more) fixes the token instead. Then only changing the variable changes
it.

The token is a full grant: whoever has it can do anything the Fleet can, including reading files, running
agents and opening terminals. Treat it like a password.

### Run a node without the web UI

A machine that only does the work, such as a server or a Mac mini under the desk, doesn't need Fleet's web app.
Start it as a node:

```sh
fleet node --host 0.0.0.0 --port 2113
```

A node is the same Fleet API, hub and terminals, with two differences:

- **No web app.** A browser at `/` gets a short page saying it's a Fleet node and how to add it; anything else
  gets the same as JSON (`{ kind: "fleet-node", message, docs }`). Other pages are `404`.
- **The token on every request**, whatever address it listens on, this machine's own requests included (as
  `--require-token`).

At startup it prints its addresses and its token. Add it from another Fleet in Settings → Machines → **Add a
machine**, and work in its sessions from there. The token is kept next to its database, as on any Fleet (see
[The access token](#the-access-token)). `fleet node` takes `--host`, `--port`, `--data-dir` and `--profile`, like
`fleet`. Behind `tailscale serve`, leave out `--host`.

A node can't pair a phone itself (`POST /api/machine/pairing` returns `409`), because the pairing link opens a page
it doesn't serve. Pair the phone with the Fleet that has the node in its list; the phone reaches the node through it.

### Keep a node running

`fleet node install-service` keeps the node running: now, when you log in, and after a crash. It takes the same
options as `fleet node`:

```sh
fleet node install-service --host 0.0.0.0 --port 2113
```

| | What it installs | Logs |
|---|---|---|
| Linux | a systemd user service, `fleet-node.service` | `journalctl --user -u fleet-node` |
| macOS | a LaunchAgent, `io.tryweave.fleet-node` | `~/Library/Logs/fleet-node.log` |
| Windows | a scheduled task, **Fleet node**, that starts when you log on | |

- **It runs as you**, so harness sign-ins, git credentials and repositories work as they do in your shell. On
  Linux and macOS it keeps the `PATH` you installed it with. On a Linux machine nobody logs in to, also run
  `loginctl enable-linger "$USER"`, or it stops when you log out.
- **It runs the launcher**, so a staged update applies when the node restarts.
- **It's always called fleet-node.** It never touches a `fleet.service` you run for the full Fleet. Running it again
  updates it.
- **A node needs its own data directory.** If the full Fleet also runs on that machine, add `--profile node`.
  Otherwise the node stops at once, because another Fleet holds the data.
- `--print` shows what it would write and run, and changes nothing.
- `fleet node uninstall-service` stops the node and removes the service. Its data stays.

### Keeping a headless machine up

`deploy/fleet-user.service` is a systemd user unit for a machine without a desktop session:

```sh
cp deploy/fleet-user.service ~/.config/systemd/user/fleet.service
systemctl --user daemon-reload
systemctl --user enable --now fleet.service
loginctl enable-linger "$USER"   # keep it running when you're logged out
```

## Adding a machine to a client

In Fleet: Settings → Machines → **Add a machine**. Paste the machine's URL and its token. Fleet checks both
against `GET /api/machine` before saving them.

The list lives on the machine that served the page (`/api/machines`, below), so every browser on it, and its paired
phones, see the same machines. A browser that kept its own list before copies it there once.

The sidebar lists every machine's sessions, grouped by machine. The machine you're working in is live: its
conversations stream, and diffs, files, terminals, Settings, Automations and Workflows all belong to it. Clicking
a session on another machine makes that machine live. The page reloads so nothing from one machine carries over
into another. Other machines' rows refresh every 15 seconds. A machine that stops answering keeps its last rows,
dimmed and marked unreachable. If it's the one you're working in, its header turns red, and a reload offers the way
back to this machine.

An automation can run on another machine in the list: pick it with the Machine chip under the automation's box. The
automation, its schedule and its runs stay on the machine that holds it. Each run starts its session (or workflow run)
on the other machine through that machine's own API, with the token kept for it: `POST /api/sessions`,
`POST /api/sessions/{id}/prompt` or `POST /api/workflows/runs`, the same bodies the web app sends. If that machine
doesn't answer, or turns the token away, the run is skipped and says why. It isn't retried or moved elsewhere.

## The client contract

This is what any client relies on. The web app is one client. A native app would speak the same contract.

### Identity: `GET /api/machine`

```json
{
  "id": "4f6c2d1e9b7a4c3d8e2f1a0b9c8d7e6f",
  "name": "hangar",
  "hostName": "hangar",
  "os": "linux",
  "version": "0.36.0",
  "apiVersion": 1,
  "authMode": "token",
  "remoteReachable": true,
  "requiresToken": true,
  "webApp": true,
  "publicUrl": "https://hangar.tail9c2e.ts.net",
  "capabilities": {
    "harnesses": [
      { "type": "opencode", "name": "OpenCode", "available": true, "enabled": true, "version": "1.18.32" },
      { "type": "claude-code", "name": "Claude Code", "available": false, "enabled": false, "version": null }
    ],
    "sessions": { "working": 2, "needsYou": 1 }
  }
}
```

- `id` never changes: not on restart, port change or rename. Key everything about a machine by it, not by its
  URL. The same machine can move address.
- `apiVersion` is the version of this contract. It goes up when a client talking to another machine would
  break. Adding a field doesn't bump it. A client should refuse a machine whose `apiVersion` it doesn't know,
  rather than half-work.
- `authMode` is `token` (local mode: present the access token) or `sign-in` (a hosted Fleet with an identity
  provider, which this contract doesn't cover yet).

- `webApp` says whether the machine serves the web app at its own address. A node (`fleet node`) doesn't, so a
  client doesn't send anyone to its pages; its sessions open from a Fleet that has it in its list. Fleets before it
  leave it out, and they serve the web app.
- `publicUrl` is the address phones should use for the machine, when someone saved one (null otherwise). Pairing
  puts it in the QR code.
- `capabilities` says what the machine can run and how busy it is. Fleets before it leave it out; the `machine` in a
  pairing answer has it `null`.
  - `harnesses`: each harness the machine knows, as it last checked them. `available` means installed and working
    there, `enabled` that its user hasn't turned it off (every harness is on until they do); a new session can use
    one only when both are true. `null` until the machine has checked once. Reading `/api/machine` never starts a
    check, so a harness installed since shows up after something on that machine asks for the harness list again.
  - `sessions`: how many of the caller's sessions there are `working` (in a turn) or `needsYou` (stopped on a
    question or a permission ask). Top-level sessions only, counted as the session list shows them.

`PUT /api/machine` with `{ "name": "…", "publicUrl": "…" }` changes either for every client (local mode only, owner
only: `403` for a device token or an agent). A
field left out stays as it is; an empty name goes back to the host name, an empty `publicUrl` clears it.

### Authentication

Present the token as `Authorization: Bearer <token>` on every HTTP request.

A browser `WebSocket` and `EventSource` can't set headers. On those, and only on those, pass the token as
`?access_token=<token>`: on `/hubs/*` and on WebSocket upgrades. Anywhere else a token in the query is ignored,
so it never has to sit in a URL that ends up in logs or history.

A wrong token is always a `401`. It never falls back to another way of signing in.

There are two kinds of token, and clients treat both as opaque strings:

- **The machine token**: one per machine, full rights (see [The access token](#the-access-token)).
- **Device tokens**: one per paired device (a phone), made by [pairing](#pairing-and-devices). A device token can do
  everything the machine token can (start sessions, steer, answer asks, `!` commands, `/btw`) **except** manage
  access: it can't read or replace the machine token, pair or remove devices, or change the machine list (`403`).
  Only a hash of its secret is stored; it stops working when the device is removed, or after 30 days without use
  (each use moves that forward).

A browser signed in with a device token (pairing, or `/auth/token-login`) gets a sign-in cookie that carries the
device's rights and stops working when the device does. Agents calling back into Fleet never have owner rights.

Clients that aren't browsers (native apps, CLIs) should use the header everywhere, including on WebSocket
upgrades.

### Cross-origin requests (browsers only)

A request that presents the token may come from any origin: Fleet answers the CORS preflight and the request
itself with `Access-Control-Allow-Origin` set to the caller's origin. Credentials are never allowed
cross-origin, so send `credentials: "omit"`. The token is the whole credential. A request without a token gets
no CORS headers from another origin.

The CORS rules are the same for device tokens: a request presenting any token may come from any origin, never with
credentials.

### Access: `GET /api/machine/access`, `POST /api/machine/access/token`

Local mode only, owner only (`403` for a device token). Returns the token, where it comes from (`saved`, `environment`, `ephemeral`), the bind
address, and `addresses`, the URLs another device might use (tailnet first, then LAN, then host name).
`POST …/token` replaces the token and returns the same shape. It returns `409` when `WEAVE_FLEET_AUTH_TOKEN`
fixes the token.

### Pairing and devices

Local mode only.

- `POST /api/machine/pairing` (owner) with `{ "baseUrl": "https://hangar.tail9c2e.ts.net" }` makes a one-time code:
  `{ secret, manualCode, expiresAt, url, payload }`. `url` is what the QR code shows:
  `<baseUrl>/pair#p=<base64url(payload)>`, and `payload` is version 1 of what any client reads from it:
  `{ "v": 1, "machineId", "machineName", "url", "secret" }`. `manualCode` is the same code to type (`XXXX-XXXX`,
  Crockford base32). A code works once, for 10 minutes; at most 5 are live; codes live in memory, so a restart
  forgets them. A node (`fleet node`) returns `409`: it doesn't serve the page the link opens.
- `POST /api/pairing/preview` (anyone) with `{ "secret" }` or `{ "manualCode" }`: `{ machineId, machineName, os,
  expiresAt }`, or `404` when the code is unknown, used or expired. Doesn't use the code up.
- `POST /api/pairing/redeem` (anyone) with the code and `{ "deviceName", "platform" }` (`ios`, `android`, `other`;
  a name of 1–60 characters): `{ deviceId, token, machine }`, where `machine` is the `GET /api/machine` shape. It also
  signs the browser in with a device cookie. `404` for a used or expired code; a bad name doesn't use the code up.
  `/api/pairing/*` takes 10 requests a minute in all, and typed codes 5 a minute.
- `GET /api/machine/devices` (owner): `{ devices: [{ id, name, platform, createdAt, lastUsedAt, pairedVia }] }`.
- `DELETE /api/machine/devices/{id}` (owner): removes the device. Its token stops working at once, its open hub
  connections and terminals are closed, its push subscriptions go, and the tokens its home got it on other machines
  are removed there.
- `POST /api/machine/devices/me/token` (a paired device, by cookie or token): a new token for the device itself, as
  `{ deviceId, token, machine }` like redeem; its old token stops working. For a phone that's signed in but lost its
  token: an iPhone's Home Screen app starts with a copy of Safari's cookies and none of its storage. `400` for an
  owner, `401` for a removed device.
- `POST /api/machine/devices` (the machine token only, not a cookie) with `{ name, platform, pairedVia }`:
  `{ deviceId, token }`. Another machine (a phone's home) calls this to get the phone a token here; see device grants.

Secrets travel only in request bodies, never in a query string, and Fleet never logs them.

### The machine list: `/api/machines`

Each machine keeps the list of other machines its clients know.

- `GET /api/machines`: `{ machines: [{ id, name, baseUrl, os, status, addedAt, lastSeenAt, token }] }`. `token` is the
  other machine's token for the owner, `null` for a device. `status` is what this machine last saw: `unknown`,
  `online`, `unreachable` or `unauthorized` (it turned the token away).
- `POST /api/machines` (owner) with `{ baseUrl, token }`: Fleet asks the machine who it is with that token (contract
  1, token auth, not this machine) and keeps it. These calls go out from Fleet: http(s) only, 5-second timeout, no
  redirects.
- `PUT /api/machines/{id}` (owner) with any of `{ baseUrl, token, name }`; a new address or token is checked first.
- `DELETE /api/machines/{id}` (owner). First removes the device tokens this machine got its phones there; if that
  machine doesn't answer, those tokens stay until they're removed on it (or go unused for 30 days).
- `POST /api/machines/import` (owner) with `{ machines: [{ id, name, baseUrl, token, os, addedAt }] }`: saves a
  browser's own list as it is (no call to each machine), idempotent by id; skips this machine and bad addresses.
- `POST /api/machines/{id}/device-grant` (a paired device): a device grant. Home asks machine `{id}` for a device
  token named "<phone> via <home>" with the machine token it keeps, records only that machine's id for the device, and
  returns `{ machineId, baseUrl, token }` to the phone. Asking again replaces the grant: the old token there is
  removed first, and if that machine can't be reached nothing changes (`502`).

Machine tokens are kept encrypted (Data Protection). Home keeps one SignalR connection per listed machine (its
machine token as the bearer, the `sessions` topic) to push those machines' notifications to its phones; it doesn't
forward one that names a machine other than the one it came from, and labels each with the id and name in its own
list.

### Push: `/api/push/*`

Local mode only. Web Push with VAPID; each machine's key pair is kept in `fleet.push.json` beside its database
(readable only by you). The VAPID subject is `Fleet:Push:Subject` (default `https://tryweave.io`).

- `GET /api/push/vapid`: `{ publicKey }`, for `pushManager.subscribe({ applicationServerKey })`.
- `PUT /api/push/subscriptions` with `{ endpoint, keys: { p256dh, auth }, kinds?, quietWhenDesk?, previousEndpoint? }`:
  saves where to push. `kinds` is any of `permission`, `question`, `finished`, `failed`, `workflow` (left out: what
  the subscription had, else all). `previousEndpoint` names a subscription the browser rotated, whose choices carry
  over and which is then removed. Returns `{ channel, kinds, quietWhenDesk, createdAt, lastSuccessAt }`.
- `POST /api/push/subscriptions/lookup` with `{ endpoint }`: the same shape, or `404`.
- `DELETE /api/push/subscriptions` with `{ endpoint }`.
- `POST /api/push/test` with `{ endpoint }`: pushes a test to that subscription; `{ outcome }` is `delivered`, `gone`
  (and removed), `retry_later` or `failed`.

The endpoint must be an `https://` address with a DNS name (no IP address, `localhost` or local name): Fleet posts to
it, without following redirects. The endpoint is a capability URL, so it only travels in bodies. A device manages only the subscriptions it made; the
owner manages any. A subscription the push service says is gone (`404`/`410`), or that fails 10 times in a row, is
removed.

A push is JSON, version 1, under 1 KB: `{ "v": 1, "machineId", "machineName", "sessionId", "kind", "reason",
"title", "body", "url", "tag", "requestId"? }`. `url` is `/phone/s/<machineId>/<sessionId>?ask=<requestId>` on the
phone's home machine; `tag` is `<machineId>:<sessionId>`.

The hub takes `SetPresenceAsync(visible, formFactor)` (`desktop` or `phone`) every 30 seconds from each window;
subscriptions with "quiet while I'm at the desk" get nothing while a desktop window on this machine is visible.

### Notifications on the `sessions` topic

`session_notification` is sent for every session that needs you, finished or failed while no tab was looking at it,
whatever the desktop notifications setting: the browser applies that setting, and a phone's home machine listens for
it on other machines. Its payload: `{ sessionId, reason, kind, requestId, machineId, machineName, title, body }`.
`reason` is `needs_you`, `finished` or `failed`; `kind` is finer (`permission`, `question`, `finished`, `failed`,
`workflow`); `requestId` is the permission ask's id. Fleets before this didn't send `kind`, `requestId` or the machine.

### Everything else

Once connected, a machine is the same Fleet API the web app always used: `/api/sessions`, the SignalR hub at
`/hubs/session-events`, and terminal sockets at `/api/sessions/{id}/terminals/{terminalId}/socket`. Session ids
are GUIDs and unique across machines. A client still has to remember which machine a session came from, because
nothing about the id says so.

### Known limits

- The browser canvas (app previews) of another machine doesn't load in the web app, because it relies on that
  machine's cookie. Open the machine's own URL to use it, when it serves the web app (`webApp`). A node doesn't, so
  its previews can't be opened from the web app yet.
- The machine token has no scopes. Paired devices get their own tokens with one limit (no managing access); there
  are no finer scopes.
