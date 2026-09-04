# Prototype B — Rendered Content Viability Note

Scope: how Markdown, HTML, and diff render together in Prototype B (Files + Changes), and what each layout variant costs to build against the current Vue codebase.

Grounded in the actual code under `client/src/components/**` and `client/src/composables/**`.

---

## Rendering infrastructure that already exists

| Piece | Where | Status |
|---|---|---|
| Markdown renderer | `visual-renderers/MarkdownRenderer.vue` + `lib/markdown-renderer.ts` | MarkdownIt + highlight.js (15 langs), `breaks`, `linkify`, `html: false`. Output sanitized by DOMPurify (`lib/sanitize-html.ts`). Stateless factory; multiple instances are cheap. |
| HTML renderer | `visual-renderers/HtmlRenderer.vue` | `<iframe srcdoc>` with `sandbox="allow-same-origin"`. No parent-DOM access. |
| Mermaid / Vue Flow | Async imports in `MermaidRenderer.vue`, `VueFlowRenderer.vue` | Not routed through MarkdownIt; picked by `visualPayload.$type`. |
| Content router in Right Panel | `SessionsV2RightPanel.vue` lines 322–412 | Picks Diff vs Markdown vs HTML vs Rendered off `visualPayload.$type` and `renderableFileType`. Already handles the Diff/Rendered toggle for `.md`/`.html`. |
| Diff view | `DiffView.vue` + `lib/diff-parser.ts` | Reusable as-is. |

**Implication:** Prototype B does not need any new rendering primitives. It reroutes existing renderers behind a different tab structure.

---

## The content slot behind Files and Changes

The rule that keeps B simple: **one file selection drives one content slot**, and the file's nature (extension + presence of a diff) picks the header toggle set.

| Selected file has… | Header toggle group | Default view |
|---|---|---|
| `.md` file, unchanged | `Rendered · Source` | Rendered |
| `.html` file, unchanged | `Rendered · Source` | Rendered |
| Code file, unchanged | (none) | Source (read-only editor) |
| Binary, unchanged | (none) | "Cannot display" |
| `.md`/`.html`, changed | `Diff · Rendered · Source` | Diff (from Changes) or Rendered (from Files) |
| Code file, changed | `Diff · Source` | Diff (from Changes) or Source (from Files) |

The `Rendered` case reuses `MarkdownRenderer` / `HtmlRenderer` unchanged. The Diff case reuses `DiffView`. All three modes share the same header, the same slot, and the same file selection state. This is already close to what `SessionsV2RightPanel.vue` (lines 322–412) implements today; B just gives the slot a stable home rather than hiding it behind an empty "Preview" tab.

---

## Five layout variants for how the tree shares space with content

| Variant | Cost | Confidence | Best for |
|---|---|---|---|
| **V1 Split** — tree left, content right, internal resize gutter | Small | High | Wide screens, users who browse and read at once |
| **V2 Swap + breadcrumb** — tree fills tab; click file → content with "← back" | Small | High | Narrow panels, single-focus reading |
| **V3 Tree collapses to slim strip** — 32px icon column on selection | Medium | Medium | Neat visual, but accessibility cost |
| **V4 Stack** — tree on top ~40%, content below, vertical gutter | Medium | Medium | Familiar but cramped at current 460px panel width |
| **V5 Overlay-in-panel** — inline content card floats over tree | Large | Low | Not recommended; scroll and focus are painful |

### V1 Split — Small surgery

- Reuses `FileBrowserPanel.vue` untouched.
- Copy the resize-gutter pattern from `AppShell.vue` (lines 281–294). Add one localStorage key: `weave:files-tree-width`.
- **Hazard:** `filesContext.scrollTop` already exists in `use-content-panel.ts` line 28. Wire it in on tree unmount / remount to prevent scroll-jump when swapping content.
- **Hazard:** How does search interact? Today search replaces the tree (`FileBrowserPanel.vue` lines 170–201). In V1 that means the left half turns into a search result list, which is fine and matches VS Code.

