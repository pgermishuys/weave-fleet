# Upstream

fleet-plan is adapted from **html-plan** by Thariq Shihipar:

- Source: https://github.com/anthropics/claude-plugins-community/tree/main/html-plan
- Commit: `f60f0454df3045f724c43c6346ec80bdcc3472b2` (2026-10-05), plugin version 1.0.0
- License: MIT, as declared in the plugin's `plugin.json`. The repository it sits in is licensed Apache-2.0.

## Files

| File | From upstream |
|---|---|
| `runtime/htmlplan.css` | unchanged |
| `runtime/pack.mjs` | unchanged |
| `references/blocks.md` | unchanged |
| `examples/scheduled-send.html` | unchanged |
| `runtime/htmlplan.js` | changed, see below |
| `SKILL.md` | rewritten for Fleet from upstream's `SKILL.md` |

## Changes to `runtime/htmlplan.js`

A page tab in Fleet is sandboxed without storage, and html-plan kept its state in `localStorage`. The patch is
marked `Fleet (patch, see ../UPSTREAM.md)` in the file. It talks to Fleet with `postMessage`
(`client/src/lib/page-messages.ts` in Fleet):

- On start, when the page is in a frame, it sends `fleet:page-hello` and waits up to 400 ms for Fleet's
  `fleet:page-state` reply. If Fleet replies, the page restores the saved state from it and knows it is in Fleet. If
  not, it starts as upstream does.
- Each save also sends `fleet:page-state` to Fleet, which keeps it for the page.
- In Fleet, the Respond sheet has **Send to agent**, which sends `fleet:page-reply` with the markdown response.
  Fleet puts it in the chat composer. Upstream left this button as an empty stub (`liveOn = false, send = null`).

To update from upstream, copy the files again and apply these three changes to `htmlplan.js`.

## MIT License

Copyright (c) Thariq Shihipar

Permission is hereby granted, free of charge, to any person obtaining a copy of this software and associated
documentation files (the "Software"), to deal in the Software without restriction, including without limitation the
rights to use, copy, modify, merge, publish, distribute, sublicense, and/or sell copies of the Software, and to permit
persons to whom the Software is furnished to do so, subject to the following conditions:

The above copyright notice and this permission notice shall be included in all copies or substantial portions of the
Software.

THE SOFTWARE IS PROVIDED "AS IS", WITHOUT WARRANTY OF ANY KIND, EXPRESS OR IMPLIED, INCLUDING BUT NOT LIMITED TO THE
WARRANTIES OF MERCHANTABILITY, FITNESS FOR A PARTICULAR PURPOSE AND NONINFRINGEMENT. IN NO EVENT SHALL THE AUTHORS OR
COPYRIGHT HOLDERS BE LIABLE FOR ANY CLAIM, DAMAGES OR OTHER LIABILITY, WHETHER IN AN ACTION OF CONTRACT, TORT OR
OTHERWISE, ARISING FROM, OUT OF OR IN CONNECTION WITH THE SOFTWARE OR THE USE OR OTHER DEALINGS IN THE SOFTWARE.
