# Build brief: file viewer option A (wrap + pick lines)

Approved by the user on 15 October 2026 ("Yes, let's build A"). Mockup: `mockups/file-viewer/file-viewer.html`
(section A and the last frame), commit `0bbb47f8` on `fleet/session-d4bc3831`
(`git show 0bbb47f8:mockups/file-viewer/file-viewer.html > /tmp/x.html`).

## Demo sentence

In a 360 px panel, a file with long lines wraps; click line 18's number, shift-click 27, press **Add to message**,
and the composer gets `@src/…/file.ts:18-27`, with no sideways scrolling at any point.

## Budget

One PR into main, about 600 lines including tests (≈350 code + ≈200 tests). One review round. Don't merge:
open the PR with screenshots, wait for CI inside the turn, report back.

## Scope

1. **Soft wrap.** `EditorView.lineWrapping` in a new compartment in `lib/code-editor/editor-extensions.ts`.
   On by default. A per-file choice is remembered in localStorage (store only overrides, cap the map, e.g. 200
   paths). Toggle in the FileCanvas bar: lucide `WrapText`, label "Wrap" when the canvas is ≥430 px wide, icon
   only below, `aria-pressed`. `Alt Z` toggles it in the editor (check it clashes with nothing in Fleet's keymap
   or CodeMirror's defaults). Wrapped continuation lines hang under the line's own indent (+2ch) if that's
   cheap in CM6; drop it and say so in the PR if not. Applies in Edit and Diff (unified merge view).
2. **Line numbers pick lines.** `lineNumbers({ domEventHandlers: { mousedown } })`: click picks the line,
   shift-click extends from the anchor, dragging down the numbers extends. Picked lines are a StateField with
   line decorations (not the text selection): they survive scrolling and a click in the text, and clear with
   Esc, the bar's ×, or a new pick. Style in `theme.ts`: accent 14% line background, gutter number in accent.
   Gutter cursor is a pointer.
3. **The range bar replaces the floating chip** (`FileCanvas.vue`, today `@scroll.capture="hideChip"` hides it
   on any scroll). Docked at the bottom of the file canvas. It shows for picked lines, and for a non-empty text
   selection (the lines it spans, same rule as today's chip). Contents: `L18–27` + "10 lines", **Copy link**
   (copies `path:18-27`, or `path:18`), **Quote** (copies `path:18-27` then a fenced block with the language),
   **Add to message** (primary; `appendDraftReference("@path:18-27")` + `focus-prompt`, as the chip does now;
   keep `data-testid="add-to-message"` on it), × clear. Copy and Quote go icon-only below ~420 px. Copies confirm
   with the canvas's existing toast. Scrolling never hides the bar.

Out of scope: Focus (option B), path-bar menus, Ctrl P changes, outline, tree keyboard (Later list).

## Files (expected)

`lib/code-editor/editor-extensions.ts`, new `lib/code-editor/line-pick.ts`, new small wrap-preference module,
`lib/code-editor/theme.ts`, `components/canvas/FileCanvas.vue`, tests beside them
(`lib/code-editor/__tests__/`, `components/canvas/__tests__/FileCanvas.test.ts`).

## Checks

- Vitest for line picking (click, shift-click, drag, Esc, survives a text click), wrap preference (default on,
  override remembered), FileCanvas (toggle, bar label, Copy link text, Add to message adds the draft reference).
- Client checks the way CI runs them (Node 22, `bunx npm ci`) as well as `bun run test`; `bunx vue-tsc --noEmit`.
- Live check in the real built app on a scratch Fleet (scratch HOME; never the real HOME or `~/.weave`; never
  port 5173). Screenshots with Playwright `headless_shell`
  (`~/.cache/ms-playwright/chromium_headless_shell-1161/chrome-linux/headless_shell`, playwright-core from
  `~/source/weave-fleet/.poc-runtime/pw/node_modules`): a 360 px panel with wrap on and lines 18–27 picked, the
  wrap toggle off, the composer with the added reference, phone width. Invented content only. Commit them under
  `mockups/file-viewer-a/` and embed them in the PR body from the moment it opens. Kill any Chrome you start.
- Rebase on origin/main, not merge.

## Decision to flag in the PR

Wrap is **on by default** in the panel, as the approved mockup showed. The user hasn't explicitly confirmed the
default; say so in the PR so it can be flipped in one line.
