# Upstream

fleet-plan is adapted from **html-plan** by Thariq Shihipar:

- Source: https://github.com/anthropics/claude-plugins-community/tree/main/html-plan
- Commit: `f60f0454df3045f724c43c6346ec80bdcc3472b2` (2026-10-05), plugin version 1.0.0
- License: html-plan's `plugin.json` declares MIT and names the author. html-plan publishes no copyright line. The
  repository it is published in is licensed Apache-2.0. Fleet follows both: `LICENSE` (MIT text and this summary) and
  `LICENSE-APACHE-2.0` (as published) are in this folder, and ship with it.

## Files

| File | From upstream |
|---|---|
| `runtime/htmlplan.css` | a license and attribution notice added at the top; nothing else |
| `runtime/pack.mjs` | unchanged |
| `references/blocks.md` | unchanged |
| `examples/scheduled-send.html` | unchanged |
| `runtime/htmlplan.js` | a license and attribution notice added at the top, and the patch below |
| `SKILL.md` | written for Fleet from upstream's `SKILL.md`; says so in its first lines |

## Changes to `runtime/htmlplan.js`

A page tab in Fleet is sandboxed without storage, and html-plan kept its state in `localStorage`. The patch is
marked `Fleet (patch, see ../UPSTREAM.md)` in the file. It talks to Fleet with `postMessage`
(`client/src/lib/page-bridge.ts` in Fleet):

- On start, when the page is in a frame, it sends `fleet:page-hello` and waits up to 400 ms for Fleet's
  `fleet:page-state` reply. If Fleet replies, the page restores the saved state from it and knows it is in Fleet. If
  not, it starts as upstream does.
- Each save also sends `fleet:page-state` to Fleet, which keeps it for the page.
- In Fleet, the Respond sheet has **Send to agent**, which sends `fleet:page-reply` with the markdown response.
  Fleet puts it in the chat composer. Upstream left this button as an empty stub (`liveOn = false, send = null`).

## Why the notices are in the runtime files

`pack.mjs` inlines `htmlplan.js` and `htmlplan.css` into every plan page it writes, so each page is a copy of
them. The notices at the top of both files travel with every page.

To update from upstream, copy the files again, put the notices back at the top of `htmlplan.js` and `htmlplan.css`,
and apply the three changes above to `htmlplan.js`.
