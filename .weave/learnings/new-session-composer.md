# Learnings: New Session Composer

Plan: `.weave/plans/new-session-composer.md`. PRs: #193 (Stage 0), #197 (Stage 1), #199 (Stage 2), #200 (phone layout), and the Stage 3–4 PR.

## First-message delivery

The hardest part of "Enter creates the session and sends the first message" was making that message show up, once, straight away. It took three separate fixes.

- **The broadcast went out before anyone listened.** `CreateSessionAsync` handed `InitialPrompt` to `SpawnAsync`. The orchestrator broadcast the user message (live only, never persisted) during the create request, before the page had subscribed. The page's snapshot was taken before OpenCode stored its echo, and `HarnessEventRelay.ShouldSuppressUserEcho` drops that echo. So the agent got the message, but the page only showed it after a reload. This was already true for GitHub starts and automations; nobody had noticed.
  - Fix: when the harness doesn't need the prompt at spawn (`RequiresInitialPrompt` false), spawn without it, then send it through `PromptSessionCoreAsync` (with a message id and the subscription ready) and save the user row before returning.
  - Correction to the first diagnosis: a live OpenCode session's snapshot comes from OpenCode (`OpenCodeSessionMessageProxy`), not Fleet's database. So the proxy also adds saved prompts OpenCode hasn't stored yet. It keeps the same id and drops them once OpenCode has the prompt, or anything newer.
- **A pre-snapshot event blanked the page.** The hub sends `message.updated` with the harness's raw payload, which has no `parts`. `applyMessageLifecycle` mapped `payload.parts` unconditionally. When that event arrived before the snapshot, it was replayed on top of it and the throw discarded the snapshot, leaving a blank session until reload (5 of 9 live runs). Now an update without parts keeps the existing parts. The hub still sends the raw payload; that's a follow-up.
- **The wait for the snapshot.** Even when correct, the message appeared 0.1–3.6 s after the page opened, mostly while OpenCode started in a new directory. Stage 2 moves the message into the page's conversation at Enter and seeds the session's sent-prompt list (`seedSentPrompt`) before navigating. The history's copy then reconciles it by text. That was only safe once the snapshot was guaranteed to carry the message: before that, `ActivityStream` cleared optimistic prompts when the agent replied, and the message vanished.
- Seeding a client-side registry can't fix a server that never persists the message. Before layering optimism on top, check that the source of truth has the thing.

## Watching every frame finds what screenshots miss

- The live checks watch the first message and the draft row on every animation frame from Enter until a few seconds after the session opens. They record when it first shows, the most copies on screen at once, frames where it was missing, and whether the row moved or resized. Screenshots at fixed moments had shown nothing wrong in runs where the frame watcher later caught a blank page or a doubled message.
- A row that becomes another row: `ProjectGroup`'s TransitionGroup keys rows, so the draft row and the session's row animated out and in. Letting the session take over the draft's key (`sessionRowKeys`) makes Vue patch the row in place.

## Scratch runtime

- Release publish is Native AOT. A `WeaveFleet.Api.dll` left in a publish folder is from an older build and runs stale server code, so run the `WeaveFleet.Api` binary. One live check "failed" for an hour on already-fixed code because of this.
- The Api publish doesn't build the client; copy `client/dist` into `wwwroot`. To compare two clients against one server, symlink everything but `wwwroot` into a second folder (`host-bin-stage1`, `host-bin-phone`). The binary serves `wwwroot` from its working directory.
- Every Fleet run, including test suites that boot `Program`, gets a scratch `HOME` (`.poc-runtime/test-scratch-home.sh`, `start-host.sh`).

## Choosing a base

- A user-supplied branch name reaches `git fetch origin <name>` and `git worktree add … <base>`. Arguments go through `ArgumentList`, so there's no shell, but git itself still reads `--upload-pack=…` as an option, and `main:refs/heads/main` as a refspec that writes a local ref. Validate against check-ref-format's rules before any git command runs (`WorkspaceService.IsValidBranchName`).
- Name the base exactly as git shows it (`origin/release/2.0` or `feature/x`), and let the chip say exactly that. "Fetch first" only makes sense for origin's branches, so the switch is disabled for local ones instead of doing something surprising.
- Resolve and fetch the base before creating anything, so an unknown base leaves no folder or branch behind.
- `git fetch origin <branch>` updates `refs/remotes/origin/<branch>` even for a branch never fetched before (opportunistic update through the configured refspec), so a remote-only base works with one fetch.
- Live tests need the two copies of a branch to differ: the test repo's origin gets a commit on `release/2.0` after the clone fetched it. Then "fetch off" and "fetch on" start from provably different commits.

## A shared frame without changing the old one

- To prove a refactor left something alone, screenshot the element with both builds and diff the pixels. There's no image library on this machine; drawing both PNGs into a canvas in the Playwright browser and comparing `getImageData` works. It showed the session composer identical when blurred, with the removed focus ring as the only difference when focused.
- Small style differences hide in "the same" CSS: one textarea was `display: block` and the other inline-block (a few pixels of descender space under it). The shared frame took the session composer's exact styles, and the new page moved 5 px instead.

## Things found along the way

- `git worktree add -b` with no start point starts from the main checkout's HEAD, which is whatever branch you left it on. Worktrees now start from `origin/<default>`, freshly fetched, with `--no-track`.
- openapi-fetch consumes the response body, so reading it again gave "HTTP 400" for every error. Use the parsed `error` it returns.
- The global focus ring is `*:focus-visible { … !important }` in `@layer base`. An unlayered `!important` loses to a layered one, so `box-shadow: none !important` in a scoped style did nothing: the session composer drew a 1 px ring around its textarea inside the rounded frame. Override it inside `@layer base`.
- At 400 px the session page gave the conversation 64 px because the right panel kept its 360 px column. Narrow desktop windows had the same problem (70 px at 800 px). The panel is now a column only while the conversation keeps 480 px, and a sheet otherwise.
