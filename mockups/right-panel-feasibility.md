# V1 Split + VP4 Mirror — Feasibility Report

Read-only investigation. Every claim grounded in the actual Vue/TypeScript source or existing tests. No files were modified.

## Executive summary

The redesign is feasible today with small, well-scoped surgery across roughly 12 files and one new component. All rendering primitives already exist. There is **one hard blocker** that must be resolved first: `useVisualPanel` is a module-level singleton (line 4) and payload state leaks across sessions. Fixing it is a 1-2 hour precursor and unlocks VP4 cleanly.

Estimated total effort: 10-16 hours.

Confidence: high (92%).

---

## 1. Component-by-component scope

### SessionsV2RightPanel.vue (root, lines 1-675)
- Drop Preview/Details from the tabs array (lines 103-116).
- Insert new `SessionMetadataHeader.vue` above the tab bar.
- Add horizontal split with resize gutter between tree/changed-list and content slot.
- Add localStorage key `weave:files-tree-width`.
- Route content slot to detect `__visual__/*` synthetic paths.
- Remove `ChangesDrawer` import (line 8) and mount (line 458).
- Keep the existing Diff/Rendered toggle (lines 264-294) as-is.

### RightPanelTabs.vue (lines 1-111)
- Accept `reviewedCount`/`totalCount` props.
- Render `N/M reviewed` on the Changes tab.
- No accessibility regression: `role="tab"`, `aria-selected`, `aria-controls` already present.

### FileBrowserPanel.vue (lines 1-492)
- Detect `__visual__/*` in the file-click handler and route without an API fetch.
- Search and tree rendering unchanged.

### FileBrowserTreeNode.vue (lines 1-361)
- No changes. Diff badges already work (lines 51-85).

### DiffView.vue (lines 1-302)
- No changes. Reused as-is inside the content slot.

### SessionDetailPanel.vue (lines 1-850)
- Actions toolbar (lines 407-568), todos (lines 570-584), and smart links (lines 586-665) extract into `SessionMetadataHeader.vue`.
- File is deleted after extraction — no residual content is worth keeping.
- Data dependencies (`useSessionTodos`, `useSessionDetailContext`, `useSmartLinksStore`) are all session-scoped and transplant cleanly.

### ChangesDrawer.vue (lines 1-527)
- Delete entirely. Only mounted in SessionsV2RightPanel. Its `sharedDiffs` inject symbol stays because other consumers use it.

### Renderers (MarkdownRenderer, HtmlRenderer, MermaidRenderer, VueFlowRenderer)
- No changes. Stateless, reusable, already used in SessionsV2RightPanel lines 366-367.

---

## 2. State model changes

### use-content-panel.ts (lines 1-205)

Type change:
```ts
export type ContentPanelTab = "files" | "changes";  // remove "preview" | "details"
```

`FilesExplorerContext` gains `filesTreeWidth: number` and `reviewedFiles: Set<string>`.

Drop `drawerMode` ref (line 95), `setDrawerMode` (lines 135-137), the drawer persistence watch (lines 98-100), and the drawer helpers (lines 150-177). Drop the localStorage key `weave:changes-drawer-mode` (line 84).

`selectFile()` now sets `activeTab.value = "changes"` when the target file is changed, and stays on "files" for browsing. Session-change reset (lines 103-116) is updated to reset the new fields and drop the drawer field.

### use-visual-panel.ts (lines 1-20) — the blocker

Currently `const visualPayload = shallowRef<VisualPayload | null>(null)` at module scope. Payload persists across sessions. Fix: session-scoped Map keyed by session id, with `useVisualPanel(sessionId)` returning a payload/showVisual/clearVisual triple. Callers to update:
- `use-file-browser.ts` line 10
- `use-artifact-viewer.ts` line 8
- `ActivityStream.vue` line 61
- `SessionsV2RightPanel.vue` line 48

### use-file-browser.ts (lines 1-217)
`selectFile(path)` gets an early-out for `__visual__/*` paths — no fetch, just update `selectedFilePath` so the content slot rerenders from `visualPayload`.

### use-diffs.ts (lines 1-159)
No changes. Already session-scoped. `sharedDiffs` inject stays valid.

---

## 3. VP4 mirror plumbing

`showVisual()` is called from four sites today, all pass a `VisualPayload` object directly (no event indirection):
- `use-file-browser.ts` lines 106, 111 — after `readSessionFile`
- `use-artifact-viewer.ts` lines 61, 77, 80 — opening/toggling artifact views
- `ActivityStream.vue` line 655 — agent-produced payloads mid-conversation

