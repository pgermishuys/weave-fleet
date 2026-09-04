# Right-Panel Redesign — V1 Split + VP4 Mirror

## TL;DR
Replace the current Files/Preview/Details tabs plus persistent ChangesDrawer with a persistent metadata header, two tabs (Files, Changes), an internal horizontal split with a single content slot, and route agent visual payloads into the same slot via synthetic `__visual__/*` paths. Session-scope `useVisualPanel` first as an in-plan precursor.

## Context
Design is fully specified upstream. Read before executing:
- `mockups/right-panel-recommended.html` — interactive prototype (source of truth for layout/interactions).
- `mockups/right-panel-feasibility.md` — per-file line ranges, risks, phases.
- `mockups/right-panel-b-viability.md` — variant analysis (V1 Split + VP4 chosen).
- `mockups/research-right-panel-prior-art.md` — design principles.
- `mockups/right-panel-index.html` — index.

Key existing code anchors:
- `client/src/composables/use-visual-panel.ts` (module-level singleton — the blocker).
- `client/src/composables/use-content-panel.ts` (tab state, drawer plumbing at lines 84, 95, 98-100, 103-116, 135-137, 150-177).
- `client/src/composables/use-file-browser.ts` (call site line 10; `selectFile` needs `__visual__/*` early-out; `files.changed` handler).
- `client/src/composables/use-artifact-viewer.ts` (call site line 8).
- `client/src/components/session/ActivityStream.vue` (call site line 61).
- `client/src/components/sessions/SessionsV2RightPanel.vue` (call site line 48; `ChangesDrawer` import line 8; mount line 458; Diff/Rendered toggle lines 264-294; content routing lines 322-412).
- `client/src/components/session/SessionDetailPanel.vue` (actions lines 407-568, todos 570-584, smart-links 586-665).
- `client/src/components/layout/RightPanelTabs.vue` (tab bar; role/aria attributes already present).
- `client/src/components/session/FileBrowserPanel.vue` (tree click handler).
- `client/src/components/layout/AppShell.vue` lines 113-175 (resize gutter pattern to reuse).
- E2E: `tests/WeaveFleet.E2E/Tests/ContentPanelTests.cs` (asserts three tabs and drawer today).

## Scope
- **In scope:** session-scoping `useVisualPanel`; state model cleanup in `use-content-panel.ts`; new `SessionMetadataHeader.vue`; rework of `SessionsV2RightPanel.vue`; `RightPanelTabs.vue` reviewed-count + arrow-key nav; synthetic-path routing; delete `ChangesDrawer.vue` and `SessionDetailPanel.vue`; per-file reviewed state (session-local); `Session artifacts` group in Changes tab; keyboard + a11y for gutter, tabs, artifact chip, review checkboxes; unit + E2E test updates.
- **Out of scope:** cross-session persistence of reviewed state; server-side reviewed tracking; new renderers; sanitizer/CSP changes; multi-payload stack/history (VP3); router/URL-driven tab state.
- **Constraints / assumptions:**
  - Working-tree safety: **do not run** `git checkout`, `git reset`, `git restore`, `git clean`, `git stash`, or `git rm` on any tracked file. Read-only git only. Deletions must be performed via editor/file tooling, not `git rm`. Pass this prohibition to every delegated subtask.
  - Reviewed state is a `Set<string>` on `FilesExplorerContext`; clears on session change; auto-clears an entry when `files.changed` reports that path.
  - `sharedDiffs` inject symbol stays (consumed by the new Changes tab body).
  - Keyboard + a11y are in initial scope, not a follow-up.
  - Toolchain: `bun` / `bunx`, never `npm` / `npx`.

## Objectives
- Ship V1 Split + VP4 Mirror end-to-end in one plan.
- Eliminate the `visualPayload` cross-session leak permanently.
- Absorb `SessionDetailPanel` and `ChangesDrawer` into the new header and Changes tab with no capability regressions.
- Keyboard-navigable panel: tabs, gutter, artifact chip, review checkboxes.
- All frontend, backend Release, and E2E gates green.

