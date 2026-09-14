# File editor: files open as tabs

Click a file in Files or Changes, or press Ctrl P, and it opens in its own canvas tab as an
editable file: CodeMirror 6 with Fleet's syntax colours and small autocomplete. Ctrl S saves.
If the agent changes the file while you have it open, a file you haven't touched updates in
place. A file you have changed is never overwritten: a bar offers Compare, Keep mine or Use the
agent's. Markdown and HTML still open rendered, with the source one click away.

Mockup (option B): https://claude.ai/code/artifact/30a718b9-2013-4d0b-ae43-05b760332088
Source: `mockups/editor/fleet-editor.html` (the editor bundle is built from `mockups/editor/cm-entry.js`).

## Decisions already made

- **Option B, files open as tabs.** Not the in-place viewer (A), focus mode (C) or the chat peek (D).
- **CodeMirror 6, not Monaco.** Measured 2026-09-14: 210 KB gzip with the merge view and 5
  languages, against ~2.6 MB for Monaco and its workers. CodeMirror works on phones, and its
  colours can come from Fleet's CSS variables, so every theme works without extra code.
- **Markdown and HTML open rendered**, with `Rendered · Source · Diff` in the tab bar. Rendered
  shows the unsaved buffer. Markdown keeps today's annotations; HTML keeps `HtmlRenderer`'s
  sandboxed iframe.
- **The agent never opens tabs.** There is no agent tool in this work. Clickable `file:line`
  paths in replies may come later as separate work.
- **Nothing about your edits goes to the agent.** It reads files from disk. The server's hash
  check stops a save from overwriting the agent's work.
- **Autocomplete is tiers 1 and 2 only:** words, keywords and snippets from the file, plus import
  paths from the repo's file list. No language server, no AI completion.

## Behaviour

- **Opening.** One click in Files opens a *preview* tab (italic label). The next single click
  replaces it, unless it has unsaved changes. Typing in it, double-clicking the tab or
  double-clicking the file in the tree keeps it. Opening from Changes, Ctrl P or a tool row in
  the conversation keeps the tab too. An already open file is focused, not opened twice.
- **Views.** Code files: `Edit · Diff`. Markdown/HTML: `Rendered · Source · Diff`. Files open in
  Rendered or Edit; from Changes they open in Diff. Diff is the git base against the buffer,
  editable, with a revert button per hunk (`@codemirror/merge` unified view). Diff is disabled
  when the file has no changes.
- **Unsaved changes.** The tab shows a dot instead of the close ×. Closing a tab with unsaved
  changes asks first. A `beforeunload` prompt appears while any buffer is unsaved.
- **Saving.** Ctrl S (Cmd S on macOS) while the editor has focus, or the Save button in the bar
  (phones). The request carries the hash of the file as last read. On success the dot goes and a
  toast says "Saved src/…".
- **The agent changes an open file.** Triggers: `files.changed` for that path, the tab becoming
  active, and the window regaining focus. When any of these happens, Fleet reads the file again,
  and if the hash differs:
  - **no unsaved changes:** apply the change as a minimal edit (the cursor and scroll mostly stay
    put), flash the changed lines and pulse the tab.
  - **unsaved changes:** show the bar. *Compare* opens the merge view (the agent's version against
    yours; reject hunks to take the agent's lines, then Done and save). *Keep mine* saves your
    buffer over the current file. *Use the agent's* replaces your buffer.
- **A save that loses the race** (the file changed after the last read) gets a 409 from the
  server and shows the same bar.
- **Agent-changed lines.** A thin accent stripe in the gutter marks lines that differ from the
  git base. This is the same data the Changes canvas shows.
- **Add to message.** Select lines and a chip offers "Add lines 14–18 to message". It appends
  `@src/middleware/auth.ts:14-18 ` to the session's draft (drawn as a reference pill by
  `composer-references.ts`) and focuses the composer. Nothing is sent until you send.
- **Go to file.** Ctrl P anywhere in a session (and a command palette entry) opens a fuzzy file
  picker fed by `GET /api/sessions/{id}/find/files`. Enter opens the file as a kept tab.
- **Files you can't edit.** Over 512 KB, binary, or not valid UTF-8: the tab opens read-only and
  says why ("Too large to edit here (over 512 KB)"). The read endpoint already flags all three.
- **Phone.** The tab strip and editor fill the sheet. Save is the button, since there's no Ctrl S.

## Changes

### Task 0: Spike (half a day)
- [x] Add CodeMirror to the client as a lazy chunk. Check its real size in Fleet's Vite build
      and that the initial bundle doesn't grow.
- [x] Round-trip a CRLF file, a file with a BOM, and a file without a trailing newline through
      an EditorState and back, byte for byte. CodeMirror joins lines with `\n` by default; set
      `EditorState.lineSeparator` from the file.
