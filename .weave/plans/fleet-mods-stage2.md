# Stage 2: Fleet mods that act

Written 11 October 2026 from `main` at `9fbbbba0`, after the three slices merged. The brief (agreed with the user in
session e07defce): `~/.cache/fleet-mods-stage2/brief.md`. Stage 1 is `fleet-mods-stage1.md`.

## Status (11 October 2026)

Stage 1 mods could only draw (tool rows). Stage 2 lets a mod change what happens, through hooks in Fleet's own server,
so they work across harnesses. All three slices are on `main`, each live-checked on a scratch Fleet (install layout,
pooled OpenCode, Claude Sonnet 5.5) before it merged.

| Slice | What's wired | PR | Lines (incl. tests) | Demo |
|---|---|---|---|---|
| S1 Prompt hook | `prompt.submit`: rewrite a prompt (the bubble shows the new text) or add model-only context (`ModelNotes`). Fails open within 2 s. `$.session.branch()`. Sample: branch-context | #518 | +750 / −21 | Pass |
| S2 Turn end → follow-up | Fleet sends `turn.complete` (with `changedFiles` from `FilesWritten`) before the queue sends its next item; `$.prompt.submit({ text })` queues a follow-up; at most 3 mod follow-ups in a row. Sample: tests-after-edits | #520 | +797 / −20 | Pass |
| S3 Permission asks | `permission.ask` in the relay, for every harness: a mod may deny an ask with a reason (no card, rejected with `<mod>: <reason>`); anything else reaches the user. Sample: push-guard | #521 | +673 / −15 (+2 screenshots) | Pass |

Also merged while main was red: #519 (relay test waited a fixed 200 ms) and #522 (installer test zip wasn't
deterministic: 4 in 6,000 runs failed). Stage total: about 2,250 lines against the ~3,400 budget, about 3.5 h of
wall clock against ~11 h. One review round per PR; no blockers were found; every non-blocking note is below.

Website docs: weave-website #17 (merged 11 October 2026, docs and showcase pages).

Screenshots: `mockups/mods-stage2-s1/`, `mockups/mods-stage2-s2/`, `mockups/mods-stage2-s3/`, and the cross-harness
check in `mockups/mods-stage2-crossharness/`.

## Live checks by harness

| | OpenCode (pooled) | OpenCode 2 | Claude Code | Pi |
|---|---|---|---|---|
| S1 `prompt.submit` | Pass: branch answered from the note, no tool call; `lgtm-expander`, written by the agent with the fleet-mod tools, rewrote the bubble | Pass after #524 (one turn; before it the note cost an extra model turn) | Pass: answered from the note, no tool call | No context by design (no `TakesModelNotes`) |
| S2 `turn.complete` + follow-up | Pass, including the visible queue and the cap | Pass ("Run the tests" sent next; agent ran the tests) | No follow-up: `changedFiles` is always `[]` there, so tests-after-edits never fires (Later) | Not checked |
| S3 `permission.ask` | Pass: push on `main` denied with the reason in the row; other asks and a branch push still asked | In the shared path, not live-checked; V2 may drop the reject text | Pass: push on `main` refused, the reason shows in the row | Pi doesn't ask |

OpenCode 2 ran on a free OpenCode model on the scratch HOME (its Copilot sign-in lives in its database, which wasn't
copied). Claude Code ran on the user's own Claude gateway (the user allowed it; only `ANTHROPIC_BASE_URL` and
`ANTHROPIC_AUTH_TOKEN` were passed to the scratch Fleet, never written to disk).

## Findings from the live checks

1. **Fixed in #524. OpenCode 2 ran each Fleet model note as its own turn.** `OpenCode2HarnessSession.SendPromptAsync` adds every
   `ModelNotes` entry with `AddSyntheticAsync(…, Resume = false)` and then sends the prompt. On a fresh session, V2
   started a turn on the note alone (the model answered "there's no question in your message yet" and ran a tool), and
   the prompt ran as a second turn. This is older than mods: memory changes, lost-work notes and side-conversation
   notes go the same way. S1 makes it frequent. Fix: send the notes with the prompt (as parts of the same message) or
   find why `Resume = false` resumes. **~150–300 lines with a live test.**
2. **Fixed in #525. Mod-sent prompts looked like the user's.** A follow-up from `$.prompt.submit` and a rewritten prompt show as plain
   user bubbles; the cap screenshot needed a reading guide. Fix: a "from <mod>" mark on queued items and on the bubble
   (a column on queued prompts, the client chip). **~300 lines.** Recommended before mods leave Experimental.
3. **OpenCode's reject text says "The user rejected…"** for a mod's deny (the reason itself is right). A "refused by
   push-guard" mark on the row would be clearer. **~150 lines.**
4. `GET /api/sessions/{id}/messages` answered 500 twice while an OpenCode 2 turn was busy, then recovered. Not
   investigated.

## Later (priced; nothing here is started)

From the brief (unchanged): dropping a prompt with a notice (~400); `/commands` (~800); composer band + status chip
wiring (~800); ~~panes + buttons (~1,500)~~ (done 12 October 2026, see "Panes and buttons" below); `$.fs` / `$.process` / `$.http` with declared calls (~2,500, needs a security
review); permission hooks for harnesses the shared path doesn't reach (~600 each; none needed so far); slash commands
and Pi's first prompt through the prompt hook; the M10 hardening list in the Stage 1 plan.

