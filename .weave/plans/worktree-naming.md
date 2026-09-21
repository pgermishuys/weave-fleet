# Worktree naming

Fleet names every new worktree the same way: branch `fleet/<slug-of-your-message>`, folder
`{repo}-worktrees/<branch with slashes flattened>`. Organizations have their own conventions —
initials prefixes, ticket keys, a single worktree root outside the repo's parent — and none of
them are reachable today.

Mockup: `mockups/worktree-naming/worktree-naming.html` (and the hosted copy). It resolves with a
port of the real slug and branch-validation rules, so what it shows is what ships.

## What's hard-coded today

| Decision | Today | Where |
|---|---|---|
| Branch name | `fleet/<slug of first line>` | `client/src/lib/new-session-request.ts:53,116` — **the browser** |
| Branch fallback | `weave-session-<8 hex>` | `WorkspaceService.cs:264` |
| Worktree root | `{parent}/{repo}-worktrees` | `WorkspaceService.cs:272`, mirrored in `GitPaths.cs:38` |
| Folder name | branch, slashes flattened, `-2` on collision | `WorkspaceService.cs:316` |

The branch name being a client decision is the structural problem: automations, the GitHub
source and any API-created session already name differently, because the server only invents a
name when the client sent none. Customization has to move naming to the server, or it inherits
that split.

## Design

One `worktrees` block of templates, resolved server-side at worktree creation:

```jsonc
{
  "worktrees": {
    "prefix":  "pg",                            // default: "fleet"
    "branch":  "{prefix}/{slug}",
    "root":    "{repoParent}/{repo}-worktrees",
    "folder":  "{branch}",
    "capture": { "ticket": "[A-Z]{2,}-\\d+" }
  }
}
```

**Layers.** Fleet defaults, then the user's settings, then the repository's committed
`weave.jsonc`. Project wins, field by field — a repo that sets only `branch` leaves the user's
`root` alone. The committed layer is the point of the feature: one file in the repo and the whole
team names branches the org's way.

**The prefix is the primary control.** The default branch template is `{prefix}/{slug}` with the
prefix defaulting to `fleet`, so "put my initials in front" is one field and no template editing,
and today's names are unchanged. A prefix rather than literal text in the template is what lets a
repository commit a convention with a per-person slot in it.

**Tokens.** `{slug}` `{repo}` `{user}` `{prefix}` `{date}` `{shortid}` `{ticket}`, plus
`{repoParent}` and `{home}` in `root`, and `{branch}` in `folder`. Deliberately small.

**Capture.** One regex per named capture, read from the message, so `{ticket}` has a value.

### Rules the resolver owes us

Both of the first two came out of running the mockup, not reading it.

1. **Empty slug hands naming back.** `{prefix}/{slug}` with an unsluggable message ("🚀🚀🚀")
   collapsed to a branch literally named `pg` — valid git, useless, and it collides with the next
   one. When a template references `{slug}` and the slug is empty, the server names the worktree
   `weave-session-<hex>` as it does today. An empty prefix is refused for the same reason.
2. **A capture is removed from the slug.** `feature/{ticket}-{slug}` produced
   `feature/PLAT-1841-plat-1841-add-rate-limiting`, because the slug is taken from the whole first
   line. What a capture consumed leaves the text before slugging.
3. **Empty tokens collapse cleanly.** No `//`, no `--`, no leading or trailing separator.
4. **Everything is validated.** The result goes through `IsValidBranchName`
   (`WorkspaceService.cs:409`) and an unknown token is an error — both at save time, so a bad
   template is rejected in Settings rather than at `git worktree add`.
5. **Uniqueness is unchanged.** A taken branch or folder still gets `-2`, `-3`.
6. The captured ticket keeps its case (`PLAT-1841`) while the slug is lowercased.

### Where the code goes

- `WorktreeNaming` (Application) — the templates, the layer merge, validation.
- `WorktreeNameResolver` — pure token resolution and collapse.
- `BranchSlug` — the slug, ported from `new-session-request.ts`.
- User layer in `IUserPreferenceRepository`; project layer read from `weave.jsonc` at the repo root.
- `GET`/`PUT /api/worktrees/naming` — typed, validates on save, returns the effective layers.

**The client keeps a preview, not the decision.** A per-keystroke round trip for the composer's
plan line is not worth it, so the TS resolver stays for preview only and the composer stops
sending a generated branch — it sends one only when someone typed an override. A shared
conformance fixture (`worktree-naming-cases.json`) is asserted by both xunit and vitest so the
two resolvers cannot drift, following the OpenCode 2 conformance-fixture precedent.

## Stages

**Stage 1 — branch template.** Resolver, slug, fixture, settings storage and endpoint, server-side
naming in `CreateWorktreeAsync`, composer preview, client stops deciding. Root and folder keep
today's behaviour.

**Stage 2 — root and folder templates.** Also fixes what assumes the `-worktrees` suffix:

- **Cleanup** (`WorkspaceService.cs:189`) only removes an empty parent whose name ends in
  `-worktrees`, so a custom root would leak empty folders.
- **The allowlist** (`WorkspaceRootService.cs:95`) derives permitted paths from
  `GitPaths.WorktreesFolderFor`. A root outside the repo's parent (`~/worktrees/<repo>`, common on
  Windows for path length) is rejected as "outside allowed workspace roots" until the resolved
  root is allowed too.
- **The composer label** (`new-session-plan.ts:39`) hard-codes `{repo}-worktrees/…` and would lie.

**Stage 3 — post-create hook.** Not started, and deliberately last: a command run in a new
worktree (copy `.env`, symlink `node_modules`, `direnv allow`) is the escape hatch for patterns
templates can't express. Fleet has no hook infrastructure, so this is its own decision once
Stages 1 and 2 are in use.

## Decisions taken

- **`weave.jsonc`, not `weave-opencode.jsonc`.** The existing file already has the user+project
  merge, but it is the OpenCode config and this is not an OpenCode concern.
- **A generic `prefix`, not an `initials` field.** Initials are too specific a thing for config;
  a prefix covers initials, a team, a handle. It stays a value rather than literal template text
  so a committed convention can leave a per-person slot.
- **User settings live in the preference store**, not localStorage like the rest of
  Settings → Workspace, because the server resolves the template.