- [x] In a scratch Fleet (scratch HOME) with a real OpenCode session, confirm that an agent edit
      emits `files.changed` with the edited path. Also check whether a `bash`/`sed` edit emits
      it. If it doesn't, the focus and hash re-checks cover it; write down what we found.
- [x] Try `@codemirror/language-data` (lazy loaders for ~100 languages, including C#, Go, Python
      and Rust) against a fixed set of language packages. Pick whichever keeps the chunk small.

**As built (2026-09-14).**
- *Size.* A lazy `import()` puts CodeMirror in its own chunks. The initial bundle stays the same
  (+55 bytes for the import stub). The editor with the mockup's fixed set (JS/TS, JSON, Markdown,
  CSS, HTML, merge) is one 210 KB gzip chunk.
- *Languages: `@codemirror/language-data` chosen.* The core (state, view, commands, search,
  autocomplete, merge, the language list) is 141 KB gzip, and each language loads when a file of
  that type opens: TS/JS +42 KB, C# +8 KB, Python +28 KB, Go +21 KB, Rust +34 KB, JSON +10 KB,
  Markdown +81 KB (it pulls in HTML, CSS and JS for embedded code). So a TS file costs 183 KB
  against 210 KB, and C#, Python, Go, Rust, YAML and SQL get highlighting. The build output
  grows by 1.8 MB on disk (115 small language chunks, fetched only when used). Import-path
  completion attaches to the JS/TS language once it has loaded, so `lang-javascript` stays out
  of the core. Direct dependencies: state, view, language, language-data, commands, search,
  autocomplete, merge, `@lezer/highlight`.
- *Line endings and BOM:* `client/src/lib/code-editor/text-format.ts`, with 19 tests (LF, CRLF,
  CR, mixed, stray CR, BOM, no trailing newline, empty, non-ASCII). Everything round-trips byte
  for byte. What it takes:
  - `lineSeparator` is set to the file's most common break. Then a mixed file keeps its odd
    breaks as characters and saves back unchanged.
  - Save with `state.sliceDoc()`. `doc.toString()` always joins with `\n`, even with
    `lineSeparator` set.
  - Pasted and dropped text is converted to the file's break with
    `EditorView.clipboardInputFilter`. Otherwise an LF paste lands inside one line of a CRLF file.
  - The server's `UTF8Encoding.GetString` keeps a BOM as U+FEFF. The client strips it on read
    and puts it back on save, so the server writes the string as UTF-8 without adding anything.
  - If the agent's version changes the break style or the BOM, the buffer can't take it as a
    minimal edit. It gets a new state instead.
- *`files.changed`: today it never reaches the browser.* In a scratch Fleet with OpenCode 1.18.30
  and a scripted model:
  - The `edit` and `write` tools make OpenCode emit `file.watcher.updated` with
    `{ file: <absolute path>, event: "add" | "change" | "unlink" }`. Fleet translates it to
    `FilesChanged`, and then `InProcessEventPublisher` drops it: "Publish dropped for unclassified
    event type file.watcher.updated". `EventTypeMetadata.Classify` has no entry for it. So the
    `files.changed` handlers in `use-diffs` and `use-file-browser` never run today, and Changes
    only refreshes on `turn.ended`.
  - A `bash` `sed -i` edit emits no file event, and neither does an edit made outside the agent.
    OpenCode has no filesystem watcher running here; the event comes from its own edit and write
    tools. The `patch` parts at the end of each step do list the files `sed` touched, but that
    is OpenCode-specific.
- *Plan changes this leads to (small; the decisions stand):*
  - **Task 1** also classifies `file.watcher.updated` as an ephemeral relay event, so
    `files.changed` reaches clients. It maps OpenCode's `event` field to `changeType` too.
    Paths arrive absolute; the client matches them against the session directory.
  - **Task 5** also re-checks open files on `turn.ended`. That catches shell edits at the end of
    each turn without anything harness-specific. Focus, tab activation and the save-time hash
    stay as the other safety nets.

### Task 1: Server, hash on read and a save endpoint
- [x] `ReadFileResult` and `ReadSessionFileResponse` gain `Hash` (SHA-256 hex of the file bytes).
- [x] `SessionOrchestrator.WriteSessionFileAsync(sessionId, path, content, baseHash, ct)`:
  - same validation as `ReadSessionFileAsync` (required path, session directory exists,
    `IsSameOrChildPath`), plus:
    - resolve symlinks and refuse targets outside the session directory
    - refuse anything under `.git/`
    - the file must already exist (no create in this work)
    - content must stay under 512 KB once UTF-8 encoded
    - refuse a file the read endpoint would call binary
  - a per-file `SemaphoreSlim` around check-and-write
  - if the current hash ≠ `baseHash`, return a conflict with the current content and hash
  - write in place (open the existing file, truncate, write) so the inode and permissions
    (such as `+x`) stay; UTF-8 without adding a BOM
  - return the new hash