New in Stage 2:
- Claude Code: `changedFiles` from its Edit/Write tool calls, so `turn.complete` hooks such as tests-after-edits work there. ~150
- `allow` decisions for `permission.ask` (needs a per-mod grant and a security review). ~400
- OpenCode 2: show a mod's deny reason to the agent (V2 drops the reject message). ~200

From the review rounds (each one round, non-blocking):
- S1 (#518): prompts wait up to 2 s while the host is starting, even with no `prompt.submit` hook (skip while loading);
  every `$.session.*` read now runs git (fetch the branch only when asked); a dispatch Fleet stopped waiting for still
  runs its hooks; retry/workflow/automation prompts also pass through the hook (ask for idempotent rewrites in the
  docs); a chain of slow mods can drop every change without a strike.
- S2 (#520): an un-awaited `$.prompt.submit` that lands after the dispatch window can wait in the queue of an idle
  session until the next turn (send it if idle); for up to 2 s after a turn a composer send can race the queue's send;
  the types file says a follow-up goes before the queue, it goes behind it (`api.md` is right); a late Stop can mark
  the next turn aborted; every idle now reads the session.
- S3 (#521): a failed reject leaves the agent waiting with no card (should fail open and show the ask); Claude Code
  can flash Needs-you for a refused ask; asks wait up to 2 s while the host loads; OpenCode 1 rejects the session's
  other pending asks on any reject; catch-handler denies read "A mod".

## Panes and buttons (12 October 2026)

Built from the brief agreed in the planning session "Canvas Expansion using Fleet mods"
(`~/.cache/fleet-mod-panes-menu/brief.md`, menu Option A). Three slices, one after another, each live-checked on a
scratch Fleet and reviewed once before it merged. About 1,630 lines against the ~2,100 budget, about 2.5 h against ~7 h.

| Slice | What's wired | PR | Lines (incl. tests, no PNGs) | Demo |
|---|---|---|---|---|
| P1 Buttons for every site | `POST /api/sessions/{id}/mods/action` → `{ handled }`; the client's `modActionSender` is every site's `onAction`; an expired handle answers `handled: false` and the site draws again. Sample: press-counter | #526 | +357 / −8 | Pass |
| P2 Panes a mod opens | Pane site (render request names `mod`, chain narrowed to it); `$.ui.open`/`close` → `mod.pane`; the session page opens/closes the tab and redraws open panes on `mods.changed`/`mod.ui`; a `ui.render` hook can't open or close a pane (review blocker: render loop). Sample: release-board | #527 | +594 / −26 | Pass |
| P3 Declared panes in the + menu | `panes` in `mod.json` (max 8; checked by the host); `GET /mods/sites` → `{ sites, panes }`, off mods' panes from the version store; "From mods" group (Draft, Open, Off hints); Off tab with Turn on; phone session menu and sheet; Keep card "Adds" row | #528 | +683 / −53 | Pass |

Screenshots: `mockups/mods-buttons/`, `mockups/mods-panes-open/`, `mockups/mods-panes-menu/`, and the final check in
`mockups/mods-panes-final/`.

**Final live check (main at `10406034`, install layout, pooled OpenCode, Claude Sonnet 5.5): pass.** Asked plainly for a
"Deploy checklist" pane with "Mark done" buttons, the agent loaded the fleet-mod skill, wrote `deploy-checklist` with a
declared pane, fixed a computed `$.state` key the check refused (the skill lists that rule), and asked to keep it. The
pane was under From mods with a Draft mark, drew, ticked a step on a press, showed Off and "is off" + Turn on, drew
again after Turn on, and the Keep card said "Adds: A pane in the + menu: Deploy checklist". (The first try's check
failed only because the scratch HOME had no `.weave/runtimes/bun/use.json`.) Follow-ups in the same PR as this note:
press-counter and release-board in Settings' sample list, the Mods switch's description says what's wired (it named a
composer band and a status chip), and a menu row's hint gives way before the pane's title.

### Later (priced where known; nothing here is started)

- Pane tabs come back after a page reload, and a `ui.open` sent while no browser listened is replayed; with real
  `$.ui.toast` and `$.session.surfaces()`. ~450
- A kept mod that's off plus an on draft of the same name: the pane is listed twice and its tab sticks on "is off"
  (the draft should win in `ModRenderService.SitesForAsync`). ~60
- Safe mode: on mods' panes vanish while off mods' panes stay listed, and their Turn on draws nothing.
- Two screens on one session (desktop + phone, two tabs) drop each other's handles, so every other press only redraws.
  A press that lands between a redraw and the new tree is dropped too. `handled: false` doesn't say why.
- A throwing button callback isn't shown to the user.
- A Pane draw that calls `$.ui.invalidate` redraws ~10×/s while open (as tool rows do); `$.ui.open` in `session.start`
  runs again on every host restart; each open pane draws twice on keep/reload; `$.ui.open` titles aren't capped
  server-side; an off draft's panes aren't validated; drafts list before kept mods in the menu; the phone sheet has no
  side padding; the pane-renders "newest" map never shrinks; a draft's pane loses its Draft mark while the host reloads.
- Escape didn't close the + menu under headless Chromium (not checked in a real browser).
- Turning a draft off and on again forgets its per-session `$.state` (the checklist came back unticked).
- A long pane title beside the Draft mark and a long mod name still truncates in the 320 px menu ("Deploy checkl…");
  the tab label truncates too. Consider dropping the mod-name hint on draft rows.
- Live checks need a scripted tool row: a scratch Fleet can't create a test-harness session with a scenario through
  the public API (P1/P2 used an uncommitted `Program.cs` patch).