## Dependencies and Order
1. **Phase 1 (Precursor)** must land before any component work — VP4 mirror cannot be wired cleanly while `visualPayload` is a module singleton.
2. **Phase 2 (State model)** must land before Phase 3 — components read `ContentPanelTab`, `reviewedFiles`, `filesTreeWidth`, and rely on `drawerMode` being gone.
3. **Phase 3 (Components)** depends on Phases 1 + 2. `weft` review runs after Phase 3.
4. **Phase 4 (Tests)** depends on Phase 3 (asserts new component/state shape). `weft` review runs after Phase 4.
5. **Phase 5 (Verification)** is last and gates completion.

## Tasks

### Phase 1 — Precursor: session-scope `useVisualPanel`

- [x] 1. Session-scope `useVisualPanel`
  - **Agent:** `shuttle-frontend` (fallback: `shuttle`)
  - **What:** Rewrite the module so state is keyed by session id. Export `useVisualPanel(sessionId)` returning `{ visualPayload, showVisual, clearVisual }` backed by an internal `Map<string, ShallowRef<VisualPayload | null>>`. No module-level payload ref. Ensure entries are lazily created and that `clearVisual` removes/nulls only the current session's entry.
  - **Files:** `client/src/composables/use-visual-panel.ts`
  - **Depends on:** None
  - **Acceptance:**
    - No module-scope `shallowRef`/`ref` holding payload state remains.
    - Public API takes a session id (string) and returns the triple.
    - Types compile: `bunx vue-tsc --noEmit` from `client/` passes for this file's consumers after task 2.

- [x] 2. Update all four `useVisualPanel` call sites
  - **Agent:** `shuttle-frontend` (fallback: `shuttle`)
  - **What:** Thread the current session id into every caller. Preserve existing behaviour of `showVisual`/`clearVisual` semantics.
  - **Files:**
    - `client/src/composables/use-file-browser.ts` (line 10)
    - `client/src/composables/use-artifact-viewer.ts` (line 8)
    - `client/src/components/session/ActivityStream.vue` (line 61)
    - `client/src/components/sessions/SessionsV2RightPanel.vue` (line 48)
  - **Depends on:** Task 1
  - **Acceptance:**
    - Every call site passes a session id.
    - `bun run test` unaffected tests still pass.
    - Manual smoke: opening a payload in session A, switching to B, shows no A payload in B.