- [x] `PUT /api/sessions/{id}/files/content` with body `{ path, content, baseHash }` →
      `200 { hash }`, `409 { content, hash }`, `400`, `404`. Same auth and session lookup as the
      read endpoint.
- [x] After a save, publish `FilesChanged` for the session and path, so the Changes canvas and
      other open windows refresh. Check how the orchestrator reaches the event broadcaster first.
- [x] Structured log line per save (session, path, user), to follow the constitution's data rule.
- [x] Classify `file.watcher.updated` as an ephemeral relay event so `files.changed` reaches
      clients (Task 0 found it dropped), and map OpenCode's `event` field to `changeType`.
- [x] Regenerate the client's OpenAPI types.
- [x] Tests (Application): traversal, symlink out, `.git/`, too large, binary, missing file,
      stale hash → conflict, CRLF and BOM bytes preserved, permissions kept on Unix.
      Tests (API): 200 and 409 shapes.

**As built.** `SessionOrchestrator.FileWrites.cs` holds the save: `WriteSessionFileAsync`,
`HashFileBytes` (SHA-256, lowercase hex) and `ResolveRealPath`, which follows every symlink
along the path, like realpath(3). A symlink that stays inside the session can be saved through.
The read endpoint uses the same `DecodeText` rule, so "binary" means the same on read and save.
The `FileMode.Truncate` write keeps the inode; a hard-link test proves it. The server writes the
string as it's given, so a BOM survives because the client sends U+FEFF back. The per-file lock
is keyed by the real path. A save broadcasts `files.changed` with the relative path. An agent
edit broadcasts the absolute path, because OpenCode sends one; the client accepts both.
`InProcessFanOutService` now sends Fleet's `{ sessionId, files }` payload for `FilesChanged`.
Without that, the classification fix alone would have delivered OpenCode's raw `{ file, event }`
under the `files.changed` type. The regenerated `schema.d.ts` also picks up canvases,
terminals, apps and bridge routes that had never been generated. The client still calls those
through its own hand-written modules. Tests: 23 in `SessionFileWriteTests`, 2 in the API tests,
plus translator, fan-out and classification tests. Checked live in the scratch Fleet: an agent
edit and a save each reach a SignalR client as `files.changed`, and a stale save gets 409 with
the current content.

### Task 2: Client editor core
- [x] `client/src/lib/code-editor/`: the extension set (line numbers, history, folding, bracket
      matching, close brackets, search, autocompletion), language by file name, and the theme.
      The theme maps highlight tags to the existing `--syntax-*` tokens that `markdown.css` uses
      for code blocks, and chrome to `--text`, `--muted`, `--border`, `--accent`. Check all nine
      themes.
- [x] Completion sources: `completeAnyWord`, the language's own (keywords, snippets, locals),
      and import paths inside `from "…"` / `import("…")` from `find/files` (cached per session).
