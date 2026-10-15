# What's switched on

Read these before you offer a feature. Never write to them: tell the user where to turn a feature on and stop.

| Feature | Check | Off means | Turn on in |
|---|---|---|---|
| Memory | `GET /api/memory` → `enabled` | notes aren't read or saved | Settings → Memory |
| Workflows | `GET /api/workflows` → 404 "turned off" | no workflow can run | Settings → Workflows |
| Mods | `GET /api/features/mods` → `on` | no mods | Settings → Features (Settings → Mods appears once it's on) |
| Agent browser | `GET /api/agent-browser/settings` → `enabled` | no browser tools | Settings → Browser |
| Messages between sessions | the `fleet_message` tool exists | the tool isn't there | Settings → Features |

Everything else Fleet stores as a preference: `GET /api/preferences` returns one object of key → value strings (for
example `Memory`, `Workflows`, `SessionMessages`). A key that isn't there is at its default. `GET /api/config` is
Fleet's own configuration, `GET /api/harnesses` the agents it can start and which are installed, and
`GET /api/skills/built-in` the built-in skills and which are on (Settings → Skills).

## "What can you do here?"

Answer from what you read, in a short list: say what is on and what is off with where to turn it on, and mention only
what this Fleet has. Start from what's always there: sessions in worktrees (`sessions.md`) and automations
(`automations.md`). Then add Workflows, Memory, Mods or the browser only where the check above says they are on, and
list the rest as off.