### V2 Swap with breadcrumb — Small surgery

- Breadcrumb pattern already exists in `TopBar.vue` (lines 52–68) and there is a proven "← back" affordance in `FilesChangedView.vue` line 339.
- No resize state. No localStorage. Just conditional rendering on `selectedFilePath !== null`.
- **Hazard:** Users lose the tree while reading. Fine for one-at-a-time reads, worse for cross-referencing.

### V3 Tree collapses to slim strip — Medium surgery

- Requires an icon-only render mode on `FileBrowserTreeNode.vue`. Today the tree row shows only a dot (line 184), so icons need to be introduced anyway.
- **Accessibility hazard:** Icon-only nav needs `aria-label` on every node and hover tooltips that don't fight scroll. Screen readers will be unhappy without care.
- **Hazard:** Search input disappears in the strip, or has to be a popover.

### V4 Stack — Medium surgery

- Vertical gutter is less common in the codebase but not novel.
- At the current 460px right-panel width, 40% tree = ~144px, content = ~216px. Both feel tight. Combined with the persistent metadata header, the content slot becomes uncomfortable.
- **Verdict:** Only makes sense if the right panel gets wider or if the tree height is very small.

### V5 Overlay-in-panel — Large surgery, low confidence

- Overlay card layered over the tree. Two scroll contexts, focus trap needed, click-outside handling. Not a pattern the codebase uses well.
- Would only be worth it if we wanted a true "peek" feel within the right panel itself, and even then the slide-over Prototype C already does that better outside the panel.

---

## Four ways to handle agent-produced visual payloads

Payloads are the Markdown/HTML/diagram artifacts that come from the conversation (e.g., an agent `Write`s a markdown plan, or renders a Mermaid diagram inline). Today they hijack the Preview tab. In B, "Preview" is gone, so they need a home.

| Variant | Cost | Confidence | Feel |
|---|---|---|---|
| **VP1 Inline card in conversation** | Small | High | Artifact is part of the answer; scrolls with the message |
| **VP2 Artifacts drawer** | Medium | Medium | Persistent overlay; two drawers on screen |
| **VP3 Virtual files under `Session artifacts/`** | Medium | Medium | Payloads discoverable in the tree; reuses one slot |
| **VP4 Mirror into the Files-tab content slot** | Small | High | Payload behaves exactly like a file preview |

### VP1 — Inline card in conversation

- Renders where the agent produced it. `MessageBubble.vue` already renders tool cards and artifacts.
- **Hazard:** Long artifacts push the conversation. Scrolls away when the conversation grows.
- **Best when:** artifact is a quick reference like a Mermaid sequence diagram.

### VP2 — Artifacts drawer

- Same drawer-mode state machine as `ChangesDrawer.vue`.
- **Hazard:** Two drawers competing for the bottom of the panel. Complicated when both want space.
- Not the right answer if we're already killing the ChangesDrawer redundancy.

### VP3 — Virtual files in the tree

- Payloads appear under `Session artifacts/` in the file tree. Selecting one uses the same content slot as a real file.
- **Hazard:** Blurs "tree = disk state". Needs a badge or icon so users know it's ephemeral.
- **Hazard:** Lifecycle. When `use-visual-panel.ts` is a global singleton (see gotcha below), virtual entries can leak across sessions.

### VP4 — Mirror into the Files-tab content slot

- Cleanest reuse: when `showVisual()` fires, switch to Files tab and route the payload into the same slot that renders file previews. Use a synthetic path like `__visual__/plan.md`.
- **Hazard:** Only one payload visible at a time. Multiple payloads need a stack, an artifact list, or virtual files (VP3).
- **Hazard:** Hides the tree if we're on V2 (Swap). Pairs naturally with V1 (Split).

---

## A preexisting gotcha this exploration surfaced