- [x] Gutter stripe and line flash as small extensions (the mockup's `cm-entry.js` has both).
- [x] `stores/file-buffers.ts`: per session and path, keep `{ state: EditorState (markRaw),
      baseHash, diskText, conflict }`. Buffers live outside the components, because
      `CanvasHost`'s `KeepAlive :max="8"` evicts the 9th tab and would lose unsaved text and
      undo history. The `beforeunload` guard reads this store.
- [x] Unit tests (vitest): line-ending round trip, dirty/clean/conflict transitions, minimal
      change application.

**As built.** All under `client/src/lib/code-editor/`:
- `theme.ts`: syntax colours from `--syntax-*` (plus `--md-heading` for Markdown headings), and
  chrome, selection, search, diff and the stripe from `--text`, `--muted`, `--accent`,
  `--running` and `--error` through `color-mix`. No theme needs its own rule.
- `languages.ts`: `language-data` by file name, plus aliases for .NET project files
  (`.csproj`, `.props`, `.slnx`) and `.env`.
- `completion.ts`: `completeAnyWord` and import paths. The language adds keywords, snippets and
  locals itself. Import paths list one folder at a time from `find/files?q=<folder>/` (cached
  10 s), which is the endpoint's own folder listing, instead of the whole repo's file list.
  `./`, `../` and `@/` resolve against the file (`@/` means the nearest `src/`). Package imports
  get nothing.
- `agent-lines.ts`: the stripe marks lines that differ from the git base. The diff is by line:
  each distinct line is encoded as one character and passed to `@codemirror/merge`'s `diff`.
  `Chunk.build` merges nearby changes, and its character diff can't tell which line an inserted
  line belongs to.
- `minimal-change.ts`: compares documents, not strings, so a CRLF break counts as one position
  and the insert keeps its lines whatever the separator.
- `file-buffer.ts`: the transitions (load, dirty, apply a disk change, conflict, saved, take the
  agent's version, rebase after Compare). They go through the mounted view when there is one.
  "Use the agent's" is its own undo step (`isolateHistory`).
- `buffers.ts`: the API side (open, refresh, save, overwrite).
- `stores/file-buffers.ts`: reactive info per buffer (status, dirty, conflict, saving), plus a
  plain record holding the `EditorState`. It has only type imports from CodeMirror, so it stays
  out of the lazy chunk.
- Tests: 63 in `code-editor/__tests__` and the store. The theme is checked across all themes in
  Task 7's screenshots.

### Task 3: File tabs in the canvas
- [ ] `CanvasKind` gains `"file"`, and `CanvasInstance.file = { path, preview, view }`. Tab id
      `file:<path>`.
- [ ] Store: `openFile(sessionId, path, { keep?, view? })` with the preview rule, plus `keepFile`
      and `setFileView`. `close` asks when the buffer is dirty.
- [ ] `canvas-registry.ts`: `file` → `FileCanvas` as an async component (the lazy chunk). Title =
      file name, icon by type. `CanvasHost`: italic preview label, unsaved dot in place of ×,
      double-click keeps; pass `{ sessionId, path }` in `activeProps`.
- [ ] `FilesCanvas`: the tree fills the canvas and a click opens a tab. `CanvasSplit` and
      `CanvasFileViewer` retire from Files.
- [ ] `ChangesCanvas`: a click opens the file tab in Diff. Its split viewer retires, so a file has
      one place.
- [ ] Tool rows in the conversation that name a file (Edited/Wrote) open the tab, if they
      link to the viewer today.
- [ ] Tests: store preview rules (replace clean preview, keep dirty preview, focus existing),
      CanvasHost renders preview/dirty states.

### Task 4: FileCanvas
- [ ] The bar: breadcrumb, save state (Unsaved + Save button / Changed on disk), view toggle.
- [ ] Views: the editor; Rendered through `MarkdownRenderer` (annotatable, as today) and
      `HtmlRenderer`, fed from the buffer; Diff through `unifiedMergeView` with the git base
      from `sharedDiffs` (`FileDiffItem.before`).
- [ ] Save, conflict bar (Compare / Keep mine / Use the agent's), read-only states.
- [ ] Selection chip → `appendDraftReference(sessionId, ref)` (new, in `use-draft-state.ts`),
      then `weave:command-focus-prompt`.
- [ ] Tests: component tests for save success, 409 → bar, each bar action, read-only states.

### Task 5: Live updates
- [ ] Subscribe to `files.changed` for the session. For each open path, and on tab activation and
      window focus, re-read the file and apply the rule above. Pulse with `store.markUpdated`.
- [ ] Also re-check open files on `turn.ended`, which catches shell edits (Task 0: `sed` emits
      no file event).
- [ ] Debounce bursts (the agent edits a file several times in one turn) the way `use-diffs`
      does (500 ms).
- [ ] Tests: event → clean buffer updates; event → dirty buffer shows the bar; no request when
      the hash matches.

### Task 6: Go to file
- [ ] Ctrl P / Cmd P registered in `command-registry.ts` (no existing binding), plus a palette
      entry. A small dialog with fuzzy match over `find/files`; ↑↓, Enter, Esc.
- [ ] Test: filtering and Enter opens a kept tab.

### Task 7: Verify and ship
- [ ] Client suite, lint and `lint:design` on Node 22 with `npm ci` (CI parity); .NET suites.
- [ ] Live check in a scratch Fleet (scratch HOME, never the real one): open, edit, save; the
      agent edits a clean file (updates in place) and a dirty one (bar, each action); Ctrl P;
      add to message; Markdown and HTML rendered/source; a phone-width sheet; a light and a
      dark theme.
- [ ] Native AOT publish still has 0 warnings.
- [ ] PR with before/after screenshots under `mockups/editor/`.

## Out of scope

The agent opening files; clickable `file:line` paths in replies; focus mode (C) and the chat
peek (D); language servers and AI completion; creating, renaming or deleting files; editing
files over 512 KB.

## Risks

- **Lost work on tab eviction or reload.** Buffers live in a store, and `beforeunload` warns
  while any buffer is unsaved. Unsaved buffers don't survive a reload; that's acceptable for now.
- **Line endings and BOMs.** Covered by Task 0 and the server tests. A save must not rewrite a
  file the user didn't change.
- **Edits that don't emit `files.changed`** (shell commands, other tools). The focus re-check and
  the save-time hash check cover them; the worst case is the bar appearing on save.
- **Write access in hosted mode.** Saving needs the same access as reading the file and
  prompting the agent, which can already write. Symlink and `.git/` checks stop writes outside
  the worktree.
