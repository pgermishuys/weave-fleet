# Copy folder path: build brief (option A)

The user chose option A on 15 October 2026. Mockup: `mockups/copy-folder-path/index.html` (screenshots beside it).

**Demo sentence:** in a worktree session on a narrow window, hover the header's path, click the copy icon, see
"Copied", and paste `/home/…/fleet-session-…` into a terminal.

**Budget:** about half a day, one PR of ~150–250 lines including tests. Client only, no server change.

## What to build

1. **Header copy button** (`client/src/components/session/SessionDetailHeader.vue`, the `.session-detail-header__directory` span):
   - Wrap the path and a 20px icon button (lucide `Copy`, check when copied) in one inline-flex element.
   - The button is always in the tab order. It's hidden (opacity 0) until the path is hovered or the button is
     focused, and it's always shown at ~0.75 under `@media (hover: none)`.
   - `aria-label="Copy folder path"`, `title` = the full path. A visible 2px accent focus ring, like `__from`.
   - Click writes `props.directory` (the session's working folder, the worktree in a worktree session) with
     `navigator.clipboard.writeText`. The icon becomes a check in `--running` and "Copied" shows after it for 1.5 s,
     the same as `MessageBubble.vue`'s copy button. A polite live region announces "Folder path copied".
   - The button never shrinks (`flex: none`, wrapper `min-width` covers it).
2. **Cut from the start:** split the path at its last `/` or `\` into a head and a leaf. The head gets the ellipsis and
   gives way first (high `flex-shrink`), so `…/fleet-session-7f3a9c21` stays readable. The copy is always the full path.
3. **Desktop ⋯ menu** (`client/src/components/session/SessionActionToolbar.vue`, "More actions"): add
   **Copy folder path** as the first row, for windows too narrow to show the path. It needs the directory passed in
   from `routes/sessions.$id.tsx`.
4. **Phone app ⋯ menu** (`client/src/components/phone/session/SessionMenuSheet.vue`): add **Copy folder path** after
   "Run a command", hint = the folder's name. On tap, copy the working folder and show the phone toast
   (`PhoneToastHost`) "Folder path copied". Wire it in `PhoneSessionPage.vue`'s `onMenu`.

Clipboard failures stay silent, as in `SessionItem.vue`'s `handleCopyPath`. Windows paths are copied unchanged.
Sessions on another machine copy their path too.

## Not in this PR (Later)

Copy source repo path, Copy as `cd` command, Open in from the header: option B's menu, added later as a right-click
on the path if the user asks.

## Tests and proof

- Unit tests in `client/src/components/session/__tests__/SessionDetailHeader.test.ts`: copies the full path, shows
  "Copied" then clears, the button has its label, and the leaf is rendered separately from the head. Add tests for
  the toolbar row and the phone menu row.
- Screenshots from the real built app (Vite mock mode or a scratch Fleet with a scratch HOME, never port 5173). Show
  hover, Copied, a narrow window keeping the folder's name, and the phone menu row. Put them in the PR body when the
  PR is opened.
