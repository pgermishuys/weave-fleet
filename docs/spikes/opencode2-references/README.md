# Spike: OpenCode 2 references (2026-10-01)

Branch `spike/opencode2-references`. Nothing here is built into Fleet. Mockup page: see the link in the final report.

## Verdict

**Viable, with conditions.** References are a small, cheap feature: a list of named folders the agent is told about
(~22 tokens plus ~45–60 per reference, in the cached system prompt). OpenCode 2 *and* OpenCode 1 (1.18.33) have it
natively, and Claude Code has `--add-dir`. Every harness Fleet runs can get it. The conditions:

1. **Fleet keeps the list and passes it per session.** OpenCode's own `references` config is per server (Fleet pools
   one server across repositories) or lives in the repo's `opencode.json`. Fleet should not write either.
2. **Fleet sets the permissions.** A reference grants nothing on OpenCode 2: reads still ask `external_directory`, and
   under Fleet's default (allow everything) the agent can write into a reference. Fleet adds per-session rules:
   allow reading the folder, deny edit/write there. The shell can still write anywhere (proven).
3. **Fleet clones Git references itself.** OpenCode 2/1 clone with plain `git` into their own data folders, each its
   own copy. A failed clone (private repo, no credentials) is only a log warning, and the missing path is still
   listed to the model.

## What OpenCode 2 does (2.0.18, proven live; source `origin/v2` afc5d1e21f)

| Question | Answer | Evidence |
|---|---|---|
| In 2.0.18? | Yes. Same strings in the binary; `GET /api/reference?directory=` lists them. | `ev-reference-list.json` |
| What the model sees | One block in the **system prompt**, after the skills list: "Project references provide additional directories that can be accessed when relevant." then `<available_references>` with name, path, description. | `ev-references-block.txt`, `ev-system-prompt.txt` |
| Token cost | 3 references = 191 tokens (o200k) / 211 (Claude legacy tokenizer). Header 22; one realistic entry ~58. | `tok/count.mjs` |
| Description-less entries | Left out of the prompt entirely (still in the API list). | `nodesc` absent |
| `hidden: true` | **Still in the prompt.** It only hides the reference from OpenCode's own `@` composer list (`app/src/composer/model.ts`). | `design` present |
| Read / grep / glob | Work on absolute paths. | `ev-allowall-tools.txt` |
| Permissions | References grant nothing. With V2 defaults, reading a reference asks `external_directory` (same as any outside folder). | `ev-default-perms-ask.json` |
| Writes | Under allow-all (Fleet's default level) the agent **wrote** into a reference. Session rules `edit`/`write` deny on `<path>/*` stop write and edit ("Permission denied: edit"); `shell` still writes. | `ev-allowall-readonly.txt`, `ev-ask-level-tools.txt` |
| Fleet Ask/Edits level | Rules `* ask` + read allow + `external_directory allow <ref>/*` → reference reads run without asking; an unlisted outside folder still asks. | `ev-ask-level-perms.json` |
| Git clone | `git clone --depth 100 -- https://github.com/<o>/<r>.git` into `$XDG_DATA_HOME/opencode/repos/<host>/<owner>/<repo>[@branch]`. Default branch, or the `branch` given (separate checkout). Refresh: fetch + hard reset, throttled daily, checked hourly. | `v2-git-lines.log` |
| Private repo | Plain git, so the server process's git credentials. With none: `fatal: could not read Username` → WARN "failed to materialize reference"; the reference is **still listed** to the model at a path that doesn't exist. No status in the API. | `ev-private-repo.log`, `ev-git-change-update.txt` |
| Config changed mid-session | System prompt stays byte-identical; the next request carries a `<system-update>` user message with the delta ("New project references are available…", "…no longer available and must not be used: x"). Bug: the XML in it is HTML-escaped (`&lt;reference&gt;`). | `ev-midsession-change.txt` |
| Per-session entry API | `PUT /api/experimental/session/{id}/instructions/entries/{key}` works on 2.0.18: lands in the system prompt as `<context key="…">`, changes arrive as "The context under … changed and supersedes…" with the system prompt unchanged. | `ev-entry-change.txt` |
| Worktrees | A session in a worktree of the repo gets the main checkout's references, resolved from the main checkout. | `ev-worktree-refs.txt` |

## Other harnesses (proven live unless marked)

- **OpenCode 1 (1.18.33):** native `references` too (not behind the `OPENCODE_EXPERIMENTAL_REFERENCES` flag). Same
  block in the system prompt (after `<env>`), and the agent's defaults allow `external_directory` for reference
  folders (reading `specs` ran; reading `secret` asked). Clones Git references into its own data folder. A config
  change mid-session did **not** reach a running session. Fleet's per-session rules (`external_directory allow`,
  `edit deny` on the path) also give read-only. Evidence: `ev-v1-system.txt`, `ev-v1-fleet-rules.txt`,
  `ev-v1-midsession.txt`.
- **Claude Code (2.1.287):** `--add-dir <path>` allows tool access and lists the folder in the system prompt's
  environment ("Additional working directories: - /path"), without a description. Default mode: reads allowed, writes
  ask. `--disallowedTools "Edit(//path/**)" "Write(//path/**)"` blocks writes even in `bypassPermissions` ("File is in
  a directory that is denied by your permission settings."). Descriptions would go through `--append-system-prompt`,
  which Fleet already uses for memory notes. Evidence: `ev-claude-add-dir.txt`, `ev-claude-readonly.txt`.
- **Pi (0.84.2): not run.** `bunx` hung offline with no request to the scripted model. From its README and source: "No
  permission popups", tools resolve any absolute path (`resolveToCwd`), and `--append-system-prompt` exists. So Fleet
  can tell Pi about references, but can't make them read-only.

## Recommendation (per harness: native, then Fleet, then off)

| Harness | List to the model | Access | Read-only | Mid-session change |
|---|---|---|---|---|
| OpenCode 2 | Fleet's per-session instruction entry, same wording as V2 | session rule `external_directory allow` | session rules `edit`/`write`/`patch`/`apply_patch`/`move` deny | entry diff, cache-safe |
| OpenCode | Fleet plugin adds the block at the session's first request (memory-snapshot pattern) | session rule `external_directory allow` | session rule `edit deny` | `ModelNotes` with the next prompt |
| Claude Code | `--append-system-prompt` (descriptions); `--add-dir` lists the path | `--add-dir` | `--disallowedTools Edit/Write(//path/**)` | next process start (unverified how Fleet restarts it) |
| Pi | `--append-system-prompt` | none needed (no rules) | **not possible**: say so in the UI | next process start |

Git references: Fleet clones them (one copy per machine, Fleet's GitHub sign-in, visible status and errors) and
passes the local path to every harness, rather than each harness keeping its own clone.

## Kit

`kit/` has the scripted OpenAI model (`CALL <tool> <json>` lines drive tool calls), a scripted Anthropic model for
Claude Code, standalone V2/V1 launchers on scratch HOMEs and small drivers. Scratch data lived in
`~/.cache/fleet-oc2-references/`.
