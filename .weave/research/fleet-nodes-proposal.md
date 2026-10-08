# Fleet nodes: one interface, many machines doing the work

*2026-10-07: proposal. Nothing is built. Mockup: `mockups/fleet-nodes/index.html`.*

Run a headless Fleet **node** on each machine with the CLI, and work with all of them from **one interface**.
Choose which node a session, automation or workflow run goes to, or let Fleet or an agent choose. Land it in small
pieces that ship in normal releases, so Fleet keeps working for customers and for us the whole way through.

## Two kinds of customer, nobody left out

**Simple.** Install the desktop app (or run `fleet`), and it just works on this computer. Most customers are here.
They should never have to learn what a node is, and nothing in this proposal changes what they see unless they go
looking for it.

**Advanced.** Run `fleet node` on one or more machines (a server, a cloud VM, a Mac mini under the desk) and point a
UI at them. The UI can be:

- **The desktop app**, adding the node under Settings → Machines. The desktop app still starts its own Fleet
  (`desktop/src/server.ts`) and loads it in the window; that local Fleet is the interface and keeps the node list.
  Optionally it runs no sessions of its own ("only use other machines").
- **A browser** pointed at any full Fleet (`fleet`) that has the node in its list, such as an always-on box. That
  works today for one machine (`docs/machines.md`).
- **The phone**, through its paired home machine, as today.

Both kinds of customer run the same Fleet. The advanced case is the simple case plus nodes, not a different product.
No feature becomes multi-machine-only, and no single-machine feature is lost or moved behind a node.

Rules that keep everyone in:

- **The simple path is the default and stays unchanged.** The desktop app's first run, `fleet` with no
  arguments, and the browser at `localhost` behave as today. New choices appear only where advanced customers
  look for them: Settings → Machines, `fleet node --help`, the docs.
- **No node vocabulary in the simple path.** Words like "node" and "control plane" show up only once you add a
  second machine.
- **Every operating system gets each piece before we announce it.** `fleet node install-service` needs systemd,
  launchd and a Windows service, not just Linux. The desktop app's "add a machine" works the same on macOS, Windows
  and Linux.
- **Every way of reaching Fleet keeps working:** desktop app, browser on the same machine, browser from another
  device, phone. Each stage says what it changes for each of them.
- **Older and newer Fleets work together** (see *Landing it without breaking anyone*), so customers update when
  they choose to, one machine at a time.
- **Hosted (sign-in) Fleets are untouched.** This is about local-mode Fleets with tokens. `authMode: sign-in` keeps
  its own path.

## Where we are