The content routing decision in SessionsV2RightPanel (lines 259-261, 322-412) needs a small update:
```ts
const isSyntheticPath = computed(() =>
  contentPanelContext.filesContext.value.selectedFilePath?.startsWith("__visual__/") ?? false
);

const shouldShowDiff = computed(() =>
  !isSyntheticPath.value
  && contentPanelContext.activeTab.value === "changes"
  && diffLines.value !== null
);
```

When a payload is shown for a synthetic path, the content slot picks a renderer off `visualPayload.$type` exactly the way it does today for the Preview tab. Same code path, different trigger.

**No new sanitizer or renderer work.** DOMPurify allowlist and HTML sandbox iframe both continue to apply because we route through the existing renderers.

---

## 4. Per-file reviewed state

No existing "reviewed" or "seen" state anywhere in the codebase. Cheapest viable option:

Add `reviewedFiles: Set<string>` to `FilesExplorerContext`, plus a `markFileReviewed(path)` action on `useContentPanelContext()`. Session-local, resets on session change. Optionally listen to `files.changed` and remove a path from the set when the agent modifies it again (matches VS Code Agents semantics).

No server changes. No localStorage. If cross-session persistence turns out to matter later, it can be added on top without rework.

---

## 5. Metadata header decomposition

`SessionDetailPanel.vue` breaks into three well-bounded regions that already have their own composables:

| Region | Lines | Composable | Notes |
|---|---|---|---|
| Actions toolbar | 407-568 | `useSessionDetailContext()` | Abort/Resume/Stop/Fork/Rename/Delete/Archive; already self-contained with per-action spinner state |
| Todos | 570-584 | `useSessionTodos(sessionId)` | Session-scoped fetch; `TodoListView` reused |
| Smart links | 586-665 | `useSmartLinksStore` + `useSmartLinksRefresh` | Refresh timer + arc animation; store is session-aware |

New `SessionMetadataHeader.vue` takes `session: SessionListItem | null` as a prop and renders all three. Layout compresses vertically: actions row on top, chips (issue/PR/todo/artifact) as a wrapping row, expandable todo strip below the chips.

`SessionDetailPanel.vue` becomes empty after extraction — safe to delete.

---

## 6. ChangesDrawer deletion audit

- Import: `SessionsV2RightPanel.vue` line 8 — remove.
- Mount: `SessionsV2RightPanel.vue` line 458 — remove.
- Tests: `ChangesDrawer.test.ts` — delete.
- localStorage: `weave:changes-drawer-mode` — remove from `use-content-panel.ts`.
- Inject symbol `sharedDiffs`: **keep**. Consumed elsewhere; provided in SessionsV2RightPanel line 58. The Changes tab body will consume it.

No orphans. Safe deletion.

---

## 7. Test surface

Updates needed:
- `use-content-panel.test.ts` lines 34-46 (selectFile → "changes" not "preview"), 77-93 (drop preview/details cases), 132-170 (session reset drops drawer), 213-253 (drop drawer tests entirely).
- `ChangesDrawer.test.ts` — delete.
- `ContentPanelTests.cs` (E2E) lines 24-48, 57-91, 96-129, 134-150+ — update tab expectations to 2 tabs, drop drawer-handle test.

Unchanged:
- `use-diffs.test.ts`, DiffView tests, tree-node tests, renderer tests — all preserved.

---

## 8. Router impact

No URL-driven tab state anywhere. `activeTab` is a local ref in `use-content-panel.ts`. Nothing to migrate.

---

## 9. Accessibility, keyboard, focus

Already present: tab roles/aria on `RightPanelTabs.vue`, pointer-based gutter pattern in `AppShell.vue` lines 113-175.

Need to add (incrementally, not a blocker):
- Arrow-key tab navigation on `RightPanelTabs.vue`.
- Keyboard resize on the new internal gutter (arrow keys, ~10px steps).
- Enter/Space activation on the artifact chip.
- Space to toggle review checkboxes.

Recommended: implement pointer/mouse behaviour with the initial change, add keyboard support as a small follow-up if it's not already in the AppShell pattern (which it isn't).

---

## 10. Risk register