- [x] 3. Precursor verification
  - **Agent:** `shuttle-frontend` (fallback: `shuttle`)
  - **What:** Add a new Vitest unit test asserting payload isolation across two session ids using the new API (write to session A, read from session B → null; write to B, read A still holds A's payload). Run frontend tests.
  - **Files:** `client/src/composables/__tests__/use-visual-panel.test.ts` (new)
  - **Depends on:** Tasks 1, 2
  - **Acceptance:**
    - New test passes.
    - Full `bun run test` (from `client/`) is green.

### Phase 2 — State model changes

- [x] 4. Update `ContentPanelTab` and `FilesExplorerContext`
  - **Agent:** `shuttle-frontend` (fallback: `shuttle`)
  - **What:**
    - Change `ContentPanelTab` union to `"files" | "changes"`.
    - Add `filesTreeWidth: number` and `reviewedFiles: Set<string>` to `FilesExplorerContext`.
    - Add `markFileReviewed(path: string)` action (and its inverse or toggle as appropriate for the checkbox interaction) exposed off `useContentPanelContext()`.
    - Persist `filesTreeWidth` to localStorage key `weave:files-tree-width` (read on init, write on change; guard for SSR/undefined).
    - Remove `drawerMode` ref (line 95), `setDrawerMode` (135-137), drawer persistence watch (98-100), drawer helpers (150-177), and the `weave:changes-drawer-mode` key (line 84).
    - Update the session-change reset (lines 103-116) to reset `reviewedFiles` (new empty Set) and `filesTreeWidth` (to persisted or default) and drop drawer fields.
    - Update `selectFile()` so that when the target file is in the changed set, `activeTab.value = "changes"`; otherwise stay on `"files"`.
  - **Files:** `client/src/composables/use-content-panel.ts`
  - **Depends on:** Phase 1
  - **Acceptance:**
    - No references to `drawerMode` / `setDrawerMode` / `weave:changes-drawer-mode` remain in this file.
    - `ContentPanelTab` is exactly `"files" | "changes"`.
    - `filesTreeWidth` and `reviewedFiles` are on the context type and initialized on session change.
    - `bunx vue-tsc --noEmit` from `client/` passes.

- [x] 5. `use-file-browser.ts` — synthetic path + reviewed invalidation
  - **Agent:** `shuttle-frontend` (fallback: `shuttle`)
  - **What:**
    - In `selectFile(path)`, early-out for paths starting with `__visual__/`: no API fetch; just update `selectedFilePath` so the content slot rerenders off `visualPayload`.
    - On `files.changed` events, remove each affected path from `reviewedFiles` (VS Code Agents semantics).
  - **Files:** `client/src/composables/use-file-browser.ts`
  - **Depends on:** Task 4
  - **Acceptance:**
    - No `readSessionFile` call fires for `__visual__/*` paths.
    - Paths in `files.changed` are removed from `reviewedFiles`.
    - Existing behaviour for real paths unchanged.

### Phase 3 — Components

- [x] 6. New `SessionMetadataHeader.vue`
  - **Agent:** `shuttle-frontend` (fallback: `shuttle`)
  - **What:** Create the component by extracting three regions from `SessionDetailPanel.vue`:
    - Actions toolbar (lines 407-568): Abort/Resume/Stop/Fork/Rename/Delete/Archive; keep `useSessionDetailContext()` wiring.
    - Todos strip (lines 570-584): via `useSessionTodos(sessionId)`; expandable per prototype.
    - Smart-link chips (lines 586-665): via `useSmartLinksStore` + `useSmartLinksRefresh`; PR/issue/todo/artifact chips.
    - Add an **artifacts chip** activation: Enter/Space opens the mirrored artifact in the content slot (via `showVisual` on a synthetic `__visual__/*` path or by selecting an existing path).
    - Compress vertically to match the prototype: actions row on top, chips as wrapping row, expandable todo strip below chips.
  - **Files:** `client/src/components/session/SessionMetadataHeader.vue` (new)
  - **Props:** `session: SessionListItem | null`
  - **A11y:** proper `role`/`aria-*` on regions, header landmark, chip buttons with `aria-label`, Enter/Space handlers.
  - **Depends on:** Phase 2
  - **Acceptance:**
    - Component renders all three regions with parity to today's SessionDetailPanel behaviour.
    - Artifact chip is keyboard-activatable (Enter and Space) and routes to the content slot.
    - No references left to internal SessionDetailPanel-only state.

- [x] 7. Rework `SessionsV2RightPanel.vue`
  - **Agent:** `shuttle-frontend` (fallback: `shuttle`)
  - **What:**
    - Mount `SessionMetadataHeader` above the tab bar.
    - Drop Preview and Details tabs; keep Files and Changes only.
    - Add a horizontal split inside each tab with a resize gutter between left list (tree or changed files) and content slot. Reuse gutter pattern from `AppShell.vue` lines 113-175. Bind width to `filesTreeWidth`; persist via the localStorage wiring from Task 4.
    - Add keyboard resize on the gutter: ArrowLeft/ArrowRight ~10px steps; Home/End for min/max; `role="separator"`, `aria-orientation="vertical"`, `aria-valuenow/min/max`, `tabindex="0"`.
    - Content-slot routing: add `isSyntheticPath` computed (`selectedFilePath?.startsWith("__visual__/")`). When true, render via existing `MarkdownRenderer`/`HtmlRenderer`/`MermaidRenderer`/`VueFlowRenderer` off `visualPayload.$type`. When false, keep today's Diff / Rendered / Source / Binary routing (including the Diff/Rendered toggle at lines 264-294).
    - Ensure `shouldShowDiff` excludes synthetic paths.
    - Remove `ChangesDrawer` import (line 8) and mount (line 458).
    - Add a `Session artifacts` group to the Changes-tab left list, showing any active VP4 payloads for the current session (selectable, routes to `__visual__/*` path).
    - Preserve the `sharedDiffs` provide (line 58).
  - **Files:** `client/src/components/sessions/SessionsV2RightPanel.vue`
  - **Depends on:** Task 6
  - **Acceptance:**
    - Tab bar shows exactly two tabs.
    - Metadata header is above the tabs.
    - Gutter resizes with mouse and keyboard; width persists in `weave:files-tree-width`.
    - `__visual__/*` path renders via existing renderers with no fetch.
    - Diff/Rendered toggle still works for real changed `.md`/`.html`.
    - No `ChangesDrawer` references remain in this file.

- [x] 8. `RightPanelTabs.vue` — reviewed count + arrow-key nav
  - **Agent:** `shuttle-frontend` (fallback: `shuttle`)
  - **What:**
    - Accept `reviewedCount: number` and `totalCount: number` props; render `N/M reviewed` next to the Changes tab label.
    - Add ArrowLeft/ArrowRight tab navigation on the tablist with roving `tabindex`; preserve existing `role="tab"`/`aria-selected`/`aria-controls`.
    - Remove Preview and Details entries from the tabs array.
  - **Files:** `client/src/components/layout/RightPanelTabs.vue`
  - **Depends on:** Phase 2
  - **Acceptance:**
    - Only Files and Changes tabs render.
    - `N/M reviewed` displays and updates from props.
    - Left/Right arrows cycle focus among tabs and activate on move (or on Enter/Space per current pattern — match existing behaviour).

- [x] 9. `FileBrowserPanel.vue` — synthetic path pass-through
  - **Agent:** `shuttle-frontend` (fallback: `shuttle`)
  - **What:** In the tree/file-click handler, pass `__visual__/*` paths through to `selectFile` without triggering a fetch; the composable early-out handles the rest. Ensure any diff-badge and search paths ignore synthetic entries safely.
  - **Files:** `client/src/components/session/FileBrowserPanel.vue`
  - **Depends on:** Task 5
  - **Acceptance:**
    - Clicking a `Session artifacts` entry routes to the content slot without a network request.
    - No regression in normal file selection.

- [x] 10. Changes tab — per-file reviewed checkboxes
  - **Agent:** `shuttle-frontend` (fallback: `shuttle`)
  - **What:** In the Changes-tab left list (within `SessionsV2RightPanel.vue` or an extracted subcomponent as needed), render a checkbox per row bound to `reviewedFiles.has(path)`. Space toggles review; wire `markFileReviewed` (toggle) action. Update `RightPanelTabs.vue` `reviewedCount`/`totalCount` props from the same source.
  - **Files:** `client/src/components/sessions/SessionsV2RightPanel.vue` (and any small extracted component if the file grows unmanageable)
  - **Depends on:** Tasks 4, 7, 8
  - **Acceptance:**
    - Checkbox reflects `reviewedFiles`.
    - Space toggles; Enter does not submit any form.
    - Count in tab label matches list state.
    - Session switch clears reviewed state; `files.changed` removes affected paths.

- [x] 11. Delete `ChangesDrawer.vue`
  - **Agent:** `shuttle-frontend` (fallback: `shuttle`)
  - **What:** Delete `client/src/components/session/ChangesDrawer.vue` via editor/file tools (not `git rm`). Confirm no remaining import references.
  - **Files:** `client/src/components/session/ChangesDrawer.vue` (delete)
  - **Depends on:** Task 7
  - **Acceptance:**
    - File removed.
    - `rg "ChangesDrawer"` in `client/src` returns no source references (tests handled in Phase 4).
    - `bunx vue-tsc --noEmit` from `client/` passes.

- [x] 12. Delete `SessionDetailPanel.vue`
  - **Agent:** `shuttle-frontend` (fallback: `shuttle`)
  - **What:** After extraction (Task 6) and after confirming the file has no remaining consumers, delete `client/src/components/session/SessionDetailPanel.vue` via editor/file tools. Search for imports first; if any consumer remains, migrate it to `SessionMetadataHeader` before deletion.
  - **Files:** `client/src/components/session/SessionDetailPanel.vue` (delete)
  - **Depends on:** Task 6, Task 7
  - **Acceptance:**
    - No remaining source imports of `SessionDetailPanel`.
    - File removed.
    - `bunx vue-tsc --noEmit` passes.

- [x] 13. Phase 3 review — `weft`
  - **Agent:** `weft`
  - **What:** Component-level review of Phase 3 changes: layout parity with the prototype, keyboard and a11y correctness (roles, `aria-*`, focus order, gutter separator semantics), state wiring, no lingering references to deleted components, `sharedDiffs` preservation.
  - **Depends on:** Tasks 6-12
  - **Acceptance:**
    - `weft` sign-off recorded, or issues surfaced and addressed before Phase 4 starts.

### Phase 4 — Tests

- [x] 14. Update `use-content-panel.test.ts`
  - **Agent:** `shuttle-frontend` (fallback: `shuttle`)
  - **What:**
    - Lines 34-46: `selectFile` on a changed file switches to `"changes"` (not `"preview"`).
    - Lines 77-93: drop preview/details cases.
    - Lines 132-170: session reset covers new fields (`reviewedFiles`, `filesTreeWidth`) and no longer references drawer.
    - Lines 213-253: drop drawer tests entirely.
    - Add tests for `markFileReviewed` toggle behaviour and for `filesTreeWidth` localStorage persistence.
  - **Files:** `client/src/composables/__tests__/use-content-panel.test.ts`
  - **Depends on:** Phase 3
  - **Acceptance:**
    - Vitest file is green.
    - No test asserts a `"preview"` or `"details"` tab.

- [x] 15. Delete `ChangesDrawer.test.ts`
  - **Agent:** `shuttle-frontend` (fallback: `shuttle`)
  - **What:** Delete `client/src/components/session/__tests__/ChangesDrawer.test.ts` via editor/file tools.
  - **Files:** `client/src/components/session/__tests__/ChangesDrawer.test.ts` (delete)
  - **Depends on:** Task 11
  - **Acceptance:**
    - File removed. `bun run test` from `client/` still green.

- [x] 16. New test — `use-file-browser` synthetic-path early-out
  - **Agent:** `shuttle-frontend` (fallback: `shuttle`)
  - **What:** Add a Vitest test asserting that `selectFile("__visual__/plan.md")` does not call `readSessionFile` and does update `selectedFilePath`. Also assert that `files.changed` for a path removes it from `reviewedFiles`.
  - **Files:** `client/src/composables/__tests__/use-file-browser.test.ts` (new or extend existing)
  - **Depends on:** Task 5
  - **Acceptance:** New tests pass; existing tests unaffected.

- [x] 17. Confirm `useVisualPanel` session-isolation test present
  - **Agent:** `shuttle-frontend` (fallback: `shuttle`)
  - **What:** Verify Task 3's test exists and covers cross-session isolation; extend if gaps found (e.g., `clearVisual` in A does not clear B).
  - **Files:** `client/src/composables/__tests__/use-visual-panel.test.ts`
  - **Depends on:** Task 3
  - **Acceptance:** Test file is green and covers the isolation invariant.

- [x] 18. Update E2E `ContentPanelTests.cs`
  - **Agent:** `shuttle-backend` (fallback: `shuttle`)
  - **What:** Update the E2E to the new UI:
    - Lines 24-48: expect exactly two tabs (Files, Changes); no Preview/Details.
    - Lines 57-91: assert no drawer handle exists.
    - Lines 96-129: assert `N/M reviewed` label present on Changes tab.
    - Lines 134-150+: update selectors and interactions to match the new content slot; assert the artifact chip and the conversation `plan.md` link both route to `__visual__/plan.md` in the content slot.
    - Add a keyboard-nav assertion: ArrowRight moves tab focus/selection.
  - **Files:** `tests/WeaveFleet.E2E/Tests/ContentPanelTests.cs`
  - **Depends on:** Phase 3
  - **Acceptance:**
    - E2E compiles.
    - Tests pass locally against the built client (`Category=E2E` filter).

- [x] 19. Phase 4 review — `weft`
  - **Agent:** `weft`
  - **What:** Review test updates for coverage completeness (session isolation, synthetic-path early-out, reviewed toggle and invalidation, E2E parity), and confirm no orphan drawer-era assertions remain.
  - **Depends on:** Tasks 14-18
  - **Acceptance:** Sign-off recorded or gaps fixed before Phase 5.

### Phase 5 — Verification

- [x] 20. Frontend build + typecheck + tests
  - **Agent:** `shuttle-frontend` (fallback: `shuttle`)
  - **What:** From `client/`, run:
    - `bun install`
    - `bun run build`
    - `bunx vue-tsc --noEmit`
    - `bun run test`
  - **Depends on:** Phase 4
  - **Acceptance:** All four commands exit 0.

- [x] 21. Backend Release build
  - **Agent:** `shuttle-backend` (fallback: `shuttle`)
  - **What:** From repo root, run `dotnet build -c Release`.
  - **Depends on:** Task 20
  - **Acceptance:** Build succeeds with 0 errors.

- [ ] 22. E2E run
  - **Agent:** `shuttle-backend` (fallback: `shuttle`)
  - **What:** Run `dotnet test tests/WeaveFleet.E2E --filter Category=E2E`. Ensure the client build from Task 20 is present (do not use `SkipFrontendBuild=true` for the final gate).
  - **Depends on:** Tasks 20, 21
  - **Acceptance:** All E2E tests pass.

- [ ] 23. Manual verification — payload isolation + keyboard walk
  - **Agent:** `shuttle-frontend` (fallback: `shuttle`)
  - **What:** Manual scripted walk:
    1. Open session A with a visual payload visible. Switch to session B. Assert no A payload leaks.
    2. Keyboard-only: Tab into the right panel; ArrowLeft/ArrowRight switches tabs; ArrowLeft/ArrowRight (and Home/End) resizes the internal gutter; arrow keys navigate the tree; Space toggles review checkboxes; Enter/Space activates the artifact chip.
    3. Confirm the artifact chip and the conversation `plan.md` link both open the same slot at `__visual__/plan.md`.
  - **Depends on:** Task 22
  - **Acceptance:** All three checks pass; any deviation is filed as a follow-up before closing the plan.

## Verification
Final gate is Phase 5 tasks 20-23. Passing looks like:
- `bun run build`, `bunx vue-tsc --noEmit`, `bun run test` — all exit 0 from `client/`.
- `dotnet build -c Release` — 0 errors at repo root.
- `dotnet test tests/WeaveFleet.E2E --filter Category=E2E` — all tests pass.
- Manual walk in Task 23 clean (no payload leak; full keyboard reachability; artifact chip and conversation link both open `__visual__/plan.md`).

## Working-tree safety reminder
No task in this plan may run `git checkout`, `git reset`, `git restore`, `git clean`, `git stash`, or `git rm`. Deletions (`ChangesDrawer.vue`, `SessionDetailPanel.vue`, `ChangesDrawer.test.ts`) are performed via editor/file tools. Read-only git (`status`, `diff`, `log`, `show`) is allowed. Any subtask that appears to require a working-tree git mutation must stop and report back rather than acting. This prohibition is passed to every delegated Shuttle.