The multi-machine work (#318, #319, #320, #331, #397) built most of the parts:

- Every machine runs its own Fleet: API, sessions, harnesses, database, and the web UI.
- Settings → Machines adds another machine by URL and token, checked against `GET /api/machine`. The list lives
  on the machine serving the page (`/api/machines`), with tokens encrypted.
- The sidebar shows every machine's sessions in the same tree. The composer's Machine chip starts a session on
  another machine.
- The machine serving the page already holds a live SignalR connection to every machine in its list
  (`Infrastructure/Machines/RemoteMachineWatcher.cs`, `RemoteMachineConnection.cs`). Today it only forwards
  notifications to phones.

What's missing:

- **No node without a UI.** `Program.cs` always serves `wwwroot` (`UseDefaultFiles`, `UseStaticFiles`,
  `MapFallbackToFile`). No flag turns it off.
- **Live on one machine at a time.** Switching reloads the page (`client/src/lib/machines.ts`, `switchToMachine`).
  The client has one shared API client (`api/client.ts`, imported in about 55 files, about 141 calls), one hub
  connection (`use-signalr-socket.ts`) with about 12 maps keyed by topic alone, and about 36 files that call
  `apiFetch`/`apiUrl`/`wsUrl` on the active machine. Other machines are polled every 15 s.
- **Nothing can name a machine except the composer.** `Automation` has `WorkspaceId`, `HarnessType`,
  `TargetType`, `WorkflowId`, but no machine. Workflows have none. `fleet_message` and `fleet_session_read` only
  reach sessions on their own Fleet, through `$FLEET_URL` over loopback.
- **No real CLI.** `src/WeaveFleet.Cli` is a placeholder. `scripts/launcher.sh` already has subcommands
  (`update`, `uninstall`, `import-legacy-sessions`) and passes `--host`, `--port`, `--require-token` to the server.

## What a node is

A node is the whole Fleet API, started without the web UI and with a token always required. Each session, its
worktree, git, terminals, previews and provider keys stay on the node that runs it.

A thinner node, where one central Fleet keeps every session and nodes only run harness processes, doesn't fit:

- Harnesses call back into Fleet over loopback (Fleet tools, memory, asks; `AgentRequests.cs`, `ILocalFleetUrl`).
- Worktrees, git, diffs, file views, terminals (`TerminalManager`) and previews all read that machine's disk.
- `IHarnessSession` has about 30 methods and an event stream. Every one, and everything around it, would need a
  remote twin.

## Two ways to connect

**A. The interface connects to each node.** This is today's model, extended. Nodes must be reachable from every
device, phone included, which in practice means a tailnet. You add each node by URL and token. No middleman, so
terminals stay direct, and one node going down only affects its own sessions.

**B. Each node connects out to a control plane.** `fleet node join <url> --code XXXX-XXXX`, reusing the phone
pairing code format. The node listens nowhere. The control plane relays API calls, hub events, terminals and
previews to it. Nodes work behind NAT and in the cloud without Tailscale, and phones only need one address. The
costs: a tunnel to build and secure, everything flows through the control plane, and if it goes down you lose
access (the nodes keep running).

**Recommendation: A now, B later if needed.** A builds on what's merged and gives the main win first.

**The control plane is a role, not option B.** In A, the Fleet you use as the interface already holds the node
list and every node's token. Give it a "send this work to a node" API and it *is* the control plane. B only
changes how it reaches the nodes. Everything below about agents and other tools talking to the control plane
works in A.

## Decisions

| Question | Recommended | Other answers |
|---|---|---|
| How do nodes connect? | A now, B later | A only; B from the start |
| Who decides where work runs? | You pick; agents can hand off | Fleet can pick ("Any node") |
| Does the interface machine run sessions too? | Yes, any Fleet can be the interface (the simple case) | Also offer "only use other machines" for advanced customers |

**No answer rules out another.** Each one is added on top of what's there:

- "Fleet picks" is a step in front of "you pick": it turns "any node" into a specific node, then follows the same
  path. Agent hand-off takes a node name now and can take "any" later through the same picker.
- B relays the same API A calls directly. The client already keys machines by id, not by address, so a node can be
  "direct" or "relayed" without the rest of the client caring. Both can be used at once.
- A UI-only Fleet is a Fleet with no harnesses. It can be added at any time.

Three things to settle early so it stays that way:

1. **Store the target as "this node", a named node, or "any node"** from the first migration, not only a node id.
2. **The Fleet that holds an automation's schedule must know the other nodes.** If a node runs its own automations
   (so a closed laptop doesn't stop them), "any node" means that node needs the node list and tokens too. Every
   Fleet can already keep a machine list, so this is a choice, not extra work.
3. **One "what can this node run" answer per node** (harnesses, folders, sessions working), used by Settings,
   the composer and the picker alike.

## Landing it without breaking anyone

Each stage below is its own PR (or a few), ships in a normal release, and changes nothing for anyone who doesn't
opt in. The rules:

- **Additive only.** New CLI commands and flags; plain `fleet` behaves as today. New fields on existing answers:
  `docs/machines.md` already says adding a field doesn't bump `apiVersion`. New database columns are empty by
  default, and empty means "this machine", which is today's behaviour.
- **Visible changes go behind a switch.** The same pattern as `SessionMessages` and `Workflows` in
  `FleetOptions` (a server default, and a user preference that wins). We turn it on in our own Fleet and use it
  daily. It becomes the default only once we're happy with it.
- **Refactors first, with no change in behaviour.** With one machine, the new code path must do exactly what
  today's does. Tests prove that before anything new is switched on.
- **Mixed versions keep working.** A newer interface talking to an older node hides what that node can't answer
  and says "update this node". A newer node talking to an older interface adds fields the interface ignores. The
  `apiVersion` check still refuses a contract the client doesn't know.
- **Names stay put until the end.** Keep "Machines" in the UI until the work is done, so nothing customers see
  moves around mid-way.

## Stages

Sizes are relative: **S** is days, **M** about a week, **L** a couple of weeks.

### 1. Headless node (S)

- `fleet node` in `scripts/launcher.sh` and `launcher.cmd` starts the same server with a new
  `Fleet:ServeUi=false` (or `--no-ui`): no static files, no SPA fallback, `/` answers with a short JSON
  "this is a Fleet node" note.
- Token always required (as `--require-token`), whatever the bind address.
- Prints what you need to add it: address, how to show the token, the harnesses it found.
- `fleet node install-service`: the systemd unit we ship (`deploy/fleet-user.service`), a launchd agent, and a
  Windows service. The command can land with Linux first, but `fleet node` isn't announced until all three work.
- **Customers see:** nothing. `fleet` without `node`, and the desktop app, are untouched.
- **Check:** integration test that a no-UI server serves `/api/*` and the hub but not `index.html`; that it
  refuses a request without a token on loopback.

### 2. Each node says what it can run (S)

- `GET /api/machine` gains `capabilities`: harnesses with availability (from `HarnessAvailabilityCache`), known
  folders/workspaces, sessions working, sessions waiting. New fields only.
- Settings → Machines shows them per row.
- **Customers see:** a bit more detail in Settings → Machines. Nothing else.

### 3. One API client per machine, no behaviour change (M)

- Replace the module-level `activeMachine` assumption in `api/client.ts` and `lib/api-client.ts` with a machine
  scope: callers get the client for "the session's machine", defaulting to the active one. `apiOnMachine` already
  exists.
- Move the 55 files over in batches, each its own PR if that's easier to review.
- **Customers see:** nothing. Only one machine is ever live in this stage.
- **Check:** the existing client tests, plus a test that every call from a session view goes to that session's
  machine.

### 4. Every machine live in the sidebar (M, behind a switch)

- One `sessions`-topic connection per listed machine, built on the phone inbox's per-machine feed
  (`client/src/lib/phone/machine-feed.ts`). Replaces the 15 s poll when the switch is on.
- Working dots and Needs you update live for every machine. Opening another machine's session still reloads.
- **Customers see:** nothing unless they turn on the switch.

### 5. Open any machine's session without a reload (L, same switch)

- `use-signalr-socket.ts`: one connection per machine; the topic maps keyed by machine and topic.
- `use-session-stream.ts`, kept streams, stores with module-level state: keyed by machine.
- Conversation, Changes, Files and terminals of a session come from its own machine. Settings, Automations and
  Workflows pages show the interface machine's own, plus a machine selector.
- **Same-machine assumptions to fix here:**
  - Open in editor / Open folder (`OpenDirectoryEndpoints.cs`) calls `Process.Start` on the node. For a remote
    node that opens a window on the remote machine's desktop. Hide it for remote sessions, or offer to copy the path.
  - The browser canvas of another machine relies on that machine's cookie (`docs/machines.md`, known limits).
    Show "open on the machine" until previews are reachable with a token.
- **Customers see:** nothing unless they turn on the switch. With it on: no reload, every machine streams.
- **Check:** E2E with two TestHarness servers: open a session on each, both stream, no navigation event.

### 6. Name a node on automations and workflow runs (M)

- Migration: nullable target column on automations; null = this machine (today's behaviour). Values: a machine id,
  or `any` (only used once stage 8 lands).
- The automation runs from the Fleet that holds it and starts the session through that machine's API, the same
  way the composer's Machine chip does today (#331).
- If the target isn't answering: skip and tell you (default), or run elsewhere (needs stage 8). Runs record the
  machine they ran on.
- Workflow runs: one node per run. Steps don't hop between machines, because the code would have to travel through
  git between steps.
- **Customers see:** a new optional field. Existing automations are unchanged.

### 7. Agents hand work to another node (M, behind a switch)

- `fleet_message` and `fleet_session_read` accept a session on another machine. A new `fleet_session_start`
  takes `{ machine, folder, prompt, branch? }`.
- The agent's own Fleet makes the call, with its stored token for that machine, so agents never see other
  machines' tokens.
- Work moves by branch: the agent pushes, the new session checks it out.
- The tool has to change in **both** OpenCode plugins (`opencode/fleet`, `opencode2/fleet`), plus
  `Application/FleetTools/fleet-tools.json`, which harnesses that take Fleet's tools over MCP (Claude Code) read.
- **Limits:** off by default. Only machines you allowed are visible to agents. Every hand-off is a normal session
  in the sidebar that you can open and step into. Agents never get owner rights (as today).
- **Customers see:** nothing unless they turn on the switch.

### 8. Fleet picks the node (M)

- "Any node" in the composer, automations and agent hand-off. Rules, in order: answering, has the folder, has the
  harness, fewest sessions working. It always says why it picked a node.
- A node without the folder offers to clone it there (the new-folder flow, #323).

### Desktop app: "only use other machines" (S, any time after stage 5)

- A setting in the desktop app (and a `Fleet:RunSessions=false` option for `fleet`) that keeps the local Fleet as
  the interface but runs no sessions on this computer. The composer and automations default to a node instead.
- For advanced customers whose desktop is only the window onto their nodes. Off by default; the simple case never
  sees it.
- **Check:** the desktop app on macOS, Windows and Linux, added to a node, with the setting on and off.

### 9. Later, if needed: nodes connect out (L)

- `fleet node join <url> --code`: one-time code, outbound connection, the control plane relays API calls, hub
  events, terminal sockets and previews.
- The client treats a relayed node like a direct one, through the machine scope from stage 3.

### Also later: other tools talking to the control plane

Once stage 7 exists, the "start a session on a node" call is an API like any other:

- **CLI:** `fleet run --node atlas --folder harbor-api "…"` from a terminal, a script or CI.
- **Harnesses Fleet doesn't run** (a Claude Code in your own terminal): the same call through an MCP server or the
  CLI.
- They use an owner or device token, not an agent token. Agents still fetch what they need through tools; nothing
  is added to their context automatically.

## Risks and open questions

- **Stage 5 is the largest and riskiest piece.** Module-level state across about 36 client files. Stage 3 takes
  the mechanical part out of it first. Stage 4 lands value before it.
- **A turn cut off by a machine going down reads "Working" until the machine returns** (seen in the multi-machine
  live checks). With every machine live, more people will notice. Show "machine not answering" on its sessions.
- **Where the always-on schedule lives** (decision 2 above). Settle it in stage 6.
- **Previews on remote nodes** need token-based access, not the cookie. Small in A, part of the relay in B.
- **Version skew.** Each stage that adds a field also adds the "update this node" message for older nodes.
- **Security of agent hand-off.** An agent on one node starting work on another is new reach. Keep it behind the
  switch, limited to machines you allowed, and visible as sessions.

## Not doing

- A separate app or edition for advanced customers. One Fleet, one desktop app; nodes are something you add.
- Changing the desktop app's first run or `fleet`'s defaults.
- A thin node that only runs harness processes.
- Workflow steps on different machines in one run.
- Load balancing beyond "fewest working".
- Renaming Machines to Nodes before the work is done.
