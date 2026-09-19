# OpenCode 2 as a second harness — research (2026-09-18)

Goal: offer OpenCode 2 **alongside** the existing OpenCode (v1) harness. v1 stays as it is.

Sources: https://opencode.ai/v2/docs (2.0.6), https://opencode.ai/v2/openapi.json, and a live spike with the
real 2.0.6 binary against a scripted model (kit: `~/.cache/opencode2-spike`, `oc.sh` runs any opencode binary
with a scratch HOME; `step1..6-*.sh` are the checks below; `fakellm.py` is a keyword-scripted OpenAI-compatible model).

## What V2 changes

Per the V2 migration guide, V2 has three breaking changes: the **server API**, the **plugin API**, and the
terminal client config (`tui.json` → `cli.json`, which doesn't matter to Fleet). Config files, agents,
commands, skills and AGENTS.md are meant to stay compatible, and a V1-format `opencode.json` worked unchanged
in the spike.

- One shared background server per user (`opencode serve --service`), multi-directory ("locations").
  `opencode serve --port N` runs a private one. Every request can carry a location (directory).
- Auth: `OPENCODE_SERVER_PASSWORD` → HTTP Basic `opencode:<password>`; otherwise the server generates a password.
- API under `/api/...`: session create/prompt/interrupt/fork/revert/compact/generate/messages, forms, permissions,
  agents, models, providers, commands, skills, pty, shell, vcs, fs, worktree, plugin RPC `/api/rpc/{id}/{method}`.
- Events: `/api/event` SSE, live-only (no replay), one stream for all locations. Durable events carry
  `durable.aggregateID` + `seq` (per session), which makes resync after a reconnect straightforward.

## Spike results (all verified live)

| Check | Result |
|---|---|
| Text turn | `session.execution.started` → `session.step.started` → `session.text.started/delta/ended` → `session.step.ended` (tokens/cost) → `session.usage.updated` → `session.execution.succeeded` |
| Tool turn | `session.tool.input.started/ended` → `session.tool.called` → `session.tool.progress` → `session.tool.success` (content blocks). Shell also emits `shell.created/exited` |
| Messages | `GET /api/session/{id}/message`, newest first; types `user`, `assistant` (content: text/reasoning/tool), `idle` (outcome succeeded/interrupted/…), plus compaction/shell/skill/synthetic/system |
| Permission ask | `permission.asked` event; `GET /api/session/{id}/permission`; reply `{"decision":"once"\|"always"\|"reject"}`. Session create accepts a `permissions` ruleset, so auto-approve can be set per session |
| Question tool | now a **form**: `form.created`, fields `{key, title, description, options[], custom}`; reply `{"answer":{"q0":"B"}}` → `form.replied` |
| Subagent | tool renamed `task`→`subagent`; child session with `parentID`, listable via `?parentID=` |
| Off the record | `POST /api/session/{id}/generate` returns text and adds nothing to history (maps to `AskOffTheRecordAsync`) |
| Interrupt | `POST /interrupt` → `session.execution.interrupted`, running tool → error, idle outcome `interrupted` |
| `OPENCODE_CONFIG_CONTENT` | honoured (plugins + V1-syntax `permission.bash: ask` applied) → profiles and Fleet injection still work |
| Fleet plugin (V2 port) | works. Plugin path must be a **directory** (`index.js`). Tool must set `options: { codemode: false }` or it only exists inside Code Mode's `execute` tool. Executor's 2nd arg has `sessionID, agent, messageID, id, progress`; `process.env.FLEET_URL` visible |
| Todos | **no todo tool and no todo schema in V2** |
| Two servers, one DB | a prompt sent to server A ran on A (3/3); B could read A's sessions |

## Running V1 and V2 on the same machine

1. **Same binary name.** Both install as `opencode`; the V2 curl installer writes `~/.opencode/bin/opencode`
   (V1's path) and `@opencode/cli` registers `opencode`. `opencode2` also exists, but only as an alias for V2:
   the curl script writes it as a `install_legacy_shim` (`exec "$(dirname "$0")/opencode"`), and npm maps both
   `opencode` and `opencode2` to the same V2 binary; the tarball has only `opencode`. So `opencode2` on PATH tells
   Fleet that V2 is installed, but after a V2 install `opencode` is V2 too and V1 is gone.
   Fleet should install V2 into its own folder (release tarballs at `opencode.ai/files/bin/<ver>/opencode-<os>-<arch>.tar.gz`)
   so a user's V1 at the default path is left alone.
2. **The V1 harness would accept a V2 binary.** `MinimumVersion` is only a lower bound (1.15.10), so a
   2.x `opencode` on PATH is reported as available and every V1 API call then fails. It needs an upper bound (< 2).
3. **Same database file.** V2's default DB is `~/.local/share/opencode/opencode.db`, the same file V1 uses. On first start V2
   **migrates it in place** (adds `session_v2`, `session_inbox`, … and drops `session_input`, `session_context_epoch`,
   `data_migration`; runs `clear_v1_session_permission`). In the spike, V1 1.18.30 still listed and ran turns on
   old and new sessions afterwards, but that is one version on one path. `OPENCODE_DB` can point V2 elsewhere
   (trade-off: the user's own V2 client then doesn't see Fleet's sessions). → decision for the user.
4. Config, skills, AGENTS.md, `.opencode/` are shared and read by both. V2 reads the V1 format, so Fleet's skill
   install paths can serve both harnesses. **But the reverse fails:** the migration guide encourages converting config
   to the native V2 shape and warns "do not point V1 at files after converting them to native V2-only shapes". A user
   who converts `~/.config/opencode/opencode.json` or a project's `opencode.json` breaks Fleet's V1 harness.
   `OPENCODE_CONFIG_CONTENT` only layers on top; it can't stop V1 reading the global/project files. Needs at least a
   clear message in setup.
5. The migration guide says V1 and V2 "are no longer installed side by side by default" and "the V2 curl installer
   replaces the V1 binary". Coexistence is something Fleet has to arrange, not a supported OpenCode setup.
6. Renamed permission actions/tools: `bash`→`shell`, `task`→`subagent`, `write`/`patch`→`edit`. The mapper and the
   client's tool-card rendering key off tool names. V2 reads only AGENTS.md (no CLAUDE.md fallback).
   V2 ignores with a warning: `logLevel`, `server`, top-level `subagent_depth`, compaction `tail_turns`/`prune`, and
   the V1 experimental fields `batch_tool`, `openTelemetry`, `primary_tools`, `continue_loop_on_deny`.

## Install modes (user direction 2026-09-18)

"Keep them separate if at all possible on a machine that has V1; a fresh Fleet install that picks OpenCode 2
installs it where it would by default."

- **Default mode**: no V1 in use. Run OpenCode's own V2 installer (`curl -fsSL https://opencode.ai/v2/install | bash`,
  i.e. `~/.opencode/bin/opencode` + `opencode2` alias; Windows has no package manager support, so download the zip).
  Default config (`~/.config/opencode`) and DB. Fleet also uses a V2 the user installed themselves this way.
- **Separate mode**: a V1 `opencode` (reports 1.x) is installed. Fleet downloads the V2 release into its own folder,
  e.g. `~/.weave/harnesses/opencode2/{bin,config,data}`, and starts it with `OPENCODE_CONFIG_DIR=<config>` and
  `OPENCODE_DB=<data>/opencode.db`. Verified in step 7 (scratch copy of this machine's layout):
  - V1's DB byte-identical afterwards, V1 sessions invisible to V2, V2 turn ran.
  - `OPENCODE_CONFIG_DIR` **replaces** the global config folder: V2 loaded the agent from its own folder and not the
    one in `~/.config/opencode` (control run without it loaded the V1 one).
  - Still shared: project config (`<repo>/opencode.json`, `.opencode/`; `OPENCODE_DISABLE_PROJECT_CONFIG` would drop
    repo agents/skills too), and V2 wrote its log to `~/.local/share/opencode/log/opencode.log` and shell output to
    `~/.local/share/opencode/shell/`. Harmless, but not perfectly separate. Caches untouched in the test.
  - Credentials live in the DB, so a separate DB means separate provider logins (V2 has an
    `import_legacy_credentials` migration; not checked whether it copies V1's auth.json into a new DB).
- Gotcha: `GET /api/agent` returns `[]` for a location until something has used it (lazy load). A catalog
  request must warm the location first.
- Either way the V1 harness needs an upper version bound (< 2) so it never drives a V2 binary.

## What Fleet needs (the V1 adapter is ~9.5k lines incl. pooling; little is reusable beyond patterns)

- **New harness type `opencode2`** (descriptor, runtime, session) in `Harnesses/OpenCode2/`.
- **Runtime:** one Fleet-owned private server per (owner, profile), serving every directory through locations. No
  per-directory pool, so none of `PooledOpenCodeInstanceRegistry` / `PoolDemuxBindingTable`. Still demux the single
  SSE stream by `sessionID` to each `IHarnessSession`. Fleet sets the password.
- **HTTP client + models + mapper** for the event list above and the message snapshot (reopen). This is the bulk of the work.
- **Questions → forms**, **permissions** auto-approved through the session ruleset (as v1 does with rules + auto-approve).
- **Plugin port** of `opencode/fleet/fleet-canvas.ts` to `Plugin.define`/`ctx.tool.transform` (canvas, app, browser,
  screenshot tools; tool results carry files, so screenshots can come back as images), skill folders via
  `skills` in config or `ctx.skill.transform`. A **`fleet_todo` tool** if Progress should keep working (otherwise
  `ReportsTodos = false`).
- **Hard-coded `"opencode"` in shared code** to generalise: `OpenCodeSessionMessageProxy` (reopen snapshot),
  `OpenCodeWarmupHostedService`, `HarnessEndpoints` warmup + default-enabled check, skill target lists
  (`BundledSkillsHostedService`, `SkillManifestMigrator`), `HarnessInstallPaths`.
- **Setup/update:** Fleet-managed download + version pinning; `LatestVersionPackage` = `@opencode/cli`.
- **Tests:** conformance fixture + live tests against the real binary in CI (like PR #242 does for v1).

## Estimate (rough)

| Stage | Work | Size |
|---|---|---|
| 0 | Coexistence guards: v1 upper bound, private V2 install, DB decision | 2–3 days |
| 1 | Adapter: runtime/server, client, SSE demux, mapper, snapshot, forms, permissions, abort, generate, catalog, resume | 1.5–2 weeks |
| 2 | Plugin port (canvas/app/browser/screenshot), skills, `fleet_todo` | 3–4 days |
| 3 | Profiles, delegation/child sessions, setup/update, conformance + live CI tests | ~1 week |

≈ 3.5–4.5 weeks for parity with the v1 harness; a text + tools MVP (Stage 0 + most of 1) ≈ 1.5–2 weeks.

## Open questions

- Shared DB (default) or Fleet-private `OPENCODE_DB` for the V2 harness?
- Should Progress keep todos on V2 (`fleet_todo` tool) or should V2 sessions go without?
- V2 is days old (2.0.6) and its API spec calls itself "Experimental HttpApi"; pin a version and expect churn.
