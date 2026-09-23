# Weave config in Fleet

Approved mockup: https://claude.ai/artifact/WARJ9gtxZfiFNqbRJtzpy7 (2026-09-23). Feasibility spike:
`~/.cache/weave-config-spike/README.md` (OpenCode 1.18.31 + local Weave build: every step works).

Fleet keeps a Weave config per user in its database and hands it to Weave when it starts OpenCode, through an
environment variable pointing at a folder only Fleet writes. The user's own files are never edited. When Weave isn't
installed, nothing changes.

| Weave | Variable | File in Fleet's folder |
|---|---|---|
| Weave (`@weaveio/weave-adapter-opencode` ≥ 0.2.0-next.1) | `WEAVE_GLOBAL_CONFIG_DIR` | `config.weave` + `prompts/*.md` |
| Weave Legacy (`@opencode_weave/weave` ≥ 0.9.0) | `WEAVE_OPENCODE_CONFIG_DIR` | `weave-opencode.jsonc` |

Scope of this PR: pooled OpenCode. OpenCode 2 and Claude Code show as "not yet" rows.

## Decisions

- **Source**: `own` (default: Fleet sets nothing) or `fleet`. Switching changes the process environment, so it
  reaches new processes; running sessions keep theirs (same rule as built-in skills and profiles).
- **Folder**: `{data}/weave/{user-hash}/`, a stable path. A save doesn't change the pool key, so the running process
  stays and each folder it has loaded is reloaded with `POST /instance/dispose?directory=` once it has no busy
  session (`GET /session/status`). Busy folders wait, and Fleet re-checks every few seconds.
- **Variables are set per file**: `WEAVE_GLOBAL_CONFIG_DIR` only when `config.weave` exists, and
  `WEAVE_OPENCODE_CONFIG_DIR` only when `weave-opencode.jsonc` exists.
- **Detection is behavioural**: one probe OpenCode, started with both variables pointing at a probe folder that
  defines a probe agent for each Weave. `/config` gives the plugin list (which Weave is installed), and the probe
  agents show whether each supports the variable. No version parsing, so local builds work. Result cached per
  user until "Check again".
- **Test/Save**: start OpenCode with the draft folder (like `CheckProfileAsync`) and list agents for a Fleet-owned
  check folder. Weave agents are the ones with `[weave-managed]`, or for Legacy the non-native ones. If there are
  none, the error is read from `<check folder>/.weave/weave.log` (Weave), or comes from Fleet's own JSONC parse
  (Legacy). Save refuses a draft that fails the check.
- **Allowed file names**: `config.weave`, `weave-opencode.jsonc`, `prompts/<name>.md` (no separators, no `..`).
- The dead `~/.weave/weave-opencode.jsonc` handling goes: `ConfigService`, the Weave part of `GET/PUT /api/config`,
  and `ConfigOverviewSection`.

## Stages

1. [x] Storage: migration 037 `weave_configs` + `weave_config_files`, repository, `WeaveConfigService`, folder mirror (atomic, whitelist)
2. [x] Runtime: variables in `PrepareRuntimeAsync`; each pooled instance records the folders it was leased for
3. [x] Detection + check (one trial process per user, probe agents, weave.log parse)
4. [x] Apply: reload idle folders, retry busy ones every 3 s for up to 30 min, status for the UI
5. [x] API `/api/weave` (GET state `?redetect=true`, PUT save, POST check, GET own)
6. [x] Client: Settings → Weave
7. [x] Removed `ConfigService`, `/api/config/paths`, the Weave part of `/api/config`, `ConfigOverviewSection`, `use-config`
8. [x] Tests (.NET + vitest), live check on a scratch Fleet, screenshots in `mockups/weave-config/`

## Verified live (2026-09-23, scratch Fleet, OpenCode 1.18.31)

- Weave 0.2.0-next.1 (npm) and a local build (`file://`): detected, probe agent seen, Test ok/broken (with line:col),
  save → new process with `WEAVE_GLOBAL_CONFIG_DIR`; second save reloads idle folders in the same process; a folder
  with a running turn waits and reloads when the turn ends, and the turn completes; the session works after.
- Weave Legacy 0.9.0: detected, JSONC caught with line/column before OpenCode, Test lists Legacy agents, save works.
- Weave Legacy 0.8.2: detected as not reading Fleet's folder; "Keep it in Fleet" disabled with an update note.
- No Weave: section says so; sessions run as before and their process gets no `WEAVE_` variables.

## Not in this PR

- OpenCode 2 and Claude Code ("Not yet" rows). OpenCode 2's adapter reads `WEAVE_GLOBAL_CONFIG_DIR` too (0.2.0-next.1);
  its reload needs its own check. Claude Code needs `--plugin-dir`.
- Switching own ↔ fleet reaches new processes only; running ones keep their config until they restart.