| Risk | Likelihood | Severity | Detection | Mitigation |
|---|---|---|---|---|
| `visualPayload` singleton leaks across sessions | High | High | E2E: switch session, assert stale payload gone | Session-scope Map (precursor task) |
| E2E tab-count assertions fail | Medium | Medium | Run `ContentPanelTests.cs` | Update to 2 tabs |
| Synthetic-path branch breaks `selectFile` | Medium | High | New unit test on `use-file-browser.ts` | Guard early, return early |
| Gutter width persistence collides with panel width | Low | Medium | Manual test | Keep `filesTreeWidth` in `filesContext`, not global |
| Smart-links refresh timer misbehaves in new host | Low | Medium | Manual + smart-links test | Composable is store-based, transplants cleanly |
| Reviewed state ephemeral by design | Low | Low | Product decision | Documented; upgrade path is additive |
| Missing keyboard nav | Low | Low | Accessibility audit | Follow-up task |
| `sharedDiffs` inject accidentally dropped | Low | High | Unit test on Changes tab | Explicit keep-note in the plan |

---

## 11. Green-light checklist

- ✓ MarkdownRenderer / HtmlRenderer / DiffView / Mermaid / VueFlow all reusable as-is
- ✓ Gutter drag pattern proven in `AppShell.vue` lines 113-175
- ✓ `use-diffs.ts` already session-scoped
- ✓ File tree already renders diff badges
- ✓ Tab state is local, not URL-driven — no router migration
- ✓ No new npm dependencies
- ✓ Diff/Rendered toggle logic preserves
- ✓ Actions/todos/smart-links regions in `SessionDetailPanel` are extractable
- ✓ `useSessionTodos` and `useSmartLinksStore` are session-scoped
- ✓ `ChangesDrawer` safely deletable; `sharedDiffs` inject stays
- ⚠ `visualPayload` singleton must be session-scoped **before** VP4 wiring (precursor task)
- ⚠ E2E `ContentPanelTests.cs` expects 3 tabs and drawer handle — must update

---

## 12. Confidence and blockers

Confidence: **92% (High)**. Ready for Pattern delegation with one precursor task.

Blocker: **`use-visual-panel.ts` module-level singleton**. Fix first, then everything else lines up.

---

## 13. Implementation phases (from Thread report)

**Phase 1 — Precursor (1-2h):** Session-scope `visualPayload`; update all four call sites.

**Phase 2 — State (2-3h):** Update `ContentPanelTab` type; add `filesTreeWidth` and `reviewedFiles` to context; remove `drawerMode` and drawer plumbing; update session-change reset.

**Phase 3 — Components (4-6h):** New `SessionMetadataHeader.vue` (extract three regions from `SessionDetailPanel.vue`); rework `SessionsV2RightPanel.vue`; add reviewed count to `RightPanelTabs.vue`; add synthetic-path branch in `FileBrowserPanel.vue` and `use-file-browser.ts`; delete `ChangesDrawer.vue` and `SessionDetailPanel.vue`.

**Phase 4 — Tests (2-3h):** Update `use-content-panel.test.ts`; update `ContentPanelTests.cs`; delete `ChangesDrawer.test.ts`; add a synthetic-path test.

**Phase 5 — Verification (1-2h):** Manual + E2E; verify no payload leak across sessions.

---

## 14. Files touched

1. `client/src/composables/use-visual-panel.ts` — session-scope
2. `client/src/composables/use-content-panel.ts` — type, state, reset
3. `client/src/composables/use-file-browser.ts` — synthetic paths
4. `client/src/components/sessions/SessionsV2RightPanel.vue` — full rework
5. `client/src/components/layout/RightPanelTabs.vue` — reviewed count
6. `client/src/components/session/FileBrowserPanel.vue` — synthetic paths
7. `client/src/components/session/SessionMetadataHeader.vue` — NEW
8. `client/src/components/session/SessionDetailPanel.vue` — DELETE
9. `client/src/components/session/ChangesDrawer.vue` — DELETE
10. `client/src/composables/__tests__/use-content-panel.test.ts` — update
11. `client/src/components/session/__tests__/ChangesDrawer.test.ts` — DELETE
12. `tests/WeaveFleet.E2E/Tests/ContentPanelTests.cs` — update

No new npm dependencies. No sanitizer changes. No renderer changes.

---

## Conclusion

**Green-light.** Precursor task (session-scope `visualPayload`) is small and independent. After that, the plan is a straightforward layout refactor plus a small state-model cleanup. Ready to hand to Pattern.
