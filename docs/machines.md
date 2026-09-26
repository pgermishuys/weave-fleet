# Machines

Fleet can work with sessions on more than one computer. Every machine runs its own Fleet and keeps its own
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

The sidebar lists every machine's sessions, grouped by machine. The machine you're working in is live: its
conversations stream, and diffs, files, terminals, Settings, Automations and Workflows all belong to it. Clicking
a session on another machine makes that machine live. The page reloads so nothing from one machine carries over
into another. Other machines' rows refresh every 15 seconds.

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
  "requiresToken": true
}
```

- `id` never changes: not on restart, port change or rename. Key everything about a machine by it, not by its
  URL. The same machine can move address.
- `apiVersion` is the version of this contract. It goes up when a client talking to another machine would
  break. Adding a field doesn't bump it. A client should refuse a machine whose `apiVersion` it doesn't know,
  rather than half-work.
- `authMode` is `token` (local mode: present the access token) or `sign-in` (a hosted Fleet with an identity
  provider, which this contract doesn't cover yet).

`PUT /api/machine` with `{ "name": "…" }` renames the machine for every client. An empty name goes back to the
host name.

### Authentication

Present the token as `Authorization: Bearer <token>` on every HTTP request.

A browser `WebSocket` and `EventSource` can't set headers. On those, and only on those, pass the token as
`?access_token=<token>`: on `/hubs/*` and on WebSocket upgrades. Anywhere else a token in the query is ignored,
so it never has to sit in a URL that ends up in logs or history.

A wrong token is always a `401`. It never falls back to another way of signing in.

Clients that aren't browsers (native apps, CLIs) should use the header everywhere, including on WebSocket
upgrades.

### Cross-origin requests (browsers only)

A request that presents the token may come from any origin: Fleet answers the CORS preflight and the request
itself with `Access-Control-Allow-Origin` set to the caller's origin. Credentials are never allowed
cross-origin, so send `credentials: "omit"`. The token is the whole credential. A request without a token gets
no CORS headers from another origin.

### Access: `GET /api/machine/access`, `POST /api/machine/access/token`

Local mode only. Returns the token, where it comes from (`saved`, `environment`, `ephemeral`), the bind
address, and `addresses`, the URLs another device might use (tailnet first, then LAN, then host name).
`POST …/token` replaces the token and returns the same shape. It returns `409` when `WEAVE_FLEET_AUTH_TOKEN`
fixes the token.

### Everything else

Once connected, a machine is the same Fleet API the web app always used: `/api/sessions`, the SignalR hub at
`/hubs/session-events`, and terminal sockets at `/api/sessions/{id}/terminals/{terminalId}/socket`. Session ids
are GUIDs and unique across machines. A client still has to remember which machine a session came from, because
nothing about the id says so.

### Known limits

- The browser canvas (app previews) of another machine doesn't load in the web app, because it relies on that
  machine's cookie. Open the machine's own URL to use it.
- There are no scopes and no per-device tokens yet. One token per machine, shared by every client that has it.
