# Content Panel Artifact Viewer

## TL;DR
Wire the ArtifactsPanel file click to render `.md`/`.html` files in the Visual tab using existing renderers, with a source/rendered toggle, file picker, and back button.

## Context
- `FileDiffItem` already provides `after` (current content) and `before` (previous content) as strings — no API changes needed.
- The Visual tab in `SessionsV2RightPanel.vue` already renders `VisualPayload` via `visual-renderer-registry.ts`.
- `useVisualPanel()` exposes `showVisual(payload)` and `clearVisual()` as global composable state.
- The right panel auto-switches to the Visual tab when `visualPayload` is set.

## Scope
- In scope: Click-to-render for `.md`/`.html` files; source/rendered toggle; file picker dropdown; back button; non-renderable files show raw source only.
- Out of scope: Diff viewer (side-by-side before/after); binary file handling; full-text search in content panel; resizable content panel.
- Constraints: No API changes. Keep VisualPayload type backward-compatible. Reuse existing MarkdownRenderer and HtmlRenderer components.

## Objectives
- Clicking a `.md` or `.html` artifact renders it in the Visual tab
- User can toggle between rendered output and raw source
- User can switch files via dropdown without returning to list
- User can dismiss the viewer to return to artifact list

## Dependencies and Order
1. Extend `VisualPayload` to carry metadata (filename, source text, available files) — needed by all UI tasks.
2. Update `ArtifactsPanel` click handler — produces the payload.
3. Build viewer toolbar (toggle + file picker + back) — consumes the payload.
4. Wire toolbar into `SessionsV2RightPanel` visual section.

## Tasks

- [x] 1. Extend VisualPayload with artifact metadata
  - **What**: Add optional fields to `VisualPayload`: `sourceFilePath?: string`, `sourceText?: string`, `viewMode?: 'rendered' | 'source'`. These let the toolbar know what file is shown and provide source text for the toggle. Do NOT change existing `$type`/`content` semantics.
  - **Files**: `client/src/lib/visual-payload.ts`
  - **Depends on**: None
  - **Acceptance**:
    - Existing `parseVisualPayload` still works unchanged for server-sent payloads (new fields are optional)
    - TypeScript compiles without errors

- [x] 2. Create `useArtifactViewer` composable
  - **What**: New composable that wraps `useVisualPanel` and `useSessionDiffsContext` to: (a) open a file by path (detects extension, builds `VisualPayload` from `FileDiffItem.after`), (b) expose `viewMode` ref (rendered/source), (c) expose `availableFiles` (filtered to renderable extensions), (d) expose `activeFilePath`, (e) provide `closeViewer()` which calls `clearVisual()`.
  - **Files**: `client/src/composables/use-artifact-viewer.ts`
  - **Depends on**: Task 1
  - **Acceptance**:
    - `openFile('foo.md')` sets `visualPayload` with `$type: 'markdown'`, content = file's `after` text, `sourceText` = same, `sourceFilePath` = path
    - `openFile('bar.html')` sets `$type: 'html'`
    - `openFile('baz.ts')` sets `$type: 'markdown'` with content wrapped in a fenced code block (source-only fallback)
    - Toggling `viewMode` to `'source'` switches `visualPayload.content` to raw source wrapped in a code fence with `$type: 'markdown'`

- [x] 3. Wire ArtifactsPanel click handler
  - **What**: Import `useArtifactViewer` and call `openFile(file.path)` in `handleFileClick`.
  - **Files**: `client/src/components/session/ArtifactsPanel.vue`
  - **Depends on**: Task 2
  - **Acceptance**:
    - Clicking a `.md` file opens the Visual tab with rendered markdown
    - Clicking a `.ts` file opens the Visual tab with syntax-highlighted source

- [x] 4. Create ArtifactViewerToolbar component
  - **What**: New component with: (a) back button (calls `closeViewer()`), (b) file picker `<select>` bound to `availableFiles` / `activeFilePath`, (c) rendered/source toggle buttons bound to `viewMode`. Style matches existing `visual-panel__header` pattern.
  - **Files**: `client/src/components/session/ArtifactViewerToolbar.vue`
  - **Depends on**: Task 2
  - **Acceptance**:
    - Back button clears visual and returns to Artifacts tab
    - File picker shows all diff files; selecting one calls `openFile(path)`
    - Toggle shows "Rendered" / "Source" buttons; active state visually distinct
    - Non-renderable files (`.ts`, `.json`, etc.) hide the toggle (always source)

- [x] 5. Integrate toolbar into SessionsV2RightPanel
  - **What**: In the `visual-panel` section, conditionally render `ArtifactViewerToolbar` when `visualPayload.sourceFilePath` is set (i.e., it's an artifact view, not a server-pushed visual). Replace the existing title/close-button header with the toolbar in that case.
  - **Files**: `client/src/components/sessions/SessionsV2RightPanel.vue`
  - **Depends on**: Tasks 3, 4
  - **Acceptance**:
    - Artifact-originated visuals show the toolbar; server-pushed visuals keep existing header
    - Back button returns to Artifacts tab
    - File picker works to switch files in-place

- [x] 6. Verify end-to-end
  - **Depends on**: Task 5
  - **Acceptance**:
    - `bun run typecheck` passes (or `bunx vue-tsc --noEmit`)
    - `bun run lint` passes
    - Manual: click .md artifact → renders markdown → toggle to Source → shows raw → pick different file → renders new file → back button → returns to artifact list

## Verification
```bash
cd client
bun run typecheck
bun run lint
bun run test
```
All must pass. Manual smoke test per Task 6 acceptance criteria.