`use-visual-panel.ts` exposes `visualPayload` as a **module-level singleton** (`ref<VisualPayload | null>(null)` at file scope). This means:

- Payload state leaks across session switches. Switching from session A to session B shows A's payload until B produces one.
- It's session-agnostic where every peer state (`filesContext`, `drawerMode`, diffs, file browser) is session-scoped.

This bites every VP variant. Any of VP1/VP3/VP4 will feel wrong until this is session-scoped. Cheap fix: make it a map keyed by session id, or move it into `use-content-panel.ts` where session-change reset logic already lives (lines 103–116).

Worth naming as a small precursor task regardless of which variant B lands on.

---

## Safety review

**Markdown:** DOMPurify allowlist in `sanitize-html.ts` (lines 47–103) covers everything we render. `html: false` in MarkdownIt means raw HTML inside markdown is escaped before DOMPurify sees it. Low XSS surface. No change needed.

**HTML:** `<iframe srcdoc sandbox="allow-same-origin">`. Scripts inside the payload run in an isolated origin and cannot reach the parent DOM. Same-origin lets them fetch from our origin, which is worth being aware of if the app has authenticated same-origin endpoints. Not a new risk introduced by B; it exists today.

**Mermaid / Vue Flow:** Rendered as data objects, not raw HTML. Safe.

Verdict: B does not widen the XSS surface. It uses the same renderers in the same sandboxes.

---

## Recommended combination

**V1 Split + VP4 Mirror**, with the session-scoped `visualPayload` fix as a precursor.

Reasoning:
1. Small surgery on both fronts, high confidence.
2. Tree stays visible while a payload or file preview is shown, matching the "browse and read at the same time" mental model.
3. One content slot, one set of renderers, one place to look at content. The Preview / Details tab duplication goes away without losing any capability.
4. Resize gutter reuses the pattern in `AppShell.vue`.
5. Search behaviour transfers cleanly.

**Alternative if the panel needs to stay narrow: V2 Swap + VP4.** Cheapest possible build. Loses the "read while browsing" affordance but the breadcrumb-back model is very familiar.

**Consider VP3 (virtual files) as an additive follow-up** if multiple payloads become common. It composes on top of VP4 without breaking it.

---

## Implementation checklist for V1 Split + VP4

Files to touch:
1. `composables/use-visual-panel.ts` — session-scope the payload (precursor).
2. `composables/use-content-panel.ts` — add `filesTreeWidth` + localStorage persistence; wire `scrollTop` restore on tree mount.
3. `components/sessions/SessionsV2RightPanel.vue` — replace 3-tab bar with 2-tab bar; add persistent metadata header; add internal resize gutter.
4. `components/session/FileBrowserPanel.vue` — detect synthetic `__visual__/` path, delegate to the content slot instead of tree navigation.
5. `components/layout/RightPanelTabs.vue` — remove Preview and Details; add "reviewed" count badge to Changes.
6. `components/session/SessionDetailPanel.vue` — extract its actions/todos/smart-links pieces into a compact `SessionMetadataHeader.vue` that lives above the tabs.

No new npm dependencies. No renderer changes. No sanitizer changes.

---

## Open questions worth answering before Shuttle picks this up

1. Should the Files-tab tree filter stay `All | Changed`, or does "Changed" migrate wholesale to the Changes tab and disappear from Files?
2. Multiple payloads per session: one at a time (VP4 only), or history (VP4 + VP3)?
3. Metadata header height budget. Todos + PR/issue chips + primary actions in one strip is comfortable; two lines of chips gets busy.
4. Should the Diff/Rendered toggle default remember per-file, per-session, or globally? Today it doesn't remember at all.

---

Confidence: high for V1/V2 and VP1/VP4. Medium for V3/V4 and VP2/VP3. Low for V5. Safety and pipeline analysis is high confidence and grounded in the actual sanitizer and MarkdownIt config.
